using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace CaveNavigationRecovery
{
    internal sealed class ScanState
    {
        public ScanState() { }
        internal object? CompletedGraph;
        internal bool Running;
        internal int Attempts;
        internal ScanRecovery? Owner;
    }
    internal interface IScanHost
    {
        bool Alive { get; }
        double Now { get; }
        object? Graph { get; }
        bool Scanning { get; }
        IEnumerator CreateOriginal();
        void RemoveIncompleteGraph(object graph);
        void Log(string message);
    }
    internal interface IScanLifecycle
    {
        bool CancellationRequested { get; }
        bool GraphRegistered(object graph);
        void Settled(Exception? failure, bool cancelled);
        void Observe(string phase, double seconds, string detail);
    }
    // Original procedural iterator, or the matching static scan sequence, one
    // step at a time. Never writes A* locks or isScanning.
    internal sealed class ScanRecovery : IEnumerator, IDisposable
    {
        [ThreadStatic] internal static ScanRecovery? CurrentScan;
        internal static readonly ScanAdmission Admission = new ScanAdmission();
        private static readonly HashSet<ScanRecovery> Live = new HashSet<ScanRecovery>();
        private static readonly HashSet<ScanRecovery> Detached = new HashSet<ScanRecovery>();
        internal static long Started, CompletedCount, FailedCount, CancelledCount, Retried, DeferredCancellations, ObservationErrors;
        internal static int Pending => Live.Count;
        internal static int DetachedCount => Detached.Count;
        private readonly IScanHost host;
        private readonly IScanLifecycle? lifecycle;
        private readonly ScanState state;
        private readonly Func<bool>? waitBeforeGraph;
        private IEnumerator? original;
        private bool acquired, ended, admitted, nativeStarted, cancelRequested, detached, finishing;
        private object? completedThisAttempt;
        private double retryAt, waitSince = -1, started, attemptStarted;
        private string waitReason = "";
        private Exception? lastError;
        public object? Current { get; private set; }
        internal bool HasActiveNativeScan => !ended && !detached && original != null && nativeStarted && host.Scanning;
        internal ScanRecovery(IScanHost host, ScanState state, Func<bool>? waitBeforeGraph = null)
        { this.host = host; lifecycle = host as IScanLifecycle; this.state = state; this.waitBeforeGraph = waitBeforeGraph; }
        internal void Completed(object graph)
        {
            if (ReferenceEquals(host.Graph, graph)) completedThisAttempt = graph;
        }
        internal void NativeStep()
        {
            if (nativeStarted) return;
            nativeStarted = true;
            Observe("native_scan", host.Now - attemptStarted, "warmup_finished=true");
        }
        internal static bool IsReady(ScanState state, object? graph) => graph != null && ReferenceEquals(state.CompletedGraph, graph);
        private void Observe(string phase, double seconds, string detail)
        {
            try { lifecycle?.Observe(phase, Math.Max(0, seconds), detail); }
            catch { ObservationErrors++; }
        }
        public bool MoveNext()
        {
            if (ended || detached) return false;
            return Advance();
        }
        private bool Advance()
        {
            try { return Step(); }
            catch (Exception error)
            {
                lastError = error;
                Finish(error, error is OperationCanceledException);
                throw;
            }
        }
        private bool Step()
        {
            Current = null;
            if (!host.Alive || (lifecycle?.CancellationRequested ?? false)) cancelRequested = true;
            if (cancelRequested && !(original != null && nativeStarted && host.Scanning))
            { Finish(null, true); return false; }
            if (!acquired)
            {
                if (state.Running) return Wait("same_owner");
                if (IsReady(state, host.Graph)) { ended = true; return false; }
                state.Running = acquired = true;
                state.Owner = this;
                state.CompletedGraph = null;
                started = host.Now;
                Live.Add(this); Started++;
                Observe("queued", 0, "waiting=" + Admission.Waiting);
            }
            if (!admitted)
            {
                if (!Admission.TryEnter(this, host.Scanning)) return Wait("admission");
                admitted = true; EndWait();
                Observe("admitted", host.Now - started, "shared_static_and_procedural=true");
            }
            if (original == null)
            {
                if (host.Now < retryAt) return true;
                if (host.Scanning) return Wait("native_idle");
                if (state.Attempts >= 3) throw new InvalidOperationException("Cave scan recovery exhausted three attempts; inspect navigation logs", lastError);
                object? graph = host.Graph;
                if (graph != null) host.RemoveIncompleteGraph(graph);
                if (waitBeforeGraph?.Invoke() == true) return Wait("existing_graph_work");
                EndWait();
                state.Attempts++;
                completedThisAttempt = null; nativeStarted = false;
                attemptStarted = host.Now;
                original = host.CreateOriginal();
                host.Log("scan attempt=" + state.Attempts);
                Observe("attempt", 0, "attempt=" + state.Attempts);
            }
            ScanRecovery? previous = CurrentScan;
            bool more;
            try
            {
                CurrentScan = this;
                more = original.MoveNext();
                if (more) { Current = original.Current; return true; }
            }
            catch (OperationCanceledException) { cancelRequested = true; Finish(null, true); return false; }
            catch (Exception e) { return Failed(e.GetType().Name + ": " + e.Message, e); }
            finally { CurrentScan = previous; }
            DisposeOriginal();
            if (completedThisAttempt == null || !ReferenceEquals(completedThisAttempt, host.Graph) || host.Scanning ||
                (lifecycle != null && !lifecycle.GraphRegistered(completedThisAttempt)))
                return Failed("iterator ended without a completed, registered graph scan");
            state.CompletedGraph = completedThisAttempt;
            state.Attempts = 0;
            host.Log("scan completed");
            Finish(null, cancelRequested);
            return false;
        }
        private bool Wait(string reason)
        {
            if (waitSince < 0 || waitReason != reason) { EndWait(); waitSince = host.Now; waitReason = reason; }
            if (host.Now - waitSince >= 60) throw new TimeoutException("Cave recovery waited 60 seconds for " + reason + "; A* state was left untouched");
            return true;
        }
        private void EndWait()
        {
            if (waitSince >= 0) Observe("wait", host.Now - waitSince, "reason=" + waitReason);
            waitSince = -1; waitReason = "";
        }
        private bool Failed(string reason, Exception? failure = null)
        {
            try { DisposeOriginal(); }
            catch (Exception cleanup)
            {
                Observe("iterator_cleanup_failed", 0, cleanup.GetType().Name + ": " + cleanup.Message);
                if (failure == null) failure = cleanup;
            }
            if (failure != null) lastError = failure;
            host.Log("scan incomplete attempt=" + state.Attempts + ": " + reason);
            Observe("attempt_failed", host.Now - attemptStarted, "attempt=" + state.Attempts + ";reason=" + reason);
            if (cancelRequested) { Finish(null, true); return false; }
            if (state.Attempts >= 3) throw new InvalidOperationException("Cave scan recovery exhausted three attempts: " + reason, lastError);
            retryAt = host.Now + 5; Retried++;
            Observe("retry", 5, "attempt=" + state.Attempts);
            EndWait();
            return true;
        }
        private void DisposeOriginal()
        {
            var item = original; original = null;
            if (item is IDisposable disposable) disposable.Dispose();
        }
        private void CleanupIncomplete()
        {
            var graph = host.Graph;
            if (graph == null || ReferenceEquals(graph, state.CompletedGraph)) return;
            if (host.Scanning) { Observe("cleanup_deferred", 0, "native_scanning=true;native_flags_untouched=true"); return; }
            try { host.RemoveIncompleteGraph(graph); }
            catch (Exception error) { Observe("cleanup_failed", 0, error.GetType().Name + ": " + error.Message); }
        }
        private void Finish(Exception? error, bool cancelled, bool quitting = false)
        {
            if (ended || finishing) return;
            finishing = true;
            bool ownsState = acquired || !state.Running;
            Exception? cleanupFailure = null;
            // Disposing an active ScanAsync can strand its native lease. Normal
            // cancellation drains it on later main-thread updates. Shutdown
            // never spins or changes native scan state to emulate completion.
            try
            {
                if (!quitting || !host.Scanning) DisposeOriginal();
                EndWait();
            }
            catch (Exception cleanup)
            {
                cleanupFailure = cleanup;
                if (error == null) { error = cleanup; cancelled = false; }
            }
            finally
            {
                try { if (ownsState && (error != null || cancelled) && !quitting) CleanupIncomplete(); }
                catch (Exception cleanup)
                {
                    if (cleanupFailure == null) cleanupFailure = cleanup;
                    if (error == null) { error = cleanup; cancelled = false; }
                }
                // Bookkeeping and settlement are unconditional, including when
                // an original iterator or pause-lease Dispose itself throws.
                ended = true;
                finishing = false;
                Live.Remove(this); Detached.Remove(this); Admission.Release(this);
                if (acquired) { state.Running = false; state.Owner = null; }
                if (cancelled) CancelledCount++;
                else if (error != null) FailedCount++;
                else CompletedCount++;
                try
                {
                    Observe(cancelled ? "cancelled" : error != null ? "failed" : "complete", host.Now - started,
                        "attempt=" + state.Attempts + ";native_active=" + host.Scanning + (error == null ? "" : ";error=" + error.GetType().Name));
                }
                catch { ObservationErrors++; }
                try { if (ownsState) lifecycle?.Settled(error, cancelled); }
                catch { ObservationErrors++; }
            }
            if (cleanupFailure != null && ReferenceEquals(error, cleanupFailure)) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
        public void Dispose()
        {
            if (ended || finishing) return;
            cancelRequested = true;
            if (original != null && nativeStarted && host.Scanning)
            {
                if (!detached)
                {
                    detached = true; Detached.Add(this); DeferredCancellations++;
                    Observe("cancel_draining", host.Now - attemptStarted, "native_scan_keeps_its_lease=true");
                }
                return;
            }
            Finish(null, true);
        }
        internal static void TickDetached()
        {
            if (Detached.Count == 0) return;
            foreach (var recovery in new List<ScanRecovery>(Detached))
            {
                if (recovery.ended) continue;
                try { recovery.Advance(); }
                catch (Exception error) { recovery.host.Log("cancelled scan drain failed: " + error.GetType().Name + ": " + error.Message); }
            }
        }
        internal static void Tick()
        {
            if (Live.Count == 0) return;
            // The native rendering iterator manually advances its child without
            // a finally. A destroyed procedural generator may therefore stop
            // calling us without disposing the child. Reap that reservation and
            // drain an already-started native scan rather than blocking every
            // subsequent cave behind a dead admission owner.
            foreach (var recovery in new List<ScanRecovery>(Live))
            {
                if (recovery.ended || recovery.detached) continue;
                try
                {
                    if (!recovery.host.Alive || (recovery.lifecycle?.CancellationRequested ?? false)) recovery.Dispose();
                }
                catch (Exception error) { recovery.host.Log("scan cancellation cleanup failed: " + error.GetType().Name + ": " + error.Message); }
            }
            TickDetached();
        }
        internal static void Shutdown()
        {
            foreach (var recovery in new List<ScanRecovery>(Live))
                try { recovery.Finish(null, true, quitting: true); }
                catch { ObservationErrors++; }
        }
        public void Reset() => throw new NotSupportedException();
    }
}
