using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;

namespace TDR.PakLib.Formats
{
    public sealed class PedPlacement
    {
        public int Type { get; set; }
        public int SkinIndex { get; set; }
        public int StandardAniIndex { get; set; } = -1;
        public Vector3 Position { get; set; }
        public float HeadingDegrees { get; set; }
        public float HeadingRadians => HeadingDegrees * (MathF.PI / 180.0f);

        public static List<PedPlacement> Load(byte[] data)
        {
            if (data == null || data.Length == 0)
                return new List<PedPlacement>();

            string text = Encoding.ASCII.GetString(data);
            return Load(text);
        }

        public static List<PedPlacement> Load(string text)
        {
            var list = new List<PedPlacement>();
            var lines = DescriptorReader.GetCleanLines(text);

            foreach (string line in lines)
            {
                var parts = DescriptorReader.TokenizeLine(line);

                // 7 tokens: Type Skin Ani PosX PosY PosZ Dir
                if (parts.Count >= 6 &&
                    DescriptorReader.TryParseFloat(parts[3], out float px) &&
                    DescriptorReader.TryParseFloat(parts[4], out float py) &&
                    DescriptorReader.TryParseFloat(parts[5], out float pz))
                {
                    DescriptorReader.TryParseInt(parts[0], out int type);
                    DescriptorReader.TryParseInt(parts[1], out int skin);
                    DescriptorReader.TryParseInt(parts[2], out int ani);

                    float dir = 0f;
                    if (parts.Count >= 7)
                    {
                        DescriptorReader.TryParseFloat(parts[6], out dir);
                    }

                    list.Add(new PedPlacement
                    {
                        Type = type,
                        SkinIndex = skin,
                        StandardAniIndex = ani,
                        Position = new Vector3(px, py, pz),
                        HeadingDegrees = dir
                    });
                }
                else if (parts.Count == 3 &&
                         DescriptorReader.TryParseFloat(parts[0], out float sx) &&
                         DescriptorReader.TryParseFloat(parts[1], out float sy) &&
                         DescriptorReader.TryParseFloat(parts[2], out float sz))
                {
                    list.Add(new PedPlacement
                    {
                        Type = 0,
                        SkinIndex = 0,
                        StandardAniIndex = -1,
                        Position = new Vector3(sx, sy, sz),
                        HeadingDegrees = 0f
                    });
                }
            }

            return list;
        }
    }

    public sealed class PedSkinTexture
    {
        public string FaceTexture { get; set; } = string.Empty;
        public string BodyTexture { get; set; } = string.Empty;
        public string DamageFaceTexture { get; set; } = string.Empty;
        public string DamageBodyTexture { get; set; } = string.Empty;
        public int SkinIndex { get; set; }
        public string Archetype { get; set; } = string.Empty;
    }

    public sealed class PedDescriptor
    {
        public string AiPathsFile { get; set; } = string.Empty;
        public string PlacementFile { get; set; } = string.Empty;
        public List<string> SkeletonDescriptors { get; } = new();
        public List<string> SkinMeshes { get; } = new();
        public List<PedSkinTexture> Textures { get; } = new();

        public static PedDescriptor? Load(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            string text = Encoding.ASCII.GetString(data);
            return Load(text);
        }

        public static PedDescriptor? Load(string text)
        {
            var desc = new PedDescriptor();
            var lines = DescriptorReader.GetCleanLines(text);

            foreach (string line in lines)
            {
                var tokens = DescriptorReader.TokenizeLine(line);
                if (tokens.Count == 0) continue;

                string firstToken = tokens[0];

                if (firstToken.EndsWith(".pai", StringComparison.OrdinalIgnoreCase))
                {
                    desc.AiPathsFile = firstToken;
                    continue;
                }

                if (firstToken.EndsWith("Placement.txt", StringComparison.OrdinalIgnoreCase))
                {
                    desc.PlacementFile = firstToken;
                    continue;
                }

                // Skip decorative section headers
                if (line.StartsWith("num skeletons", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("num skins", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("num textures", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (firstToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && firstToken.Contains("Skeleton", StringComparison.OrdinalIgnoreCase))
                {
                    desc.SkeletonDescriptors.Add(firstToken);
                    continue;
                }

                if (firstToken.EndsWith(".ski", StringComparison.OrdinalIgnoreCase))
                {
                    desc.SkinMeshes.Add(firstToken);
                    continue;
                }

                // Texture definitions line: "Face" "Body" "DamFace" "DamBody" skin_idx Archetype
                if (tokens.Count >= 5 && DescriptorReader.TryParseInt(tokens[4], out int skinIdx))
                {
                    var entry = new PedSkinTexture
                    {
                        FaceTexture = tokens[0],
                        BodyTexture = tokens[1],
                        DamageFaceTexture = tokens[2],
                        DamageBodyTexture = tokens[3],
                        SkinIndex = skinIdx,
                        Archetype = tokens.Count > 5 ? tokens[5] : string.Empty
                    };
                    desc.Textures.Add(entry);
                }
            }

            return desc;
        }
    }

    public sealed class PedSkeletonDescriptor
    {
        public string SkeletonName { get; set; } = string.Empty;
        public string SkeletonFile { get; set; } = string.Empty;
        public string DefaultAnimation { get; set; } = string.Empty;
        public string PedBodyFile { get; set; } = string.Empty;
        public Dictionary<string, string> Animations { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static PedSkeletonDescriptor? Load(byte[] data)
        {
            if (data == null || data.Length == 0)
                return null;

            string text = Encoding.ASCII.GetString(data);
            return Load(text);
        }

        public static PedSkeletonDescriptor? Load(string text)
        {
            var desc = new PedSkeletonDescriptor();
            var lines = DescriptorReader.GetCleanLines(text);
            bool inAnimations = false;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];

                if (line.Equals("START_ANIMATIONS", StringComparison.OrdinalIgnoreCase))
                {
                    inAnimations = true;
                    continue;
                }

                if (line.Equals("END_ANIMATIONS", StringComparison.OrdinalIgnoreCase))
                {
                    inAnimations = false;
                    continue;
                }

                var tokens = DescriptorReader.TokenizeLine(line);
                if (tokens.Count == 0) continue;

                if (!inAnimations)
                {
                    string firstToken = tokens[0];
                    if (firstToken.EndsWith(".ske", StringComparison.OrdinalIgnoreCase))
                    {
                        desc.SkeletonFile = firstToken;
                    }
                    else if (firstToken.EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
                    {
                        desc.DefaultAnimation = firstToken;
                    }
                    else if (firstToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && firstToken.Contains("PedBody", StringComparison.OrdinalIgnoreCase))
                    {
                        desc.PedBodyFile = firstToken;
                    }
                    else if (tokens.Count == 1 && !firstToken.Contains('.') && !DescriptorReader.TryParseFloat(firstToken, out _))
                    {
                        desc.SkeletonName = firstToken;
                    }
                }
                else
                {
                    // Animation line format: "ActionName" "FileName.ani" Speed PlayRate
                    if (tokens.Count >= 2 && tokens[1].EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
                    {
                        desc.Animations[tokens[0]] = tokens[1];
                    }
                    else if (tokens.Count == 1 && tokens[0].EndsWith(".ani", StringComparison.OrdinalIgnoreCase))
                    {
                        string file = tokens[0];
                        string action = Path.GetFileNameWithoutExtension(file);
                        desc.Animations[action] = file;
                    }
                }
            }

            return desc;
        }
    }
}
