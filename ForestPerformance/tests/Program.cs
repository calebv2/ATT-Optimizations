using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ForestPerformance;
using ForestPerformance.Support;
using Module = ForestPerformance.Support.Module;
using MelonLoader;

internal static class Program
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
    private static void Main()
    {
        var assembly = typeof(Core).Assembly;
        if (assembly.GetCustomAttributes(typeof(MelonInfoAttribute), false).Length != 1 ||
            assembly.GetTypes().Count(t => typeof(MelonMod).IsAssignableFrom(t)) != 1)
            throw new Exception("Expected one registered Forest Performance entry point");
        string[] legacy = { "ForestNavigationGuard", "ForestResetFix", "ForestStepDiagnostics" };
        foreach (string name in legacy)
            if (!typeof(Module).IsAssignableFrom(assembly.GetType(name + ".Core", true)!))
                throw new Exception("Missing embedded component: " + name);
        if (assembly.GetReferencedAssemblies().Any(a => legacy.Contains(a.Name)))
            throw new Exception("Combined DLL requires replaced DLLs");
        Settings defaults = Settings.Parse(Settings.Defaults);
        Settings off = Settings.Parse("enabled=false\nnavigation_fixes=false\nreset_fixes=false\nforest_step_diagnostics=false");
        if (!defaults.Enabled || !defaults.Navigation || !defaults.Reset || !defaults.Diagnostics || defaults.GenerationBudgetMs != 4 ||
            off.Enabled || off.Navigation || off.Reset || off.Diagnostics || defaults.OutsideConnectivityProof || off.OutsideConnectivityProof ||
            !Settings.Parse("connectivity_outside_proof=true").OutsideConnectivityProof)
            throw new Exception("Forest startup controls changed or ignored");
        foreach (string source in new[] { "enabled=maybe", "unknown=true", "enabled=true=extra", "connectivity_outside_proof=maybe", "forest_instructions_per_frame=-1", "forest_instructions_per_frame=11", "forest_generation_budget_ms=-1", "forest_generation_budget_ms=NaN", "forest_generation_budget_ms=Infinity", "forest_generation_budget_ms=51" })
        {
            bool rejected = false;
            try { Settings.Parse(source); } catch (FormatException) { rejected = true; }
            if (!rejected) throw new Exception("Malformed configuration accepted");
        }
        // Bind and attach the actual forest reset implementation in this DLL.
        // This validates game metadata without running a reset or Unity scenes.
        ForestResetFix.Core.ValidatePatchAttachment();
        LoadingTests.Run();
        GenerationBudgetTests.Run();
        TransitionTimingTests.Run();
        GateContextTests.Run();
        InstructionBatchingTests.Run();
        ConnectivityTests.Run();
        RewriteTerrainTests.Run();
        PrefabCleanupTests.Run();
        var calls = new List<string>(); var group = new ModuleGroup();
        group.Start(new Fixture("a", calls), null!, "family-root");
        group.Start(new Fixture("b", calls), null!, "family-root");
        group.Tick(); group.Stop(); group.Tick(); group.Stop();
        if (!calls.SequenceEqual(new[] { "a:init:family-root", "b:init:family-root", "a:tick", "b:tick", "b:quit", "a:quit" }))
            throw new Exception("Module lifecycle was duplicated or missed");
        ModuleGroup.CheckLegacy(legacy);
        AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("ForestNavigationGuard"), AssemblyBuilderAccess.Run);
        bool blocked = false;
        try { ModuleGroup.CheckLegacy(legacy); } catch (InvalidOperationException) { blocked = true; }
        if (!blocked) throw new Exception("Duplicate standalone component was not blocked");
        Console.WriteLine("PASS consolidation: one Forest Performance entry, three embedded components, no legacy DLL dependency, startup switches, malformed-config rejection, native reset-hook attachment, lifecycle/reverse shutdown and duplicate-load guard.");
    }
}
