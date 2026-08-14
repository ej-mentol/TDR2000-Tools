using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Services
{
    public sealed class TrackInfo
    {
        public string Name { get; set; } = string.Empty;
        public string TrackTxtPath { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> VariantFolders { get; } = new();
    }

    public static class TrackDiscoveryService
    {
        public static bool IsGameAssetsDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;

            bool hasCarmaPak = File.Exists(Path.Combine(path, "CARMA.pak")) ||
                               File.Exists(Path.Combine(path, "carma.pak")) ||
                               File.Exists(Path.Combine(path, "Assets", "CARMA.pak")) ||
                               File.Exists(Path.Combine(path, "assets", "carma.pak"));

            bool hasRacesTxt = File.Exists(Path.Combine(path, "races.txt")) ||
                               File.Exists(Path.Combine(path, "RACES.TXT")) ||
                               File.Exists(Path.Combine(path, "Assets", "races.txt")) ||
                               File.Exists(Path.Combine(path, "assets", "races.txt"));

            return hasCarmaPak || hasRacesTxt;
        }

        public static string ResolveAssetsRootPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) inputPath = Directory.GetCurrentDirectory();

            string fullPath = Path.GetFullPath(inputPath);
            if (File.Exists(fullPath))
                fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;

            // 1. If fullPath has an 'Assets' subfolder containing CARMA.pak/races.txt, return that Assets subfolder
            string subAssets = Path.Combine(fullPath, "Assets");
            if (Directory.Exists(subAssets) && IsGameAssetsDirectory(subAssets))
            {
                return subAssets;
            }

            // 2. If fullPath itself is the confirmed game assets directory containing CARMA.pak/races.txt
            if (IsGameAssetsDirectory(fullPath))
            {
                return fullPath;
            }

            var dirInfo = new DirectoryInfo(fullPath);
            if (dirInfo.Name.Equals("tracks", StringComparison.OrdinalIgnoreCase))
            {
                return dirInfo.Parent?.FullName ?? dirInfo.FullName;
            }

            return fullPath;
        }

        public static List<TrackInfo> DiscoverTracks(PakManager vfs, string rootPath)
        {
            var tracksMap = new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);

            // 0. Parse official races.txt from VFS if available (e.g., CARMA.pak/races.txt)
            byte[]? racesData = vfs.LoadFile("races.txt");
            if (racesData != null)
            {
                var officialTracks = TrackDiscovery.ParseRacesTxt(racesData);
                foreach (var track in officialTracks)
                {
                    string baseName = TrackDiscovery.GetBaseTrackName(track);
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
                        string baseTrackName = TrackDiscovery.GetBaseTrackName(folderName);
                        if (string.IsNullOrEmpty(baseTrackName) || baseTrackName.Equals(folderName, StringComparison.OrdinalIgnoreCase))
                        {
                            baseTrackName = TrackDiscovery.GetBaseTrackName(fileNameNoExt);
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

            string cleanBase = TrackDiscovery.GetBaseTrackName(baseTrackName);
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

        public static List<string> DiscoverVariants(string baseTrackName, PakManager vfs, string rootPath)
        {
            string cleanBase = TrackDiscovery.GetBaseTrackName(baseTrackName);
            var rawVariants = DiscoverRawVariants(cleanBase, vfs, rootPath);

            bool hasRaces = rawVariants.Any(v => v.Contains("race", StringComparison.OrdinalIgnoreCase));
            bool hasMissions = rawVariants.Any(v => v.Contains("mission", StringComparison.OrdinalIgnoreCase));
            bool hasAnyVariants = rawVariants.Count > 0;

            var variants = new List<string>();

            // Only add aggregate options if child variants (Race/Mission) actually exist for this track
            if (hasAnyVariants)
            {
                variants.Add("All supported resources");
            }

            variants.Add($"Base Track Only ({cleanBase})");

            if (hasRaces)
            {
                variants.Add("All Races (Race 1, Race 2...)");
            }
            if (hasMissions)
            {
                variants.Add("All Missions (Mission 1, Mission 2...)");
            }

            static bool IsValidVariant(string name)
            {
                string l = name.ToLowerInvariant().Trim();
                if (string.IsNullOrWhiteSpace(l)) return false;
                return !l.Contains("script") && !l.Contains("collision") &&
                       !l.Contains("moveable") && !l.Contains("strings") && !l.Contains("raceinfo") &&
                       !l.Contains("sfxlist") && !l.Contains("palette") && !l.Contains("follower") &&
                       !l.Contains("volume") && !l.Contains("placement") && !l.Contains("path") &&
                       !l.Contains("occluder") && !l.Contains("background") && !l.Contains("dingable");
            }

            foreach (var variant in rawVariants)
            {
                if (IsValidVariant(variant) && !variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                {
                    variants.Add(variant);
                }
            }

            return variants;
        }
    }
}
