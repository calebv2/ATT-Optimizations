using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Alta.Forests;
using Alta.Static;
using ForestPerformance;
using HarmonyLib;

internal static class LoadingTests
{
    private sealed class Result : IInstructionResult
    {
        internal int Undone;
        public void Undo() { Undone++; }
    }
    private sealed class Unit : Instruction
    {
        internal static readonly List<int> Order = new List<int>();
        internal static readonly Exception Sentinel = new InvalidOperationException("instruction failure");
        internal readonly Result Result = new Result();
        private readonly int id;
        internal bool Fail;
        internal Unit(int id) : base(Instruction.Type.Spawn) { this.id = id; }
        protected override void SerializeInternal(Alta.Serialization.Stream stream) { }
        public override IInstructionResult Execute(object context)
        {
            if (Fail) throw Sentinel;
            Order.Add(id); return Result;
        }
    }
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static void Batch(object owner, int cap, int expectedPerMove)
    {
        Core.Config.InstructionsPerFrame = cap;
        var instructions = new InstructionSet();
        var units = Enumerable.Range(0, 11).Select(i => new Unit(i)).ToArray();
        typeof(InstructionSet).GetField("instructions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instructions, units.Cast<Instruction>().ToList());
        Unit.Order.Clear();
        var iterator = (IEnumerator)Loading.Factory.Invoke(instructions, new[] { owner, Task.CompletedTask, (object)10 })!;
        int steps = 0;
        while (true)
        {
            int before = Unit.Order.Count;
            bool more = iterator.MoveNext();
            Check(Unit.Order.Count - before <= expectedPerMove, "Native instruction burst exceeded cap");
            if (!more) break;
            Check(++steps < 20, "Native iterator did not complete");
        }
        Check(Unit.Order.SequenceEqual(Enumerable.Range(0, 11)), "Instruction order/exactly-once execution changed");
        Check(instructions.Results.Count() == 11, "Native result retention changed");
        instructions.Undo(); Check(units.All(u => u.Result.Undone == 1), "Native undo behavior changed");
    }
    internal static void Run()
    {
        Core.MainThread = Thread.CurrentThread.ManagedThreadId;
        Core.Ready = true; Core.Config = new Settings();
        Loading.Bind();
        var harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.LoadingFixture");
        var observer = new HarmonyLib.Harmony("ATT.ForestPerformance.LoadObserver");
        try
        {
            observer.Patch(Loading.Factory, postfix: new HarmonyMethod(typeof(LoadingTests), nameof(Observe)));
            Loading.Attach(harmony);
            var forest = (ForestGenerator)FormatterServices.GetUninitializedObject(typeof(ForestGenerator));
            Batch(forest, 1, 1); Batch(forest, 2, 2); Batch(forest, 0, 10); Batch(new object(), 1, 10);
            var bad = new InstructionSet(); bad.Add(new Unit(0) { Fail = true });
            var broken = (IEnumerator)Loading.Factory.Invoke(bad, new object[] { forest, Task.CompletedTask, 10 })!;
            bool same = false; try { broken.MoveNext(); } catch (Exception e) { same = ReferenceEquals(e, Unit.Sentinel); }
            Check(same, "Original instruction exception changed");
            Core.Config = new Settings();
            Loading.Frame = () => 100;
            var source = new TaskCompletionSource<bool>();
            var delayed = Loading.AfterDynamic(source.Task);
            Check(!delayed.IsCompleted && Loading.Pending == 0, "Gate ran before generation completed");
            source.SetResult(true); Check(!delayed.IsCompleted && Loading.Pending == 1, "Successful load skipped gate");
            Loading.Tick(100); Check(!delayed.IsCompleted, "Gate released in native generation frame");
            Loading.Tick(101); Check(delayed.IsCompleted, "Gate failed to release on later frame");
            var a = Loading.NextFrame(102); var b = Loading.NextFrame(102);
            Loading.Tick(103); Check(a.IsCompleted && !b.IsCompleted, "Multiple complete batches released in one frame");
            Loading.Tick(103); Check(!b.IsCompleted, "Repeated tick bypassed per-frame cap");
            Loading.Tick(104); Check(b.IsCompleted, "Second batch never released");
            var error = new InvalidOperationException("native generation failed");
            var failed = Loading.AfterDynamic(Task.FromException(error)); same = false;
            try { failed.GetAwaiter().GetResult(); } catch (Exception e) { same = ReferenceEquals(e, error); }
            Check(same && Loading.Pending == 0, "Failed generation was changed or queued");
            Core.Config.SeparateLoads = false; Loading.AfterDynamic(Task.CompletedTask).GetAwaiter().GetResult();
            Check(Loading.Pending == 0, "Disabled load separation queued work"); Core.Config.SeparateLoads = true;
            var overflow = Enumerable.Range(0, 40).Select(i => Loading.NextFrame(200)).ToArray();
            Check(Loading.Pending == 32 && overflow.Skip(32).All(t => t.IsCompleted), "Overflow stranded a native load");
            Loading.Drain(); Check(overflow.All(t => t.IsCompleted), "Drain abandoned native load continuations");
            var pending = Loading.NextFrame(250);
            Loading.BeginSynchronous(false, out var normal); Check(!normal && !pending.IsCompleted, "Ordinary async save drained gates");
            Loading.BeginSynchronous(true, out var outer);
            Check(pending.IsCompleted && Loading.NextFrame(250).IsCompleted, "Instant save waited on a future frame");
            Loading.BeginSynchronous(true, out var nested); Loading.SaveEnd(nested);
            Check(Loading.NextFrame(250).IsCompleted, "Nested save ended outer scope"); Loading.SaveEnd(outer);
            Check(!Loading.NextFrame(250).IsCompleted, "Synchronous scope leaked"); Loading.Drain();
            var rollover = Loading.NextFrame(int.MaxValue); Loading.Tick(int.MinValue); Check(rollover.IsCompleted, "Frame rollover stranded load");
            harmony.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(Loading.Factory).Owners.Contains(observer.Id), "Rollback removed independent patch");
            Console.WriteLine("PASS forest pacing: real native instruction iterator keeps order/results/undo and exception identity with 1/2/native batches; other contexts unchanged; actual loading/save hooks attach; frame gates, cap/fallback, shutdown/instant-save draining, nesting, rollover and independent rollback.");
        }
        finally { harmony.UnpatchSelf(); observer.UnpatchSelf(); Loading.Drain(); Core.Ready = false; }
    }
    private static void Observe() { }
}
