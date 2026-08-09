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
        public static string ResolveAssetsRootPath(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath)) inputPath = Directory.GetCurrentDirectory();

            string fullPath = Path.GetFullPath(inputPath);
            if (File.Exists(fullPath))
                fullPath = Path.GetDirectoryName(fullPath) ?? fullPath;

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

            var variants = new List<string>
            {
                "All supported resources",
                $"Base Track Only ({cleanBase})"
            };

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
