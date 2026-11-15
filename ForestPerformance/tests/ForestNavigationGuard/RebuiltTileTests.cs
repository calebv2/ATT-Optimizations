using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using System.IO;
using Path = System.IO.Path;
using HarmonyLib;
using Pathfinding;
using ForestNavigationGuard;

internal static class RebuiltTileTests
{
    private static readonly List<NavmeshTile[]> Notifications = new List<NavmeshTile[]>();
    private static Exception? notifyError;
    private static int observations;
    private static bool recording, nested;
    private static RecastGraph? nestedGraph;

    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static NavmeshTile Tile(int x) => new NavmeshTile { x = x, z = 0, w = 1, d = 1, nodes = new TriangleMeshNode[0] };
    private static RecastGraph Graph(uint index = 0)
    {
        // Native constructors create GraphTransform and call engine matrix
        // internals unavailable in host Mono. Only the post-update fields used
        // by this empty-node fixture are initialized here.
        var graph = (RecastGraph)FormatterServices.GetUninitializedObject(typeof(RecastGraph));
        graph.graphIndex = index; graph.tileXCount = 4; graph.tileZCount = 1;
        graph.tileSizeX = graph.tileSizeZ = 128; graph.cellSize = 0.5f;
        RebuiltTiles.AllTiles.SetValue(graph, Enumerable.Range(0, 4).Select(Tile).ToArray());
        RebuiltTiles.Staging.SetValue(graph, new List<NavmeshTile>());
        Type settings = RebuiltTiles.Notify.DeclaringType!;
        object data = Activator.CreateInstance(settings, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            null, new object[] { graph }, null)!;
        typeof(NavmeshBase).GetField("navmeshUpdateData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(graph, data);
        return graph;
    }
    private static List<NavmeshTile> Staging(RecastGraph graph) => (List<NavmeshTile>)RebuiltTiles.Staging.GetValue(graph);
    private static void Stage(RecastGraph graph, params int[] indices)
    {
        Staging(graph).Clear();
        foreach (int i in indices) Staging(graph).Add(Tile(i));
    }
    private static void RunNative(RecastGraph graph)
    {
        bool completed = false;
        NavmeshTile[]? external = null;
        var expected = Staging(graph).ToArray();
        var update = new GraphUpdateObject();
        update.Completed += () => completed = true;
        graph.OnRecalculatedTiles = tiles => external = tiles;
        RebuiltTiles.UpdatePost.Invoke(graph, new object[] { update });
        Check(completed && Staging(graph).Count == 0, "Native completion or staging cleanup changed");
        Check(external != null && external.SequenceEqual(expected), "Native external rebuilt-tile event changed");
        foreach (var tile in expected) Check(ReferenceEquals(graph.GetTiles()[tile.x], tile), "Native tile replacement changed");
    }
    // Avoid native Unity cutting in this offline process, but execute the real
    // RecastGraph.UpdateAreaPost up to and after its actual notification call.
    private static bool Capture(NavmeshTile[] tiles)
    {
        if (!recording) return false;
        Notifications.Add(tiles);
        if (notifyError != null) throw notifyError;
        if (nested && nestedGraph != null)
        {
            nested = false;
            RunNative(nestedGraph);
        }
        return false;
    }
    private static void Observe() { observations++; }
    private static void MustReject(IEnumerable<CodeInstruction> input, string reason)
    {
        bool rejected = false;
        try { RebuiltTiles.ReplaceNotificationArgument(input).ToArray(); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, reason);
    }
    private static void CheckQueueDiagnostics(HarmonyLib.Harmony patcher)
    {
        var diagnostics = new HarmonyLib.Harmony("ATT.PathfindingQueueDiagnostics.ForestGuardOffline");
        string hooks = Path.GetTempFileName();
        try
        {
            // Use the installed recorder's real binding/attachment implementation
            // in this offline process. Its live-session Ready flag stays false;
            // this verifies patch composition, not recorder performance.
            var assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("ATT_CAVE_PERFORMANCE_DLL"));
            var flush = assembly.GetType("PathfindingQueueDiagnostics.Flush", true)!;
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            flush.GetMethod("Bind", flags)!.Invoke(null, null);
            void Attach() => flush.GetMethod("Attach", flags)!.Invoke(null, new object[] { diagnostics, hooks });
            void Test() {
                Notifications.Clear(); var graph = Graph(); Stage(graph, 1); var tile = Staging(graph).Single();
                RunNative(graph);
                Check(Notifications.Single().Single() == tile, "Queue diagnostics changed the rebuilt-tile notification");
            }
            Attach();
            patcher.Patch(RebuiltTiles.UpdatePost, transpiler: new HarmonyMethod(typeof(RebuiltTiles), nameof(RebuiltTiles.ReplaceNotificationArgument)));
            Test(); patcher.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(RebuiltTiles.UpdatePost).Owners.Contains(diagnostics.Id), "Guard unpatch removed queue diagnostics");
            patcher.Patch(RebuiltTiles.UpdatePost, transpiler: new HarmonyMethod(typeof(RebuiltTiles), nameof(RebuiltTiles.ReplaceNotificationArgument)));
            diagnostics.UnpatchSelf(); Attach(); Test(); diagnostics.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(RebuiltTiles.UpdatePost).Owners.Contains(patcher.Id), "Recorder unpatch removed the guard");
            Console.WriteLine("PASS: real installed PathfindingQueueDiagnostics Flush hooks compose in both attachment orders; native completion and narrowed notification survive; each owner's rollback preserves the other.");
        }
        finally { patcher.UnpatchSelf(); diagnostics.UnpatchSelf(); File.Delete(hooks); }
    }
    internal static void Run(Assembly root)
    {
        RebuiltTiles.Resolve(root);
        var patcher = new HarmonyLib.Harmony("ATT.ForestNavigationGuard.RebuiltTiles.Offline");
        var observer = new HarmonyLib.Harmony("ATT.ForestNavigationGuard.RebuiltTiles.Observer");
        var notificationFixture = new HarmonyLib.Harmony("ATT.ForestNavigationGuard.RebuiltTiles.CuttingFixture");
        try
        {
            notificationFixture.Patch(RebuiltTiles.Notify, prefix: new HarmonyMethod(typeof(RebuiltTileTests), nameof(Capture)));
            observer.Patch(RebuiltTiles.UpdatePost, postfix: new HarmonyMethod(typeof(RebuiltTileTests), nameof(Observe)));
            recording = true;
            var originalGraph = Graph();
            Stage(originalGraph, 1);
            RunNative(originalGraph);
            Check(ReferenceEquals(Notifications.Single(), originalGraph.GetTiles()), "Could not reproduce native full-array notification");
            Console.WriteLine("REPRODUCED: installed native RecastGraph.UpdateAreaPost notifies all four tiles after rebuilding one.");

            patcher.Patch(RebuiltTiles.UpdatePost, transpiler: new HarmonyMethod(typeof(RebuiltTiles), nameof(RebuiltTiles.ReplaceNotificationArgument)));
            Notifications.Clear();
            var graph = Graph(); Stage(graph, 1, 2);
            var rebuilt = Staging(graph).ToArray();
            RunNative(graph);
            Check(Notifications.Single().SequenceEqual(rebuilt), "Partial update did not notify exact rebuilt tiles");
            Check(!ReferenceEquals(Notifications.Single(), graph.GetTiles()), "Partial notification still uses full array");

            Notifications.Clear(); Stage(graph); RunNative(graph);
            Check(Notifications.Single().Length == 0, "Empty rebuild notified unrelated tiles");
            Notifications.Clear(); Stage(graph, 0, 1, 2, 3); RunNative(graph);
            Check(ReferenceEquals(Notifications.Single(), graph.GetTiles()), "Full rebuild changed array identity");

            Notifications.Clear(); var cave = Graph(1); Stage(cave, 1); RunNative(cave);
            Check(ReferenceEquals(Notifications.Single(), cave.GetTiles()), "Non-overworld notification changed");
            cave.graphIndex = 64; Stage(cave, 2); Notifications.Clear(); RunNative(cave);
            Check(ReferenceEquals(Notifications.Single(), cave.GetTiles()), "High-index cave notification changed");

            graph = Graph(); var all = graph.GetTiles();
            Staging(graph).Add(all[1]);
            Check(RebuiltTiles.Select(all, graph).Single() == all[1], "Valid selector did not select completed tile");
            var foreign = all.ToArray();
            Check(ReferenceEquals(RebuiltTiles.Select(foreign, graph), foreign), "Foreign full-array identity did not fall back");
            Stage(graph, 1);
            Check(ReferenceEquals(RebuiltTiles.Select(all, graph), all), "Not-installed staging tile did not fall back");
            Staging(graph).Clear(); Staging(graph).Add(null!);
            Check(ReferenceEquals(RebuiltTiles.Select(all, graph), all), "Null tile did not fall back");
            Staging(graph).Clear(); Staging(graph).Add(Tile(-1));
            Check(ReferenceEquals(RebuiltTiles.Select(all, graph), all), "Negative tile coordinates did not fall back");
            Staging(graph).Clear(); Staging(graph).Add(Tile(99));
            Check(ReferenceEquals(RebuiltTiles.Select(all, graph), all), "Out-of-bounds tile did not fall back");
            RebuiltTiles.Staging.SetValue(graph, null);
            Check(ReferenceEquals(RebuiltTiles.Select(all, graph), all), "Missing staging did not fall back");
            Check(RebuiltTiles.ValidationFallbacks == 6, "Fallback counter mismatch");

            Notifications.Clear(); graph = Graph(); Stage(graph, 1);
            var outerTile = Staging(graph).Single();
            nestedGraph = Graph(); Stage(nestedGraph, 2); var innerTile = Staging(nestedGraph).Single(); nested = true;
            RunNative(graph);
            Check(Notifications.Count == 2 && Notifications[0].Single() == outerTile && Notifications[1].Single() == innerTile,
                "Nested graph updates mixed notification arrays");

            var marker = new InvalidOperationException("notification fixture failure");
            notifyError = marker; graph = Graph(); Stage(graph, 1);
            Exception? actual = null;
            try { RunNative(graph); } catch (TargetInvocationException e) { actual = e.InnerException; }
            Check(ReferenceEquals(actual, marker), "Native notification exception was changed or swallowed");
            Check(Staging(graph).Count == 1, "Exception path unexpectedly changed native staging lifetime");
            notifyError = null; Notifications.Clear(); RunNative(graph);
            Check(Notifications.Single().Length == 1, "A failed notification contaminated the next update");

            MustReject(new CodeInstruction[0], "Missing call site was accepted");
            MustReject(new[] { new CodeInstruction(OpCodes.Callvirt, RebuiltTiles.Notify), new CodeInstruction(OpCodes.Callvirt, RebuiltTiles.Notify) }, "Duplicate call sites were accepted");
            MustReject(new[] { new CodeInstruction(OpCodes.Ldarg_0), new CodeInstruction(OpCodes.Ldnull), new CodeInstruction(OpCodes.Callvirt, RebuiltTiles.Notify) }, "Changed argument load was accepted");

            Check(observations > 0, "Other-owner observer did not run");
            patcher.UnpatchSelf();
            Check(HarmonyLib.Harmony.GetPatchInfo(RebuiltTiles.UpdatePost).Owners.Contains(observer.Id), "Rollback removed another patch owner");
            Notifications.Clear(); graph = Graph(); Stage(graph, 1); RunNative(graph);
            Check(ReferenceEquals(Notifications.Single(), graph.GetTiles()), "Unpatch did not restore full-array notification");
            Console.WriteLine("PASS: actual native post-update narrowed partial and empty overworld updates; full and cave arrays preserved; native tile replacement, external event and completion retained; invalid state falls back; nested calls and original exceptions preserved; changed IL rejected; observer coexistence and rollback verified.");
            CheckQueueDiagnostics(patcher);
            Console.WriteLine("LIMIT: empty-node graph fixture stubs the cutting callback; live Unity mesh cutting and forest travel are not exercised.");
        }
        finally
        {
            recording = false; notifyError = null; nested = false; nestedGraph = null;
            patcher.UnpatchSelf(); observer.UnpatchSelf(); notificationFixture.UnpatchSelf();
        }
    }
}
