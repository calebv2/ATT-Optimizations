using System;
using System.Linq;
using System.Threading;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace ForestNavigationGuard
{
    public sealed class Core : ForestPerformance.Support.Module
    {
        private HarmonyLib.Harmony? harmony;
        private float nextReport;
        private long reported, reportedRebuilds, reportedSkipped, reportedFallbacks;
        public override void OnInitializeMelon()
        {
            if (!Application.isBatchMode) return;
            try
            {
                var root = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetType("Alta.Forests.ForestGenerator", false) != null);
                var method = Patch.Resolve(root);
                var updatePost = RebuiltTiles.Resolve(root);
                harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.ForestNavigationGuard");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(Patch), nameof(Patch.Prefix)));
                harmony.Patch(updatePost, transpiler: new HarmonyMethod(typeof(RebuiltTiles), nameof(RebuiltTiles.ReplaceNotificationArgument)));
                LoggerInstance.Msg("READY v0.2.0; forest graph-mask wraparound guard active; overworld recast updates notify cutting of rebuilt tiles only. Other graph indices use native notification arrays. No forest regeneration requested.");
            }
            catch (Exception e)
            {
                try { harmony?.UnpatchSelf(); }
                catch (Exception rollback) { LoggerInstance.Error("Patch rollback failed: " + rollback); }
                harmony = null;
                LoggerInstance.Error("Forest navigation guard DISABLED: " + e);
            }
        }
        public override void OnUpdate()
        {
            if (harmony == null || Time.realtimeSinceStartup < nextReport) return;
            nextReport = Time.realtimeSinceStartup + 60;
            long count = Interlocked.Read(ref Patch.RejectedAliases);
            long rebuilds = Interlocked.Read(ref RebuiltTiles.Narrowed);
            long skipped = Interlocked.Read(ref RebuiltTiles.TilesSkipped);
            long fallbacks = Interlocked.Read(ref RebuiltTiles.ValidationFallbacks);
            if (count == reported && rebuilds == reportedRebuilds && fallbacks == reportedFallbacks) return;
            LoggerInstance.Msg("Forest navigation: rejected graph aliases=" + count + " (+" + (count - reported) +
                "); rebuilt-tile notifications=" + rebuilds + " (+" + (rebuilds - reportedRebuilds) +
                "); unrelated tile notifications avoided=" + skipped + " (+" + (skipped - reportedSkipped) +
                "); native validation fallbacks=" + fallbacks + " (+" + (fallbacks - reportedFallbacks) + "). Counts are not measured CPU savings.");
            reported = count; reportedRebuilds = rebuilds; reportedSkipped = skipped; reportedFallbacks = fallbacks;
        }
        public override void OnApplicationQuit()
        {
            harmony?.UnpatchSelf();
            Patch.Target = null;
        }
    }
}
