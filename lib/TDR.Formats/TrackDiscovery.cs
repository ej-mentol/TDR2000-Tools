using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TDR.PakLib.Formats
{
    public sealed class TrackInfo
    {
        public string Name { get; set; } = string.Empty;
        public string TrackTxtPath { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> VariantFolders { get; } = new();
    }

    public static class TrackDiscovery
    {
        private static readonly Regex VariantSuffixRegex = new(@"_(Race\d*|Mission\d*|Multiplayer|Race|Mission)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string GetBaseTrackName(string folderOrFileName)
        {
            if (string.IsNullOrWhiteSpace(folderOrFileName)) return string.Empty;
            return VariantSuffixRegex.Replace(folderOrFileName, string.Empty);
        }

        /// <summary>
        /// Stage 1 (Weak Signal): Fast structural path match (cheap, checked during tree building).
        /// Handles both full paths (tracks/hollowood/hollowood.txt) and standalone names (backofbeyond_race3.txt inside .pak).
        /// </summary>
        public static bool IsWeakTrackCandidate(string virtualOrDiskPath)
        {
            if (string.IsNullOrWhiteSpace(virtualOrDiskPath)) return false;
            string norm = virtualOrDiskPath.Replace('\\', '/').ToLowerInvariant();

            if (!norm.EndsWith(".txt")) return false;
            if (norm.EndsWith("movabledescriptor.txt") || norm.EndsWith("moveabledescriptor.txt")) return false;

            string fileName = Path.GetFileName(norm);
            if (fileName.Contains("race") || fileName.Contains("mission") || fileName.Contains("multiplayer") || fileName.EndsWith("descriptor.txt") || norm.Contains("tracks/") || !norm.Contains('/'))
                return true;

            return false;
        }

        /// <summary>
        /// Stage 2 (Strong Signal): In-depth content validation (checks for authentic track keywords and handles UTF-8 BOM).
        /// </summary>
        public static bool IsStrongTrackContent(byte[]? fileBytes)
        {
            if (fileBytes == null || fileBytes.Length == 0) return false;

            try
            {
                string text = System.Text.Encoding.UTF8.GetString(fileBytes).TrimStart('\uFEFF');
                string[] keywords = new[]
                {
                    "STATIC_MESH_DESCRIPTOR", "SKY_SPHERE", "WATER_MESH", "MOVABLE_OBJECTS",
                    "BASE_CONSOFT", "LEVEL_MESH", "STATIC_MESH", "BREAKABLES_DESCRIPTOR", "ANIMATED_PROPS"
                };

                foreach (string line in text.Split('\n'))
                {
                    string trimmed = line.Trim().TrimStart('\uFEFF');
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//")) continue;

                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 0) continue;

                    string firstToken = parts[0].ToUpperInvariant();
                    if (keywords.Contains(firstToken))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Fallback
            }

            return false;
        }

        /// <summary>
        /// Full Two-Stage Validation: Weak structural signal + Strong content confirmation.
        /// </summary>
        public static bool ValidateTrack(string virtualOrDiskPath, byte[]? fileBytes)
        {
            if (!IsWeakTrackCandidate(virtualOrDiskPath)) return false;
            return IsStrongTrackContent(fileBytes);
        }

        public static bool ValidateAssetsPath(string assetsPath, out string statusMessage)
        {
            if (string.IsNullOrWhiteSpace(assetsPath) || !Directory.Exists(assetsPath))
            {
                statusMessage = $"[!] Directory not found: '{assetsPath}'";
                return false;
            }

            bool hasPaks = Directory.GetFiles(assetsPath, "*.pak", SearchOption.AllDirectories).Length > 0;
            bool hasTracksDir = Directory.Exists(Path.Combine(assetsPath, "tracks")) || Directory.Exists(Path.Combine(assetsPath, "TRACKS"));

            if (!hasPaks && !hasTracksDir)
            {
                statusMessage = $"[!] Invalid TDR2000 assets directory in '{assetsPath}'. No .PAK archives detected.";
                return false;
            }

            statusMessage = $"[+] Valid TDR2000 PAK context verified: '{Path.GetFullPath(assetsPath)}'";
            return true;
        }

        public static List<string> ParseRacesTxt(byte[] racesBytes)
        {
            if (racesBytes == null || racesBytes.Length == 0) return new List<string>();
            var racesFile = RacesFile.Parse(racesBytes);
            return racesFile.Races.Select(r => r.Track).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<TrackInfo> DiscoverTracks(PakManager vfs, string rootPath)
        {
            var tracksMap = new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);

            // 0. Parse official races.txt from VFS if available (e.g., CARMA.pak/races.txt)
            byte[]? racesData = vfs.LoadFile("races.txt");
            if (racesData != null)
            {
                var officialTracks = ParseRacesTxt(racesData);
                foreach (var track in officialTracks)
                {
                    string baseName = GetBaseTrackName(track);
                    if (!tracksMap.TryGetValue(baseName, out var info))
                    {
                        info = new TrackInfo
                        {
                            Name = baseName,
                            TrackTxtPath = $"tracks/{baseName.ToLower()}/{baseName.ToLower()}.txt",
                            Description = $"Official track ({track})"
                        };
                        tracksMap[baseName] = info;
                    }

                    if (!track.Equals(baseName, StringComparison.OrdinalIgnoreCase) && !info.VariantFolders.Contains(track, StringComparer.OrdinalIgnoreCase))
                    {
                        info.VariantFolders.Add(track);
                    }
                }
            }

            // 1. Discover tracks from VFS index
            foreach (var file in vfs.GetFiles())
            {
                string norm = file.Name.Replace('\\', '/').ToLower();
                if (norm.StartsWith("tracks/") && norm.EndsWith(".txt"))
                {
                    string[] parts = norm.Split('/');
                    if (parts.Length == 3)
                    {
                        string folderName = parts[1];
                        string fileNameNoExt = Path.GetFileNameWithoutExtension(parts[2]);
                        string baseTrackName = GetBaseTrackName(folderName);
                        if (string.IsNullOrEmpty(baseTrackName) || baseTrackName.Equals(folderName, StringComparison.OrdinalIgnoreCase))
                        {
                            baseTrackName = GetBaseTrackName(fileNameNoExt);
                        }

                        if (!tracksMap.TryGetValue(baseTrackName, out var info))
                        {
                            info = new TrackInfo
                            {
                                Name = baseTrackName,
                                TrackTxtPath = file.Name,
                                Description = $"Level descriptor ({file.Name})"
                            };
                            tracksMap[baseTrackName] = info;
                        }

                        if (!folderName.Equals(baseTrackName, StringComparison.OrdinalIgnoreCase) && !info.VariantFolders.Contains(folderName, StringComparer.OrdinalIgnoreCase))
                        {
                            info.VariantFolders.Add(folderName);
                        }

                        if (!fileNameNoExt.Equals(baseTrackName, StringComparison.OrdinalIgnoreCase) && !info.VariantFolders.Contains(fileNameNoExt, StringComparer.OrdinalIgnoreCase))
                        {
                            info.VariantFolders.Add(fileNameNoExt);
                        }
                    }
                }
            }

            return tracksMap.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> DiscoverRawVariants(string baseTrackName, PakManager vfs, string rootPath)
        {
            var rawVariants = new List<string>();
            if (string.IsNullOrWhiteSpace(baseTrackName)) return rawVariants;

            string cleanBase = GetBaseTrackName(baseTrackName);
            rawVariants.Add(cleanBase);

            string lowerBase = cleanBase.ToLowerInvariant();

            static bool IsMetadataFile(string name)
            {
                string l = name.ToLowerInvariant();
                return l.Contains("script") ||
                       l.Contains("collision") ||
                       l.Contains("moveable") ||
                       l.Contains("strings") ||
                       l.Contains("raceinfo") ||
                       l.Contains("sfxlist") ||
                       l.Contains("palette") ||
                       l.Contains("follower") ||
                       l.Contains("volume") ||
                       l.Contains("placement") ||
                       l.Contains("path") ||
                       l.Contains("occluder") ||
                       l.Contains("background") ||
                       l.Contains("dingable");
            }

            // 1. Discover variants from official races.txt if present
            var allTracks = DiscoverTracks(vfs, rootPath);
            var targetTrack = allTracks.FirstOrDefault(t => t.Name.Equals(lowerBase, StringComparison.OrdinalIgnoreCase));
            if (targetTrack != null)
            {
                foreach (var variantFolder in targetTrack.VariantFolders)
                {
                    string vName = variantFolder;
                    if (vName.StartsWith(cleanBase + "_", StringComparison.OrdinalIgnoreCase))
                        vName = vName[(cleanBase.Length + 1)..];
                    else if (vName.StartsWith(cleanBase, StringComparison.OrdinalIgnoreCase))
                        vName = vName[cleanBase.Length..];

                    if (!string.IsNullOrWhiteSpace(vName) &&
                        !rawVariants.Contains(vName, StringComparer.OrdinalIgnoreCase) &&
                        !IsMetadataFile(variantFolder) &&
                        !IsMetadataFile(vName))
                    {
                        rawVariants.Add(vName);
                    }
                }
            }



            // 2. Discover variants from VFS indexed files matching baseTrackName prefix
            foreach (var file in vfs.GetFiles())
            {
                string fileNameNoExt = Path.GetFileNameWithoutExtension(file.Name);
                string lowerFile = fileNameNoExt.ToLowerInvariant();

                if (lowerFile.StartsWith(lowerBase + "_") || lowerFile.StartsWith(lowerBase + " "))
                {
                    if (file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                        file.Name.EndsWith(".pup", StringComparison.OrdinalIgnoreCase) ||
                        file.Name.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!rawVariants.Contains(fileNameNoExt, StringComparer.OrdinalIgnoreCase) && !IsMetadataFile(lowerFile))
                        {
                            rawVariants.Add(fileNameNoExt);
                        }
                    }
                }
            }

            // 3. Discover variants from physical disk files if rootPath is set
            if (!string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
            {
                try
                {
                    string searchPattern = $"{cleanBase}_*";
                    var diskFiles = Directory.GetFiles(rootPath, searchPattern, SearchOption.AllDirectories);
                    foreach (string diskFile in diskFiles)
                    {
                        string fnNoExt = Path.GetFileNameWithoutExtension(diskFile);
                        string lowerFn = fnNoExt.ToLowerInvariant();
                        if (!rawVariants.Contains(fnNoExt, StringComparer.OrdinalIgnoreCase) && !IsMetadataFile(lowerFn))
                        {
                            rawVariants.Add(fnNoExt);
                        }
                    }
                }
                catch { }
            }

            return rawVariants;
        }
    }
}
