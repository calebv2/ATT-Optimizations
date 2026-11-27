using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.Threading;
using ForestPerformance;
using HarmonyLib;
using Pathfinding;
using Pathfinding.Util;
using UnityEngine;
using Random = System.Random;

internal static class ConnectivityTests
{
    private const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static int checks, accepted, rejected, nativeObservations;
    private static bool throwNative;
    private static readonly Exception Sentinel = new InvalidOperationException("native fill sentinel");
    private static readonly Dictionary<string,int> Rejected = new Dictionary<string,int>();
    private sealed class FixtureGraph : NavGraph
    {
        internal GraphNode[] Nodes = new GraphNode[0];
        public override void GetNodes(Action<GraphNode> action) { foreach (var n in Nodes) action(n); }
        public override IEnumerable<Progress> ScanInternal() { yield break; }
        public override NNInfoInternal GetNearest(Vector3 p, NNConstraint c, GraphNode hint) => default;
    }
    private static void Check(bool v, string message) { checks++; if (!v) throw new Exception("Connectivity: " + message); }
    private static T Raw<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));
    private static GraphNode Node(uint graph, bool walkable = true)
    {
        var n = Raw<PointNode>(); n.connections = Array.Empty<Connection>(); n.GraphIndex = graph; n.Walkable = walkable; n.Tag = 7; n.Penalty = 19; return n;
    }
    private static TriangleMeshNode Tri(uint graph)
    {
        var n = Raw<TriangleMeshNode>(); n.connections = Array.Empty<Connection>(); n.GraphIndex = graph; n.Walkable = true; n.Tag = 5; n.Penalty = 11; return n;
    }
    private static void Link(GraphNode a, GraphNode b) { a.AddConnection(b, 1); b.AddConnection(a, 1); }
    private static void Unlink(GraphNode a, GraphNode b) { a.RemoveConnection(b); b.RemoveConnection(a); }
    private static object Processor(AstarPath astar)
    {
        Type t = typeof(AstarPath).Assembly.GetType("Pathfinding.GraphUpdateProcessor", true)!;
        return Activator.CreateInstance(t, F, null, new object[] { astar }, null)!;
    }
    private static void Full(object processor) => processor.GetType().GetMethod("FloodFill", F, null, Type.EmptyTypes, null)!.Invoke(processor, null);
    private static AstarPath Astar(params NavGraph[] graphs)
    {
        var a = Raw<AstarPath>(); a.data = new AstarData { graphs = graphs }; return a;
    }
    private static uint Last(object processor) => (uint)processor.GetType().GetField("lastUniqueAreaIndex", F)!.GetValue(processor)!;
    private static void Equivalent(GraphNode[] nodes, uint[] ours)
    {
        for (int i = 0; i < nodes.Length; i++)
        {
            Check((ours[i] == 0) == (nodes[i].Area == 0), "zero/unwalkable region differs from native");
            Check(nodes[i].Tag == 7 && nodes[i].Penalty == 19, "region writes altered tags or penalties");
            for (int j = 0; j < nodes.Length; j++) Check((ours[i] == ours[j]) == (nodes[i].Area == nodes[j].Area), "connected-component partition differs from native");
        }
    }
    private static void OneCase(Random random, bool forceIsland = false)
    {
        var before = Enumerable.Range(0, 9).Select(_ => Node(0)).ToArray();
        for (int i = 0; i < before.Length; i++) for (int j = i + 1; j < before.Length; j++) if (random.Next(4) == 0) Link(before[i], before[j]);
        var other = new[] { Node(1), Node(1), Node(1) }; Link(other[0], other[1]);
        var main = new FixtureGraph { graphIndex = 0, Nodes = before };
        var separate = new FixtureGraph { graphIndex = 1, Nodes = other };
        var astar = Astar(main, separate); object processor = Processor(astar); Full(processor);
        var removed = new HashSet<GraphNode>(before.Take(3));
        var change = new Connectivity.Change();
        foreach (var old in removed) old.GetConnections(n => { if (!removed.Contains(n)) change.Boundary.Add(n); });
        foreach (var old in removed) old.ClearConnections(true);
        var added = Enumerable.Range(0, random.Next(1, 5)).Select(_ => Node(0)).ToArray();
        for (int i = 0; i < added.Length; i++)
        {
            change.New.Add(added[i]);
            for (int j = i + 1; j < added.Length; j++) if (random.Next(3) == 0) Link(added[i], added[j]);
            if (!forceIsland) foreach (var outside in before.Skip(3)) if (random.Next(4) == 0) Link(added[i], outside);
        }
        if (!forceIsland && random.Next(3) == 0)
        {
            for (int i = 1; i < added.Length; i++) Link(added[0], added[i]);
            foreach (GraphNode edge in change.Boundary) Link(added[0], edge);
        }
        main.Nodes = before.Skip(3).Concat(added).ToArray(); var all = main.Nodes.Concat(other).ToArray();
        uint[] untouched = all.Select(n => n.Area).ToArray(); uint last = Last(processor);
        bool result = Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, last, new Connectivity.ProofBudget(20000, 0), out var plan, out string why);
        Check(untouched.SequenceEqual(all.Select(n => n.Area)), "preflight mutated graph before commit");
        Check(Last(processor) == last, "preflight mutated global allocator");
        if (result)
        {
            accepted++; foreach (var write in plan.Writes) write.Key.Area = write.Value;
            var ours = all.Select(n => n.Area).ToArray(); Full(processor); Equivalent(all, ours);
        }
        else { rejected++; Rejected[why] = Rejected.TryGetValue(why, out var n) ? n + 1 : 1; }
    }
    private static void RejectionsAndAllocation()
    {
        var boundary = Node(0); boundary.Area = 4; var a = Node(0); var b = Node(0); Link(a, boundary); Link(b, boundary);
        var change = new Connectivity.Change(); change.New.UnionWith(new[] { a, b }); change.Boundary.Add(boundary);
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1000, 0), out _, out _), "split/ambiguous outside connection accepted");
        Unlink(b, boundary); Link(a, b);
        Check(Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1000, 0), out var p, out _), "unchanged replacement bridge rejected");
        Check(p.Writes.All(x => x.Value == 4), "unchanged area was needlessly relabelled");
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1, 0), out _, out var why) && why == "proof_budget", "proof budget did not preserve native fallback");
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1000, 0), out _, out why, (area, graph) => false) && why == "area_shared_or_owner_unknown", "shared/incoming-only cross-graph area accepted");
        var foreign = Node(1); foreign.Area = 7; Link(a, foreign);
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 7, new Connectivity.ProofBudget(1000, 0), out _, out _), "cross-graph outgoing edge accepted"); Unlink(a, foreign);
        var other = Node(0); other.Area = 8; Link(b, other);
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 8, new Connectivity.ProofBudget(1000, 0), out _, out why) && why == "old_areas_merge", "old areas merge accepted"); Unlink(b, other);
        boundary.Walkable = false;
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1000, 0), out _, out _), "unwalkable connected boundary accepted"); boundary.Walkable = true;
        a.RemoveConnection(boundary);
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1000, 0), out _, out _), "lost old boundary accepted"); Link(a, boundary);
        a.RemoveConnection(boundary); // incoming-only old boundary edge must not be treated as symmetric.
        b.AddConnection(boundary, 1);
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) }, 4, new Connectivity.ProofBudget(1000, 0), out _, out _), "asymmetric edge accepted");
        var island = Node(0); var isolated = new Connectivity.Change(); isolated.New.Add(island);
        Check(!Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, isolated) }, Connectivity.MaxArea, new Connectivity.ProofBudget(1000, 0), out _, out why) && why == "area_id_exhaustion", "area overflow accepted");
        Check(Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, isolated) }, 10, new Connectivity.ProofBudget(1000, 0), out p, out _) && p.Last == 11 && p.Writes.Single().Value == 11, "new island did not receive globally fresh label");
        island.Walkable = false;
        Check(Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, isolated) }, 10, new Connectivity.ProofBudget(1000, 0), out p, out _) && p.Writes.Single().Value == 0, "isolated unwalkable node changed semantics");
        var dead = Node(0); typeof(GraphNode).GetField("nodeIndex", F)!.SetValue(dead, -1); isolated.New.Add(dead); isolated.Boundary.Add(dead);
        Check(Connectivity.TryPlan(new[] { new KeyValuePair<uint, Connectivity.Change>(0, isolated) }, 10, new Connectivity.ProofBudget(1000, 0), out p, out _) && p.Writes.All(x => !ReferenceEquals(x.Key, dead)), "superseded recut node retained");
    }
    private static void OutsideProof()
    {
        TriangleMeshNode Create(uint graph = 0)
        {
            var node = Tri(graph);
            node.Tag = 7; node.Penalty = 19;
            return node;
        }
        var old = Create();
        var left = Create();
        var middle = Create();
        var right = Create();
        Link(old, left); Link(old, right);
        Link(left, middle); Link(middle, right);
        var graph = new FixtureGraph { graphIndex = 0, Nodes = new GraphNode[] { old, left, middle, right } };
        object processor = Processor(Astar(graph));
        Full(processor);
        old.ClearConnections(true);
        var a = Create();
        var b = Create();
        Link(a, left); Link(b, right);
        // New nodes may have stale nonzero labels left over from recutting.
        a.Area = 91; b.Area = 92;
        graph.Nodes = new GraphNode[] { left, middle, right, a, b };
        var change = new Connectivity.Change();
        change.New.UnionWith(new[] { a, b });
        change.Boundary.UnionWith(new[] { left, right });
        var changes = new[] { new KeyValuePair<uint, Connectivity.Change>(0, change) };
        uint area = left.Area;
        uint last = Last(processor);
        bool Owner(uint label, uint graphIndex) => label == area && graphIndex == 0;
        bool Try(int units, out Connectivity.Plan plan, out string why, bool enabled = true, uint? allocator = null, Func<uint, uint, bool>? owns = null)
            => Connectivity.TryPlan(changes, allocator ?? last, new Connectivity.ProofBudget(units, 0), out plan, out why, owns ?? Owner, enabled);

        var before = graph.Nodes.Select(n => n.Area).ToArray();
        Check(!Try(1000, out var off, out var why, enabled: false) && !off.OutsideProofAttempted && why == "possible_old_area_split",
            "opt-out did not preserve full-fill fallback");
        Check(Try(1000, out var plan, out why) && plan.OutsideProofAttempted, "surviving exterior route was not proven: " + why);
        Check(before.SequenceEqual(graph.Nodes.Select(n => n.Area)) && Last(processor) == last,
            "outside proof changed graph metadata during preflight");
        foreach (var write in plan.Writes) write.Key.Area = write.Value;
        var local = graph.Nodes.Select(n => n.Area).ToArray();
        Full(processor);
        Equivalent(graph.Nodes, local);

        Check(!Try(plan.Units - 1, out _, out why) && why == "outside_proof_budget",
            "outside walk exceeded shared proof units");
        Check(!Connectivity.TryPlan(changes, last, new Connectivity.ProofBudget(1000, double.Epsilon),
            out _, out why, Owner, true), "elapsed time budget did not preserve fallback");
        Check(!Try(1000, out _, out _, allocator: Connectivity.MaxArea), "reused overflow region label accepted");
        Check(!Try(1000, out _, out why, owns: (_, _) => false) && why == "area_shared_or_owner_unknown", "unowned label accepted");
        middle.Walkable = false;
        Check(!Try(1000, out _, out why) && why == "outside_proof_unsupported_edge", "unwalkable outside route accepted");
        middle.Walkable = true;
        middle.RemoveConnection(left);
        Check(!Try(1000, out _, out why) && why == "outside_proof_unsupported_edge", "one-way outside route accepted");
        Link(middle, left);
        var foreign = Create(1);
        Link(middle, foreign);
        Check(!Try(1000, out _, out why) && why == "outside_proof_unsupported_edge", "cross-graph outside link accepted");
        Unlink(middle, foreign);
        var unsupported = Node(0); unsupported.Area = area;
        Link(middle, unsupported);
        Check(!Try(1000, out _, out why) && why == "outside_proof_unsupported_edge", "custom outside node accepted");
        Unlink(middle, unsupported);
        // Put both route edges after a high-degree node's branch edges. A
        // reverse-match scan must stop at the same budget as the forward walk.
        Unlink(middle, left); Unlink(middle, right);
        var branches = Enumerable.Range(0, 128).Select(_ => Create()).ToArray();
        foreach (var branch in branches) { branch.Area = area; Link(middle, branch); }
        Link(middle, left); Link(middle, right);
        Check(!Try(plan.Units + 10, out _, out why) && why == "outside_proof_budget",
            "dense reciprocal edge lookup exceeded the work limit");
        foreach (var branch in branches) Unlink(middle, branch);
        Unlink(middle, right);
        Check(!Try(1000, out _, out why) && why == "outside_proof_disconnected", "actual split accepted");
        Check(graph.Nodes.All(n => n.Area == area), "failed outside proof changed graph metadata");
        Full(processor);
        Check(left.Area == a.Area && right.Area == b.Area && left.Area != right.Area, "native oracle did not split disconnected routes");
    }
    private static RecastGraph Recast(uint index, params TriangleMeshNode[] nodes)
    {
        var graph = Raw<RecastGraph>(); graph.graphIndex = index; graph.tileXCount = 1; graph.tileZCount = 1;
        typeof(NavmeshBase).GetField("tiles", F)!.SetValue(graph, new[] { new NavmeshTile { x = 0, z = 0, w = 1, d = 1, nodes = nodes } }); return graph;
    }
    private static RecastGraph? replacementGraph;
    private static TriangleMeshNode[]? replacementNodes;
    private static bool ReplaceNative(NavmeshBase __instance)
    {
        if (!ReferenceEquals(__instance, replacementGraph)) return true;
        foreach (var tile in __instance.GetTiles().Take(1)) if (tile?.nodes != null) foreach (var node in tile.nodes)
        { node.ClearConnections(true); typeof(GraphNode).GetField("nodeIndex", F)!.SetValue(node, -1); }
        var tiles = (NavmeshTile[])__instance.GetTiles().Clone(); tiles[0] = new NavmeshTile { x = 0, z = 0, w = 1, d = 1, nodes = replacementNodes };
        typeof(NavmeshBase).GetField("tiles", F)!.SetValue(__instance, tiles);
        return false;
    }
    private static bool SkipModifiers() => false;
    private static void ObserveNative() { nativeObservations++; if (throwNative) throw Sentinel; }
    private static void ChangeEpoch()
    {
        var field = typeof(Connectivity).GetField("fillEpoch", F)!;
        field.SetValue(null, (int)field.GetValue(null)! + 1);
    }
    private static void RuntimeFlow()
    {
        Connectivity.Bind();
        var ours = new HarmonyLib.Harmony("ATT.ForestPerformance.ConnectivityFixture");
        var observer = new HarmonyLib.Harmony("ATT.ForestPerformance.ConnectivityIndependentObserver");
        var fakeNative = new HarmonyLib.Harmony("ATT.ForestPerformance.ConnectivityNativeIsolation");
        bool ready = Core.Ready; int thread = Core.MainThread; Settings config = Core.Config;
        AstarPath? previous = AstarPath.active;
        try
        {
            // Use real native Recast storage, full-fill implementation, native
            // TileHandler callback, WorkItemProcessor and Astar.FloodFill. Only
            // tile geometry replacement and Unity modifier dispatch are isolated.
            Core.MainThread = Thread.CurrentThread.ManagedThreadId; Core.Config = new Settings(); Core.Ready = true;
            var old = Tri(0); var external = Tri(0); var otherBoundary = Tri(0);
            Link(old, external); Link(old, otherBoundary); Link(external, otherBoundary);
            var main = Recast(0, old); var outside = Recast(1, Tri(1));
            // Keep the external old-region node in another tile of the same graph.
            main.tileXCount = 2;
            typeof(NavmeshBase).GetField("tiles", F)!.SetValue(main, new[] { new NavmeshTile { x = 0, z = 0, w = 1, d = 1, nodes = new[] { old } }, new NavmeshTile { x = 1, z = 0, w = 1, d = 1, nodes = new[] { external, otherBoundary } } });
            var astar = Astar(main, outside); AstarPath.active = astar;
            object processor = Processor(astar); typeof(AstarPath).GetField("graphUpdates", F)!.SetValue(astar, processor);
            Type wt = typeof(AstarPath).Assembly.GetType("Pathfinding.WorkItemProcessor", true)!;
            object work = Activator.CreateInstance(wt, F, null, new object[] { astar }, null)!;
            typeof(AstarPath).GetField("workItems", F)!.SetValue(astar, work);
            var fill = processor.GetType().GetMethod("FloodFill", F, null, Type.EmptyTypes, null)!;
            observer.Patch(fill, prefix: new HarmonyMethod(typeof(ConnectivityTests), nameof(ObserveNative)));
            Connectivity.Attach(ours);
            astar.FloodFill(); Check(Connectivity.OwnerCensuses > 0, "real native full fill failed to build owner index");
            var known = (HashSet<MethodInfo>)typeof(Connectivity).GetField("Known", F)!.GetValue(null)!;
            MethodInfo callback = known.Single(m => m.Name.StartsWith("<ClearTile>", StringComparison.Ordinal));
            object closure = FormatterServices.GetUninitializedObject(callback.DeclaringType!);
            object handler = FormatterServices.GetUninitializedObject(typeof(TileHandler));
            typeof(TileHandler).GetField("graph", F)!.SetValue(handler, main);
            typeof(TileHandler).GetField("tileXCount", F)!.SetValue(handler, 2);
            typeof(TileHandler).GetField("tileZCount", F)!.SetValue(handler, 1);
            typeof(TileHandler).GetField("activeTileTypes", F)!.SetValue(handler, new TileHandler.TileType[2]);
            foreach (FieldInfo field in callback.DeclaringType!.GetFields(F))
            { if (field.FieldType == typeof(TileHandler)) field.SetValue(closure, handler); else if (field.FieldType == typeof(int)) field.SetValue(closure, 0); }
            var d = (Func<IWorkItemContext, bool, bool>)Delegate.CreateDelegate(typeof(Func<IWorkItemContext, bool, bool>), closure, callback);
            var item = new AstarWorkItem(d);
            Check((bool)typeof(Connectivity).GetMethod("KnownItem", F)!.Invoke(null, new object[] { item })!, "patched native delegate was not recognized by provenance");
            var fresh = Tri(0); Link(fresh, external); Link(fresh, otherBoundary);
            replacementGraph = main; replacementNodes = new[] { fresh };
            var replace = typeof(NavmeshBase).GetMethod("ReplaceTile", new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(Int3[]), typeof(int[]) })!;
            fakeNative.Patch(replace, prefix: new HarmonyMethod(typeof(ConnectivityTests), nameof(ReplaceNative)) { priority = Priority.Last });
            fakeNative.Patch(typeof(GraphModifier).GetMethod("TriggerEvent", F)!, prefix: new HarmonyMethod(typeof(ConnectivityTests), nameof(SkipModifiers)));
            long scoped = Connectivity.Scoped; int fullBefore = nativeObservations;
            wt.GetMethod("AddWorkItem", F)!.Invoke(work, new object[] { item });
            Check((bool)wt.GetMethod("ProcessWorkItems", F)!.Invoke(work, new object[] { true })!, "native work item did not complete");
            Check(Connectivity.Scoped == scoped + 1, "actual capture/process/fill flow did not take scoped path");
            Check(nativeObservations == fullBefore, "scoped flow still ran native global relabel");
            Check(fresh.Area != 0 && fresh.Area == external.Area, "runtime scoped label lost existing connectivity");
            Check(!(bool)wt.GetField("queuedWorkItemFloodFill", F)!.GetValue(work)!, "native OnFloodFill bookkeeping was skipped");
            Core.Config.OutsideConnectivityProof = true;
            var splitA = Tri(0); var splitB = Tri(0);
            Link(splitA, external); Link(splitB, otherBoundary);
            replacementNodes = new[] { splitA, splitB };
            long outsideProofs = Connectivity.OutsideProofRepairs;
            wt.GetMethod("AddWorkItem", F)!.Invoke(work, new object[] { item });
            Check((bool)wt.GetMethod("ProcessWorkItems", F)!.Invoke(work, new object[] { true })!, "native outside-proof work item did not complete");
            Check(Connectivity.OutsideProofRepairs == outsideProofs + 1 && nativeObservations == fullBefore,
                "actual outside proof did not avoid native full fill");
            Check(splitA.Area == external.Area && splitB.Area == external.Area, "outside proof changed connectivity labels");
            Check(!(bool)wt.GetField("queuedWorkItemFloodFill", F)!.GetValue(work)!, "outside proof skipped native fill bookkeeping");
            var proofMethod = typeof(Connectivity).GetMethod("OutsideContactsConnected", F)!;
            fakeNative.Patch(proofMethod, prefix: new HarmonyMethod(typeof(ConnectivityTests), nameof(ChangeEpoch)));
            try
            {
                var nextA = Tri(0); var nextB = Tri(0);
                Link(nextA, external); Link(nextB, otherBoundary);
                replacementNodes = new[] { nextA, nextB };
                wt.GetMethod("AddWorkItem", F)!.Invoke(work, new object[] { item });
                wt.GetMethod("ProcessWorkItems", F)!.Invoke(work, new object[] { true });
                Check(Connectivity.OutsideProofRepairs == outsideProofs + 1 && nativeObservations == fullBefore + 1,
                    "epoch invalidation during outside proof committed a stale plan");
                Check(nextA.Area == external.Area && nextB.Area == external.Area, "native fallback lost connectivity");
                fresh = nextA;
            }
            finally { fakeNative.Unpatch(proofMethod, HarmonyPatchType.Prefix, fakeNative.Id); }
            // Unknown mutation has to run the native full fill even with a valid index.
            var unknownItem = new AstarWorkItem((IWorkItemContext context, bool force) => { context.QueueFloodFill(); return true; });
            wt.GetMethod("AddWorkItem", F)!.Invoke(work, new object[] { unknownItem });
            fullBefore = nativeObservations;
            wt.GetMethod("ProcessWorkItems", F)!.Invoke(work, new object[] { true });
            Check(nativeObservations == fullBefore + 1, "unknown work item skipped necessary native fill");
            DrainOwners(); WorkerInvalidation();
            throwNative = true; bool originalException = false;
            try { astar.FloodFill(); } catch (Exception error) { originalException = ReferenceEquals(error, Sentinel); }
            finally { throwNative = false; }
            Check(originalException, "forwarded native full-fill exception identity changed");
            ours.UnpatchSelf(); astar.FloodFill();
            Check(HarmonyLib.Harmony.GetPatchInfo(fill).Owners.Contains(observer.Id), "rollback removed independent observer");
            bool invalid = false; try { Connectivity.ReplaceFill(new[] { new CodeInstruction(OpCodes.Ret) }).ToArray(); } catch (NotSupportedException) { invalid = true; }
            Check(invalid, "changed native fill IL accepted");
            var areas = new[] { fresh.Area, external.Area };
            typeof(Connectivity).GetField("ownerGraphs", F)!.SetValue(null, new NavGraph[] { main, outside });
            typeof(Connectivity).GetField("registrySnapshot", F)!.SetValue(null, new NavGraph[] { main, outside });
            typeof(Connectivity).GetField("areaOwners", F)!.SetValue(null, new int[Connectivity.MaxArea + 1]);
            typeof(Connectivity).GetField("nextRegistry", F)!.SetValue(null, 0d);
            astar.data.graphs = new NavGraph[] { main };
            Connectivity.Tick();
            Check(typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null) == null && typeof(Connectivity).GetField("ownerGraphs", F)!.GetValue(null) == null, "unloaded graph signature retained index references");
            Check(areas.SequenceEqual(new[] { fresh.Area, external.Area }), "registry bookkeeping changed gameplay area labels");
            Connectivity.Shutdown();
            Check(typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null) == null && typeof(Connectivity).GetField("registrySnapshot", F)!.GetValue(null) == null, "shutdown retained index or registry references");
        }
        finally
        {
            ours.UnpatchSelf(); observer.UnpatchSelf(); fakeNative.UnpatchSelf(); replacementGraph = null; replacementNodes = null;
            AstarPath.active = previous; Core.Ready = ready; Core.MainThread = thread; Core.Config = config;
        }
    }
    private static object? GetOwner(string name) => typeof(Connectivity).GetField(name, F)!.GetValue(null);
    private static void SetOwner(string name, object? value) => typeof(Connectivity).GetField(name, F)!.SetValue(null, value);
    private static void OwnerCall(string name) => typeof(Connectivity).GetMethod(name, F)!.Invoke(null, null);
    private static void DrainOwners()
    {
        for (int i = 0; i < 2000 && Connectivity.OwnerRebuildPending; i++)
        { SetOwner("nextOwnerSlice", 0d); OwnerCall("AdvanceOwners"); }
        Check(!Connectivity.OwnerRebuildPending, "bounded ownership cursor failed to finish in a stable fixture");
    }
    private static void StartOwnerScan() => typeof(AstarPath).GetProperty("isScanning", F)!.SetValue(AstarPath.active, true, null);
    private static void OwnerCursor()
    {
        var a = Tri(0); a.Area = 7; var b = Tri(1); b.Area = 7; var c = Tri(1); c.Area = 9;
        var first = Recast(0, a); var second = Recast(1, b, c);
        // A native multi-cell tile is duplicated in backing storage, but its
        // nodes count only at the origin. Null slots must also be validated.
        first.tileXCount = 3;
        var wide = new NavmeshTile { x = 0, z = 0, w = 2, d = 1, nodes = new[] { a } };
        typeof(NavmeshBase).GetField("tiles", F)!.SetValue(first, new[] { wide, wide, null });
        var cursor = new Connectivity.OwnerCensus(new NavGraph[] { first, null!, second });
        int steps = 0;
        while (!cursor.Complete && cursor.Failure == null && steps++ < 100)
            cursor.Advance(new Connectivity.ProofBudget(3, 0));
        Check(cursor.Complete && cursor.Nodes == 3 && steps > 1, "sliced census lost origin nodes or failed to yield");
        Check(cursor.Owners[7] == -1 && cursor.Owners[9] == 2 && cursor.Owners[0] == 0, "shared/unique area ownership changed");
        Check(a.Area == 7 && b.Area == 7 && c.Area == 9 && a.Tag == 5 && a.Penalty == 11, "owner census changed gameplay metadata");

        var graph = Recast(0, Tri(0), Tri(0)); graph.GetTiles()[0].nodes[0].Area = 1; graph.GetTiles()[0].nodes[1].Area = 1;
        Connectivity.OwnerCensus Partial()
        {
            var value = new Connectivity.OwnerCensus(new NavGraph[] { graph });
            Check(!value.Advance(new Connectivity.ProofBudget(2, 0)), "partial fixture unexpectedly completed"); return value;
        }
        void Finish(Connectivity.OwnerCensus value)
        { for (int i = 0; i < 100 && !value.Complete && value.Failure == null; i++) value.Advance(new Connectivity.ProofBudget(3, 0)); }
        var stale = Partial(); var oldTiles = graph.GetTiles();
        typeof(NavmeshBase).GetField("tiles", F)!.SetValue(graph, oldTiles.Clone()); Finish(stale);
        Check(stale.Failure == "owner_index_storage_changed" && !stale.Complete, "replaced tile storage was published");
        stale = Partial(); var oldTile = graph.GetTiles()[0];
        graph.GetTiles()[0] = new NavmeshTile { x = 0, z = 0, w = 1, d = 1, nodes = oldTile.nodes }; Finish(stale);
        Check(stale.Failure == "owner_index_tile_changed", "in-place tile replacement was published");
        stale = Partial(); graph.GetTiles()[0].nodes = (TriangleMeshNode[])graph.GetTiles()[0].nodes.Clone(); Finish(stale);
        Check(stale.Failure == "owner_index_tile_changed", "replaced node array was published");
        var left = Tri(0); left.Area = 1; var right = Tri(1); right.Area = 2;
        stale = new Connectivity.OwnerCensus(new NavGraph[] { Recast(0, left), Recast(1, right) });
        Check(!stale.Advance(new Connectivity.ProofBudget(4, 0)), "validation fixture did not pause between passes");
        right.Area = 1; Finish(stale);
        Check(stale.Failure == "owner_index_labels_changed" && !stale.Complete, "in-place area ownership change was published");
        Check(left.Area == 1 && right.Area == 1, "rejected validation restored or changed native labels");
        stale = new Connectivity.OwnerCensus(new NavGraph[] { graph }, 1); Finish(stale);
        Check(stale.Failure == "owner_index_node_budget", "node limit failed to stop a census");
        stale = new Connectivity.OwnerCensus(new NavGraph[] { new FixtureGraph() });
        Check(stale.Failure == "custom_or_link_graph", "custom graph indexing was accepted");
    }
    private static void OwnerRebuildLifecycle()
    {
        bool ready = Core.Ready; int thread = Core.MainThread; Settings config = Core.Config; var previous = AstarPath.active;
        var invalidator = new HarmonyLib.Harmony("ATT.ForestPerformance.OwnerRebuildEpochFixture");
        try
        {
            Core.Ready = true; Core.MainThread = Thread.CurrentThread.ManagedThreadId; Core.Config = new Settings();
            Connectivity.Shutdown();
            var nodes = new[] { Tri(0), Tri(0) }; foreach (var n in nodes) n.Area = 1;
            var graph = Recast(0, nodes); var astar = Astar(graph); AstarPath.active = astar;
            // A tiny census can publish synchronously through the retained
            // BuildOwners entry; larger ones advance on subsequent main ticks.
            OwnerCall("BuildOwners"); DrainOwners();
            Check((bool)typeof(Connectivity).GetMethod("OwnerIndexMatches", F)!.Invoke(null, null)!, "stable owner request did not publish");
            var advance = typeof(Connectivity.OwnerCensus).GetMethod("Advance", F)!;
            invalidator.Patch(advance, postfix: new HarmonyMethod(typeof(ConnectivityTests), nameof(ChangeEpoch)));
            SetOwner("nextOwnerSlice", 0d); OwnerCall("BuildOwners");
            Check(GetOwner("areaOwners") == null && !Connectivity.OwnerRebuildPending, "epoch changed during the final slice but stale ownership was published");
            invalidator.UnpatchSelf();
            invalidator.Patch(advance, postfix: new HarmonyMethod(typeof(ConnectivityTests), nameof(StartOwnerScan)));
            SetOwner("nextOwnerSlice", 0d); OwnerCall("BuildOwners");
            Check(GetOwner("areaOwners") == null && !Connectivity.OwnerRebuildPending, "scan began during publication but ownership was published");
            invalidator.UnpatchSelf(); typeof(AstarPath).GetProperty("isScanning", F)!.SetValue(astar, false, null);
            // Four failures must not reproduce the old permanent three-strike
            // circuit. A budget abort keeps a request for a cooled retry.
            for (int attempt = 0; attempt < 4; attempt++)
            {
                SetOwner("nextOwnerSlice", double.MaxValue); SetOwner("ownerRetryAfter", 0d); OwnerCall("BuildOwners");
                SetOwner("ownerBuild", new Connectivity.OwnerCensus(astar.graphs, 1)); SetOwner("nextOwnerSlice", 0d);
                long retries = Connectivity.OwnerRetries; OwnerCall("AdvanceOwners");
                Check(Connectivity.OwnerRetries == retries + 1 && Connectivity.OwnerRebuildPending && GetOwner("areaOwners") == null,
                    "budget abort did not retain a safe retry");
                long slices = Connectivity.OwnerSlices; OwnerCall("AdvanceOwners");
                Check(Connectivity.OwnerSlices == slices, "cooldown retried immediately");
            }
            SetOwner("ownerRetryAfter", 0d); DrainOwners();
            Check(GetOwner("areaOwners") != null, "repeated budget failures disabled indexing for the session");
            // Known runtime churn discards progress and uses the latest native
            // epoch; it must be able to recover when a quiet window arrives.
            long restarts = Connectivity.OwnerRestarts;
            for (int i = 0; i < 4; i++) { SetOwner("nextOwnerSlice", double.MaxValue); ChangeEpoch(); OwnerCall("BuildOwners"); }
            Check(Connectivity.OwnerRestarts >= restarts + 3 && GetOwner("areaOwners") == null, "native-fill churn retained partial ownership");
            DrainOwners(); Check(GetOwner("areaOwners") != null, "latest native epoch never recovered after churn");
            SetOwner("nextOwnerSlice", double.MaxValue); OwnerCall("BuildOwners");
            typeof(Connectivity).GetMethod("Reset", F)!.Invoke(null, new object[] { true });
            SetOwner("knownDepth", 1); SetOwner("processDepth", 1);
            try
            {
                object?[] args = { graph, 0, 0, 1, 1, default(Connectivity.TileCall) };
                typeof(Connectivity).GetMethod("TileBegin", F)!.Invoke(null, args);
                Check(!Connectivity.OwnerRebuildPending && GetOwner("areaOwners") == null && !(bool)GetOwner("unknown")!,
                    "known recut retained a mixed census or unnecessarily marked provenance unknown");
            }
            finally { SetOwner("knownDepth", 0); SetOwner("processDepth", 0); typeof(Connectivity).GetMethod("Reset", F)!.Invoke(null, new object[] { true }); }
            SetOwner("nextOwnerSlice", double.MaxValue); OwnerCall("BuildOwners");
            typeof(Connectivity).GetMethod("Invalidate", F)!.Invoke(null, new object[] { "fixture_unknown_writer" });
            OwnerCall("AdvanceOwners");
            Check(!Connectivity.OwnerRebuildPending && GetOwner("areaOwners") == null, "unknown writer published or retried an untrusted census");
            SetOwner("nextOwnerSlice", 0d); OwnerCall("BuildOwners"); DrainOwners();
            typeof(AstarPath).GetProperty("isScanning", F)!.SetValue(astar, true, null);
            Check(!(bool)typeof(Connectivity).GetMethod("OwnerIndexMatches", F)!.Invoke(null, null)!, "scan reused a published owner index");
            OwnerCall("BuildOwners"); Check(!Connectivity.OwnerRebuildPending && GetOwner("areaOwners") == null, "scan retained a census request");
            typeof(AstarPath).GetProperty("isScanning", F)!.SetValue(astar, false, null);
            SetOwner("nextOwnerSlice", double.MaxValue); OwnerCall("BuildOwners");
            astar.data.graphs = new NavGraph[] { Recast(0, nodes) };
            SetOwner("nextOwnerSlice", 0d); OwnerCall("AdvanceOwners");
            Check(GetOwner("areaOwners") == null && !Connectivity.OwnerRebuildPending, "changed graph registry published stale ownership");
        }
        finally
        { invalidator.UnpatchSelf(); Connectivity.Shutdown(); AstarPath.active = previous; Core.Ready = ready; Core.MainThread = thread; Core.Config = config; }
    }
    private static void WorkerInvalidation()
    {
        object? before = typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null);
        Check(before != null, "worker invalidation fixture lacked a valid owner index");
        var started = new ManualResetEvent(false); var finish = new ManualResetEvent(false); Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                object?[] state = { default(Connectivity.FillCall) };
                typeof(Connectivity).GetMethod("FullBegin", F)!.Invoke(null, state);
                started.Set(); finish.WaitOne();
                typeof(Connectivity).GetMethod("FullEnd", F)!.Invoke(null, new[] { state[0], null });
            }
            catch (Exception error) { failure = error; started.Set(); }
        });
        worker.Start();
        try
        {
            Check(started.WaitOne(5000), "worker invalidation fixture did not start");
            Check(!(bool)typeof(Connectivity).GetMethod("OwnerIndexMatches", F)!.Invoke(null, null)!, "worker fill failed to invalidate ownership before mutation");
            Check(ReferenceEquals(before, typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null)), "worker wrote ownership array at start");
        }
        finally { finish.Set(); Check(worker.Join(5000), "worker invalidation fixture did not finish"); }
        Check(failure == null, "worker native fill observer failed: " + failure);
        Check(ReferenceEquals(before, typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null)), "worker rebuilt/wrote ownership array at completion");
        typeof(Connectivity).GetMethod("SyncWorker", F)!.Invoke(null, null);
        Check(typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null) == null, "main thread did not discard invalidated index");
    }
    private static void CensusScale()
    {
        bool ready = Core.Ready; int thread = Core.MainThread; AstarPath? previous = AstarPath.active;
        try
        {
            Core.Ready = true; Core.MainThread = Thread.CurrentThread.ManagedThreadId;
            const int tileCount = 19500, perTile = 32;
            var tiles = new NavmeshTile[tileCount];
            for (int i = 0; i < tiles.Length; i++)
            {
                var nodes = new TriangleMeshNode[perTile];
                for (int j = 0; j < nodes.Length; j++) { nodes[j] = Tri(0); nodes[j].Area = 1; }
                tiles[i] = new NavmeshTile { x = i, z = 0, w = 1, d = 1, nodes = nodes };
            }
            var graph = Recast(0); graph.tileXCount = tileCount;
            typeof(NavmeshBase).GetField("tiles", F)!.SetValue(graph, tiles);
            AstarPath.active = Astar(graph);
            long stops = Connectivity.OwnerBudgetStops, slices = Connectivity.OwnerSlices;
            typeof(Connectivity).GetMethod("BuildOwners", F)!.Invoke(null, null);
            bool valid = typeof(Connectivity).GetField("areaOwners", F)!.GetValue(null) != null;
            Check(valid || Connectivity.OwnerRebuildPending || Connectivity.OwnerBudgetStops == stops + 1, "large ownership census neither published nor yielded safely");
            DrainOwners(); valid = GetOwner("areaOwners") != null;
            Check(valid, "stable large census failed to publish after slices");
            if (valid)
            {
                Check(Connectivity.LastOwnerCensusNodes == tileCount * perTile, "large census lost or double-counted native node slots");
                Check((bool)typeof(Connectivity).GetMethod("Owned", F)!.Invoke(null, new object[] { (uint)1, (uint)0 })!, "large index omitted existing area owner");
            }
            else Check(Connectivity.LastOwnerCensusMs < 100, "bounded ownership census overran excessively");
            Console.WriteLine("Ownership census synthetic scale: " + tileCount + " tiles / " + tileCount * perTile + " distinct nodes; visited=" + Connectivity.LastOwnerCensusNodes + "; activeMs=" + Connectivity.LastOwnerCensusMs.ToString("0.###") + "; slices=" + (Connectivity.OwnerSlices - slices) + "; maxSliceMs=" + Connectivity.LastOwnerMaxSliceMs.ToString("0.###") + "; valid=" + valid + ". Synthetic storage traversal, not a live game speedup.");
        }
        finally { Connectivity.Shutdown(); Core.Ready = ready; Core.MainThread = thread; AstarPath.active = previous; }
    }
    internal static void Run()
    {
        RejectionsAndAllocation();
        OutsideProof();
        var random = new Random(615273);
        for (int i = 0; i < 500; i++) OneCase(random, i % 7 == 0);
        Console.WriteLine("Random accepted=" + accepted + "; rejected=" + rejected + "; reasons=" + string.Join(",",Rejected.Select(x=>x.Key+":"+x.Value)));
        Check(accepted > 10 && rejected > 10, "random suite missed accepted/fallback cases");
        RuntimeFlow(); OwnerCursor(); OwnerRebuildLifecycle(); CensusScale();
        Console.WriteLine("PASS connectivity: " + checks + " checks; " + accepted + " native-equivalent local repairs / " + rejected + " conservative fallbacks; actual tile capture/native work-item/fill flow, fresh IDs, splits/merges/cross-graph/asymmetry/unwalkable links, budget/overflow, no preflight writes, native bookkeeping and independent rollback.");
    }
}
