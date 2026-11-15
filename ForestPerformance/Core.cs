using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading;
using System.Globalization;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(ForestPerformance.Core), "Forest Performance", "0.2.0", "ATT", null)]
[assembly: MelonGame("Alta", "A Township Tale")]
[assembly: AssemblyVersion("0.2.0.0")]

namespace ForestPerformance
{
    public sealed class Core : MelonMod
    {
        private readonly Support.ModuleGroup modules = new Support.ModuleGroup();
        private HarmonyLib.Harmony? harmony;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        internal static Settings Config = new Settings();
        internal static bool Ready;
        internal static int MainThread;
        internal static bool Main => Thread.CurrentThread.ManagedThreadId == MainThread;
        internal static double Now => Clock.Elapsed.TotalMilliseconds;
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
                if (settings.Diagnostics) Trace = new ForestStepDiagnostics.ServerTrace(Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ"), "performance.csv"), "utc,stage,duration_ms,detail,exception_type,dropped_rows");
                Ready = true;
                if (settings.Navigation) modules.Start(new ForestNavigationGuard.Core(), LoggerInstance, directory);
                if (settings.Reset) modules.Start(new ForestResetFix.Core(), LoggerInstance, directory);
                if (settings.Diagnostics) modules.Start(new ForestStepDiagnostics.Core(), LoggerInstance, directory);
                LoggerInstance.Msg("READY v0.2.0; forest instruction batch=" + settings.InstructionsPerFrame + "; separate_entity_loads=" + settings.SeparateLoads + "; connectivity_updates=" + settings.Connectivity + "; existing forest guards/reset fixes retained. Native instructions and entity batches remain atomic. No forest reset requested. Output: " + directory);
            }
            catch (Exception error)
            {
                Ready = false; Connectivity.Shutdown(); Loading.Drain(); modules.Stop(); harmony?.UnpatchSelf(); Trace?.Dispose(); Trace = null;
                LoggerInstance.Error("Forest Performance DISABLED: " + error);
            }
        }
        public override void OnUpdate()
        {
            if (!Ready) return;
            Loading.Tick(Time.frameCount); Connectivity.Tick(); modules.Tick();
            if (Now < nextReport) return;
            nextReport = Now + 60000;
            Row("health", 0, Loading.Health() + ";limited=" + limited + ";writer_error=" + Trace?.Error);
        }
        public override void OnApplicationQuit()
        {
            Ready = false; Connectivity.Shutdown(); Loading.Drain(); modules.Stop(); harmony?.UnpatchSelf(); Trace?.Dispose(); Trace = null;
        }
    }
}
