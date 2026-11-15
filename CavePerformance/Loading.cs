using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Alta.Chunks;
using HarmonyLib;
using UnityEngine;

namespace CavePerformance
{
    internal static class Loading
    {
        internal sealed class Gate
        {
            internal readonly TaskCompletionSource<bool> Done;
            internal Gate(TaskCompletionSource<bool> done) { Done = done; }
            internal int Frame;
            internal double Started;
            internal SynchronizationContext? InvocationContext;
        }
        private static readonly Queue<Gate> Gates = new Queue<Gate>();
        [ThreadStatic] internal static CaveLayerStreamer? CurrentCave;
        private static FieldInfo ownerChunk = null!;
        private static MethodInfo entities = null!;
        internal static Func<int> Frame = () => Time.frameCount;
        internal static int Pending => Gates.Count;
        internal static long Released;
        private static int lastReleasedFrame;
        private static bool hasReleased;
        private static int synchronousDepth;
        private static MethodInfo forcedSave = null!, saveWorld = null!;
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
        internal struct BudgetCall { internal double Started; internal bool Active; }
        private static int budgetFrame = int.MinValue, budgetDepth;
        private static double spentGenerationMs;
        internal static MethodInfo Render = null!, Dynamic = null!, NativeMax = null!;
        internal static void Bind()
        {
            BindContexts();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Render = typeof(Alta.Caves.GenerationBase).GetMethod("UpdateRendering", flags)!;
            Dynamic = typeof(CaveLayerStreamer).GetMethod("Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic", flags, null, new[] { typeof(bool) }, null)!;
            NativeMax = typeof(Mathf).GetMethod("Max", new[] { typeof(float), typeof(float) })!;
            entities = typeof(DynamicChunkContentManager).GetMethod("LoadEntities", flags)!;
            ownerChunk = typeof(DynamicChunkContentManager).GetField("chunk", flags)!;
            forcedSave = typeof(Chunk).GetMethod("ForceUnloadAndSave", flags, null, Type.EmptyTypes, null)!;
            saveWorld = typeof(StreamerManager).GetMethod("SaveWorld", flags, null, new[] { typeof(bool) }, null)!;
            if (Render == null || Render.ReturnType != typeof(void) || Dynamic == null || Dynamic.ReturnType != typeof(Task) || NativeMax == null)
                throw new NotSupportedException("Cave rendering/loading signature changed");
            if (entities == null || ownerChunk == null || ownerChunk.FieldType != typeof(Chunk)) throw new NotSupportedException("Entity-load ownership changed");
            if (forcedSave == null || saveWorld == null) throw new MissingMethodException("Synchronous chunk/world save boundary unavailable");
        }
        internal static void Attach(HarmonyLib.Harmony h)
        {
            h.Patch(Render, prefix: new HarmonyMethod(typeof(Loading), nameof(RenderBegin)),
                finalizer: new HarmonyMethod(typeof(Loading), nameof(RenderEnd)), transpiler: new HarmonyMethod(typeof(Loading), nameof(CapRenderBudget)));
            h.Patch(Dynamic, prefix: new HarmonyMethod(typeof(Loading), nameof(DynamicBegin)), postfix: new HarmonyMethod(typeof(Loading), nameof(DynamicEnd)));
            h.Patch(entities, prefix: new HarmonyMethod(typeof(Loading), nameof(EntitiesBegin)), finalizer: new HarmonyMethod(typeof(Loading), nameof(EntitiesEnd)));
            h.Patch(forcedSave, prefix: new HarmonyMethod(typeof(Loading), nameof(ForcedBegin)), finalizer: new HarmonyMethod(typeof(Loading), nameof(SaveEnd)));
            h.Patch(saveWorld, prefix: new HarmonyMethod(typeof(Loading), nameof(SaveBegin)), finalizer: new HarmonyMethod(typeof(Loading), nameof(SaveEnd)));
        }
        internal static IEnumerable<CodeInstruction> CapRenderBudget(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList(); int count = 0;
            foreach (var i in list)
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo method && method.Equals(NativeMax))
                { i.opcode = OpCodes.Call; i.operand = typeof(Loading).GetMethod(nameof(ClampBudget), BindingFlags.Static | BindingFlags.NonPublic); count++; }
            if (count != 1) throw new NotSupportedException("Expected one native render budget expression; found " + count);
            return list;
        }
        internal static float ClampBudget(float floor, float native)
        {
            float original = Math.Max(floor, native);
            return Core.Ready && Core.Main && Core.Config.GenerationMs > 0 ? Math.Min(original, Math.Max(floor, Core.Config.GenerationMs - (float)spentGenerationMs)) : original;
        }
        internal static bool RenderBegin(Alta.Caves.GenerationBase __instance, out BudgetCall __state)
        {
            __state = default;
            if (!Core.Ready || !Core.Main || Core.Config.GenerationMs <= 0) return true;
            int frame = Frame();
            if (budgetDepth == 0 && frame != budgetFrame) { budgetFrame = frame; spentGenerationMs = 0; }
            if (budgetDepth == 0 && spentGenerationMs >= Core.Config.GenerationMs)
            {
                // The native method normally releases this scheduler owner at
                // its tail. A deferred/disabled generator must not retain it.
                if (ReferenceEquals(Alta.Caves.GenerationBase.CurrentlyRendering, __instance)) Alta.Caves.GenerationBase.CurrentlyRendering = null;
                return false;
            }
            __state = new BudgetCall { Started = Core.Now, Active = true }; budgetDepth++;
            return true;
        }
        internal static void RenderEnd(BudgetCall __state)
        {
            if (!__state.Active) return;
            if (--budgetDepth == 0) spentGenerationMs += Core.Now - __state.Started;
        }
        private static void DynamicBegin()
        {
            if (Core.Ready && Core.Main) Core.Transition();
        }
        private static void DynamicEnd(ref Task __result)
        {
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
        private static void EntitiesBegin(DynamicChunkContentManager __instance, out CaveLayerStreamer? __state)
        {
            __state = CurrentCave;
            if (!Core.Ready || !Core.Main) return;
            CurrentCave = null;
            try { if (ownerChunk.GetValue(__instance) is Chunk chunk) CurrentCave = chunk.ProceduralStreamer as CaveLayerStreamer; }
            catch { System.Threading.Interlocked.Increment(ref Core.Errors); }
        }
        private static void EntitiesEnd(CaveLayerStreamer? __state) => CurrentCave = __state;
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
            if (Gates.Count >= 32) { completion.TrySetResult(true); return; }
            Gates.Enqueue(new Gate(completion) { Frame = frame, Started = Core.Now, InvocationContext = invocation });
        }
        internal static void Tick(int frame)
        {
            if (!Core.Main) { Interlocked.Increment(ref OffThreadDrainDeferred); return; }
            if (Gates.Count == 0 || unchecked(frame - Gates.Peek().Frame) <= 0 || (hasReleased && lastReleasedFrame == frame)) return;
            // At most one native entity batch is released per frame. A captured
            // Unity synchronization context may post its continuation rather
            // than run it inline, so a time-only release loop is insufficient.
            var gate = Gates.Dequeue(); Released++; hasReleased = true; lastReleasedFrame = frame;
            double start = Core.Now;
            Release(gate);
            Core.Row("native_entity_batch_release", Core.Now - start, "gate_wait_ms=" + Core.F(start - gate.Started) +
                ";queued=" + Gates.Count + ";unit=whole_native_entity_batch;not_preemptible=true");
        }
        internal static void Drain()
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
                        if (Core.Main) Drain();
                        else Interlocked.Increment(ref OffThreadDrainDeferred);
                    }, null);
                }
                catch { Interlocked.Increment(ref OffThreadDrainDeferred); }
                return;
            }
            while (Gates.Count > 0) Release(Gates.Dequeue());
        }
        private static void ForcedBegin(out bool __state) => BeginSynchronous(true, out __state);
        private static void SaveBegin(bool isInstant, out bool __state) => BeginSynchronous(isInstant, out __state);
        internal static void BeginSynchronous(bool instant, out bool entered)
        {
            entered = instant && Core.Ready && Core.Main;
            if (!entered) return;
            synchronousDepth++;
            Drain();
        }
        internal static void SaveEnd(bool __state) { if (__state) synchronousDepth--; }
    }
}
