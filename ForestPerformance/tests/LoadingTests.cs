using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Alta.Chunks;
using Alta.Forests;
using Alta.Inventory;
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
    private static void PersonalCleanup(HarmonyLib.Harmony harmony)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        var cleanup = typeof(PersonalChunk).GetMethod("RemoveCurrentPlayer", fields)!;
        Check(HarmonyLib.Harmony.GetPatchInfo(cleanup)?.Owners.Contains(harmony.Id) == true, "Native personal chunk hook did not attach");
        var chunk = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
        var manager = FormatterServices.GetUninitializedObject(typeof(ServerChunkContentManager));
        typeof(Chunk).GetField("<ContentManager>k__BackingField", fields)!.SetValue(chunk, manager);
        var personal = (PersonalChunk)FormatterServices.GetUninitializedObject(typeof(PersonalChunk));
        typeof(PersonalChunk).GetField("chunk", fields)!.SetValue(personal, chunk);

        var pending = Loading.NextFrame(300);
        long bypasses = Loading.PersonalCleanupBypasses;
        Loading.PersonalCleanupBegin(personal, out var outer);
        try
        {
            Loading.ForcedBegin(chunk, out var bypass);
            Loading.SaveEnd(bypass);
            Check(!bypass && !pending.IsCompleted && Loading.PersonalCleanupBypasses == bypasses + 1, "Unrelated forest gate drained during personal cleanup");

            var nested = (PersonalChunk)FormatterServices.GetUninitializedObject(typeof(PersonalChunk));
            Loading.PersonalCleanupBegin(nested, out var previous);
            Loading.PersonalCleanupEnd(previous);
            Loading.ForcedBegin(chunk, out bypass);
            Loading.SaveEnd(bypass);
            Check(!bypass && !pending.IsCompleted, "Nested cleanup lost the outer personal chunk scope");

            Loading.Tick(301);
            Check(pending.IsCompleted, "Personal cleanup skipped normal paced forest progress");

            pending = Loading.NextFrame(301);
            Action<Chunk> subscriber = _ => { };
            chunk.BeganUnloading += subscriber;
            Loading.ForcedBegin(chunk, out var fallback);
            Loading.SaveEnd(fallback);
            Check(fallback && pending.IsCompleted, "Unload subscriber did not retain conservative forest drain");
            chunk.BeganUnloading -= subscriber;

            pending = Loading.NextFrame(301);
            chunk.FullyUnloaded += subscriber;
            Loading.ForcedBegin(chunk, out fallback);
            Loading.SaveEnd(fallback);
            Check(fallback && pending.IsCompleted, "Completed unload subscriber did not retain conservative forest drain");
            chunk.FullyUnloaded -= subscriber;

            pending = Loading.NextFrame(301);
            var procedural = typeof(Chunk).GetField("<ProceduralStreamer>k__BackingField", fields)!;
            procedural.SetValue(chunk, FormatterServices.GetUninitializedObject(typeof(ForestChunk)));
            Loading.ForcedBegin(chunk, out fallback);
            Loading.SaveEnd(fallback);
            Check(fallback && pending.IsCompleted, "Procedural chunk did not retain conservative forest drain");
            procedural.SetValue(chunk, null);

            pending = Loading.NextFrame(301);
            var other = (Chunk)FormatterServices.GetUninitializedObject(typeof(Chunk));
            Loading.ForcedBegin(other, out fallback);
            Loading.SaveEnd(fallback);
            Check(fallback && pending.IsCompleted, "Different chunk inherited personal cleanup bypass");
        }
        finally { Loading.PersonalCleanupEnd(outer); }

        pending = Loading.NextFrame(301);
        Loading.ForcedBegin(chunk, out var ordinary);
        Loading.SaveEnd(ordinary);
        Check(ordinary && pending.IsCompleted, "Personal cleanup scope leaked into later forced save");
    }
    internal static void Run()
    {
        Core.MainThread = Thread.CurrentThread.ManagedThreadId;
        Core.Ready = true; Core.Config = new Settings { GenerationBudgetMs = 0 };
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
            PersonalCleanup(harmony);
            var rollover = Loading.NextFrame(int.MaxValue); Loading.Tick(int.MinValue); Check(rollover.IsCompleted, "Frame rollover stranded load");
            harmony.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(Loading.Factory).Owners.Contains(observer.Id), "Rollback removed independent patch");
            Console.WriteLine("PASS forest pacing: real native instruction iterator keeps order/results/undo and exception identity with 1/2/native batches; other contexts unchanged; actual loading/save hooks attach; frame gates, personal cleanup isolation, conservative save draining, nesting, rollover and independent rollback.");
        }
        finally { harmony.UnpatchSelf(); observer.UnpatchSelf(); Loading.Drain(); Core.Ready = false; }
    }
    private static void Observe() { }
}

internal static class GenerationBudgetTests
{
    private sealed class UnitIterator : IEnumerator, IDisposable
    {
        internal int Calls, Disposed;
        internal int Left = 2;
        internal Action? Cost;
        internal Exception? Failure, DisposeFailure;
        internal object? Value;
        public object? Current => Value;
        public bool MoveNext()
        {
            Calls++;
            Cost?.Invoke();
            if (Failure != null) throw Failure;
            return Left-- > 0;
        }
        public void Dispose() { Disposed++; if (DisposeFailure != null) throw DisposeFailure; }
        public void Reset() => throw new NotSupportedException();
    }
    private static void Check(bool value, string why) { if (!value) throw new Exception("Shared forest budget: " + why); }
    internal static void Run()
    {
        int frame = 0; double clock = 0;
        var budget = new ForestGenerationBudget.Scheduler(() => frame, () => clock, () => 4, () => true);
        var marker = new object();
        var a = new UnitIterator { Value = marker, Cost = () => clock += 6 };
        var b = new UnitIterator { Cost = () => clock += 6 };
        var c = new UnitIterator { Cost = () => clock += 6 };
        var first = budget.Wrap(a); var second = budget.Wrap(b); var third = budget.Wrap(c);
        Check(first.MoveNext() && ReferenceEquals(first.Current, marker), "native Current changed");
        Check(second.MoveNext() && third.MoveNext() && b.Calls == 0 && c.Calls == 0, "atomic overrun failed to defer concurrent sets");
        Check(second.Current == null && budget.Pending == 2, "deferred iterator exposed a previous native yield");
        frame++;
        first.MoveNext(); second.MoveNext(); third.MoveNext();
        Check(a.Calls == 1 && b.Calls == 1 && c.Calls == 0, "fixed invocation order bypassed oldest waiter");
        frame++;
        first.MoveNext(); second.MoveNext(); third.MoveNext();
        Check(c.Calls == 1 && a.Calls == 1 && b.Calls == 1, "later forest chunk was starved");
        Check(budget.MaximumStepMs == 6 && budget.MaximumFrameMs == 6 && budget.BudgetDeferrals > 0 && budget.FairnessDeferrals > 0,
            "soft-budget costs or deferral reasons were lost");
        ((IDisposable)first).Dispose(); ((IDisposable)second).Dispose(); ((IDisposable)third).Dispose();
        Check(budget.Pending == 0 && a.Disposed == 1 && b.Disposed == 1 && c.Disposed == 1, "cancelled iterators retained scheduler tickets");

        // Zero budget does not even read frameCount; it retains native yields.
        var disabled = new ForestGenerationBudget.Scheduler(() => throw new Exception("disabled frame read"), () => clock, () => 0, () => true);
        var native = new UnitIterator { Value = marker };
        var off = disabled.Wrap(native);
        Check(off.MoveNext() && ReferenceEquals(off.Current, marker) && native.Calls == 1 && disabled.Pending == 0 && disabled.Steps == 0,
            "zero budget changed native behavior");

        // Cheap/null-yield work must continue without a busy wait or frame spam.
        var cheapBudget = new ForestGenerationBudget.Scheduler(() => frame, () => clock, () => 4, () => true);
        var cheap = new UnitIterator { Value = null, Left = 5 };
        var cheapWrapped = cheapBudget.Wrap(cheap);
        for (int i = 0; i < 5; i++) Check(cheapWrapped.MoveNext() && cheapWrapped.Current == null, "zero-cost native progress was held");
        Check(!cheapWrapped.MoveNext() && cheap.Calls == 6 && cheapBudget.MaximumStepMs == 0, "zero-cost completion changed");

        // Stop polling a deferred coroutine without disposing it. A stale
        // ticket can delay a live set by at most the next frame, not forever.
        frame = 100; clock = 0;
        var staleBudget = new ForestGenerationBudget.Scheduler(() => frame, () => clock, () => 4, () => true);
        var worker = new UnitIterator { Left = 10, Cost = () => clock += 5 };
        var stale = new UnitIterator();
        var live = staleBudget.Wrap(worker); var stopped = staleBudget.Wrap(stale);
        live.MoveNext(); stopped.MoveNext(); frame++;
        live.MoveNext(); Check(worker.Calls == 1, "waiter fairness changed before cancellation grace");
        frame++; live.MoveNext();
        Check(worker.Calls == 2 && stale.Calls == 0 && staleBudget.StaleTickets == 1, "abandoned coroutine blocked live generation");
        ((IDisposable)live).Dispose(); ((IDisposable)stopped).Dispose();

        // Native cancellation/exception identity survives scheduler cleanup.
        foreach (Exception sentinel in new Exception[] { new OperationCanceledException("native cancelled"), new InvalidOperationException("native failed") })
        {
            var errorBudget = new ForestGenerationBudget.Scheduler(() => frame, () => clock, () => 4, () => true);
            var broken = new UnitIterator { Failure = sentinel, DisposeFailure = new Exception("secondary cleanup") };
            var wrapped = errorBudget.Wrap(broken); bool same = false;
            try { wrapped.MoveNext(); } catch (Exception error) { same = ReferenceEquals(error, sentinel); }
            Check(same && !wrapped.MoveNext() && broken.Disposed == 1 && errorBudget.Pending == 0, "original failure or cancellation was replaced");
        }
        Check(Settings.Parse("forest_generation_budget_ms=0").GenerationBudgetMs == 0 && Settings.Parse("forest_generation_budget_ms=2.5").GenerationBudgetMs == 2.5,
            "budget startup configuration changed");
        Console.WriteLine("PASS shared forest generation budget: concurrent atomic overruns defer work; FIFO progress despite fixed coroutine order; native Current/zero-cost/zero-budget behavior, cancellation/disposal/stale-ticket cleanup, original exception identity and configuration are preserved.");
    }
}

internal static class TransitionTimingTests
{
    private static void Check(bool value, string why) { if (!value) throw new Exception("Forest transition timing: " + why); }
    private static void Observer() { }
    internal static void Run()
    {
        Core.MainThread = Thread.CurrentThread.ManagedThreadId; Core.Ready = true; Core.Config = new Settings();
        var timing = new HarmonyLib.Harmony("ATT.ForestPerformance.TransitionFixture");
        var observer = new HarmonyLib.Harmony("ATT.ForestPerformance.TransitionObserver");
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forest-transition-" + Guid.NewGuid().ToString("N"), "timing.csv");
        var oldTrace = Core.Trace;
        var sink = new ForestStepDiagnostics.ServerTrace(path, "utc,stage,duration_ms,detail,exception_type,dropped_rows");
        Core.Trace = sink;
        try
        {
            var load = typeof(Alta.Chunks.DynamicChunkContentManager).GetMethod("LoadInternal", BindingFlags.Instance | BindingFlags.NonPublic)!;
            observer.Patch(load, postfix: new HarmonyMethod(typeof(TransitionTimingTests), nameof(Observer)));
            LoadTransitionTiming.Attach(timing);
            Check(LoadTransitionTiming.Installed, "native timing hooks did not attach");
            int completed = 0;
            var data = new LoadTransitionTiming.TransitionData(7, "Forest fixture", Core.Now - 20);
            var source = new TaskCompletionSource<bool>(); Task original = source.Task;
            LoadTransitionTiming.Observe(original, new LoadTransitionTiming.Call(data, "forest_dynamic_ready", Core.Now - 10, _ => completed++));
            Check(!original.IsCompleted && completed == 0, "observer completed the native task");
            source.SetResult(true);
            Check(ReferenceEquals(original, source.Task) && original.Status == TaskStatus.RanToCompletion && completed == 1, "observer replaced task or completion semantics");
            var sentinel = new InvalidOperationException("native task failure"); var failed = Task.FromException(sentinel);
            LoadTransitionTiming.Observe(failed, new LoadTransitionTiming.Call(data, "forest_static_ready", Core.Now));
            bool same = false; try { failed.GetAwaiter().GetResult(); } catch (Exception error) { same = ReferenceEquals(error, sentinel); }
            Check(same, "native task error changed");
            var cancelled = Task.FromCanceled(new CancellationToken(true));
            LoadTransitionTiming.Observe(cancelled, new LoadTransitionTiming.Call(data, "forest_file_ready", Core.Now));
            Check(cancelled.IsCanceled, "native task cancellation changed");
            // Worker completion only formats captured managed state.
            var worker = new TaskCompletionSource<bool>();
            LoadTransitionTiming.Observe(worker.Task, new LoadTransitionTiming.Call(data, "forest_load_total", Core.Now));
            Task.Run(() => worker.SetResult(true)).GetAwaiter().GetResult();
            sink.Dispose();
            string rows = System.IO.File.ReadAllText(path);
            Check(rows.Contains("forest_dynamic_ready") && rows.Contains("forest_static_ready") && rows.Contains("forest_file_ready") && rows.Contains("forest_load_total") &&
                rows.Contains("outcome=completed") && rows.Contains("outcome=faulted") && rows.Contains("outcome=cancelled") && rows.Contains("load_id=7"),
                "bounded lifecycle rows lost stage, outcome or correlation");
            timing.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(load).Owners.Contains(observer.Id), "timing rollback removed an unrelated observer");
            Console.WriteLine("PASS forest transition timing: real static/dynamic-manager/file/entity-restore hooks attach; task completion/fault/cancellation/identity stay native, worker completion uses captured state, lifecycle rows correlate load IDs and independent hook rollback is preserved.");
        }
        finally
        {
            timing.UnpatchSelf(); observer.UnpatchSelf(); LoadTransitionTiming.Shutdown();
            Core.Trace = oldTrace; sink.Dispose(); Core.Ready = false;
            try { System.IO.Directory.Delete(System.IO.Path.GetDirectoryName(path)!, true); } catch { }
        }
    }
}
