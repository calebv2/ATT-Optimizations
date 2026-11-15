using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Alta.Chunks;
using Alta.Intelligence;
using Alta.Pathfinding;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using GamePath = Pathfinding.Path;

namespace CavePerformance
{
    internal static class Search
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal sealed class Binding
        {
            internal WeakReference<Navigator>? Nav;
            internal WeakReference<CaveLayerStreamer>? Cave;
            internal NavGraph? DirectGraph; // Used by isolated fixtures; production resolves the cave field.
            internal Bounds Bounds;
            internal int Generation, Layer;
            internal bool Initialization;
        }
        internal sealed class ScanMark { public ScanMark() { } internal volatile bool Complete; }
        private static readonly ConditionalWeakTable<GamePath, Binding> Paths = new ConditionalWeakTable<GamePath, Binding>();
        private static readonly ConditionalWeakTable<NavGraph, ScanMark> Scans = new ConditionalWeakTable<NavGraph, ScanMark>();
        private static readonly ConditionalWeakTable<Navigator, WeakReference<Agent>> Owners = new ConditionalWeakTable<Navigator, WeakReference<Agent>>();
        private static readonly ConditionalWeakTable<Agent, WeakReference<Navigator>> Navigators = new ConditionalWeakTable<Agent, WeakReference<Navigator>>();
        internal enum RejectReason { NoOwner, NoCave, UnsupportedCallback, Bounds, NoSubmission, UnsupportedPath, MissingBinding, GenerationMismatch, GraphNotReady, UnsupportedConstraint, FollowupBinding, NoInitOwner, Count }
        private static readonly long[] Rejections = new long[(int)RejectReason.Count];
        private static readonly long[] NextRejectionRow = new long[(int)RejectReason.Count];
        internal static long RegisteredOwners, Captured, Submitted, Prepared, InitCalls, InitQueries;
        internal static long Rejected(RejectReason reason) => Interlocked.Read(ref Rejections[(int)reason]);
        private static void Reject(RejectReason reason)
        {
            int index = (int)reason;
            Interlocked.Increment(ref Rejections[index]);
            // One diagnostic row per reason per minute; no file I/O or scene scans.
            if (!Core.Config.Timing || !Core.Ready) return;
            long now = (long)Core.Now, next = Interlocked.Read(ref NextRejectionRow[index]);
            if (now < next || Interlocked.CompareExchange(ref NextRejectionRow[index], now + 60000, next) != next) return;
            Core.Row("local_search_rejected", 0, "reason=" + reason + ";count=" + Rejected(reason));
        }
        internal static string Health()
        {
            string result = ";owner_links=" + Interlocked.Read(ref RegisteredOwners) + ";captures=" + Interlocked.Read(ref Captured) +
                ";paths_bound=" + Interlocked.Read(ref Submitted) + ";prepared_bound=" + Interlocked.Read(ref Prepared) +
                ";init_calls=" + Interlocked.Read(ref InitCalls) + ";init_queries=" + Interlocked.Read(ref InitQueries);
            for (int i = 0; i < (int)RejectReason.Count; i++) result += ";reject_" + ((RejectReason)i) + "=" + Interlocked.Read(ref Rejections[i]);
            return result;
        }
        [ThreadStatic] private static Binding? Submission;
        [ThreadStatic] internal static Binding? Preparation;
        [ThreadStatic] internal static Query? CurrentQuery;
        [ThreadStatic] private static bool bypass;
        internal sealed class Call { internal Binding? Previous; internal GamePath? Path; internal double Started; }
        internal sealed class Query
        {
            internal Query? Previous;
            internal NavGraph? Graph;
            internal NNConstraint? Constraint;
            internal double Started;
            internal int Checks, Rejected;
            internal bool Fallback;
        }
        private static FieldInfo caveGraph = null!, scanGraphs = null!;
        private static FieldInfo processorField = null!, workerThreads = null!;
        internal static PropertyInfo PathId = null!;
        internal static int Generation(GamePath path) => (ushort)PathId.GetValue(path, null);
        private static MethodInfo wander = null!, navigate = null!, wanderTo = null!, scanStep = null!, scanFactory = null!, start = null!, reset = null!, worker = null!, initialize = null!;
        internal static MethodInfo DirectionalCallback = null!;
        internal static MethodInfo WalkerStart = null!;
        private static FieldInfo directionalOwner = null!;
        internal static MethodInfo Nearest = null!, Suitable = null!, AbPrepare = null!, ConstantPrepare = null!;
        internal static long ScopedQueries, Fallbacks;
        internal static void Bind()
        {
            Type root = typeof(CaveLayerStreamer);
            processorField = typeof(AstarPath).GetField("pathProcessor", Flags) ?? throw new MissingFieldException("AstarPath.pathProcessor");
            workerThreads = typeof(PathProcessor).GetField("threads", Flags) ?? throw new MissingFieldException("PathProcessor.threads");
            if (processorField.FieldType != typeof(PathProcessor) || workerThreads.FieldType != typeof(Thread[])) throw new NotSupportedException("Worker processor metadata changed");
            PathId = typeof(GamePath).GetProperty("pathID", Flags) ?? throw new MissingMemberException("Path.pathID");
            if (PathId.PropertyType != typeof(ushort)) throw new NotSupportedException("Path generation changed");
            caveGraph = root.GetField("navGraph", Flags) ?? throw new MissingFieldException("CaveLayerStreamer.navGraph");
            if (caveGraph.FieldType != typeof(RecastGraph) || caveGraph.IsStatic) throw new NotSupportedException("Cave graph storage changed");
            wander = typeof(Navigator).GetMethod("Wander", Flags, null, new[] { typeof(Vector3), typeof(int), typeof(float), typeof(OnPathDelegate) }, null)!;
            navigate = typeof(Navigator).GetMethod("NavigateTo", Flags, null, new[] { typeof(Vector3), typeof(float) }, null)!;
            wanderTo = typeof(Navigator).GetMethod("WanderTo", Flags, null, new[] { typeof(GamePath) }, null)!;
            // The public directional Wander overload uses one game-owned
            // closure rather than WanderTo. Validate that exact closure and
            // captured navigator; arbitrary custom callbacks remain native.
            var directional = new List<MethodInfo>();
            foreach (var nested in typeof(Navigator).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                var owner = nested.GetField("<>4__this", Flags);
                if (owner?.FieldType != typeof(Navigator)) continue;
                foreach (var method in nested.GetMethods(Flags))
                {
                    var parameters = method.GetParameters();
                    if (method.Name.StartsWith("<Wander>", StringComparison.Ordinal) && method.ReturnType == typeof(void) &&
                        parameters.Length == 1 && parameters[0].ParameterType == typeof(GamePath)) directional.Add(method);
                }
            }
            if (directional.Count != 1) throw new NotSupportedException("Native directional Wander callback layout changed");
            DirectionalCallback = directional[0]; directionalOwner = DirectionalCallback.DeclaringType!.GetField("<>4__this", Flags)!;
            Nearest = typeof(AstarPath).GetMethod("GetNearest", Flags, null, new[] { typeof(Vector3), typeof(NNConstraint), typeof(GraphNode) }, null)!;
            Suitable = typeof(NNConstraint).GetMethod("SuitableGraph", Flags, null, new[] { typeof(int), typeof(NavGraph) }, null)!;
            AbPrepare = typeof(ABPath).GetMethod("Prepare", Flags)!;
            ConstantPrepare = typeof(ConstantPath).GetMethod("Prepare", Flags)!;
            start = typeof(AstarPath).GetMethod("StartPath", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(GamePath), typeof(bool) }, null)!;
            reset = typeof(GamePath).GetMethod("Reset", Flags)!;
            worker = typeof(AstarPath).GetMethod("InitializePathProcessor", Flags)!;
            initialize = typeof(Agent).GetMethod("InitializeAsServer", Flags)!;
            WalkerStart = typeof(Walker).GetMethod("Start", Flags)!;
            scanFactory = typeof(AstarPath).GetMethod("ScanAsync", Flags, null, new[] { typeof(NavGraph[]) }, null)!;
            Type[] iterators = Array.FindAll(typeof(AstarPath).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public), t => t.Name.StartsWith("<ScanAsync>d__", StringComparison.Ordinal));
            if (iterators.Length != 1) throw new NotSupportedException("Scan iterator changed");
            scanStep = iterators[0].GetMethod("MoveNext", Flags)!;
            scanGraphs = iterators[0].GetField("graphsToScan", Flags)!;
            foreach (var method in new[] { wander, navigate, wanderTo, Nearest, Suitable, AbPrepare, ConstantPrepare, start, reset, worker, scanFactory, scanStep, initialize, WalkerStart })
                if (method == null || method.GetMethodBody() == null) throw new MissingMethodException("Required pathfinding method unavailable");
            if (!Suitable.IsVirtual || Suitable.ReturnType != typeof(bool) || Nearest.ReturnType != typeof(NNInfo) ||
                scanGraphs == null || scanGraphs.FieldType != typeof(NavGraph[])) throw new NotSupportedException("Pathfinding signatures changed");
        }
        internal static void Attach(HarmonyLib.Harmony h)
        {
            HarmonyMethod Hook(string n) => new HarmonyMethod(typeof(Search), n);
            h.Patch(worker, prefix: Hook(nameof(Workers)));
            h.Patch(initialize, prefix: Hook(nameof(RegisterOwner)), postfix: Hook(nameof(RegisterOwner)));
            h.Patch(WalkerStart, prefix: Hook(nameof(InitializationBegin)), finalizer: Hook(nameof(InitializationEnd)));
            h.Patch(wander, prefix: Hook(nameof(WanderBegin)), finalizer: Hook(nameof(SubmissionEnd)));
            h.Patch(navigate, prefix: Hook(nameof(NavigateBegin)), finalizer: Hook(nameof(SubmissionEnd)));
            h.Patch(wanderTo, prefix: Hook(nameof(WanderResultBegin)), finalizer: Hook(nameof(SubmissionEnd)));
            h.Patch(DirectionalCallback, prefix: Hook(nameof(DirectionalResultBegin)), finalizer: Hook(nameof(SubmissionEnd)));
            h.Patch(start, prefix: Hook(nameof(Submit)));
            h.Patch(reset, prefix: Hook(nameof(Reset)));
            h.Patch(scanFactory, prefix: Hook(nameof(ScanBegin)));
            h.Patch(scanStep, postfix: Hook(nameof(ScanEnd)));
            h.Patch(AbPrepare, prefix: Hook(nameof(PrepareBegin)), finalizer: Hook(nameof(PrepareEnd)));
            h.Patch(ConstantPrepare, prefix: Hook(nameof(PrepareBegin)), finalizer: Hook(nameof(PrepareEnd)));
            h.Patch(Nearest, prefix: Hook(nameof(QueryBegin)), postfix: Hook(nameof(QueryResult)), finalizer: Hook(nameof(QueryEnd)));
            h.Patch(Suitable, prefix: Hook(nameof(Filter)));
        }
        internal static void Workers(AstarPath __instance)
        {
            if (Core.Ready && Core.Config.Workers > 0) __instance.threadCount = (ThreadCount)Core.Config.Workers;
        }
        internal static int ActualWorkers()
        {
            if (ReferenceEquals(AstarPath.active, null)) return 0;
            object? processor = processorField.GetValue(AstarPath.active);
            return processor == null ? 0 : (workerThreads.GetValue(processor) as Thread[])?.Length ?? 0;
        }
        internal static bool Inside(Bounds bounds, Vector3 point)
        {
            Vector3 delta = point - bounds.center, ext = bounds.extents;
            return Math.Abs(delta.x) <= ext.x && Math.Abs(delta.y) <= ext.y && Math.Abs(delta.z) <= ext.z;
        }
        // Walker is a serialized reference; it can live outside the Agent's
        // parent hierarchy. Use the native Agent -> Walker -> Navigator link.
        internal static Navigator? NavigatorFor(Agent agent) => agent.Walker == null ? null : agent.Walker.GetComponent<Navigator>();
        internal static void RegisterOwner(Agent __instance)
        {
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            try
            {
                Navigator? nav = NavigatorFor(__instance);
                if (Navigators.TryGetValue(__instance, out var old) && old.TryGetTarget(out var previous) && !ReferenceEquals(previous, nav))
                {
                    if (Owners.TryGetValue(previous, out var owner) && owner.TryGetTarget(out var agent) && ReferenceEquals(agent, __instance)) Owners.Remove(previous);
                    Navigators.Remove(__instance);
                }
                if (nav == null) return;
                if (Owners.TryGetValue(nav, out var current) && current.TryGetTarget(out var existing) && ReferenceEquals(existing, __instance)) return;
                Owners.Remove(nav); Owners.Add(nav, new WeakReference<Agent>(__instance));
                Navigators.Remove(__instance); Navigators.Add(__instance, new WeakReference<Navigator>(nav));
                Interlocked.Increment(ref RegisteredOwners);
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        internal static Agent? OwnerFor(Navigator nav)
        {
            if (Owners.TryGetValue(nav, out var owner) && owner.TryGetTarget(out var known) && known != null) return known;
            Owners.Remove(nav);
            return nav.GetComponentInParent<Agent>();
        }
        internal static Vector3 Position(Navigator nav) => nav.transform.position;
        internal static Navigator? NavigatorForWalker(Walker walker) => walker.GetComponent<Navigator>();
        internal static Vector3 WalkerPosition(Walker walker) => walker.topic.position;
        private static void InitializationBegin(Walker __instance, out Call __state)
        {
            __state = new Call { Previous = Preparation, Started = Core.Now };
            Preparation = null;
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            Interlocked.Increment(ref InitCalls);
            try
            {
                var nav = NavigatorForWalker(__instance);
                if (nav == null) { Reject(RejectReason.NoInitOwner); return; }
                var captured = Capture(nav);
                if (captured == null) { Reject(RejectReason.NoInitOwner); return; }
                if (!Inside(captured.Bounds, WalkerPosition(__instance))) { Reject(RejectReason.Bounds); return; }
                captured.Initialization = true; Preparation = captured;
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        private static void InitializationEnd(Call __state, Exception? __exception)
        {
            Preparation = __state.Previous;
            double elapsed = Core.Now - __state.Started;
            if (Core.Ready && Core.Config.Timing && (elapsed >= 1 || __exception != null))
                Core.Row("walker_initial_nearest", elapsed, "unit=native_Walker.Start", __exception);
        }
        internal static Binding? Capture(Navigator nav)
        {
            var agent = OwnerFor(nav);
            if (agent == null) { Reject(RejectReason.NoOwner); return null; }
            // Initial AI requests can precede final prefab/chunk assignment.
            // The narrowly scoped native LoadEntities context supplies ownership
            // during that interval, without needing an already assigned root.
            var chunk = agent.Entity?.PrefabRoot?.Chunk;
            // An assigned non-cave chunk is authoritative. A nested callback
            // for a surface NPC must not inherit somebody else's loading cave.
            var cave = chunk == null ? Loading.CurrentCave : chunk.ProceduralStreamer as CaveLayerStreamer;
            if (cave == null) { Reject(RejectReason.NoCave); return null; }
            Interlocked.Increment(ref Captured);
            return new Binding { Nav = new WeakReference<Navigator>(nav), Cave = new WeakReference<CaveLayerStreamer>(cave), Bounds = cave.Bounds, Layer = cave.LayerIdentifier };
        }
        private static void WanderBegin(Navigator __instance, Vector3 __0, OnPathDelegate __3, out Call __state)
        {
            __state = new Call { Previous = Submission };
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            Submission = null;
            try
            {
                // Accept only the two inspected native callbacks, with their
                // captured navigator identity. Custom callbacks may cross maps.
                if (!NativeCallback(__instance, __3)) { Reject(RejectReason.UnsupportedCallback); return; }
                var capture = Capture(__instance);
                if (capture == null) return;
                if (Inside(capture.Bounds, __0) && Inside(capture.Bounds, Position(__instance))) Submission = capture;
                else Reject(RejectReason.Bounds);
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        private static bool NativeCallback(Navigator nav, OnPathDelegate callback)
        {
            if (callback == null || callback.GetInvocationList().Length != 1) return false;
            if (callback.Method == wanderTo) return ReferenceEquals(callback.Target, nav);
            return callback.Method == DirectionalCallback && callback.Target != null &&
                ReferenceEquals(directionalOwner.GetValue(callback.Target), nav);
        }
        private static void DirectionalResultBegin(object __instance, GamePath __0, out Call __state)
        {
            __state = new Call { Previous = Submission };
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            try
            {
                if (directionalOwner.GetValue(__instance) is Navigator nav) WanderResultBegin(nav, __0, out __state);
                else { Submission = null; Reject(RejectReason.FollowupBinding); }
            }
            catch { Submission = null; Interlocked.Increment(ref Core.Errors); }
        }
        private static void NavigateBegin(Navigator __instance, Vector3 __0, out Call __state)
        {
            __state = new Call { Previous = Submission };
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            Submission = null;
            try
            {
                var capture = Capture(__instance);
                if (capture == null) return;
                if (Inside(capture.Bounds, __0) && Inside(capture.Bounds, Position(__instance))) Submission = capture;
                else Reject(RejectReason.Bounds);
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        private static void WanderResultBegin(Navigator __instance, GamePath __0, out Call __state)
        {
            __state = new Call { Previous = Submission };
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            Submission = null;
            try
            {
                if (!Paths.TryGetValue(__0, out var bound) || bound.Generation != Generation(__0) || bound.Nav == null ||
                    !bound.Nav.TryGetTarget(out var nav) || !ReferenceEquals(nav, __instance)) { Reject(RejectReason.FollowupBinding); return; }
                var current = Capture(nav);
                if (current != null && current.Layer == bound.Layer && current.Cave!.TryGetTarget(out var a) &&
                    bound.Cave != null && bound.Cave.TryGetTarget(out var b) && ReferenceEquals(a, b)) Submission = current;
                else Reject(RejectReason.FollowupBinding);
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        private static void SubmissionEnd(Call __state) => Submission = __state.Previous;
        private static void Submit(GamePath __0)
        {
            if (!Core.Ready || !Core.Main || !Core.Config.LocalSearch) return;
            if (Submission == null) { Reject(RejectReason.NoSubmission); return; }
            try
            {
                Type type = __0.GetType();
                if (type != typeof(ABPath) && type != typeof(ConstantPath)) { Reject(RejectReason.UnsupportedPath); return; }
                if (__0 is ABPath ab && (!Inside(Submission.Bounds, ab.originalStartPoint) || !Inside(Submission.Bounds, ab.originalEndPoint))) { Reject(RejectReason.Bounds); return; }
                if (__0 is ConstantPath cp && !Inside(Submission.Bounds, cp.originalStartPoint)) { Reject(RejectReason.Bounds); return; }
                BindPath(__0, Submission);
                Interlocked.Increment(ref Submitted);
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        internal static void BindPath(GamePath path, Binding source)
        {
            Paths.Remove(path);
            Paths.Add(path, new Binding { Nav = source.Nav, Cave = source.Cave, DirectGraph = source.DirectGraph,
                Bounds = source.Bounds, Layer = source.Layer, Generation = Generation(path) });
        }
        private static void Reset(GamePath __instance) => Paths.Remove(__instance);
        private static void ScanBegin(NavGraph[] graphsToScan)
        {
            var graphs = graphsToScan ?? AstarPath.active?.graphs;
            if (graphs == null) return;
            foreach (var graph in graphs) if (graph != null) Scans.GetOrCreateValue(graph).Complete = false;
        }
        private static void ScanEnd(object __instance, bool __result, bool __runOriginal)
        {
            if (__result || !__runOriginal || ReferenceEquals(AstarPath.active, null) || AstarPath.active.isScanning) return;
            if (scanGraphs.GetValue(__instance) is NavGraph[] graphs)
                foreach (var graph in graphs) if (graph != null) MarkScanned(graph);
        }
        internal static void MarkScanned(NavGraph graph) => Scans.GetOrCreateValue(graph).Complete = true;
        private static NavGraph? Expected(Binding binding)
        {
            NavGraph? graph = binding.DirectGraph;
            if (binding.Cave != null)
            {
                if (!binding.Cave.TryGetTarget(out var cave)) return null;
                graph = caveGraph.GetValue(cave) as NavGraph;
            }
            var astar = AstarPath.active;
            return graph != null && !ReferenceEquals(astar, null) && !astar.isScanning && astar.graphs != null &&
                Array.IndexOf(astar.graphs, graph) >= 0 && Scans.TryGetValue(graph, out var mark) && mark.Complete ? graph : null;
        }
        internal static void PrepareBegin(GamePath __instance, out Call __state)
        {
            __state = new Call { Previous = Preparation, Path = __instance, Started = Core.Now };
            Preparation = null;
            if (!Core.Ready || !Core.Config.LocalSearch) return;
            if (!Paths.TryGetValue(__instance, out var bound)) { Reject(RejectReason.MissingBinding); return; }
            if (bound.Generation != Generation(__instance)) { Reject(RejectReason.GenerationMismatch); return; }
            Preparation = bound; Interlocked.Increment(ref Prepared);
        }
        internal static void PrepareEnd(Call __state, Exception? __exception)
        {
            Preparation = __state.Previous;
            double elapsed = Core.Now - __state.Started;
            if (Core.Ready && (elapsed >= 1 || __exception != null))
                Core.Row("path_prepare", elapsed, "path=" + __state.Path?.GetType().Name + ";path_id=" + (__state.Path == null ? 0 : Generation(__state.Path)), __exception);
        }
        internal static void QueryBegin(NNConstraint constraint, out Query? __state)
        {
            __state = null;
            if (!Core.Ready || ((!Core.Config.Timing || !Core.InTransition) && Preparation == null)) return;
            var query = new Query { Previous = CurrentQuery, Started = Core.Now, Constraint = constraint };
            CurrentQuery = __state = query;
            if (!bypass && Core.Config.LocalSearch && Preparation != null && constraint != null &&
                (constraint.GetType() == typeof(NNConstraint) || constraint.GetType() == typeof(PathNNConstraint)))
            {
                try { query.Graph = Expected(Preparation); }
                catch { Interlocked.Increment(ref Core.Errors); }
                if (query.Graph != null)
                {
                    Interlocked.Increment(ref ScopedQueries);
                    if (Preparation.Initialization) Interlocked.Increment(ref InitQueries);
                }
                else Reject(RejectReason.GraphNotReady);
            }
            else if (!bypass && Preparation != null && Core.Config.LocalSearch) Reject(RejectReason.UnsupportedConstraint);
        }
        internal static bool Filter(NNConstraint __instance, NavGraph __1, ref bool __result)
        {
            var query = CurrentQuery;
            if (query == null || !ReferenceEquals(query.Constraint, __instance)) return true;
            query.Checks++;
            if (query.Graph == null || ReferenceEquals(query.Graph, __1)) return true;
            query.Rejected++; __result = false; return false;
        }
        internal static void QueryResult(AstarPath __instance, Vector3 position, NNConstraint constraint, GraphNode hint, ref NNInfo __result, Query? __state)
        {
            if (__state?.Graph == null || __result.node != null) return;
            Interlocked.Increment(ref Fallbacks); __state.Fallback = true;
            bool previous = bypass;
            try { bypass = true; __result = __instance.GetNearest(position, constraint, hint); }
            finally { bypass = previous; }
        }
        internal static void QueryEnd(Query? __state, Exception? __exception)
        {
            if (__state == null) return;
            CurrentQuery = __state.Previous;
            double elapsed = Core.Now - __state.Started;
            if (elapsed >= 5 || __exception != null || __state.Fallback)
                Core.Row("nearest_node_search", elapsed, "scoped=" + (__state.Graph != null) + ";graph_checks=" + __state.Checks +
                    ";graphs_rejected=" + __state.Rejected + ";fallback=" + __state.Fallback + ";layer=" + Preparation?.Layer + Timing.StackIfSlow(elapsed), __exception);
        }
    }
}
