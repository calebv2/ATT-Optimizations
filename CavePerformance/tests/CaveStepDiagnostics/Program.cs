using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Serialization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using CaveStepDiagnostics;
using HarmonyLib;

internal sealed class Target
{
    internal static readonly Exception Sentinel = new InvalidOperationException("sentinel");
    [MethodImpl(MethodImplOptions.NoInlining)] public int Step(bool fail)
    { Thread.Sleep(40); if (fail) throw Sentinel; return 42; }
}
internal sealed class FakePath
{
    public int State;
    public bool Fail;
    [MethodImpl(MethodImplOptions.NoInlining)] public bool MoveNext()
    { Thread.Sleep(35); State++; if (Fail) throw Target.Sentinel; return true; }
}
internal sealed class Inner : IEnumerator { public object Current => null!; public bool MoveNext() => false; public void Reset() { } }
internal static class Test
{
    private static void Set(object o, string n, object? v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(o, v);
    private static void Main()
    {
        string game = Path.Combine(Environment.GetEnvironmentVariable("ATT_GAME_PATH")!, "A Township Tale_Data/Managed/Root.Township.dll");
        Assembly root = Assembly.LoadFrom(game);
        Type generator = root.GetType("Alta.Caves.GenerationBase", true)!;
        Type state = generator.GetNestedTypes(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single(t => t.Name.StartsWith("<Render>d__"));
        string directory = Path.Combine(Path.GetTempPath(), "cave-step-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "steps.csv");
        var harmony = new HarmonyLib.Harmony("ATT.CaveStepDiagnostics.Smoke");
        try
        {
            using (var sink = new ServerTrace(path, "test"))
            {
                StepProbe.Initialize(state, generator, sink); // verifies every required field and property of the installed DLL
                var moveNext = state.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
                harmony.Patch(moveNext, prefix: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.Start)), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End)));
                Console.WriteLine("Installed game iterator hook attached: " + state.FullName);
                // Exercise context extraction with uninitialized actual game types; no Unity APIs run.
                object generatorObject = FormatterServices.GetUninitializedObject(generator);
                object chunk = FormatterServices.GetUninitializedObject(root.GetType("Alta.Caves.MapChunk", true)!);
                Type pluginType = root.GetType("Alta.Caves.PluginInstance", true)!;
                object plugin = FormatterServices.GetUninitializedObject(pluginType);
                Set(generatorObject, "<Layer>k__BackingField", 123); Set(chunk, "<X>k__BackingField", 3); Set(chunk, "<Z>k__BackingField", 4);
                Set(plugin, "<Name>k__BackingField", "sample-plugin");
                var plugins = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pluginType))!;
                plugins.Add(plugin); Set(generatorObject, "<PluginInstances>k__BackingField", plugins);
                object frame = FormatterServices.GetUninitializedObject(state);
                Set(frame, "<>4__this", generatorObject); Set(frame, "chunk", chunk); Set(frame, "<i>5__2", 0);
                var stack = new Stack<IEnumerator>(); stack.Push(new Inner()); Set(frame, "<renderStack>5__3", stack);
                StepProbe.Start(frame, out var probe); Thread.Sleep(35); StepProbe.End(probe, null);
                if (StepProbe.CurrentStepId != 0) throw new Exception("Outer scope leaked after completion");
                string pathRows = Path.Combine(directory, "path.csv");
                using (var pathSink = new ServerTrace(pathRows, "test"))
                {
                    PathProbe.Sink = pathSink;
                    PathProbe.AttachAll(root, harmony, Path.Combine(directory, "hooks.csv"));
                    if (PathProbe.Targets.Count != 5 || File.ReadAllText(Path.Combine(directory, "hooks.csv")).Contains("unavailable"))
                        throw new Exception("Installed pathfinding metadata or patch failed: " + File.ReadAllText(Path.Combine(directory, "hooks.csv")));
                    var fakeMethod = typeof(FakePath).GetMethod(nameof(FakePath.MoveNext))!;
                    var fakeInfo = new PathProbe.Info("FakePath", fakeMethod, typeof(FakePath).GetField("State")!, null, null, null);
                    if (!PathProbe.Targets.TryAdd(fakeMethod, fakeInfo)) throw new Exception("duplicate fake");
                    harmony.Patch(fakeMethod, prefix: new HarmonyMethod(typeof(PathProbe), nameof(PathProbe.Start)), finalizer: new HarmonyMethod(typeof(PathProbe), nameof(PathProbe.End)));
                    var fake = new FakePath();
                    // Unscoped calls do not generate misleading cave rows.
                    if (!fake.MoveNext()) throw new Exception("unscoped return changed");
                    StepProbe.Start(frame, out var outer); long parent = StepProbe.CurrentStepId;
                    if (parent == 0 || !fake.MoveNext()) throw new Exception("scoped call failed");
                    StepProbe.Start(frame, out var inner); long nested = StepProbe.CurrentStepId;
                    if (nested == parent) throw new Exception("nested scope ID reused");
                    StepProbe.End(inner, null);
                    if (StepProbe.CurrentStepId != parent) throw new Exception("nested scope not restored");
                    fake.Fail = true;
                    try { fake.MoveNext(); throw new Exception("nested exception swallowed"); }
                    catch (InvalidOperationException e) { if (!ReferenceEquals(e, Target.Sentinel)) throw new Exception("nested exception changed"); }
                    StepProbe.End(outer, null);
                    if (StepProbe.CurrentStepId != 0) throw new Exception("parent scope leaked");
                    PathProbe.Sink = null;
                }
                string[] pathRecords = File.ReadAllLines(pathRows);
                if (pathRecords.Length != 3 || !pathRecords[1].Contains(",FakePath,") || !pathRecords[1].Contains(",123,3,4,") || !pathRecords[2].Contains("System.InvalidOperationException"))
                    throw new Exception("path scope or rows invalid: " + string.Join("|", pathRecords));
                Console.WriteLine("Path probe: five installed metadata hooks attached, scoped fake slow steps preserve results/exceptions, no unscoped rows, nested scope restores.");
                const int iterations = 100000; var clock = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++) { StepProbe.Start(frame, out var measured); StepProbe.End(measured, null); }
                clock.Stop(); Console.WriteLine("Probe overhead in synthetic short calls: " + (clock.Elapsed.TotalMilliseconds * 1000 / iterations).ToString("F2") + " us/call");
                // A real Harmony detour exercises normal and exceptional paths, including failed context reads.
                var target = typeof(Target).GetMethod(nameof(Target.Step))!;
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.Start)), finalizer: new HarmonyMethod(typeof(StepProbe), nameof(StepProbe.End)));
                var instance = new Target();
                if (instance.Step(false) != 42) throw new Exception("result changed");
                try { instance.Step(true); throw new Exception("exception swallowed"); }
                catch (InvalidOperationException e) { if (!ReferenceEquals(e, Target.Sentinel)) throw new Exception("exception identity changed"); }
                if (StepProbe.CurrentStepId != 0) throw new Exception("scope leaked after original exception");
                harmony.UnpatchSelf();
                StepProbe.Sink = null;
            }
            string[] rows = File.ReadAllLines(path);
            if (rows.Length < 4 || !rows[1].Contains(",123,3,4,0,sample-plugin,") || !rows.Any(r => r.Contains("System.InvalidOperationException"))) throw new Exception("slow rows, context or exception missing: " + string.Join("|", rows));
            Console.WriteLine("Real Harmony detour preserved result and exception identity; both slow calls recorded.");
        }
        finally { StepProbe.Sink = null; harmony.UnpatchSelf(); Directory.Delete(directory, true); }
    }
}
