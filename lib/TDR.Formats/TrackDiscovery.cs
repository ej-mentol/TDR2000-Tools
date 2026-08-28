using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TDR.PakLib.Formats
{
    /// <summary>
    /// Pure string and content verification utilities for TDR2000 track files and descriptors.
    /// Completely decoupled from VFS / PakManager.
    /// </summary>
    public static class TrackDiscovery
    {
        private static readonly Regex VariantSuffixRegex = new(@"_(Race\d*|Mission\d*|Multiplayer|Race|Mission)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string GetBaseTrackName(string folderOrFileName)
        {
            if (string.IsNullOrWhiteSpace(folderOrFileName)) return string.Empty;
            return VariantSuffixRegex.Replace(folderOrFileName, string.Empty);
        }

        public static string GetVariantSuffix(string folderOrFileName)
        {
            if (string.IsNullOrWhiteSpace(folderOrFileName)) return string.Empty;
            var match = VariantSuffixRegex.Match(folderOrFileName);
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        public static bool IsVariantTrack(string folderOrFileName)
        {
            if (string.IsNullOrWhiteSpace(folderOrFileName)) return false;
            return VariantSuffixRegex.IsMatch(folderOrFileName);
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

            if (norm.EndsWith("movabledescriptor.txt") ||
                norm.EndsWith("moveabledescriptor.txt") ||
                norm.EndsWith("lightsdescriptor.txt") ||
                norm.EndsWith("breakdescriptor.txt") ||
                norm.EndsWith("pedsdescriptor.txt") ||
                norm.EndsWith("dronedescriptor.txt") ||
                norm.EndsWith("texanimdescriptor.txt") ||
                norm.EndsWith("ambientsnddescriptor.txt") ||
                norm.EndsWith("followerpathsdescriptor.txt") ||
                norm.EndsWith("occludersdescriptor.txt"))
                return false;

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TrackDiscovery] Parse failed: {ex.Message}");
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
            bool hasTracksDir = Directory.Exists(Path.Combine(assetsPath, "tracks")) ||
                                Directory.Exists(Path.Combine(assetsPath, "Tracks")) ||
                                Directory.Exists(Path.Combine(assetsPath, "TRACKS"));

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
    }
}
