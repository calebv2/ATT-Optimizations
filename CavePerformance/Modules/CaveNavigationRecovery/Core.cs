using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace CaveNavigationRecovery
{
    public sealed class Core : CavePerformance.Support.Module
    {
        internal static Core? Instance;
        private HarmonyLib.Harmony? harmony;
        private static int ownerThread;
        private static bool bypassFactory, queueWarning;
        private static readonly ConditionalWeakTable<object, ScanState> Scans = new ConditionalWeakTable<object, ScanState>();
        private static readonly ConditionalWeakTable<object, WanderState> Wanders = new ConditionalWeakTable<object, WanderState>();
        private static readonly List<PendingWander> Queue = new List<PendingWander>();
        [ThreadStatic] private static WanderState? retrying;
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        internal static MethodInfo Generate = null!, Need = null!, ScanStep = null!, Wander = null!, Failure = null!, Cancel = null!;
        private static FieldInfo graph = null!, scanGraphs = null!, preparingPath = null!, requesting = null!;
        private static PropertyInfo scanning = null!, layer = null!;
        private static FieldInfo active = null!, astarData = null!;
        private static MethodInfo removeGraph = null!, getIndex = null!;
        private static bool OnThread => Thread.CurrentThread.ManagedThreadId == ownerThread;
        private static bool Alive(object owner) => owner is Behaviour behaviour && behaviour != null && behaviour.isActiveAndEnabled;
        private static string Name(object owner) => owner is UnityEngine.Object value ? value.name : owner.GetType().FullName;
        private static void Log(string message) { Instance?.LoggerInstance.Msg(message); }
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            Instance = this; ownerThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                Assembly root = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("Alta.Chunks.CaveLayerStreamer", false) != null);
                Bind(root);
                harmony = new HarmonyLib.Harmony("ATT.CavePerformance.CaveNavigationRecovery");
                Attach(harmony);
                Log("READY v0.1.0; completed cave scans required; at most three scan attempts, five seconds between attempts; failed wander flag recovery with at most two delayed retries. GC policy unchanged.");
            }
            catch (Exception e)
            {
                try { harmony?.UnpatchSelf(); }
                catch (Exception rollback) { LoggerInstance.Error("Patch rollback failed: " + rollback); }
                LoggerInstance.Error("Cave navigation recovery DISABLED: " + e);
                harmony = null;
            }
        }
        internal static void Bind(Assembly root)
        {
            if (root.ManifestModule.ModuleVersionId != new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff")) throw new NotSupportedException("Unsupported game assembly");
            Type cave = root.GetType("Alta.Chunks.CaveLayerStreamer", true)!;
            Type astar = root.GetType("AstarPath", true)!;
            Type nav = root.GetType("Alta.Pathfinding.Navigator", true)!;
            Type data = root.GetType("Pathfinding.AstarData", true)!;
            Type graphType = root.GetType("Pathfinding.NavGraph", true)!;
            Type recast = root.GetType("Pathfinding.RecastGraph", true)!;
            Type path = root.GetType("Pathfinding.Path", true)!;
            Type callback = root.GetType("Pathfinding.OnPathDelegate", true)!;
            Generate = Method(cave, "GeneratePathfinding", typeof(IEnumerator));
            Need = Method(cave, "get_NeedToGeneratePathFinding", typeof(bool));
            graph = Field(cave, "navGraph", recast);
            layer = cave.GetProperty("LayerIdentifier", Flags) ?? throw new MissingMemberException("LayerIdentifier");
            Type iterator = astar.GetNestedTypes(Flags).Single(t => t.Name.StartsWith("<ScanAsync>d__", StringComparison.Ordinal));
            ScanStep = Method(iterator, "MoveNext", typeof(bool));
            scanGraphs = Field(iterator, "graphsToScan", graphType.MakeArrayType());
            active = astar.GetField("active", Flags) ?? throw new MissingFieldException("AstarPath.active");
            scanning = astar.GetProperty("isScanning", Flags) ?? throw new MissingMemberException("AstarPath.isScanning");
            astarData = Field(astar, "data", data);
            if (!active.IsStatic || active.FieldType != astar || scanning.PropertyType != typeof(bool) || layer.PropertyType != typeof(int))
                throw new NotSupportedException("Unexpected A* or layer metadata");
            removeGraph = Method(data, "RemoveGraph", typeof(bool), graphType);
            getIndex = Method(data, "GetGraphIndex", typeof(int), graphType);
            Wander = Method(nav, "Wander", typeof(void), typeof(Vector3), typeof(int), typeof(float), callback);
            Failure = Method(nav, "OnPathFindFailed", typeof(void), path);
            Cancel = Method(nav, "CancelRequest", typeof(void));
            preparingPath = Field(nav, "preparingPath", path);
            requesting = Field(nav, "isRequestingPath", typeof(bool));
        }
        private static FieldInfo Field(Type type, string name, Type expected)
        {
            var field = type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name);
            if (field.IsStatic || field.FieldType != expected) throw new NotSupportedException("Unexpected field " + name);
            return field;
        }
        private static MethodInfo Method(Type type, string name, Type result, params Type[] args)
        {
            var method = type.GetMethod(name, Flags, null, args, null) ?? throw new MissingMethodException(type.FullName, name);
            if (method.IsStatic || method.ReturnType != result) throw new NotSupportedException("Unexpected method " + name);
            return method;
        }
        internal static void Attach(HarmonyLib.Harmony patcher)
        {
            HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(Core), name);
            patcher.Patch(Generate, postfix: Hook(nameof(WrapScan)));
            patcher.Patch(Need, postfix: Hook(nameof(NeedsScan)));
            patcher.Patch(ScanStep, postfix: Hook(nameof(ScanFinished)));
            patcher.Patch(Wander, postfix: Hook(nameof(WanderStarted)));
            patcher.Patch(Failure, prefix: Hook(nameof(BeforeFailure)), postfix: Hook(nameof(AfterFailure)));
            patcher.Patch(Cancel, prefix: Hook(nameof(Cancelled)));
        }
        private static object? Invoke(MethodInfo method, object owner, params object[] args)
        {
            try { return method.Invoke(owner, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        private sealed class ScanHost : IScanHost
        {
            private readonly object owner;
            private IEnumerator? first;
            internal ScanHost(object owner, IEnumerator first) { this.owner = owner; this.first = first; }
            public bool Alive => owner is UnityEngine.Object value && value != null;
            public double Now => Time.realtimeSinceStartup;
            public object? Graph => graph.GetValue(owner);
            private object Astar => active.GetValue(null) ?? throw new InvalidOperationException("A* unavailable");
            public bool Scanning => (bool)scanning.GetValue(Astar, null);
            public IEnumerator CreateOriginal()
            {
                if (first != null) { var item = first; first = null; return item; }
                bypassFactory = true;
                try { return (IEnumerator)Invoke(Generate, owner)!; }
                finally { bypassFactory = false; }
            }
            public void RemoveIncompleteGraph(object incomplete)
            {
                object manager = Astar;
                if ((bool)scanning.GetValue(manager, null)) throw new InvalidOperationException("Cannot remove a graph while scanning");
                object data = astarData.GetValue(manager)!;
                if ((int)Invoke(getIndex, data, incomplete)! < 0) throw new InvalidOperationException("Incomplete cave graph is no longer registered; refusing unsafe removal");
                if (!(bool)Invoke(removeGraph, data, incomplete)!) throw new InvalidOperationException("Incomplete cave graph removal failed");
                if (ReferenceEquals(Graph, incomplete)) graph.SetValue(owner, null);
                Log("removed incomplete cave graph");
            }
            public void Log(string message) => Core.Log("cave layer=" + layer.GetValue(owner, null) + " " + message);
        }
        private static void WrapScan(object __instance, ref IEnumerator __result)
        {
            if (!OnThread || bypassFactory) return;
            __result = new ScanRecovery(new ScanHost(__instance, __result), Scans.GetOrCreateValue(__instance));
        }
        private static void NeedsScan(object __instance, ref bool __result)
        {
            if (!OnThread) return;
            __result = !ScanRecovery.IsReady(Scans.GetOrCreateValue(__instance), graph.GetValue(__instance));
        }
        private static void ScanFinished(object __instance, bool __result)
        {
            if (__result || ScanRecovery.CurrentScan == null) return;
            if (scanGraphs.GetValue(__instance) is Array graphs)
                foreach (object item in graphs) if (item != null) ScanRecovery.CurrentScan.Completed(item);
        }
        private static void Cancelled(object __instance)
        {
            if (!OnThread) return;
            var state = Wanders.GetOrCreateValue(__instance);
            state.Cancel(ReferenceEquals(retrying, state));
        }
        private static void WanderStarted(object __instance, object[] __args)
        {
            if (!OnThread || !(bool)requesting.GetValue(__instance) || preparingPath.GetValue(__instance) == null) return;
            Wanders.GetOrCreateValue(__instance).Arguments = (object[])__args.Clone();
        }
        private struct FailedRequest
        {
            internal WanderState? State;
            internal object[]? Arguments;
            internal long Epoch;
        }
        private static void BeforeFailure(object __instance, object __0, out FailedRequest __state)
        {
            __state = default;
            if (!OnThread || !ReferenceEquals(preparingPath.GetValue(__instance), __0) || !(bool)requesting.GetValue(__instance)) return;
            var state = Wanders.GetOrCreateValue(__instance);
            __state = new FailedRequest { State = state, Arguments = state.Arguments, Epoch = state.Epoch };
            requesting.SetValue(__instance, false);
            preparingPath.SetValue(__instance, null);
            Log("wander preparation failed; cleared pending flag mob=" + Name(__instance));
        }
        private sealed class PendingWander
        {
            internal readonly WeakReference Owner;
            internal readonly long Epoch;
            internal readonly double Due;
            internal PendingWander(object owner, WanderState state) { Owner = new WeakReference(owner); Epoch = state.Epoch; Due = state.Due; }
        }
        private static void AfterFailure(object __instance, FailedRequest __state)
        {
            if (__state.State == null || !Alive(__instance)) return;
            var state = __state.State;
            if (!state.Schedule(__state.Epoch, __state.Arguments, Time.realtimeSinceStartup, (bool)requesting.GetValue(__instance))) return;
            if (Queue.Count >= 256)
            {
                state.Pending = false;
                if (!queueWarning) { queueWarning = true; Log("wander recovery queue full; extra automatic retries skipped"); }
                return;
            }
            Queue.Add(new PendingWander(__instance, state));
        }
        public override void OnUpdate()
        {
            if (harmony == null || !OnThread || Queue.Count == 0) return;
            double now = Time.realtimeSinceStartup;
            int remaining = 2;
            // A bounded queue, two requests per update, and delayed retries avoid a path request burst.
            for (int i = 0; i < Queue.Count && remaining > 0;)
            {
                var entry = Queue[i];
                object? owner = entry.Owner.Target;
                if (owner == null || !Alive(owner) || !Wanders.TryGetValue(owner, out var state) || !state.Pending || state.Epoch != entry.Epoch)
                { Queue.RemoveAt(i); continue; }
                if (now < entry.Due) { i++; continue; }
                Queue.RemoveAt(i);
                state.Pending = false;
                if ((bool)requesting.GetValue(owner) || state.Arguments == null) continue;
                object[] args = state.Arguments;
                state.Retries++;
                remaining--;
                var previous = retrying; retrying = state;
                try { Log("retrying failed wander attempt=" + state.Retries + " mob=" + Name(owner)); Invoke(Wander, owner, args); }
                catch (Exception e) { Log("wander retry failed: " + e.GetType().Name + ": " + e.Message); }
                finally { retrying = previous; }
            }
        }
        public override void OnApplicationQuit()
        {
            Queue.Clear();
            harmony?.UnpatchSelf();
        }
    }
}
