using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alta.Chunks;
using Alta.Forests;
using Alta.Inventory;
using Alta.Static;
using HarmonyLib;
using UnityEngine;

namespace ForestPerformance
{
    internal static class Loading
    {
        private sealed class Gate
        {
            internal readonly TaskCompletionSource<bool> Done;
            internal Gate(TaskCompletionSource<bool> done) { Done = done; }
            internal int Frame;
            internal double Started;
            internal SynchronizationContext? InvocationContext;
        }
        private static readonly Queue<Gate> Gates = new Queue<Gate>();
        internal static Func<int> Frame = () => Time.frameCount;
        internal static MethodInfo Factory = null!, Dynamic = null!, Step = null!;
        private static MethodInfo forcedSave = null!, saveWorld = null!;
        private static MethodInfo? removeCurrentPlayer;
        private static FieldInfo? personalChunkField, beganUnloading, fullyUnloaded;
        [ThreadStatic] private static Chunk? personalCleanup;
        private static FieldInfo context = null!;
        private static int lastFrame, synchronousDepth;
        private static bool releasedInFrame;
        internal static long Adjusted, Released, Overflow;
        internal static long Drained, PersonalCleanupBypasses;
        internal static int Pending => Gates.Count;
        private static Type? unityContextType;
        private static FieldInfo? unityContextMain, unityContextQueue;
        private static SynchronizationContext? lastPostingContext;
        internal static Func<SynchronizationContext?, SynchronizationContext?, bool> CompatibleContexts = NativeContextsCompatible;
        internal static long ContextFallbacks, OffThreadDrainRequests, OffThreadDrainDeferred;
        private static void BindContexts()
        {
            unityContextType = typeof(Application).Assembly.GetType("UnityEngine.UnitySynchronizationContext", true);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            unityContextMain = unityContextType!.GetField("m_MainThreadID", flags);
            unityContextQueue = unityContextType.GetField("m_AsyncWorkQueue", flags);
            if (unityContextMain?.FieldType != typeof(int) || unityContextQueue == null)
                throw new NotSupportedException("Unity load-gate synchronization context layout changed");
            CompatibleContexts = NativeContextsCompatible;
        }
        internal static bool NativeContextsCompatible(SynchronizationContext? a, SynchronizationContext? b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.GetType() == unityContextType && b.GetType() == unityContextType &&
                (int)unityContextMain!.GetValue(a) == Core.MainThread && (int)unityContextMain.GetValue(b) == Core.MainThread &&
                ReferenceEquals(unityContextQueue!.GetValue(a), unityContextQueue.GetValue(b));
        }
        private static void ContextFallback(string reason)
        {
            Interlocked.Increment(ref ContextFallbacks);
            Core.Row("load_gate_context_fallback", 0, reason);
        }
        private static void Release(Gate gate)
        {
            // Physical-main-thread callers only. A copied Unity context shares
            // the same queue but has different identity; restoring the invocation
            // instance lets native awaiters inline instead of posting behind a
            // synchronous save. No unrelated queued callback is pumped.
            var previous = SynchronizationContext.Current;
            bool compatible = gate.InvocationContext == null || CompatibleContexts(gate.InvocationContext, previous);
            if (!compatible)
            {
                ContextFallback("release_context_changed;no_context_impersonation");
                gate.Done.TrySetResult(true); return;
            }
            try
            {
                if (!ReferenceEquals(previous, gate.InvocationContext)) SynchronizationContext.SetSynchronizationContext(gate.InvocationContext);
                gate.Done.TrySetResult(true);
            }
            finally
            {
                if (!ReferenceEquals(SynchronizationContext.Current, previous)) SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
        internal struct StepCall { internal double Started; }
        internal static void Bind()
        {
            BindContexts();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Factory = typeof(InstructionSet).GetMethod("ExecuteInternal", flags, null, new[] { typeof(object), typeof(Task), typeof(int) }, null)
                ?? throw new MissingMethodException("InstructionSet.ExecuteInternal");
            Dynamic = typeof(ForestChunk).GetMethod("Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic", flags, null, new[] { typeof(bool) }, null)
                ?? throw new MissingMethodException("ForestChunk.LoadDynamic");
            var iterators = Array.FindAll(typeof(InstructionSet).GetNestedTypes(flags), t => t.Name.StartsWith("<ExecuteInternal>d__", StringComparison.Ordinal));
            if (iterators.Length != 1 || Factory.ReturnType != typeof(IEnumerator) || Dynamic.ReturnType != typeof(Task)) throw new NotSupportedException("Forest loading signature changed");
            Step = iterators[0].GetMethod("MoveNext", flags) ?? throw new MissingMethodException("InstructionSet iterator.MoveNext");
            context = iterators[0].GetField("context", flags) ?? throw new MissingFieldException("InstructionSet iterator.context");
            if (context.FieldType != typeof(object)) throw new NotSupportedException("Forest instruction context changed");
            forcedSave = typeof(Chunk).GetMethod("ForceUnloadAndSave", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException("Chunk.ForceUnloadAndSave");
            saveWorld = typeof(StreamerManager).GetMethod("SaveWorld", flags, null, new[] { typeof(bool) }, null) ?? throw new MissingMethodException("StreamerManager.SaveWorld");
            removeCurrentPlayer = typeof(PersonalChunk).GetMethod("RemoveCurrentPlayer", flags, null, Type.EmptyTypes, null);
            personalChunkField = typeof(PersonalChunk).GetField("chunk", flags);
            beganUnloading = typeof(Chunk).GetField("BeganUnloading", flags);
            fullyUnloaded = typeof(Chunk).GetField("FullyUnloaded", flags);
        }
        internal static void Attach(HarmonyLib.Harmony harmony)
        {
            harmony.Patch(Factory, prefix: new HarmonyMethod(typeof(Loading), nameof(Cap)),
                postfix: new HarmonyMethod(typeof(Loading), nameof(ShareBudget)));
            harmony.Patch(Step, prefix: new HarmonyMethod(typeof(Loading), nameof(StepBegin)),
                transpiler: new HarmonyMethod(typeof(ForestInstructionBatching), nameof(ForestInstructionBatching.CountEveryInstruction)),
                finalizer: new HarmonyMethod(typeof(Loading), nameof(StepEnd)));
            harmony.Patch(Dynamic, prefix: new HarmonyMethod(typeof(Loading), nameof(DynamicBegin)),
                postfix: new HarmonyMethod(typeof(Loading), nameof(DynamicEnd)));
            harmony.Patch(forcedSave, prefix: new HarmonyMethod(typeof(Loading), nameof(ForcedBegin)), finalizer: new HarmonyMethod(typeof(Loading), nameof(SaveEnd)));
            harmony.Patch(saveWorld, prefix: new HarmonyMethod(typeof(Loading), nameof(SaveBegin)), finalizer: new HarmonyMethod(typeof(Loading), nameof(SaveEnd)));
            if (removeCurrentPlayer?.ReturnType == typeof(void) && personalChunkField?.FieldType == typeof(Chunk) &&
                beganUnloading?.FieldType == typeof(Action<Chunk>) && fullyUnloaded?.FieldType == typeof(Action<Chunk>))
                harmony.Patch(removeCurrentPlayer, prefix: new HarmonyMethod(typeof(Loading), nameof(PersonalCleanupBegin)),
                    finalizer: new HarmonyMethod(typeof(Loading), nameof(PersonalCleanupEnd)));
        }
        internal static void Cap(object __0, ref int __2, bool __runOriginal)
        {
            // The original iterator owns ordering, results, reader state, yields
            // and completion. Only lower its existing batch parameter for forest
            // generation contexts. Never split an instruction or an entity restore.
            int limit = Core.Config.InstructionsPerFrame;
            if (!__runOriginal || !Core.Ready || !Core.Main || !(__0 is ForestGenerator) || limit == 0 || __2 <= limit) return;
            __2 = limit; Adjusted++;
        }
        internal static void ShareBudget(object __0, ref IEnumerator __result, bool __runOriginal)
        {
            if (__runOriginal && Core.Ready && Core.Main && Core.Config.GenerationBudgetMs > 0 &&
                Core.Config.InstructionsPerFrame > 0 && __0 is ForestGenerator && __result != null)
                __result = ForestGenerationBudget.Wrap(__result);
        }
        private static void StepBegin(object __instance, out StepCall __state)
        {
            __state = default;
            if (Core.Ready && Core.Main && Core.Config.Diagnostics && context.GetValue(__instance) is ForestGenerator) __state.Started = Core.Now;
        }
        private static void StepEnd(StepCall __state, Exception? __exception)
        {
            if (__state.Started == 0) return;
            double ms = Core.Now - __state.Started;
            if (ms >= 10 || __exception != null) Core.Row("forest_instruction_step", ms, "complete_native_instruction_batch;limit=" + Core.Config.InstructionsPerFrame, __exception);
        }
        private static void DynamicBegin(ForestChunk __instance, out LoadTransitionTiming.Call __state) => __state = LoadTransitionTiming.BeginStreamer(__instance, "forest_dynamic_ready");
        private static void DynamicEnd(ref Task __result, LoadTransitionTiming.Call __state)
        {
            LoadTransitionTiming.Observe(__result, __state);
            if (!Core.Ready || !Core.Main || !Core.Config.SeparateLoads || __result == null) return;
            __result = AfterDynamic(__result);
        }
        internal static Task AfterDynamic(Task original)
        {
            // Capture at invocation, before awaiting the original task. Capturing
            // only when it completes can retain an ExecutionContext copy instead
            // of the context the native caller awaits this wrapper under.
            var invocation = SynchronizationContext.Current;
            if (!CompatibleContexts(invocation, invocation))
            {
                ContextFallback("unsupported_invocation_context;native_task_retained"); return original;
            }
            if (Core.Main && invocation != null) Volatile.Write(ref lastPostingContext, invocation);
            var completion = new TaskCompletionSource<bool>();
            _ = CompleteAfterOriginal(original, invocation, completion);
            return completion.Task;
        }
        private static async Task CompleteAfterOriginal(Task original, SynchronizationContext? invocation, TaskCompletionSource<bool> completion)
        {
            // This helper only observes original generation. The Task returned
            // to native LoadInternal is the gate's own completion source, with
            // no intermediary awaiter whose scheduler could defer native work
            // behind a synchronous save or onto a worker.
            try
            {
                await original;
                if (!Core.Ready || !Core.Main || !Core.Config.SeparateLoads)
                { completion.TrySetResult(true); return; }
                if (!CompatibleContexts(invocation, SynchronizationContext.Current))
                {
                    ContextFallback("original_completion_context_changed;native_continuation_retained");
                    completion.TrySetResult(true); return;
                }
                QueueCompletion(Frame(), invocation, completion);
            }
            catch (OperationCanceledException error) { completion.TrySetCanceled(error.CancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        }
        internal static Task NextFrame(int frame)
        {
            var completion = new TaskCompletionSource<bool>();
            QueueCompletion(frame, SynchronizationContext.Current, completion);
            return completion.Task;
        }
        private static void QueueCompletion(int frame, SynchronizationContext? invocation, TaskCompletionSource<bool> completion)
        {
            if (synchronousDepth > 0 || !Core.Ready) { completion.TrySetResult(true); return; }
            if (!Core.Main || !CompatibleContexts(invocation, SynchronizationContext.Current))
            {
                ContextFallback("gate_creation_context_changed;no_future_gate"); completion.TrySetResult(true); return;
            }
            if (invocation != null) Volatile.Write(ref lastPostingContext, invocation);
            if (Gates.Count >= 32)
            {
                Overflow++;
                double before = Core.Now;
                completion.TrySetResult(true);
                Core.Row("native_entity_batch_overflow", Core.Now - before, "queue_limit=32");
                return;
            }
            Gates.Enqueue(new Gate(completion) { Frame = frame, Started = Core.Now, InvocationContext = invocation });
        }
        internal static void Tick(int frame)
        {
            if (!Core.Main) { Interlocked.Increment(ref OffThreadDrainDeferred); return; }
            if (Gates.Count == 0 || unchecked(frame - Gates.Peek().Frame) <= 0 || (releasedInFrame && lastFrame == frame)) return;
            var gate = Gates.Dequeue(); lastFrame = frame; releasedInFrame = true; Released++;
            double before = Core.Now;
            Release(gate);
            Core.Row("native_entity_batch_release", Core.Now - before, "gate_wait_ms=" + (before - gate.Started).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + ";queued=" + Gates.Count + ";whole_native_batch;not_preemptible=true");
        }
        internal static void PersonalCleanupBegin(PersonalChunk __instance, out Chunk? __state)
        {
            __state = personalCleanup;
            personalCleanup = Core.Ready && Core.Main ? personalChunkField?.GetValue(__instance) as Chunk : null;
        }
        internal static void PersonalCleanupEnd(Chunk? __state) => personalCleanup = __state;
        internal static void ForcedBegin(Chunk __instance, out bool __state)
        {
            // The stock personal chunk waits on its own content manager. A
            // procedural streamer or unload subscriber may introduce a dependency
            // on another chunk, so retain the global drain in either case.
            bool personal = personalCleanup is not null && ReferenceEquals(personalCleanup, __instance);
            if (Core.Ready && Core.Main && personal && __instance.GetType() == typeof(Chunk) &&
                ReferenceEquals(__instance.ProceduralStreamer, null) && __instance.ContentManager?.GetType() == typeof(ServerChunkContentManager) &&
                beganUnloading != null && beganUnloading.GetValue(__instance) == null && fullyUnloaded != null && fullyUnloaded.GetValue(__instance) == null)
            {
                __state = false;
                if (Gates.Count > 0)
                {
                    PersonalCleanupBypasses++;
                    Core.Row("native_entity_batch_drain_skipped", 0, "reason=personal_chunk_cleanup;queued=" + Gates.Count);
                }
                return;
            }
            BeginSynchronous(true, out __state, personal ? "personal_chunk_forced_unload_save" : "forced_unload_save");
        }
        private static void SaveBegin(bool isInstant, out bool __state) => BeginSynchronous(isInstant, out __state, "instant_world_save");
        internal static void BeginSynchronous(bool instant, out bool entered, string reason = "explicit_synchronous_save")
        {
            entered = instant && Core.Ready && Core.Main;
            if (!entered) return;
            synchronousDepth++;
            Drain(reason);
        }
        internal static void SaveEnd(bool __state) { if (__state) synchronousDepth--; }
        internal static void Drain() => Drain("shutdown_or_explicit");
        private static void Drain(string reason)
        {
            if (!Core.Main)
            {
                // Never touch the main-owned queue or impersonate Unity on a
                // shutdown/worker thread. Posting is managed and thread-safe for
                // the validated Unity context. Otherwise leave gates pending.
                Interlocked.Increment(ref OffThreadDrainRequests);
                var posting = Volatile.Read(ref lastPostingContext);
                if (posting == null || !CompatibleContexts(posting, posting))
                { Interlocked.Increment(ref OffThreadDrainDeferred); return; }
                try
                {
                    posting.Post(_ =>
                    {
                        if (Core.Main) Drain(reason);
                        else Interlocked.Increment(ref OffThreadDrainDeferred);
                    }, null);
                }
                catch { Interlocked.Increment(ref OffThreadDrainDeferred); }
                return;
            }
            if (Gates.Count == 0) return;
            int queuedBefore = Gates.Count, drained = 0;
            double before = Core.Now;
            while (Gates.Count > 0) { drained++; Release(Gates.Dequeue()); }
            Drained += drained;
            Core.Row("native_entity_batch_drain", Core.Now - before, "reason=" + reason + ";queued_before=" + queuedBefore + ";released=" + drained);
        }
        internal static string Health() => "instruction_batches_adjusted=" + Adjusted + ";native_load_batches_released=" + Released + ";native_load_batches_drained=" + Drained + ";personal_cleanup_drain_bypasses=" + PersonalCleanupBypasses + ";pending_load_gates=" + Gates.Count + ";gate_overflow=" + Overflow + ";context_fallbacks=" + ContextFallbacks + ";offthread_drain_requests=" + OffThreadDrainRequests + ";offthread_drain_deferred=" + OffThreadDrainDeferred + ";" + ForestGenerationBudget.Health();
    }
}
