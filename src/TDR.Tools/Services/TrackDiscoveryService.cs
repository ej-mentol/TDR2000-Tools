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
        /// <summary>
        /// Verified track name aliases mapping base level names to internal asset prefixes used by Torus Games.
        /// - hollowood: "FilmStudioTraffic_Paths_1.pak", "filmstudio", "film_studio"
        /// - backofbeyond: "outback"
        /// - docksmd: "docks", "New_DOCKSDrone_Paths.pak"
        /// - militarymd: "military", "MilitaryDrone_Paths.pak"
        /// - policestate: "police", "New_PoliceDrone_Path.pak"
        /// </summary>
        public static readonly Dictionary<string, string[]> TrackAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hollowood"] = new[] { "filmstudio", "film_studio" },
            ["backofbeyond"] = new[] { "outback" },
            ["docksmd"] = new[] { "docks" },
            ["militarymd"] = new[] { "military" },
            ["policestate"] = new[] { "police" }
        };

        public static bool IsTrackOrAliasMatch(string pathOrName, string mainTrack)
        {
            if (string.IsNullOrEmpty(pathOrName) || string.IsNullOrEmpty(mainTrack)) return false;
            string norm = pathOrName.Replace('\\', '/').ToLowerInvariant().Replace("_", "");
            string cleanMain = mainTrack.ToLowerInvariant().Replace("_", "");

            if (norm.Contains(cleanMain)) return true;

            if (TrackAliases.TryGetValue(mainTrack.ToLowerInvariant(), out var aliases))
            {
                foreach (var alias in aliases)
                {
                    if (norm.Contains(alias.Replace("_", ""))) return true;
                }
            }

            return false;
        }

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

        public static void IndexWithSharedFolders(PakManager vfs, string rootPath, Action<string>? log = null)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return;

            vfs.IndexDirectory(rootPath);

            try
            {
                string? parentDir = Path.GetDirectoryName(rootPath);
                if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
                {
                    string[] sharedFolders = new[] { "MOVABLEOBJECTS", "POWERUPS", "SHARED", "TEXTURES", "ATTRIBUTES" };
                    var indexedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    string? searchDir = parentDir;
                    for (int depth = 0; depth < 2 && !string.IsNullOrEmpty(searchDir) && Directory.Exists(searchDir); depth++)
                    {
                        foreach (string folder in sharedFolders)
                        {
                            string folderDir = Path.Combine(searchDir, folder);
                            if (Directory.Exists(folderDir) &&
                                !folderDir.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase) &&
                                indexedDirs.Add(Path.GetFullPath(folderDir)))
                            {
                                vfs.IndexDirectory(folderDir);
                            }
                        }
                        searchDir = Path.GetDirectoryName(searchDir);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[!] Warning during parent shared assets auto-indexing: {ex.Message}");
            }
        }

        public static List<TrackInfo> DiscoverTracks(PakManager vfs, string rootPath)
        {
            var tracksMap = new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);

            // 0. Parse official races.txt from VFS or root directory if available
            byte[]? racesData = vfs.LoadFile("races.txt") ??
                (!string.IsNullOrEmpty(rootPath) && File.Exists(Path.Combine(rootPath, "races.txt"))
                    ? File.ReadAllBytes(Path.Combine(rootPath, "races.txt"))
                    : null);
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

            string discoveryMode = AppSettings.Load().TrackDiscoveryMode;
            bool allowHeuristic = !discoveryMode.Equals("RacesOnly", StringComparison.OrdinalIgnoreCase);

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
                            // In strict "RacesOnly" mode, do not register unlisted tracks
                            if (!allowHeuristic) continue;

                            info = new TrackInfo
                            {
                                Name = baseTrackName,
                                TrackTxtPath = file.Name,
                                Description = $"Level descriptor ({file.Name})"
                            };
                            tracksMap[baseTrackName] = info;
                        }
                        else
                        {
                            // If previous path was a guessed default from races.txt, update with the real confirmed VFS file
                            if (string.IsNullOrEmpty(info.TrackTxtPath) ||
                                !vfs.FileExists(info.TrackTxtPath) ||
                                fileNameNoExt.Equals(baseTrackName, StringComparison.OrdinalIgnoreCase))
                            {
                                info.TrackTxtPath = file.Name;
                            }
                        }

                        if (allowHeuristic)
                        {
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

            // 1. Discover variants from official races.txt if present (targeted, without full VFS rescan)
            byte[]? racesData = vfs.LoadFile("races.txt");
            if (racesData != null)
            {
                var officialTracks = TrackDiscovery.ParseRacesTxt(racesData);
                foreach (var track in officialTracks)
                {
                    if (TrackDiscovery.GetBaseTrackName(track).Equals(lowerBase, StringComparison.OrdinalIgnoreCase))
                    {
                        string vName = track;
                        if (vName.StartsWith(cleanBase + "_", StringComparison.OrdinalIgnoreCase))
                            vName = vName[(cleanBase.Length + 1)..];
                        else if (vName.StartsWith(cleanBase, StringComparison.OrdinalIgnoreCase))
                            vName = vName[cleanBase.Length..];

                        if (!string.IsNullOrWhiteSpace(vName) &&
                            !rawVariants.Contains(vName, StringComparer.OrdinalIgnoreCase) &&
                            !IsMetadataFile(track) &&
                            !IsMetadataFile(vName))
                        {
                            rawVariants.Add(vName);
                        }
                    }
                }
            }

            string discoveryMode = AppSettings.Load().TrackDiscoveryMode;
            bool allowHeuristic = !discoveryMode.Equals("RacesOnly", StringComparison.OrdinalIgnoreCase);

            // 2. Discover variants from VFS indexed files matching baseTrackName prefix (if heuristics allowed)
            if (allowHeuristic)
            {
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
            }

            // 3. Discover variants from physical disk files if rootPath is set (if heuristics allowed)
            if (allowHeuristic && !string.IsNullOrEmpty(rootPath) && Directory.Exists(rootPath))
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
