using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TDR.PakLib;
using TDR.Tools.Services;

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
        private readonly Action<string, LogLevel>? _logAction;

        public static readonly string[] CandidateSuffixes = new[]
        {
            "_512x512_32.tga", "_256x256_32.tga", "_256_256_32.tga", "_128x128_32.tga", "_128_128_32.tga",
            "_64x64_32.tga", "_32x32_32.tga", "_16x16_32.tga", "_8x8_32.tga", "_4x4_32.tga", "_2x2_32.tga", "_1x1_32.tga",
            "_512x512_8.tga", "_256x256_8.tga", "_256_256_8.tga", "_128x128_8.tga", "_128_128_8.tga",
            "_64x64_8.tga", "_32x32_8.tga", "_16x16_8.tga", "_8x8_8.tga", "_4x4_8.tga", "_2x2_8.tga", "_1x1_8.tga"
        };

        public TextureResolutionService(PakManager vfs, string exportDir, string? trackContext = null, bool convertTexturesToPng = true, Action<string, LogLevel>? logAction = null)
        {
            _vfs = vfs;
            _exportDir = exportDir;
            _trackContext = trackContext;
            _convertTexturesToPng = convertTexturesToPng;
            _logAction = logAction;
        }

        private void Log(string message, LogLevel level = LogLevel.Info) => _logAction?.Invoke(message, level);

        private byte[]? SafeLoadCandidate(string candidateName, string? archivePath)
        {
            // 1. Exact archive / context folder
            if (!string.IsNullOrEmpty(archivePath))
            {
                byte[]? data = _vfs.LoadFileContext(candidateName, archivePath);
                if (data != null && data.Length > 0) return data;
            }

            // 2. Current track context
            if (!string.IsNullOrEmpty(_trackContext))
            {
                byte[]? data = _vfs.LoadFileContext(candidateName, _trackContext);
                if (data != null && data.Length > 0) return data;
            }

            // 3. Shared powerups / global non-track contexts
            byte[]? sharedData = _vfs.LoadFileContext(candidateName, "POWERUPS") ??
                                 _vfs.LoadFileContext(candidateName, "MovableObjects") ??
                                 _vfs.LoadFileContext(candidateName, "shared");
            if (sharedData != null && sharedData.Length > 0) return sharedData;

            // 4. Global match ONLY IF it does not belong to another foreign track (O(1) dictionary lookup)
            var candidates = _vfs.GetCandidatesByFileName(candidateName);
            var indexed = candidates.FirstOrDefault(f => !TextureResolver.IsOtherTrackFile(f.ArchivePath, f.Name, _trackContext ?? ""));
            if (indexed != null)
            {
                return _vfs.LoadFile(indexed);
            }

            return null;
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
                byte[]? matchData = _vfs.LoadFile(matchResult.File);
                if (matchData != null && matchData.Length > 0)
                {
                    return SaveTextureWithFormat(matchData, Path.GetFileName(matchResult.File.Name), outFolder);
                }
                else
                {
                    Log($"    [ERROR] Failed to read raw bytes for matched texture file '{matchResult.File.Name}' from '{matchResult.File.ArchivePath}'", LogLevel.Error);
                }
            }

            // Tier 1: Canonical OBJ fallback candidate search with strict track isolation
            string cleanT = t.TrimEnd('!');
            string bangT = cleanT + "!";

            // Direct .tga check
            string directTga = $"{t}.tga";
            byte[]? directData = SafeLoadCandidate(directTga, archivePath);
            if (directData != null && directData.Length > 0)
            {
                return SaveTextureWithFormat(directData, directTga, outFolder);
            }

            // Suffix permutations
            foreach (string suffix in CandidateSuffixes)
            {
                string candBang = bangT + suffix;
                byte[]? data = SafeLoadCandidate(candBang, archivePath);
                if (data != null && data.Length > 0)
                {
                    return SaveTextureWithFormat(data, Path.GetFileName(candBang), outFolder);
                }

                string candClean = cleanT + suffix;
                data = SafeLoadCandidate(candClean, archivePath);
                if (data != null && data.Length > 0)
                {
                    return SaveTextureWithFormat(data, Path.GetFileName(candClean), outFolder);
                }
            }

            Log($"    [WARNING] Texture '{t}' could not be resolved in VFS for track context '{_trackContext ?? "global"}'.", LogLevel.Warning);
            return null;
        }

        public string SaveTextureWithFormat(byte[] rawData, string originalFileName, string targetDir)
        {
            if (rawData == null || rawData.Length == 0)
            {
                Log($"    [ERROR] Texture '{originalFileName}' has 0 bytes of data.", LogLevel.Error);
                return originalFileName;
            }

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
                    Log($"    [WARNING] Texture collision for '{originalFileName}': existing file differs from incoming asset. Appended hash suffix -> '{targetBaseName}'", LogLevel.Warning);
                }
            }

            if (_convertTexturesToPng && ext.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                if (rawData.Length < 18)
                {
                    Log($"    [ERROR] Corrupted TGA header for '{originalFileName}': size {rawData.Length} bytes is below 18-byte minimum.", LogLevel.Error);
                }
                else
                {
                    ushort w = BitConverter.ToUInt16(rawData, 12);
                    ushort h = BitConverter.ToUInt16(rawData, 14);
                    byte bpp = rawData[16];

                    if (w == 0 || h == 0 || w > 8192 || h > 8192)
                    {
                        Log($"    [ERROR] Invalid TGA dimensions for '{originalFileName}': {w}x{h}, {bpp}bpp. Cannot convert to PNG.", LogLevel.Error);
                    }
                    else
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
                        else
                        {
                            Log($"    [ERROR] TgaDecoder failed to encode PNG for '{originalFileName}' ({w}x{h}, {bpp}bpp). Saving raw TGA.", LogLevel.Error);
                        }
                    }
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
