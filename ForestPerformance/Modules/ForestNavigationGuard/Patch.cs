using System;
using System.Reflection;
using System.Threading;

namespace ForestNavigationGuard
{
    internal static class Patch
    {
        internal static readonly Guid SupportedMvid = new Guid("29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff");
        internal static object? Target;
        internal static FieldInfo Mask = null!;
        internal static long RejectedAliases;
        internal static MethodInfo Resolve(Assembly root)
        {
            if (root.ManifestModule.ModuleVersionId != SupportedMvid)
                throw new NotSupportedException("Unsupported game assembly");
            Type owner = root.GetType("Alta.Forests.ForestGenerator", true)!;
            Type constraint = root.GetType("Pathfinding.NNConstraint", true)!;
            Type graph = root.GetType("Pathfinding.NavGraph", true)!;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            FieldInfo field = owner.GetField("overworldOnlyAstar", flags) ?? throw new MissingFieldException(owner.FullName, "overworldOnlyAstar");
            if (!field.IsStatic || !field.IsInitOnly || field.FieldType != constraint) throw new NotSupportedException("Unexpected forest constraint field");
            object target = field.GetValue(null) ?? throw new InvalidOperationException("Forest constraint is null");
            Mask = constraint.GetField("graphMask", flags) ?? throw new MissingFieldException(constraint.FullName, "graphMask");
            if (Mask.IsStatic || Mask.FieldType != typeof(int) || target.GetType() != constraint || (int)Mask.GetValue(target) != 1)
                throw new NotSupportedException("Forest constraint is not the inspected overworld-only mask");
            MethodInfo method = constraint.GetMethod("SuitableGraph", flags, null, new[] { typeof(int), graph }, null)
                ?? throw new MissingMethodException(constraint.FullName, "SuitableGraph");
            if (method.IsStatic || !method.IsVirtual || method.ReturnType != typeof(bool) || method.DeclaringType != constraint)
                throw new NotSupportedException("Unexpected graph selection method");
            Target = target;
            return method;
        }
        internal static bool Prefix(object __instance, int __0, ref bool __result)
        {
            // Narrow identity guard: all other constraints, including all-graphs masks,
            // retain the game's existing semantics. Lower indices keep the original call.
            if (!ReferenceEquals(__instance, Target) || __0 < 32) return true;
            if ((int)Mask.GetValue(__instance) != 1) return true;
            __result = false;
            if ((__0 & 31) == 0) Interlocked.Increment(ref RejectedAliases);
            return false;
        }
    }
}
