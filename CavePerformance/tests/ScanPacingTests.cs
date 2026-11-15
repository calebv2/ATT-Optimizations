using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using CaveNavigationRecovery;
using CavePerformance;
using HarmonyLib;
using Pathfinding;
using Core = CavePerformance.Core;

internal static class ScanPacingTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Host : ScanPacing.IPauseHost
    {
        internal bool ActiveValue = true, ReadyValue;
        internal double Time;
        internal int Acquired, Released;
        internal System.Action? OnRelease;
        public bool Active => ActiveValue;
        public bool Ready => ReadyValue;
        public double Now => Time;
        public IDisposable Acquire() { Acquired++; return new Lease(this); }
        private sealed class Lease : IDisposable
        {
            private Host? host;
            internal Lease(Host host) { this.host = host; }
            public void Dispose()
            {
                var value = host; host = null;
                if (value != null) { value.Released++; value.OnRelease?.Invoke(); }
            }
        }
    }
    private sealed class Source : IEnumerable<Progress>
    {
        internal int Enumerators, Moves, Disposed;
        internal Exception? GetFailure, FirstFailure;
        internal System.Action? OnFirst;
        public IEnumerator<Progress> GetEnumerator()
        {
            Enumerators++;
            if (GetFailure != null) throw GetFailure;
            return Run().GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        private IEnumerable<Progress> Run()
        {
            try
            {
                Moves++; OnFirst?.Invoke();
                if (FirstFailure != null) throw FirstFailure;
                yield return new Progress(.2f, "native-first");
                Moves++; yield return new Progress(.8f, "native-second");
                Moves++;
            }
            finally { Disposed++; }
        }
    }
    private sealed class NativeHost : ScanPacing.IPauseHost
    {
        internal readonly PathProcessor Processor;
        internal readonly object Queue;
        internal NativeHost(PathProcessor processor, object queue) { Processor = processor; Queue = queue; }
        public bool Active => true;
        public bool Ready => (bool)Queue.GetType().GetProperty("AllReceiversBlocked", Any)!.GetValue(Queue, null)!;
        public double Now => 0;
        public IDisposable Acquire() => new ScanPacing.NativeLease(Processor.PausePathfinding(false));
    }
    private sealed class RecoveryHost : IScanHost
    {
        public bool Alive => true;
        public double Now => 0;
        public object? Graph => null;
        public bool Scanning => false;
        public IEnumerator CreateOriginal() => throw new NotSupportedException();
        public void RemoveIncompleteGraph(object graph) => throw new NotSupportedException();
        public void Log(string text) { }
    }
    private static void Check(bool value, string text) { if (!value) throw new Exception(text); }
    private static long WaitTotal => (long)typeof(ScanPacing).GetField("waitMilliseconds", Any)!.GetValue(null)!;
    private static void Observer() { }
    internal static void Run()
    {
        ScanPacing.Bind();
        Lifecycle(); NativeOwnership(); Attachment();
        Check(ScanPacing.PendingCount == 0, "Warmup fixture retained a pending pause lease");
        Console.WriteLine("PASS asynchronous cave scan start: frame-yielded receiver warmup, bounded timeout/cancel/shutdown, native progress and exception identity, exact-once owned lease cleanup, no-worker fallback, actual native lock handoff preserving external owners, scoped factory attachment and rollback.");
    }
    private static void Lifecycle()
    {
        var host = new Host(); var source = new Source();
        long waitBefore = WaitTotal;
        source.OnFirst = () => host.Time += 17;
        using (var iterator = ScanPacing.Wrap(source, () => host, () => true).GetEnumerator())
        {
            Check(iterator.MoveNext() && iterator.Current.description.Contains("Waiting"), "Busy receivers did not produce a cave-frame wait");
            Check(host.Acquired == 1 && host.Released == 0 && source.Enumerators == 0 && source.Moves == 0, "Warmup entered native scan or released its pause before readiness");
            host.Time = 50; Check(iterator.MoveNext() && source.Moves == 0, "Second warmup frame entered native scan");
            host.ReadyValue = true;
            Check(iterator.MoveNext() && iterator.Current.description == "native-first" && iterator.Current.progress == .2f, "Native first progress changed");
            Check(host.Released == 1 && ScanPacing.PendingCount == 0, "Extra pause survived the first native scan step");
            Check(WaitTotal - waitBefore == 50, "Native first-step execution was counted as receiver warmup waiting");
            host.ActiveValue = false; host.ReadyValue = false;
            Check(iterator.MoveNext() && iterator.Current.description == "native-second", "Warmup continued controlling native scan after handing off");
            Check(!iterator.MoveNext() && source.Disposed == 1 && host.Released == 1, "Native completion/disposal changed");
        }
        host = new Host(); source = new Source();
        using (var iterator = ScanPacing.Wrap(source, () => host, () => true).GetEnumerator())
        {
            iterator.MoveNext(); host.Time = 10000;
            try { iterator.MoveNext(); throw new Exception("Warmup timeout was not bounded"); } catch (TimeoutException) { }
            Check(source.Moves == 0 && host.Released == 1 && !iterator.MoveNext(), "Timeout entered native or retained the owned pause");
        }
        foreach (bool shutdown in new[] { false, true })
        {
            host = new Host(); source = new Source(); bool live = true;
            using (var iterator = ScanPacing.Wrap(source, () => host, () => live).GetEnumerator())
            {
                iterator.MoveNext();
                if (shutdown) ScanPacing.Shutdown(); else live = false;
                try { iterator.MoveNext(); throw new Exception("Cancelled warmup continued"); } catch (OperationCanceledException) { }
                Check(source.Moves == 0 && host.Released == 1, "Cancellation/shutdown entered native or failed to release its own pause");
            }
        }
        host = new Host(); source = new Source();
        var abandoned = ScanPacing.Wrap(source, () => host, () => true).GetEnumerator(); abandoned.MoveNext(); abandoned.Dispose(); abandoned.Dispose();
        Check(host.Released == 1 && source.Enumerators == 0, "Pre-native disposal was not exact-once");
        var sentinel = new InvalidOperationException("native scan fixture failure");
        foreach (bool factoryFailure in new[] { false, true })
        {
            host = new Host { ReadyValue = true }; source = new Source();
            if (factoryFailure) source.GetFailure = sentinel; else source.FirstFailure = sentinel;
            using (var iterator = ScanPacing.Wrap(source, () => host, () => true).GetEnumerator())
            {
                try { iterator.MoveNext(); throw new Exception("Native failure was swallowed"); }
                catch (InvalidOperationException error) { Check(ReferenceEquals(error, sentinel), "Native failure identity changed"); }
                Check(host.Released == 1, "Native/factory failure retained the extra pause");
            }
        }
        host = new Host { ReadyValue = true, OnRelease = () => throw new ApplicationException("cleanup failure") };
        source = new Source { FirstFailure = sentinel };
        using (var iterator = ScanPacing.Wrap(source, () => host, () => true).GetEnumerator())
        {
            try { iterator.MoveNext(); throw new Exception("Native failure was swallowed by cleanup"); }
            catch (InvalidOperationException error) { Check(ReferenceEquals(error, sentinel), "Cleanup failure hid the original scan failure"); }
        }
        source = new Source();
        using (var iterator = ScanPacing.Wrap(source, () => null, () => true).GetEnumerator())
            Check(iterator.MoveNext() && iterator.Current.description == "native-first", "No-worker fallback changed native progress");
    }
    private static void NativeOwnership()
    {
        // Execute the installed native nonblocking Lock/Unlock methods. The two
        // worker acknowledgements are simulated, without starting path workers.
        var processor = (PathProcessor)FormatterServices.GetUninitializedObject(typeof(PathProcessor));
        var queueField = typeof(PathProcessor).GetField("queue", Any)!;
        var queue = Activator.CreateInstance(queueField.FieldType, Any, null, new object[] { 2 }, null)!;
        queueField.SetValue(processor, queue);
        typeof(PathProcessor).GetField("locks", Any)!.SetValue(processor, new List<int>());
        int resumed = 0;
        typeof(PathProcessor).GetField("OnQueueUnblocked", Any)!.SetValue(processor, new System.Action(() => resumed++));
        var external = processor.PausePathfinding(false);
        var native = default(PathProcessor.GraphUpdateLock);
        var host = new NativeHost(processor, queue);
        var source = new Source { OnFirst = () => { Check(host.Ready, "Native step ran before receiver acknowledgement"); native = processor.PausePathfinding(false); } };
        try
        {
            using (var iterator = ScanPacing.Wrap(source, () => host, () => true).GetEnumerator())
            {
                Check(iterator.MoveNext() && source.Moves == 0, "Actual native queue did not wait for both receivers");
                queue.GetType().GetField("blockedReceivers", Any)!.SetValue(queue, 2);
                Check(iterator.MoveNext() && source.Moves == 1, "Ready native queue did not hand off to original scan");
                Check(external.Held && native.Held && resumed == 0, "Warmup release removed a native/external lock or resumed workers prematurely");
                var locks = (List<int>)typeof(PathProcessor).GetField("locks", Any)!.GetValue(processor)!;
                Check(locks.Count == 2, "Warmup retained its extra native lock through scanning");
            }
            native.Release(); Check(external.Held && resumed == 0, "Native scan release removed the external owner");
            external.Release(); Check(resumed == 1, "Final native owner did not resume its own queue");
        }
        finally { if (native.Held) native.Release(); if (external.Held) external.Release(); }
    }
    private static void Attachment()
    {
        var harmony = new HarmonyLib.Harmony("ATT.CavePerformance.ScanPacingFixture");
        var observer = new HarmonyLib.Harmony("ATT.CavePerformance.ScanPacingObserver");
        var factory = typeof(AstarPath).GetMethod("ScanAsync", Any, null, new[] { typeof(NavGraph[]) }, null)!;
        bool enabled = Core.Config.AsyncScanStart; var context = ScanRecovery.CurrentScan;
        var astar = (AstarPath)FormatterServices.GetUninitializedObject(typeof(AstarPath));
        try
        {
            observer.Patch(factory, postfix: new HarmonyMethod(typeof(ScanPacingTests), nameof(Observer)));
            ScanPacing.Attach(harmony); Core.Config.AsyncScanStart = true;
            // Invoke the tiny iterator factory reflectively after attachment;
            // the host JIT must not inline it before this fixture patches it.
            IEnumerable<Progress> Invoke() => (IEnumerable<Progress>)factory.Invoke(astar, new object[] { new NavGraph[0] })!;
            ScanRecovery.CurrentScan = null;
            var ordinary = Invoke();
            Check(ordinary.GetType().Assembly == typeof(AstarPath).Assembly, "Unscoped native scan was wrapped");
            ScanRecovery.CurrentScan = new ScanRecovery(new RecoveryHost(), new ScanState());
            var cave = Invoke();
            Check(cave.GetType().Assembly == typeof(ScanPacing).Assembly, "Cave recovery scan factory was not wrapped");
            Core.Config.AsyncScanStart = false;
            var disabled = Invoke();
            Check(disabled.GetType().Assembly == typeof(AstarPath).Assembly, "Disabled setting still wrapped native scans");
            harmony.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(factory).Owners.Contains(observer.Id), "Scan pacing rollback removed unrelated scan observer");
        }
        finally { harmony.UnpatchSelf(); observer.UnpatchSelf(); Core.Config.AsyncScanStart = enabled; ScanRecovery.CurrentScan = context; }
    }
}
