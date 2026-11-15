using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace CaveStepDiagnostics
{
    public sealed class Core : CavePerformance.Support.Module
    {
        private HarmonyLib.Harmony? harmony;
        private ServerTrace? sink, pathSink, health;
        private float nextPathHealth;
        private float nextHealthCheck;
        private bool writerWarning, contextWarning;
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            try
            {
                string dir = Path.Combine(DataDirectory, "Diagnostics", "CaveStepDiagnostics", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
                Directory.CreateDirectory(dir);
                sink = new ServerTrace(Path.Combine(dir, "slow-steps.csv"), "utc,duration_ms,layer,chunk_x,chunk_z,plugin_index,plugin_name,plugin_type,iterator_type,iterator_state_before,outer_state_before,exception_type,dropped_rows,context_error,step_id");
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Alta.Caves.GenerationBase", false)).FirstOrDefault(t => t != null)
                    ?? throw new TypeLoadException("Alta.Caves.GenerationBase");
                var iterators = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => t.Name.StartsWith("<Render>d__", StringComparison.Ordinal)).ToArray();
                if (iterators.Length != 1) throw new InvalidOperationException("Expected exactly one GenerationBase.Render iterator; found " + iterators.Length);
                var step = iterators[0].GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingMethodException(iterators[0].FullName, "MoveNext");
                StepProbe.Initialize(iterators[0], type, sink);
                harmony = new HarmonyLib.Harmony("ATT.CavePerformance.CaveStepDiagnostics");
                harmony.Patch(step, prefix: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.Start)), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End)));
                pathSink = new ServerTrace(Path.Combine(dir, "pathfinding-steps.csv"), "utc,step_id,stage,duration_ms,layer,chunk_x,chunk_z,thread_id,state_before,state_after,yielded,progress,description,exception_type,context_error,dropped_rows");
                health = new ServerTrace(Path.Combine(dir, "pathfinding-health.csv"), "utc,stage,calls,slow_calls,context_errors,probe_errors,writer_error,dropped_rows");
                PathProbe.Sink = pathSink;
                PathProbe.AttachAll(type.Assembly, harmony, Path.Combine(dir, "pathfinding-hooks.csv"));
                File.WriteAllText(Path.Combine(dir, "run.txt"), "Cave Step Diagnostics 0.2.0\nThreshold: 25 ms inclusive wall time\nHook: " + iterators[0].FullName + ".MoveNext\nIterator and state are snapshots before the outer call; a call may transition between iterators. Rows can overlap other diagnostics.\nPathfinding nested steps are inclusive and recorded only within a cave render step, on the same thread. IDs join slow-steps.csv to pathfinding-steps.csv.\n");
                LoggerInstance.Msg("Cave step diagnostics attached; output: " + dir);
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Cave step diagnostics unavailable: " + e);
                OnApplicationQuit();
            }
        }
        public override void OnUpdate()
        {
            // Checking only every ten seconds avoids adding work to every frame.
            if (sink == null) return;
            if (pathSink != null && health != null && Time.realtimeSinceStartup >= nextPathHealth)
            {
                nextPathHealth = Time.realtimeSinceStartup + 60f;
                foreach (var info in PathProbe.Targets.Values)
                    health.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), info.Stage, Interlocked.Read(ref info.Calls), Interlocked.Read(ref info.Slow),
                        Interlocked.Read(ref info.ContextErrors), PathProbe.Errors, pathSink.Error ?? "", pathSink.Dropped));
            }
            if (Time.realtimeSinceStartup < nextHealthCheck) return;
            nextHealthCheck = Time.realtimeSinceStartup + 10f;
            if (!writerWarning && sink.Error != null) { writerWarning = true; LoggerInstance.Warning("Cave step writer failed: " + sink.Error); }
            if (!writerWarning && ((pathSink?.Error != null) || (health?.Error != null))) { writerWarning = true; LoggerInstance.Warning("Cave pathfinding diagnostics writer failed: " + (pathSink?.Error ?? health?.Error)); }
            if (!contextWarning && (StepProbe.Errors > 0 || PathProbe.Errors > 0)) { contextWarning = true; LoggerInstance.Warning("Cave step context collection encountered errors; inspect context_error in slow-steps.csv. Count at first warning: " + StepProbe.Errors); }
        }
        public override void OnApplicationQuit()
        {
            StepProbe.Sink = null;
            PathProbe.Sink = null;
            try { harmony?.UnpatchSelf(); } catch { }
            try { sink?.Dispose(); } catch { }
            try { pathSink?.Dispose(); } catch { }
            try { health?.Dispose(); } catch { }
            sink = null;
        }
    }

    internal static class StepProbe
    {
        internal struct State
        {
            internal long Started;
            internal object? Instance, Iterator;
            internal int PluginIndex, InnerState, OuterState;
            internal bool IsPluginIterator, ContextFailed;
            internal long StepId, PreviousStepId;
            internal object? PreviousInstance;
        }
        internal static ServerTrace? Sink;
        [ThreadStatic] internal static long CurrentStepId;
        [ThreadStatic] internal static object? CurrentInstance;
        private static long nextStepId;
        private static FieldInfo? ownerField, chunkField, stackField, pathField, indexField, outerStateField;
        private static PropertyInfo? layerProperty, pluginsProperty, xProperty, zProperty, pluginNameProperty, pluginProperty;
        private static readonly Dictionary<Type, FieldInfo?> StateFields = new Dictionary<Type, FieldInfo?>();
        [ThreadStatic] private static Type? cachedType;
        [ThreadStatic] private static FieldInfo? cachedField;
        private static long contextErrors;
        internal static long Errors => Interlocked.Read(ref contextErrors);
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Initialize(Type stateType, Type generatorType, ServerTrace sink)
        {
            ownerField = RequiredField(stateType, "<>4__this"); chunkField = RequiredField(stateType, "chunk");
            stackField = RequiredField(stateType, "<renderStack>5__3"); pathField = RequiredField(stateType, "<pathfindingGeneration>5__5");
            indexField = RequiredField(stateType, "<i>5__2"); outerStateField = RequiredField(stateType, "<>1__state");
            layerProperty = RequiredProperty(generatorType, "Layer"); pluginsProperty = RequiredProperty(generatorType, "PluginInstances");
            Type chunkType = chunkField.FieldType; xProperty = RequiredProperty(chunkType, "X"); zProperty = RequiredProperty(chunkType, "Z");
            Type? pluginType = generatorType.Assembly.GetType("Alta.Caves.PluginInstance", false);
            if (pluginType == null) throw new TypeLoadException("Alta.Caves.PluginInstance");
            pluginNameProperty = RequiredProperty(pluginType, "Name"); pluginProperty = RequiredProperty(pluginType, "Plugin");
            if (ownerField.FieldType != generatorType || chunkType.FullName != "Alta.Caves.MapChunk" ||
                indexField.FieldType != typeof(int) || outerStateField.FieldType != typeof(int) ||
                !typeof(Stack<IEnumerator>).IsAssignableFrom(stackField.FieldType) || !typeof(IEnumerator).IsAssignableFrom(pathField.FieldType) ||
                layerProperty.PropertyType != typeof(int) || xProperty.PropertyType != typeof(int) || zProperty.PropertyType != typeof(int) ||
                !typeof(IList).IsAssignableFrom(pluginsProperty.PropertyType) || pluginNameProperty.PropertyType != typeof(string))
                throw new InvalidOperationException("Cave iterator metadata types changed; refusing to attach");
            Sink = sink;
        }
        private static FieldInfo RequiredField(Type type, string name) => type.GetField(name, Flags) ?? throw new MissingFieldException(type.FullName, name);
        private static PropertyInfo RequiredProperty(Type type, string name) => type.GetProperty(name, Flags) ?? throw new MissingMemberException(type.FullName, name);

        internal static void Start(object __instance, out State __state)
        {
            __state = new State { Started = Stopwatch.GetTimestamp(), PluginIndex = -1, InnerState = int.MinValue, OuterState = int.MinValue };
            try
            {
                __state.PreviousStepId = CurrentStepId; __state.PreviousInstance = CurrentInstance;
                __state.StepId = Interlocked.Increment(ref nextStepId);
                CurrentStepId = __state.StepId; CurrentInstance = __instance;
                __state.Instance = __instance;
                __state.PluginIndex = (int)indexField!.GetValue(__instance);
                __state.OuterState = (int)outerStateField!.GetValue(__instance);
                var stack = stackField!.GetValue(__instance) as Stack<IEnumerator>;
                __state.IsPluginIterator = stack != null && stack.Count != 0;
                __state.Iterator = __state.IsPluginIterator ? stack!.Peek() : pathField!.GetValue(__instance);
                if (__state.Iterator != null)
                {
                    Type type = __state.Iterator.GetType();
                    if (cachedType != type)
                    {
                        lock (StateFields) { if (!StateFields.TryGetValue(type, out cachedField)) StateFields[type] = cachedField = type.GetField("<>1__state", Flags); }
                        cachedType = type;
                    }
                    if (cachedField?.GetValue(__state.Iterator) is int state) __state.InnerState = state;
                }
            }
            catch { __state.ContextFailed = true; Interlocked.Increment(ref contextErrors); }
        }
        internal static void End(State __state, Exception? __exception)
        {
            try
            {
                var sink = Sink; if (sink == null || __state.Started == 0) return;
                double ms = (Stopwatch.GetTimestamp() - __state.Started) * 1000.0 / Stopwatch.Frequency;
                if (ms < 25) return;
                object layer = "", x = "", z = "", name = "", pluginType = ""; string contextError = __state.ContextFailed ? "prefix_context_unavailable" : "";
                try
                {
                    object? owner = __state.Instance == null ? null : ownerField!.GetValue(__state.Instance);
                    object? chunk = __state.Instance == null ? null : chunkField!.GetValue(__state.Instance);
                    if (owner != null)
                    {
                        layer = layerProperty!.GetValue(owner, null) ?? "";
                        var plugins = pluginsProperty!.GetValue(owner, null) as IList;
                        if (__state.IsPluginIterator && plugins != null && __state.PluginIndex >= 0 && __state.PluginIndex < plugins.Count)
                        {
                            object? plugin = plugins[__state.PluginIndex];
                            if (plugin != null) { name = pluginNameProperty!.GetValue(plugin, null) ?? ""; pluginType = pluginProperty!.GetValue(plugin, null)?.GetType().FullName ?? ""; }
                        }
                    }
                    if (chunk != null) { x = xProperty!.GetValue(chunk, null) ?? ""; z = zProperty!.GetValue(chunk, null) ?? ""; }
                }
                catch (Exception e) { contextError = (contextError.Length == 0 ? "" : contextError + ";") + e.GetType().Name; }
                sink.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), ms, layer, x, z, __state.IsPluginIterator ? (object)__state.PluginIndex : "",
                    name, pluginType, __state.Iterator?.GetType().FullName ?? "transition/unknown",
                    __state.InnerState == int.MinValue ? (object)"" : __state.InnerState,
                    __state.OuterState == int.MinValue ? (object)"" : __state.OuterState,
                    __exception?.GetType().FullName ?? "", sink.Dropped, contextError, __state.StepId));
            }
            catch { Interlocked.Increment(ref contextErrors); }
            finally { CurrentStepId = __state.PreviousStepId; CurrentInstance = __state.PreviousInstance; }
        }
        internal static void GetContext(object instance, out object layer, out object x, out object z)
        {
            layer = ""; x = ""; z = "";
            object? owner = ownerField!.GetValue(instance), chunk = chunkField!.GetValue(instance);
            if (owner != null) layer = layerProperty!.GetValue(owner, null) ?? "";
            if (chunk != null) { x = xProperty!.GetValue(chunk, null) ?? ""; z = zProperty!.GetValue(chunk, null) ?? ""; }
        }
    }
}
