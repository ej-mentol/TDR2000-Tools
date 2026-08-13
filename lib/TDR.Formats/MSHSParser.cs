using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace TDR.PakLib.Formats
{
    public enum MeshMode : ushort
    {
        NGon = 0,
        TriIndexedPosition = 256,
        Tri = 512
    }

    public sealed class MeshVertex
    {
        public Vector3 Position { get; set; }
        public Vector3 Normal { get; set; }
        public Vector4 Color { get; set; }
        public Vector2 UV { get; set; }
        public int PositionIndex { get; set; } = -1;
    }

    public sealed class MeshFace
    {
        public Vector3 Normal { get; set; }
        public int V1 { get; set; }
        public int V2 { get; set; }
        public int V3 { get; set; }
        public List<MeshVertex> Vertices { get; } = new();
    }

    public sealed class TDRMeshData
    {
        public int FaceCount { get; set; }
        public MeshMode Mode { get; set; }
        public int VertexCount { get; set; }
        public Vector3 BoundingCenter { get; set; }
        public float BoundingRadius { get; set; }

        public List<Vector3> Positions { get; } = new();
        public List<MeshVertex> Vertices { get; } = new();
        public List<MeshFace> Faces { get; } = new();
    }

    public sealed class MSHSContainer
    {
        public List<TDRMeshData> Meshes { get; } = new();

        public static MSHSContainer Load(byte[] data, string fileName = "")
        {
            var container = new MSHSContainer();
            bool isSingleMesh = fileName.EndsWith(".msh", StringComparison.OrdinalIgnoreCase);

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);
            while (br.BaseStream.Position + 4 <= br.BaseStream.Length)
            {
                var mesh = new TDRMeshData
                {
                    FaceCount = br.ReadUInt16(),
                    Mode = (MeshMode)br.ReadUInt16()
                };

                if (mesh.Mode == MeshMode.Tri)
                {
                    br.ReadBytes(16); // Padding bytes
                }

                mesh.VertexCount = br.ReadInt32();
                if (mesh.VertexCount < 0 || mesh.VertexCount > 1_000_000) break;

                if (mesh.Mode != MeshMode.Tri)
                {
                    mesh.BoundingCenter = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    mesh.BoundingRadius = br.ReadSingle();
                }

                switch (mesh.Mode)
                {
                    case MeshMode.NGon:
                        for (int i = 0; i < mesh.FaceCount; i++)
                        {
                            int vertCount = br.ReadInt32();
                            if (vertCount < 0 || vertCount > 10_000) break;
                            var face = new MeshFace
                            {
                                Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle())
                            };

                            for (int j = 0; j < vertCount; j++)
                            {
                                face.Vertices.Add(new MeshVertex
                                {
                                    Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                                    Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                                    Color = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                                    UV = new Vector2(br.ReadSingle(), br.ReadSingle())
                                });
                            }

                            mesh.Faces.Add(face);
                        }
                        break;

                    case MeshMode.TriIndexedPosition:
                        for (int i = 0; i < mesh.VertexCount; i++)
                        {
                            mesh.Positions.Add(new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
                        }

                        for (int i = 0; i < mesh.FaceCount; i++)
                        {
                            var face = new MeshFace
                            {
                                Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle())
                            };

                            var v1 = new MeshVertex { PositionIndex = br.ReadInt32() };
                            var v2 = new MeshVertex { PositionIndex = br.ReadInt32() };
                            var v3 = new MeshVertex { PositionIndex = br.ReadInt32() };

                            v1.Color = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            v2.Color = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            v3.Color = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                            v1.UV = new Vector2(br.ReadSingle(), br.ReadSingle());
                            v2.UV = new Vector2(br.ReadSingle(), br.ReadSingle());
                            v3.UV = new Vector2(br.ReadSingle(), br.ReadSingle());

                            v1.Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            v2.Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            v3.Normal = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                            if (v1.PositionIndex >= 0 && v1.PositionIndex < mesh.Positions.Count &&
                                v2.PositionIndex >= 0 && v2.PositionIndex < mesh.Positions.Count &&
                                v3.PositionIndex >= 0 && v3.PositionIndex < mesh.Positions.Count)
                            {
                                face.Vertices.Add(v1);
                                face.Vertices.Add(v2);
                                face.Vertices.Add(v3);
                                mesh.Faces.Add(face);
                            }
                        }

                        if (isSingleMesh)
                        {
                            for (int i = 0; i < mesh.VertexCount; i++)
                            {
                                ushort pointCount = br.ReadUInt16();
                                for (int j = 0; j < pointCount; j++)
                                {
                                    br.ReadUInt16();
                                }
                            }
                        }
                        break;

                    case MeshMode.Tri:
                        for (int i = 0; i < mesh.VertexCount; i++)
                        {
                            mesh.Vertices.Add(new MeshVertex
                            {
                                Position = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                                Color = new Vector4(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                                UV = new Vector2(br.ReadSingle(), br.ReadSingle())
                            });
                        }

                        for (int i = 0; i < mesh.FaceCount; i++)
                        {
                            mesh.Faces.Add(new MeshFace
                            {
                                V1 = br.ReadInt32(),
                                V2 = br.ReadInt32(),
                                V3 = br.ReadInt32()
                            });
                        }

                        foreach (var face in mesh.Faces)
                        {
                            if (face.V1 >= 0 && face.V1 < mesh.Vertices.Count &&
                                face.V2 >= 0 && face.V2 < mesh.Vertices.Count &&
                                face.V3 >= 0 && face.V3 < mesh.Vertices.Count)
                            {
                                Vector3 v0 = mesh.Vertices[face.V1].Position;
                                Vector3 v1 = mesh.Vertices[face.V2].Position;
                                Vector3 v2 = mesh.Vertices[face.V3].Position;

                                Vector3 u = v0 - v1;
                                Vector3 v = v0 - v2;
                                Vector3 norm = Vector3.Cross(u, v);
                                if (norm.LengthSquared() > 0) norm = Vector3.Normalize(norm);
                                face.Normal = norm;

                                mesh.Vertices[face.V1].Normal += norm;
                                mesh.Vertices[face.V2].Normal += norm;
                                mesh.Vertices[face.V3].Normal += norm;
                            }
                        }

                        foreach (var vert in mesh.Vertices)
                        {
                            if (vert.Normal.LengthSquared() > 0)
                                vert.Normal = Vector3.Normalize(vert.Normal);
                        }
                        break;
                }

                container.Meshes.Add(mesh);
            }

            return container;
        }
    }
}
