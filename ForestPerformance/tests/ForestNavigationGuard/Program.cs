using System;
using System.Reflection;
using System.Linq;
using ForestNavigationGuard;
using HarmonyLib;
using Patch = ForestNavigationGuard.Patch;

internal static class Program
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    private static void Main()
    {
        Assembly root = Assembly.LoadFrom(Environment.GetEnvironmentVariable("ATT_GAME_PATH") + "/A Township Tale_Data/Managed/Root.Township.dll");
        MethodInfo method = Patch.Resolve(root);
        bool Select(object constraint, int index) => (bool)method.Invoke(constraint, new object?[] { index, null });
        object forest = Patch.Target!;
        Check(Select(forest, 0) && Select(forest, 32) && Select(forest, 64) && Select(forest, 96) && Select(forest, 128), "Could not reproduce installed graph-mask aliasing");
        Console.WriteLine("REPRODUCED: actual forest constraint incorrectly accepts 0,32,64,96,128 before patch.");
        object other = Activator.CreateInstance(forest.GetType())!;
        Patch.Mask.SetValue(other, 1);
        var allGraphs = Activator.CreateInstance(forest.GetType())!;
        var patcher = new HarmonyLib.Harmony("ATT.ForestNavigationGuard.Offline");
        var observer = new HarmonyLib.Harmony("ATT.ForestNavigationGuard.OtherOwner");
        try
        {
            observer.Patch(method, postfix: new HarmonyMethod(typeof(Program), nameof(Observe)));
            patcher.Patch(method, prefix: new HarmonyMethod(typeof(Patch), nameof(Patch.Prefix)));
            long initial = Patch.RejectedAliases;
            for (int index = 0; index <= 255; index++)
            {
                Check(Select(forest, index) == (index == 0), "forest matched wrong graph " + index);
                Check(Select(other, index) == ((index & 31) == 0), "unrelated mask changed " + index);
                Check(Select(allGraphs, index), "all-graphs navigation changed " + index);
            }
            Check(Patch.RejectedAliases - initial == 7, "alias counter mismatch");
            Patch.Mask.SetValue(forest, -1);
            Check(Select(forest, 33), "changed mask semantics not preserved");
            Patch.Mask.SetValue(forest, 1);
            Check(!Select(forest, 32), "guard did not resume for original mask");
            patcher.UnpatchSelf();
            Check(Select(forest, 32), "rollback did not restore original");
            Check(HarmonyLib.Harmony.GetPatchInfo(method).Owners.Contains(observer.Id), "rollback removed independent observer");
            Check(observations > 0, "independent observer did not run");
            Console.WriteLine("PASS: actual installed method patched; forest selects only graph 0 across indices 0..255; other mask/all-graphs instances and changed target mask preserved; counters, observer coexistence and rollback verified.");
        }
        finally { Patch.Mask.SetValue(forest, 1); patcher.UnpatchSelf(); observer.UnpatchSelf(); }
        RebuiltTileTests.Run(root);
    }
    private static int observations;
    private static void Observe() { observations++; }
}
