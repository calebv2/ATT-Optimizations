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
        private static int censusBudgetFailures;
        internal static long OwnerCensuses, OwnerBudgetStops;
        internal static double LastOwnerCensusMs;
        internal static int LastOwnerCensusNodes;
        internal static long Scoped, Native, Fallbacks, Errors, UnknownItems, ProofUnits, NewNodes, BoundaryNodes;
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
                return Units <= limit && (milliseconds <= 0 || (Units & 31) != 0 ||
                    (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency <= milliseconds);
            }
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
            areaOwners = null; ownerGraphs = null; ownerReason = "unknown_topology_writer";
        }
        private static void Reset(bool trustworthy)
        {
            Changes.Clear(); unknown = !trustworthy; reason = trustworthy ? "none" : "unknown_mutation";
        }
        private static void SyncWorker()
        {
            if (Interlocked.Exchange(ref workerFull, 0) != 0)
            { Reset(false); areaOwners = null; ownerGraphs = null; ownerReason = "worker_native_fill_invalidated"; }
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
        internal static bool TryPlan(IEnumerable<KeyValuePair<uint, Change>> changes, uint last, ProofBudget budget, out Plan plan, out string why, Func<uint, uint, bool>? ownerCheck = null)
        {
            plan = new Plan { Last = last }; why = "none";
            foreach (var pair in changes)
            {
                uint graph = pair.Key; Change change = pair.Value;
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
                            if (!other.ContainsConnection(current)) { bad = true; failure = "asymmetric_connection"; return; }
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
                foreach (GraphNode boundary in change.Boundary)
                {
                    if (!budget.Take()) { why = "proof_budget"; return false; }
                    if (boundary == null || boundary.Destroyed || nodes.Contains(boundary)) continue;
                    if (boundary.GraphIndex != graph || !boundary.Walkable || boundary.Area == 0) { why = "unsupported_old_boundary"; return false; }
                    if (ownerCheck != null && !ownerCheck(boundary.Area, graph)) { why = "area_shared_or_owner_unknown"; return false; }
                    if (!outside.TryGetValue(boundary, out int id)) { why = "old_boundary_disconnected"; return false; }
                    if (oldRegionComponent.TryGetValue(boundary.Area, out int old) && old != id) { why = "possible_old_area_split"; return false; }
                    oldRegionComponent[boundary.Area] = id;
                }
                // Also require all final external contacts of one old region to
                // join one replacement component, even across repeated recuts.
                foreach (var edge in outside)
                {
                    if (oldRegionComponent.TryGetValue(edge.Key.Area, out int id) && id != edge.Value) { why = "possible_old_area_split"; return false; }
                    oldRegionComponent[edge.Key.Area] = edge.Value;
                }
                for (int i = 0; i < labels.Count; i++)
                    if (labels[i] == 0)
                    { if (plan.Last >= MaxArea) { why = "area_id_exhaustion"; return false; } labels[i] = ++plan.Last; }
                foreach (var entry in component) plan.Writes.Add(new KeyValuePair<GraphNode, uint>(entry.Key, labels[entry.Value]));
            }
            plan.Units = budget.Units;
            return true;
        }
        // Native full relabel has already finalized every region while path
        // processing is paused. Index the final region owners to reject even
        // incoming-only cross-graph connections (which local adjacency misses).
        // Tile storage enumeration is bounded between units and adds no Unity
        // calls. Three over-budget attempts stop further indexing this session.
        private static void BuildOwners()
        {
            areaOwners = null; ownerGraphs = null;
            if (!Core.Config.Connectivity || !Core.Ready || censusBudgetFailures >= 3)
            { ownerReason = "owner_index_disabled_or_budget_circuit"; return; }
            double started = Core.Now; int epoch = Volatile.Read(ref fillEpoch);
            try
            {
                if (Volatile.Read(ref workerFills) != 0) { ownerReason = "worker_fill_in_progress"; return; }
                if (modifierRoot.GetValue(null) != null) { ownerReason = "graph_modifier_or_node_link"; return; }
                var astar = AstarPath.active;
                if (ReferenceEquals(astar, null) || astar.graphs == null) { ownerReason = "no_graphs"; return; }
                var graphs = (NavGraph[])astar.graphs.Clone(); var owners = new int[MaxArea + 1];
                int count = 0; LastOwnerCensusNodes = 0;
                foreach (NavGraph graph in graphs)
                {
                    if (graph == null) continue;
                    if (graph.GetType() != typeof(RecastGraph)) { ownerReason = "custom_or_link_graph"; return; }
                    var tiles = ((RecastGraph)graph).GetTiles() ?? new NavmeshTile[0];
                    for (int tileIndex = 0; tileIndex < tiles.Length; tileIndex++)
                    {
                        if ((tileIndex & 255) == 0 && Core.Now - started > 20)
                        { censusBudgetFailures++; Interlocked.Increment(ref OwnerBudgetStops); ownerReason = "owner_index_budget"; return; }
                        NavmeshTile tile = tiles[tileIndex];
                        // Match native GetNodes: a multi-cell tile appears in the
                        // backing array more than once and is visited at origin only.
                        if (tile == null || tile.x + tile.z * ((RecastGraph)graph).tileXCount != tileIndex || tile.nodes == null) continue;
                        foreach (GraphNode node in tile.nodes)
                        {
                            LastOwnerCensusNodes = ++count;
                            if (count > 5000000 || ((count & 255) == 0 && Core.Now - started > 20))
                            { censusBudgetFailures++; Interlocked.Increment(ref OwnerBudgetStops); ownerReason = "owner_index_budget"; return; }
                            if (node == null || node.Destroyed || node.GraphIndex != graph.graphIndex) { ownerReason = "unexpected_tile_nodes"; return; }
                            uint area = node.Area; if (area == 0) continue;
                            int owner = (int)graph.graphIndex + 1;
                            if (owners[area] == 0) owners[area] = owner;
                            else if (owners[area] != owner) owners[area] = -1;
                        }
                    }
                }
                if (epoch != Volatile.Read(ref fillEpoch) || Volatile.Read(ref workerFills) != 0)
                { ownerReason = "worker_fill_invalidated_index"; return; }
                ownerGraphs = graphs; ownerEpoch = epoch; areaOwners = owners; ownerReason = "valid";
                Interlocked.Increment(ref OwnerCensuses);
            }
            catch { Interlocked.Increment(ref Errors); ownerReason = "owner_index_error"; }
            finally { LastOwnerCensusMs = Core.Now - started; }
        }
        private static bool OwnerIndexMatches()
        {
            var graphs = AstarPath.active?.graphs; var expected = ownerGraphs;
            return ownerEpoch == Volatile.Read(ref fillEpoch) && Volatile.Read(ref workerFills) == 0 && Volatile.Read(ref workerFull) == 0 && areaOwners != null && graphs != null && expected != null && graphs.Length == expected.Length &&
                graphs.Where((g, i) => !ReferenceEquals(g, expected[i])).Any() == false;
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
                        if (TryPlan(Changes.Select(x => new KeyValuePair<uint, Change>(x.Key.graphIndex, x.Value)), (uint)lastArea.GetValue(processor)!, budget, out var plan, out why, Owned))
                        {
                            // The bounded proof may take several milliseconds.
                            // Recheck the native-fill epoch immediately before
                            // any write, including unknown worker requests.
                            if (!Active || unknown || !OwnerIndexMatches()) why = "owner_index_invalidated_during_proof";
                            else
                            {
                                Commit(plan, processor); Interlocked.Increment(ref Scoped); Interlocked.Add(ref ProofUnits, plan.Units);
                                foreach (var change in Changes.Values) { Interlocked.Add(ref NewNodes, change.New.Count); Interlocked.Add(ref BoundaryNodes, change.Boundary.Count); }
                                Reset(true); Record("connectivity_scoped", Core.Now - start, "proof_units=" + plan.Units + ";writes=" + plan.Writes.Count + ";native_area_equivalence=boundary_preserved"); return;
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
            Changes.Clear(); areaOwners = null; ownerGraphs = null; registrySnapshot = null;
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
                        Reset(false); areaOwners = null; ownerGraphs = null;
                        reason = ownerReason = "graph_registry_changed";
                        registrySnapshot = current == null ? null : (NavGraph[])current.Clone();
                    }
                }
            }
            if (Core.Now < nextHealth) return;
            nextHealth = Core.Now + 60000;
            Record("connectivity_health", 0, "scoped=" + Interlocked.Read(ref Scoped) + ";native=" + Interlocked.Read(ref Native) + ";fallbacks=" + Interlocked.Read(ref Fallbacks) + ";unknown_items=" + Interlocked.Read(ref UnknownItems) + ";errors=" + Interlocked.Read(ref Errors) + ";pending_graphs=" + Changes.Count + ";pending_unknown=" + unknown + ";reason=" + reason + ";circuit_disabled=" + faulted + ";owner_index=" + ownerReason + ";owner_censuses=" + Interlocked.Read(ref OwnerCensuses) + ";owner_budget_stops=" + Interlocked.Read(ref OwnerBudgetStops) + ";owner_census_ms=" + LastOwnerCensusMs + ";owner_census_nodes=" + LastOwnerCensusNodes + ";owner_index_valid=" + (areaOwners != null && ownerEpoch == Volatile.Read(ref fillEpoch)));
        }
    }
}
