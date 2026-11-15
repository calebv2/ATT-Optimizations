using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CaveNavigationRecovery;
using HarmonyLib;

internal sealed class Host : IScanHost
{
    public bool Alive { get; set; } = true;
    public double Now { get; set; }
    public object? Graph { get; set; }
    public bool Scanning { get; set; }
    internal int Created, Removed, Failures, Disposed;
    internal bool Throw, HoldScan, MissingCompletion;
    internal readonly List<string> Logs = new List<string>();
    public void Log(string message) => Logs.Add(message);
    public void RemoveIncompleteGraph(object graph)
    {
        Check(!Scanning && ReferenceEquals(graph, Graph), "unsafe graph removal");
        Graph = null; Removed++;
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public IEnumerator CreateOriginal() { Created++; return Run(); }
    private IEnumerator Run()
    {
        Graph = new object();
        try
        {
            yield return "game-yield";
            if (HoldScan) { Scanning = true; throw new InvalidOperationException("locked scan"); }
            if (Created <= Failures)
            {
                if (Throw) throw new InvalidOperationException("fixture scan failed");
                yield break;
            }
            if (!MissingCompletion) ScanRecovery.CurrentScan!.Completed(Graph);
        }
        finally { Disposed++; }
    }
}
internal sealed class FakeNavigator
{
    public object? preparingPath;
    public bool isRequestingPath;
    internal Action? FailureCallback;
    [MethodImpl(MethodImplOptions.NoInlining)] public void Fail(object path) { FailureCallback?.Invoke(); }
}
internal static class Tests
{
    private static void Check(bool value, string message) => Host.Check(value, message);
    private static void Drive(ScanRecovery iterator, Host host)
    {
        for (int i = 0; i < 60; i++) { if (!iterator.MoveNext()) return; host.Now++; }
        throw new Exception("recovery did not terminate");
    }
    private static void ScanTests()
    {
        var host = new Host(); var state = new ScanState(); var iterator = new ScanRecovery(host, state);
        Check(iterator.MoveNext() && Equals(iterator.Current, "game-yield"), "original yield not preserved");
        Check(!ScanRecovery.IsReady(state, host.Graph), "partial graph marked ready");
        Check(!iterator.MoveNext() && ScanRecovery.IsReady(state, host.Graph), "completed graph not ready");
        Check(host.Created == 1 && host.Removed == 0 && host.Disposed == 1 && !state.Running, "success lifecycle");
        Drive(new ScanRecovery(host, state), host); Check(host.Created == 1, "ready graph rescanned");
        foreach (bool throws in new[] { false, true })
        {
            host = new Host { Failures = 1, Throw = throws }; state = new ScanState(); iterator = new ScanRecovery(host, state);
            Check(iterator.MoveNext(), "initial yield"); Check(iterator.MoveNext(), "failure should delay");
            host.Now = 4; Check(iterator.MoveNext() && host.Created == 1, "retried too early");
            host.Now = 5; Drive(iterator, host);
            Check(host.Created == 2 && host.Removed == 1 && host.Disposed == 2 && ScanRecovery.IsReady(state, host.Graph), "failed scan not rebuilt");
        }
        host = new Host { Failures = 99, Throw = true }; state = new ScanState(); iterator = new ScanRecovery(host, state);
        try { Drive(iterator, host); throw new Exception("retry limit ignored"); }
        catch (InvalidOperationException e) { Check(e.Message.Contains("three attempts"), "wrong exhaustion error"); }
        Check(host.Created == 3 && !state.Running && !ScanRecovery.IsReady(state, host.Graph), "unbounded retries or false success");
        try { new ScanRecovery(host, state).MoveNext(); throw new Exception("budget reset on new iterator"); }
        catch (InvalidOperationException) { Check(host.Created == 3, "new render bypassed limit"); }
        host = new Host { HoldScan = true }; state = new ScanState(); iterator = new ScanRecovery(host, state);
        iterator.MoveNext(); iterator.MoveNext(); host.Now = 5; iterator.MoveNext(); host.Now = 65;
        try { iterator.MoveNext(); throw new Exception("locked scan did not time out"); }
        catch (TimeoutException) { Check(host.Scanning && host.Removed == 0 && host.Created == 1 && !state.Running, "changed global scan/lock on failure"); }
        host = new Host(); state = new ScanState(); iterator = new ScanRecovery(host, state); iterator.MoveNext(); iterator.Dispose();
        Check(!ScanRecovery.IsReady(state, host.Graph) && !state.Running && host.Disposed == 1, "cancellation incorrectly completed scan");
        Drive(new ScanRecovery(host, state), host); Check(host.Removed == 1 && host.Created == 2, "cancelled graph not retried");
        host = new Host(); state = new ScanState(); var first = new ScanRecovery(host, state); var second = new ScanRecovery(host, state);
        first.MoveNext(); Check(second.MoveNext() && host.Created == 1, "overlapping scan started");
        Check(!first.MoveNext() && !second.MoveNext() && host.Created == 1, "waiter did not observe completion");
        host = new Host(); state = new ScanState(); iterator = new ScanRecovery(host, state); iterator.MoveNext(); host.Alive = false;
        Check(!iterator.MoveNext() && !state.Running && host.Disposed == 1, "destroyed owner not cancelled");
        host = new Host { MissingCompletion = true }; state = new ScanState(); iterator = new ScanRecovery(host, state);
        try { Drive(iterator, host); throw new Exception("missing completion accepted"); } catch (InvalidOperationException) { }
        Check(!ScanRecovery.IsReady(state, host.Graph) && host.Created == 3, "graph existence mistaken for readiness");
        Console.WriteLine("PASS scan recovery: real completion required, original yields preserved, delayed bounded retry, safe removal, cancellation, concurrency, locks untouched, exhausted budget persists.");
    }
    private static void WanderTests()
    {
        var state = new WanderState(); var args = new object[] { "original callback and arguments" };
        state.Cancel(false); long epoch = state.Epoch;
        Check(state.Schedule(epoch, args, 10, false) && state.Due == 12 && ReferenceEquals(args, state.Arguments), "retry scheduling");
        Check(!state.Schedule(epoch, args, 10, false), "duplicate retry");
        state.Cancel(false); Check(!state.Pending && !state.Schedule(epoch, args, 12, false), "new AI command did not cancel retry");
        Check(!state.Schedule(state.Epoch, args, 12, true), "overwrote current request");
        state.Retries = 1; state.Cancel(true); Check(state.Retries == 1, "retry budget reset on automatic request");
        Check(state.Schedule(state.Epoch, args, 12, false), "second retry missing");
        state.Retries = 2; state.Cancel(true); Check(!state.Schedule(state.Epoch, args, 15, false), "third retry allowed");
        state.Cancel(false); Check(state.Retries == 0, "new independent request retained old budget");
        Check(!state.Schedule(state.Epoch, null, 20, false), "unknown wander spec retried");
        Console.WriteLine("PASS wander policy: delay, duplicate prevention, new-command cancellation, pending-request protection and retry budget.");
    }
    private static void AttachmentTests()
    {
        string game = (Environment.GetEnvironmentVariable("ATT_GAME_PATH")! + "/");
        Assembly root = Assembly.LoadFrom(game + "A Township Tale_Data/Managed/Root.Township.dll");
        Core.Bind(root);
        var recovery = new HarmonyLib.Harmony("ATT.CaveNavigationRecovery.Offline");
        var daily = new HarmonyLib.Harmony("ATT.CaveNavigationRecovery.DailyOffline");
        var cave = new HarmonyLib.Harmony("ATT.CaveNavigationRecovery.CaveOffline");
        string hooks = Path.GetTempFileName();
        try
        {
            Assembly dailyAssembly = Assembly.LoadFrom(game + "Mods/DailyGarbageCollection.dll");
            var patch = dailyAssembly.GetType("DailyGarbageCollection.Patch", true)!;
            daily.Patch(Core.ScanStep, transpiler: new HarmonyMethod(patch, "ReplaceScanCollect"));
            Assembly caveAssembly = typeof(CaveNavigationRecovery.Core).Assembly;
            caveAssembly.GetType("CaveStepDiagnostics.PathProbe", true)!.GetMethod("AttachAll", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { root, cave, hooks });
            Check(File.ReadAllLines(hooks).Skip(1).All(x => x.Contains(",attached,")), "cave hooks failed");
            Core.Attach(recovery);
            foreach (var target in new[] { Core.Generate, Core.Need, Core.ScanStep, Core.Wander, Core.Failure, Core.Cancel })
                Check(HarmonyLib.Harmony.GetPatchInfo(target).Owners.Contains(recovery.Id), "attachment missing: " + target.Name);
            Check(HarmonyLib.Harmony.GetPatchInfo(Core.ScanStep).Owners.Contains(daily.Id) && HarmonyLib.Harmony.GetPatchInfo(Core.ScanStep).Owners.Contains(cave.Id), "existing scan patches missing");
            recovery.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(Core.ScanStep).Owners.Contains(daily.Id) && HarmonyLib.Harmony.GetPatchInfo(Core.ScanStep).Owners.Contains(cave.Id), "rollback removed other mods");
            Console.WriteLine("PASS installed game metadata and six Harmony targets attach alongside actual daily GC transpiler and cave diagnostics; rollback preserves both owners. Unity game methods not executed.");
        }
        finally { recovery.UnpatchSelf(); daily.UnpatchSelf(); cave.UnpatchSelf(); File.Delete(hooks); }
    }
    private static void FailureHookTests()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        FieldInfo preparing = typeof(Core).GetField("preparingPath", flags)!;
        FieldInfo requesting = typeof(Core).GetField("requesting", flags)!;
        FieldInfo thread = typeof(Core).GetField("ownerThread", flags)!;
        object? oldPreparing = preparing.GetValue(null), oldRequesting = requesting.GetValue(null), oldThread = thread.GetValue(null);
        var patcher = new HarmonyLib.Harmony("ATT.CaveNavigationRecovery.FailureFixture");
        try
        {
            preparing.SetValue(null, typeof(FakeNavigator).GetField("preparingPath"));
            requesting.SetValue(null, typeof(FakeNavigator).GetField("isRequestingPath"));
            thread.SetValue(null, System.Threading.Thread.CurrentThread.ManagedThreadId);
            patcher.Patch(typeof(FakeNavigator).GetMethod("Fail"), prefix: new HarmonyMethod(typeof(Core), "BeforeFailure"));
            var path = new object(); var nav = new FakeNavigator { preparingPath = path, isRequestingPath = true };
            bool called = false;
            nav.FailureCallback = () => { Check(!nav.isRequestingPath && nav.preparingPath == null, "AI callback saw stuck flag"); called = true; };
            nav.Fail(path); Check(called, "failure notification suppressed");
            var newer = new object(); nav.preparingPath = newer; nav.isRequestingPath = true;
            nav.FailureCallback = () => Check(nav.isRequestingPath && ReferenceEquals(nav.preparingPath, newer), "stale failure cancelled newer request");
            nav.Fail(path);
            var sentinel = new InvalidOperationException("AI callback failed");
            nav.preparingPath = path; nav.isRequestingPath = true; nav.FailureCallback = () => throw sentinel;
            try { nav.Fail(path); throw new Exception("callback exception swallowed"); }
            catch (InvalidOperationException e) { Check(ReferenceEquals(e, sentinel) && !nav.isRequestingPath, "failure exception/flag changed"); }
            patcher.UnpatchSelf();
            nav.preparingPath = path; nav.isRequestingPath = true; nav.FailureCallback = () => { };
            nav.Fail(path); Check(nav.isRequestingPath && ReferenceEquals(nav.preparingPath, path), "rollback did not restore behavior");
            Console.WriteLine("PASS actual failure prefix in Harmony fixture: stale flag cleared before original AI event, newer request preserved, callback exception preserved, unpatch restores behavior.");
        }
        finally
        {
            patcher.UnpatchSelf(); preparing.SetValue(null, oldPreparing); requesting.SetValue(null, oldRequesting); thread.SetValue(null, oldThread);
        }
    }
    private static void Main() { ScanTests(); WanderTests(); FailureHookTests(); AttachmentTests(); }
}
