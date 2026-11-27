using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CaveNavigationRecovery;
using HarmonyLib;
using Pathfinding;

namespace CavePerformance
{
    // Waits across existing cave iterator frames for a native, nonblocking
    // pause lease. The native scan still acquires and owns its original lease.
    internal static class ScanPacing
    {
        internal interface IPauseHost
        {
            bool Active { get; }
            bool Ready { get; }
            double Now { get; }
            IDisposable Acquire();
        }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static FieldInfo processorField = null!, queueField = null!, threadsField = null!, workItemLock = null!;
        private static PropertyInfo allBlocked = null!, terminating = null!;
        private static MethodInfo scanFactory = null!;
        private static readonly HashSet<Warmup> Pending = new HashSet<Warmup>();
        internal static long Started, Yielded, HandedOff, Timeouts, Cancelled, NativeFallbacks;
        internal static long GraphWorkYields, GraphWorkTimeouts;
        private static long waitMilliseconds, graphWorkWaitMilliseconds;
        private const double MaxGraphWorkWaitMs = 1000;
        internal static int PendingCount => Pending.Count;
        internal static void Bind()
        {
            if (typeof(AstarPath).Module.ModuleVersionId != Core.SupportedMvid) throw new NotSupportedException("Unsupported scan pacing game assembly");
            processorField = typeof(AstarPath).GetField("pathProcessor", Flags) ?? throw new MissingFieldException("AstarPath.pathProcessor");
            queueField = typeof(PathProcessor).GetField("queue", Flags) ?? throw new MissingFieldException("PathProcessor.queue");
            threadsField = typeof(PathProcessor).GetField("threads", Flags) ?? throw new MissingFieldException("PathProcessor.threads");
            workItemLock = typeof(AstarPath).GetField("workItemLock", Flags) ?? throw new MissingFieldException("AstarPath.workItemLock");
            allBlocked = queueField.FieldType.GetProperty("AllReceiversBlocked", Flags) ?? throw new MissingMemberException("ThreadControlQueue.AllReceiversBlocked");
            terminating = queueField.FieldType.GetProperty("IsTerminating", Flags) ?? throw new MissingMemberException("ThreadControlQueue.IsTerminating");
            scanFactory = typeof(AstarPath).GetMethod("ScanAsync", Flags, null, new[] { typeof(NavGraph[]) }, null) ?? throw new MissingMethodException("AstarPath.ScanAsync");
            var pause = typeof(PathProcessor).GetMethod("PausePathfinding", Flags, null, new[] { typeof(bool) }, null);
            var release = typeof(PathProcessor.GraphUpdateLock).GetMethod("Release", Flags, null, Type.EmptyTypes, null);
            var held = typeof(PathProcessor.GraphUpdateLock).GetProperty("Held", Flags);
            if (processorField.FieldType != typeof(PathProcessor) || threadsField.FieldType != typeof(Thread[]) ||
                workItemLock.IsStatic || workItemLock.FieldType != typeof(PathProcessor.GraphUpdateLock) ||
                allBlocked.PropertyType != typeof(bool) || terminating.PropertyType != typeof(bool) ||
                scanFactory.ReturnType != typeof(IEnumerable<Progress>) || scanFactory.GetMethodBody() == null ||
                pause?.ReturnType != typeof(PathProcessor.GraphUpdateLock) || pause.GetMethodBody() == null ||
                release?.ReturnType != typeof(void) || held?.PropertyType != typeof(bool))
                throw new NotSupportedException("Native nonblocking scan pause metadata changed");
        }
        internal static void Attach(HarmonyLib.Harmony harmony) => harmony.Patch(scanFactory,
            postfix: new HarmonyMethod(typeof(ScanPacing), nameof(AfterFactory)));
        private static void AfterFactory(AstarPath __instance, ref IEnumerable<Progress> __result)
        {
            if (!Core.Ready || !Core.Main || !Core.Config.AsyncScanStart || ScanRecovery.CurrentScan == null || __result == null) return;
            __result = Wrap(__result, () => CreateHost(__instance), () => Core.Ready && Core.Main);
        }
        private static IPauseHost? CreateHost(AstarPath astar)
        {
            if (!ReferenceEquals(AstarPath.active, astar)) throw new OperationCanceledException("Cave scan manager changed before warmup");
            var processor = processorField.GetValue(astar) as PathProcessor;
            if (processor == null || !(threadsField.GetValue(processor) is Thread[] threads) || threads.Length == 0) return null;
            object? queue = queueField.GetValue(processor);
            return queue == null ? null : new NativeHost(astar, processor, queue);
        }
        private sealed class NativeHost : IPauseHost
        {
            private readonly AstarPath astar;
            private readonly PathProcessor processor;
            private readonly object queue;
            internal NativeHost(AstarPath astar, PathProcessor processor, object queue) { this.astar = astar; this.processor = processor; this.queue = queue; }
            public bool Active => Core.Ready && Core.Main && ReferenceEquals(AstarPath.active, astar) &&
                ReferenceEquals(processorField.GetValue(astar), processor) && !(bool)terminating.GetValue(queue, null);
            public bool Ready => !astar.isScanning && (bool)allBlocked.GetValue(queue, null);
            public double Now => Core.Now;
            public IDisposable Acquire() => new NativeLease(processor.PausePathfinding(false));
        }
        internal static bool HasGraphWork(AstarPath astar) => ((PathProcessor.GraphUpdateLock)workItemLock.GetValue(astar)).Held ||
            astar.IsAnyGraphUpdateQueued || astar.IsAnyGraphUpdateInProgress || astar.IsAnyWorkItemInProgress;
        internal static Func<bool>? BeforeGraphRegistration()
        {
            if (!Core.Ready || !Core.Main || !Core.Config.AsyncScanStart || ReferenceEquals(AstarPath.active, null)) return null;
            var astar = AstarPath.active;
            var preflight = new GraphPreflight();
            return () => Core.Ready && Core.Main && ReferenceEquals(AstarPath.active, astar) && preflight.Defer(HasGraphWork(astar), Core.Now);
        }
        internal sealed class GraphPreflight
        {
            private double started = -1;
            internal bool Defer(bool existingWork, double now)
            {
                // A* advances async work items only outside scans and later
                // force-flushes pending work inside a scan. Yield before even
                // registering a cave graph, so existing work cannot see a
                // graph reserved to a scan that has not yet generated tiles.
                if (existingWork)
                {
                    if (started < 0) started = now;
                    if (now - started < MaxGraphWorkWaitMs)
                    {
                        Interlocked.Increment(ref GraphWorkYields);
                        return true;
                    }
                }
                if (started >= 0)
                {
                    if (existingWork) Interlocked.Increment(ref GraphWorkTimeouts);
                    double elapsed = now - started;
                    Interlocked.Add(ref graphWorkWaitMilliseconds, (long)Math.Max(0, elapsed));
                    Core.Row("scan_wait_existing_graph_work", elapsed, "pause_held=false;new_graph_registered=false;deadline_reached=" + existingWork);
                    started = -1;
                }
                return false;
            }
        }
        internal sealed class NativeLease : IDisposable
        {
            private PathProcessor.GraphUpdateLock value;
            private bool released;
            internal NativeLease(PathProcessor.GraphUpdateLock value) { this.value = value; }
            public void Dispose()
            {
                if (released) return;
                released = true;
                // The lease has a distinct native ID. Never release somebody
                // else's lock or write queue/isScanning flags to emulate it.
                if (value.Held) value.Release();
            }
        }
        internal static IEnumerable<Progress> Wrap(IEnumerable<Progress> source, Func<IPauseHost?> host, Func<bool> live) => new Wrapped(source, host, live);
        private sealed class Wrapped : IEnumerable<Progress>
        {
            private readonly IEnumerable<Progress> source;
            private readonly Func<IPauseHost?> host;
            private readonly Func<bool> live;
            internal Wrapped(IEnumerable<Progress> source, Func<IPauseHost?> host, Func<bool> live) { this.source = source; this.host = host; this.live = live; }
            public IEnumerator<Progress> GetEnumerator() => new Warmup(source, host, live);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
        private sealed class Warmup : IEnumerator<Progress>
        {
            private readonly IEnumerable<Progress> source;
            private readonly Func<IPauseHost?> createHost;
            private readonly Func<bool> live;
            private IPauseHost? host;
            private IDisposable? lease;
            private IEnumerator<Progress>? inner;
            private bool initialized, enteredNative, ended, cancelled;
            private double started, readyAt = -1;
            public Progress Current { get; private set; }
            object IEnumerator.Current => Current;
            internal Warmup(IEnumerable<Progress> source, Func<IPauseHost?> host, Func<bool> live) { this.source = source; createHost = host; this.live = live; }
            public bool MoveNext()
            {
                if (ended) return false;
                try
                {
                    if (!enteredNative && (cancelled || !live())) throw new OperationCanceledException("Cave scan warmup was cancelled before native scan entry");
                    if (!initialized)
                    {
                        initialized = true;
                        host = Pending.Count >= 16 ? null : createHost();
                        if (host == null) Interlocked.Increment(ref NativeFallbacks);
                        else
                        {
                            if (!host.Active) throw new OperationCanceledException("Cave scan processor is no longer active");
                            started = host.Now;
                            lease = host.Acquire(); Pending.Add(this); Interlocked.Increment(ref Started);
                        }
                    }
                    if (lease != null)
                    {
                        if (!host!.Active) throw new OperationCanceledException("Cave scan processor stopped during warmup");
                        if (host.Now - started >= 10000)
                        {
                            Interlocked.Increment(ref Timeouts);
                            throw new TimeoutException("Cave scan pause warmup exceeded 10 seconds; native scan was not entered");
                        }
                        if (!host.Ready)
                        {
                            Current = new Progress(.01f, "Waiting for cave path workers to pause");
                            Interlocked.Increment(ref Yielded); return true;
                        }
                    }
                    if (inner == null) inner = source.GetEnumerator();
                    bool first = !enteredNative; enteredNative = true;
                    if (first && lease != null) readyAt = host!.Now;
                    bool more = false;
                    Exception? nativeError = null;
                    try { more = inner.MoveNext(); }
                    catch (Exception error) { nativeError = error; throw; }
                    finally
                    {
                        // First native step acquires its own blocking lease
                        // while our ready lease still keeps receivers paused.
                        // Release only ours immediately after that step, even
                        // on native failure; never retain it through scanning.
                        if (first && lease != null)
                        {
                            try
                            {
                                double nativeStepMs = Math.Max(0, host!.Now - readyAt);
                                double waitMs = Math.Max(0, readyAt - started);
                                ReleaseLease();
                                if (nativeError == null && more) Interlocked.Increment(ref HandedOff);
                                Core.Row("scan_pause_warmup", waitMs, "unit=nonblocking_receiver_pause;native_first_step_ms=" + Core.F(nativeStepMs) + ";extra_pause_released=true;native_first_step=" +
                                    (nativeError != null ? "failed" : more ? "yielded" : "completed"));
                            }
                            catch (Exception cleanup)
                            {
                                if (nativeError == null) throw;
                                Interlocked.Increment(ref Core.Errors); Core.Row("scan_pause_cleanup_error", 0, "original_failure_preserved", cleanup);
                            }
                        }
                    }
                    if (more) { Current = inner.Current; return true; }
                    Dispose(); return false;
                }
                catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); CleanupAfterFailure(); throw; }
                catch { CleanupAfterFailure(); throw; }
            }
            private void ReleaseLease()
            {
                var owned = lease; lease = null; Pending.Remove(this);
                if (owned != null)
                {
                    if (host != null) Interlocked.Add(ref waitMilliseconds, (long)Math.Max(0, (readyAt >= 0 ? readyAt : host.Now) - started));
                    owned.Dispose();
                }
            }
            private void CleanupAfterFailure()
            {
                try { Dispose(); }
                catch (Exception error) { Interlocked.Increment(ref Core.Errors); Core.Row("scan_pause_cleanup_error", 0, "original_failure_preserved", error); }
            }
            internal void CancelWarmup()
            {
                cancelled = true;
                // Do not dispose a native iterator reentrantly if shutdown was
                // requested by one of its callbacks. Its owner still owns it.
                ReleaseLease();
            }
            public void Dispose()
            {
                if (ended) return;
                ended = true;
                try { inner?.Dispose(); }
                finally { ReleaseLease(); }
            }
            public void Reset() => throw new NotSupportedException();
        }
        internal static void Shutdown()
        {
            foreach (var pending in new List<Warmup>(Pending))
                try { pending.CancelWarmup(); }
                catch (Exception error) { Interlocked.Increment(ref Core.Errors); Core.Row("scan_pause_cleanup_error", 0, "shutdown", error); }
        }
        internal static string Health() => ";scan_warmups=" + Interlocked.Read(ref Started) + ";scan_warmup_yields=" + Interlocked.Read(ref Yielded) +
            ";scan_warmup_handoffs=" + Interlocked.Read(ref HandedOff) + ";scan_warmup_timeouts=" + Interlocked.Read(ref Timeouts) +
            ";scan_warmup_cancelled=" + Interlocked.Read(ref Cancelled) + ";scan_warmup_native_fallbacks=" + Interlocked.Read(ref NativeFallbacks) +
            ";scan_warmup_pending=" + Pending.Count + ";scan_warmup_wait_ms=" + Interlocked.Read(ref waitMilliseconds) +
            ";scan_existing_graph_work_yields=" + Interlocked.Read(ref GraphWorkYields) + ";scan_existing_graph_work_timeouts=" + Interlocked.Read(ref GraphWorkTimeouts) +
            ";scan_existing_graph_work_wait_ms=" + Interlocked.Read(ref graphWorkWaitMilliseconds);
    }
}
