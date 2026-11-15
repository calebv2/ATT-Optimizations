using System;

namespace ForestPerformance
{
    internal sealed class Settings
    {
        internal bool Enabled = true, Navigation = true, Reset = true, Diagnostics = true, Connectivity = true, SeparateLoads = true;
        internal int InstructionsPerFrame = 1;
        internal const string Defaults = "# Forest Performance 0.2.0; startup settings.\nenabled=true\nnavigation_fixes=true\nreset_fixes=true\nforest_step_diagnostics=true\nconnectivity_updates=true\nforest_instructions_per_frame=1\nseparate_entity_loads=true\n";
        internal static Settings Parse(string source)
        {
            var value = new Settings();
            foreach (string raw in source.Split('\n'))
            {
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                string[] pair = line.Split('=');
                if (pair.Length != 2) throw new FormatException("Expected key=value: " + line);
                switch (pair[0].Trim())
                {
                    case "enabled": value.Enabled = bool.Parse(pair[1].Trim()); break;
                    case "navigation_fixes": value.Navigation = bool.Parse(pair[1].Trim()); break;
                    case "reset_fixes": value.Reset = bool.Parse(pair[1].Trim()); break;
                    case "forest_step_diagnostics": value.Diagnostics = bool.Parse(pair[1].Trim()); break;
                    case "connectivity_updates": value.Connectivity = bool.Parse(pair[1].Trim()); break;
                    case "forest_instructions_per_frame": value.InstructionsPerFrame = int.Parse(pair[1].Trim(), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "separate_entity_loads": value.SeparateLoads = bool.Parse(pair[1].Trim()); break;
                    default: throw new FormatException("Unknown setting: " + pair[0]);
                }
            }
            if (value.InstructionsPerFrame < 0 || value.InstructionsPerFrame > 10) throw new FormatException("forest_instructions_per_frame must be 0 (native) through 10");
            return value;
        }
    }
}
