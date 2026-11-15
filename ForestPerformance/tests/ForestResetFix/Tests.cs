#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ForestResetFix;
using HarmonyLib;

public static class Tests
{
    private static int checks;
    private static void Check(bool valid, string message) { checks++; if (!valid) throw new Exception(message); }
    private static Task Failed(Exception error) { var t = new TaskCompletionSource<bool>(); t.SetException(error); return t.Task; }
    public static void Main()
    {
        CacheRecovery();
        Pump.Run(CacheCompletion);
        Flow().GetAwaiter().GetResult();
        Gate().GetAwaiter().GetResult();
        Harmony();
        Console.WriteLine("Passed " + checks + " checks: reset ordering/failure cleanup, asynchronous cache completion/finalization/failure draining, cache ownership/recovery, single-flight/reentrant continuations, real Harmony Task replacement/null snapshot argument writeback/cache prefix.");
    }
    private static void CacheRecovery()
    {
        var active = new TaskCompletionSource<bool>();
        var canceled = new TaskCompletionSource<bool>(); canceled.SetCanceled();
        var tasks = new Dictionary<string, Task> {
            {"426874109 - Forest Nodes The Forest - 123.cache", Failed(new Exception("nodes"))},
            {"426874109 - Chunk 31-33 - Forest Static - The Forest - 123.cache", canceled.Task},
            {"426874109 - Forest Terrain - The Forest - 123.cache", active.Task},
            {"426874109 - River Nodes - The Forest - 123.cache", Task.FromResult(true)},
            {"426874109 - Forest Nodes Other Forest - 123.cache", Failed(new Exception("other"))},
            {"426874109 - Cave Layer - 123.cache", Failed(new Exception("cave"))}
        };
        Check(CacheTasks.RemoveFailed(tasks, "The Forest") == 2, "Failed forest caches not removed.");
        Check(tasks.Count == 4 && tasks.Values.Contains(active.Task), "Removed unrelated/in-flight/successful task.");
        try { CacheTasks.EnsureSettled(tasks, "The Forest"); throw new Exception("Active cache was accepted."); }
        catch (InvalidOperationException) { checks++; }
        active.SetResult(true); CacheTasks.EnsureSettled(tasks, "The Forest"); checks++;
        Check(!CacheTasks.Owned("426 - Forest Nodes The Forest Annex - 1.cache", "The Forest"), "Forest prefix collision.");
        Check(!CacheTasks.Owned("Forest Nodes The Forest-123", "The Forest"), "Non-native key accepted.");
    }
    private static async Task Flow()
    {
        var normal = new Steps(); await ResetFlow.Run(normal, true);
        Check(normal.Events.SequenceEqual(new[] {"validate","enter","players:True","prepare","unload","hold","clear","invalidate","release","rebuild","persist","release","exit"}), "Incorrect reset sequencing.");
        Check(!normal.Held && !normal.Entered && normal.Persisted, "Successful reset did not release gates.");
        foreach (string failure in new[] {"players:False","prepare","unload","hold","clear","invalidate","rebuild","persist"})
        {
            var fake = new Steps { Fail = failure };
            try { await ResetFlow.Run(fake, false); throw new Exception("Expected failure at " + failure); }
            catch (Sentinel) { checks++; }
            Check(!fake.Held && !fake.Entered, "Failure retained maintenance/generation lock: " + failure);
            if (failure == "players:False" || failure == "prepare" || failure == "unload")
                Check(!fake.Events.Contains("clear"), "Deleted data before prerequisite succeeded.");
            if (failure != "persist") Check(!fake.Persisted, "Committed after failed reset.");
        }
    }
    private static bool Includes(Exception error, Exception expected)
    {
        if (ReferenceEquals(error, expected)) return true;
        var aggregate = error as AggregateException;
        return (aggregate != null && aggregate.InnerExceptions.Any(e => Includes(e, expected)))
            || (error.InnerException != null && Includes(error.InnerException, expected));
    }
    private static async Task CacheCompletion()
    {
        string firstKey = "-1 - Chunk 55-33 - Forest Static - The Forest - 674436796.cache";
        string secondKey = "-1 - Chunk 55-34 - Forest Static - The Forest - 674436796.cache";
        var context = SynchronizationContext.Current;
        int thread = Thread.CurrentThread.ManagedThreadId, resumptions = 0;
        Action resumed = () => {
            resumptions++;
            Check(Thread.CurrentThread.ManagedThreadId == thread && ReferenceEquals(SynchronizationContext.Current, context), "Cache drain left the caller's main-thread synchronization context.");
        };

        // Native generation returns with its final partial batch still running.
        // Completing an inner task does not mean its cache file has been queued:
        // the native GetCache continuation removes the entry only after that.
        var first = new TaskCompletionSource<bool>();
        var second = new TaskCompletionSource<bool>();
        var tasks = new Dictionary<string, Task> { { firstKey, first.Task }, { secondKey, second.Task } };
        var steps = new Steps { Rebuild = () => CacheTasks.WaitForCompletion(tasks, new[] { firstKey, secondKey }, resumed) };
        Task reset = ResetFlow.Run(steps, true);
        Check(steps.Entered && !steps.Persisted && !reset.IsCompleted, "Reset committed or released maintenance before its final two caches completed.");
        first.SetResult(true);
        tasks.Remove(firstKey);
        Check(!steps.Persisted && !reset.IsCompleted, "Reset committed after only one trailing cache completed.");
        second.SetResult(true);
        Check(!steps.Persisted && steps.Entered && !reset.IsCompleted, "Reset committed before the native cache finalizer removed its entry.");
        await Task.Yield();
        Check(!steps.Persisted && !reset.IsCompleted, "A scheduler turn incorrectly substituted for native cache finalization.");
        tasks.Remove(secondKey);
        await reset;
        Check(steps.Persisted && !steps.Entered, "Reset failed to commit after both native cache finalizers completed.");
        Check(resumptions >= 2, "Cache drain did not validate its caller context after an asynchronous wait.");

        // An early fault must not release maintenance while a sibling remains
        // active, or while its successful native wrapper is still finalizing.
        first = new TaskCompletionSource<bool>(); second = new TaskCompletionSource<bool>();
        tasks = new Dictionary<string, Task> { { firstKey, first.Task }, { secondKey, second.Task } };
        var original = new Sentinel();
        steps = new Steps { Rebuild = () => CacheTasks.WaitForCompletion(tasks, new[] { firstKey, secondKey }, resumed) };
        reset = ResetFlow.Run(steps, true);
        first.SetException(original);
        Check(!reset.IsCompleted && steps.Entered, "Cache failure released maintenance while another cache was still running.");
        second.SetResult(true);
        await Task.Yield();
        Check(!reset.IsCompleted && steps.Entered, "Cache failure released maintenance while a successful sibling was still finalizing.");
        tasks.Remove(secondKey);
        try { await reset; throw new Exception("Expected cache generation failure."); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains(firstKey) && error.Message.Contains("Faulted"), "Cache error omitted its exact key or task status.");
            Check(Includes(error, original), "Cache drain discarded the original generation exception.");
        }
        Check(!steps.Persisted && !steps.Entered, "Failed cache reset persisted or retained maintenance after draining siblings.");
        Check(tasks.ContainsKey(firstKey), "Validation discarded a failed native cache task.");

        var canceled = new TaskCompletionSource<bool>(); canceled.SetCanceled();
        tasks = new Dictionary<string, Task> { { firstKey, canceled.Task } };
        try { await CacheTasks.WaitForCompletion(tasks, new[] { firstKey }, resumed); throw new Exception("Canceled cache was accepted."); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains(firstKey) && error.Message.Contains("Canceled"), "Canceled cache diagnostic omitted its exact key or status.");
            Check(error.InnerException != null, "Cancellation cause was discarded.");
        }

        // Missing selected keys already completed natively; unrelated pending
        // or failed work must never block a forest-only reset.
        var unrelated = new TaskCompletionSource<bool>();
        tasks = new Dictionary<string, Task> { { "unrelated", unrelated.Task }, { "other failure", Failed(new Sentinel()) } };
        await CacheTasks.WaitForCompletion(tasks, new[] { firstKey, secondKey }, resumed);
        Check(tasks.Count == 2 && !unrelated.Task.IsCompleted, "Forest cache drain touched unrelated cache work.");

        // Inner success remains pending until the native wrapper removes it.
        tasks = new Dictionary<string, Task> { { firstKey, Task.FromResult(true) } };
        Task finalizing = CacheTasks.WaitForCompletion(tasks, new[] { firstKey }, resumed);
        Check(!finalizing.IsCompleted, "Successful inner task was mistaken for completed native cache finalization.");
        await Task.Yield();
        Check(!finalizing.IsCompleted, "Native cache finalization was skipped after yielding.");
        tasks.Remove(firstKey);
        await finalizing;

        tasks = new Dictionary<string, Task> { { firstKey, Task.FromResult(true) } };
        try { await CacheTasks.WaitForCompletion(tasks, new[] { firstKey }, resumed, 0); throw new Exception("Stuck native cache finalizer was accepted."); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains(firstKey) && error.Message.ToLowerInvariant().Contains("final"), "Stuck finalizer error omitted its key or finalization cause.");
        }

        // Discover selected tasks added or replaced while the first snapshot
        // is awaited. Dictionary mutations all occur on this same thread.
        first = new TaskCompletionSource<bool>();
        var replacement = new TaskCompletionSource<bool>();
        var late = new TaskCompletionSource<bool>();
        tasks = new Dictionary<string, Task> { { firstKey, first.Task } };
        Task changing = CacheTasks.WaitForCompletion(tasks, new[] { firstKey, secondKey }, resumed);
        tasks[firstKey] = replacement.Task; tasks[secondKey] = late.Task;
        first.SetResult(true);
        Check(!changing.IsCompleted, "Cache drain missed a replacement task or a newly registered selected key.");
        replacement.SetResult(true); tasks.Remove(firstKey);
        Check(!changing.IsCompleted, "Cache drain missed a selected key added during its original await.");
        late.SetResult(true); tasks.Remove(secondKey);
        await changing;
        Check(tasks.Count == 0, "Replacement and late-registered cache work did not fully drain.");
    }
    private static async Task Gate()
    {
        var gate = new OperationGate(); object a = new object(), b = new object();
        var waiting = new TaskCompletionSource<bool>(); int invocations = 0;
        Task first = gate.Start(a, -1, true, () => { invocations++; return waiting.Task; });
        Check(ReferenceEquals(first, gate.Start(a, -1, true, () => Task.FromResult(true))), "Duplicate reset not coalesced.");
        try { await gate.Start(b, -1, true, () => Task.FromResult(true)); throw new Exception("Concurrent different forest accepted."); }
        catch (InvalidOperationException) { checks++; }
        Task chain = Continue(gate, first, b);
        waiting.SetResult(true); await chain;
        Check(invocations == 1 && !gate.IsBusy, "Single-flight gate retained after continuation.");
        try { await gate.Start(a, -1, false, () => Failed(new Sentinel())); }
        catch (Sentinel) { checks++; }
        await gate.Start(a, 123, false, () => Task.FromResult(true));
        Check(!gate.IsBusy, "Failed gate cannot be retried.");
    }
    private static async Task Continue(OperationGate gate, Task first, object next) { await first; await gate.Start(next, 123, false, () => Task.FromResult(true)); }
    private static void Harmony()
    {
        var harmony = new HarmonyLib.Harmony("ATT.ForestResetFix.Tests");
        try
        {
            var wipe = typeof(FixtureForest).GetMethod("Wipe");
            harmony.Patch(wipe, prefix: new HarmonyMethod(typeof(HookBodies).GetMethod("WipePrefix")));
            int seedSeen = 0;
            HookBodies.Reset = (obj, seed) => { seedSeen = seed; return Task.FromResult(true); };
            var forest = new FixtureForest(); forest.Wipe(456).GetAwaiter().GetResult();
            Check(seedSeen == 456 && forest.NativeCalls == 0, "Harmony Task prefix did not replace native wipe.");
            var original = new Sentinel(); HookBodies.Reset = (obj, seed) => Failed(original);
            try { forest.Wipe(-1).GetAwaiter().GetResult(); throw new Exception("Exception swallowed."); }
            catch (Sentinel error) { Check(ReferenceEquals(original, error), "Changed original failure."); }
            harmony.Patch(typeof(FixtureRiver).GetMethod("GetGenerationAsync"), prefix: new HarmonyMethod(typeof(RiverArgument<FixtureForest, Access>).GetMethod("Prefix")));
            RiverArgument<FixtureForest, Access>.Prepare = (obj, forestArg, access) => access ?? new Access { Value = 42 };
            Check(new FixtureRiver().GetGenerationAsync(forest, null).Result == 42, "Harmony __args replacement did not reach native null argument.");
            Check(new FixtureRiver().GetGenerationAsync(forest, new Access { Value = 7 }).Result == 7, "Changed non-null terrain snapshot.");
            harmony.Patch(typeof(FixtureCache).GetMethod("CleanCache"), prefix: new HarmonyMethod(typeof(HookBodies).GetMethod("CleanPrefix")));
            var cache = new FixtureCache(); string key = "426 - River Nodes - The Forest - 123.cache";
            cache.Tasks[key] = Failed(new Sentinel());
            HookBodies.Clean = (obj, args) => CacheTasks.RemoveFailed(((FixtureCache)obj).Tasks, "The Forest");
            cache.CleanCache(key).GetAwaiter().GetResult(); Check(!cache.Tasks.ContainsKey(key), "Harmony cache cleanup could not recover pinned fault.");
        }
        finally { harmony.UnpatchSelf(); HookBodies.Reset = null; RiverArgument<FixtureForest, Access>.Prepare = null; HookBodies.Clean = null; }
    }
    public sealed class Sentinel : Exception { }
    public sealed class Access { public int Value; }
    public sealed class FixtureForest
    {
        public int NativeCalls;
        [MethodImpl(MethodImplOptions.NoInlining)] public Task Wipe(int seed) { NativeCalls++; return Task.FromResult(true); }
    }
    public sealed class FixtureRiver
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public Task<int> GetGenerationAsync(FixtureForest forest, Access access) { return Task.FromResult(access.Value); }
    }
    public sealed class FixtureCache
    {
        public readonly Dictionary<string, Task> Tasks = new Dictionary<string, Task>();
        [MethodImpl(MethodImplOptions.NoInlining)] public async Task CleanCache(string name) { Task task; if (Tasks.TryGetValue(name, out task)) await task; Tasks.Remove(name); }
    }
    private sealed class Steps : IResetSteps
    {
        public readonly List<string> Events = new List<string>();
        public string Fail;
        public Func<Task> Rebuild;
        public bool Held, Entered, Persisted;
        private void Event(string value) { Events.Add(value); if (Fail == value) throw new Sentinel(); }
        public void Validate() { Event("validate"); }
        public void EnterMaintenance() { Event("enter"); Entered = true; }
        public Task EnsureNoPlayers(bool disconnect) { Event("players:" + disconnect); return Task.FromResult(true); }
        public Task PrepareOldGeneration() { Event("prepare"); return Task.FromResult(true); }
        public Task QuiesceChunks() { Event("unload"); return Task.FromResult(true); }
        public void HoldGeneration() { Held = true; Event("hold"); }
        public Task ClearOldData() { Check(Held, "No generation gate during delete."); Event("clear"); return Task.FromResult(true); }
        public void SelectSeedAndInvalidate() { Event("invalidate"); }
        public void ReleaseGeneration() { Held = false; Event("release"); }
        public Task RebuildAndValidate() { Check(!Held, "Rebuild would deadlock on wipe gate."); Event("rebuild"); return Rebuild == null ? Task.FromResult(true) : Rebuild(); }
        public Task Persist() { Event("persist"); Persisted = true; return Task.FromResult(true); }
        public void ExitMaintenance() { Entered = false; Event("exit"); }
    }
    // A small single-thread event loop models Unity's synchronization context.
    // Thread-pool timers may post continuations, but only this thread executes
    // them or changes the native-style task dictionary.
    private sealed class Pump : SynchronizationContext
    {
        private readonly Queue<Action> callbacks = new Queue<Action>();
        private readonly AutoResetEvent posted = new AutoResetEvent(false);
        public override void Post(SendOrPostCallback callback, object state)
        {
            lock (callbacks) callbacks.Enqueue(() => callback(state));
            posted.Set();
        }
        private void Next()
        {
            Action callback = null;
            lock (callbacks) if (callbacks.Count != 0) callback = callbacks.Dequeue();
            if (callback != null) { callback(); return; }
            if (!posted.WaitOne(5000)) throw new Exception("Cache completion regression test stalled without a continuation.");
        }
        public static void Run(Func<Task> test)
        {
            var previous = Current;
            var context = new Pump();
            SetSynchronizationContext(context);
            try
            {
                Task running = test();
                while (!running.IsCompleted) context.Next();
                running.GetAwaiter().GetResult();
            }
            finally { SetSynchronizationContext(previous); context.posted.Dispose(); }
        }
    }
}
