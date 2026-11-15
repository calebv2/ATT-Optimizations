using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace PathfindingQueueDiagnostics
{
    public sealed class Core : CavePerformance.Support.Module
    {
        private static readonly long origin = Stopwatch.GetTimestamp();
        internal static double Now => (Stopwatch.GetTimestamp() - origin) * 1000.0 / Stopwatch.Frequency;
        internal static volatile bool Ready, DisableRequested;
        internal static int MainThread;
        internal static bool Main => Ready && !DisableRequested && Thread.CurrentThread.ManagedThreadId == MainThread;
        internal static long Errors, Limited, ScopeSkipped;
        private static readonly RateGate gate = new RateGate(512);
        internal static ServerTrace? RequestRows, FlushRows, EngineRows, Health;
        private HarmonyLib.Harmony? harmony;
        private double nextEngine, nextHealth;
        internal static string F(double number) => number.ToString("0.###", CultureInfo.InvariantCulture);
        internal static void Error() { if (Interlocked.Increment(ref Errors) >= 50) DisableRequested = true; }
        internal static void Row(ServerTrace? sink, params object[] cells)
        {
            if (sink == null) return;
            if (!gate.Take(Now)) { Interlocked.Increment(ref Limited); return; }
            sink.Enqueue(ServerTrace.Row(cells));
        }
        internal static MethodInfo Method(Type type, string name, params Type[] arguments) => type.GetMethod(name,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, arguments, null)
            ?? throw new MissingMethodException(type.FullName, name);
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            MainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                if (typeof(AstarPath).Module.ModuleVersionId != new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff")) throw new NotSupportedException("Unsupported game assembly");
                EngineState.Bind(); Requests.Bind(); Flush.Bind();
                string directory = Path.Combine(DataDirectory, "Diagnostics", "PathfindingQueueDiagnostics", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"));
                Directory.CreateDirectory(directory);
                RequestRows = new ServerTrace(Path.Combine(directory, "requests.csv"), "utc,mono_ms,request_id,mob_entity_id,layer,stage,path_leg_id,path_id,path_type,pipeline_state,complete_state,request_age_ms,thread_id,pending,detail,exception_type,dropped_rows");
                FlushRows = new ServerTrace(Path.Combine(directory, "flush.csv"), "utc,mono_ms,span_id,parent_span_id,stage,elapsed_ms,self_ms,known_wait_ms,nonwait_wall_ms,thread_id,detail,exception_type,dropped_rows");
                EngineRows = new ServerTrace(Path.Combine(directory, "engine.csv"), "utc,mono_ms,context,request_id,span_id,state,dropped_rows");
                Health = new ServerTrace(Path.Combine(directory, "health.csv"), "utc,mono_ms,active_requests,errors,rate_limited,path_cache_evictions,scope_skipped,request_drops,flush_drops,engine_drops,health_drops,state,writer_error");
                harmony = new HarmonyLib.Harmony("ATT.CavePerformance.PathfindingQueueDiagnostics");
                string hooks = Path.Combine(directory, "hooks.csv"); File.WriteAllText(hooks, "target,status\n");
                Requests.Attach(harmony, hooks); Flush.Attach(harmony, hooks);
                Ready = true;
                File.WriteAllText(Path.Combine(directory, "run.txt"), "Pathfinding Queue Diagnostics 0.1.0\nTurabada Navigator request -> Astar submission -> pipeline transitions -> callback -> Navigator completion, guarded against pooled path reuse. Selected cave layers >=100. Max 256 request records and 512 weak path-generation bindings. Pending requests sampled every five seconds, expire after 120 seconds.\nFlush spans use same-thread scopes. receiver_sleep measures original Thread.Sleep(int); async_graph_wait measures the original parameterless WaitHandle.WaitOne call in GraphUpdateProcessor.ProcessGraphUpdates. Nonwait wall time is NOT measured CPU time. Nested spans overlap.\nEngine fields are non-atomic snapshots; queue_count_hint can miss PushFront. No wait handles are sampled or signaled, no queues enumerated/drained, no requests restarted, no graph locks released by diagnostics. Default max detail rows 512/s. Check health/drops before interpreting absent events.\n");
                LoggerInstance.Msg("READY v0.1.0; Turabada request lifecycle at cave layers >=" + CavePerformance.Core.Config.DiagnosticMinLayer + "; FlushWorkItems wait/execution split; observational only. Output: " + directory);
            }
            catch (Exception e) { LoggerInstance.Error("Pathfinding queue diagnostics DISABLED: " + e); Stop(false); }
        }
        public override void OnUpdate()
        {
            if (!Ready) return;
            if (DisableRequested || RequestRows?.Error != null || FlushRows?.Error != null || EngineRows?.Error != null || Health?.Error != null)
            { LoggerInstance.Warning("Pathfinding diagnostics stopped after probe/writer failure; errors=" + Errors); Stop(false); return; }
            try
            {
                Requests.Poll();
                if (Now >= nextEngine) { nextEngine = Now + 5000; EngineState.Emit("periodic", 0, 0); }
                if (Now >= nextHealth) { nextHealth = Now + 60000; HealthRow("running"); }
            }
            catch { Error(); }
        }
        private static void HealthRow(string state)
        {
            Health?.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(Now), Requests.Count, Errors, Limited, Requests.CacheEvictions,
                ScopeSkipped, RequestRows?.Dropped ?? 0, FlushRows?.Dropped ?? 0, EngineRows?.Dropped ?? 0, Health?.Dropped ?? 0, state,
                RequestRows?.Error ?? FlushRows?.Error ?? EngineRows?.Error ?? Health?.Error ?? ""));
        }
        private void Stop(bool wait)
        {
            Ready = false; HealthRow("shutdown");
            try { harmony?.UnpatchSelf(); } catch (Exception e) { LoggerInstance.Error("Diagnostic unpatch failed: " + e.GetType().Name); }
            Requests.Clear();
            RequestRows?.Stop(wait); FlushRows?.Stop(wait); EngineRows?.Stop(wait); Health?.Stop(wait);
            RequestRows = FlushRows = EngineRows = Health = null;
        }
        public override void OnApplicationQuit() => Stop(true);
    }
}
