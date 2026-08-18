using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Services;

namespace TDR.Tools.Export
{
    public static class SplineResolver
    {
        private static readonly string[] ExcludedKeywords = new[]
        {
            "camera", "energy", "ped", "shark", "gorilla", "kong", "look", "boat", "tug"
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
                bool matchesTrack = TrackDiscoveryService.IsTrackOrAliasMatch(fn, trackBase) ||
                                    TrackDiscoveryService.IsTrackOrAliasMatch(archiveLower, trackBase);

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
        /// Generates evenly distributed, collision-free spawn matrices along the continuous length of available road splines.
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

            var validSplines = splines.Where(s => s.Points.Count >= 2).ToList();
            if (validSplines.Count == 0)
            {
                foreach (var s in splines)
                {
                    if (s.Points.Count > 0) spawnMatrices.Add(s.GetSpawnMatrix(0));
                }
                if (spawnMatrices.Count == 0) spawnMatrices.Add(Matrix4x4.Identity);
                return spawnMatrices;
            }

            var splineLengths = new List<float>();
            float totalLength = 0f;
            foreach (var sp in validSplines)
            {
                float len = 0f;
                for (int i = 0; i < sp.Points.Count - 1; i++)
                {
                    len += Vector3.Distance(sp.Points[i], sp.Points[i + 1]);
                }
                splineLengths.Add(len);
                totalLength += len;
            }

            // Distribute vehicle spawn slots proportionally along splines with minimum distance check
            float minSeparation = 10.0f; // Minimum 10 meters between vehicle centres to prevent stacking
            var spawnedPositions = new List<Vector3>();

            for (int sIdx = 0; sIdx < validSplines.Count; sIdx++)
            {
                var spline = validSplines[sIdx];
                float spLen = splineLengths[sIdx];
                if (spLen < 1.0f) continue;

                int splineSlots = Math.Max(1, (int)Math.Round((double)spLen / Math.Max(1.0f, totalLength) * targetCount));
                float stepDist = spLen / (splineSlots + 1);

                for (int slot = 1; slot <= splineSlots; slot++)
                {
                    float targetDist = slot * stepDist;
                    Matrix4x4 mat = SampleSplineAtDistance(spline, targetDist);
                    Vector3 pos = new Vector3(mat.M41, mat.M42, mat.M43);

                    // Collision check with already spawned points
                    bool tooClose = spawnedPositions.Any(p => Vector3.Distance(p, pos) < minSeparation);
                    if (!tooClose)
                    {
                        spawnedPositions.Add(pos);
                        spawnMatrices.Add(mat);
                        if (spawnMatrices.Count >= targetCount) break;
                    }
                }

                if (spawnMatrices.Count >= targetCount) break;
            }

            // If more slots needed, fill with fallback waypoint positions ensuring min separation
            if (spawnMatrices.Count < targetCount)
            {
                foreach (var spline in validSplines)
                {
                    for (int i = 0; i < spline.Points.Count; i++)
                    {
                        Matrix4x4 mat = spline.GetSpawnMatrix(i);
                        Vector3 pos = new Vector3(mat.M41, mat.M42, mat.M43);
                        if (!spawnedPositions.Any(p => Vector3.Distance(p, pos) < 5.0f))
                        {
                            spawnedPositions.Add(pos);
                            spawnMatrices.Add(mat);
                            if (spawnMatrices.Count >= targetCount) break;
                        }
                    }
                    if (spawnMatrices.Count >= targetCount) break;
                }
            }

            if (spawnMatrices.Count == 0) spawnMatrices.Add(Matrix4x4.Identity);
            return spawnMatrices;
        }

        public static Matrix4x4 ComputeSplineSpawnMatrix(TDRSpline spline, int pointIndex = 0, float yOffset = 0.35f)
        {
            if (spline.Points.Count == 0) return Matrix4x4.Identity;

            int idx0 = Math.Clamp(pointIndex, 0, spline.Points.Count - 1);
            Vector3 pos = spline.Points[idx0];
            pos.Y += yOffset;

            Vector3 forward;
            if (idx0 < spline.Points.Count - 1)
            {
                forward = spline.Points[idx0 + 1] - spline.Points[idx0];
            }
            else if (idx0 > 0)
            {
                forward = spline.Points[idx0] - spline.Points[idx0 - 1];
            }
            else
            {
                forward = Vector3.UnitZ;
            }

            if (forward.LengthSquared() > 0.0001f)
                forward = Vector3.Normalize(forward);
            else
                forward = Vector3.UnitZ;

            Vector3 up = Vector3.UnitY;
            Vector3 right = Vector3.Cross(up, forward);
            if (right.LengthSquared() < 0.0001f)
            {
                // Handle vertical/steep climbs and dives without singularity
                right = Vector3.Cross(Vector3.UnitZ, forward);
                if (right.LengthSquared() < 0.0001f) right = Vector3.UnitX;
                else right = Vector3.Normalize(right);
            }
            else
            {
                right = Vector3.Normalize(right);
            }

            Vector3 realUp = Vector3.Normalize(Vector3.Cross(forward, right));

            return new Matrix4x4(
                right.X,   right.Y,   right.Z,   0f,
                realUp.X,  realUp.Y,  realUp.Z,  0f,
                forward.X, forward.Y, forward.Z, 0f,
                pos.X,     pos.Y,     pos.Z,     1f
            );
        }

        private static Matrix4x4 SampleSplineAtDistance(TDRSpline spline, float distance, float yOffset = 0.35f)
        {
            if (spline.Points.Count == 0) return Matrix4x4.Identity;
            if (spline.Points.Count == 1) return ComputeSplineSpawnMatrix(spline, 0, yOffset);

            float accumulated = 0f;
            for (int i = 0; i < spline.Points.Count - 1; i++)
            {
                Vector3 p0 = spline.Points[i];
                Vector3 p1 = spline.Points[i + 1];
                float segLen = Vector3.Distance(p0, p1);

                if (accumulated + segLen >= distance || i == spline.Points.Count - 2)
                {
                    float t = segLen > 0.0001f ? Math.Clamp((distance - accumulated) / segLen, 0f, 1f) : 0f;
                    Vector3 pos = Vector3.Lerp(p0, p1, t);
                    pos.Y += yOffset;

                    Vector3 forward = segLen > 0.0001f ? Vector3.Normalize(p1 - p0) : Vector3.UnitZ;
                    Vector3 up = Vector3.UnitY;
                    Vector3 right = Vector3.Cross(up, forward);
                    if (right.LengthSquared() < 0.0001f) right = Vector3.UnitX;
                    else right = Vector3.Normalize(right);

                    Vector3 realUp = Vector3.Normalize(Vector3.Cross(forward, right));

                    return new Matrix4x4(
                        right.X,   right.Y,   right.Z,   0f,
                        realUp.X,  realUp.Y,  realUp.Z,  0f,
                        forward.X, forward.Y, forward.Z, 0f,
                        pos.X,     pos.Y,     pos.Z,     1f
                    );
                }

                accumulated += segLen;
            }

            return ComputeSplineSpawnMatrix(spline, spline.Points.Count - 1, yOffset);
        }
    }
}
