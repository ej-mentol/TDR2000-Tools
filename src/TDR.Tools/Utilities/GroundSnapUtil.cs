using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using TDR.PakLib.Formats;

namespace TDR.Tools.Utilities
{
    /// <summary>
    /// Experimental standalone helper for raycasting movables onto terrain/surface collision geometry.
    /// Located in Utilities namespace for clean architecture.
    /// </summary>
    public static class GroundSnapUtil
    {
        public sealed class Triangle
        {
            public Vector3 A;
            public Vector3 B;
            public Vector3 C;
        }

        public static List<Triangle> ExtractBaseTriangles(TDRHierarchy hie, Func<string, byte[]?> loader)
        {
            var tris = new List<Triangle>();
            if (hie?.Root == null) return tris;

            var meshCache = new Dictionary<string, MSHSContainer>(StringComparer.OrdinalIgnoreCase);

            void RecurseNode(TDRNode node, Matrix4x4 parentMatrix)
            {
                Matrix4x4 localMat = Matrix4x4.Identity;
                if (node.Type == TDRNode.NodeType.Matrix && node.Index >= 0 && node.Index < hie.Matrices.Count)
                {
                    localMat = hie.Matrices[node.Index];
                }
                Matrix4x4 worldMat = localMat * parentMatrix;

                if (node.Type == TDRNode.NodeType.Mesh && node.Index >= 0 && node.Index < hie.Meshes.Count)
                {
                    string mshName = hie.Meshes[node.Index];
                    if (!meshCache.TryGetValue(mshName, out var container))
                    {
                        byte[]? mshBytes = loader(mshName);
                        if (mshBytes != null && mshBytes.Length > 0)
                        {
                            container = MSHSContainer.Load(mshBytes, mshName);
                            meshCache[mshName] = container;
                        }
                    }

                    if (container != null)
                    {
                        foreach (var meshData in container.Meshes)
                        {
                            foreach (var face in meshData.Faces)
                            {
                                if (face.Vertices.Count >= 3)
                                {
                                    Vector3 v0 = Vector3.Transform(face.Vertices[0].Position, worldMat);
                                    for (int i = 1; i < face.Vertices.Count - 1; i++)
                                    {
                                        Vector3 v1 = Vector3.Transform(face.Vertices[i].Position, worldMat);
                                        Vector3 v2 = Vector3.Transform(face.Vertices[i + 1].Position, worldMat);
                                        tris.Add(new Triangle { A = v0, B = v1, C = v2 });
                                    }
                                }
                            }
                        }
                    }
                }

                foreach (var child in node.Children)
                {
                    RecurseNode(child, worldMat);
                }
            }

            RecurseNode(hie.Root, Matrix4x4.Identity);
            return tris;
        }

        public static Vector3 SnapPointToSurface(Vector3 origPos, List<Triangle> triangles, float maxDropDistance = 15.0f, float rayStartHeight = 5.0f)
        {
            if (triangles == null || triangles.Count == 0) return origPos;

            Vector3 rayStart = new Vector3(origPos.X, origPos.Y + rayStartHeight, origPos.Z);
            Vector3 rayDir = new Vector3(0, -1.0f, 0);

            float highestHitY = float.MinValue;
            bool hitFound = false;

            foreach (var tri in triangles)
            {
                if (RayTriangleIntersect(rayStart, rayDir, tri.A, tri.B, tri.C, out float t))
                {
                    if (t >= 0.0f && t <= (maxDropDistance + rayStartHeight))
                    {
                        float hitY = rayStart.Y - t;
                        if (hitY > highestHitY)
                        {
                            highestHitY = hitY;
                            hitFound = true;
                        }
                    }
                }
            }

            if (hitFound && highestHitY > (origPos.Y - maxDropDistance))
            {
                return new Vector3(origPos.X, highestHitY, origPos.Z);
            }

            return origPos;
        }

        private static bool RayTriangleIntersect(Vector3 orig, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
        {
            t = 0.0f;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 pvec = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, pvec);

            if (MathF.Abs(det) < 0.000001f) return false;
            float invDet = 1.0f / det;

            Vector3 tvec = orig - v0;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < 0.0f || u > 1.0f) return false;

            Vector3 qvec = Vector3.Cross(tvec, e1);
            float v = Vector3.Dot(dir, qvec) * invDet;
            if (v < 0.0f || u + v > 1.0f) return false;

            t = Vector3.Dot(e2, qvec) * invDet;
            return t >= 0.0f;
        }
    }
}
