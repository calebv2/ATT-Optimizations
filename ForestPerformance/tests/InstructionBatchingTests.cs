using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Alta.Forests;
using Alta.Serialization;
using Alta.Static;
using ForestPerformance;
using HarmonyLib;

internal static class InstructionBatchingTests
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly Exception Sentinel = new InvalidOperationException("queued/reader instruction failure");
    private static readonly List<int> Executed = new List<int>();
    private static Unit[] ReaderUnits = Array.Empty<Unit>();
    private static int readerIndex;
    private sealed class Result : IInstructionResult { internal int Undone; public void Undo() { Undone++; } }
    private sealed class Unit : Instruction
    {
        internal readonly Result Saved = new Result();
        internal readonly int Id;
        internal readonly bool ReturnsNull;
        internal bool Fail;
        internal Action? Cost;
        internal Unit(int id, bool returnsNull) : base(Instruction.Type.Spawn) { Id = id; ReturnsNull = returnsNull; }
        protected override void SerializeInternal(Alta.Serialization.Stream stream) { }
        public override IInstructionResult Execute(object context)
        {
            if (Fail) throw Sentinel;
            Cost?.Invoke();
            Executed.Add(Id); return ReturnsNull ? null! : Saved;
        }
    }
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private static void CheckIterator(IEnumerator iterator, int maximum)
    {
        int steps = 0;
        while (true)
        {
            int before = Executed.Count; bool more = iterator.MoveNext();
            Check(Executed.Count - before <= maximum, "Null-result instructions bypassed the forest cap");
            Check(++steps < 50, "Native iterator did not finish");
            if (!more) break;
        }
    }
    private static void Case(object context, int cap, bool reader, bool allNull, int maximum)
    {
        Core.Config.InstructionsPerFrame = cap;
        Executed.Clear(); readerIndex = 0;
        var set = new InstructionSet();
        var units = Enumerable.Range(0, 11).Select(i => new Unit(i, allNull || i % 2 == 0)).ToArray();
        if (reader)
        {
            ReaderUnits = units;
            typeof(InstructionSet).GetField("reader", Flags)!.SetValue(set, FormatterServices.GetUninitializedObject(typeof(StreamReader)));
        }
        else foreach (var unit in units) set.Add(unit);
        var iterator = (IEnumerator)Loading.Factory.Invoke(set, new[] { context, Task.CompletedTask, (object)10 })!;
        CheckIterator(iterator, maximum);
        Check(Executed.SequenceEqual(Enumerable.Range(0, 11)), "Queued/reader order or exactly-once behavior changed");
        Check(set.Results.Count() == units.Count(u => !u.ReturnsNull) && set.Results.All(r => r != null), "A null result was retained or valid result lost");
        if (reader) Check(typeof(InstructionSet).GetField("reader", Flags)!.GetValue(set) == null, "Native reader cleanup did not run");
        set.Undo(); Check(units.All(u => u.Saved.Undone == (u.ReturnsNull ? 0 : 1)), "Native result Undo semantics changed");
    }
    internal static void Run()
    {
        Core.Ready = true; Core.MainThread = Thread.CurrentThread.ManagedThreadId; Core.Config = new Settings(); Loading.Bind();
        var owner = (ForestGenerator)FormatterServices.GetUninitializedObject(typeof(ForestGenerator));
        var harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.NullBatchFixture");
        var readerShim = new HarmonyLib.Harmony("ATT.ForestPerformance.ReaderBoundaryFixture");
        var observer = new HarmonyLib.Harmony("ATT.ForestPerformance.InstructionObserver");
        try
        {
            observer.Patch(Loading.Step, postfix: new HarmonyMethod(typeof(InstructionBatchingTests), nameof(Observe)));
            harmony.Patch(Loading.Factory, prefix: new HarmonyMethod(typeof(Loading), nameof(Loading.Cap)));
            harmony.Patch(Loading.Step, transpiler: new HarmonyMethod(typeof(ForestInstructionBatching), nameof(ForestInstructionBatching.CountEveryInstruction)));
            // Keep the real native iterator/branches/counter/yields/cleanup, but
            // isolate binary input and actual game instruction effects.
            readerShim.Patch(typeof(StreamReader).GetMethod("Reset", Flags, null, Type.EmptyTypes, null), prefix: new HarmonyMethod(typeof(InstructionBatchingTests), nameof(Skip)));
            readerShim.Patch(typeof(Alta.Serialization.Stream).GetMethod("SkipBits", Flags, null, new[] { typeof(uint) }, null), prefix: new HarmonyMethod(typeof(InstructionBatchingTests), nameof(Skip)));
            readerShim.Patch(typeof(Alta.Serialization.Stream).GetMethod("SerializeUnsignedInt", Flags, null, new[] { typeof(uint).MakeByRefType() }, null), prefix: new HarmonyMethod(typeof(InstructionBatchingTests), nameof(ReadCount)));
            var execute = typeof(Instruction).GetMethod("Execute", Flags, null, new[] { typeof(StreamReader), typeof(object) }, null)!;
            readerShim.Patch(execute, prefix: new HarmonyMethod(typeof(InstructionBatchingTests), nameof(ReadUnit)));
            foreach (bool reader in new[] { false, true })
            {
                Case(owner, 1, reader, true, 1); Case(owner, 1, reader, false, 1); Case(owner, 2, reader, false, 2);
                Case(owner, 0, reader, true, 11); Case(new object(), 1, reader, true, 11);
                var failing = new InstructionSet(); var bad = new Unit(0, true) { Fail = true };
                if (reader)
                {
                    ReaderUnits = new[] { bad }; readerIndex = 0;
                    typeof(InstructionSet).GetField("reader", Flags)!.SetValue(failing, FormatterServices.GetUninitializedObject(typeof(StreamReader)));
                }
                else failing.Add(bad);
                Core.Config.InstructionsPerFrame = 1;
                var iterator = (IEnumerator)Loading.Factory.Invoke(failing, new object[] { owner, Task.CompletedTask, 10 })!;
                bool same = false;
                try { while (iterator.MoveNext()) { } } catch (Exception e) { same = ReferenceEquals(e, Sentinel); }
                Check(same, "Queued/reader original instruction exception changed");
            }
            bool rejected = false;
            var il = new DynamicMethod("changed_forest_shape", typeof(void), Type.EmptyTypes).GetILGenerator();
            try { ForestInstructionBatching.CountEveryInstruction(new[] { new CodeInstruction(OpCodes.Ret) }, il, Loading.Step).ToArray(); }
            catch (NotSupportedException) { rejected = true; }
            Check(rejected, "Changed iterator IL was accepted");
            SharedBudgetCase(owner);
            harmony.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(Loading.Step).Owners.Contains(observer.Id), "Batch rollback removed an unrelated observer");
            Check(ForestInstructionBatching.CountedNull > 0, "Null-result accounting never engaged");
            Console.WriteLine("PASS forest null-result batching: native queued and reader MoveNext branches count null/mixed instructions at caps1/2, preserve native behavior for cap0/other contexts, keep results/Undo/order/reader cleanup/exception identity; changed IL rejected and independent rollback preserved. Binary reads and instruction effects isolated.");
        }
        finally { harmony.UnpatchSelf(); readerShim.UnpatchSelf(); observer.UnpatchSelf(); Core.Ready = false; }
    }
    private static void Observe() { }
    private static bool Skip() => false;
    private static bool ReadCount(ref uint __0) { __0 = (uint)ReaderUnits.Length; return false; }
    private static bool ReadUnit(object context, ref IInstructionResult __result) { __result = ReaderUnits[readerIndex++].Execute(context); return false; }
    private static void SharedBudgetCase(object context)
    {
        // Compose the new shared wrapper with the REAL native queued iterator,
        // cap and null-result transpiler; no instruction or result is replaced.
        double clock = 0; int frame = 0;
        var budget = new ForestGenerationBudget.Scheduler(() => frame, () => clock, () => 4, () => true);
        Core.Config.InstructionsPerFrame = 1; Executed.Clear();
        var sets = Enumerable.Range(0, 3).Select(_ => new InstructionSet()).ToArray();
        for (int i = 0; i < sets.Length; i++)
            for (int j = 0; j < 4; j++) sets[i].Add(new Unit(i * 10 + j, true) { Cost = () => clock += 5 });
        var iterators = sets.Select(set => budget.Wrap((IEnumerator)Loading.Factory.Invoke(set, new[] { context, Task.CompletedTask, (object)10 })!)).ToArray();
        var finished = new bool[iterators.Length];
        for (; frame < 30 && finished.Any(value => !value); frame++)
        {
            int before = Executed.Count;
            for (int i = 0; i < iterators.Length; i++) if (!finished[i]) finished[i] = !iterators[i].MoveNext();
            Check(Executed.Count - before <= 1, "Concurrent native null-result sets bypassed shared budget");
            if (frame == 2) Check(Executed.Take(3).SequenceEqual(new[] { 0, 10, 20 }), "Native coroutine ordering starved a later forest set");
        }
        Check(finished.All(value => value), "Shared-budget native instruction sets did not finish");
        for (int i = 0; i < sets.Length; i++)
        {
            int at = i;
            Check(Executed.Where(id => id / 10 == at).SequenceEqual(Enumerable.Range(0, 4).Select(j => at * 10 + j)), "Shared wrapper changed native null-result ordering");
            Check(!sets[i].Results.Any(), "Shared wrapper stored a null native result");
        }
        Check(budget.MaximumStepMs == 5 && budget.BudgetDeferrals > 0 && budget.Pending == 0, "Shared-budget native work accounting changed");
    }
}
