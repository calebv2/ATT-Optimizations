using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using Pathfinding;

namespace ForestNavigationGuard
{
    // RecastGraph.UpdateAreaPost passes the full tiles array to cutting even for
    // a local rebuild. Select its completed staging tiles at that single call.
    // No shared/nested callback context or coroutine wrapper is needed.
    internal static class RebuiltTiles
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static FieldInfo Staging = null!, AllTiles = null!;
        internal static MethodInfo UpdatePost = null!, Notify = null!;
        internal static long Narrowed, TilesSkipped, ValidationFallbacks;

        internal static MethodInfo Resolve(Assembly root)
        {
            if (root.ManifestModule.ModuleVersionId != Patch.SupportedMvid || root != typeof(RecastGraph).Assembly)
                throw new NotSupportedException("Unsupported tile-rebuild game assembly");
            Staging = typeof(RecastGraph).GetField("stagingTiles", Flags)
                ?? throw new MissingFieldException(typeof(RecastGraph).FullName, "stagingTiles");
            AllTiles = typeof(NavmeshBase).GetField("tiles", Flags)
                ?? throw new MissingFieldException(typeof(NavmeshBase).FullName, "tiles");
            if (Staging.IsStatic || Staging.FieldType != typeof(List<NavmeshTile>) ||
                AllTiles.IsStatic || AllTiles.FieldType != typeof(NavmeshTile[]))
                throw new NotSupportedException("Unexpected tile storage metadata");
            UpdatePost = typeof(RecastGraph).GetMethod("Pathfinding.IUpdatableGraph.UpdateAreaPost", Flags,
                null, new[] { typeof(GraphUpdateObject) }, null)
                ?? throw new MissingMethodException(typeof(RecastGraph).FullName, "UpdateAreaPost");
            Type settings = typeof(NavmeshUpdates).GetNestedType("NavmeshUpdateSettings", BindingFlags.NonPublic)
                ?? throw new MissingMemberException("NavmeshUpdateSettings");
            Notify = settings.GetMethod("OnRecalculatedTiles", Flags, null, new[] { typeof(NavmeshTile[]) }, null)
                ?? throw new MissingMethodException(settings.FullName, "OnRecalculatedTiles");
            if (UpdatePost.IsStatic || UpdatePost.ReturnType != typeof(void) || Notify.IsStatic || Notify.ReturnType != typeof(void))
                throw new NotSupportedException("Unexpected rebuilt-tile method signature");
            return UpdatePost;
        }

        internal static IEnumerable<CodeInstruction> ReplaceNotificationArgument(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var matches = list.Select((instruction, index) => new { instruction, index })
                .Where(x => (x.instruction.opcode == OpCodes.Call || x.instruction.opcode == OpCodes.Callvirt)
                    && x.instruction.operand is MethodInfo method && method.Equals(Notify)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Expected exactly one native rebuilt-tile notification; found " + matches.Length);
            int index = matches[0].index;
            // Validate the inspected argument load rather than rewriting a
            // different notification if another transpiler changed this site.
            if (index < 2 || list[index - 2].opcode != OpCodes.Ldarg_0 ||
                list[index - 1].opcode != OpCodes.Ldfld || !(list[index - 1].operand is FieldInfo field) || !field.Equals(AllTiles) ||
                list[index].blocks.Count != 0)
                throw new InvalidOperationException("Unexpected rebuilt-tile notification IL; leaving native code intact");
            var loadGraph = new CodeInstruction(OpCodes.Ldarg_0);
            loadGraph.labels.AddRange(list[index].labels);
            list[index].labels.Clear();
            list.Insert(index, loadGraph);
            list.Insert(index + 1, new CodeInstruction(OpCodes.Call,
                typeof(RebuiltTiles).GetMethod(nameof(Select), BindingFlags.Static | BindingFlags.NonPublic)));
            return list;
        }

        internal static NavmeshTile[] Select(NavmeshTile[] tiles, RecastGraph graph)
        {
            // The inspected forest constraint targets graph zero. Other graph
            // indices, including all cave graphs, retain the original argument.
            if (graph == null || graph.graphIndex != 0 || tiles == null || tiles.Length == 0) return tiles!;
            try
            {
                if (!ReferenceEquals(AllTiles.GetValue(graph), tiles) ||
                    !(Staging.GetValue(graph) is List<NavmeshTile> staging)) return Fallback(tiles);
                if (staging.Count >= tiles.Length) return tiles;
                var rebuilt = staging.ToArray();
                // At this call the game must already have installed these exact
                // tiles. On unexpected state, pass the original full array.
                foreach (var tile in rebuilt)
                {
                    if (tile == null || tile.x < 0 || tile.z < 0 || tile.x >= graph.tileXCount || tile.z >= graph.tileZCount)
                        return Fallback(tiles);
                    long slot = (long)tile.z * graph.tileXCount + tile.x;
                    if (slot >= tiles.Length || !ReferenceEquals(tiles[(int)slot], tile)) return Fallback(tiles);
                }
                Interlocked.Increment(ref Narrowed);
                Interlocked.Add(ref TilesSkipped, tiles.Length - rebuilt.Length);
                return rebuilt;
            }
            catch { return Fallback(tiles); }
        }

        private static NavmeshTile[] Fallback(NavmeshTile[] tiles)
        {
            Interlocked.Increment(ref ValidationFallbacks);
            return tiles;
        }
    }
}
