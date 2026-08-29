using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Export
{
    public readonly struct TerrainTriangle
    {
        public readonly Vector3 A;
        public readonly Vector3 B;
        public readonly Vector3 C;
        public readonly Vector3 Normal;
        public readonly float MinX;
        public readonly float MaxX;
        public readonly float MinZ;
        public readonly float MaxZ;

        public TerrainTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
        {
            A = a;
            B = b;
            C = c;
            Normal = normal;
            MinX = Math.Min(a.X, Math.Min(b.X, c.X));
            MaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            MinZ = Math.Min(a.Z, Math.Min(b.Z, c.Z));
            MaxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z));
        }
    }

    /// <summary>
    /// Fast spatial acceleration grid for raycasting against level terrain meshes.
    /// Used by SceneReconstruction to procedurally align MovableProps to track surfaces.
    /// </summary>
    public sealed class TerrainRaycaster
    {
        private const float CellSize = 15.0f;
        private readonly Dictionary<long, List<TerrainTriangle>> _grid = new();
        private readonly Dictionary<string, MSHSContainer> _meshCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TDRHierarchy> _hieCache = new(StringComparer.OrdinalIgnoreCase);
        public int TriangleCount { get; private set; }

        private static long GetCellKey(int gx, int gz)
        {
            return ((long)gx << 32) ^ (uint)gz;
        }

        public static bool IsTerrainHie(string hieName)
        {
            if (string.IsNullOrWhiteSpace(hieName)) return false;
            string lower = Path.GetFileName(hieName).ToLowerInvariant();
            return !lower.Contains("sky") &&
                   !lower.Contains("water") &&
                   !lower.Contains("ocean") &&
                   !lower.Contains("radar") &&
                   !lower.Contains("shadow") &&
                   !lower.Contains("spline") &&
                   !lower.Contains("drone");
        }

        public void AddHierarchyTriangles(
            PakManager vfs,
            string hieName,
            Matrix4x4 rootTransform,
            string? trackContext)
        {
            if (string.IsNullOrWhiteSpace(hieName)) return;

            if (!_hieCache.TryGetValue(hieName, out var hie))
            {
                byte[]? hieBytes = vfs.LoadFileContext(hieName, trackContext ?? string.Empty) ?? vfs.LoadFile(hieName);
                if (hieBytes != null && hieBytes.Length > 0)
                {
                    hie = TDRHierarchy.Load(hieBytes, hieName);
                    _hieCache[hieName] = hie;
                }
            }

            if (hie?.Root == null) return;

            void Recurse(TDRNode node, Matrix4x4 parentMatrix)
            {
                Matrix4x4 worldMat = node.Transform * parentMatrix;

                if (node.Type == TDRNode.NodeType.Mesh && node.Index >= 0 && node.Index < hie.Meshes.Count)
                {
                    string mshName = hie.Meshes[node.Index];
                    if (!_meshCache.TryGetValue(mshName, out var container))
                    {
                        byte[]? mshBytes = vfs.LoadFileContext(mshName, trackContext ?? string.Empty) ?? vfs.LoadFile(mshName);
                        if (mshBytes != null && mshBytes.Length > 0)
                        {
                            container = MSHSContainer.Load(mshBytes, mshName);
                            _meshCache[mshName] = container;
                        }
                    }

                    if (container != null)
                    {
                        var stream = new TriangleStream();
                        foreach (var meshData in container.Meshes)
                        {
                            MeshGeometryReader.AppendTriangles(meshData, worldMat, stream);
                        }

                        for (int i = 0; i + 2 < stream.Vertices.Count; i += 3)
                        {
                            Vector3 v0Norm = stream.Vertices[i].Normal;
                            Vector3 v1Norm = stream.Vertices[i + 1].Normal;
                            Vector3 v2Norm = stream.Vertices[i + 2].Normal;
                            Vector3 sumNorm = v0Norm + v1Norm + v2Norm;
                            Vector3 triNorm = sumNorm.LengthSquared() > 1e-4f
                                ? Vector3.Normalize(sumNorm)
                                : (v0Norm.LengthSquared() > 1e-4f ? Vector3.Normalize(v0Norm) : Vector3.UnitY);

                            AddTriangle(new TerrainTriangle(
                                stream.Vertices[i].Position,
                                stream.Vertices[i + 1].Position,
                                stream.Vertices[i + 2].Position,
                                triNorm));
                        }
                    }
                }

                foreach (var child in node.Children)
                {
                    Recurse(child, worldMat);
                }
            }

            Recurse(hie.Root, rootTransform);
        }

        public static TerrainRaycaster Build(
            PakManager vfs,
            DescriptorAssets assets,
            string? trackContext,
            Action<string>? log = null)
        {
            var raycaster = new TerrainRaycaster();

            // 1. Process base level HIE files
            foreach (string hieFile in assets.HieFiles)
            {
                if (!IsTerrainHie(hieFile)) continue;
                raycaster.AddHierarchyTriangles(vfs, hieFile, Matrix4x4.Identity, trackContext);
            }

            // 2. Process placed HIE instances
            foreach (var inst in assets.HieInstances)
            {
                if (!IsTerrainHie(inst.HieName)) continue;
                raycaster.AddHierarchyTriangles(vfs, inst.HieName, inst.Transform, trackContext);
            }

            if (raycaster.TriangleCount > 0)
            {
                log?.Invoke($"[TerrainRaycaster] Indexed {raycaster.TriangleCount} terrain triangles for ground snapping.");
            }

            return raycaster;
        }

        public void AddTriangle(TerrainTriangle tri)
        {
            TriangleCount++;
            int minGx = (int)MathF.Floor(tri.MinX / CellSize);
            int maxGx = (int)MathF.Floor(tri.MaxX / CellSize);
            int minGz = (int)MathF.Floor(tri.MinZ / CellSize);
            int maxGz = (int)MathF.Floor(tri.MaxZ / CellSize);

            for (int gx = minGx; gx <= maxGx; gx++)
            {
                for (int gz = minGz; gz <= maxGz; gz++)
                {
                    long key = GetCellKey(gx, gz);
                    if (!_grid.TryGetValue(key, out var list))
                    {
                        list = new List<TerrainTriangle>();
                        _grid[key] = list;
                    }
                    list.Add(tri);
                }
            }
        }

        public bool RaycastGround(float x, float z, float startY, float maxDrop, out float hitY)
        {
            hitY = float.MinValue;
            if (TriangleCount == 0) return false;

            int gx = (int)MathF.Floor(x / CellSize);
            int gz = (int)MathF.Floor(z / CellSize);
            long key = GetCellKey(gx, gz);

            if (!_grid.TryGetValue(key, out var list))
            {
                return false;
            }

            bool found = false;
            foreach (var tri in list)
            {
                if (x < tri.MinX - 0.01f || x > tri.MaxX + 0.01f || z < tri.MinZ - 0.01f || z > tri.MaxZ + 0.01f)
                {
                    continue;
                }

                // Filter surfaces by explicit author normal: only hit upward-facing walkable ground / floor surfaces.
                // This reliably discards downward-facing ceilings/roof undersides (Normal.Y < 0) and vertical walls (Normal.Y ≈ 0)
                // while preserving even steep mountain road ramps (e.g. up to ~84° slope).
                if (tri.Normal.Y < 0.1f) continue;

                Vector3 e1 = tri.B - tri.A;
                Vector3 e2 = tri.C - tri.A;
                Vector3 pvec = new Vector3(-e2.Z, 0.0f, e2.X);
                float det = e1.X * pvec.X + e1.Y * pvec.Y + e1.Z * pvec.Z;

                // Standard Möller-Trumbore non-degenerate triangle check
                if (MathF.Abs(det) < 1e-7f) continue;
                float invDet = 1.0f / det;

                Vector3 tvec = new Vector3(x - tri.A.X, startY - tri.A.Y, z - tri.A.Z);
                float u = (tvec.X * pvec.X + tvec.Y * pvec.Y + tvec.Z * pvec.Z) * invDet;
                if (u < -0.001f || u > 1.001f) continue;

                Vector3 qvec = new Vector3(
                    tvec.Y * e1.Z - tvec.Z * e1.Y,
                    tvec.Z * e1.X - tvec.X * e1.Z,
                    tvec.X * e1.Y - tvec.Y * e1.X);
                float v = -qvec.Y * invDet;
                if (v < -0.001f || u + v > 1.001f) continue;

                float t = (e2.X * qvec.X + e2.Y * qvec.Y + e2.Z * qvec.Z) * invDet;
                if (t >= 0.0f && t <= maxDrop)
                {
                    float currentHitY = startY - t;
                    if (!found || currentHitY > hitY)
                    {
                        hitY = currentHitY;
                        found = true;
                    }
                }
            }

            return found;
        }
    }
}
