using System;
using System.Globalization;

namespace CavePerformance
{
    internal sealed class Settings
    {
        internal bool Enabled = true, LocalSearch = true, Timing = true, SeparateLoads = true;
        internal bool NavigationRecovery = true, StepDiagnostics = true, QueueDiagnostics = true, MobDiagnostics = true, AsyncScanStart = true;
        internal int Workers = 2;
        internal int DiagnosticMinLayer = 90;
        internal float GenerationMs = 4;
        internal float WyrmMultiplier = 0.75f;
        internal float TurabadaMultiplier = 1f;
        internal const string Defaults = "# Cave Performance 0.2.5 startup settings.\nenabled=true\npath_workers=2\ncave_local_search=true\ngeneration_ms=4\nseparate_entity_loads=true\nfine_timing=true\ncave_wyrm_multiplier=0.75\ncave_turabada_multiplier=1\nnavigation_recovery=true\ncave_step_diagnostics=true\npathfinding_queue_diagnostics=true\nmob_combat_diagnostics=true\ncave_diagnostic_min_layer=90\nasync_cave_scan_start=true\n";
        internal static Settings Parse(string source)
        {
            var value = new Settings();
            foreach (string raw in source.Split('\n'))
            {
                string line = raw.Split('#')[0].Trim(); if (line.Length == 0) continue;
                var pair = line.Split('='); if (pair.Length != 2) throw new FormatException("Expected key=value: " + line);
                string key = pair[0].Trim(), text = pair[1].Trim();
                switch (key)
                {
                    case "enabled": value.Enabled = bool.Parse(text); break;
                    case "path_workers": value.Workers = int.Parse(text, CultureInfo.InvariantCulture); break;
                    case "cave_local_search": value.LocalSearch = bool.Parse(text); break;
                    case "generation_ms": value.GenerationMs = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "separate_entity_loads": value.SeparateLoads = bool.Parse(text); break;
                    case "fine_timing": value.Timing = bool.Parse(text); break;
                    case "cave_wyrm_multiplier": value.WyrmMultiplier = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "cave_turabada_multiplier": value.TurabadaMultiplier = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "navigation_recovery": value.NavigationRecovery = bool.Parse(text); break;
                    case "cave_step_diagnostics": value.StepDiagnostics = bool.Parse(text); break;
                    case "pathfinding_queue_diagnostics": value.QueueDiagnostics = bool.Parse(text); break;
                    case "mob_combat_diagnostics": value.MobDiagnostics = bool.Parse(text); break;
                    case "cave_diagnostic_min_layer": value.DiagnosticMinLayer = int.Parse(text, CultureInfo.InvariantCulture); break;
                    case "async_cave_scan_start": value.AsyncScanStart = bool.Parse(text); break;
                    default: throw new FormatException("Unknown setting: " + key);
                }
            }
            if (value.Workers != 0 && value.Workers != 1 && value.Workers != 2) throw new FormatException("path_workers must be 0 (native), 1 or 2");
            if (value.DiagnosticMinLayer < 0) throw new FormatException("cave_diagnostic_min_layer must be nonnegative");
            if (float.IsNaN(value.GenerationMs) || float.IsInfinity(value.GenerationMs) || value.GenerationMs < 0 || value.GenerationMs > 16)
                throw new FormatException("generation_ms must be 0 (native) through 16");
            if (float.IsNaN(value.WyrmMultiplier) || float.IsInfinity(value.WyrmMultiplier) || value.WyrmMultiplier < 0 || value.WyrmMultiplier > 1)
                throw new FormatException("cave_wyrm_multiplier must be 0 through 1");
            if (float.IsNaN(value.TurabadaMultiplier) || float.IsInfinity(value.TurabadaMultiplier) || value.TurabadaMultiplier < 0 || value.TurabadaMultiplier > 1)
                throw new FormatException("cave_turabada_multiplier must be 0 through 1");
            return value;
        }
    }
}
