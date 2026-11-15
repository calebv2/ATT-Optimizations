using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(CavePerformance.Core), "Cave Performance", "0.2.1", "ATT", null)]
[assembly: MelonGame("Alta", "A Township Tale")]
[assembly: AssemblyVersion("0.2.1.0")]
[assembly: InternalsVisibleTo("CavePerformance.Tests")]

namespace CavePerformance
{
    public sealed class Core : MelonMod
    {
        internal static readonly Guid SupportedMvid = new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff");
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        internal static Settings Config = new Settings();
        internal static volatile bool Ready;
        internal static int MainThread;
        internal static long Errors;
        internal static long Limited;
        private static readonly object RowLock = new object();
        private static long rowSecond = -1;
        private static int rowsThisSecond;
        internal static double TransitionUntil;
        internal static ServerTrace? Trace;
        private HarmonyLib.Harmony? harmony;
        private readonly Support.ModuleGroup modules = new Support.ModuleGroup();
        private double nextReport;
        internal static double Now => Clock.Elapsed.TotalMilliseconds;
        internal static bool Main => Thread.CurrentThread.ManagedThreadId == MainThread;
        internal static bool InTransition => Ready && Now < Volatile.Read(ref TransitionUntil);
        internal static string F(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);
        internal static void Transition() => Volatile.Write(ref TransitionUntil, Now + 60000);
        internal static void Row(string stage, double ms, string detail, Exception? error = null)
        {
            if (Trace == null) return;
            try
            {
            lock (RowLock)
            {
                long second = (long)Now / 1000;
                if (second != rowSecond) { rowSecond = second; rowsThisSecond = 0; }
                if (++rowsThisSecond > 128) { Interlocked.Increment(ref Limited); return; }
            }
            Trace.Enqueue(ServerTrace.Row(DateTime.UtcNow.ToString("O"), F(Now), stage, F(ms), Thread.CurrentThread.ManagedThreadId,
                detail, error?.GetType().FullName ?? "", Trace.Dropped));
            }
            catch { Interlocked.Increment(ref Errors); }
        }
        internal static void Bind()
        {
            if (typeof(AstarPath).Module.ModuleVersionId != SupportedMvid) throw new NotSupportedException("Unsupported game assembly");
            Search.Bind(); ScanPacing.Bind(); Loading.Bind(); Timing.Bind(); WyrmPopulation.Bind();
        }
        internal static void Attach(HarmonyLib.Harmony h)
        {
            Search.Attach(h); ScanPacing.Attach(h); Loading.Attach(h); Timing.Attach(h); WyrmPopulation.Attach(h);
        }
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            MainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "UserData", "CavePerformance");
                Directory.CreateDirectory(directory);
                string config = Path.Combine(directory, "config.txt");
                if (!File.Exists(config)) File.WriteAllText(config, Settings.Defaults);
                Config = Settings.Parse(File.ReadAllText(config));
                if (!Config.Enabled) { LoggerInstance.Msg("DISABLED by startup configuration"); return; }
                Support.ModuleGroup.CheckLegacy("CaveNavigationRecovery", "CaveStepDiagnostics", "PathfindingQueueDiagnostics", "MobCombatDiagnostics");
                Bind();
                harmony = new HarmonyLib.Harmony("ATT.CavePerformance"); Attach(harmony);
                if (Config.Timing) Trace = new ServerTrace(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"), "timing.csv"),
                    "utc,mono_ms,stage,duration_ms,thread_id,detail,exception_type,dropped_rows");
                Ready = true;
                if (Config.NavigationRecovery) modules.Start(new CaveNavigationRecovery.Core(), LoggerInstance, directory);
                if (Config.StepDiagnostics) modules.Start(new CaveStepDiagnostics.Core(), LoggerInstance, directory);
                if (Config.QueueDiagnostics) modules.Start(new PathfindingQueueDiagnostics.Core(), LoggerInstance, directory);
                if (Config.MobDiagnostics) modules.Start(new MobCombatDiagnostics.Core(), LoggerInstance, directory);
                LoggerInstance.Msg("READY v0.2.1; explicit walker owner binding and rejection counters; path_workers=" + Config.Workers + "; cave_local_search=" + Config.LocalSearch +
                    "; generation_ms=" + Config.GenerationMs + "; separate_entity_loads=" + Config.SeparateLoads +
                    "; fine_timing=" + Config.Timing + "; cave_wyrm_multiplier=" + Config.WyrmMultiplier + ". Native entity restore batches remain atomic; budgets cannot interrupt a native call. Activation is at startup; GC policy unchanged.");
            }
            catch (Exception e)
            {
                Ready = false;
                ScanPacing.Shutdown();
                modules.Stop();
                try { harmony?.UnpatchSelf(); } catch (Exception rollback) { LoggerInstance.Error("Rollback: " + rollback); }
                Trace?.Dispose(); Trace = null;
                LoggerInstance.Error("Cave Performance DISABLED: " + e);
            }
        }
        public override void OnUpdate()
        {
            if (!Ready) return;
            modules.Tick();
            Loading.Tick(Time.frameCount);
            if (Now < nextReport) return;
            nextReport = Now + 60000;
            Row("health", 0, "workers_configured=" + Config.Workers + ";workers_actual=" + Search.ActualWorkers() + ";scoped_queries=" + Interlocked.Read(ref Search.ScopedQueries) +
                ";fallbacks=" + Interlocked.Read(ref Search.Fallbacks) + ";native_load_batches_released=" + Loading.Released +
                ";pending_load_gates=" + Loading.Pending + ";wyrm_target_reductions=" + Interlocked.Read(ref WyrmPopulation.Reduced) +
                ";" + Search.Health() +
                ScanPacing.Health() + ";load_context_fallbacks=" + Loading.ContextFallbacks + ";load_offthread_drain_deferred=" + Loading.OffThreadDrainDeferred +
                ";errors=" + Interlocked.Read(ref Errors) + ";rate_limited=" + Interlocked.Read(ref Limited) + ";writer_error=" + Trace?.Error);
        }
        public override void OnApplicationQuit()
        {
            // Complete queued gates before detaching, so an awaiting native
            // loader is never abandoned solely because this mod shuts down.
            Ready = false; ScanPacing.Shutdown(); Loading.Drain(); modules.Stop();
            harmony?.UnpatchSelf(); Trace?.Dispose(); Trace = null;
        }
    }
}
