using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace TDR.PakLib.Formats
{
    public sealed class SkiVertex
    {
        public Vector3 Position { get; set; }
        public Vector3 Normal { get; set; }
        public Vector2 UV { get; set; }
        public Vector4 Color { get; set; } = Vector4.One;
        public byte[] BoneIndices { get; } = new byte[4];
        public float[] Weights { get; } = new float[4];

        public SkiVertex()
        {
            Weights[0] = 1.0f;
        }
    }

    public sealed class SkiPolygon
    {
        public int Type { get; set; } // 3 = Triangle, 4 = Quad
        public List<SkiVertex> Vertices { get; } = new();
    }

    public sealed class SkiPart
    {
        public int PartIndex { get; set; }
        public int LODIndex { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<SkiPolygon> Polygons { get; } = new();

        public List<Vector3> Positions { get; } = new();
        public List<Vector3> Normals { get; } = new();
        public List<Vector2> UVs { get; } = new();
        public List<ushort[]> Joints { get; } = new();
        public List<Vector4> Weights { get; } = new();
        public List<ushort> Indices { get; } = new();
    }

    public sealed class SkiModel
    {
        private const int MaxNameLength = 256;
        private const int MaxParts = 60;

        public string Name { get; set; } = string.Empty;
        public int LODCount { get; set; }
        public List<SkiPart> Parts { get; } = new();

        public static SkiModel? Load(byte[] data, int targetLod = 0, int numBones = 25)
        {
            if (data == null || data.Length < 8)
                return null;

            var model = new SkiModel();
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            int nameLen = br.ReadInt32();
            if (nameLen > 0 && nameLen < MaxNameLength && br.BaseStream.Position + nameLen <= br.BaseStream.Length)
            {
                byte[] nameBytes = br.ReadBytes(nameLen);
                model.Name = Encoding.Latin1.GetString(nameBytes).TrimEnd('\0');
            }

            model.LODCount = br.ReadInt32();
            long scanPos = br.BaseStream.Position;

            while (scanPos + 8 <= br.BaseStream.Length && model.Parts.Count < MaxParts)
            {
                br.BaseStream.Position = scanPos;
                int val = br.ReadInt32();
                if (val >= 1 && val <= 500)
                {
                    int nextT = br.ReadInt32();
                    if (nextT >= 3 && nextT <= 10)
                    {
                        int polyCount = val;
                        br.BaseStream.Position = scanPos + 4;

                        var part = new SkiPart
                        {
                            PartIndex = model.Parts.Count,
                            LODIndex = 0,
                            Name = $"Part_{model.Parts.Count:D2}"
                        };

                        bool partValid = true;
                        for (int p = 0; p < polyCount; p++)
                        {
                            if (br.BaseStream.Position + 4 > br.BaseStream.Length) { partValid = false; break; }
                            int pType = br.ReadInt32();
                            if (pType < 3 || pType > 10) { partValid = false; break; }

                            var polygon = new SkiPolygon { Type = pType };
                            var polyIdx = new List<ushort>();

                            for (int v = 0; v < pType; v++)
                            {
                                if (br.BaseStream.Position + 28 > br.BaseStream.Length) { partValid = false; break; }

                                float vx = br.ReadSingle();
                                float vy = br.ReadSingle();
                                float vz = br.ReadSingle();
                                float nx = br.ReadSingle();
                                float ny = br.ReadSingle();
                                float nz = br.ReadSingle();

                                uint boneInfo = br.ReadUInt32();
                                byte b0 = (byte)(boneInfo & 0xFF);
                                byte b1 = (byte)((boneInfo >> 8) & 0xFF);
                                byte b2 = (byte)((boneInfo >> 16) & 0xFF);
                                byte b3 = (byte)((boneInfo >> 24) & 0xFF);

                                int boneCnt = 1;
                                if (b1 > 0) boneCnt = 2;
                                if (b2 > 0) boneCnt = 3;
                                if (b3 > 0) boneCnt = 4;

                                float[] w = new float[] { 1.0f, 0.0f, 0.0f, 0.0f };
                                if (boneCnt > 1 && br.BaseStream.Position + boneCnt * 4 <= br.BaseStream.Length)
                                {
                                    for (int wIdx = 0; wIdx < boneCnt; wIdx++)
                                    {
                                        w[wIdx] = br.ReadSingle();
                                    }
                                }

                                // Normalize weights to strictly 1.0
                                float totalW = w[0] + w[1] + w[2] + w[3];
                                if (totalW > 0.0001f)
                                {
                                    float invW = 1.0f / totalW;
                                    w[0] *= invW;
                                    w[1] *= invW;
                                    w[2] *= invW;
                                    w[3] *= invW;
                                }
                                else
                                {
                                    w[0] = 1.0f;
                                }

                                // Remap 1-based bone index to 0-based joint index
                                byte[] rawBones = new byte[] { b0, b1, b2, b3 };
                                ushort[] j = new ushort[4];
                                for (int slot = 0; slot < 4; slot++)
                                {
                                    if (w[slot] > 0.0f && rawBones[slot] > 0)
                                    {
                                        j[slot] = (ushort)Math.Clamp(rawBones[slot] - 1, 0, numBones - 1);
                                    }
                                    else
                                    {
                                        j[slot] = 0;
                                    }
                                }

                                var vert = new SkiVertex
                                {
                                    Position = new Vector3(vx, vy, vz),
                                    Normal = new Vector3(nx, ny, nz)
                                };
                                vert.BoneIndices[0] = b0;
                                vert.BoneIndices[1] = b1;
                                vert.BoneIndices[2] = b2;
                                vert.BoneIndices[3] = b3;
                                Array.Copy(w, vert.Weights, 4);

                                ushort idx = (ushort)part.Positions.Count;
                                part.Positions.Add(vert.Position);
                                part.Normals.Add(vert.Normal);
                                part.Joints.Add(j);
                                part.Weights.Add(new Vector4(w[0], w[1], w[2], w[3]));
                                polyIdx.Add(idx);

                                polygon.Vertices.Add(vert);
                            }

                            if (!partValid) break;

                            // Read UV and RGBA color for each vertex
                            for (int v = 0; v < pType; v++)
                            {
                                if (br.BaseStream.Position + 12 > br.BaseStream.Length) { partValid = false; break; }
                                uint rgba = br.ReadUInt32();
                                float u = br.ReadSingle();
                                float vCoord = br.ReadSingle();

                                if (v < polygon.Vertices.Count)
                                {
                                    polygon.Vertices[v].UV = new Vector2(u, vCoord);
                                    polygon.Vertices[v].Color = new Vector4(
                                        (rgba & 0xFF) / 255.0f,
                                        ((rgba >> 8) & 0xFF) / 255.0f,
                                        ((rgba >> 16) & 0xFF) / 255.0f,
                                        ((rgba >> 24) & 0xFF) / 255.0f
                                    );
                                }
                                part.UVs.Add(new Vector2(u, vCoord));
                            }

                            if (!partValid) break;

                            if (pType == 3)
                            {
                                part.Indices.AddRange(polyIdx);
                            }
                            else if (pType == 4 && polyIdx.Count == 4)
                            {
                                part.Indices.Add(polyIdx[0]);
                                part.Indices.Add(polyIdx[1]);
                                part.Indices.Add(polyIdx[2]);

                                part.Indices.Add(polyIdx[0]);
                                part.Indices.Add(polyIdx[2]);
                                part.Indices.Add(polyIdx[3]);
                            }
                            else if (pType >= 5 && polyIdx.Count == pType)
                            {
                                for (int i = 1; i < pType - 1; i++)
                                {
                                    part.Indices.Add(polyIdx[0]);
                                    part.Indices.Add(polyIdx[i]);
                                    part.Indices.Add(polyIdx[i + 1]);
                                }
                            }

                            part.Polygons.Add(polygon);
                        }

                        if (partValid && part.Positions.Count > 0)
                        {
                            model.Parts.Add(part);
                            scanPos = br.BaseStream.Position;
                            continue;
                        }
                    }
                }

                scanPos += 4;
            }

            // Systematic LOD 0 Boundary Extraction:
            // The first part(s) bind to the head/root bone hierarchy.
            // As the part stream proceeds through body and limb extremities, the reappearance
            // of the initial root/head bone set indicates the beginning of LOD 1.
            if (model.Parts.Count > 0 && targetLod == 0)
            {
                var rawParts = model.Parts.ToList();
                var rootBones = new HashSet<byte>();
                foreach (var polygon in rawParts[0].Polygons)
                {
                    foreach (var v in polygon.Vertices)
                    {
                        foreach (var b in v.BoneIndices)
                        {
                            if (b > 0) rootBones.Add(b);
                        }
                    }
                }

                bool seenLimbs = false;
                int lod0Count = rawParts.Count;

                for (int i = 1; i < rawParts.Count; i++)
                {
                    var partBones = new HashSet<byte>();
                    foreach (var polygon in rawParts[i].Polygons)
                    {
                        foreach (var v in polygon.Vertices)
                        {
                            foreach (var b in v.BoneIndices)
                            {
                                if (b > 0) partBones.Add(b);
                            }
                        }
                    }

                    bool hasRootBone = false;
                    bool hasOtherBone = false;
                    foreach (var b in partBones)
                    {
                        if (rootBones.Contains(b)) hasRootBone = true;
                        else hasOtherBone = true;
                    }

                    if (hasOtherBone)
                    {
                        seenLimbs = true;
                    }
                    else if (seenLimbs && hasRootBone)
                    {
                        lod0Count = i;
                        break;
                    }
                }

                model.Parts.Clear();
                for (int i = 0; i < lod0Count; i++)
                {
                    rawParts[i].PartIndex = i;
                    model.Parts.Add(rawParts[i]);
                }
            }

            return model;
        }
    }
}
