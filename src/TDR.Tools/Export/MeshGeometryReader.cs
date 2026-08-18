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
        public static void AppendTriangles(TDRMeshData mesh, Matrix4x4 transform, TriangleStream output)
        {
            if (mesh == null || output == null) return;

            switch (mesh.Mode)
            {
                case MeshMode.TriIndexedPosition:
                    foreach (var face in mesh.Faces)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            var vert = face.Vertices[i];
                            if (vert.PositionIndex < 0 || vert.PositionIndex >= mesh.Positions.Count) continue;
                            Vector3 pos = Vector3.Transform(mesh.Positions[vert.PositionIndex], transform);
                            Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);
                            Vector2 uv = vert.UV;

                            output.Vertices.Add(new GeometryVertex(pos, norm, uv));
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
                            output.Vertices.Add(triVertices[face.V1]);
                            output.Vertices.Add(triVertices[face.V2]);
                            output.Vertices.Add(triVertices[face.V3]);
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
                            output.Vertices.Add(faceVerts[0]);
                            output.Vertices.Add(faceVerts[k]);
                            output.Vertices.Add(faceVerts[k + 1]);
                        }
                    }
                    break;
            }
        }
    }
}
