using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using TDR.PakLib;

namespace TDR.Tools.Export
{
    /// <summary>
    /// Canonical texture resolution and export service.
    /// Unifies Tier 0 (TextureResolver.ResolveBestMatch) with the full OBJ fallback candidates list
    /// and MD5-safe PNG/TGA writing to prevent texture collisions.
    /// </summary>
    public sealed class TextureResolutionService
    {
        private readonly PakManager _vfs;
        private readonly string _exportDir;
        private readonly string? _trackContext;
        private readonly bool _convertTexturesToPng;

        public static readonly string[] CandidateSuffixes = new[]
        {
            "_512x512_32.tga", "_256x256_32.tga", "_256_256_32.tga", "_128x128_32.tga", "_128_128_32.tga",
            "_64x64_32.tga", "_32x32_32.tga", "_16x16_32.tga", "_8x8_32.tga", "_4x4_32.tga", "_2x2_32.tga", "_1x1_32.tga",
            "_512x512_8.tga", "_256x256_8.tga", "_256_256_8.tga", "_128x128_8.tga", "_128_128_8.tga",
            "_64x64_8.tga", "_32x32_8.tga", "_16x16_8.tga", "_8x8_8.tga", "_4x4_8.tga", "_2x2_8.tga", "_1x1_8.tga"
        };

        public TextureResolutionService(PakManager vfs, string exportDir, string? trackContext = null, bool convertTexturesToPng = true)
        {
            _vfs = vfs;
            _exportDir = exportDir;
            _trackContext = trackContext;
            _convertTexturesToPng = convertTexturesToPng;
        }

        public string? ResolveAndSave(string textureName, string? archivePath, string? targetFolder = null)
        {
            if (string.IsNullOrWhiteSpace(textureName)) return null;

            string t = textureName.Trim('"');
            string outFolder = targetFolder ?? _exportDir;

            // Tier 0: Best match via unified TextureResolver
            var matchResult = TextureResolver.ResolveBestMatch(_vfs, t, archivePath, _trackContext);
            if (matchResult?.File != null)
            {
                byte[]? matchData = (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(matchResult.File.Name, archivePath) : null)
                                    ?? _vfs.LoadFile(matchResult.File);
                if (matchData != null && matchData.Length > 0)
                {
                    return SaveTextureWithFormat(matchData, Path.GetFileName(matchResult.File.Name), outFolder);
                }
            }

            // Tier 1: Canonical OBJ fallback candidate search
            string cleanT = t.TrimEnd('!');
            string bangT = cleanT + "!";

            // Direct .tga check
            string directTga = $"{t}.tga";
            byte[]? directData = _vfs.LoadFileContext(directTga, "POWERUPS") ?? _vfs.LoadFile(directTga);
            if (directData != null && directData.Length > 0)
            {
                return SaveTextureWithFormat(directData, directTga, outFolder);
            }

            // Suffix permutations
            foreach (string suffix in CandidateSuffixes)
            {
                string candBang = bangT + suffix;
                byte[]? data = _vfs.LoadFileContext(candBang, "POWERUPS") ?? _vfs.LoadFile(candBang);
                if (data != null && data.Length > 0)
                {
                    return SaveTextureWithFormat(data, Path.GetFileName(candBang), outFolder);
                }

                string candClean = cleanT + suffix;
                data = _vfs.LoadFileContext(candClean, "POWERUPS") ?? _vfs.LoadFile(candClean);
                if (data != null && data.Length > 0)
                {
                    return SaveTextureWithFormat(data, Path.GetFileName(candClean), outFolder);
                }
            }

            return null;
        }

        public string SaveTextureWithFormat(byte[] rawData, string originalFileName, string targetDir)
        {
            string baseStem = Path.GetFileNameWithoutExtension(originalFileName);
            string ext = Path.GetExtension(originalFileName);
            string targetBaseName = baseStem;

            string testPath = Path.Combine(targetDir, originalFileName);
            if (File.Exists(testPath))
            {
                byte[] existingBytes = File.ReadAllBytes(testPath);
                if (existingBytes.Length != rawData.Length || !existingBytes.AsSpan().SequenceEqual(rawData))
                {
                    string hash = GetMd5(rawData).Substring(0, 6).ToLowerInvariant();
                    targetBaseName = $"{baseStem}_{hash}";
                }
            }

            if (_convertTexturesToPng && ext.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                string pngName = $"{targetBaseName}.png";
                string pngPath = Path.Combine(targetDir, pngName);
                if (TgaDecoder.SaveTgaAsPng(rawData, pngPath))
                {
                    string staleTga = Path.Combine(targetDir, $"{targetBaseName}.tga");
                    if (File.Exists(staleTga))
                    {
                        try { File.Delete(staleTga); } catch { }
                    }
                    return pngName;
                }
            }

            string rawName = $"{targetBaseName}{ext}";
            string rawPath = Path.Combine(targetDir, rawName);
            if (!File.Exists(rawPath)) File.WriteAllBytes(rawPath, rawData);
            return rawName;
        }

        private static string GetMd5(byte[] data)
        {
            byte[] hash = MD5.HashData(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
