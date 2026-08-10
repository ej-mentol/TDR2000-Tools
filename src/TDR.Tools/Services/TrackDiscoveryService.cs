using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Services
{
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

        public static List<string> DiscoverVariants(string baseTrackName, PakManager vfs, string rootPath)
        {
            string cleanBase = TrackDiscovery.GetBaseTrackName(baseTrackName);
            var rawVariants = TrackDiscovery.DiscoverRawVariants(cleanBase, vfs, rootPath);

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

            foreach (var variant in rawVariants)
            {
                if (!variants.Contains(variant, StringComparer.OrdinalIgnoreCase))
                {
                    variants.Add(variant);
                }
            }

            return variants;
        }
    }
}
