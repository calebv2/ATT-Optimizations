using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Alta.Chunks;
using Alta.Intelligence;
using Alta.Pathfinding;
using CavePerformance;
using HarmonyLib;
using Pathfinding;
using UnityEngine;
using GamePath = Pathfinding.Path;

// This fixture runs the installed Navigator.Wander -> ConstantPath.Construct ->
// AstarPath.StartPath sequence. Only unavailable Unity instance/time accesses,
// cancellation and the final enqueue are isolated; Search.BindPath is not used.
internal static class SearchBindingTests
{
    private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class Graph : RecastGraph
    {
        internal GraphNode Node = null!;
        internal int Calls;
        internal bool Fail;
        internal static readonly Exception Sentinel = new InvalidOperationException("native nearest fixture sentinel");
        public override void GetNodes(System.Action<GraphNode> action) => action(Node);
        public override NNInfoInternal GetNearest(Vector3 position, NNConstraint constraint, GraphNode hint) => GetNearestForce(position, constraint);
        public override NNInfoInternal GetNearestForce(Vector3 position, NNConstraint constraint)
        {
            Calls++;
            if (Fail) throw Sentinel;
            return constraint.Suitable(Node) ? new NNInfoInternal(Node) { clampedPosition = (Vector3)Node.position, constrainedNode = Node, constClampedPosition = (Vector3)Node.position } : default;
        }
    }
    private static Navigator? registeredNav;
    private static GamePath? submitted;
    private static Vector3 position = new Vector3(0, -1500, 0);
    private static Vector3 writtenPosition;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T UnityObject<T>() where T : UnityEngine.Object
    {
        var value = (T)FormatterServices.GetUninitializedObject(typeof(T));
        typeof(UnityEngine.Object).GetField("m_CachedPtr", Any)!.SetValue(value, new IntPtr(1));
        return value;
    }
    private static bool Registered(ref Navigator? __result) { __result = registeredNav; return false; }
    private static bool AtPosition(ref Vector3 __result) { __result = position; return false; }
    private static Vector3 NativePosition(Component _) => position;
    private static float NativeTime() => 0;
    private static bool NativeServer() => true;
    private static Vector3 ReadTransform(Transform _) => position;
    private static void WriteTransform(Transform _, Vector3 value) => writtenPosition = value;
    private static bool Cancel() => false;
    private static bool Enqueue(GamePath __0) { submitted = __0; return false; }
    private static bool Points(ref List<Vector3> __result)
    {
        __result = new List<Vector3> { position + Vector3.right, position - Vector3.right };
        return false;
    }
    private static bool Seek(Vector3 __0, Vector3 __1, ref GamePath __result)
    {
        // Preserve native ABPath construction and the actual patched submission;
        // isolate Seeker's Unity scene and callback/lifecycle setup in this test.
        __result = ABPath.Construct(__0, __1, null);
        AstarPath.StartPath(__result);
        return false;
    }
    private static IEnumerable<CodeInstruction> ReplaceUnityReads(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
    {
        var instructions = input.ToList();
        var transform = AccessTools.PropertyGetter(typeof(Component), "transform");
        var vector = AccessTools.PropertyGetter(typeof(Transform), "position");
        var time = AccessTools.PropertyGetter(typeof(Time), "time");
        int positions = 0, times = 0;
        for (int i = 0; i < instructions.Count; i++)
        {
            if (instructions[i].Calls(transform) && i + 1 < instructions.Count && instructions[i + 1].Calls(vector))
            {
                instructions[i].opcode = OpCodes.Call; instructions[i].operand = AccessTools.Method(typeof(SearchBindingTests), nameof(NativePosition));
                instructions[i + 1].opcode = OpCodes.Nop; instructions[i + 1].operand = null; positions++;
            }
            if (instructions[i].Calls(time))
            {
                instructions[i].opcode = OpCodes.Call; instructions[i].operand = AccessTools.Method(typeof(SearchBindingTests), nameof(NativeTime)); times++;
            }
        }
        int expectedTimes = __originalMethod.DeclaringType == typeof(Navigator) ? 1 : 0;
        Check(positions == 1 && times == expectedTimes, "Installed native Wander's Unity access shape changed");
        return instructions;
    }
    private static IEnumerable<CodeInstruction> ReplaceWalkerUnityReads(IEnumerable<CodeInstruction> input)
    {
        int server = 0, read = 0, write = 0;
        foreach (var instruction in input)
        {
            if (instruction.operand is MethodInfo method)
            {
                string? replacement = null;
                if (method.Name == "get_IsServer" && method.DeclaringType?.Name == "NetworkSceneManager") { replacement = nameof(NativeServer); server++; }
                if (method == AccessTools.PropertyGetter(typeof(Transform), "position")) { replacement = nameof(ReadTransform); read++; }
                if (method == AccessTools.PropertySetter(typeof(Transform), "position")) { replacement = nameof(WriteTransform); write++; }
                if (replacement != null) { instruction.opcode = OpCodes.Call; instruction.operand = AccessTools.Method(typeof(SearchBindingTests), replacement); }
            }
            yield return instruction;
        }
        Check(server == 1 && read == 1 && write == 1, "Installed native Walker.Start shape changed");
    }
    internal static void Run()
    {
        var h = new HarmonyLib.Harmony("ATT.CavePerformance.RealSubmissionFixture");
        var agent = UnityObject<Turabada>(); var nav = UnityObject<Navigator>(); var nextNav = UnityObject<Navigator>();
        var cave = UnityObject<CaveLayerStreamer>();
        cave.Bounds = new Bounds(position, new Vector3(400, 15, 400));
        typeof(CaveLayerStreamer).GetField("layer", Any)!.SetValue(cave, 100);
        var astar = UnityObject<AstarPath>();
        astar.maxNearestNodeDistance = 100;
        astar.data = (AstarData)FormatterServices.GetUninitializedObject(typeof(AstarData));
        var graph = (Graph)FormatterServices.GetUninitializedObject(typeof(Graph)); graph.graphIndex = 100;
        var node = (PointNode)FormatterServices.GetUninitializedObject(typeof(PointNode));
        node.Walkable = true; node.Area = 1; node.GraphIndex = 100; node.position = (Int3)position; graph.Node = node;
        astar.data.graphs = new NavGraph[101]; astar.data.graphs[100] = graph;
        typeof(CaveLayerStreamer).GetField("navGraph", Any)!.SetValue(cave, graph);
        var previousAstar = AstarPath.active; AstarPath.active = astar;
        var previousCave = Loading.CurrentCave; Loading.CurrentCave = cave;
        typeof(Navigator).GetField("<Seeker>k__BackingField", Any)!.SetValue(nav, UnityObject<Seeker>());
        var wander = typeof(Navigator).GetMethod("Wander", Any, null, new[] { typeof(Vector3), typeof(int), typeof(float), typeof(OnPathDelegate) }, null)!;
        var wanderTo = typeof(Navigator).GetMethod("WanderTo", Any)!;
        try
        {
            h.Patch(AccessTools.Method(typeof(Search), nameof(Search.NavigatorFor)), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Registered)));
            h.Patch(AccessTools.Method(typeof(Search), nameof(Search.Position)), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(AtPosition)));
            h.Patch(AccessTools.Method(typeof(Search), nameof(Search.NavigatorForWalker)), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Registered)));
            h.Patch(AccessTools.Method(typeof(Search), nameof(Search.WalkerPosition)), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(AtPosition)));
            h.Patch(typeof(Navigator).GetMethod("CancelRequest", Any), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Cancel)));
            // Patch the tiny Seeker forwarding overload before the native
            // callback is detoured/JIT-compiled, to prevent test-host inlining.
            h.Patch(typeof(Seeker).GetMethod("StartPath", new[] { typeof(Vector3), typeof(Vector3) }), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Seek)));
            h.Patch(typeof(Seeker).GetMethod("StartPath", new[] { typeof(Vector3), typeof(Vector3), typeof(OnPathDelegate) }), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Seek)));
            h.Patch(typeof(PathUtilities).GetMethod("GetPointsOnNodes", new[] { typeof(List<GraphNode>), typeof(int), typeof(float) }), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Points)));
            h.Patch(wander, prefix: new HarmonyMethod(typeof(Search), "WanderBegin"), finalizer: new HarmonyMethod(typeof(Search), "SubmissionEnd"), transpiler: new HarmonyMethod(typeof(SearchBindingTests), nameof(ReplaceUnityReads)));
            h.Patch(wanderTo, prefix: new HarmonyMethod(typeof(Search), "WanderResultBegin"), finalizer: new HarmonyMethod(typeof(Search), "SubmissionEnd"));
            h.Patch(Search.DirectionalCallback, prefix: new HarmonyMethod(typeof(Search), "DirectionalResultBegin"), finalizer: new HarmonyMethod(typeof(Search), "SubmissionEnd"), transpiler: new HarmonyMethod(typeof(SearchBindingTests), nameof(ReplaceUnityReads)));
            h.Patch(typeof(AstarPath).GetMethod("StartPath", Any), prefix: new HarmonyMethod(typeof(Search), "Submit"));
            h.Patch(typeof(AstarPath).GetMethod("StartPath", Any), prefix: new HarmonyMethod(typeof(SearchBindingTests), nameof(Enqueue)));
            h.Patch(Search.Nearest, prefix: new HarmonyMethod(typeof(Search), nameof(Search.QueryBegin)), postfix: new HarmonyMethod(typeof(Search), nameof(Search.QueryResult)), finalizer: new HarmonyMethod(typeof(Search), nameof(Search.QueryEnd)));
            h.Patch(Search.Suitable, prefix: new HarmonyMethod(typeof(Search), nameof(Search.Filter)));
            h.Patch(Search.WalkerStart, prefix: new HarmonyMethod(typeof(Search), "InitializationBegin"), finalizer: new HarmonyMethod(typeof(Search), "InitializationEnd"), transpiler: new HarmonyMethod(typeof(SearchBindingTests), nameof(ReplaceWalkerUnityReads)));
            registeredNav = nav; Search.RegisterOwner(agent);
            Check(ReferenceEquals(Search.OwnerFor(nav), agent), "Serialized Walker owner registration lost the actual agent");
            Check(agent.Entity == null, "Fixture unexpectedly has a prefab root/chunk before native initialization");
            Search.MarkScanned(graph);
            long boundBefore = Search.Submitted;
            nav.Wander(position, 100, .1f);
            Check(submitted is ConstantPath && Search.Submitted == boundBefore + 1, "Actual native Wander -> Construct -> StartPath did not bind the submitted path");
            var path = submitted!;
            Search.PrepareBegin(path, out var call);
            Check(Search.Preparation?.Layer == 100, "Submitted path lost its cave context before preparation");
            long scopedBefore = Search.ScopedQueries;
            var nearest = astar.GetNearest(position, path.nnConstraint, null);
            Search.PrepareEnd(call, null);
            Check(nearest.node == node && Search.ScopedQueries == scopedBefore + 1, "Real submitted path did not use its completed native cave graph: node=" + (nearest.node == node) + ";scoped=" + (Search.ScopedQueries - scopedBefore) + Search.Health() + ";constraint=" + path.nnConstraint.GetType().Name + ";area=" + path.nnConstraint.area + ";constrainArea=" + path.nnConstraint.constrainArea);
            Check(Search.Preparation == null && Search.CurrentQuery == null, "Submission/preparation context leaked");

            var walker = UnityObject<Walker>();
            typeof(Walker).GetField("transform", Any)!.SetValue(walker, UnityObject<Transform>());
            long initBefore = Search.InitQueries;
            Search.PrepareBegin(path, out var outerCall);
            var outer = Search.Preparation;
            Search.WalkerStart.Invoke(walker, null);
            Check(Search.InitQueries == initBefore + 1 && Vector3.Distance(writtenPosition, position) < .01f, "Native Walker.Start did not use its known owner's completed cave graph");
            Check(ReferenceEquals(Search.Preparation, outer), "Walker.Start did not restore its enclosing path context");
            graph.Fail = true;
            try { Search.WalkerStart.Invoke(walker, null); throw new Exception("Native Walker.Start exception was swallowed"); }
            catch (TargetInvocationException exception) { Check(ReferenceEquals(exception.InnerException, Graph.Sentinel), "Native Walker.Start exception identity changed"); }
            finally { graph.Fail = false; }
            Check(ReferenceEquals(Search.Preparation, outer) && Search.CurrentQuery == null, "Failed Walker.Start leaked context");
            Search.PrepareEnd(outerCall, null);

            // The game's normal directional Wander uses its own captured
            // Navigator closure; it is not an arbitrary custom callback.
            boundBefore = Search.Submitted;
            nav.Wander(position, 100, Vector3.right, 2, .1f);
            Check(submitted is ConstantPath && Search.Submitted == boundBefore + 1, "Native directional Wander was excluded as a custom callback");
            var directional = submitted!;
            directional.callback(directional);
            Check(submitted is ABPath && Search.Submitted == boundBefore + 2, "Native directional follow-up did not inherit the same cave ownership");
            Search.PrepareBegin(submitted!, out call);
            Check(Search.Preparation?.Layer == 100, "Directional ABPath follow-up lost its layer");
            Search.PrepareEnd(call, null);

            // If the native selection is outside the owned layer, its follow-up
            // is left native even though the preceding ConstantPath was bound.
            var outside = position + new Vector3(0, 20, 0);
            submitted = null;
            nav.Wander(position, 100, Vector3.right, 2, .1f);
            directional = submitted!;
            var originalPosition = position; position = outside;
            boundBefore = Search.Submitted;
            directional.callback(directional);
            position = originalPosition;
            Check(submitted is ABPath && Search.Submitted == boundBefore, "Out-of-layer native follow-up was bound");
            Search.PrepareBegin(submitted!, out call); Check(Search.Preparation == null, "Out-of-layer follow-up retained a cave binding"); Search.PrepareEnd(call, null);

            // Callback and bounds exclusions remain native, and are diagnosed.
            long callbacks = Search.Rejected(Search.RejectReason.UnsupportedCallback);
            wander.Invoke(nav, new object[] { position, 100, .1f, new OnPathDelegate(_ => { }) });
            Check(Search.Rejected(Search.RejectReason.UnsupportedCallback) == callbacks + 1, "Custom callback was accepted or not diagnosed");
            long bounds = Search.Rejected(Search.RejectReason.Bounds);
            nav.Wander(position + new Vector3(0, 20, 0), 100, .1f);
            Check(Search.Rejected(Search.RejectReason.Bounds) == bounds + 1, "Cross-layer wander was bound");
            Search.PathId.SetValue(path, (ushort)((ushort)Search.PathId.GetValue(path, null)! + 1), null);
            long generation = Search.Rejected(Search.RejectReason.GenerationMismatch);
            Search.PrepareBegin(path, out call); Check(Search.Preparation == null, "Pooled path generation retained stale ownership"); Search.PrepareEnd(call, null);
            Check(Search.Rejected(Search.RejectReason.GenerationMismatch) == generation + 1, "Generation rejection was not diagnosed");

            // A reused agent reference can point to a different serialized Walker.
            registeredNav = nextNav; Search.RegisterOwner(agent);
            // Inspect the weak mapping rather than invoking unavailable Unity hierarchy calls.
            var owners = typeof(Search).GetField("Owners", Any)!.GetValue(null)!;
            object?[] oldLookup = { nav, null };
            Check(!(bool)owners.GetType().GetMethod("TryGetValue")!.Invoke(owners, oldLookup)!, "Agent reinitialization retained its old Navigator owner mapping");
            Check(ReferenceEquals(Search.OwnerFor(nextNav), agent), "Agent reinitialization did not replace its Navigator mapping");
            Console.WriteLine("PASS real native submission: serialized Walker owner mapping, early entity-load cave before root assignment, native normal/directional Wander and ABPath follow-up binding, native Walker.Start initial nearest scope, completed-graph lookup, custom callback/cross-layer guards, pooled-generation diagnostics, exception/context restoration and owner reinitialization.");
        }
        finally
        {
            h.UnpatchSelf(); AstarPath.active = previousAstar; Loading.CurrentCave = previousCave;
            registeredNav = null; submitted = null;
        }
    }
}
