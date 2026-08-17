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
                Matrix4x4 worldMat = node.Transform * parentMatrix;

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
                            if (meshData.Mode == MeshMode.TriIndexedPosition || (meshData.Positions.Count > 0 && meshData.Faces.Count > 0))
                            {
                                foreach (var face in meshData.Faces)
                                {
                                    if (face.Vertices.Count >= 3)
                                    {
                                        int p0Idx = face.Vertices[0].PositionIndex;
                                        int p1Idx = face.Vertices[1].PositionIndex;
                                        int p2Idx = face.Vertices[2].PositionIndex;

                                        if (p0Idx >= 0 && p0Idx < meshData.Positions.Count &&
                                            p1Idx >= 0 && p1Idx < meshData.Positions.Count &&
                                            p2Idx >= 0 && p2Idx < meshData.Positions.Count)
                                        {
                                            Vector3 v0 = Vector3.Transform(meshData.Positions[p0Idx], worldMat);
                                            Vector3 v1 = Vector3.Transform(meshData.Positions[p1Idx], worldMat);
                                            Vector3 v2 = Vector3.Transform(meshData.Positions[p2Idx], worldMat);
                                            tris.Add(new Triangle { A = v0, B = v1, C = v2 });
                                        }
                                    }
                                }
                            }
                            else if (meshData.Vertices.Count > 0 && meshData.Faces.Count > 0)
                            {
                                foreach (var face in meshData.Faces)
                                {
                                    int f1 = face.V1, f2 = face.V2, f3 = face.V3;
                                    if (f1 >= 0 && f1 < meshData.Vertices.Count &&
                                        f2 >= 0 && f2 < meshData.Vertices.Count &&
                                        f3 >= 0 && f3 < meshData.Vertices.Count)
                                    {
                                        Vector3 v0 = Vector3.Transform(meshData.Vertices[f1].Position, worldMat);
                                        Vector3 v1 = Vector3.Transform(meshData.Vertices[f2].Position, worldMat);
                                        Vector3 v2 = Vector3.Transform(meshData.Vertices[f3].Position, worldMat);
                                        tris.Add(new Triangle { A = v0, B = v1, C = v2 });
                                    }
                                }
                            }
                            else
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
                }

                foreach (var child in node.Children)
                {
                    RecurseNode(child, worldMat);
                }
            }

            RecurseNode(hie.Root, Matrix4x4.Identity);
            return tris;
        }

        public static void ComputeTerrainBounds(List<Triangle>? triangles, out float minY, out float maxY)
        {
            minY = float.MaxValue;
            maxY = float.MinValue;
            if (triangles == null || triangles.Count == 0) return;

            foreach (var tri in triangles)
            {
                if (tri.A.Y < minY) minY = tri.A.Y;
                if (tri.A.Y > maxY) maxY = tri.A.Y;
                if (tri.B.Y < minY) minY = tri.B.Y;
                if (tri.B.Y > maxY) maxY = tri.B.Y;
                if (tri.C.Y < minY) minY = tri.C.Y;
                if (tri.C.Y > maxY) maxY = tri.C.Y;
            }
        }

        /// <summary>
        /// Snaps a 3D point and aligns its transform matrix to the ground surface using multi-ray contact points.
        /// Extracts contact points from lowest vertices / bounds, casting rays to compute true contact height and surface normal.
        /// </summary>
        public static (Vector3 SnappedPos, Matrix4x4 AlignedTransform) SnapAndAlignToSurface(
            Vector3 origPos,
            Matrix4x4 origTransform,
            List<Triangle> triangles,
            float contactRadius = 0.5f,
            float maxDropDistance = 500.0f,
            float rayStartHeight = 25.0f,
            float? floorLimitY = null)
        {
            if (triangles == null || triangles.Count == 0) return (origPos, origTransform);

            // 4 contact footprint rays around the object's base (Bottom vertices footprint)
            Vector3[] sampleOffsets = new[]
            {
                new Vector3(0, 0, 0),                           // Center ray
                new Vector3(-contactRadius, 0,  contactRadius), // Front-Left
                new Vector3( contactRadius, 0,  contactRadius), // Front-Right
                new Vector3(-contactRadius, 0, -contactRadius), // Rear-Left
                new Vector3( contactRadius, 0, -contactRadius), // Rear-Right
            };

            float totalHitY = 0f;
            int validHits = 0;
            Vector3 accumulatedNormal = Vector3.Zero;

            foreach (var offset in sampleOffsets)
            {
                Vector3 samplePos = origPos + offset;
                if (CastSingleRay(samplePos, triangles, maxDropDistance, rayStartHeight, floorLimitY, out float hitY, out Vector3 hitNormal))
                {
                    totalHitY += hitY;
                    accumulatedNormal += hitNormal;
                    validHits++;
                }
            }

            if (validHits == 0) return (origPos, origTransform);

            float avgHitY = totalHitY / validHits;
            Vector3 finalPos = new Vector3(origPos.X, avgHitY, origPos.Z);

            // If normal is stable, calculate slope orientation
            Matrix4x4 finalTransform = origTransform;
            finalTransform.M41 = finalPos.X;
            finalTransform.M42 = finalPos.Y;
            finalTransform.M43 = finalPos.Z;

            if (accumulatedNormal.LengthSquared() > 0.001f)
            {
                Vector3 groundNormal = Vector3.Normalize(accumulatedNormal);
                // Only align if surface is walkable ground (slope < 60 deg)
                if (groundNormal.Y > 0.5f)
                {
                    // TDR2000 vehicle/prop convention: M31..M33 is the basis Z-axis (+Z in model space, pointing backward / -forward).
                    Vector3 basisZ = new Vector3(origTransform.M31, origTransform.M32, origTransform.M33);
                    if (basisZ.LengthSquared() < 0.001f) basisZ = Vector3.UnitZ;
                    basisZ = Vector3.Normalize(basisZ);

                    // Compute orthonormal right and basis Z aligned to the terrain slope normal
                    Vector3 right = Vector3.Normalize(Vector3.Cross(groundNormal, basisZ));
                    Vector3 alignedBasisZ = Vector3.Normalize(Vector3.Cross(right, groundNormal));

                    finalTransform = new Matrix4x4(
                        right.X,         right.Y,         right.Z,         0,
                        groundNormal.X,  groundNormal.Y,  groundNormal.Z,  0,
                        alignedBasisZ.X, alignedBasisZ.Y, alignedBasisZ.Z, 0,
                        finalPos.X,      finalPos.Y,      finalPos.Z,      1
                    );
                }
            }

            return (finalPos, finalTransform);
        }

        /// <summary>
        /// Single ray cast returning hit height and triangle surface normal.
        /// </summary>
        public static bool CastSingleRay(
            Vector3 origPos,
            List<Triangle> triangles,
            float maxDropDistance,
            float rayStartHeight,
            float? floorLimitY,
            out float hitY,
            out Vector3 hitNormal)
        {
            hitY = float.MinValue;
            hitNormal = Vector3.UnitY;
            if (triangles == null || triangles.Count == 0) return false;

            Vector3 rayStart = new Vector3(origPos.X, origPos.Y + rayStartHeight, origPos.Z);
            Vector3 rayDir = new Vector3(0, -1.0f, 0);

            float highestHitY = float.MinValue;
            bool hitFound = false;
            float maxAllowedT = maxDropDistance + rayStartHeight;

            foreach (var tri in triangles)
            {
                if (RayTriangleIntersect(rayStart, rayDir, tri.A, tri.B, tri.C, out float t))
                {
                    if (t >= 0.0f && t <= maxAllowedT)
                    {
                        float currentHitY = rayStart.Y - t;
                        if (floorLimitY.HasValue && currentHitY < floorLimitY.Value) continue;

                        if (currentHitY <= (origPos.Y + rayStartHeight))
                        {
                            if (currentHitY > highestHitY)
                            {
                                highestHitY = currentHitY;
                                hitFound = true;

                                // Calculate triangle surface normal
                                Vector3 edge1 = tri.B - tri.A;
                                Vector3 edge2 = tri.C - tri.A;
                                Vector3 normal = Vector3.Cross(edge1, edge2);
                                if (normal.LengthSquared() > 0.0001f)
                                {
                                    hitNormal = Vector3.Normalize(normal);
                                    if (hitNormal.Y < 0) hitNormal = -hitNormal; // Ensure normal points upwards
                                }
                            }
                        }
                    }
                }
            }

            if (hitFound && highestHitY > (origPos.Y - maxDropDistance))
            {
                hitY = highestHitY;
                return true;
            }

            return false;
        }

        public static Vector3 SnapPointToSurface(
            Vector3 origPos,
            List<Triangle> triangles,
            float maxDropDistance = 500.0f,
            float rayStartHeight = 25.0f,
            float? floorLimitY = null)
        {
            if (CastSingleRay(origPos, triangles, maxDropDistance, rayStartHeight, floorLimitY, out float hitY, out _))
            {
                return new Vector3(origPos.X, hitY, origPos.Z);
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
