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
    internal static class WyrmPopulation
    {
        private sealed class PrefabKind { internal bool Wyrm; }
        private sealed class Rounding
        {
            public Rounding() { Fraction = FractionFor((uint)Interlocked.Increment(ref nextRounding)); }
            internal readonly double Fraction;
        }
        private static readonly ConditionalWeakTable<NetworkPrefab, PrefabKind> Kinds = new ConditionalWeakTable<NetworkPrefab, PrefabKind>();
        private static readonly ConditionalWeakTable<PopulationSample, Rounding> Rounds = new ConditionalWeakTable<PopulationSample, Rounding>();
        private static int nextRounding;
        private static FieldInfo distribution = null!, prefabHashes = null!, festiveGroups = null!, festiveHashes = null!;
        private static MethodInfo ceil = null!;
        internal static MethodInfo Check = null!;
        internal static long Reduced;
        // Fixtures replace component inspection only; production reads the
        // native prefab asset without instantiating or selecting random spawns.
        internal static Func<NetworkPrefab, bool> IsWyrm = p =>
        {
            var agents = p.GetComponentsInChildren<Agent>(true);
            return agents.Length > 0 && agents.All(a => a is Wyrm);
        };
        internal static void Bind()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Check = typeof(PopulationSample).GetMethod("CheckSpawned", flags, null, Type.EmptyTypes, null)!;
            distribution = typeof(PopulationDefinition).GetField("distribution", flags)!;
            prefabHashes = typeof(PopulationDefinition).GetField("prefabHashes", flags)!;
            festiveGroups = typeof(PopulationDefinition).GetField("festiveGroups", flags)!;
            festiveHashes = festiveGroups?.FieldType.GetElementType()?.GetField("prefabHashes", flags)!;
            ceil = typeof(Mathf).GetMethod("CeilToInt", new[] { typeof(float) })!;
            if (Check == null || Check.ReturnType != typeof(void) || distribution?.FieldType != typeof(Alta.Caves.Distribution) ||
                prefabHashes?.FieldType != typeof(uint[]) || festiveHashes?.FieldType != typeof(uint[]) || ceil == null)
                throw new NotSupportedException("Population target metadata changed");
        }
        internal static void Attach(HarmonyLib.Harmony h) => h.Patch(Check, transpiler: new HarmonyMethod(typeof(WyrmPopulation), nameof(Rewrite)));
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var matches = list.Select((i,index) => new { i,index }).Where(x =>
                (x.i.opcode == OpCodes.Call || x.i.opcode == OpCodes.Callvirt) && x.i.operand is MethodInfo m && m.Equals(ceil)).ToArray();
            if (matches.Length != 1 || matches[0].i.blocks.Count != 0) throw new NotSupportedException("Expected one unambiguous native population target conversion");
            int at = matches[0].index;
            var owner = new CodeInstruction(OpCodes.Ldarg_0);owner.labels.AddRange(list[at].labels);list[at].labels.Clear();
            list[at].opcode = OpCodes.Call;list[at].operand = typeof(WyrmPopulation).GetMethod(nameof(Target), BindingFlags.Static | BindingFlags.NonPublic);
            list.Insert(at, owner);return list;
        }
        internal static bool Eligible(PopulationSample sample)
        {
            // No floor-number filter: this includes cave layer zero and every
            // deeper layer. The concrete cave streamer excludes surface areas.
            return sample.Chunk != null && sample.Chunk.ProceduralStreamer is CaveLayerStreamer;
        }
        private static bool KnownWyrm(NetworkPrefab prefab)
        {
            if (prefab == null) return false;
            return Kinds.GetValue(prefab, p => new PrefabKind { Wyrm = IsWyrm(p) }).Wyrm;
        }
        private static bool HashIsWyrm(uint hash)
        {
            if (hash == 0 || !PrefabManager.Exists(hash)) return false;
            return KnownWyrm(PrefabManager.GetPrefab(hash));
        }
        private static bool DefinitionIsWyrm(PopulationDefinition definition)
        {
            if (definition == null) return false;
            if (distribution.GetValue(definition) is Alta.Caves.Distribution dist)
            {
                int count = 0;
                // Include inactive festive alternatives too; a mixed/unknown
                // population must not reduce unrelated creatures later.
                foreach (var item in dist.Items)
                {
                    if (++count > 256 || !(item.Topic is NetworkPrefab prefab) || !KnownWyrm(prefab)) return false;
                }
                foreach (var group in dist.Festivities)
                    foreach (var item in group.Items)
                        if (++count > 256 || !(item.Topic is NetworkPrefab prefab) || !KnownWyrm(prefab)) return false;
                return count > 0;
            }
            var hashes = prefabHashes.GetValue(definition) as uint[];
            if (hashes == null || hashes.Length == 0 || hashes.Length > 256 || !hashes.All(HashIsWyrm)) return false;
            if (festiveGroups.GetValue(definition) is IEnumerable groups)
                foreach (object group in groups)
                    if (!(festiveHashes.GetValue(group) is uint[] alternatives) || !alternatives.All(HashIsWyrm)) return false;
            return true;
        }
        internal static double FractionFor(uint value)
        {
            // A stable, stratified fraction per sample. Every four consecutive
            // samples span the four quarters, so small groups do not all round
            // back up to the original target. No Unity/world RNG is consumed.
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
            // Native CheckSpawned subtracts a negative deficit from its hidden
            // population. Avoid that side effect when live counts exceed the
            // new target; existing wyrms are retained and not force-despawned.
            return Math.Max(existing, target);
        }
        internal static int Target(float nativeValue, PopulationSample sample)
        {
            int native = Mathf.CeilToInt(nativeValue);
            if (!Core.Ready || !Core.Main || Core.Config.WyrmMultiplier >= 1 || native <= 0) return native;
            try
            {
                if (!Eligible(sample) || !DefinitionIsWyrm(sample.Definition)) return native;
                int result = ReducedTarget(native, sample.SpawnedEntities.Count, Core.Config.WyrmMultiplier, Rounds.GetOrCreateValue(sample).Fraction);
                if (result < native) Interlocked.Increment(ref Reduced);
                return result;
            }
            catch { Interlocked.Increment(ref Core.Errors); return native; }
        }
    }
}
