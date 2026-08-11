using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using TDR.PakLib;

namespace TDR.PakLib.Formats
{
    public sealed class TDRNode
    {
        public enum NodeType
        {
            Matrix = 1,
            Texture = 2,
            Mesh = 3,
            Expression = 4,
            Material = 5,
            Spline = 6,
            DynamicCollision = 7,
            CullNode = 8
        }

        public int ID { get; set; }
        public NodeType Type { get; set; }
        public int Index { get; set; }
        public int Child { get; set; } = -1;
        public int Sibling { get; set; } = -1;
        public string Name { get; set; } = string.Empty;
        public TDRNode? Parent { get; set; }
        public List<TDRNode> Children { get; } = new();
        public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

        public TDRNode(string name, int id, string rawLine)
        {
            Name = name;
            ID = id;

            string[] parts = rawLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                Type = (NodeType)int.Parse(parts[0], CultureInfo.InvariantCulture);
                Index = int.Parse(parts[1], CultureInfo.InvariantCulture);
                Child = parts[2].Equals("NULL", StringComparison.OrdinalIgnoreCase) ? -1 : int.Parse(parts[2], CultureInfo.InvariantCulture);
                Sibling = parts[3].Equals("NULL", StringComparison.OrdinalIgnoreCase) ? -1 : int.Parse(parts[3], CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class TDRMaterial
    {
        public string Name { get; set; } = string.Empty;
        public Vector4 Color { get; set; } = Vector4.One;
        public int TextureIndex { get; set; } = -1;

        public TDRMaterial(string rawLine)
        {
            string[] parts = rawLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            int offset = 0;
            if (parts[0].StartsWith("\""))
            {
                Name = parts[0].Trim('"');
                offset = 1;
            }

            if (parts.Length >= offset + 5)
            {
                Color = new Vector4(
                    float.Parse(parts[offset + 0], CultureInfo.InvariantCulture),
                    float.Parse(parts[offset + 1], CultureInfo.InvariantCulture),
                    float.Parse(parts[offset + 2], CultureInfo.InvariantCulture),
                    float.Parse(parts[offset + 3], CultureInfo.InvariantCulture)
                );
                int rawIdx = int.Parse(parts[offset + 4], CultureInfo.InvariantCulture);
                TextureIndex = rawIdx > 0 ? rawIdx - 1 : -1;
            }
            else if (parts.Length >= offset + 1)
            {
                if (int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int texIdx))
                {
                    TextureIndex = texIdx > 0 ? texIdx - 1 : -1;
                }
            }
        }
    }

    public sealed class TDRHierarchy
    {
        public int Version { get; set; }
        public float AnimationFps { get; set; } = 60.0f;
        public List<string> Textures { get; } = new();
        public List<TDRMaterial> Materials { get; } = new();
        public List<Matrix4x4> Matrices { get; } = new();
        public List<string> Meshes { get; } = new();
        public List<TDRNode> Nodes { get; } = new();
        public TDRNode? Root => Nodes.Count > 0 ? Nodes[0] : null;

        public static TDRHierarchy Load(byte[] data, string name)
        {
            var hie = new TDRHierarchy();
            if (data == null || data.Length == 0) return hie;

            string[] lines;
            using (var ms = new MemoryStream(data))
            using (var sr = new StreamReader(ms))
            {
                string text = sr.ReadToEnd();
                lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(l => l.Trim())
                            .ToArray();
            }

            for (int i = 0; i < lines.Length; i++)
            {
                string lineLower = lines[i].ToLowerInvariant();
                switch (lineLower)
                {
                    case "//version number":
                        if (i + 2 < lines.Length)
                        {
                            hie.Version = int.Parse(lines[i + 2], CultureInfo.InvariantCulture);
                            i += 2;
                            if (hie.Version == 257 && i + 1 < lines.Length)
                            {
                                if (float.TryParse(lines[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float fps))
                                {
                                    hie.AnimationFps = fps;
                                    i++;
                                }
                            }
                        }
                        break;

                    case "// number of textures":
                        if (i + 1 < lines.Length) { int count = int.Parse(lines[++i], CultureInfo.InvariantCulture); }
                        break;

                    case "// texture name list":
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(next)) { i++; continue; }
                            if (next.StartsWith("//"))
                            {
                                if (IsKnownSectionHeader(next)) break;
                                i++; continue;
                            }
                            hie.Textures.Add(lines[++i].Replace("\"", "").Trim());
                        }
                        break;

                    case "// number of materials":
                        if (i + 1 < lines.Length) { int count = int.Parse(lines[++i], CultureInfo.InvariantCulture); }
                        break;

                    case "// material name list":
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(next)) { i++; continue; }
                            if (next.StartsWith("//"))
                            {
                                if (IsKnownSectionHeader(next)) break;
                                i++; continue;
                            }
                            string matLine = lines[++i];
                            hie.Materials.Add(new TDRMaterial(matLine));
                        }
                        break;

                    case "// number of matrices":
                        if (i + 1 < lines.Length) { int count = int.Parse(lines[++i], CultureInfo.InvariantCulture); }
                        break;

                    case "// matrix name list":
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(next)) { i++; continue; }
                            if (next.StartsWith("//"))
                            {
                                if (IsKnownSectionHeader(next)) break;
                                i++; continue;
                            }
                            string l1 = lines[++i];
                            if (l1.StartsWith("\"")) continue; // Matrix name line ("NONE")

                            string l2 = i + 1 < lines.Length ? lines[++i] : "0 1 0 0;";
                            string l3 = i + 1 < lines.Length ? lines[++i] : "0 0 1 0;";
                            string l4 = i + 1 < lines.Length ? lines[++i] : "0 0 0 1;";
                            if (i + 1 < lines.Length && lines[i + 1].StartsWith("\"")) i++; // Skip name if present

                            Matrix4x4 mat = ParseMatrix4x4(l1, l2, l3, l4);
                            hie.Matrices.Add(mat);
                        }
                        break;

                    case "// number of meshes":
                        if (i + 1 < lines.Length) { int count = int.Parse(lines[++i], CultureInfo.InvariantCulture); }
                        break;

                    case "// mesh name list":
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(next)) { i++; continue; }
                            if (next.StartsWith("//"))
                            {
                                if (IsKnownSectionHeader(next)) break;
                                i++; continue;
                            }
                            hie.Meshes.Add(lines[++i].Replace("\"", "").Trim());
                        }
                        break;

                    case "// node list :":
                    case "// node list":
                        int nodeIdx = 0;
                        while (i + 1 < lines.Length)
                        {
                            string next = lines[i + 1].Trim();
                            if (string.IsNullOrWhiteSpace(next)) { i++; continue; }
                            if (next.StartsWith("//"))
                            {
                                if (IsKnownSectionHeader(next)) break;
                                i++; continue;
                            }

                            string nLine = lines[++i].Trim();
                            var node = new TDRNode($"Node_{nodeIdx}", nodeIdx, nLine);
                            if (node.Type == TDRNode.NodeType.Matrix && node.Index >= 0 && node.Index < hie.Matrices.Count)
                            {
                                node.Transform = hie.Matrices[node.Index];
                            }
                            hie.Nodes.Add(node);
                            nodeIdx++;
                        }
                        break;
                }
            }

            // Build hierarchy tree
            if (hie.Nodes.Count > 0)
            {
                BuildTree(hie.Nodes[0], 0, hie.Nodes);
            }

            return hie;
        }

        private static bool IsKnownSectionHeader(string line)
        {
            string l = line.ToLowerInvariant().Trim();
            return l.StartsWith("// number of") ||
                   l.StartsWith("// texture name") ||
                   l.StartsWith("// material name") ||
                   l.StartsWith("// matrix name") ||
                   l.StartsWith("// mesh name") ||
                   l.StartsWith("// node list") ||
                   l.StartsWith("// cull node") ||
                   l.StartsWith("// collision data") ||
                   l.StartsWith("// line name") ||
                   l.StartsWith("// expression") ||
                   l.StartsWith("//version number");
        }

        private static Matrix4x4 ParseMatrix4x4(string r1, string r2, string r3, string r4)
        {
            float[] p1 = ParseRow(r1);
            float[] p2 = ParseRow(r2);
            float[] p3 = ParseRow(r3);
            float[] p4 = ParseRow(r4);

            return new Matrix4x4(
                p1[0], p1[1], p1[2], p1.Length > 3 ? p1[3] : 0,
                p2[0], p2[1], p2[2], p2.Length > 3 ? p2[3] : 0,
                p3[0], p3[1], p3[2], p3.Length > 3 ? p3[3] : 0,
                p4[0], p4[1], p4[2], p4.Length > 3 ? p4[3] : 1
            );
        }

        private static float[] ParseRow(string row)
        {
            string clean = row.Trim().TrimEnd(';');
            string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            float[] result = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                result[i] = float.Parse(parts[i], CultureInfo.InvariantCulture);
            }
            return result;
        }

        private static void BuildTree(TDRNode parent, int index, List<TDRNode> nodes, HashSet<int>? visiting = null)
        {
            visiting ??= new HashSet<int>();
            if (index < 0 || index >= nodes.Count) return;
            if (!visiting.Add(index)) return;

            TDRNode current = nodes[index];

            if (current.Child >= 0 && current.Child < nodes.Count)
            {
                TDRNode childNode = nodes[current.Child];
                childNode.Parent = current;
                current.Children.Add(childNode);
                BuildTree(current, current.Child, nodes, visiting);
            }

            if (current.Sibling >= 0 && current.Sibling < nodes.Count)
            {
                TDRNode siblingNode = nodes[current.Sibling];
                siblingNode.Parent = parent;
                parent.Children.Add(siblingNode);
                BuildTree(parent, current.Sibling, nodes, visiting);
            }

            visiting.Remove(index);
        }
    }
}
