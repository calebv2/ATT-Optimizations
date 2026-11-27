using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Alta.Caves;
using Alta.Chunks;
using HarmonyLib;
using UnityEngine;

namespace CavePerformance
{
    // Observes existing tasks; never substitutes an awaiter, changes a scheduler,
    // or touches Unity objects from task-completion callbacks. Phase wall times
    // may overlap and must not be summed as CPU time.
    internal static class LoadTransitionTiming
    {
        internal sealed class Load
        {
            internal readonly long Id;
            internal readonly string Label;
            internal bool TotalStarted, TotalFinished;
            internal Load(long id, string label) { Id = id; Label = label; }
        }
        internal sealed class Call
        {
            internal readonly Load Load;
            internal readonly string Phase;
            internal readonly double Started;
            private int finished;
            internal Call(Load load, string phase) { Load = load; Phase = phase; Started = Now(); }
            internal bool FinishOnce() => Interlocked.Exchange(ref finished, 1) == 0;
        }
        private static readonly ConditionalWeakTable<object, Load> Loads = new ConditionalWeakTable<object, Load>();
        private static readonly Dictionary<MethodBase, string> Phases = new Dictionary<MethodBase, string>();
        private static FieldInfo chunk = null!;
        internal static MethodInfo Total = null!, File = null!, Entities = null!;
        private static MethodInfo entrance = null!, setup = null!;
        internal static bool Ready;
        private static long nextId;
        internal static Func<double> Now = () => Core.Now;
        internal static Action<string, double, string, Exception?> Emit = Core.Row;
        internal static void Bind()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Phases.Clear();
            chunk = typeof(DynamicChunkContentManager).GetField("chunk", flags) ?? throw new MissingFieldException("DynamicChunkContentManager.chunk");
            Total = typeof(DynamicChunkContentManager).GetMethod("LoadInternal", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("LoadInternal");
            File = typeof(DynamicChunkContentManager).GetMethod("LoadFromFile", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("LoadFromFile");
            Entities = typeof(DynamicChunkContentManager).GetMethod("LoadEntities", flags) ?? throw new MissingMethodException("LoadEntities");
            Phases[Total] = "total"; Phases[File] = "file_ready";
            foreach (Type type in new[] { typeof(CaveLayerStreamer), typeof(StaticCaveChunk) })
            {
                var method = type.GetMethod("Alta.Chunks.IProceduralGenerationStreamer.LoadStatic", flags, null, new[] { typeof(CancellationToken) }, null)
                    ?? throw new MissingMethodException(type.FullName, "LoadStatic");
                if (method.ReturnType != typeof(Task)) throw new NotSupportedException("Static load task signature changed");
                Phases[method] = "static_ready";
            }
            entrance = RequireTask(typeof(CaveLayerManager), "WaitForEntranceInformation", typeof(int));
            setup = RequireTask(typeof(GenerationBase), "SetupLayer", typeof(CaveLayerStreamer), typeof(int), typeof(int), typeof(Vector2), typeof(Vector2));
            Phases[RequireTask(typeof(CaveLayerStreamer), "Generate")] = "layer_generate";
            Phases[RequireTask(typeof(GenerationBase), "PrepareAndStart")] = "layer_prepare_and_render";
            Phases[RequireTask(typeof(GenerationBase), "StartGenerating")] = "layer_render_and_nav_ready";
            if (!typeof(Task).IsAssignableFrom(Total.ReturnType) || !typeof(Task).IsAssignableFrom(File.ReturnType) || Entities.ReturnType != typeof(void) || chunk.FieldType != typeof(Chunk))
                throw new NotSupportedException("Cave load timing boundary changed");
        }
        private static MethodInfo RequireTask(Type type, string name, params Type[] parameters)
        {
            var method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
            if (method == null || method.ReturnType != typeof(Task)) throw new MissingMethodException(type.FullName, name);
            return method;
        }
        internal static void Attach(HarmonyLib.Harmony harmony)
        {
            foreach (var method in Phases.Keys)
                AttachTask(harmony, method, nameof(Begin));
            AttachTask(harmony, setup, nameof(BeginSetup));
            AttachTask(harmony, entrance, nameof(BeginEntrance));
            harmony.Patch(Entities, prefix: new HarmonyMethod(typeof(LoadTransitionTiming), nameof(EntityBegin)),
                finalizer: new HarmonyMethod(typeof(LoadTransitionTiming), nameof(EntityEnd)));
            Ready = true;
        }
        private static void AttachTask(HarmonyLib.Harmony harmony, MethodBase method, string prefix) =>
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(LoadTransitionTiming), prefix),
                postfix: new HarmonyMethod(typeof(LoadTransitionTiming), nameof(Returned)),
                finalizer: new HarmonyMethod(typeof(LoadTransitionTiming), nameof(Thrown)));
        private static bool Enabled => Ready && Core.Ready && Core.Main && Core.Config.Timing;
        private static Load? Context(object instance, bool total)
        {
            try
            {
                Chunk? owner = instance is DynamicChunkContentManager ? chunk.GetValue(instance) as Chunk :
                    instance is GenerationBase generator ? generator.LayerStreamer?.Chunk : (instance as ICaveLayer)?.Chunk;
                if (owner == null || !(owner.ProceduralStreamer is ICaveLayer cave)) return null;
                if (!Loads.TryGetValue(owner, out var load) || Volatile.Read(ref load.TotalFinished))
                {
                    Loads.Remove(owner);
                    load = new Load(Interlocked.Increment(ref nextId), "kind=" + (cave is StaticCaveChunk ? "static" : "procedural") + ";layer=" + cave.LayerIdentifier);
                    Loads.Add(owner, load);
                }
                if (total) load.TotalStarted = true;
                return load;
            }
            catch { Interlocked.Increment(ref Core.Errors); return null; }
        }
        internal static Call? Start(object instance, string phase)
        {
            if (!Enabled) return null;
            var load = Context(instance, phase == "total");
            if (load == null) return null;
            var call = new Call(load, phase);
            Write("load_phase_start", 0, call, "status=pending", null);
            return call;
        }
        private static void Begin(object __instance, MethodBase __originalMethod, out Call? __state) => __state = Start(__instance, Phases[__originalMethod]);
        private static void BeginSetup(CaveLayerStreamer __0, out Call? __state) => __state = Start(__0, "layer_setup");
        private static void BeginEntrance(int __0, out Call? __state)
        {
            __state = null;
            if (!Enabled) return;
            var load = new Load(Interlocked.Increment(ref nextId), "kind=procedural;layer=" + __0);
            __state = new Call(load, "entrance_wait");
            Write("load_phase_start", 0, __state, "status=pending;correlate_by_layer=true", null);
        }
        private static void Returned(Task __result, Call? __state) => Observe(__result, __state);
        private static void Thrown(Call? __state, Exception? __exception)
        {
            if (__exception != null) Finish(__state, "failed", __exception);
        }
        internal static void Observe(Task? original, Call? call)
        {
            if (call == null) return;
            if (original == null) { Finish(call, "no_task", null); return; }
            try
            {
                if (original.IsCompleted) TaskFinished(original, call);
                else _ = original.ContinueWith(task => TaskFinished(task, call), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        private static void TaskFinished(Task task, Call call)
        {
            Exception? error = task.IsFaulted ? task.Exception?.GetBaseException() : null;
            Finish(call, task.IsCanceled ? "cancelled" : task.IsFaulted ? "failed" : "completed", error);
        }
        internal static void Finish(Call? call, string status, Exception? error)
        {
            if (call == null || !call.FinishOnce()) return;
            if (call.Phase == "total") Volatile.Write(ref call.Load.TotalFinished, true);
            Write("load_phase_end", Math.Max(0, Now() - call.Started), call, "status=" + status + ";unit=inclusive_wall_time;phases_may_overlap=true", error);
        }
        private static void EntityBegin(object __instance, out Call? __state) => __state = Start(__instance, "entity_restore");
        private static void EntityEnd(Call? __state, Exception? __exception) => Finish(__state, __exception == null ? "completed" : "failed", __exception);
        private static void Write(string stage, double ms, Call call, string detail, Exception? error)
        {
            try { Emit(stage, ms, "load_id=" + call.Load.Id + ";" + call.Load.Label + ";phase=" + call.Phase + ";" + detail, error); }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
    }
}
