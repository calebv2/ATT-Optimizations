using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Alta.Caves;
using Alta.Chunks;
using HarmonyLib;
using Pathfinding;
using UnityEngine;

namespace CaveNavigationRecovery
{
    public sealed partial class Core
    {
        internal static MethodInfo StaticGenerate = null!, StaticLoad = null!;
        private static FieldInfo staticChunk = null!, staticCompletion = null!;
        private static readonly ConditionalWeakTable<object, StaticState> StaticStates = new ConditionalWeakTable<object, StaticState>();
        private static readonly HashSet<StaticState> PendingStatic = new HashSet<StaticState>();
        private static bool staticStopping;
        private static long staticStarted, staticComplete, staticFailed, staticCancelled;

        // No Unity access or task completion occurs on a cancellation callback.
        // Active native scans are allowed to finish before cancellation settles
        // their load task, so native graph leases are not abandoned mid-scan.
        internal sealed class StaticState
        {
            internal readonly ScanState Scan = new ScanState();
            internal readonly TaskCompletionSource<bool> Completion;
            internal readonly WeakReference<object> Owner;
            internal readonly int Layer;
            internal object? Graph, Manager;
            internal ScanRecovery? Recovery;
            private int cancelled;
            private CancellationToken cancellation;
            private readonly object tokenLock = new object();
            internal bool CancellationRequested => Volatile.Read(ref cancelled) != 0;
            internal StaticState(object owner, int layer, TaskCompletionSource<bool> completion)
            { Owner = new WeakReference<object>(owner); Layer = layer; Completion = completion; }
            internal void RequestCancellation(CancellationToken token = default)
            {
                if (Completion.Task.IsCompleted) return;
                lock (tokenLock)
                {
                    if (cancelled != 0) return;
                    cancellation = token; Volatile.Write(ref cancelled, 1);
                }
            }
            internal void Settle(Exception? failure, bool wasCancelled)
            {
                if (Completion.Task.IsCompleted) return;
                if (wasCancelled)
                {
                    CancellationToken token;
                    lock (tokenLock) token = cancellation;
                    if (Completion.TrySetCanceled(token)) staticCancelled++;
                }
                else if (failure != null)
                {
                    Fault(failure);
                }
                else if (ScanRecovery.IsReady(Scan, Graph))
                {
                    if (Completion.TrySetResult(true)) staticComplete++;
                }
                else
                {
                    Fault(new InvalidOperationException("Static cave recovery ended without verified scan completion"));
                }
            }
            private void Fault(Exception failure)
            {
                if (!Completion.TrySetException(failure)) return;
                staticFailed++;
                // Startup scans can fail before any chunk load awaits them.
                // Observe our owned fault without changing its later await result.
                _ = Completion.Task.Exception;
            }
        }

        private static void BindStatic(Assembly root)
        {
            Type cave = root.GetType("Alta.Chunks.StaticCaveChunk", true)!;
            Type chunk = root.GetType("Alta.Chunks.LocationChunk", true)!;
            StaticGenerate = Method(cave, "GeneratePathfinding", typeof(IEnumerator));
            StaticLoad = Method(cave, "Alta.Chunks.IProceduralGenerationStreamer.LoadStatic", typeof(Task), typeof(CancellationToken));
            staticChunk = Field(cave, "chunk", chunk);
            staticCompletion = Field(cave, "pathfindingGenerated", typeof(TaskCompletionSource<bool>));
            // The supported assembly's static scan sequence is graph creation,
            // the chunk bounds, ScanAsync, then SetResult. Keep that sequence;
            // settle the same native completion source only after validation.
            if (StaticGenerate.GetMethodBody() == null || StaticLoad.GetMethodBody() == null ||
                typeof(CaveSettings<StreamingSettings>).GetProperty("Instance", Flags | BindingFlags.FlattenHierarchy) == null ||
                typeof(StreamingSettings).GetMethod("GetCavePathfindingGraph", Flags) == null)
                throw new NotSupportedException("Static cave scan metadata changed");
        }
        private static void AttachStatic(HarmonyLib.Harmony patcher)
        {
            patcher.Patch(StaticGenerate, prefix: new HarmonyMethod(typeof(Core), nameof(ReplaceStaticScan)));
            patcher.Patch(StaticLoad, prefix: new HarmonyMethod(typeof(Core), nameof(StaticLoadBegin)),
                postfix: new HarmonyMethod(typeof(Core), nameof(StaticLoadEnd)), finalizer: new HarmonyMethod(typeof(Core), nameof(StaticLoadFinally)));
        }
        private static StaticState StateFor(object owner)
        {
            if (!StaticStates.TryGetValue(owner, out var state))
            {
                state = new StaticState(owner, ((StaticCaveChunk)owner).LayerIdentifier, (TaskCompletionSource<bool>)staticCompletion.GetValue(owner));
                StaticStates.Add(owner, state);
            }
            if (!state.Completion.Task.IsCompleted)
            {
                if (staticStopping) { state.RequestCancellation(); state.Settle(null, true); }
                else PendingStatic.Add(state);
            }
            return state;
        }
        private static bool ReplaceStaticScan(object __instance, ref IEnumerator __result)
        {
            if (!OnThread) return true;
            var state = StateFor(__instance);
            if (state.Completion.Task.IsCompleted) { __result = Empty(); return false; }
            var recovery = new ScanRecovery(new StaticHost(__instance, state), state.Scan, CavePerformance.ScanPacing.BeforeGraphRegistration());
            if (state.Recovery == null) state.Recovery = recovery;
            __result = recovery;
            staticStarted++;
            return false;
        }
        private static IEnumerator Empty() { yield break; }

        private sealed class StaticHost : IScanHost, IScanLifecycle
        {
            private readonly object owner;
            private readonly StaticState state;
            internal StaticHost(object owner, StaticState state) { this.owner = owner; this.state = state; }
            public bool Alive => owner is UnityEngine.Object value && value != null &&
                (state.Manager == null || ReferenceEquals(active.GetValue(null), state.Manager));
            public double Now => Time.realtimeSinceStartup;
            public object? Graph => state.Graph;
            private object Astar => active.GetValue(null) ?? throw new InvalidOperationException("A* unavailable");
            public bool Scanning => (bool)scanning.GetValue(Astar, null);
            public bool CancellationRequested => state.CancellationRequested;
            public bool GraphRegistered(object candidate) => state.Manager != null && ReferenceEquals(active.GetValue(null), state.Manager) &&
                ReferenceEquals(candidate, state.Graph) && (int)Invoke(getIndex, astarData.GetValue(state.Manager)!, candidate)! >= 0;
            public void Settled(Exception? failure, bool cancelled) => state.Settle(failure, cancelled);
            public void Observe(string phase, double seconds, string detail) => TraceScan("static", state.Layer, phase, seconds, detail);
            public void Log(string message) => Core.Log("static cave layer=" + state.Layer + " " + message);
            public void RemoveIncompleteGraph(object incomplete)
            {
                object manager = Astar;
                if (!ReferenceEquals(manager, state.Manager) || !ReferenceEquals(incomplete, state.Graph))
                    throw new InvalidOperationException("Static scan does not own this graph or manager");
                if ((bool)scanning.GetValue(manager, null)) throw new InvalidOperationException("Cannot remove a graph while scanning");
                object data = astarData.GetValue(manager)!;
                if ((int)Invoke(getIndex, data, incomplete)! >= 0 && !(bool)Invoke(removeGraph, data, incomplete)!)
                    throw new InvalidOperationException("Incomplete static graph removal failed");
                state.Graph = null;
                Log("removed incomplete static graph");
            }
            public IEnumerator CreateOriginal() => Run();
            private IEnumerator Run()
            {
                var manager = (AstarPath)Astar;
                if (state.Manager != null && !ReferenceEquals(state.Manager, manager)) throw new OperationCanceledException("Static cave A* manager changed");
                state.Manager = manager;
                RecastGraph graph = CaveSettings<StreamingSettings>.Instance.GetCavePathfindingGraph();
                state.Graph = graph;
                Bounds bounds = ((LocationChunk)staticChunk.GetValue(owner)).Bounds;
                graph.forcedBoundsCenter = bounds.center;
                graph.forcedBoundsSize = bounds.size;
                // Original static sequence, without its unconditional SetResult.
                // Recovery observes the actual native iterator completion first.
                foreach (Progress progress in manager.ScanAsync(graph)) { _ = progress; yield return null; }
            }
        }
        private struct StaticLoadCall
        {
            internal CancellationTokenRegistration Registration;
            internal bool Registered;
        }
        private static void StaticLoadBegin(object __instance, CancellationToken cancellationToken, out StaticLoadCall __state)
        {
            __state = default;
            if (!OnThread) return;
            var state = StateFor(__instance);
            if (state.Completion.Task.IsCompleted || !cancellationToken.CanBeCanceled) return;
            __state.Registration = cancellationToken.Register(() => state.RequestCancellation(cancellationToken));
            __state.Registered = true;
        }
        private static void StaticLoadEnd(ref Task __result, StaticLoadCall __state)
        {
            if (__state.Registered && __result != null) ObserveRegistration(__result, __state.Registration);
        }
        internal static void ObserveRegistration(Task original, CancellationTokenRegistration registration)
        {
            // Cleanup observes the native task; it introduces no new task for
            // the native caller to await and never performs Unity work.
            _ = original.ContinueWith(_ =>
            {
                try { registration.Dispose(); }
                catch { Interlocked.Increment(ref ScanRecovery.ObservationErrors); }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        private static void StaticLoadFinally(StaticLoadCall __state, Exception? __exception)
        {
            if (__exception != null && __state.Registered) __state.Registration.Dispose();
        }
        private static void TickStatic()
        {
            if (PendingStatic.Count == 0) return;
            foreach (var state in new List<StaticState>(PendingStatic))
            {
                if (state.Completion.Task.IsCompleted) { PendingStatic.Remove(state); continue; }
                if (!state.Owner.TryGetTarget(out var owner) || !(owner is UnityEngine.Object value) || value == null) state.RequestCancellation();
                if (!state.CancellationRequested) continue;
                if (state.Recovery == null) state.Settle(null, true);
                else
                {
                    try { (state.Scan.Owner ?? state.Recovery).Dispose(); }
                    catch (Exception error) { Log("static cancellation cleanup failed: " + error.GetType().Name + ": " + error.Message); }
                }
            }
        }
        private static void ShutdownStatic()
        {
            staticStopping = true;
            var pending = new List<StaticState>(PendingStatic);
            PendingStatic.Clear();
            foreach (var state in pending) { state.RequestCancellation(); state.Settle(null, true); }
        }
        private static void TraceScan(string kind, int layerNumber, string phase, double seconds, string detail) =>
            CavePerformance.Core.Row("scan_lifecycle", seconds * 1000, "kind=" + kind + ";layer=" + layerNumber + ";phase=" + phase + ";" + detail);
        internal static string Health() => ";scan_admission_waiting=" + ScanRecovery.Admission.Waiting + ";scan_recovery_pending=" + ScanRecovery.Pending +
            ";scan_recovery_draining=" + ScanRecovery.DetachedCount + ";scan_recovery_started=" + ScanRecovery.Started +
            ";scan_recovery_completed=" + ScanRecovery.CompletedCount + ";scan_recovery_failed=" + ScanRecovery.FailedCount +
            ";scan_recovery_cancelled=" + ScanRecovery.CancelledCount + ";scan_recovery_retries=" + ScanRecovery.Retried +
            ";static_scan_started=" + staticStarted + ";static_scan_completed=" + staticComplete + ";static_scan_failed=" + staticFailed +
            ";static_scan_cancelled=" + staticCancelled + ";scan_observation_errors=" + ScanRecovery.ObservationErrors;
    }
}
