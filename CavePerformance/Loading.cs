using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using Alta.Chunks;
using Alta.Inventory;
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
            internal LoadTransitionTiming.Load? Observation;
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
        private static MethodInfo? removeCurrentPlayer;
        private static FieldInfo? personalChunkField, beganUnloading, fullyUnloaded;
        [ThreadStatic] private static Chunk? personalCleanup;
        internal static long Drained, PersonalCleanupBypasses;
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
        private static readonly RenderTurns<Alta.Caves.GenerationBase> RenderQueue = new RenderTurns<Alta.Caves.GenerationBase>();
        internal static MethodInfo Render = null!, Dynamic = null!, NativeMax = null!;
        internal static void Bind()
        {
            BindContexts();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Render = typeof(Alta.Caves.GenerationBase).GetMethod("UpdateRendering", flags)!;
            var rendering = typeof(Alta.Caves.GenerationBase).GetField("rendering", flags);
            Dynamic = typeof(CaveLayerStreamer).GetMethod("Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic", flags, null, new[] { typeof(bool) }, null)!;
            NativeMax = typeof(Mathf).GetMethod("Max", new[] { typeof(float), typeof(float) })!;
            entities = typeof(DynamicChunkContentManager).GetMethod("LoadEntities", flags)!;
            ownerChunk = typeof(DynamicChunkContentManager).GetField("chunk", flags)!;
            forcedSave = typeof(Chunk).GetMethod("ForceUnloadAndSave", flags, null, Type.EmptyTypes, null)!;
            saveWorld = typeof(StreamerManager).GetMethod("SaveWorld", flags, null, new[] { typeof(bool) }, null)!;
            removeCurrentPlayer = typeof(PersonalChunk).GetMethod("RemoveCurrentPlayer", flags, null, Type.EmptyTypes, null);
            personalChunkField = typeof(PersonalChunk).GetField("chunk", flags);
            beganUnloading = typeof(Chunk).GetField("BeganUnloading", flags);
            fullyUnloaded = typeof(Chunk).GetField("FullyUnloaded", flags);
            if (Render == null || Render.ReturnType != typeof(void) || rendering?.FieldType != typeof(List<Alta.Caves.MapChunk>) ||
                Dynamic == null || Dynamic.ReturnType != typeof(Task) || NativeMax == null)
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
            if (removeCurrentPlayer?.ReturnType == typeof(void) && personalChunkField?.FieldType == typeof(Chunk) &&
                beganUnloading?.FieldType == typeof(Action<Chunk>) && fullyUnloaded?.FieldType == typeof(Action<Chunk>))
                h.Patch(removeCurrentPlayer, prefix: new HarmonyMethod(typeof(Loading), nameof(PersonalCleanupBegin)),
                    finalizer: new HarmonyMethod(typeof(Loading), nameof(PersonalCleanupEnd)));
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
        internal static bool RenderBegin(Alta.Caves.GenerationBase __instance, List<Alta.Caves.MapChunk> ___rendering, out BudgetCall __state)
        {
            __state = default;
            if (!Core.Ready || !Core.Main || Core.Config.GenerationMs <= 0) return true;
            int frame = Frame();
            if (budgetDepth == 0 && frame != budgetFrame) { budgetFrame = frame; spentGenerationMs = 0; }
            if (___rendering.Count == 0) { RenderQueue.Withdraw(__instance); return true; }
            if (budgetDepth == 0 && !RenderQueue.Admit(__instance, frame, spentGenerationMs >= Core.Config.GenerationMs))
            {
                bool activeScan = CaveNavigationRecovery.Core.HasActiveProceduralScan(__instance.LayerStreamer);
                RenderQueue.Defer(__instance, frame, activeScan);
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
        internal static string RenderHealth() => ";render_yields=" + RenderQueue.Deferred + ";render_fair_turns=" + RenderQueue.Granted +
            ";render_scan_turns=" + RenderQueue.ScanGranted + ";render_waiting=" + RenderQueue.Pending;
        internal static void ClearRenderQueue() => RenderQueue.Clear();

        internal sealed class RenderTurns<T> where T : class
        {
            private sealed class Turn
            {
                internal readonly T Owner;
                internal int Seen;
                internal bool ActiveScan;
                internal Turn(T owner) { Owner = owner; }
            }
            private const int Limit = 64;
            private readonly List<Turn> waiting = new List<Turn>();
            private Turn? reserved;
            private int frame;
            private bool hasFrame;
            internal int Pending => waiting.Count;
            internal long Deferred, Granted, ScanGranted;
            internal void Advance(int nextFrame)
            {
                if (hasFrame && nextFrame == frame) return;
                frame = nextFrame;
                hasFrame = true;
                reserved = null;
                // A renderer only owns a reservation for the next frame. A
                // destroyed/disabled layer or one that stopped calling us
                // cannot retain another frame of the shared render budget.
                for (int i = waiting.Count - 1; i >= 0; i--)
                    if (unchecked(nextFrame - waiting[i].Seen) != 1) waiting.RemoveAt(i);
                foreach (var turn in waiting)
                {
                    if (reserved == null || turn.ActiveScan) reserved = turn;
                    if (turn.ActiveScan) break;
                }
            }
            internal bool Admit(T owner, int frame, bool exhausted)
            {
                Advance(frame);
                if (exhausted || (reserved != null && !ReferenceEquals(reserved.Owner, owner))) return false;
                if (reserved != null)
                {
                    Granted++;
                    if (reserved.ActiveScan) ScanGranted++;
                }
                Withdraw(owner);
                return true;
            }
            internal void Defer(T owner, int frame, bool activeScan)
            {
                Deferred++;
                var turn = Find(owner);
                if (turn == null)
                {
                    if (waiting.Count >= Limit) return;
                    turn = new Turn(owner);
                    waiting.Add(turn);
                }
                turn.Seen = frame;
                turn.ActiveScan = activeScan;
            }
            internal void Withdraw(T owner)
            {
                var turn = Find(owner);
                if (turn == null) return;
                waiting.Remove(turn);
                if (ReferenceEquals(turn, reserved)) reserved = null;
            }
            private Turn? Find(T owner)
            {
                foreach (var turn in waiting)
                    if (ReferenceEquals(turn.Owner, owner)) return turn;
                return null;
            }
            internal void Clear() { waiting.Clear(); reserved = null; hasFrame = false; }
        }
        private static void DynamicBegin(object __instance, out LoadTransitionTiming.Call? __state)
        {
            __state = LoadTransitionTiming.Start(__instance, "generation_ready");
            if (Core.Ready && Core.Main) Core.Transition();
        }
        private static void DynamicEnd(ref Task __result, LoadTransitionTiming.Call? __state)
        {
            LoadTransitionTiming.Observe(__result, __state);
            if (!Core.Ready || !Core.Main || !Core.Config.SeparateLoads || __result == null) return;
            __result = AfterDynamic(__result, __state?.Load);
        }
        internal static Task AfterDynamic(Task original, LoadTransitionTiming.Load? observation = null)
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
            _ = CompleteAfterOriginal(original, invocation, completion, observation);
            return completion.Task;
        }
        private static async Task CompleteAfterOriginal(Task original, SynchronizationContext? invocation, TaskCompletionSource<bool> completion, LoadTransitionTiming.Load? observation)
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
                QueueCompletion(Frame(), invocation, completion, observation);
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
        private static void QueueCompletion(int frame, SynchronizationContext? invocation, TaskCompletionSource<bool> completion, LoadTransitionTiming.Load? observation = null)
        {
            if (synchronousDepth > 0 || !Core.Ready) { completion.TrySetResult(true); return; }
            if (!Core.Main || !CompatibleContexts(invocation, SynchronizationContext.Current))
            {
                ContextFallback("gate_creation_context_changed;no_future_gate"); completion.TrySetResult(true); return;
            }
            if (invocation != null) Volatile.Write(ref lastPostingContext, invocation);
            if (Gates.Count >= 32) { completion.TrySetResult(true); return; }
            Gates.Enqueue(new Gate(completion) { Frame = frame, Started = Core.Now, InvocationContext = invocation, Observation = observation });
        }
        internal static void Tick(int frame)
        {
            if (!Core.Main) { Interlocked.Increment(ref OffThreadDrainDeferred); return; }
            RenderQueue.Advance(frame);
            if (Gates.Count == 0 || unchecked(frame - Gates.Peek().Frame) <= 0 || (hasReleased && lastReleasedFrame == frame)) return;
            // At most one native entity batch is released per frame. A captured
            // Unity synchronization context may post its continuation rather
            // than run it inline, so a time-only release loop is insufficient.
            var gate = Gates.Dequeue(); Released++; hasReleased = true; lastReleasedFrame = frame;
            double start = Core.Now;
            Release(gate);
            Core.Row("native_entity_batch_release", Core.Now - start, "gate_wait_ms=" + Core.F(start - gate.Started) +
                ";queued=" + Gates.Count + ";unit=whole_native_entity_batch;not_preemptible=true" +
                (gate.Observation == null ? "" : ";load_id=" + gate.Observation.Id + ";" + gate.Observation.Label));
        }
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
            double start = Core.Now;
            while (Gates.Count > 0) { drained++; Release(Gates.Dequeue()); }
            Drained += drained;
            Core.Row("native_entity_batch_drain", Core.Now - start, "reason=" + reason + ";queued_before=" + queuedBefore + ";released=" + drained);
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
            // procedural streamer or unload subscriber could depend on a gate.
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
    }
}
