using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ATT.PerformanceCompatibility
{
    // Shared source, linked into each performance DLL. Reports observations only: it never patches or
    // changes another mod's settings. Run once after all mod initializers, preferably at late start.
    internal sealed class FeatureStatus
    {
        internal readonly string Name, Detail;
        internal readonly bool Configured;
        internal readonly bool? Initialized;
        internal FeatureStatus(string name, bool configured, bool? initialized, string detail = "")
        { Name = name; Configured = configured; Initialized = initialized; Detail = detail; }
    }

    internal sealed class CompatibilitySnapshot
    {
        internal readonly Dictionary<string, string> Mods = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Cave = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Forest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> Steady = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Target IDs map to actual Harmony owners. An empty array means inspected and no patches;
        // an absent entry means unavailable/not inspected, never proof of native or failed initialization.
        internal readonly Dictionary<string, string[]> Owners = new Dictionary<string, string[]>(StringComparer.Ordinal);
        internal readonly List<string> Notes = new List<string>();
        internal string GcSettingsSource = "file/default expectations";
    }

    internal static class StartupReport
    {
        private static readonly HashSet<string> Written = new HashSet<string>(StringComparer.Ordinal);
        private static readonly object Sync = new object();

        internal static void WriteOnce(string family, Action<string> log, params FeatureStatus[] features)
        { WriteOnce(family, log, RuntimeSnapshot.Capture, features); }

        internal static void WriteOnce(string family, Action<string> log, Func<CompatibilitySnapshot> capture, params FeatureStatus[] features)
        {
            lock (Sync) { if (!Written.Add(family)) return; }
            try { foreach (string line in Build(family, capture(), features)) log("Compatibility: " + line); }
            catch (Exception error)
            {
                // A diagnostic must never disable gameplay fixes or prevent server startup.
                try { log("Compatibility: report unavailable: " + error.GetType().Name + ": " + error.Message); } catch { }
            }
        }

        internal static List<string> Build(string family, CompatibilitySnapshot state, params FeatureStatus[] features)
        {
            var lines = new List<string>();
            string[] relevant = { "Cave Performance", "Forest Performance", "Steady", "Daily Garbage Collection" };
            lines.Add(family + "; registered mods: " + string.Join(", ", relevant.Select(name =>
                state.Mods.TryGetValue(name, out string version) ? name + " " + version : name + " absent")) +
                ". Registration/configuration does not prove initialization; patch owners below show attachment, not successful execution.");
            foreach (FeatureStatus feature in features ?? new FeatureStatus[0])
                lines.Add("local " + feature.Name + ": configured=" + feature.Configured + "; initialized=" +
                    (feature.Initialized.HasValue ? feature.Initialized.Value.ToString() : "unknown") +
                    (string.IsNullOrEmpty(feature.Detail) ? "" : "; " + feature.Detail));
            if (state.Mods.ContainsKey("Steady"))
            {
                Handoff(lines, state, "forest connectivity", "Forest Performance", state.Forest, "connectivity_updates", "forest-fill");
                Handoff(lines, state, "forest graph mask", "Forest Performance", state.Forest, "navigation_fixes", "graph-mask");
                Handoff(lines, state, "forest instruction pacing", "Forest Performance", state.Forest, "forest_instructions_per_frame", "forest-instructions");
                Handoff(lines, state, "forest entity pacing", "Forest Performance", state.Forest, "separate_entity_loads", "forest-load");
                Handoff(lines, state, "procedural cave scan recovery", "Cave Performance", state.Cave, "navigation_recovery", "cave-scan");
                Handoff(lines, state, "cave entity pacing", "Cave Performance", state.Cave, "separate_entity_loads", "cave-load");
                Handoff(lines, state, "path workers (Steady only participates when PathThreads > 0)", "Cave Performance", state.Cave, "path_workers", "path-workers");
                lines.Add("static cave scan recovery; " + OwnerText(state, "static-scan") +
                    "; Steady 26.11.4 skips its whole cave scan module when procedural recovery is handed off. Inspect owners and local initialization before assuming static scan coverage.");
                lines.Add("complementary targets: " + OwnerText(state, "nearest") + "; " + OwnerText(state, "recast") + "; " + OwnerText(state, "full-fill") +
                    ". Distinct hooks may compose; owner presence alone is not a compatibility or performance test.");
                Gc(lines, state);
            }
            foreach (string note in state.Notes) lines.Add(note);
            return lines;
        }

        private static void Handoff(List<string> lines, CompatibilitySnapshot state, string name, string mod,
            Dictionary<string, string> config, string key, string target)
        {
            bool expected = state.Mods.ContainsKey(mod) && SteadyDetectsOn(config, key);
            lines.Add(name + ": Steady config/name handoff expectation=" + (expected ? mod : "no handoff") +
                "; " + OwnerText(state, target));
            string ownerPrefix = mod == "Cave Performance" ? "ATT.CavePerformance" : "ATT.ForestPerformance";
            if (expected && state.Owners.TryGetValue(target, out string[] owners) &&
                !owners.Any(owner => owner == ownerPrefix || owner.StartsWith(ownerPrefix + ".", StringComparison.Ordinal)))
                lines.Add("WARNING: " + name + " is expected to be handed off by Steady, but no " + ownerPrefix +
                    " patch owner was found on the inspected target; check local initialization before relying on this handoff.");
        }

        private static string OwnerText(CompatibilitySnapshot state, string target)
        {
            if (!state.Owners.TryGetValue(target, out string[] owners)) return target + " patch owners=unknown (target unavailable)";
            return target + " patch owners=" + (owners.Length == 0 ? "none" : string.Join(",", owners));
        }

        private static void Gc(List<string> lines, CompatibilitySnapshot state)
        {
            bool dailyRegistered = state.Mods.ContainsKey("Daily Garbage Collection");
            bool dailyTimer = HasOwner(state, "gc-timer", "ATT.DailyGarbageCollection");
            bool dailyScan = HasOwner(state, "gc-scan", "ATT.DailyGarbageCollection");
            bool? forced = BoolSetting(state.Steady, "ForcedGc", false);
            bool? scan = BoolSetting(state.Steady, "ScanGc", false);
            lines.Add("GC ownership: " + OwnerText(state, "gc-timer") + "; " + OwnerText(state, "gc-scan") +
                "; Steady ForcedGc=" + Show(forced) + ", ScanGc=" + Show(scan) + " (" + state.GcSettingsSource + ").");
            if (dailyRegistered && (!dailyTimer || !dailyScan))
                lines.Add("WARNING: Daily Garbage Collection is registered but both expected suppression patch owners were not confirmed; daily scheduler initialization is unknown from registration alone.");
            if (dailyRegistered && (forced != true || scan != true))
                lines.Add("WARNING: redundant/order-sensitive GC ownership. With Daily Garbage Collection owning both paths, Steady ForcedGc=true and ScanGc=true leave Steady's GC patches disabled; our Daily GC suppression and 06:00 scheduler remain their own responsibility. False defaults can make the scan transpilers depend on installation order. No settings were changed.");
            else if (dailyRegistered)
                lines.Add("GC policy handoff configured: Steady leaves both GC paths alone; confirm Daily GC's own active startup line for the 06:00 scheduler. Patch owners do not prove scheduler readiness.");
        }

        private static bool HasOwner(CompatibilitySnapshot state, string target, string expected) =>
            state.Owners.TryGetValue(target, out string[] owners) && owners.Any(owner => owner == expected);
        private static string Show(bool? value) => value.HasValue ? value.Value.ToString() : "invalid/unknown";

        // Match Steady 26.11.4 Others.On exactly, including its absent-setting defaults. Do not turn
        // this expectation into a claim about the other mod's Ready flags or successful Harmony attachment.
        internal static bool SteadyDetectsOn(Dictionary<string, string> settings, string setting) =>
            !(settings.TryGetValue("enabled", out string enabled) && Off(enabled)) &&
            (!settings.TryGetValue(setting, out string value) || !Off(value));
        private static bool Off(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase) || value == "0";
        internal static Dictionary<string, string> ParseHandoffSettings(IEnumerable<string> input)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in input)
            {
                int equals = line.IndexOf('=');
                if (equals > 0 && !line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    values[line.Substring(0, equals).Trim()] = line.Substring(equals + 1).Trim();
            }
            return values;
        }
        internal static bool? BoolSetting(Dictionary<string, string> settings, string key, bool missingDefault)
        {
            if (!settings.TryGetValue(key, out string value)) return missingDefault;
            if (bool.TryParse(value, out bool parsed)) return parsed;
            return null;
        }

        // Read only the requested TOML category. Key=value files use null. Comments outside quotes
        // are stripped; a similarly named key in another category must not affect the report.
        internal static Dictionary<string, string> ParseSettings(IEnumerable<string> input, string? section)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool inside = section == null;
            foreach (string raw in input)
            {
                string line = StripComment(raw).Trim();
                if (line.StartsWith("[", StringComparison.Ordinal))
                {
                    inside = section != null && line == "[" + section + "]";
                    continue;
                }
                if (!inside) continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line.Substring(0, equals).Trim(), value = line.Substring(equals + 1).Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[value.Length - 1] == '"') ||
                    (value[0] == '\'' && value[value.Length - 1] == '\''))) value = value.Substring(1, value.Length - 2);
                values[key] = value;
            }
            return values;
        }

        private static string StripComment(string line)
        {
            char quote = '\0'; bool escaped = false;
            for (int i = 0; i < line.Length; i++)
            {
                char current = line[i];
                if (escaped) { escaped = false; continue; }
                if (quote == '"' && current == '\\') { escaped = true; continue; }
                if (quote != '\0') { if (current == quote) quote = '\0'; continue; }
                if (current == '"' || current == '\'') { quote = current; continue; }
                if (current == '#' || current == ';') return line.Substring(0, i);
            }
            return line;
        }
    }
}
