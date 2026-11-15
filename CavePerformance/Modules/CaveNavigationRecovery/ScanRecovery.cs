using System;
using System.Collections;

namespace CaveNavigationRecovery
{
    internal sealed class ScanState
    {
        public ScanState() { }
        internal object? CompletedGraph;
        internal bool Running;
        internal int Attempts;
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
    // Drives the game's iterator one step at a time. Never changes A* locks or isScanning.
    internal sealed class ScanRecovery : IEnumerator, IDisposable
    {
        [ThreadStatic] internal static ScanRecovery? CurrentScan;
        private readonly IScanHost host;
        private readonly ScanState state;
        private IEnumerator? original;
        private bool acquired, ended;
        private object? completedThisAttempt;
        private double retryAt, waitSince = -1;
        private Exception? lastError;
        public object? Current { get; private set; }
        internal ScanRecovery(IScanHost host, ScanState state) { this.host = host; this.state = state; }
        internal void Completed(object graph)
        {
            if (ReferenceEquals(host.Graph, graph)) completedThisAttempt = graph;
        }
        internal static bool IsReady(ScanState state, object? graph) => graph != null && ReferenceEquals(state.CompletedGraph, graph);
        public bool MoveNext()
        {
            if (ended) return false;
            try { return Step(); }
            catch { Dispose(); throw; }
        }
        private bool Step()
        {
            Current = null;
            if (!host.Alive) { Dispose(); return false; }
            if (!acquired)
            {
                if (state.Running) return Wait("another recovery for this cave");
                if (IsReady(state, host.Graph)) { ended = true; return false; }
                state.Running = acquired = true;
                state.CompletedGraph = null;
                waitSince = -1;
            }
            if (original == null)
            {
                if (host.Now < retryAt) return true;
                if (host.Scanning) return Wait("A* to become idle");
                waitSince = -1;
                if (state.Attempts >= 3) throw new InvalidOperationException("Cave scan recovery exhausted three attempts; inspect navigation logs", lastError);
                state.Attempts++;
                // Only remove this streamer's incomplete graph, through the game's safe API.
                object? graph = host.Graph;
                if (graph != null) host.RemoveIncompleteGraph(graph);
                completedThisAttempt = null;
                original = host.CreateOriginal();
                host.Log("scan attempt=" + state.Attempts);
            }
            ScanRecovery? previous = CurrentScan;
            bool more;
            try
            {
                CurrentScan = this;
                more = original.MoveNext();
                if (more) { Current = original.Current; return true; }
            }
            catch (Exception e) { lastError = e; return Failed(e.GetType().Name + ": " + e.Message); }
            finally { CurrentScan = previous; }
            DisposeOriginal();
            if (completedThisAttempt == null || !ReferenceEquals(completedThisAttempt, host.Graph) || host.Scanning)
                return Failed("iterator ended without a completed graph scan");
            state.CompletedGraph = completedThisAttempt;
            state.Attempts = 0;
            host.Log("scan completed");
            Dispose();
            return false;
        }
        private bool Wait(string reason)
        {
            if (waitSince < 0) waitSince = host.Now;
            if (host.Now - waitSince >= 60) throw new TimeoutException("Cave recovery waited 60 seconds for " + reason + "; A* state was left untouched");
            return true;
        }
        private bool Failed(string reason)
        {
            DisposeOriginal();
            host.Log("scan incomplete attempt=" + state.Attempts + ": " + reason);
            if (state.Attempts >= 3) throw new InvalidOperationException("Cave scan recovery exhausted three attempts: " + reason, lastError);
            retryAt = host.Now + 5;
            waitSince = -1;
            return true;
        }
        private void DisposeOriginal()
        {
            var item = original; original = null;
            if (item is IDisposable disposable) disposable.Dispose();
        }
        public void Dispose()
        {
            if (ended) return;
            ended = true;
            try { DisposeOriginal(); }
            finally { if (acquired) state.Running = false; }
        }
        public void Reset() => throw new NotSupportedException();
    }
}
