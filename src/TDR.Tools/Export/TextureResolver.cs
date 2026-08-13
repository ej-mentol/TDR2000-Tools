using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TDR.PakLib;

namespace TDR.Tools.Export
{
    public static class TextureResolver
    {
        public static int GetTextureResolutionArea(string filename)
        {
            var match = Regex.Match(filename, @"_(\d+)[x_](\d+)_", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
            {
                return w * h;
            }
            return 0;
        }

        public static bool NameMatch(string fileName, string targetMaterial, bool allowStrippedFallback = false)
        {
            if (!fileName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) &&
                !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return false;

            string cleanPath = fileName.Replace('\\', '/');
            string fileNameOnly = Path.GetFileNameWithoutExtension(cleanPath);

            string cleanMat = targetMaterial.Trim('"').Trim();
            string baseMat = cleanMat.TrimEnd('!');

            // 1. Exact match
            if (fileNameOnly.Equals(cleanMat, StringComparison.OrdinalIgnoreCase) ||
                fileNameOnly.Equals(baseMat, StringComparison.OrdinalIgnoreCase))
                return true;

            // 2. Direct resolution/bitdepth suffix match (e.g. road_main_128x128_32, tree2b!_256x256_32, tree2b_256_256_32)
            string? matchPrefix = fileNameOnly.StartsWith(cleanMat, StringComparison.OrdinalIgnoreCase) ? cleanMat
                                : (fileNameOnly.StartsWith(baseMat, StringComparison.OrdinalIgnoreCase) ? baseMat : null);

            if (matchPrefix != null && fileNameOnly.Length > matchPrefix.Length)
            {
                string sub = fileNameOnly[matchPrefix.Length..].TrimStart('_', '!');
                if (string.IsNullOrEmpty(sub) || Regex.IsMatch(sub, @"^(\d+[x_]\d+|\d+)(_\d+)?$", RegexOptions.IgnoreCase))
                    return true;
            }

            // 3. Optional fallback for stripped suffix (only if explicitly enabled for Tier 5 fallback)
            if (allowStrippedFallback)
            {
                string strippedMat = Regex.Replace(baseMat, @"[a-zA-Z]$", "");
                if (!string.IsNullOrEmpty(strippedMat) && strippedMat.Length > 2)
                {
                    if (fileNameOnly.Equals(strippedMat, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (fileNameOnly.StartsWith(strippedMat, StringComparison.OrdinalIgnoreCase) && fileNameOnly.Length > strippedMat.Length)
                    {
                        string sub = fileNameOnly[strippedMat.Length..].TrimStart('_', '!');
                        if (string.IsNullOrEmpty(sub) || Regex.IsMatch(sub, @"^(\d+[x_]\d+|\d+)(_\d+)?$", RegexOptions.IgnoreCase))
                            return true;
                    }
                }
            }

            // 4. Known exact TDR texture aliases
            if (cleanMat.Equals("span", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("spanner", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_spanner", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("span", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("eng", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("engine", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_engine", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("eng", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("helm", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("helmet", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_helmet", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("helm", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("wad", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("wadocash", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_wadocash", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("wad", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("ped", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("pedsign", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_pedsign", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("ped", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool IsOtherTrackFile(string? archivePath, string? filePath, string mainTrack)
        {
            if (string.IsNullOrEmpty(mainTrack)) return false;

            string normArchive = (archivePath ?? "").Replace('\\', '/').ToLowerInvariant();
            string normFile = (filePath ?? "").Replace('\\', '/').ToLowerInvariant();

            bool isTrackAsset = normArchive.Contains("tracks/") || normFile.Contains("tracks/");
            if (!isTrackAsset) return false;

            string cleanMain = mainTrack.Replace("_", "");
            bool isCurrentTrack = normArchive.Replace("_", "").Contains(cleanMain) || normFile.Replace("_", "").Contains(cleanMain);

            return !isCurrentTrack;
        }

        public record MatchResult(PakManager.IndexedFile File, string TierName);

        public static MatchResult? ResolveBestMatch(PakManager vfs, string materialName, string? archivePath, string? trackContext)
        {
            var vfsFiles = vfs.GetFiles();
            string mainTrack = (trackContext ?? "").ToLowerInvariant();
            string? ctxDir = archivePath != null ? Path.GetDirectoryName(archivePath) : null;

            // 1A. Exact same PAK file as the .hie model
            PakManager.IndexedFile? matchTier1A = (!string.IsNullOrEmpty(archivePath)
                ? vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) &&
                      f.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault()
                : null);
            if (matchTier1A != null) return new MatchResult(matchTier1A, "Tier 1A (Exact PAK File)");

            // 1B. Same PAK directory / folder
            PakManager.IndexedFile? matchTier1B = ctxDir != null
                ? vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) &&
                      Path.GetDirectoryName(f.ArchivePath)
                          ?.Equals(ctxDir, StringComparison.OrdinalIgnoreCase) == true)
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault()
                : null;
            if (matchTier1B != null) return new MatchResult(matchTier1B, "Tier 1B (Same PAK Directory)");

            // 2. Same Track Level
            PakManager.IndexedFile? matchTier2 = !string.IsNullOrEmpty(mainTrack)
                ? vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) &&
                      ((f.ArchivePath ?? "").ToLowerInvariant().Replace("_", "").Contains(mainTrack.Replace("_", "")) ||
                       f.Name.ToLowerInvariant().Replace("_", "").Contains(mainTrack.Replace("_", ""))))
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault()
                : null;
            if (matchTier2 != null) return new MatchResult(matchTier2, "Tier 2 (Same Track Level)");

            // 3. Shared Assets (Non-track assets, e.g. MovableObjects.pak, Powerups.pak)
            PakManager.IndexedFile? matchTier3 = vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) && !(f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant().Contains("tracks/"))
                  .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                  .ThenByDescending(f => f.Name.Contains("_32"))
                  .ThenByDescending(f => f.Name.Contains("_24"))
                  .FirstOrDefault();
            if (matchTier3 != null) return new MatchResult(matchTier3, "Tier 3 (Shared Assets)");

            // 4. Safe VFS Global Match (Excludes other track directories to prevent cross-track asset leaks)
            PakManager.IndexedFile? matchTier4 = vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) && !IsOtherTrackFile(f.ArchivePath, f.Name, mainTrack))
                  .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                  .ThenByDescending(f => f.Name.Contains("_32"))
                  .ThenByDescending(f => f.Name.Contains("_24"))
                  .FirstOrDefault();
            if (matchTier4 != null) return new MatchResult(matchTier4, "Tier 4 (VFS Global Match)");

            // 5. Stripped Suffix Fallback (Excludes other track directories)
            PakManager.IndexedFile? matchTier5 = vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: true) && !IsOtherTrackFile(f.ArchivePath, f.Name, mainTrack))
                  .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                  .ThenByDescending(f => f.Name.Contains("_32"))
                  .ThenByDescending(f => f.Name.Contains("_24"))
                  .FirstOrDefault();
            if (matchTier5 != null) return new MatchResult(matchTier5, "Tier 5 (Stripped Suffix Fallback)");

            return null;
        }
    }
}
