using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace TDR.PakLib.Formats
{
    public sealed class SkeBone
    {
        public int ID { get; set; }
        public int ParentID { get; set; } = -1;
        public uint Flag { get; set; }
        public Matrix4x4 WorldMatrix { get; set; } = Matrix4x4.Identity;
        public Matrix4x4 LocalMatrix { get; set; } = Matrix4x4.Identity;
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
    }

    public sealed class SkeSkeleton
    {
        public int HeaderBoneCount { get; set; }
        public List<SkeBone> RawBones { get; } = new();

        /// <summary>
        /// Systematic extraction of all active skeleton bones based on the header bone count
        /// and bone ID records. Universally works for humans (25/27/30 bones),
        /// animals (sheep: 14, bull: 18, horse: 20, cat: 18, dog: 20, rat: 20), and aliens (12/13/19).
        /// </summary>
        public List<SkeBone> GetActiveBones()
        {
            var result = new List<SkeBone>();
            int targetCount = HeaderBoneCount > 0 ? HeaderBoneCount : Math.Min(25, RawBones.Count);

            // In TDR2000 SKE format, each active bone 'k' (from 0 to HeaderBoneCount-1)
            // is uniquely identified by its Bone ID in the record.
            var boneRecordMap = new Dictionary<int, SkeBone>();
            foreach (var raw in RawBones)
            {
                if (raw.ID >= 0 && raw.ID < targetCount && !boneRecordMap.ContainsKey(raw.ID))
                {
                    boneRecordMap[raw.ID] = raw;
                }
            }

            for (int k = 0; k < targetCount; k++)
            {
                if (boneRecordMap.TryGetValue(k, out var rec))
                {
                    result.Add(new SkeBone
                    {
                        ID = k,
                        ParentID = rec.ParentID,
                        Flag = rec.Flag,
                        WorldMatrix = rec.WorldMatrix,
                        Position = rec.Position,
                        Rotation = rec.Rotation
                    });
                }
                else if (k < RawBones.Count)
                {
                    result.Add(new SkeBone
                    {
                        ID = k,
                        ParentID = RawBones[k].ParentID,
                        Flag = RawBones[k].Flag,
                        WorldMatrix = RawBones[k].WorldMatrix,
                        Position = RawBones[k].Position,
                        Rotation = RawBones[k].Rotation
                    });
                }
            }

            // Compute parent-relative local matrices for active bones
            for (int i = 0; i < result.Count; i++)
            {
                int p = result[i].ParentID;
                if (p >= 0 && p < result.Count && p != i)
                {
                    if (Matrix4x4.Invert(result[p].WorldMatrix, out var invP))
                    {
                        result[i].LocalMatrix = Matrix4x4.Multiply(result[i].WorldMatrix, invP);
                    }
                    else
                    {
                        result[i].LocalMatrix = result[i].WorldMatrix;
                    }
                }
                else
                {
                    result[i].LocalMatrix = result[i].WorldMatrix;
                }
            }

            return result;
        }

        public List<SkeBone> GetAnatomical25Bones() => GetActiveBones();

        public static SkeSkeleton? Load(byte[] data)
        {
            if (data == null || data.Length < 4)
                return null;

            var skeleton = new SkeSkeleton();
            int totalRecords = (data.Length - 4) / 76;
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            skeleton.HeaderBoneCount = (int)br.ReadUInt32();
            var parentStack = new Stack<int>();

            for (int i = 0; i < totalRecords; i++)
            {
                if (br.BaseStream.Position + 76 > br.BaseStream.Length) break;

                float[] m = new float[16];
                for (int mi = 0; mi < 16; mi++)
                {
                    m[mi] = br.ReadSingle();
                }

                uint pad = br.ReadUInt32();
                int boneId = br.ReadInt32();
                uint flag = br.ReadUInt32();

                // Convert 3ds Max/DirectX Row-Major matrix (decimeters) to Column-Major (meters)
                var worldMat = new Matrix4x4(
                    m[0], m[1], m[2], 0.0f,
                    m[4], m[5], m[6], 0.0f,
                    m[8], m[9], m[10], 0.0f,
                    m[12] * 0.1f, m[13] * 0.1f, m[14] * 0.1f, 1.0f
                );

                Matrix4x4.Decompose(worldMat, out var scale, out var rot, out var trans);

                int currentParent = parentStack.Count > 0 ? parentStack.Peek() : -1;
                int nodeRef = currentParent;

                if (boneId >= 0)
                {
                    nodeRef = boneId;
                }

                var bone = new SkeBone
                {
                    ID = boneId,
                    ParentID = currentParent,
                    Flag = flag,
                    WorldMatrix = worldMat,
                    Position = trans,
                    Rotation = rot
                };
                skeleton.RawBones.Add(bone);

                // Tree DFS stack manipulation
                if (flag == 2 || flag == 3 || flag == 4 || flag == 8)
                {
                    parentStack.Push(nodeRef);
                }
                else if (flag == 0)
                {
                    if (parentStack.Count > 0)
                    {
                        parentStack.Pop();
                    }
                }
            }

            return skeleton;
        }
    }
}
