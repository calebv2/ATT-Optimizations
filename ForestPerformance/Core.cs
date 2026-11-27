using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using MelonLoader;
using UnityEngine;
using ATT.PerformanceCompatibility;

[assembly: MelonInfo(typeof(ForestPerformance.Core), "Forest Performance", "0.2.7", "ATT", null)]
[assembly: MelonGame("Alta", "A Township Tale")]
[assembly: AssemblyVersion("0.2.7.0")]

namespace ForestPerformance
{
    public sealed class Core : MelonMod
    {
        private readonly Support.ModuleGroup modules = new Support.ModuleGroup();
        private HarmonyLib.Harmony? harmony;
        private HarmonyLib.Harmony? timingHarmony;
        private HarmonyLib.Harmony? rewriteTerrainHarmony;
        private HarmonyLib.Harmony? generationTimingHarmony;
        private HarmonyLib.Harmony? prefabUnloadHarmony;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        internal static Settings Config = new Settings();
        internal static bool Ready;
        internal static int MainThread;
        internal static bool Main => Thread.CurrentThread.ManagedThreadId == MainThread;
        internal static double Now => Clock.Elapsed.TotalMilliseconds;
        internal static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        internal static ForestStepDiagnostics.ServerTrace? Trace;
        private static long rowSecond = -1, limited;
        private static int rowCount;
        private static readonly object RowLock = new object();
        private double nextReport;
        internal static void Row(string stage, double ms, string detail, Exception? error = null)
        {
            var sink = Trace; if (sink == null) return;
            lock (RowLock)
            {
                long second = (long)Now / 1000;
                if (rowSecond != second) { rowSecond = second; rowCount = 0; }
                if (++rowCount > 128) { Interlocked.Increment(ref limited); return; }
            }
            sink.Enqueue(ForestStepDiagnostics.ServerTrace.Row(DateTime.UtcNow.ToString("O"), stage, ms.ToString("0.###", CultureInfo.InvariantCulture), detail, error?.GetType().FullName ?? "", sink.Dropped));
        }
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            MainThread = Thread.CurrentThread.ManagedThreadId;
            MelonEvents.OnApplicationLateStart.Subscribe(ReportCompatibility);
            try
            {
                string directory = Path.Combine(Path.GetDirectoryName(Application.dataPath)!, "UserData", "ForestPerformance");
                Directory.CreateDirectory(directory);
                string config = Path.Combine(directory, "config.txt");
                if (!File.Exists(config)) File.WriteAllText(config, Settings.Defaults);
                Settings settings = Config = Settings.Parse(File.ReadAllText(config));
                if (!settings.Enabled) { LoggerInstance.Msg("DISABLED by startup configuration"); return; }
                Support.ModuleGroup.CheckLegacy("ForestNavigationGuard", "ForestResetFix", "ForestStepDiagnostics");
                if (typeof(AstarPath).Module.ModuleVersionId != new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff"))
                    throw new NotSupportedException("Unsupported game assembly");
                Loading.Bind(); Connectivity.Bind();
                harmony = new HarmonyLib.Harmony("ATT.ForestPerformance");
                Loading.Attach(harmony); Connectivity.Attach(harmony);
                try
                {
                    prefabUnloadHarmony = new HarmonyLib.Harmony("ATT.ForestPerformance.PrefabUnloadGuard");
                    PrefabUnloadGuard.Attach(prefabUnloadHarmony);
                    LoggerInstance.Msg("Detached child prefab guard enabled for server chunk unloads.");
                }
                catch (Exception error)
                {
                    StopPrefabUnloadGuard();
                    LoggerInstance.Warning("Detached child prefab guard unavailable: " + error.Message);
                }
                if (settings.Diagnostics)
                {
                    timingHarmony = new HarmonyLib.Harmony("ATT.ForestPerformance.LoadTransitionTiming");
                    try { LoadTransitionTiming.Attach(timingHarmony); }
                    catch (Exception error)
                    {
                        timingHarmony.UnpatchSelf(); LoadTransitionTiming.Shutdown();
                        LoggerInstance.Warning("Forest transition timing unavailable: " + error.Message);
                    }
                }
                if (settings.Diagnostics) Trace = new ForestStepDiagnostics.ServerTrace(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"), "performance.csv"), "utc,stage,duration_ms,detail,exception_type,dropped_rows");
                generationTimingHarmony = new HarmonyLib.Harmony("ATT.ForestPerformance.GenerationTiming");
                try
                {
                    bool rewriteQueue = ForestGenerationTiming.Attach(generationTimingHarmony);
                    LoggerInstance.Msg("Forest generation hook attached; rewrite cold generation gate timing=" + (rewriteQueue && settings.Diagnostics)
                        + "; bridge preparation=" + ForestGenerationTiming.BridgePreparationAttached + ".");
                }
                catch (Exception error)
                {
                    StopGenerationTiming();
                    LoggerInstance.Warning("Forest generation hook unavailable: " + error.Message);
                }
                if (settings.RewriteTerrainMasks)
                {
                    rewriteTerrainHarmony = new HarmonyLib.Harmony("ATT.ForestPerformance.RewriteTerrain");
                    try
                    {
                        if (RewriteTerrain.Attach(rewriteTerrainHarmony))
                            LoggerInstance.Msg("ForestRewrite terrain blend preparation enabled; Unity terrain writes timed separately.");
                    }
                    catch (Exception error)
                    {
                        StopRewriteTerrain();
                        LoggerInstance.Warning("ForestRewrite terrain preparation unavailable: " + error.Message);
                    }
                }
                Ready = true;
                if (settings.Navigation) modules.Start(new ForestNavigationGuard.Core(), LoggerInstance, directory);
                if (settings.Reset) modules.Start(new ForestResetFix.Core(), LoggerInstance, directory);
                if (settings.Diagnostics) modules.Start(new ForestStepDiagnostics.Core(), LoggerInstance, directory);
                LoggerInstance.Msg("READY v0.2.7; forest instruction batch=" + settings.InstructionsPerFrame + "; shared_generation_budget_ms=" + F(settings.GenerationBudgetMs) + "; separate_entity_loads=" + settings.SeparateLoads + "; connectivity_updates=" + settings.Connectivity + "; connectivity_outside_proof=" + (settings.Connectivity && settings.OutsideConnectivityProof) + "; rewrite_terrain_masks=" + RewriteTerrain.Installed + ". Output: " + directory);
            }
            catch (Exception error)
            {
                Stop();
                LoggerInstance.Error("Forest Performance DISABLED: " + error);
            }
        }
        private void ReportCompatibility()
        {
            if (Ready && Main && Config.PopulationRestorePrewarm)
            {
                double began = Now;
                try
                {
                    PopulationRestorePrewarm.Prepare();
                    Row("population_serializer_prewarm", Now - began, "native_version=1;scratch_round_trip=True");
                    LoggerInstance.Msg("Forest population serializer prepared at startup in " + F(Now - began) + " ms.");
                }
                catch (Exception error)
                {
                    Row("population_serializer_prewarm_error", Now - began, "native_restore_available", error);
                    LoggerInstance.Warning("Forest population serializer prewarm unavailable: " + error.GetType().Name);
                }
                began = Now;
                try
                {
                    var definition = PopulationRestorePrewarm.WarmDefinitions();
                    PopulationRestorePrewarm.Prepare(definition);
                    Row("population_definitions_prewarm", Now - began, "native_cache=True;scratch_round_trip=True");
                    LoggerInstance.Msg("Forest population definitions prepared at startup in " + F(Now - began) + " ms.");
                }
                catch (Exception error)
                {
                    Row("population_definitions_prewarm_error", Now - began, "native_restore_available", error);
                    LoggerInstance.Warning("Forest population definition prewarm unavailable: " + error.GetType().Name);
                }
            }
            StartupReport.WriteOnce("Forest Performance", message => LoggerInstance.Msg(message),
                new FeatureStatus("core forest fixes", Config.Enabled, Ready),
                new FeatureStatus("shared forest generation budget", Config.InstructionsPerFrame > 0 && Config.GenerationBudgetMs > 0,
                    Ready && Config.InstructionsPerFrame > 0 && Config.GenerationBudgetMs > 0,
                    "budget_ms=" + F(Config.GenerationBudgetMs) + ";native_batch=" + Config.InstructionsPerFrame),
                new FeatureStatus("local connectivity repair", Config.Connectivity, Ready && Config.Connectivity,
                    "runtime circuit status is recorded in connectivity health"),
                new FeatureStatus("outside connectivity proof", Config.OutsideConnectivityProof, Ready && Config.Connectivity && Config.OutsideConnectivityProof),
                new FeatureStatus("detached child prefab guard", Config.Enabled, prefabUnloadHarmony != null),
                new FeatureStatus("load phase timing", Config.Diagnostics, LoadTransitionTiming.Installed),
                new FeatureStatus("ForestRewrite bridge intersection preparation", Config.Enabled, Ready && ForestGenerationTiming.BridgePreparationAttached),
                new FeatureStatus("ForestRewrite terrain blend preparation", Config.RewriteTerrainMasks, RewriteTerrain.Installed));
        }
        private void StopRewriteTerrain()
        {
            RewriteTerrain.Shutdown();
            rewriteTerrainHarmony?.UnpatchSelf();
        }
        private void StopGenerationTiming()
        {
            generationTimingHarmony?.UnpatchSelf();
            ForestGenerationTiming.Reset();
        }
        private void StopPrefabUnloadGuard()
        {
            prefabUnloadHarmony?.UnpatchSelf();
            prefabUnloadHarmony = null;
        }
        public override void OnUpdate()
        {
            if (!Ready) return;
            Loading.Tick(Time.frameCount); Connectivity.Tick(); modules.Tick();
            if (Now < nextReport) return;
            nextReport = Now + 60000;
            Row("health", 0, Loading.Health() + ";transition_timing=" + LoadTransitionTiming.Installed + ";transition_errors=" + LoadTransitionTiming.Errors + ";limited=" + limited + ";writer_error=" + Trace?.Error);
        }
        public override void OnApplicationQuit() => Stop();

        private void Stop()
        {
            Ready = false;
            StopRewriteTerrain();
            StopGenerationTiming();
            StopPrefabUnloadGuard();
            ForestGenerationBudget.Shutdown();
            LoadTransitionTiming.Shutdown();
            Connectivity.Shutdown();
            Loading.Drain();
            modules.Stop();
            timingHarmony?.UnpatchSelf();
            harmony?.UnpatchSelf();
            Trace?.Dispose();
            Trace = null;
        }
    }
}
