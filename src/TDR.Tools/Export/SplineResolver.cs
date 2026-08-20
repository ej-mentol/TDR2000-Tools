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
            "camera", "cam", "energy", "ped", "shark", "gorilla", "kong", "look", "boat", "tug",
            "biplane", "stuka", "plane", "fly", "air", "train", "tram", "shot", "zoom", "circle",
            "jump", "radar", "gate", "lift", "spinner", "explode", "sphere", "intro", "ai"
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

            // Phase 1: Try resolving via Track Drone Paths HIE Hierarchy (which contains exact intersection placement matrices)
            foreach (var file in vfs.GetFiles())
            {
                string fn = file.Name.Replace('\\', '/').ToLowerInvariant();
                string archiveLower = (file.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();

                if (!fn.EndsWith(".hie", StringComparison.OrdinalIgnoreCase)) continue;
                if (!fn.Contains("drone") && !fn.Contains("traffic")) continue;

                bool isExcluded = false;
                foreach (string keyword in ExcludedKeywords)
                {
                    if (fn.Contains(keyword) || archiveLower.Contains(keyword)) { isExcluded = true; break; }
                }
                if (isExcluded) continue;

                bool matchesTrack = TrackDiscoveryService.IsTrackOrAliasMatch(fn, trackBase) ||
                                    TrackDiscoveryService.IsTrackOrAliasMatch(archiveLower, trackBase);
                if (!matchesTrack) continue;

                byte[]? hieBytes = vfs.LoadFile(file.Name);
                if (hieBytes == null || hieBytes.Length == 0) continue;

                try
                {
                    var hie = TDRHierarchy.Load(hieBytes, file.Name);
                    if (hie.Nodes.Any(n => n.Type == TDRNode.NodeType.Spline))
                    {
                        foreach (string lineFileName in hie.LineNames)
                        {
                            byte[]? spBytes = vfs.LoadFileContext(lineFileName, trackContext ?? cleanTrackName) ??
                                              vfs.LoadFile(lineFileName);
                            if (spBytes != null && spBytes.Length > 0)
                            {
                                // TDR2000 road network splines are authored directly in world space coordinates.
                                var container = TDRSplineContainer.Load(spBytes, lineFileName, null);
                                foreach (var sp in container.Splines)
                                {
                                    if (sp.Points.Count >= 2)
                                    {
                                        roadSplines.Add(sp);
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[WARN] Failed to load hierarchical spline '{file.Name}': {ex.Message}");
                }
            }

            if (roadSplines.Count > 0) return roadSplines;

            // Phase 2: Direct .lins / .lin fallback
            foreach (var file in vfs.GetFiles())
            {
                string fn = file.Name.Replace('\\', '/').ToLowerInvariant();
                string archiveLower = (file.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();

                bool isExcluded = false;
                foreach (string keyword in ExcludedKeywords)
                {
                    if (fn.Contains(keyword) || archiveLower.Contains(keyword)) { isExcluded = true; break; }
                }
                if (isExcluded) continue;

                bool isSplineExt = (fn.EndsWith(".lins", StringComparison.OrdinalIgnoreCase) || fn.EndsWith(".lin", StringComparison.OrdinalIgnoreCase)) &&
                                   (fn.Contains("traffic") || fn.Contains("drone"));

                if (!isSplineExt) continue;

                bool matchesTrack = TrackDiscoveryService.IsTrackOrAliasMatch(fn, trackBase) ||
                                    TrackDiscoveryService.IsTrackOrAliasMatch(archiveLower, trackBase);

                if (!matchesTrack) continue;

                byte[]? splineBytes = vfs.LoadFile(file.Name);
                if (splineBytes != null && splineBytes.Length > 0)
                {
                    try
                    {
                        var container = TDRSplineContainer.Load(splineBytes, file.Name, null);
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
                    if (s.Points.Count > 0) spawnMatrices.Add(ComputeSplineSpawnMatrix(s, 0));
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

            // Distribute vehicle spawn slots across distinct major arterial roads (avoiding short junction connectors)
            float minSeparation = 50.0f; // Minimum 50 meters between vehicles across distinct sectors
            var spawnedPositions = new List<Vector3>();

            // Sort splines: prioritize gentle road grades (<= 10% slope, e.g. city streets) over steep mountain climbing ramps, then by length descending
            var sortedIndices = Enumerable.Range(0, validSplines.Count)
                .OrderBy(idx =>
                {
                    var sp = validSplines[idx];
                    float spLen = splineLengths[idx];
                    float dy = Math.Abs(sp.Points[^1].Y - sp.Points[0].Y);
                    float grade = dy / Math.Max(1.0f, spLen);
                    return grade > 0.10f ? 1 : 0; // 0 = gentle city street first, 1 = steep mountain ramp
                })
                .ThenByDescending(idx => splineLengths[idx])
                .ToList();

            // Phase 1: Spawn exactly 1 vehicle in the middle of each distinct major road (len >= 35m, avoiding intersection turn arcs)
            foreach (int sIdx in sortedIndices)
            {
                var spline = validSplines[sIdx];
                float spLen = splineLengths[sIdx];
                if (spLen < 35.0f) continue;

                float targetDist = spLen * 0.5f; // Middle of the road
                Matrix4x4 mat = SampleSplineAtDistance(spline, targetDist);
                Vector3 pos = new Vector3(mat.M41, mat.M42, mat.M43);

                bool tooClose = spawnedPositions.Any(p => Vector3.Distance(p, pos) < minSeparation);
                if (!tooClose)
                {
                    spawnedPositions.Add(pos);
                    spawnMatrices.Add(mat);
                    if (spawnMatrices.Count >= targetCount) break;
                }
            }

            // Phase 2: If more slots needed, sample proportional slots on major roads
            if (spawnMatrices.Count < targetCount)
            {
                foreach (int sIdx in sortedIndices)
                {
                    var spline = validSplines[sIdx];
                    float spLen = splineLengths[sIdx];
                    if (spLen < 35.0f) continue;

                    int slots = Math.Max(2, (int)(spLen / 50.0f));
                    float stepDist = spLen / (slots + 1);

                    for (int slot = 1; slot <= slots; slot++)
                    {
                        float targetDist = slot * stepDist;
                        Matrix4x4 mat = SampleSplineAtDistance(spline, targetDist);
                        Vector3 pos = new Vector3(mat.M41, mat.M42, mat.M43);

                        bool tooClose = spawnedPositions.Any(p => Vector3.Distance(p, pos) < 35.0f);
                        if (!tooClose)
                        {
                            spawnedPositions.Add(pos);
                            spawnMatrices.Add(mat);
                            if (spawnMatrices.Count >= targetCount) break;
                        }
                    }
                    if (spawnMatrices.Count >= targetCount) break;
                }
            }

            // Fallback if very few splines exist
            if (spawnMatrices.Count < targetCount)
            {
                foreach (var spline in validSplines)
                {
                    for (int i = 0; i < spline.Points.Count; i++)
                    {
                        Matrix4x4 mat = ComputeSplineSpawnMatrix(spline, i);
                        Vector3 pos = new Vector3(mat.M41, mat.M42, mat.M43);
                        if (!spawnedPositions.Any(p => Vector3.Distance(p, pos) < 15.0f))
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

            Vector3 delta;
            if (idx0 < spline.Points.Count - 1)
            {
                delta = spline.Points[idx0 + 1] - spline.Points[idx0];
            }
            else if (idx0 > 0)
            {
                delta = spline.Points[idx0] - spline.Points[idx0 - 1];
            }
            else
            {
                delta = Vector3.UnitZ;
            }

            // Align vehicle heading horizontally along the road (Pitch = 0, Roll = 0, Up = +Y)
            Vector3 forward = new Vector3(delta.X, 0f, delta.Z);
            if (forward.LengthSquared() > 0.0001f)
            {
                forward = Vector3.Normalize(forward);
            }
            else
            {
                forward = Vector3.UnitZ;
            }

            Vector3 up = Vector3.UnitY;
            Vector3 right = Vector3.Cross(up, forward);
            if (right.LengthSquared() < 0.0001f) right = Vector3.UnitX;
            else right = Vector3.Normalize(right);

            return new Matrix4x4(
                right.X,   right.Y,   right.Z,   0f,
                up.X,      up.Y,      up.Z,      0f,
                forward.X, forward.Y, forward.Z, 0f,
                pos.X,     pos.Y,     pos.Z,     1f
            );
        }

        public static Matrix4x4 SampleSplineAtDistance(TDRSpline spline, float distance, float yOffset = 0.35f)
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

                    // Align vehicle heading horizontally along the road (Pitch = 0, Roll = 0, Up = +Y)
                    Vector3 delta = p1 - p0;
                    Vector3 forward = new Vector3(delta.X, 0f, delta.Z);
                    if (forward.LengthSquared() > 0.0001f)
                    {
                        forward = Vector3.Normalize(forward);
                    }
                    else
                    {
                        forward = Vector3.UnitZ;
                    }

                    Vector3 up = Vector3.UnitY;
                    Vector3 right = Vector3.Cross(up, forward);
                    if (right.LengthSquared() < 0.0001f) right = Vector3.UnitX;
                    else right = Vector3.Normalize(right);

                    return new Matrix4x4(
                        right.X,   right.Y,   right.Z,   0f,
                        up.X,      up.Y,      up.Z,      0f,
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
