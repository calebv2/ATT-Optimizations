using System;
using System.Threading.Tasks;

namespace ForestPerformance
{
    internal sealed class RewriteTerrainMask
    {
        internal readonly struct Point
        {
            internal readonly double X, Z;
            internal Point(double x, double z) { X = x; Z = z; }
        }

        internal readonly struct Grid
        {
            internal readonly int X, Y, Width, Height, Stride;
            internal readonly float OffsetX, OffsetZ, UnitX, UnitZ;
            internal Grid(int x, int y, int width, int height, int stride, float offsetX, float offsetZ, float unitX, float unitZ)
            {
                X = x; Y = y; Width = width; Height = height; Stride = stride;
                OffsetX = offsetX; OffsetZ = offsetZ; UnitX = unitX; UnitZ = unitZ;
            }

            // ForestRewrite does this arithmetic in single precision before passing doubles to Boundary.
            internal double WorldX(int column) => OffsetX + (float)(X + column * Stride) * UnitX;
            internal double WorldZ(int row) => OffsetZ + (float)(Y + row * Stride) * UnitZ;
        }

        private readonly struct Edge
        {
            internal readonly double X, Z, Dx, Dz, EndZ, LengthSquared, MinX, MaxX, MinZ, MaxZ;
            internal Edge(Point a, Point b)
            {
                X = a.X; Z = a.Z; Dx = b.X - a.X; Dz = b.Z - a.Z; EndZ = b.Z;
                LengthSquared = Dx * Dx + Dz * Dz;
                MinX = Math.Min(a.X, b.X) - 1e-6; MaxX = Math.Max(a.X, b.X) + 1e-6;
                MinZ = Math.Min(a.Z, b.Z) - 1e-6; MaxZ = Math.Max(a.Z, b.Z) + 1e-6;
            }
        }

        internal readonly float[] Heights;
        internal readonly bool[] KeepSplat;

        private RewriteTerrainMask(Grid heights, Grid splat)
        {
            Heights = new float[heights.Width * heights.Height];
            KeepSplat = new bool[splat.Width * splat.Height];
        }

        internal static RewriteTerrainMask Build(Point[] outline, Grid heights, Grid splat)
        {
            var edges = new Edge[outline.Length];
            for (int i = 0; i < edges.Length; i++)
                edges[i] = new Edge(outline[i], outline[(i + 1) % edges.Length]);
            var result = new RewriteTerrainMask(heights, splat);
            var parallel = new ParallelOptions { MaxDegreeOfParallelism = 2 };
            var heightInset = Math.Max(heights.UnitX, heights.UnitZ) * 2f;
            var splatInset = Math.Max(splat.UnitX, splat.UnitZ) * 4f;
            Parallel.For(0, heights.Height, parallel, row =>
            {
                double z = heights.WorldZ(row);
                int start = row * heights.Width;
                for (int column = 0; column < heights.Width; column++)
                {
                    double x = heights.WorldX(column);
                    if (Contains(edges, x, z))
                    {
                        float weight = (float)((Distance(edges, x, z) - heightInset) / 20.0);
                        result.Heights[start + column] = weight < 0f ? 0f : weight > 1f ? 1f : weight;
                    }
                }
            });
            Parallel.For(0, splat.Height, parallel, row =>
            {
                double z = splat.WorldZ(row);
                int start = row * splat.Width;
                for (int column = 0; column < splat.Width; column++)
                {
                    double x = splat.WorldX(column);
                    result.KeepSplat[start + column] = Contains(edges, x, z) && Distance(edges, x, z) >= splatInset;
                }
            });
            return result;
        }

        private static bool Contains(Edge[] edges, double x, double z)
        {
            bool inside = false;
            foreach (var edge in edges)
            {
                double relativeZ = z - edge.Z;
                if (x >= edge.MinX && x <= edge.MaxX && z >= edge.MinZ && z <= edge.MaxZ &&
                    Math.Abs(edge.Dx * relativeZ - edge.Dz * (x - edge.X)) < 1e-6)
                    return true;
                if ((edge.Z > z) != (edge.EndZ > z) && x < edge.Dx * relativeZ / edge.Dz + edge.X)
                    inside = !inside;
            }
            return inside;
        }

        private static double Distance(Edge[] edges, double x, double z)
        {
            double nearest = double.MaxValue;
            foreach (var edge in edges)
            {
                double dx = x - edge.X;
                double dz = z - edge.Z;
                double t = Math.Max(0.0, Math.Min(1.0, (dx * edge.Dx + dz * edge.Dz) / edge.LengthSquared));
                dx -= t * edge.Dx;
                dz -= t * edge.Dz;
                nearest = Math.Min(nearest, dx * dx + dz * dz);
            }
            return Math.Sqrt(nearest);
        }
    }
}
