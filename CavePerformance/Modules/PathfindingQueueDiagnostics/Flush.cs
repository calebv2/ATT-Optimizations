using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace PathfindingQueueDiagnostics
{
    internal static class Flush
    {
        internal static readonly Dictionary<MethodBase, string> Stages = new Dictionary<MethodBase, string>();
        internal static MethodInfo LockMethod = null!, GraphMethod = null!;
        private static readonly MethodInfo SleepTarget = typeof(Thread).GetMethod("Sleep", new[] { typeof(int) })!;
        private static readonly MethodInfo WaitTarget = typeof(WaitHandle).GetMethod("WaitOne", Type.EmptyTypes)!;
        internal static void Bind()
        {
            var root = typeof(AstarPath).Assembly;
            Type Type(string name) => root.GetType("Pathfinding." + name, true)!;
            void Add(Type t, string name, string stage, params Type[] args) => Stages[Core.Method(t, name, args)] = stage;
            Add(typeof(AstarPath), "FlushWorkItems", "flush");
            Add(typeof(AstarPath), "FlushWorkItems", "flush", typeof(bool), typeof(bool));
            Add(Type("PathProcessor"), "Lock", "path_lock_acquire", typeof(bool));
            Add(Type("PathProcessor"), "Unlock", "path_lock_release", typeof(int));
            Add(Type("PathProcessor"), "TickNonMultithreaded", "path_calculation_tick");
            Add(Type("PathReturnQueue"), "ReturnPaths", "path_callbacks", typeof(bool));
            Add(Type("WorkItemProcessor"), "ProcessWorkItems", "work_items", typeof(bool));
            Add(Type("GraphUpdateProcessor"), "ProcessGraphUpdates", "graph_update_total", typeof(bool));
            Add(Type("GraphUpdateProcessor"), "ProcessRegularUpdates", "graph_regular_execution", typeof(bool));
            Add(Type("GraphUpdateProcessor"), "ProcessPostUpdates", "graph_post_execution");
            Add(Type("GraphUpdateProcessor"), "FloodFill", "graph_flood_fill");
            var guo = Type("GraphUpdateObject");
            foreach (string name in new[] { "UpdateAreaInit", "UpdateArea", "UpdateAreaPost" })
            {
                var method = Type("RecastGraph").GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Single(m => m.Name.EndsWith("IUpdatableGraph." + name, StringComparison.Ordinal) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == guo);
                Stages[method] = "recast_" + name;
            }
            LockMethod = Core.Method(Type("PathProcessor"), "Lock", typeof(bool));
            GraphMethod = Core.Method(Type("GraphUpdateProcessor"), "ProcessGraphUpdates", typeof(bool));
        }
        internal static void Attach(HarmonyLib.Harmony harmony, string file)
        {
            foreach (var stage in Stages)
            {
                harmony.Patch((MethodInfo)stage.Key, prefix: new HarmonyMethod(typeof(Flush), nameof(Begin)), finalizer: new HarmonyMethod(typeof(Flush), nameof(End)),
                    transpiler: stage.Key == LockMethod ? new HarmonyMethod(typeof(Flush), nameof(ReplaceSleep)) : stage.Key == GraphMethod ? new HarmonyMethod(typeof(Flush), nameof(ReplaceWait)) : null);
                File.AppendAllText(file, ServerTrace.Row(stage.Key.DeclaringType!.FullName + "." + stage.Key.Name, "attached:" + stage.Value) + "\n");
            }
        }
        internal static void Begin(MethodBase __originalMethod, out Span? __state)
        {
            __state = null;
            if (!Core.Ready || Core.DisableRequested) return;
            try
            {
                if (!Stages.TryGetValue(__originalMethod, out var stage)) return;
                __state = Span.Begin(stage, stage == "flush", Core.Now);
                if (__state == null) { if (Span.Current != null) Core.ScopeSkipped++; return; }
                if (stage == "flush") EngineState.Emit("flush_begin", 0, __state.Id);
            }
            catch { Core.Error(); }
        }
        internal static void End(Span? __state, Exception? __exception) => Finish(__state, __exception);
        internal static void Finish(Span? span, Exception? error)
        {
            if (span == null) return;
            try
            {
                var timing = span.End(Core.Now);
                // Small sleep calls still contribute to parents' known_wait_ms,
                // without writing a CSV row for every one-millisecond sleep.
                bool wait = span.Stage == "receiver_sleep" || span.Stage == "async_graph_wait";
                if (span.Root || error != null || timing.elapsed >= (wait ? 10 : 1))
                    Core.Row(Core.FlushRows, DateTime.UtcNow.ToString("O"), Core.F(Core.Now), span.Id, span.Parent?.Id ?? 0, span.Stage,
                        Core.F(timing.elapsed), Core.F(timing.self), Core.F(timing.knownWait), Core.F(Math.Max(0, timing.elapsed - timing.knownWait)),
                        Thread.CurrentThread.ManagedThreadId, "inclusive_wall_time", error?.GetType().FullName ?? "", Core.FlushRows?.Dropped ?? 0);
                if (span.Root) EngineState.Emit("flush_end", 0, span.Id);
            }
            catch { Span.Current = span.Parent; Core.Error(); }
        }
        internal static void Sleep(int milliseconds)
        {
            Span? span = null; Exception? error = null;
            try { span = Span.Begin("receiver_sleep", false, Core.Now); } catch { Core.Error(); }
            try
            {
                // Span allocation can itself trigger GC. Keep that setup time in
                // the parent's self time, not in the measured original wait.
                if (span != null) span.Start = Core.Now;
                Thread.Sleep(milliseconds);
            }
            catch (Exception e) { error = e; throw; }
            finally { Finish(span, error); }
        }
        internal static bool Wait(WaitHandle handle)
        {
            Span? span = null; Exception? error = null;
            try { span = Span.Begin("async_graph_wait", false, Core.Now); } catch { Core.Error(); }
            try
            {
                if (span != null) span.Start = Core.Now;
                return handle.WaitOne();
            }
            catch (Exception e) { error = e; throw; }
            finally { Finish(span, error); }
        }
        internal static IEnumerable<CodeInstruction> ReplaceSleep(IEnumerable<CodeInstruction> instructions) => Replace(instructions, SleepTarget, nameof(Sleep));
        internal static IEnumerable<CodeInstruction> ReplaceWait(IEnumerable<CodeInstruction> instructions) => Replace(instructions, WaitTarget, nameof(Wait));
        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, MethodInfo target, string replacement)
        {
            var list = instructions.ToList(); int count = 0;
            foreach (var i in list)
                if ((i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) && i.operand is MethodInfo method && method.Equals(target))
                { i.opcode = OpCodes.Call; i.operand = typeof(Flush).GetMethod(replacement, BindingFlags.Static | BindingFlags.NonPublic); count++; }
            if (count != 1) throw new InvalidOperationException("Expected exactly one original " + target.Name + " call; found " + count);
            return list;
        }
    }
}
