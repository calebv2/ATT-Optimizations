using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ForestPerformance;

internal static class GateContextTests
{
    private sealed class QueuedContext : SynchronizationContext
    {
        internal readonly Queue<Action> Work;
        internal readonly int MainId;
        internal QueuedContext(int id) { MainId = id; Work = new Queue<Action>(); }
        private QueuedContext(QueuedContext original) { MainId = original.MainId; Work = original.Work; }
        public override SynchronizationContext CreateCopy() => new QueuedContext(this);
        public override void Post(SendOrPostCallback callback, object state) { lock (Work) Work.Enqueue(() => callback(state)); }
        internal int Pending { get { lock (Work) return Work.Count; } }
        internal void Pump()
        {
            Check(Thread.CurrentThread.ManagedThreadId == MainId, "Fixture context pumped off main thread");
            while (true)
            {
                Action work;
                lock (Work) { if (Work.Count == 0) return; work = Work.Dequeue(); }
                work();
            }
        }
    }
    private sealed class Flow
    {
        internal bool CloneAlive, Loading, Ready, Unloaded, Saved;
        internal int Restores, Disposals, RestoreThread;
    }
    private static void Check(bool good, string why) { if (!good) throw new Exception(why); }
    private static async Task NativeLoad(Task original, Flow flow)
    {
        flow.CloneAlive = flow.Loading = true;
        try
        {
            await Loading.AfterDynamic(original);
            Check(flow.CloneAlive && flow.Loading, "Native save/flags lifetime ended before complete entity restore");
            flow.RestoreThread = Thread.CurrentThread.ManagedThreadId;
            Check(flow.RestoreThread == Core.MainThread, "Native Unity restore continued off the physical main thread");
            flow.Restores++;
        }
        finally { flow.Disposals++; flow.CloneAlive = flow.Loading = false; }
        flow.Ready = true;
    }
    private static async Task UpperLoad(Task load) { await load; }
    private static async Task Unload(Task load, Flow flow) { await load; Check(flow.Ready, "Unload ran before native load ready"); flow.Unloaded = true; }
    private static async Task Save(Task unload, Flow flow) { await unload; Check(flow.Unloaded && flow.Disposals == 1, "Save ran before native cleanup/unload"); flow.Saved = true; }
    private static Task Chain(Task original, Flow flow)
    {
        Task load = NativeLoad(original, flow); return Save(Unload(UpperLoad(load), flow), flow);
    }
    private static bool FixtureCompatible(SynchronizationContext? a, SynchronizationContext? b)
    {
        if (a == null || b == null) return a == null && b == null;
        return a is QueuedContext x && b is QueuedContext y && x.MainId == Core.MainThread && y.MainId == Core.MainThread && ReferenceEquals(x.Work, y.Work);
    }
    private static void SameOrCopied(bool copied, bool asynchronousOriginal)
    {
        var captured = new QueuedContext(Core.MainThread);
        SynchronizationContext.SetSynchronizationContext(captured);
        var source = new TaskCompletionSource<bool>();
        if (!asynchronousOriginal) source.SetResult(true);
        var flow = new Flow(); Task saved = Chain(source.Task, flow);
        Check(!saved.IsCompleted && flow.CloneAlive && flow.Disposals == 0, "Native load/save chain finished before gate");
        var current = copied ? captured.CreateCopy() : captured;
        SynchronizationContext.SetSynchronizationContext(current);
        if (asynchronousOriginal)
        {
            source.SetResult(true);
            // Represents the engine's ordinary callback processing while the
            // original asynchronous generation finishes, before a blocking save.
            captured.Pump();
        }
        Check(Loading.Pending == 1, "Successful native generation failed to gate");
        bool unrelated = false; captured.Post(_ => unrelated = true, null!);
        Loading.BeginSynchronous(true, out var entered);
        try
        {
            // No callback pump occurs here: Drain must complete the modeled
            // entire native load/unload/save await chain under the captured
            // caller context, then restore the context of the synchronous save.
            Check(saved.IsCompleted && !saved.IsFaulted && flow.Saved && flow.Restores == 1 && flow.Disposals == 1, "Drain stranded native chain: copied=" + copied + ";asyncOriginal=" + asynchronousOriginal + ";status=" + saved.Status + ";restores=" + flow.Restores + ";disposed=" + flow.Disposals + ";callbacks=" + captured.Pending + ";fault=" + saved.Exception);
            Check(ReferenceEquals(SynchronizationContext.Current, current), "Copied/same caller context was not restored");
            Check(!unrelated && captured.Pending == 1, "Synchronous save pumped an unrelated queued callback");
        }
        finally { Loading.SaveEnd(entered); }
        captured.Pump(); Check(unrelated, "Fixture unrelated callback lost");
    }
    internal static void Run()
    {
        bool ready = Core.Ready; var config = Core.Config; int main = Core.MainThread;
        Core.MainThread = Thread.CurrentThread.ManagedThreadId; Core.Ready = true; Core.Config = new Settings(); Loading.Bind();
        var previous = SynchronizationContext.Current; var frame = Loading.Frame;
        var policy = Loading.CompatibleContexts;
        Loading.Frame = () => 500;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            // A null context has no safe off-main posting route. Leaving its
            // gate pending is safer than running native Unity work on a worker.
            var flow = new Flow(); Task pending = Chain(Task.CompletedTask, flow);
            long deferred = Loading.OffThreadDrainDeferred;
            var thread = new Thread(Loading.Drain); thread.Start(); thread.Join();
            Check(!pending.IsCompleted && Loading.Pending == 1 && Loading.OffThreadDrainDeferred > deferred, "Off-main null-context drain executed native body or dropped a gate");
            Loading.Drain(); Check(pending.IsCompleted && flow.Saved && flow.RestoreThread == Core.MainThread, "Main null-context drain failed");
            var unsupported = new QueuedContext(Core.MainThread);
            SynchronizationContext.SetSynchronizationContext(unsupported);
            Task native = Task.CompletedTask;
            Check(ReferenceEquals(Loading.AfterDynamic(native), native) && Loading.Pending == 0, "Production accepted a custom context or replaced its native task");
            Check(!Loading.NativeContextsCompatible(unsupported, unsupported), "Native context policy accepts arbitrary context classes");
            // Only this fixture may substitute its simulated Unity-compatible
            // context. Production still validates Unity class/private queue/ID.
            Loading.CompatibleContexts = FixtureCompatible;
            foreach (bool copied in new[] { false, true }) foreach (bool asyncOriginal in new[] { false, true }) SameOrCopied(copied, asyncOriginal);
            // An off-main request posts a drain request; it never accesses or
            // drains the main-owned gate queue until the engine dispatches it.
            var context = new QueuedContext(Core.MainThread); SynchronizationContext.SetSynchronizationContext(context);
            flow = new Flow(); pending = Chain(Task.CompletedTask, flow);
            var worker = new Thread(Loading.Drain); worker.Start(); worker.Join();
            Check(!pending.IsCompleted && Loading.Pending == 1 && context.Pending == 1, "Off-main drain did not safely post/defer");
            SynchronizationContext.SetSynchronizationContext(context.CreateCopy()); context.Pump();
            Check(pending.IsCompleted && flow.Saved && flow.RestoreThread == Core.MainThread, "Posted main drain failed copied-context native save chain");
            // Failure paths preserve exception identity and native finally.
            var sentinel = new InvalidOperationException("original dynamic generation failed");
            SynchronizationContext.SetSynchronizationContext(context);
            flow = new Flow(); var failed = NativeLoad(Task.FromException(sentinel), flow);
            bool same = false; try { failed.GetAwaiter().GetResult(); } catch (Exception error) { same = ReferenceEquals(error, sentinel); }
            Check(same && flow.Disposals == 1 && !flow.Ready && Loading.Pending == 0, "Failed original changed exception, leaked save lifetime or queued a success gate");
            var cancellation = new CancellationToken(true);
            flow = new Flow(); var canceled = NativeLoad(Task.FromCanceled(cancellation), flow);
            bool sameToken = false;
            try { canceled.GetAwaiter().GetResult(); }
            catch (OperationCanceledException error) { sameToken = error.CancellationToken == cancellation; }
            Check(sameToken && flow.Disposals == 1 && !flow.Ready && Loading.Pending == 0, "Original cancellation token/native cleanup was changed");
            Console.WriteLine("PASS ForestPerformance gate contexts: modeled native load/upper-load/unload/save chains complete exactly once before synchronous-save return with same/copied invocation context and async originals; finally/lifetime/exception identity retained; no arbitrary pump; off-main drain posts or defers; custom production contexts retain native task.");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Loading.Drain(); Loading.CompatibleContexts = policy; Loading.Frame = frame;
            SynchronizationContext.SetSynchronizationContext(previous); Core.Ready = ready; Core.Config = config; Core.MainThread = main;
        }
    }
}
