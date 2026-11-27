using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CaveNavigationRecovery;

internal sealed class NativeScanFixture
{
    internal bool Active;
    internal int Maximum, Owners;
}
internal sealed class LifecycleHost : IScanHost, IScanLifecycle
{
    private readonly NativeScanFixture native;
    internal LifecycleHost(NativeScanFixture native) { this.native = native; }
    public bool Alive { get; set; } = true;
    public double Now { get; set; }
    public object? Graph { get; set; }
    public bool Scanning => native.Active;
    public bool CancellationRequested { get; set; }
    internal bool Registered = true, MissingMarker;
    internal Exception? DisposeFailure;
    internal Action? DuringDispose;
    internal int WarmupSteps = 2, NativeSteps = 2, Failures, Created, Removed, Disposed;
    internal readonly List<string> Phases = new List<string>();
    internal readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>();
    public void Log(string message) { }
    public bool GraphRegistered(object graph) => Registered && ReferenceEquals(graph, Graph);
    public void Observe(string phase, double seconds, string detail) => Phases.Add(phase + ";" + detail);
    public void Settled(Exception? error, bool cancelled)
    {
        if (cancelled) Completion.TrySetCanceled();
        else if (error != null) Completion.TrySetException(error);
        else Completion.TrySetResult(true);
    }
    public void RemoveIncompleteGraph(object graph)
    {
        Host.Check(!native.Active && ReferenceEquals(Graph, graph), "cleanup touched an active or foreign graph");
        Removed++; Graph = null;
    }
    public IEnumerator CreateOriginal() { Created++; return Run(); }
    private IEnumerator Run()
    {
        Graph = new object();
        bool released = false;
        try
        {
            for (int i = 0; i < WarmupSteps; i++) yield return "worker-warmup";
            Host.Check(!native.Active, "two native scan owners overlapped");
            native.Active = true; native.Owners++;
            native.Maximum = Math.Max(native.Maximum, native.Owners);
            ScanRecovery.CurrentScan!.NativeStep();
            for (int i = 0; i < NativeSteps; i++) yield return "native-scan-step";
            native.Active = false; native.Owners--; released = true;
            if (Created <= Failures) throw new InvalidOperationException("native fixture scan failed after releasing its own lease");
            if (!MissingMarker) ScanRecovery.CurrentScan!.Completed(Graph);
        }
        finally
        {
            // Like the installed ScanAsync, Dispose during scanning does not
            // provide a general-purpose native lease release. The test must
            // reach the native completion above before disposal is safe.
            Disposed++;
            if (!released && native.Active) Phases.Add("disposed_with_native_lease");
            DuringDispose?.Invoke();
            if (DisposeFailure != null) throw DisposeFailure;
        }
    }
}
internal static partial class Tests
{
    private static void DriveLifecycle(ScanRecovery scan, LifecycleHost host)
    {
        for (int i = 0; i < 80; i++)
        {
            if (!scan.MoveNext()) return;
            host.Now++;
        }
        throw new Exception("lifecycle scan did not finish");
    }
    private static void AdmissionLifecycleTests()
    {
        Check(ScanRecovery.Pending == 0 && ScanRecovery.Admission.Waiting == 0, "earlier test leaked admission");
        var native = new NativeScanFixture();
        var cave = new LifecycleHost(native); var stat = new LifecycleHost(native);
        var caveState = new ScanState(); var staticState = new ScanState();
        var first = new ScanRecovery(cave, caveState); var second = new ScanRecovery(stat, staticState);
        Check(first.MoveNext() && Equals(first.Current, "worker-warmup") && !native.Active, "fixture did not reach pre-native warmup");
        Check(second.MoveNext() && stat.Created == 0 && ScanRecovery.Admission.Waiting == 1, "static scan entered during procedural pre-native warmup");
        DriveLifecycle(first, cave);
        Check(cave.Completion.Task.Status == TaskStatus.RanToCompletion && ScanRecovery.IsReady(caveState, cave.Graph), "procedural completion was not verified");
        DriveLifecycle(second, stat);
        Check(native.Maximum == 1 && stat.Completion.Task.Status == TaskStatus.RanToCompletion, "shared admission did not serialize both hosts");
        Check(ScanRecovery.Admission.Owner == null && ScanRecovery.Admission.Waiting == 0, "completed admission was retained");
        Check(stat.Phases.Exists(x => x.StartsWith("wait;reason=admission")) && stat.Phases.Exists(x => x.StartsWith("complete;")), "lifecycle omitted admission or completion");

        native = new NativeScanFixture(); stat = new LifecycleHost(native) { Failures = 99, WarmupSteps = 0, NativeSteps = 1 };
        staticState = new ScanState(); second = new ScanRecovery(stat, staticState);
        try { DriveLifecycle(second, stat); throw new Exception("failed static scan accepted"); }
        catch (InvalidOperationException error) { Check(error.Message.Contains("three attempts"), "wrong terminal static error"); }
        Check(stat.Created == 3 && stat.Removed == 3 && stat.Completion.Task.IsFaulted && !staticState.Running && !native.Active,
            "static retry failure left a graph/task/admission behind");

        native = new NativeScanFixture(); stat = new LifecycleHost(native) { Registered = false, WarmupSteps = 0, NativeSteps = 1 };
        staticState = new ScanState(); second = new ScanRecovery(stat, staticState);
        try { DriveLifecycle(second, stat); throw new Exception("unregistered graph accepted"); } catch (InvalidOperationException) { }
        Check(stat.Completion.Task.IsFaulted && !ScanRecovery.IsReady(staticState, stat.Graph), "scan marker alone falsely completed static load");

        native = new NativeScanFixture(); stat = new LifecycleHost(native) { MissingMarker = true, WarmupSteps = 0, NativeSteps = 1 };
        staticState = new ScanState(); second = new ScanRecovery(stat, staticState);
        try { DriveLifecycle(second, stat); throw new Exception("missing native completion marker accepted"); } catch (InvalidOperationException) { }
        Check(stat.Completion.Task.IsFaulted && stat.Created == 3, "graph existence falsely completed static load");

        native = new NativeScanFixture(); stat = new LifecycleHost(native) { WarmupSteps = 0, NativeSteps = 3 };
        cave = new LifecycleHost(native); staticState = new ScanState(); second = new ScanRecovery(stat, staticState);
        Check(second.MoveNext() && native.Active, "native cancellation fixture never entered scan");
        second.Dispose();
        Check(!stat.Completion.Task.IsCompleted && stat.Disposed == 0 && ScanRecovery.DetachedCount == 1, "cancellation disposed a live native lease");
        first = new ScanRecovery(cave, new ScanState());
        Check(first.MoveNext() && cave.Created == 0, "next scan entered while cancellation was draining");
        for (int i = 0; i < 5; i++) ScanRecovery.TickDetached();
        Check(!native.Active && stat.Completion.Task.IsCanceled && stat.Disposed == 1 && stat.Removed == 0 &&
            !stat.Phases.Contains("disposed_with_native_lease") && ScanRecovery.DetachedCount == 0, "detached native scan did not finish safely before cancellation settled");
        DriveLifecycle(first, cave);

        native = new NativeScanFixture(); stat = new LifecycleHost(native);
        second = new ScanRecovery(stat, new ScanState()); second.MoveNext(); second.Dispose();
        Check(stat.Completion.Task.IsCanceled && stat.Removed == 1 && stat.Disposed == 1 && !native.Active, "pre-native cancellation failed cleanup");

        native = new NativeScanFixture { Active = true }; stat = new LifecycleHost(native);
        second = new ScanRecovery(stat, new ScanState()); second.MoveNext(); stat.Now = 60;
        try { second.MoveNext(); throw new Exception("native busy wait unbounded"); } catch (TimeoutException) { }
        Check(stat.Created == 0 && stat.Completion.Task.IsFaulted && native.Active, "native idle timeout changed another scan");

        native = new NativeScanFixture(); stat = new LifecycleHost(native) { WarmupSteps = 0 };
        staticState = new ScanState(); second = new ScanRecovery(stat, staticState); second.MoveNext();
        var duplicate = new ScanRecovery(stat, staticState); duplicate.MoveNext(); duplicate.Dispose();
        Check(!stat.Completion.Task.IsCompleted && staticState.Running, "cancelled duplicate settled the active owner's load");
        DriveLifecycle(second, stat);
        Check(stat.Completion.Task.Status == TaskStatus.RanToCompletion && stat.Created == 1, "duplicate ran or cancelled another attempt");

        native = new NativeScanFixture(); stat = new LifecycleHost(native);
        staticState = new ScanState(); second = new ScanRecovery(stat, staticState); second.MoveNext();
        var disposeFailure = new InvalidOperationException("iterator dispose failure"); stat.DisposeFailure = disposeFailure;
        stat.DuringDispose = () => second.Dispose();
        try { second.Dispose(); throw new Exception("iterator Dispose error swallowed"); }
        catch (InvalidOperationException error) { Check(ReferenceEquals(error, disposeFailure), "iterator Dispose failure changed"); }
        Check(stat.Completion.Task.IsFaulted && ReferenceEquals(stat.Completion.Task.Exception!.InnerException, disposeFailure) &&
            !staticState.Running && ScanRecovery.Admission.Owner == null && ScanRecovery.Pending == 0,
            "throwing/reentrant Dispose leaked admission or static completion");

        native = new NativeScanFixture(); stat = new LifecycleHost(native);
        second = new ScanRecovery(stat, new ScanState()); second.MoveNext(); stat.Alive = false;
        ScanRecovery.Tick();
        Check(stat.Completion.Task.IsCanceled && ScanRecovery.Admission.Owner == null && stat.Removed == 1,
            "abandoned pre-native generator retained admission");
        native = new NativeScanFixture(); stat = new LifecycleHost(native) { WarmupSteps = 0, NativeSteps = 3 };
        second = new ScanRecovery(stat, new ScanState()); second.MoveNext(); stat.Alive = false;
        for (int i = 0; i < 5; i++) ScanRecovery.Tick();
        Check(stat.Completion.Task.IsCanceled && !native.Active && stat.Disposed == 1 && ScanRecovery.Pending == 0 &&
            !stat.Phases.Contains("disposed_with_native_lease"), "abandoned active generator did not drain its native lease");

        native = new NativeScanFixture(); stat = new LifecycleHost(native) { WarmupSteps = 0 };
        second = new ScanRecovery(stat, new ScanState()); second.MoveNext();
        ScanRecovery.Shutdown();
        Check(native.Active && stat.Disposed == 0 && stat.Completion.Task.IsCanceled && ScanRecovery.Pending == 0, "shutdown spun or altered native leases");
        Console.WriteLine("PASS shared static/procedural admission through warmup; completed registered graphs required; bounded static failures settle tasks; safe cancellation drains native leases one step per update; shutdown never spins or alters native flags.");
    }
    private static void StaticCompletionTests()
    {
        var completion = new TaskCompletionSource<bool>();
        var state = new Core.StaticState(new object(), 20, completion);
        state.Settle(null, false);
        Check(completion.Task.IsFaulted, "static completion source accepted an unverified graph");
        completion = new TaskCompletionSource<bool>(); state = new Core.StaticState(new object(), 40, completion);
        object graph = new object(); state.Graph = graph; state.Scan.CompletedGraph = graph;
        state.Settle(null, false);
        Check(completion.Task.Status == TaskStatus.RanToCompletion && completion.Task.Result, "verified static completion did not resolve native source");
        completion = new TaskCompletionSource<bool>(); state = new Core.StaticState(new object(), 60, completion);
        var sentinel = new InvalidOperationException("terminal scan failure"); state.Settle(sentinel, false);
        Check(completion.Task.IsFaulted && ReferenceEquals(completion.Task.Exception!.InnerException, sentinel), "terminal static scan failure changed");
        completion = new TaskCompletionSource<bool>(); state = new Core.StaticState(new object(), 20, completion);
        using (var source = new CancellationTokenSource())
        {
            var token = source.Token;
            Task.Run(() => state.RequestCancellation(token)).Wait();
            Check(state.CancellationRequested && !completion.Task.IsCompleted, "worker cancellation performed task/Unity work before native scan settled");
            source.Cancel(); state.Settle(null, true);
            Check(completion.Task.IsCanceled, "static cancellation did not resolve the native completion source");
        }
        foreach (var outcome in new[] { "success", "failure", "cancel" })
        {
            int callbacks = 0;
            using (var source = new CancellationTokenSource())
            {
                var registration = source.Token.Register(() => Interlocked.Increment(ref callbacks));
                var original = new TaskCompletionSource<bool>();
                Task identity = original.Task;
                Core.ObserveRegistration(identity, registration);
                if (outcome == "success") original.SetResult(true);
                else if (outcome == "failure") original.SetException(sentinel);
                else original.SetCanceled();
                source.Cancel();
                Check(callbacks == 0 && ReferenceEquals(identity, original.Task), "registration observer changed task identity or leaked its callback: " + outcome);
                if (outcome == "failure") Check(ReferenceEquals(identity.Exception!.InnerException, sentinel), "registration observer changed original failure");
                if (outcome == "cancel") Check(identity.IsCanceled, "registration observer changed native cancellation");
            }
        }
        Console.WriteLine("PASS actual static completion policy: no unverified success, original error/cancellation preserved; worker cancellation only requests; registration cleanup preserves native task identity and all outcomes.");
    }
}
