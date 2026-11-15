using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CavePerformance;
using CavePerformance.Support;
using Module = CavePerformance.Support.Module;
using MelonLoader;

internal static class ConsolidationTests
{
    private sealed class Fixture : Module
    {
        private readonly string id;
        private readonly List<string> calls;
        internal Fixture(string id, List<string> calls) { this.id = id; this.calls = calls; }
        public override void OnInitializeMelon() => calls.Add(id + ":init:" + DataDirectory);
        public override void OnUpdate() => calls.Add(id + ":tick");
        public override void OnApplicationQuit() => calls.Add(id + ":quit");
    }
    internal static void Run()
    {
        var assembly = typeof(Core).Assembly;
        if (assembly.GetCustomAttributes(typeof(MelonInfoAttribute), false).Length != 1 ||
            assembly.GetTypes().Count(t => typeof(MelonMod).IsAssignableFrom(t)) != 1)
            throw new Exception("Expected one registered Cave Performance entry point");
        string[] legacy = { "CaveNavigationRecovery", "CaveStepDiagnostics", "PathfindingQueueDiagnostics", "MobCombatDiagnostics" };
        foreach (string name in legacy)
        {
            Type component = assembly.GetType(name + ".Core", true)!;
            if (!typeof(Module).IsAssignableFrom(component) || typeof(MelonMod).IsAssignableFrom(component))
                throw new Exception("Component has its own registered lifecycle: " + name);
        }
        if (assembly.GetReferencedAssemblies().Any(a => legacy.Contains(a.Name)))
            throw new Exception("Combined DLL still requires replaced DLLs");
        var calls = new List<string>(); var group = new ModuleGroup();
        group.Start(new Fixture("a", calls), null!, "family-root");
        group.Start(new Fixture("b", calls), null!, "family-root");
        group.Tick(); group.Stop(); group.Tick(); group.Stop();
        if (!calls.SequenceEqual(new[] { "a:init:family-root", "b:init:family-root", "a:tick", "b:tick", "b:quit", "a:quit" }))
            throw new Exception("Module lifecycle was missed, duplicated or shut down out of order");
        ModuleGroup.CheckLegacy(legacy);
        AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("CaveStepDiagnostics"), AssemblyBuilderAccess.Run);
        bool blocked = false;
        try { ModuleGroup.CheckLegacy(legacy); } catch (InvalidOperationException) { blocked = true; }
        if (!blocked) throw new Exception("Duplicate standalone component was not blocked");
        Console.WriteLine("PASS consolidation: one Melon entry, four embedded components, no legacy DLL dependency, shared data root, single lifecycle/reverse shutdown and duplicate-load guard.");
    }
}
