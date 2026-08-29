using System;
using System.Collections.Generic;
using System.Numerics;
using TDR.PakLib.Formats;

namespace TDR.Tools.Export
{
    public readonly struct GeometryVertex
    {
        public readonly Vector3 Position;
        public readonly Vector3 Normal;
        public readonly Vector2 UV;

        public GeometryVertex(Vector3 position, Vector3 normal, Vector2 uv)
        {
            Position = position;
            Normal = normal;
            UV = uv;
        }
    }

    public sealed class TriangleStream
    {
        public readonly List<GeometryVertex> Vertices = new();
    }

    /// <summary>
    /// Canonical extractor for turning TDRMeshData into world-space TriangleStreams.
    /// Strict transcription of ObjExporter.WriteSubMesh geometry parsing.
    /// </summary>
    public static class MeshGeometryReader
    {
        public static void AppendTriangles(TDRMeshData mesh, Matrix4x4 transform, TriangleStream output, bool doubleSided = false)
        {
            if (mesh == null || output == null) return;

            switch (mesh.Mode)
            {
                case MeshMode.TriIndexedPosition:
                    foreach (var face in mesh.Faces)
                    {
                        var fv = new GeometryVertex[3];
                        bool valid = true;
                        for (int i = 0; i < 3; i++)
                        {
                            var vert = face.Vertices[i];
                            if (vert.PositionIndex < 0 || vert.PositionIndex >= mesh.Positions.Count) { valid = false; break; }
                            Vector3 pos = Vector3.Transform(mesh.Positions[vert.PositionIndex], transform);
                            Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);
                            Vector2 uv = vert.UV;
                            fv[i] = new GeometryVertex(pos, norm, uv);
                        }
                        if (!valid) continue;
                        output.Vertices.Add(fv[0]);
                        output.Vertices.Add(fv[1]);
                        output.Vertices.Add(fv[2]);
                        if (doubleSided)
                        {
                            output.Vertices.Add(new GeometryVertex(fv[0].Position, -fv[0].Normal, fv[0].UV));
                            output.Vertices.Add(new GeometryVertex(fv[2].Position, -fv[2].Normal, fv[2].UV));
                            output.Vertices.Add(new GeometryVertex(fv[1].Position, -fv[1].Normal, fv[1].UV));
                        }
                    }
                    break;

                case MeshMode.Tri:
                    var triVertices = new List<GeometryVertex>(mesh.Vertices.Count);
                    foreach (var vert in mesh.Vertices)
                    {
                        Vector3 pos = Vector3.Transform(vert.Position, transform);
                        Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);
                        Vector2 uv = vert.UV;

                        triVertices.Add(new GeometryVertex(pos, norm, uv));
                    }

                    foreach (var face in mesh.Faces)
                    {
                        if (face.V1 >= 0 && face.V1 < triVertices.Count &&
                            face.V2 >= 0 && face.V2 < triVertices.Count &&
                            face.V3 >= 0 && face.V3 < triVertices.Count)
                        {
                            var v0 = triVertices[face.V1];
                            var v1 = triVertices[face.V2];
                            var v2 = triVertices[face.V3];
                            output.Vertices.Add(v0);
                            output.Vertices.Add(v1);
                            output.Vertices.Add(v2);
                            if (doubleSided)
                            {
                                output.Vertices.Add(new GeometryVertex(v0.Position, -v0.Normal, v0.UV));
                                output.Vertices.Add(new GeometryVertex(v2.Position, -v2.Normal, v2.UV));
                                output.Vertices.Add(new GeometryVertex(v1.Position, -v1.Normal, v1.UV));
                            }
                        }
                    }
                    break;

                case MeshMode.NGon:
                default:
                    foreach (var face in mesh.Faces)
                    {
                        if (face.Vertices.Count < 3) continue;

                        var faceVerts = new List<GeometryVertex>(face.Vertices.Count);
                        foreach (var vert in face.Vertices)
                        {
                            Vector3 pos = Vector3.Transform(vert.Position, transform);
                            Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);
                            Vector2 uv = vert.UV;

                            faceVerts.Add(new GeometryVertex(pos, norm, uv));
                        }

                        // Fan triangulation (0, k, k+1)
                        for (int k = 1; k < faceVerts.Count - 1; k++)
                        {
                            var v0 = faceVerts[0];
                            var v1 = faceVerts[k];
                            var v2 = faceVerts[k + 1];
                            output.Vertices.Add(v0);
                            output.Vertices.Add(v1);
                            output.Vertices.Add(v2);
                            if (doubleSided)
                            {
                                output.Vertices.Add(new GeometryVertex(v0.Position, -v0.Normal, v0.UV));
                                output.Vertices.Add(new GeometryVertex(v2.Position, -v2.Normal, v2.UV));
                                output.Vertices.Add(new GeometryVertex(v1.Position, -v1.Normal, v1.UV));
                            }
                        }
                    }
                    break;
            }
        }

        public static float ComputeHierarchyMinimumY(TDRHierarchy hie, Func<string, byte[]?> loader)
        {
            if (hie?.Root == null) return float.MaxValue;
            float minY = float.MaxValue;
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
                                foreach (var pos in meshData.Positions)
                                {
                                    float y = Vector3.Transform(pos, worldMat).Y;
                                    if (y < minY) minY = y;
                                }
                            }
                            else
                            {
                                foreach (var vert in meshData.Vertices)
                                {
                                    float y = Vector3.Transform(vert.Position, worldMat).Y;
                                    if (y < minY) minY = y;
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
            return minY;
        }
    }
}
