using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Alta.Pathfinding;
using HarmonyLib;
using Pathfinding;
using PathfindingQueueDiagnostics;
using Span = PathfindingQueueDiagnostics.Span;
using GamePath = Pathfinding.Path;

internal static class Fixture
{
    [MethodImpl(MethodImplOptions.NoInlining)] internal static int Sleep(int milliseconds) { Thread.Sleep(milliseconds); return 37; }
    [MethodImpl(MethodImplOptions.NoInlining)] internal static bool Wait(WaitHandle handle) => handle.WaitOne();
    [MethodImpl(MethodImplOptions.NoInlining)] internal static void Submit(GamePath path, bool push)
    {
        Advance(path, "PathQueue"); Advance(path, "Processing"); Advance(path, "ReturnQueue");
    }
    internal static void Advance(GamePath path, string name)
    {
        Type state = Requests.Advance.GetParameters()[0].ParameterType;
        Requests.Advance.Invoke(path, new[] { Enum.Parse(state, name) });
    }
}
internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void ModelTests()
    {
        var root = Span.Begin("flush", true, 0)!;
        var lockSpan = Span.Begin("path_lock_acquire", false, 1)!;
        var sleep = Span.Begin("receiver_sleep", false, 2)!;
        var s = sleep.End(5); Check(s.knownWait == 3 && s.self == 3, "sleep accounting");
        var l = lockSpan.End(7); Check(l.elapsed == 6 && l.self == 3 && l.knownWait == 3, "parent accounting");
        var work = Span.Begin("work_items", false, 7)!;
        var wait = Span.Begin("async_graph_wait", false, 8)!; wait.End(10);
        var w = work.End(13); Check(w.knownWait == 2 && w.elapsed - w.knownWait == 4, "queue/wait split");
        var r = root.End(15); Check(r.elapsed == 15 && r.knownWait == 5 && r.self == 3 && Span.Current == null, "root sums double counted nested intervals");
        Check(Span.Begin("receiver_sleep", false, 0) == null, "unscoped call measured");
        var cache = new GenerationCache<object, object>(2); var key = new object(); var other = new object(); var latest = new object();
        cache.Bind(key, new object()); cache.Bind(key, latest); cache.Bind(other, new object());
        Check(cache.TryGet(key, out var value) && ReferenceEquals(value, latest), "old FIFO entry removed reused generation");
        cache.Bind(new object(), new object()); Check(!cache.TryGet(key, out _) && cache.Evictions == 1, "bounded cache did not evict");
        cache.Invalidate(other); Check(!cache.TryGet(other, out _), "pool reset did not invalidate");
        var gate = new RateGate(2); Check(gate.Take(0) && gate.Take(1) && !gate.Take(2) && gate.Take(1000), "rate bound");
        Console.WriteLine("PASS model: nested/exclusive wall time, known wait totals without double counting, unscoped exclusion, pooled-generation FIFO safety and rate bounds.");
    }
    private static void WaitTests()
    {
        var patch = new HarmonyLib.Harmony("ATT.PathfindingQueueDiagnostics.WaitFixture");
        var sleep = typeof(Fixture).GetMethod("Sleep", Static)!; var wait = typeof(Fixture).GetMethod("Wait", Static)!;
        try
        {
            patch.Patch(sleep, transpiler: new HarmonyMethod(typeof(Flush), "ReplaceSleep"));
            patch.Patch(wait, transpiler: new HarmonyMethod(typeof(Flush), "ReplaceWait"));
            var root = Span.Begin("flush", true, Core.Now)!;
            Check(Fixture.Sleep(2) == 37, "sleep wrapper changed result");
            using (var manual = new ManualResetEvent(true)) Check(Fixture.Wait(manual) && manual.WaitOne(0), "manual-reset signal changed");
            using (var automatic = new AutoResetEvent(true)) Check(Fixture.Wait(automatic) && !automatic.WaitOne(0), "auto-reset signal consumed more than once");
            var result = root.End(Core.Now); Check(result.knownWait >= 1 && Span.Current == null, "real waits not counted");
            var errorScope = Span.Begin("flush", true, Core.Now)!;
            try { Fixture.Sleep(-2); throw new Exception("invalid sleep did not throw"); }
            catch (ArgumentOutOfRangeException) { Check(ReferenceEquals(Span.Current, errorScope), "sleep error corrupted scope"); }
            using (var disposed = new ManualResetEvent(true))
            {
                disposed.Dispose();
                try { Fixture.Wait(disposed); throw new Exception("disposed wait did not throw"); }
                catch (ObjectDisposedException) { Check(ReferenceEquals(Span.Current, errorScope), "wait error corrupted scope"); }
            }
            errorScope.End(Core.Now);
            try { Flush.ReplaceSleep(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); throw new Exception("missing Sleep call accepted"); }
            catch (InvalidOperationException) { }
            var call = new CodeInstruction(OpCodes.Call, typeof(Thread).GetMethod("Sleep", new[] { typeof(int) }));
            try { Flush.ReplaceSleep(new[] { call, new CodeInstruction(call) }).ToArray(); throw new Exception("duplicate Sleep call accepted"); }
            catch (InvalidOperationException) { }
            Console.WriteLine("PASS real Harmony wait wrappers: original arguments/results, manual/auto-reset signal semantics, exception propagation, scope restoration and exact-call validation.");
        }
        finally { Span.Current = null; patch.UnpatchSelf(); }
    }
    private static GamePath Path(ushort id)
    {
        var path = new ConstantPath();
        typeof(GamePath).GetProperty("pathID", Instance)!.SetValue(path, id, null);
        return path;
    }
    private static void RequestTests(string directory)
    {
        var patch = new HarmonyLib.Harmony("ATT.PathfindingQueueDiagnostics.RequestFixture");
        Core.Ready = true; Core.MainThread = Thread.CurrentThread.ManagedThreadId;
        var link = new Requests.Link { Nav = new WeakReference<Navigator>(null!) };
        var request = new Requests.Request { Id = 8, Entity = 1234, Layer = 113, Link = link, Started = Core.Now };
        var previous = new Requests.Request { Id = 99 };
        string file = System.IO.Path.Combine(directory, "request-fixture.csv");
        using (var sink = new ServerTrace(file, "utc,mono,request,entity,layer,stage,leg,generation,type,pipeline,complete,age,thread,pending,detail,error,drops"))
        {
            Core.RequestRows = sink;
            try
            {
                patch.Patch(typeof(Fixture).GetMethod("Submit", Static), prefix: new HarmonyMethod(typeof(Requests), "SubmitBegin"), finalizer: new HarmonyMethod(typeof(Requests), "SubmitEnd"));
                patch.Patch(Requests.Advance, finalizer: new HarmonyMethod(typeof(Requests), "Advanced"));
                patch.Patch(Requests.Return, prefix: new HarmonyMethod(typeof(Requests), "CallbackBegin"), finalizer: new HarmonyMethod(typeof(Requests), "CallbackEnd"));
                var first = Path(7); Requests.Current = request; Fixture.Submit(first, false);
                Check(request.Legs == 1 && request.Preparation!.Generation == 7, "preparation path not associated");
                var followup = Path(8);
                first.callback = p =>
                {
                    Check(ReferenceEquals(Requests.Current, request), "callback association missing");
                    Fixture.Submit(followup, false);
                };
                Requests.Current = previous;
                Requests.Return.Invoke(first, null);
                Check(request.Legs == 2 && ReferenceEquals(Requests.Current, previous) && request.Latest!.Generation == 8, "followup not linked/restored");
                var sentinel = new InvalidOperationException("callback failure"); first.callback = p => throw sentinel;
                try { Requests.Return.Invoke(first, null); throw new Exception("callback exception swallowed"); }
                catch (TargetInvocationException e) { Check(ReferenceEquals(e.InnerException, sentinel), "callback exception replaced"); }
                Check(ReferenceEquals(Requests.Current, previous), "failed callback left scope");
                var leg = request.Preparation!;
                typeof(GamePath).GetProperty("pathID", Instance)!.SetValue(first, (ushort)77, null);
                first.callback = p => Check(ReferenceEquals(Requests.Current, previous), "pooled generation reused old request");
                Requests.Return.Invoke(first, null);
                Check(request.Legs == 2, "pooled generation created phantom followup");
                string last = request.LastStage; long tick = request.LastTick;
                typeof(Requests).GetMethod("Emit", Static)!.Invoke(null, new object?[] { request, "still_pending", null, null, "snapshot", null });
                Check(request.LastStage == last && request.LastTick == tick, "pending snapshots reset lifecycle progress age");
                Console.WriteLine("PASS real managed Path lifecycle fixture: queued/processing/return states, preparation-to-followup association, callback exception identity, nested scope restoration, pooled generation guard and pending-age stability. No path search or Unity scene execution.");
            }
            finally { Core.RequestRows = null; Requests.Current = null; Core.Ready = false; patch.UnpatchSelf(); }
        }
        string text = File.ReadAllText(file);
        Check(text.Contains("pipeline_advance") && text.Contains("callback_return") && text.Contains("System.InvalidOperationException"), "lifecycle records incomplete");
    }
    private static void InstalledTests(string directory)
    {
        var patch = new HarmonyLib.Harmony("ATT.PathfindingQueueDiagnostics.Installed");
        var existing = new HarmonyLib.Harmony("ATT.PathfindingQueueDiagnostics.Other");
        string hooks = System.IO.Path.Combine(directory, "hooks.csv"); File.WriteAllText(hooks, "target,status\n");
        try
        {
            var flush = Core.Method(typeof(AstarPath), "FlushWorkItems");
            existing.Patch(flush, postfix: new HarmonyMethod(typeof(Program), nameof(Observe)));
            Requests.Attach(patch, hooks); Flush.Attach(patch, hooks);
            Check(File.ReadAllLines(hooks).Length >= 25, "required installed probes missing");
            Check(HarmonyLib.Harmony.GetPatchInfo(Flush.LockMethod).Owners.Contains(patch.Id) && HarmonyLib.Harmony.GetPatchInfo(Flush.GraphMethod).Owners.Contains(patch.Id), "actual Sleep/Wait transpiler targets not attached");
            patch.UnpatchSelf(); Check(HarmonyLib.Harmony.GetPatchInfo(flush).Owners.Contains(existing.Id), "unpatch removed existing instrumentation");
            Console.WriteLine("PASS installed method metadata and all lifecycle/flush hooks/transpilers attach; rollback preserves independent flush observer. Original Unity methods not executed.");
        }
        finally { patch.UnpatchSelf(); existing.UnpatchSelf(); }
    }
    private static void Observe() { }
    private static void Main()
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "path-queue-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try { EngineState.Bind(); Requests.Bind(); Flush.Bind(); ModelTests(); WaitTests(); RequestTests(directory); InstalledTests(directory); }
        finally { Directory.Delete(directory, true); }
    }
}
