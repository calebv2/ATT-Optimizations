using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using Alta.Chunks;
using Alta.Intelligence;
using Alta.Networking;
using Alta.Populations;
using HarmonyLib;
using UnityEngine;

namespace CavePerformance
{
    internal static class CavePopulationTargets
    {
        internal enum Creature { Other, Wyrm, Turabada }
        private sealed class PrefabKind { internal Creature Value; }
        private sealed class Rounding
        {
            internal Rounding(ref int sequence) { Fraction = FractionFor((uint)Interlocked.Increment(ref sequence)); }
            internal readonly double Fraction;
        }

        private static readonly ConditionalWeakTable<NetworkPrefab, PrefabKind> Kinds = new();
        private static readonly ConditionalWeakTable<PopulationSample, Rounding> WyrmRounds = new();
        private static readonly ConditionalWeakTable<PopulationSample, Rounding> TurabadaRounds = new();
        private static int nextWyrmRounding, nextTurabadaRounding;
        private static FieldInfo distribution = null!, prefabHashes = null!, festiveGroups = null!, festiveHashes = null!;
        private static AccessTools.FieldRef<PopulationSample, bool> isOneOff = null!;
        private static MethodInfo ceil = null!;
        internal static MethodInfo Check = null!;
        internal static long WyrmReduced, TurabadaReduced;

        // Fixtures substitute component inspection. Production classifies the
        // prefab asset without spawning it or selecting a random variant.
        internal static Func<NetworkPrefab, Creature> ClassifyPrefab = prefab =>
        {
            Creature kind = Creature.Other;
            foreach (Agent agent in prefab.GetComponentsInChildren<Agent>(true))
            {
                Creature next = agent is Wyrm ? Creature.Wyrm : agent is Turabada ? Creature.Turabada : Creature.Other;
                if (!Include(ref kind, next)) return Creature.Other;
            }
            return kind;
        };

        internal static void Bind()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Check = typeof(PopulationSample).GetMethod("CheckSpawned", flags, null, Type.EmptyTypes, null)!;
            distribution = typeof(PopulationDefinition).GetField("distribution", flags)!;
            prefabHashes = typeof(PopulationDefinition).GetField("prefabHashes", flags)!;
            festiveGroups = typeof(PopulationDefinition).GetField("festiveGroups", flags)!;
            festiveHashes = festiveGroups?.FieldType.GetElementType()?.GetField("prefabHashes", flags)!;
            isOneOff = AccessTools.FieldRefAccess<PopulationSample, bool>("isOneOff");
            ceil = typeof(Mathf).GetMethod("CeilToInt", new[] { typeof(float) })!;
            if (Check == null || Check.ReturnType != typeof(void) || distribution?.FieldType != typeof(Alta.Caves.Distribution) ||
                prefabHashes?.FieldType != typeof(uint[]) || festiveHashes?.FieldType != typeof(uint[]) || ceil == null)
                throw new NotSupportedException("Population target metadata changed");
        }

        internal static void Attach(HarmonyLib.Harmony harmony)
            => harmony.Patch(Check, transpiler: new HarmonyMethod(typeof(CavePopulationTargets), nameof(Rewrite)));

        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var matches = list.Select((instruction, index) => (instruction, index)).Where(entry =>
                (entry.instruction.opcode == OpCodes.Call || entry.instruction.opcode == OpCodes.Callvirt) &&
                Equals(entry.instruction.operand, ceil)).ToArray();
            if (matches.Length != 1 || matches[0].instruction.blocks.Count != 0)
                throw new NotSupportedException("Expected one unambiguous native population target conversion");
            int at = matches[0].index;
            var owner = new CodeInstruction(OpCodes.Ldarg_0);
            owner.labels.AddRange(list[at].labels);
            list[at].labels.Clear();
            list[at].opcode = OpCodes.Call;
            list[at].operand = typeof(CavePopulationTargets).GetMethod(nameof(Target), BindingFlags.Static | BindingFlags.NonPublic);
            list.Insert(at, owner);
            return list;
        }

        private static bool Eligible(PopulationSample sample)
            => sample.Chunk != null && sample.Chunk.ProceduralStreamer is CaveLayerStreamer;

        private static Creature PrefabCreature(NetworkPrefab prefab) => prefab == null ? Creature.Other
            : Kinds.GetValue(prefab, asset => new PrefabKind { Value = ClassifyPrefab(asset) }).Value;

        private static Creature HashCreature(uint hash) => hash != 0 && PrefabManager.Exists(hash)
            ? PrefabCreature(PrefabManager.GetPrefab(hash)) : Creature.Other;

        private static bool Include(ref Creature kind, Creature next)
        {
            if (next == Creature.Other || (kind != Creature.Other && next != kind)) return false;
            kind = next;
            return true;
        }

        private static Creature DefinitionCreature(PopulationDefinition definition)
        {
            if (definition == null) return Creature.Other;
            Creature kind = Creature.Other;
            if (distribution.GetValue(definition) is Alta.Caves.Distribution dist)
            {
                // Include inactive festive alternatives so a mixed population
                // always retains its native target.
                int count = 0;
                foreach (var item in dist.Items)
                    if (++count > 256 || !(item.Topic is NetworkPrefab prefab) || !Include(ref kind, PrefabCreature(prefab)))
                        return Creature.Other;
                foreach (var group in dist.Festivities)
                    foreach (var item in group.Items)
                        if (++count > 256 || !(item.Topic is NetworkPrefab prefab) || !Include(ref kind, PrefabCreature(prefab)))
                            return Creature.Other;
                return kind;
            }
            var hashes = prefabHashes.GetValue(definition) as uint[];
            if (hashes == null || hashes.Length == 0 || hashes.Length > 256) return Creature.Other;
            foreach (uint hash in hashes)
                if (!Include(ref kind, HashCreature(hash))) return Creature.Other;
            if (festiveGroups.GetValue(definition) is IEnumerable groups)
                foreach (object group in groups)
                {
                    if (!(festiveHashes.GetValue(group) is uint[] alternatives)) return Creature.Other;
                    foreach (uint hash in alternatives)
                        if (!Include(ref kind, HashCreature(hash))) return Creature.Other;
                }
            return kind;
        }

        internal static double FractionFor(uint value)
        {
            // Stratified rounding lets small samples average the same reduction
            // as large ones. Each species advances its own sequence.
            value = (value >> 16) | (value << 16);
            value = ((value & 0xFF00FF00u) >> 8) | ((value & 0x00FF00FFu) << 8);
            value = ((value & 0xF0F0F0F0u) >> 4) | ((value & 0x0F0F0F0Fu) << 4);
            value = ((value & 0xCCCCCCCCu) >> 2) | ((value & 0x33333333u) << 2);
            value = ((value & 0xAAAAAAAAu) >> 1) | ((value & 0x55555555u) << 1);
            return value / 4294967296.0;
        }

        internal static int ReducedTarget(int native, int existing, float multiplier, double fraction = 0.5)
        {
            if (native <= 0 || multiplier >= 1 || float.IsNaN(multiplier) || multiplier < 0) return native;
            int target = (int)Math.Floor(native * (double)multiplier + fraction);
            // A negative deficit in CheckSpawned would inflate the saved hidden
            // population. Count existing creatures toward the reduced target.
            return Math.Max(existing, target);
        }

        internal static int Target(float nativeValue, PopulationSample sample)
        {
            int native = Mathf.CeilToInt(nativeValue);
            if (!Core.Ready || !Core.Main || native <= 0 ||
                (Core.Config.WyrmMultiplier >= 1 && Core.Config.TurabadaMultiplier >= 1)) return native;
            try
            {
                if (!Eligible(sample)) return native;
                Creature kind = DefinitionCreature(sample.Definition);
                if (kind == Creature.Other || (kind == Creature.Turabada && isOneOff(sample))) return native;
                bool wyrm = kind == Creature.Wyrm;
                float multiplier = wyrm ? Core.Config.WyrmMultiplier : Core.Config.TurabadaMultiplier;
                if (multiplier >= 1) return native;
                double fraction = wyrm
                    ? WyrmRounds.GetValue(sample, _ => new Rounding(ref nextWyrmRounding)).Fraction
                    : TurabadaRounds.GetValue(sample, _ => new Rounding(ref nextTurabadaRounding)).Fraction;
                int result = ReducedTarget(native, sample.SpawnedEntities.Count, multiplier, fraction);
                if (result < native)
                {
                    if (wyrm) Interlocked.Increment(ref WyrmReduced);
                    else Interlocked.Increment(ref TurabadaReduced);
                }
                return result;
            }
            catch { Interlocked.Increment(ref Core.Errors); return native; }
        }
    }
}
