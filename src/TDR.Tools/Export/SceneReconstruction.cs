using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Services;
using TDR.Tools.Utilities;

namespace TDR.Tools.Export
{
    public enum EntityCategory
    {
        MovableProp,
        TrafficDrone,
        PowerupItem,
        Pedestrian
    }

    public sealed class PlacedEntity
    {
        public EntityCategory Category { get; set; }
        public string InstanceId { get; set; } = string.Empty;
        public string ModelHieName { get; set; } = string.Empty;
        public Matrix4x4 WorldTransform { get; set; } = Matrix4x4.Identity;
        public string? Tag { get; set; }
        public int TypeId { get; set; }
    }

    /// <summary>
    /// Unified scene entity reconstruction service. Resolves world transforms, raycast ground snapping,
    /// slope alignments, and spline distribution once for all exporters (OBJ, glTF, JSON).
    /// </summary>
    public static class SceneReconstruction
    {
        public static List<PlacedEntity> ReconstructDynamicEntities(
            PakManager vfs,
            string levelName,
            DescriptorAssets assets,
            bool includeMovables,
            bool useLocalCoords,
            Vector3? globalOrigin,
            string? trackContext = null,
            Action<string>? log = null)
        {
            var entities = new List<PlacedEntity>();
            string cleanTrackName = TrackDiscovery.GetBaseTrackName(levelName);



            // 1. Movables (Cumulative Base Track + Variant Track Descriptors)
            if (includeMovables)
            {
                var allMovDescs = new List<string>(assets.MovableDescriptors);
                string defaultVarMov = $"{levelName}_MoveableDescriptor.txt";
                string defaultBaseMov = $"{cleanTrackName}_MoveableDescriptor.txt";

                if (vfs.FileExists(defaultVarMov) && !allMovDescs.Contains(defaultVarMov, StringComparer.OrdinalIgnoreCase))
                {
                    allMovDescs.Add(defaultVarMov);
                }
                else if (allMovDescs.Count == 0 && vfs.FileExists(defaultBaseMov))
                {
                    allMovDescs.Add(defaultBaseMov);
                }

                var terrainRaycaster = TerrainRaycaster.Build(vfs, assets, trackContext ?? cleanTrackName, log);
                var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var spawnedMovableLocations = new List<(string Model, Vector3 Pos)>();

                foreach (string movDesc in allMovDescs)
                {
                    byte[]? movData = vfs.LoadFileContext(movDesc, trackContext ?? cleanTrackName);
                    if (movData == null) continue;

                    string text = Encoding.ASCII.GetString(movData);
                    foreach (string line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                        if (string.IsNullOrWhiteSpace(clean)) continue;

                        string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 8) continue;

                        string hieName = parts[0].Trim('"');
                        if (!hieName.EndsWith(".hie", StringComparison.OrdinalIgnoreCase)) hieName += ".hie";

                        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ||
                            !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) ||
                            !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz) ||
                            !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float qx) ||
                            !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float qy) ||
                            !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float qz) ||
                            !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float qw))
                        {
                            continue;
                        }

                        string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                        var rawPos = new Vector3(px, py, pz);
                        if (spawnedMovableLocations.Any(loc => loc.Model.Equals(modelBaseName, StringComparison.OrdinalIgnoreCase) &&
                                                              Vector3.DistanceSquared(loc.Pos, rawPos) < 0.01f))
                        {
                            continue;
                        }
                        spawnedMovableLocations.Add((modelBaseName, rawPos));

                        int instIdx = instCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                        instCounts[modelBaseName] = instIdx;
                        string instanceId = $"Movable_{modelBaseName}_{instIdx:D3}";

                        float finalPy = py;
                        if (terrainRaycaster.TriangleCount > 0)
                        {
                            if (terrainRaycaster.RaycastGround(px, pz, py + 10.0f, 50.0f, out float hitY))
                            {
                                finalPy = hitY;
                            }
                            else if (terrainRaycaster.RaycastGround(px, pz, 250.0f, 500.0f, out float highHitY))
                            {
                                finalPy = highHitY;
                            }
                        }

                        Matrix4x4 rot = Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, qw));
                        Matrix4x4 worldMat = rot with { M41 = px, M42 = finalPy, M43 = pz };

                        if (useLocalCoords && globalOrigin.HasValue)
                        {
                            worldMat = worldMat with
                            {
                                M41 = worldMat.M41 - globalOrigin.Value.X,
                                M42 = worldMat.M42 - globalOrigin.Value.Y,
                                M43 = worldMat.M43 - globalOrigin.Value.Z
                            };
                        }

                        entities.Add(new PlacedEntity
                        {
                            Category = EntityCategory.MovableProp,
                            InstanceId = instanceId,
                            ModelHieName = hieName,
                            WorldTransform = worldMat,
                            Tag = modelBaseName
                        });
                    }
                }
            }

            // 2. Powerups (.pup)
            string cleanBaseTrack = TrackDiscovery.GetBaseTrackName(levelName);
            var pupNames = new List<string>();
            string varPup = $"{levelName}.pup";
            if (vfs.FileExists(varPup)) pupNames.Add(varPup);

            string basePup = $"{cleanBaseTrack}.pup";
            if (!pupNames.Contains(basePup, StringComparer.OrdinalIgnoreCase) && vfs.FileExists(basePup))
                pupNames.Add(basePup);

            string race1Pup = $"{cleanBaseTrack}_Race1.pup";
            if (!pupNames.Contains(race1Pup, StringComparer.OrdinalIgnoreCase) && vfs.FileExists(race1Pup))
                pupNames.Add(race1Pup);

            var spawnedPupPositions = new List<Vector3>();
            int runningPupIndex = 0;

            foreach (string pupFile in pupNames)
            {
                byte[]? pupData = vfs.LoadFileContext(pupFile, trackContext ?? cleanBaseTrack);
                if (pupData == null) continue;

                string text = Encoding.ASCII.GetString(pupData);
                string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                string lastCommentName = "Powerup";
                int lastTypeId = 0;

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    if (line.StartsWith("//"))
                    {
                        lastCommentName = line.Substring(2).Trim();
                        continue;
                    }

                    if (int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeId))
                    {
                        lastTypeId = typeId;
                        continue;
                    }

                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 &&
                        float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
                    {
                        Vector3 pos = new Vector3(px, py, pz);
                        if (spawnedPupPositions.Any(p => Vector3.DistanceSquared(p, pos) < 0.25f))
                            continue;
                        spawnedPupPositions.Add(pos);

                        runningPupIndex++;
                        string iconHieName = TextureResolver.ResolvePowerupIconHie(lastTypeId, lastCommentName);
                        string cleanComment = lastCommentName.Replace(' ', '_').Replace('!', '_').Replace('.', '_');
                        string instanceId = $"Powerup_{runningPupIndex:D3}_{cleanComment}";

                        Matrix4x4 pupMat = Matrix4x4.CreateTranslation(px, py, pz);
                        if (useLocalCoords && globalOrigin.HasValue)
                        {
                            pupMat.M41 -= globalOrigin.Value.X;
                            pupMat.M42 -= globalOrigin.Value.Y;
                            pupMat.M43 -= globalOrigin.Value.Z;
                        }

                        entities.Add(new PlacedEntity
                        {
                            Category = EntityCategory.PowerupItem,
                            InstanceId = instanceId,
                            ModelHieName = iconHieName,
                            WorldTransform = pupMat,
                            Tag = lastCommentName,
                            TypeId = lastTypeId
                        });
                    }
                }
            }

            // 3. Traffic Drones (DRONE_DESCRIPTOR)
            var droneDescs = new List<string>(assets.DroneDescriptors);
            string defaultDrone = $"{cleanBaseTrack}_DroneDescriptor.txt";
            if (!droneDescs.Contains(defaultDrone, StringComparer.OrdinalIgnoreCase) && vfs.FileExists(defaultDrone))
            {
                droneDescs.Add(defaultDrone);
            }

            var droneRequests = new List<(string Name, int Count)>();
            foreach (string descName in droneDescs)
            {
                byte[]? data = vfs.LoadFileContext(descName, trackContext ?? cleanTrackName);
                if (data == null || data.Length == 0) continue;

                string text = Encoding.ASCII.GetString(data);
                string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string rawLine in lines)
                {
                    string clean = rawLine.Contains("//") ? rawLine[..rawLine.IndexOf("//")].Trim() : rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;

                    string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1], out int count) && count > 0)
                    {
                        droneRequests.Add((parts[0].Trim('"'), count));
                    }
                }
            }

            if (droneRequests.Count > 0)
            {
                var roadSplines = SplineResolver.ResolveRoadSplines(vfs, cleanTrackName, trackContext, log);
                int totalActiveDrones = droneRequests.Sum(r => Math.Min(r.Count, 2));
                var spawnMatrices = SplineResolver.GenerateSpawnMatrices(roadSplines, totalActiveDrones);

                int spawnIdx = 0;
                int globalDroneIndex = 0;
                foreach (var req in droneRequests)
                {
                    string resolvedHie = ResolveDroneModelHie(vfs, req.Name);
                    int spawnCount = Math.Min(req.Count, 2);

                    for (int i = 0; i < spawnCount; i++)
                    {
                        if (spawnIdx >= spawnMatrices.Count) break;
                        Matrix4x4 spawnMat = spawnMatrices[spawnIdx++];
                        globalDroneIndex++;

                        if (useLocalCoords && globalOrigin.HasValue)
                        {
                            spawnMat.M41 -= globalOrigin.Value.X;
                            spawnMat.M42 -= globalOrigin.Value.Y;
                            spawnMat.M43 -= globalOrigin.Value.Z;
                        }

                        string clean = req.Name
                            .Replace("MAIN_NULL_PED", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("MAIN_NULL", "", StringComparison.OrdinalIgnoreCase)
                            .Replace("_PED", "", StringComparison.OrdinalIgnoreCase)
                            .Trim('_');

                        entities.Add(new PlacedEntity
                        {
                            Category = EntityCategory.TrafficDrone,
                            InstanceId = $"TrafficDrone_{globalDroneIndex:D2}_{clean}",
                            ModelHieName = resolvedHie,
                            WorldTransform = spawnMat,
                            Tag = req.Name
                        });
                    }
                }
            }

            // 4. Pedestrian Spawners
            var pedDescs = new List<string>(assets.PedestrianDescriptors);
            string defaultPed = $"{cleanBaseTrack}_PedDescriptor.txt";
            if (!pedDescs.Contains(defaultPed, StringComparer.OrdinalIgnoreCase) && vfs.FileExists(defaultPed))
            {
                pedDescs.Add(defaultPed);
            }

            int pedIndex = 0;
            foreach (string descName in pedDescs)
            {
                byte[]? data = vfs.LoadFileContext(descName, trackContext ?? cleanTrackName);
                if (data == null || data.Length == 0) continue;

                string text = Encoding.ASCII.GetString(data);
                string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string rawLine in lines)
                {
                    string clean = rawLine.Contains("//") ? rawLine[..rawLine.IndexOf("//")].Trim() : rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;

                    string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    // Format: Type Skin Ani PosX PosY PosZ Dir(deg) -> 7 fields
                    if (parts.Length >= 6 &&
                        float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                        float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                        float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
                    {
                        pedIndex++;
                        Vector3 pos = new Vector3(px, py, pz);
                        float yawDeg = 0f;
                        if (parts.Length >= 7 && float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float dir))
                        {
                            yawDeg = dir;
                        }

                        float yawRad = yawDeg * (MathF.PI / 180f);
                        Matrix4x4 pedMat = Matrix4x4.CreateRotationY(yawRad) * Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z);
                        if (useLocalCoords && globalOrigin.HasValue)
                        {
                            pedMat.M41 -= globalOrigin.Value.X;
                            pedMat.M42 -= globalOrigin.Value.Y;
                            pedMat.M43 -= globalOrigin.Value.Z;
                        }

                        entities.Add(new PlacedEntity
                        {
                            Category = EntityCategory.Pedestrian,
                            InstanceId = $"Pedestrian_{pedIndex:D3}",
                            ModelHieName = "__pedestrian_proxy__",
                            WorldTransform = pedMat,
                            Tag = "Pedestrian"
                        });
                    }
                    else if (parts.Length == 3 &&
                             float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float sx) &&
                             float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sy) &&
                             float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float sz))
                    {
                        pedIndex++;
                        Vector3 pos = new Vector3(sx, sy, sz);
                        Matrix4x4 pedMat = Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z);
                        if (useLocalCoords && globalOrigin.HasValue)
                        {
                            pedMat.M41 -= globalOrigin.Value.X;
                            pedMat.M42 -= globalOrigin.Value.Y;
                            pedMat.M43 -= globalOrigin.Value.Z;
                        }

                        entities.Add(new PlacedEntity
                        {
                            Category = EntityCategory.Pedestrian,
                            InstanceId = $"Pedestrian_{pedIndex:D3}",
                            ModelHieName = "__pedestrian_proxy__",
                            WorldTransform = pedMat,
                            Tag = "Pedestrian"
                        });
                    }
                }
            }

            return entities;
        }

        private static string ResolveDroneModelHie(PakManager vfs, string rawName)
        {
            string clean = rawName
                .Replace("MAIN_NULL_PED", "", StringComparison.OrdinalIgnoreCase)
                .Replace("MAIN_NULL", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_PED", "", StringComparison.OrdinalIgnoreCase)
                .Trim('_');

            var candidates = new[]
            {
                rawName,
                rawName + ".hie",
                $"cars/{rawName}/{rawName}.hie",
                $"cars\\{rawName}\\{rawName}.hie",
                clean,
                clean + ".hie",
                $"cars/{clean}/{clean}.hie",
                $"cars\\{clean}\\{clean}.hie",
                $"drones/{clean}/{clean}.hie",
                $"drones\\{clean}\\{clean}.hie"
            };

            foreach (var cand in candidates)
            {
                if (vfs.FileExists(cand)) return cand;
            }

            return clean.EndsWith(".hie", StringComparison.OrdinalIgnoreCase) ? clean : clean + ".hie";
        }
    }

    public readonly struct TerrainTriangle
    {
        public readonly Vector3 A;
        public readonly Vector3 B;
        public readonly Vector3 C;
        public readonly float MinX;
        public readonly float MaxX;
        public readonly float MinZ;
        public readonly float MaxZ;

        public TerrainTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            A = a;
            B = b;
            C = c;
            MinX = Math.Min(a.X, Math.Min(b.X, c.X));
            MaxX = Math.Max(a.X, Math.Max(b.X, c.X));
            MinZ = Math.Min(a.Z, Math.Min(b.Z, c.Z));
            MaxZ = Math.Max(a.Z, Math.Max(b.Z, c.Z));
        }
    }

    /// <summary>
    /// Fast spatial acceleration grid for raycasting against level terrain meshes.
    /// Used by SceneReconstruction to procedurally align MovableProps to track surfaces.
    /// </summary>
    public sealed class TerrainRaycaster
    {
        private const float CellSize = 15.0f;
        private readonly Dictionary<long, List<TerrainTriangle>> _grid = new();
        public int TriangleCount { get; private set; }

        private static long GetCellKey(int gx, int gz)
        {
            return ((long)gx << 32) ^ (uint)gz;
        }

        public static bool IsTerrainHie(string hieName)
        {
            if (string.IsNullOrWhiteSpace(hieName)) return false;
            string lower = Path.GetFileName(hieName).ToLowerInvariant();
            return !lower.Contains("sky") &&
                   !lower.Contains("water") &&
                   !lower.Contains("ocean") &&
                   !lower.Contains("radar") &&
                   !lower.Contains("shadow") &&
                   !lower.Contains("spline") &&
                   !lower.Contains("drone");
        }

        public static TerrainRaycaster Build(
            PakManager vfs,
            DescriptorAssets assets,
            string? trackContext,
            Action<string>? log = null)
        {
            var raycaster = new TerrainRaycaster();
            var meshCache = new Dictionary<string, MSHSContainer>(StringComparer.OrdinalIgnoreCase);
            var hieCache = new Dictionary<string, TDRHierarchy>(StringComparer.OrdinalIgnoreCase);

            TDRHierarchy? LoadHie(string hieName)
            {
                if (hieCache.TryGetValue(hieName, out var cached)) return cached;
                byte[]? hieBytes = vfs.LoadFileContext(hieName, trackContext ?? string.Empty) ?? vfs.LoadFile(hieName);
                if (hieBytes != null && hieBytes.Length > 0)
                {
                    var hie = TDRHierarchy.Load(hieBytes, hieName);
                    hieCache[hieName] = hie;
                    return hie;
                }
                return null;
            }

            MSHSContainer? LoadMsh(string mshName)
            {
                if (meshCache.TryGetValue(mshName, out var cached)) return cached;
                byte[]? mshBytes = vfs.LoadFileContext(mshName, trackContext ?? string.Empty) ?? vfs.LoadFile(mshName);
                if (mshBytes != null && mshBytes.Length > 0)
                {
                    var msh = MSHSContainer.Load(mshBytes, mshName);
                    meshCache[mshName] = msh;
                    return msh;
                }
                return null;
            }

            void ProcessHieHierarchy(TDRHierarchy hie, Matrix4x4 rootTransform)
            {
                if (hie?.Root == null) return;

                void Recurse(TDRNode node, Matrix4x4 parentMatrix)
                {
                    Matrix4x4 worldMat = node.Transform * parentMatrix;

                    if (node.Type == TDRNode.NodeType.Mesh && node.Index >= 0 && node.Index < hie.Meshes.Count)
                    {
                        string mshName = hie.Meshes[node.Index];
                        var container = LoadMsh(mshName);
                        if (container != null)
                        {
                            var stream = new TriangleStream();
                            foreach (var meshData in container.Meshes)
                            {
                                MeshGeometryReader.AppendTriangles(meshData, worldMat, stream);
                            }

                            for (int i = 0; i + 2 < stream.Vertices.Count; i += 3)
                            {
                                raycaster.AddTriangle(new TerrainTriangle(
                                    stream.Vertices[i].Position,
                                    stream.Vertices[i + 1].Position,
                                    stream.Vertices[i + 2].Position));
                            }
                        }
                    }

                    foreach (var child in node.Children)
                    {
                        Recurse(child, worldMat);
                    }
                }

                Recurse(hie.Root, rootTransform);
            }

            // 1. Process base level HIE files
            foreach (string hieFile in assets.HieFiles)
            {
                if (!IsTerrainHie(hieFile)) continue;
                var hie = LoadHie(hieFile);
                if (hie != null)
                {
                    ProcessHieHierarchy(hie, Matrix4x4.Identity);
                }
            }

            // 2. Process placed HIE instances
            foreach (var inst in assets.HieInstances)
            {
                if (!IsTerrainHie(inst.HieName)) continue;
                var hie = LoadHie(inst.HieName);
                if (hie != null)
                {
                    ProcessHieHierarchy(hie, inst.Transform);
                }
            }

            if (raycaster.TriangleCount > 0)
            {
                log?.Invoke($"[TerrainRaycaster] Indexed {raycaster.TriangleCount} terrain triangles for ground snapping.");
            }

            return raycaster;
        }

        public void AddTriangle(TerrainTriangle tri)
        {
            TriangleCount++;
            int minGx = (int)MathF.Floor(tri.MinX / CellSize);
            int maxGx = (int)MathF.Floor(tri.MaxX / CellSize);
            int minGz = (int)MathF.Floor(tri.MinZ / CellSize);
            int maxGz = (int)MathF.Floor(tri.MaxZ / CellSize);

            for (int gx = minGx; gx <= maxGx; gx++)
            {
                for (int gz = minGz; gz <= maxGz; gz++)
                {
                    long key = GetCellKey(gx, gz);
                    if (!_grid.TryGetValue(key, out var list))
                    {
                        list = new List<TerrainTriangle>();
                        _grid[key] = list;
                    }
                    list.Add(tri);
                }
            }
        }

        public bool RaycastGround(float x, float z, float startY, float maxDrop, out float hitY)
        {
            hitY = float.MinValue;
            if (TriangleCount == 0) return false;

            int gx = (int)MathF.Floor(x / CellSize);
            int gz = (int)MathF.Floor(z / CellSize);
            long key = GetCellKey(gx, gz);

            if (!_grid.TryGetValue(key, out var list))
            {
                return false;
            }

            bool found = false;
            foreach (var tri in list)
            {
                if (x < tri.MinX - 0.01f || x > tri.MaxX + 0.01f || z < tri.MinZ - 0.01f || z > tri.MaxZ + 0.01f)
                {
                    continue;
                }

                Vector3 e1 = tri.B - tri.A;
                Vector3 e2 = tri.C - tri.A;
                Vector3 pvec = new Vector3(-e2.Z, 0.0f, e2.X);
                float det = e1.X * pvec.X + e1.Y * pvec.Y + e1.Z * pvec.Z;
                if (MathF.Abs(det) < 1e-7f) continue;
                float invDet = 1.0f / det;

                Vector3 tvec = new Vector3(x - tri.A.X, startY - tri.A.Y, z - tri.A.Z);
                float u = (tvec.X * pvec.X + tvec.Y * pvec.Y + tvec.Z * pvec.Z) * invDet;
                if (u < -0.001f || u > 1.001f) continue;

                Vector3 qvec = new Vector3(
                    tvec.Y * e1.Z - tvec.Z * e1.Y,
                    tvec.Z * e1.X - tvec.X * e1.Z,
                    tvec.X * e1.Y - tvec.Y * e1.X);
                float v = -qvec.Y * invDet;
                if (v < -0.001f || u + v > 1.001f) continue;

                float t = (e2.X * qvec.X + e2.Y * qvec.Y + e2.Z * qvec.Z) * invDet;
                if (t >= 0.0f && t <= maxDrop)
                {
                    float currentHitY = startY - t;
                    if (!found || currentHitY > hitY)
                    {
                        hitY = currentHitY;
                        found = true;
                    }
                }
            }

            return found;
        }
    }
}
