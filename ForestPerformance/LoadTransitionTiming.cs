using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Alta.Chunks;
using HarmonyLib;

namespace ForestPerformance
{
    // Read-only observers: native Tasks are returned unchanged. Completion
    // callbacks use only captured managed state, never Unity properties.
    internal static class LoadTransitionTiming
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly ConditionalWeakTable<Chunk, Transition> Loads = new ConditionalWeakTable<Chunk, Transition>();
        private static FieldInfo managerChunk = null!;
        private static long nextId, errors;
        private static bool installed;
        private sealed class Transition
        {
            internal long Id;
            internal string Chunk = "";
            internal double DynamicReady = -1;
            public Transition() { }
        }
        internal sealed class Call
        {
            internal readonly TransitionData Data;
            internal readonly string Stage;
            internal readonly double Started;
            internal readonly Action<double>? Completed;
            internal Call(TransitionData data, string stage, double started, Action<double>? completed = null)
            { Data = data; Stage = stage; Started = started; Completed = completed; }
        }
        internal sealed class TransitionData
        {
            internal readonly long Id;
            internal readonly string Chunk;
            internal readonly double Ready;
            internal TransitionData(long id, string chunk, double ready) { Id = id; Chunk = chunk; Ready = ready; }
        }
        internal static bool Installed => installed;
        internal static long Errors => Interlocked.Read(ref errors);
        internal static void Attach(HarmonyLib.Harmony harmony)
        {
            managerChunk = typeof(DynamicChunkContentManager).GetField("chunk", Flags) ?? throw new MissingFieldException("DynamicChunkContentManager.chunk");
            if (managerChunk.FieldType != typeof(Chunk)) throw new NotSupportedException("Forest transition manager chunk changed");
            var load = typeof(DynamicChunkContentManager).GetMethod("LoadInternal", Flags, null, Type.EmptyTypes, null);
            var file = typeof(DynamicChunkContentManager).GetMethod("LoadFromFile", Flags, null, Type.EmptyTypes, null);
            var restore = typeof(DynamicChunkContentManager).GetMethod("LoadEntities", Flags);
            var terrain = typeof(ForestChunk).GetMethod("Alta.Chunks.IProceduralGenerationStreamer.LoadStatic", Flags);
            if (load == null || file == null || restore == null || terrain == null || load.ReturnType != typeof(Task) || terrain.ReturnType != typeof(Task) || !typeof(Task).IsAssignableFrom(file.ReturnType))
                throw new NotSupportedException("Forest transition timing methods changed");
            harmony.Patch(load, prefix: H(nameof(LoadBegin)), postfix: H(nameof(TaskEnd)), finalizer: H(nameof(TaskFailed)));
            harmony.Patch(file, prefix: H(nameof(FileBegin)), postfix: H(nameof(TaskEnd)), finalizer: H(nameof(TaskFailed)));
            harmony.Patch(terrain, prefix: H(nameof(StaticBegin)), postfix: H(nameof(TaskEnd)), finalizer: H(nameof(TaskFailed)));
            harmony.Patch(restore, prefix: H(nameof(RestoreBegin)), finalizer: H(nameof(RestoreEnd)));
            installed = true;
        }
        private static HarmonyMethod H(string name) => new HarmonyMethod(typeof(LoadTransitionTiming), name);
        private static TransitionData Snapshot(Transition item) => new TransitionData(item.Id, item.Chunk, Volatile.Read(ref item.DynamicReady));
        private static Transition For(Chunk chunk, bool fresh)
        {
            Transition item = Loads.GetOrCreateValue(chunk);
            if (fresh || item.Id == 0)
            {
                item.Id = Interlocked.Increment(ref nextId);
                item.Chunk = chunk.ChunkIdentifier ?? "";
                item.DynamicReady = -1;
            }
            return item;
        }
        private static Chunk? ForestManager(object manager)
        {
            if (!Core.Ready || !Core.Main || !Core.Config.Diagnostics) return null;
            var chunk = managerChunk.GetValue(manager) as Chunk;
            return chunk?.ProceduralStreamer is ForestChunk ? chunk : null;
        }
        private static Call? BeginManager(object manager, string stage, bool fresh = false)
        {
            try
            {
                Chunk? chunk = ForestManager(manager);
                return chunk == null ? null : new Call(Snapshot(For(chunk, fresh)), stage, Core.Now);
            }
            catch { Interlocked.Increment(ref errors); return null; }
        }
        internal static Call BeginStreamer(ForestChunk streamer, string stage)
        {
            try
            {
                if (!installed || !Core.Ready || !Core.Main || !Core.Config.Diagnostics || ReferenceEquals(streamer.Chunk, null)) return null!;
                Transition item = For(streamer.Chunk, false);
                TransitionData data = Snapshot(item);
                return new Call(data, stage, Core.Now,
                    stage == "forest_dynamic_ready" ? (Action<double>)(at =>
                    {
                        if (Volatile.Read(ref item.Id) == data.Id) Volatile.Write(ref item.DynamicReady, at);
                    }) : null);
            }
            catch { Interlocked.Increment(ref errors); return null!; }
        }
        private static void LoadBegin(object __instance, out Call __state) => __state = BeginManager(__instance, "forest_load_total", true)!;
        private static void FileBegin(object __instance, out Call __state) => __state = BeginManager(__instance, "forest_file_ready")!;
        private static void StaticBegin(ForestChunk __instance, out Call __state) => __state = BeginStreamer(__instance, "forest_static_ready");
        private static void RestoreBegin(object __instance, out Call __state) => __state = BeginManager(__instance, "forest_entity_restore")!;
        private static void TaskEnd(Task __result, Call __state) => Observe(__result, __state);
        private static Exception? TaskFailed(Exception? __exception, Call __state)
        {
            if (__exception != null) Complete(__state, "threw", __exception);
            return __exception;
        }
        internal static void Observe(Task? task, Call? state)
        {
            if (state == null || task == null) return;
            try
            {
                _ = task.ContinueWith(done =>
                {
                    // Exception access observes an error for logging, without
                    // altering the native Task or suppressing native handling.
                    Exception? error = done.IsFaulted ? done.Exception?.InnerException : null;
                    Complete(state, done.IsCanceled ? "cancelled" : done.IsFaulted ? "faulted" : "completed", error);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            catch { Interlocked.Increment(ref errors); }
        }
        private static Exception? RestoreEnd(Exception? __exception, Call __state)
        {
            Complete(__state, __exception == null ? "completed" : "threw", __exception);
            return __exception;
        }
        private static void Complete(Call? call, string outcome, Exception? error)
        {
            if (call == null) return;
            try
            {
                double at = Core.Now;
                if (outcome == "completed") call.Completed?.Invoke(at);
                string detail = "load_id=" + call.Data.Id + ";chunk=" + call.Data.Chunk + ";outcome=" + outcome +
                    ";unit=" + (call.Stage == "forest_entity_restore" ? "synchronous_native_restore" : "inclusive_task_wall_time");
                if (call.Stage == "forest_entity_restore" && call.Data.Ready >= 0)
                    detail += ";since_dynamic_ready_ms=" + Core.F(Math.Max(0, call.Started - call.Data.Ready));
                Core.Row(call.Stage, Math.Max(0, at - call.Started), detail, error);
            }
            catch { Interlocked.Increment(ref errors); }
        }
        internal static void Shutdown() { installed = false; }
    }
}
