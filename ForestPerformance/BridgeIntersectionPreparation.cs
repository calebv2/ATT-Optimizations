using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alta;
using Alta.Chunks;
using Alta.Forests;
using Alta.Static;
using UnityEngine;

namespace ForestPerformance
{
    internal static class BridgeIntersectionPreparation
    {
        private const int MaxSegments = 65536;
        private const int MaxHits = 65536;
        private const long MinimumWorkerPairs = 8192;
        private static readonly SemaphoreSlim Worker = new SemaphoreSlim(1, 1);
        private static readonly FieldInfo? RiverIndex = typeof(RiverGenerator).GetField("intersections", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly FieldInfo? ChunkIndex = typeof(RiverIntersections).GetField("intersections", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly Type? UnityContext = typeof(Application).Assembly.GetType("UnityEngine.UnitySynchronizationContext");

        internal static bool Supported => UnityContext != null && RiverIndex?.FieldType == typeof(RiverIntersections) &&
            ChunkIndex?.FieldType == typeof(Dictionary<Chunk, List<RiverIntersections.Intersection>>);

        internal readonly struct Segment
        {
            internal readonly Vector2 From, To;
            internal Segment(Vector2 from, Vector2 to) { From = from; To = to; }
        }

        internal readonly struct Hit
        {
            internal readonly int Edge, River;
            internal readonly float EdgeProgress, RiverProgress;

            internal Hit(int edge, int river, float edgeProgress, float riverProgress)
            {
                Edge = edge;
                River = river;
                EdgeProgress = edgeProgress;
                RiverProgress = riverProgress;
            }
        }

        internal static Task Generate(IForestGeneration generator, ForestGeneration forest, LocationChunk chunk,
            InstructionSet instructions, CancellationToken cancellation)
        {
            if (generator.GetType() != typeof(ForestBridges) || !Core.Ready || !Core.Main)
                return generator.GenerateStatic(forest, chunk, instructions);

            if (!Supported || SynchronizationContext.Current?.GetType() != UnityContext)
                return GenerateBridge(generator, forest, chunk, instructions);

            RiverGenerator river = forest.Generator.RiverGenerator;
            if (river == null || RiverIndex!.GetValue(river) != null)
                return GenerateBridge(generator, forest, chunk, instructions);

            return PrepareAndGenerate(generator, forest, chunk, instructions, river, cancellation);
        }

        private static async Task PrepareAndGenerate(IForestGeneration generator, ForestGeneration forest, LocationChunk chunk,
            InstructionSet instructions, RiverGenerator river, CancellationToken cancellation)
        {
            await Worker.WaitAsync(cancellation);
            try
            {
                await Prepare(river, forest, chunk.ChunkIdentifier, cancellation);
            }
            finally { Worker.Release(); }

            cancellation.ThrowIfCancellationRequested();
            RequireMain();
            await GenerateBridge(generator, forest, chunk, instructions);
        }

        private static async Task Prepare(RiverGenerator river, ForestGeneration forest, string chunk, CancellationToken cancellation)
        {
            RequireMain();
            long snapshotStarted = Stopwatch.GetTimestamp();
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if (RiverIndex!.GetValue(river) != null) return;
                RiverGeneration generation = river.TryGetGenerated(false);
                if (generation == null || !ReferenceEquals(generation.Forest, forest)) return;

                var edges = new List<Edge>();
                var edgeSegments = new List<Segment>();
                foreach (Edge edge in generation.Forest.Edges)
                {
                    if (!edge.Is(EdgeType.Normal | EdgeType.WaterBridge)) continue;
                    if (edges.Count >= MaxSegments) return;
                    edges.Add(edge);
                    edgeSegments.Add(new Segment(edge.A.Position, edge.B.Position));
                }

                var paths = new List<RiverPath>();
                var riverSegments = new List<Segment>();
                foreach (RiverNode node in generation.Nodes)
                {
                    RiverPath path = node.Outgoing;
                    if (path == null || path.IsUnderground) continue;
                    if (paths.Count >= MaxSegments) return;
                    paths.Add(path);
                    riverSegments.Add(new Segment(path.From.Position, path.To.Position));
                }

                long nativePairs = (long)edges.Count * generation.Nodes.Count;
                if (nativePairs < MinimumWorkerPairs) return;
                long pairs = (long)edges.Count * paths.Count;
                Core.Row("forest_bridge_index_snapshot", Elapsed(snapshotStarted), "chunk=" + chunk + ";pairs=" + pairs +
                    ";native_node_visits=" + nativePairs);
                long workerStarted = Stopwatch.GetTimestamp();
                List<Hit>? hits = await FindOnWorker(edgeSegments, riverSegments, cancellation);
                RequireMain();
                cancellation.ThrowIfCancellationRequested();
                Core.Row("forest_bridge_index_worker", Elapsed(workerStarted), "chunk=" + chunk + ";pairs=" + pairs +
                    ";hits=" + (hits?.Count.ToString() ?? "limit") + ";unit=elapsed_with_resume");
                if (!Core.Ready || hits == null || RiverIndex.GetValue(river) != null ||
                    !ReferenceEquals(river.TryGetGenerated(false), generation)) return;

                long applyStarted = Stopwatch.GetTimestamp();
                var index = BuildChunkIndex(edges, paths, hits);
                if (index == null) return;
                cancellation.ThrowIfCancellationRequested();
                foreach (Hit hit in hits) edges[hit.Edge].Type = EdgeType.WaterBridge;
                RiverIndex.SetValue(river, index);
                Core.Row("forest_bridge_index_apply", Elapsed(applyStarted), "chunk=" + chunk + ";hits=" + hits.Count);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                Core.Row("forest_bridge_index_fallback", Elapsed(snapshotStarted), "chunk=" + chunk, error);
            }
        }

        private static Task<List<Hit>?> FindOnWorker(List<Segment> edges, List<Segment> rivers, CancellationToken cancellation)
            => Task.Run(() => FindCrossings(edges, rivers, cancellation), cancellation);

        internal static List<Hit>? FindCrossings(List<Segment> edges, List<Segment> rivers, CancellationToken cancellation)
        {
            var hits = new List<Hit>();
            for (int edgeIndex = 0; edgeIndex < edges.Count; edgeIndex++)
            {
                cancellation.ThrowIfCancellationRequested();
                Segment edge = edges[edgeIndex];
                for (int riverIndex = 0; riverIndex < rivers.Count; riverIndex++)
                {
                    Segment river = rivers[riverIndex];
                    if (!Math2d.LineSegmentsIntersection(edge.From, edge.To, river.From, river.To,
                        out float edgeProgress, out float riverProgress)) continue;

                    if (hits.Count == MaxHits) return null;
                    hits.Add(new Hit(edgeIndex, riverIndex, edgeProgress, riverProgress));
                }
            }
            return hits;
        }

        private static RiverIntersections? BuildChunkIndex(List<Edge> edges, List<RiverPath> rivers, List<Hit> hits)
        {
            var byChunk = new Dictionary<Chunk, List<RiverIntersections.Intersection>>(UnityObjectComparer.Instance);
            foreach (Hit hit in hits)
            {
                Edge edge = edges[hit.Edge];
                LocationChunk chunk = LocationChunkHelper.GetBestContaining(Vector3.Lerp(edge.A.Position3d, edge.B.Position3d, hit.EdgeProgress), null);
                if (chunk == null) return null;
                if (!byChunk.TryGetValue(chunk, out var entries))
                    byChunk.Add(chunk, entries = new List<RiverIntersections.Intersection>());

                entries.Add(new RiverIntersections.Intersection
                {
                    edge = edge,
                    river = rivers[hit.River],
                    pathProgress = hit.EdgeProgress,
                    riverProgress = hit.RiverProgress
                });
            }
            var index = new RiverIntersections();
            ChunkIndex!.SetValue(index, byChunk);
            return index;
        }

        private static Task GenerateBridge(IForestGeneration generator, ForestGeneration forest, LocationChunk chunk, InstructionSet instructions)
        {
            long started = Stopwatch.GetTimestamp();
            try { return generator.GenerateStatic(forest, chunk, instructions); }
            finally
            {
                double ms = Elapsed(started);
                if (ms >= 25 && Core.Trace != null)
                {
                    string? chunkId = null;
                    try { chunkId = chunk?.ChunkIdentifier; }
                    catch { }
                    Core.Row("forest_bridge_stage_call", ms, "chunk=" + chunkId + ";unit=synchronous_wall_time");
                }
            }
        }

        private static void RequireMain()
        {
            if (!Core.Main) throw new InvalidOperationException("Forest bridge preparation lost the Unity thread");
        }

        private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
    }
}
