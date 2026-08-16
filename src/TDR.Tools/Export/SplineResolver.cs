using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Export
{
    public static class SplineResolver
    {
        /// <summary>
        /// Verified track name aliases mapping base level names to internal asset prefixes.
        /// - hollowood: "FilmStudioTraffic_Paths_1.pak"
        /// - backofbeyond: "outback"
        /// - docksmd: "New_DOCKSDrone_Paths.pak"
        /// - militarymd: "MilitaryDrone_Paths.pak"
        /// - policestate: "New_PoliceDrone_Path.pak"
        /// </summary>
        private static readonly Dictionary<string, string[]> TrackAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hollowood"] = new[] { "filmstudio", "film_studio" },
            ["backofbeyond"] = new[] { "outback" },
            ["docksmd"] = new[] { "docks" },
            ["militarymd"] = new[] { "military" },
            ["policestate"] = new[] { "police" }
        };

        private static readonly string[] ExcludedKeywords = new[]
        {
            "camera", "energy", "ped", "shark", "gorilla", "kong", "look"
        };

        /// <summary>
        /// Discovers, filters, and loads road splines for the specified track, applying companion alignment matrices.
        /// </summary>
        public static List<TDRSpline> ResolveRoadSplines(
            PakManager vfs,
            string cleanTrackName,
            string? trackContext = null,
            Action<string>? log = null)
        {
            string trackBase = cleanTrackName.ToLowerInvariant();
            var roadSplines = new List<TDRSpline>();

            foreach (var file in vfs.GetFiles())
            {
                string fn = file.Name.Replace('\\', '/').ToLowerInvariant();
                string archiveLower = (file.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();

                // Exclude non-road splines (cameras, energy beams, monster animations)
                bool isExcluded = false;
                foreach (string keyword in ExcludedKeywords)
                {
                    if (fn.Contains(keyword))
                    {
                        isExcluded = true;
                        break;
                    }
                }
                if (isExcluded) continue;

                // Asymmetric extension check: .lins is always a spline package, .lin requires traffic/drone/paths keyword
                bool isSplineExt = fn.EndsWith(".lins", StringComparison.OrdinalIgnoreCase) ||
                                  (fn.EndsWith(".lin", StringComparison.OrdinalIgnoreCase) && (fn.Contains("traffic") || fn.Contains("drone") || fn.Contains("paths")));

                if (!isSplineExt) continue;

                // Check track match directly or via aliases dictionary
                bool matchesTrack = fn.Contains(trackBase) || archiveLower.Contains(trackBase);
                if (!matchesTrack && TrackAliases.TryGetValue(trackBase, out string[]? aliases))
                {
                    foreach (string alias in aliases)
                    {
                        if (fn.Contains(alias) || archiveLower.Contains(alias))
                        {
                            matchesTrack = true;
                            break;
                        }
                    }
                }

                if (!matchesTrack) continue;

                byte[]? splineBytes = vfs.LoadFile(file.Name);
                if (splineBytes != null && splineBytes.Length > 0)
                {
                    try
                    {
                        // 3-tier companion options .txt resolution
                        string optShortName = Path.ChangeExtension(Path.GetFileName(file.Name), ".txt");
                        string optFullPath = Path.ChangeExtension(file.Name, ".txt");
                        byte[]? optBytes = vfs.LoadFileContext(optShortName, trackContext ?? cleanTrackName) ??
                                           vfs.LoadFile(optFullPath) ??
                                           vfs.LoadFile(optShortName);

                        var container = TDRSplineContainer.Load(splineBytes, file.Name, optBytes);
                        foreach (var spline in container.Splines)
                        {
                            if (spline.Points.Count >= 2)
                            {
                                roadSplines.Add(spline);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        log?.Invoke($"[WARN] Failed to load spline '{file.Name}': {ex.Message}");
                    }
                }
            }

            return roadSplines;
        }

        /// <summary>
        /// Generates evenly distributed spawn matrices along the waypoints of available road splines.
        /// </summary>
        public static List<Matrix4x4> GenerateSpawnMatrices(List<TDRSpline> splines, int requestedCount)
        {
            var spawnMatrices = new List<Matrix4x4>();
            if (splines == null || splines.Count == 0)
            {
                spawnMatrices.Add(Matrix4x4.Identity);
                return spawnMatrices;
            }

            int targetCount = Math.Max(1, requestedCount);
            int slotsPerSpline = Math.Max(1, (int)Math.Ceiling((double)targetCount / splines.Count));

            foreach (var spline in splines)
            {
                int ptCount = spline.Points.Count;
                if (ptCount < 2)
                {
                    spawnMatrices.Add(spline.GetSpawnMatrix(0));
                    continue;
                }

                int step = Math.Max(1, ptCount / slotsPerSpline);
                for (int ptIdx = 0; ptIdx < ptCount; ptIdx += step)
                {
                    spawnMatrices.Add(spline.GetSpawnMatrix(ptIdx));
                }
            }

            if (spawnMatrices.Count == 0)
            {
                spawnMatrices.Add(Matrix4x4.Identity);
            }

            return spawnMatrices;
        }
    }
}
