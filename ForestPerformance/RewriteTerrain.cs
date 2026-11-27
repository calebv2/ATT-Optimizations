using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Alta.Forests;
using Alta.Static;
using HarmonyLib;
using UnityEngine;
using Grid = ForestPerformance.RewriteTerrainMask.Grid;
using Point = ForestPerformance.RewriteTerrainMask.Point;

namespace ForestPerformance
{
    internal static class RewriteTerrain
    {
        private static readonly Guid SupportedRewrite = new Guid("8f66b69d-a3d9-4413-9664-c354fa010485");
        internal static readonly Guid Rewrite053 = new Guid("4f74eeb8-12e3-45da-bde7-7984a37f034b");
        internal static readonly Guid Rewrite054 = new Guid("c791bc08-34ce-4603-906a-b0180d8eac19");
        private const long CellLimit = 8_388_608;
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly ConditionalWeakTable<TerrainAccess, Prepared> prepared = new ConditionalWeakTable<TerrainAccess, Prepared>();
        private static FieldInfo? scopeField, terrainField, maskField, pointsField, xField, zField;
        private static MethodInfo? stateGetter;
        private static Func<ForestGenerator, bool>? isModified;
        private static Prepared? previous;
        private static int preparing;
        internal static bool Installed { get; private set; }

        private sealed class Prepared
        {
            internal readonly ForestGenerator Owner;
            internal readonly Point[] Outline;
            internal readonly Grid Heights, Splat;
            internal readonly RewriteTerrainMask Mask;

            internal Prepared(ForestGenerator owner, Point[] outline, Grid heights, Grid splat, RewriteTerrainMask mask)
            {
                Owner = owner; Outline = outline; Heights = heights; Splat = splat; Mask = mask;
            }
        }

        internal static bool Attach(HarmonyLib.Harmony harmony)
        {
            var rewrite = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "ForestRewrite.Server");
            if (rewrite == null) return false;
            Guid build = rewrite.ManifestModule.ModuleVersionId;
            if (build != SupportedRewrite && build != Rewrite053 && build != Rewrite054)
                throw new NotSupportedException("Unrecognized ForestRewrite build for terrain blend preparation");

            var mod = rewrite.GetType("ForestRewrite.ForestRewriteMod", true)!;
            var state = mod.GetNestedType("State", BindingFlags.NonPublic)!;
            var boundary = rewrite.GetType("ForestRewrite.Boundary", true)!;
            var point = rewrite.GetType("ForestRewrite.Point", true)!;
            scopeField = mod.GetField("terrainScope", StaticPrivate)!;
            terrainField = state.GetField("Terrain")!;
            maskField = state.GetField("Mask")!;
            pointsField = boundary.GetField("Points")!;
            xField = point.GetField("x")!;
            zField = point.GetField("z")!;
            stateGetter = mod.GetMethod("For", StaticPrivate, null, new[] { typeof(ForestGenerator) }, null)!;
            isModified = (Func<ForestGenerator, bool>)mod.GetMethod("Modified", StaticPrivate)!
                .CreateDelegate(typeof(Func<ForestGenerator, bool>));
            if (scopeField?.FieldType != typeof(AsyncLocal<ForestGenerator>) || terrainField?.FieldType != typeof(TerrainInfo) ||
                maskField?.FieldType != boundary || pointsField?.FieldType != point.MakeArrayType() || xField?.FieldType != typeof(double) ||
                zField?.FieldType != typeof(double) || stateGetter?.ReturnType != state)
                throw new MissingMemberException("ForestRewrite terrain bindings differ from the supported build");

            var terrain = typeof(ForestGenerator).GetMethod("GenerateTerrain", BindingFlags.Instance | BindingFlags.NonPublic,
                null, new[] { typeof(ForestGeneration) }, null)!;
            var moveNext = AccessTools.DeclaredMethod(terrain.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType, "MoveNext");
            harmony.Patch(moveNext, transpiler: Own(nameof(PrepareBeforeApply)));
            harmony.Patch(RewritePrefix(mod, "HeightPrefix"), prefix: Own(nameof(ApplyHeightBlend)));
            harmony.Patch(RewritePrefix(mod, "SplatPrefix"), prefix: Own(nameof(ApplySplatBlend)));
            foreach (string name in new[] { nameof(TerrainAccess.ApplyHeights), nameof(TerrainAccess.ApplySplat) })
                harmony.Patch(typeof(TerrainAccess).GetMethod(name), prefix: Own(nameof(BeginNativeApply), Priority.Last),
                    finalizer: Own(nameof(EndNativeApply), Priority.First));
            Installed = true;
            return true;
        }

        internal static void Shutdown()
        {
            Installed = false;
            previous = null;
        }

        private static MethodInfo RewritePrefix(Type mod, string name) =>
            mod.GetMethod(name, StaticPrivate, null, new[] { typeof(TerrainAccess) }, null)
                ?? throw new MissingMethodException(mod.FullName, name);

        private static HarmonyMethod Own(string name, int priority = Priority.Normal) =>
            new HarmonyMethod(typeof(RewriteTerrain).GetMethod(name, StaticPrivate)) { priority = priority };

        private static IEnumerable<CodeInstruction> PrepareBeforeApply(IEnumerable<CodeInstruction> instructions)
        {
            var body = instructions.ToList();
            int match = -1;
            for (int i = 0; i < body.Count; i++)
            {
                if (!(body[i].operand is MethodInfo call) || call.Name != "GetCache" || call.DeclaringType?.Name != "CacheManager" ||
                    !call.IsGenericMethod || call.ReturnType != typeof(Task<TerrainAccess>)) continue;
                if (match >= 0) throw new InvalidOperationException("Multiple terrain cache calls in GenerateTerrain");
                match = i;
            }
            if (match < 0) throw new InvalidOperationException("Terrain cache call missing from GenerateTerrain");
            body.Insert(match + 1, new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(RewriteTerrain), nameof(Prepare))));
            return body;
        }

        private static ForestGenerator? Owner() => ((AsyncLocal<ForestGenerator>)scopeField!.GetValue(null)).Value;

        private static async Task<TerrainAccess> Prepare(Task<TerrainAccess> cache)
        {
            var access = await cache;
            if (!Installed || !Core.Main || access == null) return access!;
            if (Interlocked.CompareExchange(ref preparing, 1, 0) != 0) return access;
            try
            {
                var owner = Owner();
                if (owner == null || !isModified!(owner)) return access;
                var state = stateGetter!.Invoke(null, new object[] { owner });
                if ((TerrainInfo)terrainField!.GetValue(state) != access.Terrain) return access;
                var mask = maskField!.GetValue(state);
                var points = (Array)pointsField!.GetValue(mask);
                var outline = new Point[points.Length];
                for (int i = 0; i < outline.Length; i++)
                {
                    var point = points.GetValue(i);
                    outline[i] = new Point((double)xField!.GetValue(point), (double)zField!.GetValue(point));
                }
                if (outline.Length < 3 || outline.Length > 256) return access;

                Grid heights = AccessGrid(access, false);
                Grid splat = AccessGrid(access, true);
                long cells = (long)heights.Width * heights.Height + (long)splat.Width * splat.Height;
                if (cells <= 0 || cells > CellLimit) return access;
                Prepared? last = previous;
                var clock = Stopwatch.StartNew();
                bool reused = last != null && heights.Equals(last.Heights) && splat.Equals(last.Splat) && outline.SequenceEqual(last.Outline);
                var weights = reused ? last!.Mask : await Task.Run(() => RewriteTerrainMask.Build(outline, heights, splat));
                if (Installed)
                {
                    var result = new Prepared(owner, outline, heights, splat, weights);
                    prepared.Remove(access);
                    prepared.Add(access, result);
                    previous = result;
                    Core.Row("rewrite_terrain_mask_ready", clock.Elapsed.TotalMilliseconds,
                        "cells=" + cells + ";reused=" + reused + ";execution=worker_or_cache");
                }
            }
            catch (Exception error)
            {
                Core.Row("rewrite_terrain_mask_fallback", 0, "preparation_failed", error);
            }
            finally { Interlocked.Exchange(ref preparing, 0); }
            return access;
        }

        private static Grid AccessGrid(TerrainAccess access, bool splat)
        {
            Vector3 offset = access.Terrain.Offset;
            Vector3 unit = access.Terrain.Unit;
            int width = splat ? access.SplatWidth : access.HeightsWidth;
            int height = splat ? access.SplatHeight : access.HeightsHeight;
            if (width < 0 || height < 0 || !(unit.x > 0f) || !(unit.z > 0f))
                throw new InvalidOperationException("Unsupported forest terrain grid");
            return new Grid(access.X, access.Y, width, height, splat ? 2 : 1, offset.x, offset.z, unit.x, unit.z);
        }

        private static Prepared? Lookup(TerrainAccess access) =>
            Installed && Core.Main && prepared.TryGetValue(access, out var value) && ReferenceEquals(value.Owner, Owner()) ? value : null;

        // These prefixes replace only the two ForestRewrite callbacks. Unity's native commits still run.
        private static bool ApplyHeightBlend(TerrainAccess __0)
        {
            var value = Lookup(__0);
            if (value == null) return true;
            var generated = __0.Heights;
            if (generated == null || !value.Heights.Equals(AccessGrid(__0, false))) return true;
            var clock = Stopwatch.StartNew();
            Grid grid = value.Heights;
            var original = __0.Terrain.TerrainData.GetHeights(grid.X, grid.Y, grid.Width, grid.Height);
            BlendHeights(generated, original, value.Mask.Heights);
            Core.Row("rewrite_terrain_height_blend", clock.Elapsed.TotalMilliseconds, "samples=" + value.Mask.Heights.Length);
            return false;
        }

        internal static void BlendHeights(float[,] generated, float[,] original, float[] weights)
        {
            int width = generated.GetLength(1);
            for (int row = 0; row < generated.GetLength(0); row++)
                for (int column = 0; column < width; column++)
                    generated[row, column] = Mathf.Lerp(original[row, column], generated[row, column], weights[row * width + column]);
        }

        private static bool ApplySplatBlend(TerrainAccess __0)
        {
            var value = Lookup(__0);
            if (value == null) return true;
            var generated = __0.Splat;
            if (generated == null || !value.Splat.Equals(AccessGrid(__0, true))) return true;
            var texture = __0.Terrain.Splat;
            if (texture == null) return true;
            var clock = Stopwatch.StartNew();
            Grid grid = value.Splat;
            var original = new SplatData[grid.Width, grid.Height];
            SplatData.ToSplat(texture.GetPixels(grid.X / 2, grid.Y / 2, grid.Width, grid.Height), original);
            for (int column = 0; column < grid.Width; column++)
                for (int row = 0; row < grid.Height; row++)
                    if (!value.Mask.KeepSplat[row * grid.Width + column]) generated[column, row] = original[column, row];
            Core.Row("rewrite_terrain_splat_blend", clock.Elapsed.TotalMilliseconds, "samples=" + value.Mask.KeepSplat.Length);
            return false;
        }

        private static void BeginNativeApply(TerrainAccess __instance, out long __state) =>
            __state = Lookup(__instance) == null ? 0 : Stopwatch.GetTimestamp();

        private static void EndNativeApply(MethodBase __originalMethod, long __state, Exception? __exception)
        {
            if (__state != 0)
                Core.Row("rewrite_terrain_native_apply", (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency,
                    "method=" + __originalMethod.Name, __exception);
        }
    }
}
