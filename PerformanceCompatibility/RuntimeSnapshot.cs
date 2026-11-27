using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using MelonLoader.Utils;

namespace ATT.PerformanceCompatibility
{
    internal static class RuntimeSnapshot
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        internal static CompatibilitySnapshot Capture()
        {
            var snapshot = new CompatibilitySnapshot();
            foreach (MelonBase melon in MelonBase.RegisteredMelons)
                if (melon.Info != null) snapshot.Mods[melon.Info.Name] = melon.Info.Version;
            string data = MelonEnvironment.UserDataDirectory;
            Read(snapshot, Path.Combine(data, "CavePerformance", "config.txt"), null, snapshot.Cave);
            Read(snapshot, Path.Combine(data, "ForestPerformance", "config.txt"), null, snapshot.Forest);
            Read(snapshot, Path.Combine(data, "MelonPreferences.cfg"), "Steady", snapshot.Steady);
            Inspect(snapshot, "cave-scan", () => Method("Alta.Chunks.CaveLayerStreamer", "GeneratePathfinding"));
            Inspect(snapshot, "static-scan", () => Method("Alta.Caves.StaticCaveChunk", "GeneratePathfinding") ?? Method("Alta.Chunks.StaticCaveChunk", "GeneratePathfinding"));
            Inspect(snapshot, "forest-fill", () => Method("AstarPath", "FloodFill", method => method.GetParameters().Length == 0));
            Inspect(snapshot, "full-fill", () => Method("Pathfinding.GraphUpdateProcessor", "FloodFill", method => method.GetParameters().Length == 0));
            Inspect(snapshot, "graph-mask", () => Method("Pathfinding.NNConstraint", "SuitableGraph"));
            Inspect(snapshot, "forest-instructions", () => Method("Alta.Static.InstructionSet", "ExecuteInternal"));
            Inspect(snapshot, "forest-load", () => Method("ForestChunk", "Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic"));
            Inspect(snapshot, "cave-load", () => Method("Alta.Chunks.CaveLayerStreamer", "Alta.Chunks.IProceduralGenerationStreamer.LoadDynamic"));
            Inspect(snapshot, "path-workers", () => Method("AstarPath", "InitializePathProcessor"));
            Inspect(snapshot, "nearest", () => Method("AstarPath", "GetNearest", method => method.GetParameters().Length == 3));
            Inspect(snapshot, "recast", () => Method("Pathfinding.RecastGraph", "Pathfinding.IUpdatableGraph.UpdateAreaPost"));
            Inspect(snapshot, "gc-timer", () => Method("GarbageCollectTimer", "Start"));
            Inspect(snapshot, "gc-scan", () =>
            {
                Type? astar = AccessTools.TypeByName("AstarPath");
                Type[] iterators = astar?.GetNestedTypes(Flags).Where(type => type.Name.StartsWith("<ScanAsync>d__", StringComparison.Ordinal)).ToArray() ?? new Type[0];
                return iterators.Length == 1 ? iterators[0].GetMethod("MoveNext", Flags) : null;
            });
            MelonBase? steady = MelonBase.RegisteredMelons.FirstOrDefault(melon => melon.Info?.Name == "Steady");
            if (steady != null)
            {
                string? source = steady.MelonAssembly?.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                snapshot.Notes.Add("Steady assembly informational version=" + (source ?? "unknown") + "; compatibility expectations describe reviewed 26.11.4 behavior; they are not a hard version block.");
                ReadLiveGcSettings(snapshot, steady.MelonAssembly?.Assembly);
            }
            return snapshot;
        }

        private static void Read(CompatibilitySnapshot snapshot, string path, string? category, Dictionary<string, string> into)
        {
            try
            {
                if (!File.Exists(path)) { snapshot.Notes.Add(Path.GetFileName(Path.GetDirectoryName(path)) + "/" + Path.GetFileName(path) + " absent; configuration expectations use defaults."); return; }
                var parsed = category == null ? StartupReport.ParseHandoffSettings(File.ReadLines(path)) : StartupReport.ParseSettings(File.ReadLines(path), category);
                foreach (var item in parsed) into[item.Key] = item.Value;
            }
            catch (Exception error) { snapshot.Notes.Add("Could not read " + Path.GetFileName(path) + ": " + error.GetType().Name + "; configuration expectations use defaults."); }
        }

        private static void ReadLiveGcSettings(CompatibilitySnapshot snapshot, Assembly? assembly)
        {
            try
            {
                Type? config = assembly?.GetType("Steady.SteadyConfig", false);
                object? forced = config?.GetProperty("ForcedGc", Flags)?.GetValue(null, null);
                object? scan = config?.GetProperty("ScanGc", Flags)?.GetValue(null, null);
                if (forced is bool forcedValue && scan is bool scanValue)
                {
                    snapshot.Steady["ForcedGc"] = forcedValue.ToString();
                    snapshot.Steady["ScanGc"] = scanValue.ToString();
                    snapshot.GcSettingsSource = "live Steady settings";
                }
            }
            catch (Exception error) { snapshot.Notes.Add("Live Steady GC settings unavailable: " + error.GetType().Name + "; reporting file/default expectations."); }
        }

        private static MethodInfo? Method(string typeName, string name, Func<MethodInfo, bool>? predicate = null)
        {
            Type? type = AccessTools.TypeByName(typeName);
            if (type == null) return null;
            MethodInfo[] methods = type.GetMethods(Flags).Where(method => method.Name == name && (predicate == null || predicate(method))).ToArray();
            return methods.Length == 1 ? methods[0] : null;
        }

        private static void Inspect(CompatibilitySnapshot state, string name, Func<MethodBase?> resolve)
        {
            try
            {
                MethodBase? target = resolve();
                if (target != null) state.Owners[name] = Owners(target);
            }
            catch (Exception error) { state.Notes.Add(name + " ownership inspection unavailable: " + error.GetType().Name); }
        }

        internal static string[] Owners(MethodBase method)
        {
            Patches? patches = HarmonyLib.Harmony.GetPatchInfo(method);
            return patches == null ? new string[0] : patches.Owners.OrderBy(owner => owner, StringComparer.Ordinal).ToArray();
        }
    }
}
