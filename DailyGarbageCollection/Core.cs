using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

[assembly: MelonInfo(typeof(DailyGarbageCollection.Core), "Daily Garbage Collection", "0.1.0", "ATT", null)]
[assembly: MelonGame("Alta", "A Township Tale")]
[assembly: AssemblyVersion("0.1.0.0")]

namespace DailyGarbageCollection
{
    public sealed class Core : MelonMod
    {
        private HarmonyLib.Harmony? harmony;
        private TimeZoneInfo? eastern;
        private string marker = "";
        private string? lastDate;
        private long nextCheck;
        private bool ready, warned;

        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            try
            {
                eastern = DailySchedule.ResolveEastern();
                string directory = Path.Combine(MelonEnvironment.UserDataDirectory, "DailyGarbageCollection");
                Directory.CreateDirectory(directory);
                marker = Path.Combine(directory, "last-request-date.txt");
                lastDate = DailySchedule.ReadMarker(marker);
                // Test write access before changing either game path.
                string probe = Path.Combine(directory, "write-check.tmp");
                File.WriteAllText(probe, "ok"); File.Delete(probe);
                var root = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("GarbageCollectTimer", false)).FirstOrDefault(t => t != null)?.Assembly
                    ?? throw new TypeLoadException("Game garbage collection timer not loaded");
                var targets = Patch.Resolve(root);
                harmony = new HarmonyLib.Harmony("ATT.DailyGarbageCollection");
                harmony.Patch(targets.scan, transpiler: new HarmonyMethod(typeof(Patch), nameof(Patch.ReplaceScanCollect)));
                harmony.Patch(targets.timer, prefix: new HarmonyMethod(typeof(Patch), nameof(Patch.SkipGameTimer)));
                ready = true;
                LoggerInstance.Msg("Daily GC active: periodic game timer disabled; pathfinding scan GC request suppressed; one GC request at 06:00 America/New_York. Automatic GC remains enabled. Game assembly MVID " + root.ManifestModule.ModuleVersionId + "; time zone " + eastern.Id + ".");
            }
            catch (Exception e)
            {
                ready = false;
                try
                {
                    harmony?.UnpatchSelf();
                    LoggerInstance.Error("Daily GC unavailable; own patches removed: " + e.GetType().Name + ": " + e.Message);
                }
                catch (Exception rollback)
                {
                    LoggerInstance.Error("Daily GC unavailable and patch rollback failed; inspect server before relying on GC policy: " + rollback.GetType().Name + ": " + rollback.Message);
                }
            }
        }
        public override void OnUpdate()
        {
            if (!ready || eastern == null) return;
            long timestamp = Stopwatch.GetTimestamp();
            if (timestamp < nextCheck) return;
            nextCheck = timestamp + Stopwatch.Frequency;
            try
            {
                DateTime local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, eastern);
                if (!DailySchedule.ShouldRun(local, lastDate)) return;
                string today = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                // Persist before requesting GC so a restart cannot request it twice in the same window.
                DailySchedule.WriteMarker(marker, today);
                lastDate = today;
                LoggerInstance.Msg("Requesting daily full garbage collection for " + today + " at " + local.ToString("O") + " Eastern; suppressed cave-scan requests=" + System.Threading.Interlocked.Read(ref Patch.SuppressedCaveCollections));
                var clock = Stopwatch.StartNew();
                GC.Collect();
                clock.Stop();
                LoggerInstance.Msg("Daily GC request returned after " + clock.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms (wall time; not necessarily GC pause duration).");
            }
            catch (Exception e)
            {
                if (!warned) { warned = true; LoggerInstance.Error("Daily GC request failed; automatic GC remains enabled: " + e.GetType().Name + ": " + e.Message); }
            }
        }
        public override void OnApplicationQuit()
        {
            ready = false;
            try { harmony?.UnpatchSelf(); } catch { }
        }
    }
}
