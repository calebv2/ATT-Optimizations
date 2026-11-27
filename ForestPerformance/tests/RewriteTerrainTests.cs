using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Alta;
using ForestPerformance;
using HarmonyLib;
using UnityEngine;
using Grid = ForestPerformance.RewriteTerrainMask.Grid;
using Point = ForestPerformance.RewriteTerrainMask.Point;
using Segment = ForestPerformance.BridgeIntersectionPreparation.Segment;

internal static class RewriteTerrainTests
{
    internal static void Run()
    {
        CheckBridgeWorker();
        var game = Environment.GetEnvironmentVariable("ATT_GAME_PATH");
        var dll = Environment.GetEnvironmentVariable("ATT_FOREST_REWRITE_DLL")
            ?? Path.Combine(game ?? "", "Mods", "ForestRewrite.Server.dll");
        if (!File.Exists(dll))
        {
            Console.WriteLine("SKIP ForestRewrite terrain binding and oracle (ForestRewrite.Server.dll unavailable).");
            return;
        }

        var rewrite = Assembly.LoadFrom(dll);
        if (!CheckHookAttachment(rewrite)) return;

        var boundary = rewrite.GetType("ForestRewrite.Boundary", true)!;
        var point = rewrite.GetType("ForestRewrite.Point", true)!;
        var outline = new[] { new Point(-80, -70), new Point(73, -70), new Point(111, -10), new Point(36, 20),
            new Point(88, 71), new Point(-80, 71), new Point(-115, -1) };
        var originalPoints = Array.CreateInstance(point, outline.Length);
        for (int i = 0; i < outline.Length; i++)
            originalPoints.SetValue(Activator.CreateInstance(point, outline[i].X, 0.0, outline[i].Z), i);
        var polygon = Activator.CreateInstance(boundary, new object[] { originalPoints })!;
        var contains = (Func<double, double, bool>)boundary.GetMethod("Contains")!
            .CreateDelegate(typeof(Func<double, double, bool>), polygon);
        var distance = (Func<double, double, double>)boundary.GetMethod("BorderDistance")!
            .CreateDelegate(typeof(Func<double, double, double>), polygon);
        var allows = (Func<double, double, double, bool>)boundary.GetMethod("Allows")!
            .CreateDelegate(typeof(Func<double, double, double, bool>), polygon);

        Check(new Grid(-3, 5, 137, 181, 1, -121.25f, -110.125f, 2.5f, 1.75f),
            new Grid(-3, 5, 68, 90, 2, -121.25f, -110.125f, 2.5f, 1.75f));
        Check(new Grid(3, -7, 221, 158, 1, -121f, -63f, 1f, 1f),
            new Grid(3, -7, 110, 78, 2, -121f, -63f, 1f, 1f));

        Console.WriteLine("PASS ForestRewrite terrain geometry, bitwise height blending, paint mask, and installed assembly hook attachment.");

        void Check(Grid heights, Grid splat)
        {
            var mask = RewriteTerrainMask.Build(outline, heights, splat);
            var fresh = new float[heights.Height, heights.Width];
            var generated = new float[heights.Height, heights.Width];
            var expected = new float[heights.Height, heights.Width];
            var random = new System.Random(115);
            for (int row = 0; row < heights.Height; row++)
            for (int column = 0; column < heights.Width; column++)
            {
                double x = heights.WorldX(column);
                double z = heights.WorldZ(row);
                float weight = contains(x, z) ? Mathf.Clamp01((float)((distance(x, z) - Math.Max(heights.UnitX, heights.UnitZ) * 2f) / 20.0)) : 0f;
                Equal(mask.Heights[row * heights.Width + column], weight);
                fresh[row, column] = (float)random.NextDouble();
                generated[row, column] = (float)random.NextDouble();
                expected[row, column] = Mathf.Lerp(fresh[row, column], generated[row, column], weight);
            }
            RewriteTerrain.BlendHeights(generated, fresh, mask.Heights);
            for (int row = 0; row < heights.Height; row++)
            for (int column = 0; column < heights.Width; column++)
                Equal(generated[row, column], expected[row, column]);
            for (int row = 0; row < splat.Height; row++)
            for (int column = 0; column < splat.Width; column++)
                if (mask.KeepSplat[row * splat.Width + column] != allows(splat.WorldX(column), splat.WorldZ(row), Math.Max(splat.UnitX, splat.UnitZ) * 4f))
                    throw new Exception("ForestRewrite paint mask changed");
        }
    }

    private static bool CheckHookAttachment(Assembly rewrite)
    {
        var harmony = new HarmonyLib.Harmony("ATT.ForestPerformance.RewriteTerrain.Tests");
        var timing = new HarmonyLib.Harmony("ATT.ForestPerformance.GenerationTiming.Tests");
        try
        {
            Guid build = rewrite.ManifestModule.ModuleVersionId;
            bool expectedQueue = build == RewriteTerrain.Rewrite053 || build == RewriteTerrain.Rewrite054;
            if (ForestGenerationTiming.Attach(timing) != expectedQueue || !timing.GetPatchedMethods().GetEnumerator().MoveNext())
                throw new Exception("Forest generation timing hooks are unavailable");
            if (build == RewriteTerrain.Rewrite054 && (!BridgeIntersectionPreparation.Supported || !ForestGenerationTiming.BridgePreparationAttached))
                throw new Exception("Forest bridge preparation cannot bind the installed game grid");

            try
            {
                if (!RewriteTerrain.Attach(harmony) || !RewriteTerrain.Installed || !harmony.GetPatchedMethods().GetEnumerator().MoveNext())
                    throw new Exception("ForestRewrite terrain hooks are unavailable");
            }
            catch (NotSupportedException error) when (!expectedQueue &&
                error.Message == "Unrecognized ForestRewrite build for terrain blend preparation")
            {
                if (RewriteTerrain.Installed || harmony.GetPatchedMethods().GetEnumerator().MoveNext())
                    throw new Exception("Unrecognized ForestRewrite build left terrain hooks installed");
                Console.WriteLine("PASS unrecognized ForestRewrite build leaves optional terrain and bridge preparation disabled.");
                return false;
            }
            return true;
        }
        finally
        {
            RewriteTerrain.Shutdown();
            timing.UnpatchSelf();
            ForestGenerationTiming.Reset();
            harmony.UnpatchSelf();
        }
    }

    private static void CheckBridgeWorker()
    {
        var edges = new List<Segment> { new Segment(new Vector2(-8, 0), new Vector2(8, 0)),
            new Segment(new Vector2(0, -8), new Vector2(0, 8)), new Segment(Vector2.zero, Vector2.one) };
        var rivers = new List<Segment> { new Segment(new Vector2(8, -8), new Vector2(8, 8)),
            new Segment(new Vector2(-2, 8), new Vector2(-2, -8)), new Segment(new Vector2(3, 0), new Vector2(3, 8)),
            new Segment(new Vector2(-8, 0), new Vector2(8, 0)), new Segment(new Vector2(20, 20), new Vector2(21, 21)) };
        var actual = BridgeIntersectionPreparation.FindCrossings(edges, rivers, CancellationToken.None)!;
        int hitIndex = 0;
        for (int edge = 0; edge < edges.Count; edge++)
        for (int river = 0; river < rivers.Count; river++)
        {
            if (!Math2d.LineSegmentsIntersection(edges[edge].From, edges[edge].To, rivers[river].From, rivers[river].To,
                out float onEdge, out float onRiver)) continue;
            var hit = actual[hitIndex++];
            if (hit.Edge != edge || hit.River != river)
                throw new Exception("Forest bridge lookup changed native intersection order");
            Equal(hit.EdgeProgress, onEdge);
            Equal(hit.RiverProgress, onRiver);
        }
        if (hitIndex != actual.Count || hitIndex != 5)
            throw new Exception("Forest bridge lookup changed native intersection count");

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                BridgeIntersectionPreparation.FindCrossings(edges, rivers, cancellation.Token);
                throw new Exception("Forest bridge work ignored generation cancellation");
            }
            catch (OperationCanceledException) { }
        }

        var duplicateEdges = new List<Segment>();
        var duplicateRivers = new List<Segment>();
        for (int i = 0; i < 257; i++)
        {
            duplicateEdges.Add(edges[0]);
            duplicateRivers.Add(rivers[0]);
        }
        if (BridgeIntersectionPreparation.FindCrossings(duplicateEdges, duplicateRivers, CancellationToken.None) != null)
            throw new Exception("Forest bridge worker exceeded its result limit");
        Console.WriteLine("PASS ordered bridge intersections, exact progress values, cancellation and result limit.");
    }

    private static void Equal(float result, float expected)
    {
        if (BitConverter.ToInt32(BitConverter.GetBytes(result), 0) != BitConverter.ToInt32(BitConverter.GetBytes(expected), 0))
            throw new Exception("ForestRewrite floating-point result changed");
    }
}
