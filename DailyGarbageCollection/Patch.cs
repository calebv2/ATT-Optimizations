using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace DailyGarbageCollection
{
    internal static class Patch
    {
        internal static readonly Guid SupportedMvid = new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff");
        internal static long SuppressedCaveCollections;
        internal static bool SkipGameTimer() { return false; }
        internal static void SkipScanCollection() { Interlocked.Increment(ref SuppressedCaveCollections); }
        internal static IEnumerable<CodeInstruction> ReplaceScanCollect(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var target = typeof(GC).GetMethod(nameof(GC.Collect), Type.EmptyTypes)!;
            var replacement = typeof(Patch).GetMethod(nameof(SkipScanCollection), BindingFlags.Static | BindingFlags.NonPublic)!;
            int count = 0;
            foreach (var instruction in list)
            {
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && instruction.operand is MethodInfo method && method.Equals(target))
                { instruction.opcode = OpCodes.Call; instruction.operand = replacement; count++; }
            }
            if (count != 1) throw new InvalidOperationException("Expected exactly one GC.Collect() in pathfinding scan; found " + count);
            return list;
        }
        internal static (MethodInfo timer, MethodInfo scan) Resolve(Assembly root)
        {
            if (root.ManifestModule.ModuleVersionId != SupportedMvid) throw new InvalidOperationException("Unsupported game assembly; no changes applied");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            Type timerType = root.GetType("GarbageCollectTimer", true)!;
            MethodInfo timer = timerType.GetMethod("Start", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(timerType.FullName, "Start");
            if (timer.IsStatic || timer.ReturnType != typeof(void)) throw new InvalidOperationException("Unexpected timer Start signature");
            FieldInfo running = timerType.GetField("runGcCollect", flags) ?? throw new MissingFieldException(timerType.FullName, "runGcCollect");
            if (running.GetValue(null) != null) throw new InvalidOperationException("Game timer already running; refusing partial change");
            Type astar = root.GetType("AstarPath", true)!;
            Type[] types = astar.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => t.Name.StartsWith("<ScanAsync>d__", StringComparison.Ordinal)).ToArray();
            if (types.Length != 1) throw new InvalidOperationException("Expected exactly one AstarPath.ScanAsync iterator");
            MethodInfo scan = types[0].GetMethod("MoveNext", flags, null, Type.EmptyTypes, null) ?? throw new MissingMethodException(types[0].FullName, "MoveNext");
            if (scan.IsStatic || scan.ReturnType != typeof(bool)) throw new InvalidOperationException("Unexpected scan iterator signature");
            return (timer, scan);
        }
    }
}
