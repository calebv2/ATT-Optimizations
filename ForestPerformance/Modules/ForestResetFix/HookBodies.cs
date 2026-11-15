// This component predates nullable annotations.
#nullable disable
using System;
using System.Threading.Tasks;

namespace ForestResetFix
{
    // Argument arrays let the same tested hook bodies bind to the verified game signatures.
    public static class HookBodies
    {
        public static Func<object, int, Task> Reset;
        public static Action<object, object[]> Clean;

        public static bool WipePrefix(object __instance, object[] __args, ref Task __result, bool __runOriginal)
        {
            if (!__runOriginal || Reset == null) return true;
            __result = Reset(__instance, (int)__args[0]);
            return false;
        }
        public static void CleanPrefix(object __instance, object[] __args) { if (Clean != null) Clean(__instance, __args); }
    }

    public static class RiverArgument<TForest, TTerrain> where TTerrain : class
    {
        public static Func<object, TForest, TTerrain, TTerrain> Prepare;
        public static void Prefix(object __instance, TForest __0, ref TTerrain __1)
        {
            if (Prepare != null) __1 = Prepare(__instance, __0, __1);
        }
    }
}
