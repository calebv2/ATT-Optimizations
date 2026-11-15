using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;

namespace CavePerformance
{
    internal static class Timing
    {
        internal struct Call { internal double Started; internal string? Detail; internal Delegate? Callback; internal object? State; }
        private static readonly Dictionary<MethodBase, string> Targets = new Dictionary<MethodBase, string>();
        private static MethodInfo? coroutine;
        internal static MethodInfo? WorkInvoke;
        private static long nextMainStack, nextWorkerStack;
        internal static void Bind()
        {
            Targets.Clear();
            var root = typeof(AstarPath).Assembly;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            void Add(string type, string name)
            {
                var owner = root.GetType(type, true)!;
                foreach (var m in owner.GetMethods(flags))
                    if (m.Name == name && m.GetMethodBody() != null && !m.IsAbstract) Targets[m] = type + "." + name;
            }
            Add("Alta.Networking.NetworkEntity", "InitializeWithChildrenAsServer");
            Add("Alta.Chunks.DynamicChunkContentManager", "LoadEntities");
            Add("Alta.Chunks.StreamerHelper", "LoadSetupRecursive");
            Add("Alta.Networking.ServerSpawnManager", "SpawnInternal");
            Add("Alta.Caves.GenerationBase", "UpdateRendering");
            foreach (string pathType in new[] { "Pathfinding.ABPath", "Pathfinding.ConstantPath" })
                foreach (string stage in new[] { "Initialize", "CalculateStep", "Cleanup" }) Add(pathType, stage);
            var setup = typeof(UnityEngine.Application).Assembly.GetType("UnityEngine.SetupCoroutine", false);
            coroutine = setup?.GetMethod("InvokeMoveNext", flags, null, new[] { typeof(IEnumerator), typeof(IntPtr) }, null);
            var work = typeof(UnityEngine.Application).Assembly.GetType("UnityEngine.UnitySynchronizationContext+WorkRequest", false);
            WorkInvoke = work?.GetMethod("Invoke", flags, null, Type.EmptyTypes, null);
            if (WorkInvoke != null && (work!.GetField("m_DelagateCallback", flags)?.FieldType != typeof(SendOrPostCallback) || work.GetField("m_DelagateState", flags) == null))
                throw new NotSupportedException("Unity task callback layout changed");
            if (Targets.Count < 4) throw new MissingMethodException("Transition timing boundaries unavailable");
        }
        internal static void Attach(HarmonyLib.Harmony h)
        {
            foreach (var target in Targets.Keys)
                h.Patch(target, prefix: new HarmonyMethod(typeof(Timing), nameof(Begin)), finalizer: new HarmonyMethod(typeof(Timing), nameof(End)));
            if (coroutine != null && coroutine.GetMethodBody() != null)
                h.Patch(coroutine, prefix: new HarmonyMethod(typeof(Timing), nameof(CoroutineBegin)), finalizer: new HarmonyMethod(typeof(Timing), nameof(End)));
            if (WorkInvoke != null && WorkInvoke.GetMethodBody() != null)
                h.Patch(WorkInvoke, prefix: new HarmonyMethod(typeof(Timing), nameof(WorkBegin)), finalizer: new HarmonyMethod(typeof(Timing), nameof(End)));
        }
        private static void Begin(MethodBase __originalMethod, out Call __state)
        {
            __state = default;
            if (!Core.InTransition || !Core.Main || !Core.Config.Timing) return;
            __state = new Call { Started = Core.Now, Detail = Targets[__originalMethod] };
        }
        private static void CoroutineBegin(IEnumerator __0, out Call __state)
        {
            __state = default;
            if (!Core.InTransition || !Core.Main || !Core.Config.Timing || __0 == null) return;
            __state = new Call { Started = Core.Now, Detail = "unity_coroutine=" + __0.GetType().FullName };
        }
        private static void End(Call __state, Exception? __exception)
        {
            if (__state.Started == 0) return;
            try
            {
            double elapsed = Core.Now - __state.Started;
            bool path = __state.Detail?.StartsWith("Pathfinding.", StringComparison.Ordinal) == true;
            if (elapsed >= (path ? 5 : 25) || __exception != null)
            {
                string detail = __state.Detail ?? "";
                if (__state.Callback != null)
                {
                    detail += ";callback=" + Name(__state.Callback) + ";state_type=" + __state.State?.GetType().FullName;
                    if (__state.State is Delegate action)
                    {
                        detail += ";continuation=" + Name(action);
                        object? target = action.Target;
                        if (target != null)
                        {
                            // Identify the async state-machine TYPE only; never
                            // stringify arbitrary state/player objects.
                            var field = target.GetType().GetField("m_stateMachine", BindingFlags.Instance | BindingFlags.NonPublic);
                            if (field != null) detail += ";async_state_machine=" + field.GetValue(target)?.GetType().FullName;
                        }
                    }
                }
                Core.Row("transition_call", elapsed, detail + StackIfSlow(elapsed), __exception);
            }
            }
            catch { Interlocked.Increment(ref Core.Errors); }
        }
        private static string Name(Delegate callback) => callback.Method.DeclaringType?.FullName + "." + callback.Method.Name;
        private static void WorkBegin(SendOrPostCallback ___m_DelagateCallback, object ___m_DelagateState, out Call __state)
        {
            __state = default;
            if (!Core.InTransition || !Core.Main || !Core.Config.Timing) return;
            __state = new Call { Started = Core.Now, Detail = "unity_task_callback", Callback = ___m_DelagateCallback, State = ___m_DelagateState };
        }
        internal static string StackIfSlow(double ms)
        {
            try
            {
            if (ms < 100 || !Core.Config.Timing || Core.Trace == null) return "";
            long now = Stopwatch.GetTimestamp();
            if (Core.Main ? !TakeStack(ref nextMainStack, now) : !TakeStack(ref nextWorkerStack, now)) return "";
            string stack = new StackTrace(2, false).ToString().Replace('\r', ' ').Replace('\n', '|');
            return ";managed_caller_stack=" + (stack.Length > 2500 ? stack.Substring(0, 2500) : stack);
            }
            catch { Interlocked.Increment(ref Core.Errors); return ";managed_caller_stack=unavailable"; }
        }
        private static bool TakeStack(ref long slot, long now)
        {
            long due = Interlocked.Read(ref slot);
            return now >= due && Interlocked.CompareExchange(ref slot, now + 15 * Stopwatch.Frequency, due) == due;
        }
    }
}
