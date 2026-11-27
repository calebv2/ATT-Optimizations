using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Alta.Networking;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForestPerformance
{
    // A cached child array can contain a destroyed/detached entry after an
    // earlier child's teardown callbacks run. Guard only that iteration's
    // parent lookup; the native owner comparison, destruction and reparenting
    // policy remain the game's responsibility.
    internal static class PrefabUnloadGuard
    {
        private static readonly Guid Supported = new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff");
        private const BindingFlags InstanceMethods = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly;
        internal static long InvalidChildren, MissingParents;
        private static long nextReport;

        internal static void Attach(HarmonyLib.Harmony harmony)
        {
            if (harmony == null) throw new ArgumentNullException(nameof(harmony));
            MethodInfo target = ResolveTarget();
            harmony.Patch(target, transpiler: new HarmonyMethod(typeof(PrefabUnloadGuard), nameof(GuardParentLookup)));
        }

        internal static MethodInfo ResolveTarget()
        {
            if (typeof(SpawnManager).Module.ModuleVersionId != Supported)
                throw new NotSupportedException("Unsupported SpawnManager game build");
            MethodInfo? target = typeof(SpawnManager).GetMethod("DestroyChildPrefabs", InstanceMethods, null,
                new[] { typeof(INetworkEntity), typeof(NetworkPrefab), typeof(bool) }, null);
            if (target == null || target.ReturnType != typeof(void) || target.IsStatic)
                throw new MissingMethodException("SpawnManager.DestroyChildPrefabs");
            return target;
        }

        internal static IEnumerable<CodeInstruction> GuardParentLookup(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            if (__originalMethod != ResolveTarget()) throw new NotSupportedException("Unexpected prefab unload target");
            MethodInfo transform = typeof(Component).GetProperty(nameof(Component.transform))!.GetGetMethod();
            MethodInfo parent = typeof(Transform).GetProperty(nameof(Transform.parent))!.GetGetMethod();
            var code = instructions.Select(instruction => new CodeInstruction(instruction)).ToList();
            var sites = new List<int>();
            for (int i = 1; i + 4 < code.Count; i++)
            {
                if (!code[i].Calls(transform) || !code[i + 1].Calls(parent) ||
                    !(code[i + 2].operand is MethodInfo lookup) || !lookup.IsGenericMethod ||
                    lookup.DeclaringType != typeof(Component) || lookup.Name != "GetComponentInParent" ||
                    lookup.GetParameters().Length != 0 || lookup.GetGenericArguments().Length != 1 ||
                    lookup.GetGenericArguments()[0] != typeof(NetworkPrefab)) continue;
                // Exact native IL0021..0032: local2 child; transform; parent;
                // GetComponentInParent<NetworkPrefab>; arg2 root; bne.un next.
                if (code[i - 1].opcode != OpCodes.Ldloc_2 || code[i].opcode != OpCodes.Callvirt ||
                    code[i + 1].opcode != OpCodes.Callvirt || code[i + 2].opcode != OpCodes.Callvirt ||
                    code[i + 3].opcode != OpCodes.Ldarg_2 ||
                    (code[i + 4].opcode != OpCodes.Bne_Un && code[i + 4].opcode != OpCodes.Bne_Un_S) ||
                    !(code[i + 4].operand is Label) ||
                    code[i].blocks.Count != 0 || code[i + 1].blocks.Count != 0 || code[i + 2].blocks.Count != 0 ||
                    code[i + 1].labels.Count != 0 || code[i + 2].labels.Count != 0)
                    throw new NotSupportedException("Native child-parent ownership branch changed");
                sites.Add(i);
            }
            if (sites.Count != 1)
                throw new NotSupportedException("Expected one native child-parent lookup, found " + sites.Count);
            int site = sites[0];
            // Equivalent stack effect: child -> parent prefab. Keep the first
            // call's labels; the two interior calls are verified label-free.
            // No instructions in the native SaveMode/destruction body change.
            code[site].opcode = OpCodes.Ldarg_2; code[site].operand = null;
            code[site + 1].opcode = OpCodes.Ldarg_3; code[site + 1].operand = null;
            code[site + 2].opcode = OpCodes.Call;
            code[site + 2].operand = typeof(PrefabUnloadGuard).GetMethod(nameof(ParentForDestroy), BindingFlags.Static | BindingFlags.NonPublic)!;
            return code;
        }

        internal static NetworkPrefab? ParentForDestroy(NetworkPrefab child, NetworkPrefab expectedRoot, bool isUnload)
        {
            // Unity's overloaded comparison detects both managed null and a
            // surviving managed wrapper whose native object was destroyed.
            // Ordinary entries never read global scene/Core state, counters or
            // diagnostics, and still perform exactly one native parent lookup.
            string reason = "invalid_child";
            if (Alive(child))
            {
                Transform transform = ChildTransform(child);
                reason = "invalid_transform";
                if (Alive(transform))
                {
                    Transform parent = ParentTransform(transform);
                    if (Alive(parent)) return FindParent(parent);
                    reason = "detached_parent";
                }
            }
            if (!isUnload || !Core.Ready || !Core.Main || !Alive(expectedRoot) || !NetworkSceneManager.IsServer)
                return NativeParentLookup(child);
            if (reason == "detached_parent") MissingParents++;
            else InvalidChildren++;
            Report(reason, expectedRoot);
            return null;
        }

        // Explicit no-inline boundaries allow the offline fixture to substitute
        // Unity-native access without pretending to run the Unity engine.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool Alive(Object value) => value != null;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Transform ChildTransform(NetworkPrefab child) => child.transform;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Transform ParentTransform(Transform transform) => transform.parent;
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static NetworkPrefab? FindParent(Transform parent) => parent.GetComponentInParent<NetworkPrefab>();
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static NetworkPrefab? NativeParentLookup(NetworkPrefab child) => child.transform.parent.GetComponentInParent<NetworkPrefab>();

        private static void Report(string reason, NetworkPrefab root)
        {
            long now = Stopwatch.GetTimestamp();
            if (now < nextReport) return;
            nextReport = now + 60L * Stopwatch.Frequency;
            try
            {
                string chunk = "unknown_after_teardown";
                NetworkEntity entity = root.Entity;
                if (Alive(entity) && Alive(entity.Chunk)) chunk = entity.Chunk.ChunkIdentifier.ToString();
                Core.Row("prefab_unload_guard", 0, "reason=" + reason + ";root_prefab_hash=" + root.Hash + ";chunk=" + chunk +
                    ";invalid_children=" + InvalidChildren + ";missing_parents=" + MissingParents + ";action=skip_stale_child_lookup");
            }
            catch (Exception) { /* Diagnostics must not interrupt native teardown. */ }
        }
    }
}
