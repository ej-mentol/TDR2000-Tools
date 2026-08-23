using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace TDR.PakLib.Formats
{
    public sealed class AniBoneTransform
    {
        public int BoneIndex { get; set; }
        public float[] RawMatrix { get; } = new float[16];
        public Matrix4x4 DeltaMatrix { get; set; } = Matrix4x4.Identity;
        public Vector3 Translation { get; set; }
        public Quaternion Rotation { get; set; } = Quaternion.Identity;
    }

    public sealed class AniFrame
    {
        public int FrameIndex { get; set; }
        public List<AniBoneTransform> BoneTransforms { get; } = new();
    }

    public sealed class AniAnimation
    {
        public string Name { get; set; } = string.Empty;
        public int FrameCount { get; set; }
        public float FPS { get; set; } = 25.0f;
        public int BoneCount { get; set; }
        public List<AniFrame> Frames { get; } = new();

        public static AniAnimation? Load(byte[] data, string name = "")
        {
            if (data == null || data.Length < 12)
                return null;

            var animation = new AniAnimation { Name = name };
            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            animation.FrameCount = br.ReadInt32();
            animation.FPS = br.ReadSingle();
            animation.BoneCount = br.ReadInt32();

            for (int f = 0; f < animation.FrameCount; f++)
            {
                var frame = new AniFrame { FrameIndex = f };

                for (int b = 0; b < animation.BoneCount; b++)
                {
                    if (br.BaseStream.Position + 64 > br.BaseStream.Length) break;

                    var transform = new AniBoneTransform { BoneIndex = b };
                    for (int mi = 0; mi < 16; mi++)
                    {
                        transform.RawMatrix[mi] = br.ReadSingle();
                    }

                    float[] raw = transform.RawMatrix;

                    // DirectX Left-Handed -> OpenGL / glTF Right-Handed Coordinate Conversion
                    var deltaMat = new Matrix4x4(
                         raw[0],  raw[4], -raw[8],  0.0f,
                         raw[1],  raw[5], -raw[9],  0.0f,
                        -raw[2], -raw[6],  raw[10], 0.0f,
                         raw[12], raw[13], -raw[14], 1.0f
                    );

                    transform.DeltaMatrix = deltaMat;
                    Matrix4x4.Decompose(deltaMat, out var scale, out var rot, out var trans);
                    transform.Translation = trans;
                    transform.Rotation = rot;

                    frame.BoneTransforms.Add(transform);
                }

                animation.Frames.Add(frame);
            }

            return animation;
        }
    }
}
