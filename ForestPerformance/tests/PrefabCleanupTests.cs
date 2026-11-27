using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Alta.Networking;
using ForestPerformance;
using HarmonyLib;
using UnityEngine;
using UnityObject = UnityEngine.Object;

internal static class PrefabCleanupTests
{
    private static readonly MethodInfo DestroyChildren = AccessTools.DeclaredMethod(typeof(SpawnManager), "DestroyChildPrefabs");
    private static readonly MethodInfo GuardLookup = AccessTools.DeclaredMethod(typeof(PrefabUnloadGuard), "ParentForDestroy");
    private static NetworkPrefab? foundRoot;
    private static Transform? childTransform;
    private static Transform? parentTransform;
    private static UnityObject? destroyed;
    private static bool server;
    private static int lookups;

    internal static void Run()
    {
        CheckNativeAttachment();
        CheckLookup();
        Console.WriteLine("PASS prefab unload guard: native hook attachment, unchanged destroy/reparent tail, rejection of missing/duplicate calls, null/destroyed hierarchy skips and unguarded behavior. Unity calls isolated.");
    }

    private static void CheckNativeAttachment()
    {
        var harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.PrefabCleanupTests.Native");
        var original = PatchProcessor.GetOriginalInstructions(DestroyChildren);
        int lookup = original.FindIndex(IsParentLookup);
        Check(lookup >= 3 && original.Count(IsParentLookup) == 1, "Native parent lookup is not unique");
        Expect<NotSupportedException>(() => PrefabUnloadGuard.GuardParentLookup(
            new[] { new CodeInstruction(OpCodes.Ret) }, DestroyChildren).ToArray());
        var changed = original.Select(item => new CodeInstruction(item)).ToList();
        changed[lookup + 2].opcode = OpCodes.Beq;
        Expect<NotSupportedException>(() => PrefabUnloadGuard.GuardParentLookup(changed, DestroyChildren).ToArray());
        var duplicate = original.Select(item => new CodeInstruction(item)).ToList();
        duplicate.InsertRange(lookup + 3, original.Skip(lookup - 3).Take(6).Select(item => new CodeInstruction(item)));
        Expect<NotSupportedException>(() => PrefabUnloadGuard.GuardParentLookup(duplicate, DestroyChildren).ToArray());
        try
        {
            PrefabUnloadGuard.Attach(harmony);
            Check(HarmonyLib.Harmony.GetPatchInfo(DestroyChildren)?.Transpilers.Any(patch => patch.owner == harmony.Id) == true,
                "Native prefab cleanup hook did not attach");
            var patched = PatchProcessor.GetCurrentInstructions(DestroyChildren);
            int guard = patched.FindIndex(instruction => instruction.Calls(GuardLookup));
            Check(guard >= 0 && patched.Count(instruction => instruction.Calls(GuardLookup)) == 1 && !patched.Any(IsParentLookup),
                "Expected one guarded native parent lookup");
            Check(original.Skip(lookup + 1).Select(ComparableInstruction)
                .SequenceEqual(patched.Skip(guard + 1).Select(ComparableInstruction)), "The native comparison, destroy or reparent IL changed");
        }
        finally { harmony.UnpatchSelf(); }
    }

    private static (string?, object?) ComparableInstruction(CodeInstruction instruction)
    {
        if (instruction.operand is Label)
            return (instruction.opcode.Name?.Replace(".s", ""), null);
        return (instruction.opcode.Name, instruction.operand);
    }

    private static void CheckLookup()
    {
        var harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.PrefabCleanupTests.UnityBoundaries");
        bool ready = Core.Ready;
        int mainThread = Core.MainThread;
        try
        {
            var shim = new HarmonyMethod(typeof(PrefabCleanupTests), nameof(ShimUnityCalls));
            harmony.Patch(GuardLookup, transpiler: shim);
            harmony.Patch(AccessTools.DeclaredMethod(typeof(PrefabUnloadGuard), nameof(PrefabUnloadGuard.NativeParentLookup)), transpiler: shim);
            Core.Ready = true;
            Core.MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            server = true;
            var child = Uninitialized<NetworkPrefab>();
            var root = Uninitialized<NetworkPrefab>();
            var otherRoot = Uninitialized<NetworkPrefab>();
            childTransform = Uninitialized<Transform>();
            parentTransform = Uninitialized<Transform>();
            foundRoot = root;
            lookups = 0;
            long invalid = PrefabUnloadGuard.InvalidChildren;
            long detached = PrefabUnloadGuard.MissingParents;

            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), root), "Owned child lost its root");
            foundRoot = otherRoot;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), otherRoot), "Nested child lost its actual root");
            Check(lookups == 2, "Valid paths did not use the native lookup boundary exactly once");

            destroyed = child;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), null), "Destroyed child was not skipped");
            destroyed = parentTransform;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), null), "Destroyed parent was not skipped");
            destroyed = childTransform;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), null), "Destroyed transform was not skipped");
            destroyed = null;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(null!, root, true), null), "Null child was not skipped");
            childTransform = null;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), null), "Missing child transform was not skipped");
            childTransform = Uninitialized<Transform>();
            parentTransform = null;
            Check(ReferenceEquals(PrefabUnloadGuard.ParentForDestroy(child, root, true), null), "Detached child was not skipped");
            Check(lookups == 2, "A missing hierarchy reached the Unity lookup boundary");

            Expect<NullReferenceException>(() => PrefabUnloadGuard.ParentForDestroy(child, root, false));
            Expect<NullReferenceException>(() => PrefabUnloadGuard.ParentForDestroy(child, null!, true));
            server = false;
            Expect<NullReferenceException>(() => PrefabUnloadGuard.ParentForDestroy(child, root, true));
            server = true;
            Core.MainThread = -1;
            Expect<NullReferenceException>(() => PrefabUnloadGuard.ParentForDestroy(child, root, true));
            Core.MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            Core.Ready = false;
            Expect<NullReferenceException>(() => PrefabUnloadGuard.ParentForDestroy(child, root, true));
            Check(PrefabUnloadGuard.InvalidChildren == invalid + 4 && PrefabUnloadGuard.MissingParents == detached + 2,
                "Guard counters include a valid or out-of-scope lookup or missed a stale hierarchy");
        }
        finally
        {
            Core.Ready = ready;
            Core.MainThread = mainThread;
            harmony.UnpatchSelf();
            childTransform = null;
            parentTransform = null;
            foundRoot = null;
            destroyed = null;
        }
    }

    private static bool IsParentLookup(CodeInstruction instruction) => instruction.operand is MethodInfo method &&
        method.DeclaringType == typeof(Component) && method.Name == nameof(Component.GetComponentInParent) &&
        method.IsGenericMethod && method.GetGenericArguments().Single() == typeof(NetworkPrefab) && method.GetParameters().Length == 0;

    private static IEnumerable<CodeInstruction> ShimUnityCalls(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (!(instruction.operand is MethodInfo method)) { yield return instruction; continue; }
            string? replacement = IsParentLookup(instruction) ? nameof(ReadRoot) : null;
            if (method.DeclaringType == typeof(PrefabUnloadGuard))
                replacement = method.Name switch
                {
                    nameof(PrefabUnloadGuard.Alive) => nameof(Alive),
                    nameof(PrefabUnloadGuard.ChildTransform) => nameof(ReadTransform),
                    nameof(PrefabUnloadGuard.ParentTransform) => nameof(ReadParent),
                    nameof(PrefabUnloadGuard.FindParent) => nameof(ReadRoot),
                    _ => replacement
                };
            else if (method.DeclaringType == typeof(Component) && method.Name == "get_transform") replacement = nameof(ReadTransform);
            else if (method.DeclaringType == typeof(Transform) && method.Name == "get_parent") replacement = nameof(ReadParent);
            else if (method.DeclaringType == typeof(NetworkSceneManager) && method.Name == "get_IsServer") replacement = nameof(IsServer);
            if (replacement != null)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = AccessTools.DeclaredMethod(typeof(PrefabCleanupTests), replacement);
            }
            yield return instruction;
        }
    }

    private static bool IsServer() => server;
    private static bool Alive(UnityObject? value) => !ReferenceEquals(value, null) && !ReferenceEquals(value, destroyed);
    private static Transform? ReadTransform(Component child) { RequireObject(child); return childTransform; }
    private static Transform? ReadParent(Transform child) { RequireObject(child); return parentTransform; }
    private static NetworkPrefab? ReadRoot(Transform parent) { RequireObject(parent); lookups++; return foundRoot; }

    private static void RequireObject(UnityObject value)
    {
        if (ReferenceEquals(value, null)) throw new NullReferenceException();
        if (!Alive(value)) throw new MissingReferenceException();
    }

    private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
