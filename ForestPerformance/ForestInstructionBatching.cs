using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Alta.Forests;
using Alta.Static;
using HarmonyLib;

namespace ForestPerformance
{
    // The native queued/reader branches increment their batch counter only for
    // non-null results. Route a forest null result into the existing counter
    // and compiler-generated yield/resume logic, without ever storing null in
    // Results. The in-memory branch already counts every instruction naturally.
    internal static class ForestInstructionBatching
    {
        internal static long CountedNull;
        private static readonly Guid Supported = new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff");
        internal static bool CountNull(object context, int nativeBatch)
        {
            bool count = Core.Ready && Core.Main && Core.Config.InstructionsPerFrame > 0 && nativeBatch > 0 && context is ForestGenerator;
            if (count) CountedNull++;
            return count;
        }
        internal static IEnumerable<CodeInstruction> CountEveryInstruction(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod)
        {
            if (__originalMethod.Module.ModuleVersionId != Supported || __originalMethod.Name != "MoveNext" ||
                __originalMethod.DeclaringType == null || __originalMethod.DeclaringType.DeclaringType != typeof(InstructionSet) ||
                !__originalMethod.DeclaringType.Name.StartsWith("<ExecuteInternal>d__", StringComparison.Ordinal))
                throw new NotSupportedException("Unsupported forest instruction iterator");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var owner = __originalMethod.DeclaringType;
            var context = owner.GetField("context", flags);
            var batch = owner.GetField("instructionsPerFrame", flags);
            var counter = owner.GetFields(flags).SingleOrDefault(f => f.Name.StartsWith("<count>5__", StringComparison.Ordinal));
            if (context?.FieldType != typeof(object) || batch?.FieldType != typeof(int) || counter?.FieldType != typeof(int))
                throw new NotSupportedException("Forest iterator fields changed");
            var add = typeof(List<IInstructionResult>).GetMethod("Add")!;
            var code = instructions.ToList();
            var sites = new List<int>();
            for (int i = 5; i + 4 < code.Count; i++)
            {
                if (!code[i].Calls(add)) continue;
                // Only the two result stores followed by count++, excluding
                // the earlier in-memory list loop's independent j++ counter.
                if (code[i + 1].opcode != OpCodes.Ldarg_0 || !code[i + 2].LoadsField(counter) ||
                    code[i + 3].opcode != OpCodes.Ldc_I4_1 || code[i + 4].opcode != OpCodes.Add) continue;
                int guard = i - 4;
                if ((code[guard].opcode != OpCodes.Brfalse && code[guard].opcode != OpCodes.Brfalse_S) ||
                    !(code[guard].operand is Label) || code[i - 5].opcode != code[i - 1].opcode ||
                    !Equals(code[i - 5].operand, code[i - 1].operand) ||
                    !(code[i - 2].operand is FieldInfo results) || results.Name != "results" || results.DeclaringType != typeof(InstructionSet) ||
                    code[guard].blocks.Count != 0 || code[i + 1].blocks.Count != 0)
                    throw new NotSupportedException("Forest null-result branch changed");
                sites.Add(i);
            }
            if (sites.Count != 2) throw new NotSupportedException("Expected exactly two queued/reader result counters, found " + sites.Count);
            foreach (int addIndex in sites.OrderByDescending(n => n))
            {
                int guardIndex = addIndex - 4;
                var guard = code[guardIndex];
                Label skip = (Label)guard.operand;
                Label nonnull = generator.DefineLabel(), countLabel = generator.DefineLabel();
                code[addIndex + 1].labels.Add(countLabel);
                var begin = new CodeInstruction(OpCodes.Dup);
                begin.labels.AddRange(guard.labels); guard.labels.Clear(); guard.labels.Add(nonnull);
                code.InsertRange(guardIndex, new[] {
                    begin,
                    new CodeInstruction(OpCodes.Brtrue, nonnull),
                    new CodeInstruction(OpCodes.Pop),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, context),
                    new CodeInstruction(OpCodes.Ldarg_0),
                    new CodeInstruction(OpCodes.Ldfld, batch),
                    new CodeInstruction(OpCodes.Call, typeof(ForestInstructionBatching).GetMethod(nameof(CountNull), BindingFlags.Static | BindingFlags.NonPublic)),
                    new CodeInstruction(OpCodes.Brtrue, countLabel),
                    new CodeInstruction(OpCodes.Br, skip)
                });
            }
            // Insertion may push a formerly short branch past its signed-byte
            // offset. Widen all branch forms without changing any destinations.
            foreach (var ins in code)
                if (ins.opcode.OperandType == OperandType.ShortInlineBrTarget)
                {
                    string name = ins.opcode.Name!;
                    string full = name.Substring(0, name.Length - 2);
                    ins.opcode = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
                        .Select(f => f.GetValue(null)).OfType<OpCode>().Single(op => op.Name == full);
                }
            return code;
        }
    }
}
