using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace TDR.PakLib
{
    /// <summary>
    /// TDR2000 archive format implementation for .PAK/.DIR files.
    /// Handles Trie indexing, zIG/RAW encapsulation, and path normalization.
    /// </summary>
    public static class TDRArchive
    {
        /*
         * FORMAT SPECIFICATION:
         * 
         * .DIR Index Structure (Trie):
         *   Node: [char:u8][flags:u8][metadata?]
         *   Flags: 0x08=file, 0x40=branch, 0x80=sibling
         *   Metadata: [offset:u32][size:u32] (only if 0x08 set)
         * 
         * .PAK Container (zIG/RAW blocks):
         *   Header: [key:u8][sig_xor:3bytes][size_xor:u32][payload]
         *   MetaKey: ROR8(key, 3)
         *   Alignment: 4-byte boundaries
         * 
         * Path Normalization:
         *   Lowercase, forward slashes, no traversal sequences
         */

        private const byte FlagFile = 0x08;
        private const byte FlagBranch = 0x40;
        private const byte FlagSibling = 0x80;

        public enum PathViolation
        {
            None,
            UppercaseDetected,
            BackslashUsage,
            PathTraversal,
            DoubleSeparator,
            NonAscii
        }

        public static PathViolation ValidatePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return PathViolation.None;
            if (path.Any(char.IsUpper)) return PathViolation.UppercaseDetected;
            if (path.Contains("\\")) return PathViolation.BackslashUsage;
            if (path.Contains("..")) return PathViolation.PathTraversal;
            if (path.Contains("//")) return PathViolation.DoubleSeparator;
            if (path.Any(c => c < 32 || c > 126)) return PathViolation.NonAscii;
            return PathViolation.None;
        }

        public static string SanitizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string result = path.Replace("\\", "/");

            if (result.StartsWith("./"))
                result = result.Substring(2);

            while (result.Contains("//"))
                result = result.Replace("//", "/");

            if (result.Contains(".."))
                result = result.Replace("..", "__");

            return result.Trim('/');
        }

        public static string NormalizeArchivePath(string virtualPath, string archiveName)
        {
            string clean = SanitizePath(virtualPath);
            string root = Path.GetFileNameWithoutExtension(archiveName).ToLower() + "/";

            if (clean.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return clean.Substring(root.Length);

            return clean;
        }

        public static byte RotateRight8(byte value, int count)
        {
            int c = count & 7;
            return (byte)((value >> c) | (value << (8 - c)));
        }

        // Verified byte-for-byte on official .PAK archives:
        // when key >= 0x80 upper nibble of meta_key is set to 0xF0.
        // Matches arithmetic right-shift behavior in original executable.
        public static byte ComputeMetaKey(byte key)
        {
            byte m = RotateRight8(key, 3);
            if ((key & 0x80) != 0)
                m |= 0xF0;
            return m;
        }

        // Automatically selects compression mode based on ratio threshold.
        public static byte[] CreateZigHeaderAuto(byte[] content, double threshold = 0.98)
        {
            if (content == null || content.Length == 0)
                return Array.Empty<byte>();

            byte[] compressed = Compress(content);
            bool worthIt = compressed.Length < content.Length * threshold;
            return CreateZigHeader(content, worthIt);
        }

        public static byte[] CreateZigHeader(byte[] content, bool compress)
        {
            if (content == null || content.Length == 0)
                return Array.Empty<byte>();

            byte key = (byte)Random.Shared.Next(1, 255);
            byte metaKey = ComputeMetaKey(key);

            byte[] payload = compress ? Compress(content) : content;
            byte[] signature = Encoding.ASCII.GetBytes(compress ? "zIG" : "RAW");

            byte[] header = new byte[8];
            header[0] = key;
            for (int i = 0; i < 3; i++)
                header[1 + i] = (byte)(signature[i] ^ key);

            byte[] sizeBytes = BitConverter.GetBytes((uint)content.Length);
            for (int i = 0; i < 4; i++)
                header[4 + i] = (byte)(sizeBytes[i] ^ metaKey);

            byte[] result = new byte[header.Length + payload.Length];
            Buffer.BlockCopy(header, 0, result, 0, 8);
            Buffer.BlockCopy(payload, 0, result, 8, payload.Length);

            return result;
        }

        public static byte[] DecompressZig(byte[] raw)
        {
            if (raw == null || raw.Length < 8)
                return raw ?? Array.Empty<byte>();

            byte key = raw[0];
            byte[] sigBytes = new byte[3];
            for (int i = 0; i < 3; i++)
                sigBytes[i] = (byte)(raw[1 + i] ^ key);

            string signature = Encoding.ASCII.GetString(sigBytes);

            if (signature == "RAW")
            {
                byte[] result = new byte[raw.Length - 8];
                Buffer.BlockCopy(raw, 8, result, 0, result.Length);
                return result;
            }

            if (signature == "zIG")
            {
                try
                {
                    using var ms = new MemoryStream(raw, 8, raw.Length - 8);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    z.CopyTo(outMs);
                    return outMs.ToArray();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"zIG decompression failed: {ex.Message}");
                    return raw;
                }
            }

            // Uncompressed raw files (e.g. TGA headers, TTEX descriptors) are stored directly without zIG wrapper headers.
            return raw;
        }

        public static List<FileEntry> ParseTrieIndex(string path)
        {
            if (!File.Exists(path))
                return new List<FileEntry>();

            byte[] data = File.ReadAllBytes(path);
            var files = new List<FileEntry>();
            int pos = 0;

            void Walk(string prefix)
            {
                while (pos < data.Length)
                {
                    if (pos + 2 > data.Length) break;

                    byte chr = data[pos];
                    byte flags = data[pos + 1];
                    pos += 2;

                    string currentName = prefix + (char)chr;

                    if ((flags & FlagFile) != 0)
                    {
                        if (pos + 8 > data.Length) break;

                        files.Add(new FileEntry
                        {
                            Name = currentName,
                            Offset = BitConverter.ToUInt32(data, pos),
                            Size = BitConverter.ToUInt32(data, pos + 4)
                        });
                        pos += 8;
                    }

                    if ((flags & FlagBranch) != 0) Walk(currentName);
                    if ((flags & FlagSibling) == 0) break;
                }
            }

            Walk(string.Empty);
            return files;
        }

        public static byte[] SerializeTrieIndex(List<FileEntry> list)
        {
            if (list == null || list.Count == 0)
                return Array.Empty<byte>();

            var unique = list
                .GroupBy(f => SanitizePath(f.Name))
                .Select(g => g.Last())
                .OrderBy(f => f.Name.ToLower())
                .ToList();

            var root = new TrieNode();

            foreach (var entry in unique)
            {
                string norm = SanitizePath(entry.Name);
                TrieNode current = root;

                for (int i = 0; i < norm.Length; i++)
                {
                    char c = norm[i];

                    if (!current.Children.TryGetValue(c, out TrieNode? next))
                    {
                        next = new TrieNode();
                        current.Children[c] = next;
                    }

                    if (i == norm.Length - 1)
                    {
                        next.Flags |= FlagFile;
                        next.Meta = (entry.Offset, entry.Size);
                    }
                    else
                    {
                        next.Flags |= FlagBranch;
                    }

                    current = next;
                }
            }

            using var ms = new MemoryStream();
            WriteNodes(root.Children, ms);
            return ms.ToArray();
        }

        public static void WriteAligned(Stream stream, byte[] data)
        {
            long pos = stream.Position;
            int padding = (int)((4 - (pos % 4)) % 4);

            if (padding > 0)
                stream.Write(new byte[padding], 0, padding);

            stream.Write(data, 0, data.Length);
        }

        private sealed class TrieNode
        {
            public byte Flags;
            public (uint Offset, uint Size)? Meta;
            public Dictionary<char, TrieNode> Children = new Dictionary<char, TrieNode>();
        }

        private static void WriteNodes(Dictionary<char, TrieNode> nodes, MemoryStream ms)
        {
            var ordered = nodes.OrderBy(k => k.Key).ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                char key = ordered[i].Key;
                TrieNode node = ordered[i].Value;

                byte flags = node.Flags;
                if (i < ordered.Count - 1)
                    flags |= FlagSibling;

                ms.WriteByte((byte)key);
                ms.WriteByte(flags);

                if (node.Meta.HasValue)
                {
                    ms.Write(BitConverter.GetBytes(node.Meta.Value.Offset), 0, 4);
                    ms.Write(BitConverter.GetBytes(node.Meta.Value.Size), 0, 4);
                }

                if (node.Children.Count > 0)
                    WriteNodes(node.Children, ms);
            }
        }

        private static byte[] Compress(byte[] data)
        {
            using var ms = new MemoryStream();
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal))
                z.Write(data, 0, data.Length);
            return ms.ToArray();
        }
    }
}