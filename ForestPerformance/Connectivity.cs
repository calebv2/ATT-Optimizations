using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Pathfinding;
using Pathfinding.Util;

namespace ForestPerformance
{
    // Keep a native region label only when a replacement demonstrably preserves
    // connectivity at EVERY old boundary. Splits, merges, unknown writers and
    // NodeLink/custom GraphModifier callbacks always retain the native full fill.
    internal static class Connectivity
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int MaxTracked = 20000, MaxProofUnits = 20000;
        private const double ProofMs = 2;
        internal const uint MaxArea = 131071;
        private static MethodInfo processorFill = null!, rootFill = null!, process = null!, queueFill = null!;
        private static MethodInfo replaceTile = null!, ensure = null!, addWork = null!;
        private static FieldInfo processorField = null!, lastArea = null!, modifierRoot = null!, workQueue = null!;
        private static PropertyInfo workCount = null!, workItem = null!;
        private static Action<object> nativeFill = null!;
        private static readonly HashSet<MethodInfo> Known = new HashSet<MethodInfo>();
        private static readonly Dictionary<NavmeshBase, Change> Changes = new Dictionary<NavmeshBase, Change>();
        private static bool unknown = true, faulted;
        private static string reason = "startup_requires_native_fill";
        private static int workerFull, workerFills, fillEpoch, ownerEpoch;
        [ThreadStatic] private static int knownDepth, ensureDepth, processDepth;
        [ThreadStatic] private static string? fillReason;
        private static double nextHealth, nextCaller, nextRegistry;
        private static NavGraph[]? registrySnapshot;
        private static int[]? areaOwners;
        private static NavGraph[]? ownerGraphs;
        private static string ownerReason = "no_native_fill_index";
        private const int OwnerSliceUnits = 65536;
        private const double OwnerSliceMs = 1, OwnerSliceIntervalMs = 10, OwnerRetryMs = 1000;
        private static int topologyEpoch, requestedEpoch, requestedTopology;
        private static OwnerCensus? ownerBuild;
        private static AstarPath? requestedAstar;
        private static NavGraph[]? requestedGraphs;
        private static double nextOwnerSlice, ownerRetryAfter;
        internal static bool OwnerRebuildPending => requestedGraphs != null;
        internal static long OwnerSlices, OwnerSliceYields, OwnerRestarts, OwnerRetries;
        internal static double LastOwnerMaxSliceMs;
        internal static long OwnerCensuses, OwnerBudgetStops;
        internal static double LastOwnerCensusMs;
        internal static int LastOwnerCensusNodes;
        internal static long Scoped, Native, Fallbacks, Errors, UnknownItems, ProofUnits, NewNodes, BoundaryNodes;
        internal static long OutsideProofAttempts, OutsideProofRepairs, OutsideProofBudgetStops;
        internal sealed class Change
        {
            internal readonly HashSet<GraphNode> New = new HashSet<GraphNode>();
            internal readonly HashSet<GraphNode> Boundary = new HashSet<GraphNode>();
        }
        internal sealed class Plan
        {
            internal readonly List<KeyValuePair<GraphNode, uint>> Writes = new List<KeyValuePair<GraphNode, uint>>();
            internal uint Last;
            internal int Units;
            internal bool OutsideProofAttempted;
        }
        internal sealed class ProofBudget
        {
            internal int Units;
            private readonly long started = Stopwatch.GetTimestamp();
            private readonly int limit;
            private readonly double milliseconds;
            internal ProofBudget(int limit = MaxProofUnits, double milliseconds = ProofMs) { this.limit = limit; this.milliseconds = milliseconds; }
            internal bool Take(int n = 1)
            {
                Units += n;
                return Units <= limit && ((Units & 31) != 0 || TimeAvailable);
            }
            internal bool TimeAvailable => milliseconds <= 0 ||
                (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency <= milliseconds;
        }
        internal struct TileCall { internal NavmeshBase? Graph; internal int X, Z, W, D; }
        internal struct FillCall { internal double Started; internal string? Reason; }
        internal static void Bind()
        {
            if (typeof(AstarPath).Module.ModuleVersionId != new System.Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff")) throw new NotSupportedException("Unsupported connectivity game assembly");
            Type p = typeof(AstarPath).Assembly.GetType("Pathfinding.GraphUpdateProcessor", true)!;
            Type w = typeof(AstarPath).Assembly.GetType("Pathfinding.WorkItemProcessor", true)!;
            processorField = typeof(AstarPath).GetField("graphUpdates", F)!;
            lastArea = p.GetField("lastUniqueAreaIndex", F)!;
            modifierRoot = typeof(GraphModifier).GetField("root", F)!;
            rootFill = typeof(AstarPath).GetMethod("FloodFill", F, null, Type.EmptyTypes, null)!;
            processorFill = p.GetMethod("FloodFill", F, null, Type.EmptyTypes, null)!;
            process = w.GetMethod("ProcessWorkItems", F)!;
            addWork = w.GetMethod("AddWorkItem", F)!;
            ensure = w.GetMethod("EnsureValidFloodFill", F)!;
            queueFill = w.GetMethod("Pathfinding.IWorkItemContext.QueueFloodFill", F)!;
            workQueue = w.GetField("workItems", F)!;
            workCount = workQueue.FieldType.GetProperty("Count", F)!;
            workItem = workQueue.FieldType.GetProperty("Item", F)!;
            replaceTile = typeof(NavmeshBase).GetMethod("ReplaceTile", F, null,
                new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Int3[]), typeof(int[]) }, null)!;
            if (new MemberInfo?[] { processorField, lastArea, modifierRoot, rootFill, processorFill, process, addWork, ensure, queueFill, workQueue, workCount, workItem, replaceTile }.Any(x => x == null) ||
                processorField.FieldType != p || lastArea.FieldType != typeof(uint) || lastArea.IsInitOnly || modifierRoot.FieldType != typeof(GraphModifier))
                throw new NotSupportedException("Connectivity metadata changed");
            var dm = new DynamicMethod("ForestConnectivity_NativeFill", typeof(void), new[] { typeof(object) }, typeof(Connectivity), true);
            ILGenerator il = dm.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, p); il.Emit(OpCodes.Callvirt, processorFill); il.Emit(OpCodes.Ret);
            nativeFill = (Action<object>)dm.CreateDelegate(typeof(Action<object>));
            Known.Clear();
            foreach (Type t in new[] { typeof(TileHandler) }.Concat(typeof(TileHandler).GetNestedTypes(F)))
                foreach (MethodInfo m in t.GetMethods(F | BindingFlags.DeclaredOnly))
                {
                    string? n = new[] { "LoadTile", "ClearTile", "StartBatchLoad", "EndBatchLoad" }.FirstOrDefault(s => m.Name.StartsWith("<" + s + ">", StringComparison.Ordinal));
                    if (n == null || m.ReturnType != typeof(bool) || m.GetMethodBody() == null) continue;
                    var body = PatchProcessor.GetOriginalInstructions(m);
                    var calls = body.Where(x => (x.opcode == OpCodes.Call || x.opcode == OpCodes.Callvirt) && x.operand is MethodInfo).Select(x => (MethodInfo)x.operand).ToArray();
                    if (n == "LoadTile" || n == "ClearTile")
                    {
                        if (calls.Count(x => x.DeclaringType == typeof(NavmeshBase) && x.Name == "ReplaceTile") != 1 || calls.Count(x => x.Name == "QueueFloodFill" && x.DeclaringType == typeof(IWorkItemContext)) != 1) continue;
                    }
                    else if (calls.Count(x => x.DeclaringType == typeof(NavmeshBase) && x.Name == (n == "StartBatchLoad" ? "StartBatchTileUpdate" : "EndBatchTileUpdate")) != 1) continue;
                    Known.Add(m);
                }
            if (Known.Count != 4) throw new NotSupportedException("Expected four inspected native TileHandler work-item delegates; found " + Known.Count);
        }
        internal static void Attach(HarmonyLib.Harmony h)
        {
            HarmonyMethod H(string n) => new HarmonyMethod(typeof(Connectivity), n);
            h.Patch(rootFill, transpiler: H(nameof(ReplaceFill)));
            h.Patch(processorFill, prefix: H(nameof(FullBegin)), finalizer: H(nameof(FullEnd)));
            h.Patch(addWork, prefix: H(nameof(AddedWork)));
            h.Patch(process, prefix: H(nameof(ProcessBegin)), finalizer: H(nameof(ProcessEnd)));
            h.Patch(ensure, prefix: H(nameof(EnsureBegin)), finalizer: H(nameof(EnsureEnd)));
            h.Patch(queueFill, prefix: H(nameof(QueueRequested)));
            h.Patch(replaceTile, prefix: H(nameof(TileBegin)), finalizer: H(nameof(TileEnd)));
            foreach (MethodInfo m in Known) h.Patch(m, prefix: H(nameof(KnownBegin)), finalizer: H(nameof(KnownEnd)));
        }
        internal static IEnumerable<CodeInstruction> ReplaceFill(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList(); int n = 0;
            foreach (var i in list)
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && Equals(i.operand, processorFill))
                { i.opcode = OpCodes.Call; i.operand = typeof(Connectivity).GetMethod(nameof(Dispatch), F); n++; }
            if (n != 1) throw new NotSupportedException("Expected exactly one native graph-processor fill in AstarPath.FloodFill");
            return list;
        }
        private static void Record(string stage, double elapsed, string detail, Exception? error = null)
        {
            try { Core.Row(stage, elapsed, detail, error); }
            catch { Interlocked.Increment(ref Errors); }
        }
        private static bool Active => Core.Ready && Core.Main && Core.Config.Connectivity && !faulted && Volatile.Read(ref workerFills) == 0;
        private static void Invalidate(string why)
        {
            unknown = true; reason = why;
            Interlocked.Increment(ref topologyEpoch); CancelOwnerBuild();
            areaOwners = null; ownerGraphs = null; ownerReason = "unknown_topology_writer";
        }
        private static void Reset(bool trustworthy)
        {
            Changes.Clear(); unknown = !trustworthy; reason = trustworthy ? "none" : "unknown_mutation";
        }
        private static void SyncWorker()
        {
            if (Interlocked.Exchange(ref workerFull, 0) != 0)
            { Reset(false); Interlocked.Increment(ref topologyEpoch); CancelOwnerBuild(); areaOwners = null; ownerGraphs = null; ownerReason = "worker_native_fill_invalidated"; }
        }
        private static void KnownBegin(out int __state) { __state = knownDepth; knownDepth++; }
        private static void KnownEnd(int __state, Exception? __exception)
        {
            knownDepth = __state; if (__exception != null && Core.Main) Invalidate("tile_work_item_exception");
        }
        private static bool KnownItem(AstarWorkItem item)
        {
            bool found = false;
            foreach (Delegate? d in new Delegate?[] { item.init, item.initWithContext, item.update, item.updateWithContext })
            {
                if (d == null) continue;
                if (d.GetInvocationList().Length != 1 || !(d.Method is MethodInfo m) || !Known.Contains(m)) return false;
                found = true;
            }
            return found;
        }
        private static void AddedWork(AstarWorkItem __0)
        {
            if (!Core.Ready || !Core.Config.Connectivity) return;
            if (!Core.Main) { Interlocked.Exchange(ref workerFull, 1); return; }
            if (!KnownItem(__0)) { Interlocked.Increment(ref UnknownItems); Invalidate("unknown_work_item_added"); }
        }
        private static void ProcessBegin(object __instance, out int __state)
        {
            __state = processDepth; processDepth++;
            if (!Active) return;
            SyncWorker();
            try
            {
                object q = workQueue.GetValue(__instance)!; int count = (int)workCount.GetValue(q, null)!;
                if (count > 256) { Invalidate("work_item_queue_budget"); return; }
                var budget = new ProofBudget();
                for (int i = 0; i < count; i++)
                {
                    if (!budget.Take(4)) { Invalidate("work_item_provenance_budget"); return; }
                    if (!KnownItem((AstarWorkItem)workItem.GetValue(q, new object[] { i })!))
                    { Interlocked.Increment(ref UnknownItems); Invalidate("unknown_work_item"); break; }
                }
            }
            catch { Interlocked.Increment(ref Errors); Invalidate("work_item_inspection_error"); }
        }
        private static void ProcessEnd(int __state, Exception? __exception)
        {
            processDepth = __state; if (__exception != null && Core.Main) Invalidate("work_item_process_exception");
        }
        private static void EnsureBegin(out int __state) { __state = ensureDepth; ensureDepth++; }
        private static void EnsureEnd(int __state) { ensureDepth = __state; }
        private static void QueueRequested()
        {
            if (!Core.Main) { Interlocked.Exchange(ref workerFull, 1); return; }
            if (knownDepth == 0) Invalidate("unknown_fill_request");
        }
        private static IEnumerable<NavmeshTile> Tiles(NavmeshBase graph, int x, int z, int width, int depth)
        {
            var seen = new HashSet<NavmeshTile>();
            for (int dz = z; dz < z + depth; dz++) for (int dx = x; dx < x + width; dx++)
            {
                if (dx < 0 || dz < 0 || dx >= graph.tileXCount || dz >= graph.tileZCount) throw new InvalidOperationException("Unexpected tile bounds");
                NavmeshTile tile = graph.GetTile(dx, dz);
                if (tile != null && seen.Add(tile)) yield return tile;
            }
        }
        private static void TileBegin(NavmeshBase __instance, int x, int z, int w, int d, out TileCall __state)
        {
            __state = default;
            if (!Core.Ready || !Core.Config.Connectivity) return;
            Interlocked.Increment(ref topologyEpoch);
            if (!Core.Main) { Interlocked.Exchange(ref workerFull, 1); return; }
            // A published index intentionally survives known recuts: it proves
            // ownership of the OLD labels used by TryPlan. A partial census
            // cannot mix pre- and post-recut storage, even for a known writer.
            CancelOwnerBuild();
            if (!Active) return;
            SyncWorker();
            if (knownDepth == 0 || processDepth == 0) { Invalidate("unknown_tile_writer"); return; }
            if (unknown) return;
            try
            {
                if (w < 1 || d < 1 || (long)w * d > 64) { Invalidate("tile_capture_budget"); return; }
                var budget = new ProofBudget();
                var old = new HashSet<GraphNode>();
                foreach (var tile in Tiles(__instance, x, z, w, d)) if (tile.nodes != null)
                    foreach (var node in tile.nodes)
                    { if (!budget.Take() || old.Count >= MaxTracked) { Invalidate("tile_capture_budget"); return; } old.Add(node); }
                if (!Changes.TryGetValue(__instance, out var change))
                {
                    if (Changes.Count >= 8) { Invalidate("tracked_graph_budget"); return; }
                    Changes.Add(__instance, change = new Change());
                }
                foreach (GraphNode node in old)
                    node.GetConnections(other =>
                    {
                        if (unknown) return;
                        if (!budget.Take() || change.Boundary.Count + change.New.Count >= MaxTracked)
                        { Invalidate("tile_capture_budget"); return; }
                        if (!old.Contains(other)) change.Boundary.Add(other);
                    });
                if (unknown || change.Boundary.Count + change.New.Count > MaxTracked) { Invalidate("tile_capture_budget"); return; }
                __state = new TileCall { Graph = __instance, X = x, Z = z, W = w, D = d };
            }
            catch { Interlocked.Increment(ref Errors); Invalidate("tile_capture_error"); }
        }
        private static void TileEnd(TileCall __state, Exception? __exception)
        {
            if (__exception != null && Core.Main) { Invalidate("native_tile_exception"); return; }
            if (__state.Graph == null || !Active || unknown) return;
            try
            {
                var change = Changes[__state.Graph];
                var budget = new ProofBudget();
                foreach (var tile in Tiles(__state.Graph, __state.X, __state.Z, __state.W, __state.D))
                    if (tile.nodes != null) foreach (var node in tile.nodes)
                    {
                        if (!budget.Take() || change.Boundary.Count + change.New.Count >= MaxTracked) { Invalidate("tile_capture_budget"); return; }
                        change.New.Add(node);
                    }
                if (change.Boundary.Count + change.New.Count > MaxTracked) Invalidate("tile_capture_budget");
            }
            catch { Interlocked.Increment(ref Errors); Invalidate("tile_capture_error"); }
        }
        // Pure preflight: no GraphNode.Area is changed on either success or failure.
        // Multiple tile recuts collapse to their surviving final nodes.
        internal static bool TryPlan(IEnumerable<KeyValuePair<uint, Change>> changes, uint last, ProofBudget budget, out Plan plan, out string why,
            Func<uint, uint, bool>? ownerCheck = null, bool allowOutsideProof = false)
        {
            plan = new Plan { Last = last }; why = "none";
            foreach (var pair in changes)
            {
                uint graph = pair.Key; Change change = pair.Value;
                bool outsideProofEnabled = allowOutsideProof && graph == 0 && ownerCheck != null && last < MaxArea;
                var nodes = new HashSet<GraphNode>(change.New.Where(n => n != null && !n.Destroyed));
                var component = new Dictionary<GraphNode, int>();
                var outside = new Dictionary<GraphNode, int>();
                var labels = new List<uint>(); var stack = new Stack<GraphNode>();
                foreach (GraphNode seed in nodes)
                {
                    if (!budget.Take()) { why = "proof_budget"; return false; }
                    if (seed.GraphIndex != graph) { why = "cross_graph_node"; return false; }
                    if (component.ContainsKey(seed)) continue;
                    if (!seed.Walkable)
                    {
                        bool linked = false; seed.GetConnections(other => linked = true);
                        if (linked) { why = "unwalkable_connected_node"; return false; }
                        plan.Writes.Add(new KeyValuePair<GraphNode, uint>(seed, 0)); continue;
                    }
                    int id = labels.Count; labels.Add(0); component.Add(seed, id); stack.Push(seed);
                    bool bad = false; string failure = "none";
                    while (stack.Count > 0)
                    {
                        if (!budget.Take()) { why = "proof_budget"; return false; }
                        GraphNode current = stack.Pop();
                        current.GetConnections(other =>
                        {
                            if (bad) return;
                            if (!budget.Take()) { bad = true; failure = "proof_budget"; return; }
                            if (other == null || other.Destroyed || other.GraphIndex != graph) { bad = true; failure = "cross_graph_or_destroyed_edge"; return; }
                            if (!other.Walkable) { bad = true; failure = "unwalkable_connected_node"; return; }
                            // Native TriangleMesh tile edges must be reciprocal. An
                            // asymmetric/custom connection uses the native relabel.
                            if (outsideProofEnabled)
                            {
                                if (!TryTriangle(current, graph, out var currentTriangle) || !TryTriangle(other, graph, out var otherTriangle))
                                { bad = true; failure = "unsupported_triangle_connection"; return; }
                                if (!CheckReverseConnection(otherTriangle, currentTriangle, budget, out bool reciprocal))
                                { bad = true; failure = "proof_budget"; return; }
                                if (!reciprocal) { bad = true; failure = "asymmetric_connection"; return; }
                            }
                            else if (!other.ContainsConnection(current)) { bad = true; failure = "asymmetric_connection"; return; }
                            if (nodes.Contains(other))
                            {
                                if (component.TryGetValue(other, out int existing))
                                { if (existing != id) { bad = true; failure = "asymmetric_component"; } }
                                else { component.Add(other, id); stack.Push(other); }
                                return;
                            }
                            if (other.Area == 0 || other.Area > last) { bad = true; failure = "unlabelled_existing_boundary"; return; }
                            if (ownerCheck != null && !ownerCheck(other.Area, graph)) { bad = true; failure = "area_shared_or_owner_unknown"; return; }
                            if (outside.TryGetValue(other, out int previous) && previous != id) { bad = true; failure = "boundary_reaches_multiple_components"; return; }
                            outside[other] = id;
                            if (labels[id] != 0 && labels[id] != other.Area) { bad = true; failure = "old_areas_merge"; return; }
                            labels[id] = other.Area;
                        });
                        if (bad) { why = failure; return false; }
                    }
                }
                var oldRegionComponent = new Dictionary<uint, int>();
                HashSet<uint>? splitAreas = null;
                bool NoteComponent(uint area, int id)
                {
                    if (!oldRegionComponent.TryGetValue(area, out int previous))
                    {
                        oldRegionComponent.Add(area, id);
                        return true;
                    }
                    if (previous == id) return true;
                    // Overflowed native area IDs can identify different islands.
                    if (!outsideProofEnabled) return false;
                    (splitAreas ??= new HashSet<uint>()).Add(area);
                    return true;
                }
                foreach (GraphNode boundary in change.Boundary)
                {
                    if (!budget.Take()) { why = "proof_budget"; return false; }
                    if (boundary == null || boundary.Destroyed || nodes.Contains(boundary)) continue;
                    if (boundary.GraphIndex != graph || !boundary.Walkable || boundary.Area == 0) { why = "unsupported_old_boundary"; return false; }
                    if (ownerCheck != null && !ownerCheck(boundary.Area, graph)) { why = "area_shared_or_owner_unknown"; return false; }
                    if (!outside.TryGetValue(boundary, out int id)) { why = "old_boundary_disconnected"; return false; }
                    if (!NoteComponent(boundary.Area, id)) { why = "possible_old_area_split"; return false; }
                }
                // Validate all final contacts, including contacts created by
                // later recuts in this work-item batch.
                foreach (var edge in outside)
                {
                    if (outsideProofEnabled && !budget.Take()) { why = "proof_budget"; return false; }
                    if (!NoteComponent(edge.Key.Area, edge.Value)) { why = "possible_old_area_split"; return false; }
                }
                if (splitAreas != null)
                {
                    plan.OutsideProofAttempted = true;
                    foreach (uint area in splitAreas)
                        if (!OutsideContactsConnected(graph, area, outside, nodes, component, labels, budget, out why))
                            return false;
                }
                for (int i = 0; i < labels.Count; i++)
                    if (labels[i] == 0)
                    { if (plan.Last >= MaxArea) { why = "area_id_exhaustion"; return false; } labels[i] = ++plan.Last; }
                foreach (var entry in component) plan.Writes.Add(new KeyValuePair<GraphNode, uint>(entry.Key, labels[entry.Value]));
            }
            if (plan.OutsideProofAttempted && !budget.TimeAvailable) { why = "outside_proof_budget"; return false; }
            plan.Units = budget.Units;
            return true;
        }
        // If the replacement splits an old area into several components, test
        // whether all their external contacts still connect through surviving
        // tiles. Every surviving old boundary already has a replacement
        // contact, so proving this external connection preserves the old area.
        // New nodes are never used to traverse the outside graph.
        private static bool OutsideContactsConnected(uint graph, uint area, Dictionary<GraphNode, int> outside,
            HashSet<GraphNode> changed, Dictionary<GraphNode, int> component, List<uint> labels, ProofBudget budget, out string why)
        {
            why = "outside_proof_budget";
            if (!budget.TimeAvailable) return false;
            var remaining = new HashSet<TriangleMeshNode>();
            TriangleMeshNode? start = null;
            foreach (var entry in outside)
            {
                if (!budget.Take()) return false;
                if (entry.Key.Area != area) continue;
                if (!TryTriangle(entry.Key, graph, out var boundary)) { why = "outside_proof_unsupported_edge"; return false; }
                remaining.Add(boundary);
                if (start == null) start = boundary;
            }
            if (start == null) { why = "outside_proof_disconnected"; return false; }
            remaining.Remove(start);
            var seen = new HashSet<TriangleMeshNode> { start };
            var pending = new Queue<TriangleMeshNode>();
            pending.Enqueue(start);
            while (remaining.Count > 0 && pending.Count > 0)
            {
                if (!budget.Take() || !budget.TimeAvailable) return false;
                TriangleMeshNode current = pending.Dequeue();
                if (current.connections == null) continue;
                foreach (var link in current.connections)
                {
                    if (!budget.Take()) return false;
                    if (!TryTriangle(link.node, graph, out var other)) { why = "outside_proof_unsupported_edge"; return false; }
                    bool changedNode = changed.Contains(other);
                    if (changedNode ? !component.TryGetValue(other, out int id) || labels[id] != area : other.Area != area)
                    { why = "outside_proof_unsupported_edge"; return false; }
                    if (!CheckReverseConnection(other, current, budget, out bool reciprocal)) return false;
                    if (!reciprocal) { why = "outside_proof_unsupported_edge"; return false; }
                    if (!changedNode && seen.Add(other))
                    {
                        remaining.Remove(other);
                        pending.Enqueue(other);
                    }
                }
            }
            if (!budget.TimeAvailable) return false;
            why = remaining.Count == 0 ? "none" : "outside_proof_disconnected";
            return remaining.Count == 0;
        }
        private static bool TryTriangle(GraphNode? node, uint graph, out TriangleMeshNode triangle)
        {
            if (node != null && !node.Destroyed && node.Walkable && node.GraphIndex == graph && node.GetType() == typeof(TriangleMeshNode))
            { triangle = (TriangleMeshNode)node; return true; }
            triangle = null!;
            return false;
        }
        private static bool CheckReverseConnection(TriangleMeshNode node, TriangleMeshNode neighbor, ProofBudget budget, out bool found)
        {
            found = false;
            if (node.connections == null) return true;
            foreach (var reverse in node.connections)
            {
                if (!budget.Take()) return false;
                if (ReferenceEquals(reverse.node, neighbor)) { found = true; break; }
            }
            return true;
        }
        // Main-thread cursor over native Recast storage. The second pass checks
        // tile/node-array identity and recomputes ownership, including in-place
        // label changes. Nothing is published until both passes and epochs agree.
        internal sealed class OwnerCensus
        {
            private sealed class Storage
            {
                internal readonly RecastGraph Graph;
                internal readonly uint Index;
                internal readonly int Width, Depth;
                internal readonly NavmeshTile[]? Tiles;
                internal Stamp[]? Seen;
                internal Storage(RecastGraph graph)
                { Graph = graph; Index = graph.graphIndex; Width = graph.tileXCount; Depth = graph.tileZCount; Tiles = graph.GetTiles(); }
                internal bool Matches() => Graph.graphIndex == Index && Graph.tileXCount == Width && Graph.tileZCount == Depth && ReferenceEquals(Graph.GetTiles(), Tiles);
            }
            private struct Stamp
            {
                internal NavmeshTile? Tile;
                internal GraphNode[]? Nodes;
                internal int X, Z, W, D;
                internal Stamp(NavmeshTile? tile)
                { Tile = tile; Nodes = tile?.nodes; X = tile?.x ?? 0; Z = tile?.z ?? 0; W = tile?.w ?? 0; D = tile?.d ?? 0; }
                internal bool Matches(NavmeshTile? tile) => ReferenceEquals(Tile, tile) &&
                    (tile == null || ReferenceEquals(Nodes, tile.nodes) && X == tile.x && Z == tile.z && W == tile.w && D == tile.d);
            }
            internal readonly NavGraph[] Graphs;
            internal readonly int[] Owners = new int[MaxArea + 1];
            private readonly int[] verified = new int[MaxArea + 1];
            private readonly List<uint> labels = new List<uint>();
            private readonly Storage?[] storage;
            private readonly int maxNodes;
            private int graphIndex, tileIndex, nodeIndex, compareIndex;
            private bool checking, entered;
            internal int Nodes;
            internal bool Complete;
            internal string? Failure;
            internal OwnerCensus(NavGraph[] graphs, int maxNodes = 5000000)
            {
                Graphs = (NavGraph[])graphs.Clone(); storage = new Storage?[graphs.Length]; this.maxNodes = maxNodes;
                if (graphs.Length > 128) { Failure = "owner_index_graph_budget"; return; }
                var indices = new HashSet<uint>(); long tiles = 0;
                for (int i = 0; i < graphs.Length; i++)
                {
                    if (graphs[i] == null) continue;
                    if (graphs[i].GetType() != typeof(RecastGraph)) { Failure = "custom_or_link_graph"; return; }
                    var item = storage[i] = new Storage((RecastGraph)graphs[i]);
                    if (!indices.Add(item.Index)) { Failure = "unexpected_graph_index"; return; }
                    tiles += item.Tiles?.Length ?? 0;
                    if (tiles > 250000) { Failure = "owner_index_tile_budget"; return; }
                }
            }
            internal bool StorageMatches()
            {
                foreach (var item in storage) if (item != null && !item.Matches()) return false;
                return true;
            }
            private void Note(int[] target, uint area, uint graph)
            {
                if (area == 0) return;
                int owner = (int)graph + 1;
                if (target[area] == 0) { if (!checking) labels.Add(area); target[area] = owner; }
                else if (target[area] != owner) target[area] = -1;
            }
            // False without Failure is a cooperative budget yield. Call again
            // with a fresh budget; no graph or gameplay value has been written.
            internal bool Advance(ProofBudget budget)
            {
                if (Failure != null || Complete) return Complete;
                if (!StorageMatches()) { Failure = "owner_index_storage_changed"; return false; }
                while (graphIndex < storage.Length)
                {
                    var item = storage[graphIndex]; var tiles = item?.Tiles;
                    if (item == null || tiles == null || tileIndex >= tiles.Length)
                    { graphIndex++; tileIndex = nodeIndex = 0; entered = false; continue; }
                    if (!entered)
                    {
                        if (!budget.Take()) return false;
                        if (item.Seen == null) item.Seen = new Stamp[tiles.Length];
                        var tile = tiles[tileIndex];
                        if (!checking) item.Seen[tileIndex] = new Stamp(tile);
                        else if (!item.Seen[tileIndex].Matches(tile)) { Failure = "owner_index_tile_changed"; return false; }
                        entered = true;
                    }
                    var stamp = item.Seen![tileIndex]; var nodes = stamp.Nodes;
                    // Match native GetNodes: multi-cell tiles are visited only
                    // at their origin. Empty and duplicate slots are still checked.
                    if (stamp.Tile != null && stamp.X + stamp.Z * item.Width == tileIndex && nodes != null)
                    {
                        while (nodeIndex < nodes.Length)
                        {
                            if (!budget.Take()) return false;
                            var node = nodes[nodeIndex++];
                            if (!checking && ++Nodes > maxNodes) { Failure = "owner_index_node_budget"; return false; }
                            if (node == null || node.Destroyed || node.GraphIndex != item.Index || node.Area > MaxArea)
                            { Failure = "unexpected_tile_nodes"; return false; }
                            if (checking && node.Area != 0 && Owners[node.Area] == 0)
                            { Failure = "owner_index_labels_changed"; return false; }
                            Note(checking ? verified : Owners, node.Area, item.Index);
                        }
                    }
                    tileIndex++; nodeIndex = 0; entered = false;
                }
                if (!checking) { checking = true; graphIndex = tileIndex = nodeIndex = 0; return Advance(budget); }
                while (compareIndex < labels.Count)
                {
                    if (!budget.Take()) return false;
                    uint label = labels[compareIndex++];
                    if (Owners[label] != verified[label]) { Failure = "owner_index_labels_changed"; return false; }
                }
                Complete = true; return true;
            }
        }
        private static void CancelOwnerBuild()
        {
            if (requestedGraphs != null) Interlocked.Increment(ref OwnerRestarts);
            ownerBuild = null; requestedGraphs = null; requestedAstar = null;
        }
        // A successful native fill is the only runtime source of a rebuild
        // request. Repeated fills coalesce to the latest epoch; cooldown is not
        // reset by churn, and an unfinished/stale census is never published.
        private static void BuildOwners()
        {
            CancelOwnerBuild(); areaOwners = null; ownerGraphs = null;
            if (!Core.Config.Connectivity || !Core.Ready || faulted)
            { ownerReason = "owner_index_disabled"; return; }
            var astar = AstarPath.active;
            if (ReferenceEquals(astar, null) || astar.graphs == null) { ownerReason = "no_graphs"; return; }
            if (astar.graphs.Length > 128) { ownerReason = "owner_index_graph_budget"; ownerRetryAfter = Core.Now + OwnerRetryMs; return; }
            requestedAstar = astar; requestedGraphs = (NavGraph[])astar.graphs.Clone();
            requestedEpoch = Volatile.Read(ref fillEpoch); requestedTopology = Volatile.Read(ref topologyEpoch);
            LastOwnerCensusNodes = 0; LastOwnerCensusMs = LastOwnerMaxSliceMs = 0;
            ownerReason = "owner_index_pending"; AdvanceOwners();
        }
        private static bool OwnerRequestMatches() => requestedGraphs != null && ReferenceEquals(AstarPath.active, requestedAstar) &&
            requestedEpoch == Volatile.Read(ref fillEpoch) && requestedTopology == Volatile.Read(ref topologyEpoch) &&
            Volatile.Read(ref workerFills) == 0 && Volatile.Read(ref workerFull) == 0 && SameRegistry(requestedGraphs, requestedAstar?.graphs);
        private static void AdvanceOwners()
        {
            if (!Core.Main || requestedGraphs == null || !Core.Ready || !Core.Config.Connectivity || faulted) return;
            if (!OwnerRequestMatches()) { CancelOwnerBuild(); ownerReason = "owner_rebuild_invalidated"; return; }
            if (requestedAstar!.isScanning) { Invalidate("scan_requires_native_fill"); return; }
            if (modifierRoot.GetValue(null) != null) { CancelOwnerBuild(); ownerReason = "graph_modifier_or_node_link"; return; }
            double now = Core.Now;
            if (now < ownerRetryAfter) { ownerReason = "owner_index_cooldown"; return; }
            if (now < nextOwnerSlice) return;
            double started = now;
            try
            {
                var budget = new ProofBudget(OwnerSliceUnits, OwnerSliceMs);
                ownerBuild ??= new OwnerCensus(requestedGraphs);
                bool complete = ownerBuild.Advance(budget);
                LastOwnerCensusNodes = ownerBuild.Nodes;
                if (!OwnerRequestMatches() || !ownerBuild.StorageMatches())
                { CancelOwnerBuild(); ownerReason = "owner_rebuild_invalidated"; return; }
                if (requestedAstar!.isScanning) { Invalidate("scan_requires_native_fill"); return; }
                if (modifierRoot.GetValue(null) != null) { CancelOwnerBuild(); ownerReason = "graph_modifier_or_node_link"; return; }
                if (ownerBuild.Failure != null || LastOwnerCensusMs > 250)
                {
                    string failure = ownerBuild.Failure ?? "owner_index_time_budget";
                    if (failure.EndsWith("_budget", StringComparison.Ordinal))
                    {
                        ownerBuild = null; ownerRetryAfter = Core.Now + OwnerRetryMs;
                        LastOwnerCensusMs = 0; Interlocked.Increment(ref OwnerBudgetStops); Interlocked.Increment(ref OwnerRetries);
                        ownerReason = failure;
                    }
                    else { Invalidate(failure); ownerReason = failure; }
                    return;
                }
                if (complete)
                {
                    // No work can interleave on the main thread between this
                    // final epoch check and publishing the completed arrays.
                    ownerGraphs = requestedGraphs; ownerEpoch = requestedEpoch; areaOwners = ownerBuild.Owners;
                    ownerBuild = null; requestedGraphs = null; requestedAstar = null; ownerRetryAfter = 0;
                    ownerReason = "valid"; Interlocked.Increment(ref OwnerCensuses);
                }
                else { ownerReason = "owner_index_building"; Interlocked.Increment(ref OwnerSliceYields); }
            }
            catch { Interlocked.Increment(ref Errors); CancelOwnerBuild(); ownerReason = "owner_index_error"; ownerRetryAfter = Core.Now + OwnerRetryMs; }
            finally
            {
                double elapsed = Core.Now - started; LastOwnerCensusMs += elapsed;
                LastOwnerMaxSliceMs = Math.Max(LastOwnerMaxSliceMs, elapsed);
                Interlocked.Increment(ref OwnerSlices); nextOwnerSlice = Core.Now + OwnerSliceIntervalMs;
            }
        }
        private static bool OwnerIndexMatches()
        {
            var astar = AstarPath.active; var graphs = astar?.graphs; var expected = ownerGraphs;
            return !ReferenceEquals(astar, null) && !astar.isScanning && ownerEpoch == Volatile.Read(ref fillEpoch) &&
                Volatile.Read(ref workerFills) == 0 && Volatile.Read(ref workerFull) == 0 && areaOwners != null && graphs != null && expected != null && SameRegistry(graphs, expected);
        }
        private static bool Owned(uint area, uint graph)
        {
            var owners = areaOwners;
            return owners != null && area > 0 && area < owners.Length && owners[area] == graph + 1;
        }
        private static void Commit(Plan plan, object processor)
        {
            uint before = (uint)lastArea.GetValue(processor)!;
            var saved = plan.Writes.Select(x => new KeyValuePair<GraphNode, uint>(x.Key, x.Key.Area)).ToArray();
            try
            {
                foreach (var entry in plan.Writes) entry.Key.Area = entry.Value;
                lastArea.SetValue(processor, plan.Last);
                // Fresh labels are globally unused; retained labels were proved
                // exclusive to this graph by the previous native-fill index.
                if (areaOwners != null) foreach (var entry in plan.Writes)
                    if (entry.Value != 0) areaOwners[entry.Value] = (int)entry.Key.GraphIndex + 1;
            }
            catch
            {
                try { foreach (var entry in saved) entry.Key.Area = entry.Value; lastArea.SetValue(processor, before); }
                catch { faulted = true; }
                throw;
            }
        }
        internal static void Dispatch(object processor)
        {
            string why = reason; double start = Core.Now;
            if (Active)
            {
                SyncWorker();
                try
                {
                    if (unknown) why = reason;
                    else if (ensureDepth == 0 || processDepth == 0) why = "not_tile_work_item_fill";
                    else if (ReferenceEquals(AstarPath.active, null) || AstarPath.active.isScanning) why = "scan_or_no_astar";
                    else if (!ReferenceEquals(processorField.GetValue(AstarPath.active), processor)) why = "different_processor";
                    else if (modifierRoot.GetValue(null) != null) why = "graph_modifier_or_node_link";
                    else if (AstarPath.active.graphs == null || AstarPath.active.graphs.Any(g => g != null && g.GetType() != typeof(RecastGraph))) why = "custom_or_link_graph";
                    else if (!OwnerIndexMatches()) why = "owner_index_unavailable:" + ownerReason;
                    else if (Changes.Count == 0) why = "no_captured_tile_changes";
                    else
                    {
                        var budget = new ProofBudget();
                        bool planned = TryPlan(Changes.Select(x => new KeyValuePair<uint, Change>(x.Key.graphIndex, x.Value)),
                            (uint)lastArea.GetValue(processor)!, budget, out var plan, out why, Owned, Core.Config.OutsideConnectivityProof);
                        if (plan.OutsideProofAttempted)
                        {
                            Interlocked.Increment(ref OutsideProofAttempts);
                            if (why == "outside_proof_budget") Interlocked.Increment(ref OutsideProofBudgetStops);
                        }
                        if (planned)
                        {
                            // The bounded proof may take several milliseconds.
                            // Recheck the native-fill epoch immediately before
                            // any write, including unknown worker requests.
                            if (!Active || unknown || !OwnerIndexMatches()) why = "owner_index_invalidated_during_proof";
                            else
                            {
                                Commit(plan, processor); Interlocked.Increment(ref Scoped); Interlocked.Add(ref ProofUnits, plan.Units);
                                if (plan.OutsideProofAttempted) Interlocked.Increment(ref OutsideProofRepairs);
                                foreach (var change in Changes.Values) { Interlocked.Add(ref NewNodes, change.New.Count); Interlocked.Add(ref BoundaryNodes, change.Boundary.Count); }
                                Reset(true); Record("connectivity_scoped", Core.Now - start, "proof_units=" + plan.Units + ";writes=" + plan.Writes.Count + ";native_area_equivalence=boundary_preserved;outside_proof=" + plan.OutsideProofAttempted); return;
                            }
                        }
                    }
                }
                catch (Exception error) { Interlocked.Increment(ref Errors); why = "proof_or_commit_error"; Record("connectivity_error", Core.Now - start, why, error); }
                Interlocked.Increment(ref Fallbacks);
            }
            string? previous = fillReason; fillReason = why;
            try { nativeFill(processor); }
            finally { fillReason = previous; }
        }
        private static void FullBegin(out FillCall __state)
        {
            Interlocked.Increment(ref fillEpoch);
            if (!Core.Main) { Interlocked.Increment(ref workerFills); Interlocked.Exchange(ref workerFull, 1); }
            __state = new FillCall { Started = Core.Now, Reason = fillReason ?? "native_direct_or_scan" };
        }
        private static Exception? FullEnd(FillCall __state, Exception? __exception)
        {
            Interlocked.Increment(ref Native);
            double nativeElapsed = Core.Now - __state.Started;
            if (Core.Main)
            {
                if (__exception == null)
                { Reset(ensureDepth > 0 || processDepth == 0); BuildOwners(); }
                else Invalidate("native_full_fill_exception");
                if (nativeElapsed >= 25 || __exception != null)
                {
                    string caller = "";
                    if (Core.Now >= nextCaller) { nextCaller = Core.Now + 30000; caller = ";caller=" + new StackTrace(false).ToString().Replace('\r', ' ').Replace('\n', '|'); if (caller.Length > 2400) caller = caller.Substring(0, 2400); }
                    Record("connectivity_native", nativeElapsed, "reason=" + __state.Reason + ";owner_index=" + ownerReason + ";owner_census_ms=" + LastOwnerCensusMs + ";owner_census_nodes=" + LastOwnerCensusNodes + caller, __exception);
                }
            }
            else
            {
                Interlocked.Decrement(ref workerFills);
                Interlocked.Exchange(ref workerFull, 1);
            }
            return __exception;
        }
        internal static void Shutdown()
        {
            Changes.Clear(); CancelOwnerBuild(); areaOwners = null; ownerGraphs = null; registrySnapshot = null;
            nextOwnerSlice = ownerRetryAfter = 0;
            unknown = true; reason = "shutdown"; ownerReason = "shutdown";
        }
        private static bool SameRegistry(NavGraph[]? a, NavGraph[]? b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }
        internal static void Tick()
        {
            if (!Core.Ready) return;
            if (Core.Main)
            {
                SyncWorker();
                if (Core.Now >= nextRegistry)
                {
                    nextRegistry = Core.Now + 1000;
                    var current = AstarPath.active?.graphs;
                    // Keep an unloaded graph alive for at most the bounded
                    // registry-check interval, including an invalidated index.
                    if (registrySnapshot == null) registrySnapshot = ownerGraphs ?? (current == null ? null : (NavGraph[])current.Clone());
                    bool staleTracked = Changes.Keys.Any(g => current == null || Array.IndexOf(current, g) < 0);
                    if (staleTracked || !SameRegistry(registrySnapshot, current) || (ownerGraphs != null && !SameRegistry(ownerGraphs, current)))
                    {
                        Reset(false); Invalidate("graph_registry_changed");
                        reason = ownerReason = "graph_registry_changed";
                        registrySnapshot = current == null ? null : (NavGraph[])current.Clone();
                    }
                }
                AdvanceOwners();
            }
            if (Core.Now < nextHealth) return;
            nextHealth = Core.Now + 60000;
            Record("connectivity_health", 0, "scoped=" + Interlocked.Read(ref Scoped) + ";native=" + Interlocked.Read(ref Native) + ";fallbacks=" + Interlocked.Read(ref Fallbacks) + ";unknown_items=" + Interlocked.Read(ref UnknownItems) + ";errors=" + Interlocked.Read(ref Errors) + ";pending_graphs=" + Changes.Count + ";pending_unknown=" + unknown + ";reason=" + reason + ";circuit_disabled=" + faulted + ";owner_index=" + ownerReason + ";owner_censuses=" + Interlocked.Read(ref OwnerCensuses) + ";owner_budget_stops=" + Interlocked.Read(ref OwnerBudgetStops) + ";owner_census_ms=" + LastOwnerCensusMs + ";owner_census_nodes=" + LastOwnerCensusNodes + ";owner_index_valid=" + OwnerIndexMatches() + ";owner_rebuild_pending=" + OwnerRebuildPending +
                ";owner_slices=" + Interlocked.Read(ref OwnerSlices) + ";owner_slice_yields=" + Interlocked.Read(ref OwnerSliceYields) +
                ";owner_restarts=" + Interlocked.Read(ref OwnerRestarts) + ";owner_retries=" + Interlocked.Read(ref OwnerRetries) +
                ";owner_max_slice_ms=" + LastOwnerMaxSliceMs + ";owner_retry_ms=" + Math.Max(0, ownerRetryAfter - Core.Now) +
                ";outside_proof_enabled=" + Core.Config.OutsideConnectivityProof + ";outside_proof_attempts=" + Interlocked.Read(ref OutsideProofAttempts) +
                ";outside_proof_repairs=" + Interlocked.Read(ref OutsideProofRepairs) + ";outside_proof_budget_stops=" + Interlocked.Read(ref OutsideProofBudgetStops));
        }
    }
}
