using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace CaveStepDiagnostics
{
    // Measures nested iterator calls only while GenerationBase.Render is running on this thread.
    // These are inclusive wall times; rows sharing a parent ID overlap and must not be summed.
    internal static class PathProbe
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal sealed class Info
        {
            internal readonly string Stage;
            internal readonly MethodInfo Step;
            internal readonly FieldInfo State;
            internal readonly FieldInfo? Current, Progress, Description;
            internal long Calls, Slow, ContextErrors;
            internal Info(string stage, MethodInfo step, FieldInfo state, FieldInfo? current, FieldInfo? progress, FieldInfo? description)
            { Stage = stage; Step = step; State = state; Current = current; Progress = progress; Description = description; }
        }
        internal struct CallState
        {
            internal long Started, StepId;
            internal int Before, Thread;
            internal Info? Info;
            internal object? Instance, ParentInstance;
            internal string? Error;
        }
        internal static readonly ConcurrentDictionary<MethodBase, Info> Targets = new ConcurrentDictionary<MethodBase, Info>();
        internal static ServerTrace? Sink;
        private static long errors;
        internal static long Errors => Interlocked.Read(ref errors);
        internal static readonly (string type, string method, string stage)[] Specs = {
            ("Alta.Chunks.CaveLayerStreamer", "GeneratePathfinding", "CaveLayerStreamer.GeneratePathfinding"),
            ("AstarPath", "ScanAsync", "AstarPath.ScanAsync"),
            ("AstarPath", "ScanGraph", "AstarPath.ScanGraph"),
            ("Pathfinding.RecastGraph", "ScanInternal", "RecastGraph.ScanInternal"),
            ("Pathfinding.RecastGraph", "ScanAllTiles", "RecastGraph.ScanAllTiles")
        };
        internal static Info Resolve(Assembly assembly, string type, string method, string stage)
        {
            Type owner = assembly.GetType(type, true)!;
            Type[] candidates = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .Where(t => t.Name.StartsWith("<" + method + ">d__", StringComparison.Ordinal)).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("Expected one concrete iterator, found " + candidates.Length);
            Type iterator = candidates[0];
            if (iterator.IsValueType) throw new InvalidOperationException("Expected reference-type iterator; refusing incompatible hook");
            var step = iterator.GetMethod("MoveNext", Flags) ?? throw new MissingMethodException(iterator.FullName, "MoveNext");
            if (step.ReturnType != typeof(bool) || step.GetParameters().Length != 0) throw new InvalidOperationException("Unexpected MoveNext signature");
            var state = iterator.GetField("<>1__state", Flags) ?? throw new MissingFieldException(iterator.FullName, "<>1__state");
            if (state.FieldType != typeof(int)) throw new InvalidOperationException("Iterator state field changed");
            var current = iterator.GetField("<>2__current", Flags);
            FieldInfo? progress = null, description = null;
            if (current != null && current.FieldType.FullName == "Pathfinding.Progress")
            {
                progress = current.FieldType.GetField("progress", Flags);
                description = current.FieldType.GetField("description", Flags);
            }
            return new Info(stage, step, state, current, progress, description);
        }
        internal static void AttachAll(Assembly assembly, HarmonyLib.Harmony harmony, string hookFile)
        {
            File.WriteAllText(hookFile, "stage,status,detail\n");
            foreach (var spec in Specs)
            {
                try
                {
                    Info info = Resolve(assembly, spec.type, spec.method, spec.stage);
                    if (!Targets.TryAdd(info.Step, info)) throw new InvalidOperationException("Duplicate target");
                    try { harmony.Patch(info.Step, prefix: new HarmonyMethod(typeof(PathProbe), nameof(Start)), finalizer: new HarmonyMethod(typeof(PathProbe), nameof(End))); }
                    catch { Targets.TryRemove(info.Step, out _); throw; }
                    File.AppendAllText(hookFile, ServerTrace.Row(spec.stage, "attached", info.Step.DeclaringType!.FullName) + "\n");
                }
                catch (Exception e) { File.AppendAllText(hookFile, ServerTrace.Row(spec.stage, "unavailable", e.GetType().Name + ": " + e.Message) + "\n"); }
            }
        }
        internal static void Start(MethodBase __originalMethod, object __instance, out CallState __state)
        {
            __state = new CallState { Before = int.MinValue };
            if (StepProbe.CurrentStepId == 0) return;
            try
            {
                if (!Targets.TryGetValue(__originalMethod, out Info info)) { Interlocked.Increment(ref errors); return; }
                __state.Started = Stopwatch.GetTimestamp(); __state.StepId = StepProbe.CurrentStepId;
                __state.ParentInstance = StepProbe.CurrentInstance; __state.Instance = __instance;
                __state.Info = info; __state.Thread = Thread.CurrentThread.ManagedThreadId;
                Interlocked.Increment(ref info.Calls);
                __state.Before = (int)info.State.GetValue(__instance);
            }
            catch (Exception e) { __state.Error = e.GetType().Name; if (__state.Info != null) Interlocked.Increment(ref __state.Info.ContextErrors); else Interlocked.Increment(ref errors); }
        }
        internal static void End(CallState __state, bool __result, Exception? __exception)
        {
            try
            {
                var sink = Sink; var info = __state.Info;
                if (sink == null || info == null || __state.Started == 0) return;
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                if (ms < 25) return;
                Interlocked.Increment(ref info.Slow);
                object layer = "", x = "", z = "", after = "", progress = "", description = "";
                string contextError = __state.Error ?? "";
                try
                {
                    if (__state.ParentInstance != null) StepProbe.GetContext(__state.ParentInstance, out layer, out x, out z);
                    if (__state.Instance != null)
                    {
                        after = info.State.GetValue(__state.Instance) ?? "";
                        // Progress is post-call and is a breadcrumb, not a label for all work in the call.
                        if (__result && info.Progress != null && info.Description != null && info.Current != null)
                        {
                            object? current = info.Current.GetValue(__state.Instance);
                            if (current != null) { progress = info.Progress.GetValue(current) ?? ""; description = info.Description.GetValue(current) ?? ""; }
                        }
                    }
                }
                catch (Exception e) { contextError = (contextError.Length == 0 ? "" : contextError + ";") + e.GetType().Name; Interlocked.Increment(ref info.ContextErrors); }
                sink.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), __state.StepId, info.Stage, ms, layer, x, z, __state.Thread,
                    __state.Before == int.MinValue ? (object)"" : __state.Before, after, __result, progress, description,
                    __exception?.GetType().FullName ?? "", contextError, sink.Dropped));
            }
            catch { Interlocked.Increment(ref errors); }
        }
    }
}
