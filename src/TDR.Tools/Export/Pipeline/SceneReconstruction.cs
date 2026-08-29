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
    /// <summary>
    /// Unified scene entity reconstruction service. Resolves world transforms, raycast ground snapping,
    /// slope alignments, and spline distribution once for all exporters (OBJ, glTF, JSON).
    /// </summary>
    public static class SceneReconstruction
    {
        /// <summary>
        /// Maximum number of drone instances to spawn per requested vehicle type.
        /// In TDR2000, drone descriptors specify dynamic pool capacities (e.g. 4-10 per type).
        /// Capping per type prevents traffic congestion and road clustering in static scene exports.
        /// </summary>
        public const int MaxSpawnedDronesPerType = 2;

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

            TerrainRaycaster? terrainRaycaster = null;
            if (includeMovables || assets.StartPosition.HasValue || assets.PedestrianDescriptors.Count > 0)
            {
                terrainRaycaster = TerrainRaycaster.Build(vfs, assets, trackContext ?? cleanTrackName, log);
            }

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

                var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var spawnedMovableLocations = new List<(string Model, Vector3 Pos)>();

                var rawMovables = new List<(string HieName, string ModelBaseName, float Px, float Py, float Pz, float Qx, float Qy, float Qz, float Qw)>();

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
                        rawMovables.Add((hieName, modelBaseName, px, py, pz, qx, qy, qz, qw));
                    }
                }

                // Sort movables by authored Py ascending so base/bottom objects are placed & registered into raycaster first.
                // This preserves vertical stacks (e.g. crate pyramids, barrel stacks) so upper props rest atop lower props.
                var sortedMovables = rawMovables.OrderBy(m => m.Py).ToList();

                foreach (var m in sortedMovables)
                {
                    var rawPos = new Vector3(m.Px, m.Py, m.Pz);
                    if (spawnedMovableLocations.Any(loc => loc.Model.Equals(m.ModelBaseName, StringComparison.OrdinalIgnoreCase) &&
                                                          Vector3.DistanceSquared(loc.Pos, rawPos) < 0.01f))
                    {
                        continue;
                    }
                    spawnedMovableLocations.Add((m.ModelBaseName, rawPos));

                    int instIdx = instCounts.GetValueOrDefault(m.ModelBaseName, 0) + 1;
                    instCounts[m.ModelBaseName] = instIdx;
                    string instanceId = $"Movable_{m.ModelBaseName}_{instIdx:D3}";

                    var quat = new Quaternion(m.Qx, m.Qy, m.Qz, m.Qw);
                    Matrix4x4 rot = Matrix4x4.CreateFromQuaternion(quat);

                    // Physical check: evaluate the world UP vector after rotation.
                    // If tilted by more than ~25 deg (worldUp.Y < 0.90), preserve authored 3ds Max elevation.
                    Vector3 worldUp = Vector3.Transform(Vector3.UnitY, quat);
                    bool isTiltedOrFallen = worldUp.Y < 0.90f;

                    // Wall-mounted, hinged fixtures (doors, gates, hinges, attachments)
                    // MUST strictly hang from their authored pivot axes and must not be snapped down to the floor.
                    // Ground-standing props (poles, streetlights, traffic lights, signs, trees) snap cleanly to the ground.
                    bool isWallOrHingedMount = (m.ModelBaseName.Contains("Door", StringComparison.OrdinalIgnoreCase) ||
                                                m.ModelBaseName.Contains("Gate", StringComparison.OrdinalIgnoreCase) ||
                                                m.ModelBaseName.Contains("Hinge", StringComparison.OrdinalIgnoreCase) ||
                                                m.ModelBaseName.Contains("Attach", StringComparison.OrdinalIgnoreCase)) &&
                                               !m.ModelBaseName.Contains("pole", StringComparison.OrdinalIgnoreCase) &&
                                               !m.ModelBaseName.Contains("post", StringComparison.OrdinalIgnoreCase);

                    // [Ground Snapping & Stack Analysis]
                    // Single-point vertical raycasts through (Px, Pz) accurately ground standalone street props (poles, trees, lamps).
                    // However, for indoor micro-assemblies and tabletop items (e.g. computer monitor on a desk edge in HiRise):
                    // 1) If the pivot (Px, Pz) is near the edge of supporting geometry, a mathematical point ray can miss the tabletop
                    //    and hit the room floor 70 cm below.
                    // 2) If (m.Py - floorY > 0.35m), the item is falsely snapped down to the floor (sinking into the desk),
                    //    while the desk itself (gap < 0.35m to floor) remains at its authored height.
                    // Recommended future solution: Use a multi-sample footprint raycast (e.g. RaycastFootprint with radius ~0.15-0.20m)
                    // or respect authored 3ds Max coordinates for indoor/stacked props when fine adjustments are needed.
                    float finalPy = m.Py;
                    if (terrainRaycaster != null && terrainRaycaster.TriangleCount > 0 && !isTiltedOrFallen && !isWallOrHingedMount)
                    {
                        // Start raycast strictly 10 cm above authored Y to stay below indoor ceilings/roofs (e.g. bar tables & chairs),
                        // and search downward up to 30 meters to catch elevated outdoor models (e.g. large trees & towers).
                        if (terrainRaycaster.RaycastGround(m.Px, m.Pz, m.Py + 0.1f, 30.0f, out float hitY))
                        {
                            // Only snap if authored model was significantly floating above ground or supporting prop
                            if (m.Py - hitY > 0.35f)
                            {
                                finalPy = hitY;
                            }
                        }
                    }

                    Matrix4x4 worldMat = rot with { M41 = m.Px, M42 = finalPy, M43 = m.Pz };

                    // Register this placed movable's geometry into the raycaster so subsequent stacked objects land on top of it
                    terrainRaycaster?.AddHierarchyTriangles(vfs, m.HieName, worldMat, trackContext ?? cleanTrackName);

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
                        ModelHieName = m.HieName,
                        WorldTransform = worldMat,
                        Tag = m.ModelBaseName
                    });

                    log?.Invoke($"    [+] Placed Movable '{instanceId}' ({m.HieName}) at ({m.Px:F1}, {finalPy:F1}, {m.Pz:F1})");
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
                int totalActiveDrones = droneRequests.Sum(r => Math.Min(r.Count, MaxSpawnedDronesPerType));
                var existingPositions = entities.Select(e => new Vector3(e.WorldTransform.M41, e.WorldTransform.M42, e.WorldTransform.M43)).ToList();
                var spawnMatrices = SplineResolver.GenerateSpawnMatrices(roadSplines, totalActiveDrones, assets.StartPosition, existingPositions);

                int spawnIdx = 0;
                int globalDroneIndex = 0;
                foreach (var req in droneRequests)
                {
                    string resolvedHie = ResolveDroneModelHie(vfs, req.Name);
                    int spawnCount = Math.Min(req.Count, MaxSpawnedDronesPerType);

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
            // Sort descriptors so full PedDescriptors (which contain mesh & texture mappings) are processed before bare Placement.txt
            var pedDescs = assets.PedestrianDescriptors
                .OrderBy(d => d.EndsWith("Placement.txt", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ToList();

            string defaultPed = $"{cleanBaseTrack}_PedDescriptor.txt";
            if (!pedDescs.Contains(defaultPed, StringComparer.OrdinalIgnoreCase) && vfs.FileExists(defaultPed))
            {
                pedDescs.Insert(0, defaultPed);
            }

            int pedIndex = 0;
            // Guard against multiple descriptors (PEDS/ZOMBIES/ALIENS) sharing the same PlacementFile.
            // In TDR2000 each variant descriptor re-uses the base placement file but supplies its own skins/textures.
            // To avoid duplicating all spawn points, each unique placement file is processed only once —
            // using the FIRST descriptor that references it (the ped-class-specific mapping).
            var processedPlacementFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string descName in pedDescs)
            {
                byte[]? data = vfs.LoadFileContext(descName, trackContext ?? cleanTrackName) ?? vfs.LoadFile(descName);
                if (data == null || data.Length == 0) continue;

                PedDescriptor? pedDesc = null;
                List<PedPlacement> placements = new();
                string placementKey;  // key used for deduplication

                if (descName.EndsWith("Placement.txt", StringComparison.OrdinalIgnoreCase))
                {
                    placementKey = Path.GetFileName(descName);
                    if (!processedPlacementFiles.Add(placementKey)) continue;
                    placements = PedPlacement.Load(data);

                    // If placement was loaded standalone, load the default track PedDescriptor for mesh & texture mapping
                    byte[]? defDescData = vfs.LoadFileContext(defaultPed, trackContext ?? cleanTrackName) ?? vfs.LoadFile(defaultPed);
                    if (defDescData != null && defDescData.Length > 0)
                    {
                        pedDesc = PedDescriptor.Load(defDescData);
                    }
                }
                else
                {
                    pedDesc = PedDescriptor.Load(data);
                    if (pedDesc != null && !string.IsNullOrEmpty(pedDesc.PlacementFile))
                    {
                        placementKey = Path.GetFileName(pedDesc.PlacementFile);
                        if (!processedPlacementFiles.Add(placementKey))
                        {
                            // This placement file was already emitted by a previous descriptor.
                            // Skip to avoid N×duplication (e.g. Peds + Zombies + Aliens all share one placement).
                            continue;
                        }

                        byte[]? placeData = vfs.LoadFileContext(pedDesc.PlacementFile, trackContext ?? cleanTrackName) ?? vfs.LoadFile(pedDesc.PlacementFile);
                        if (placeData != null && placeData.Length > 0)
                        {
                            placements = PedPlacement.Load(placeData);
                        }
                    }
                }

                foreach (var p in placements)
                {
                    pedIndex++;
                    string skinMesh = "__pedestrian_proxy__";
                    string texName = "Default";

                    if (pedDesc != null)
                    {
                        // p.SkinIndex is the "skin type" from the placement file —
                        // a direct index into PedDescriptor.Textures[] (the texture variant list),
                        // NOT a direct index into SkinMeshes[].
                        PedSkinTexture? texMatch = null;
                        if (p.SkinIndex >= 0 && p.SkinIndex < pedDesc.Textures.Count)
                        {
                            texMatch = pedDesc.Textures[p.SkinIndex];
                        }

                        if (texMatch != null)
                        {
                            // Resolve the correct .ski mesh via the mesh-index stored in the texture entry
                            if (texMatch.SkinIndex >= 0 && texMatch.SkinIndex < pedDesc.SkinMeshes.Count)
                            {
                                skinMesh = pedDesc.SkinMeshes[texMatch.SkinIndex];
                            }
                            string face = !string.IsNullOrEmpty(texMatch.FaceTexture) ? texMatch.FaceTexture : texMatch.BodyTexture;
                            string body = !string.IsNullOrEmpty(texMatch.BodyTexture) ? texMatch.BodyTexture : texMatch.FaceTexture;
                            texName = $"{face}|{body}";
                        }
                        else if (pedDesc.SkinMeshes.Count > 0)
                        {
                            // Fallback: placement refers to a skin index beyond the texture list
                            // (e.g. mission-specific VIP peds). Use first available mesh.
                            skinMesh = pedDesc.SkinMeshes[0];
                            log?.Invoke($"    [Ped] SkinType={p.SkinIndex} out of Textures range ({pedDesc.Textures.Count}), using fallback mesh '{skinMesh}'.");
                        }
                    }

                    float headingRad = p.HeadingRadians;
                    if (MathF.Abs(p.HeadingDegrees) < 0.001f)
                    {
                        // Deterministic position-based hash to give ambient pedestrians natural randomized orientations
                        // when the level designer left Dir = 0.0 (e.g. 1920s, Hell, Docks)
                        int seed = (int)(p.Position.X * 100) ^ ((int)(p.Position.Z * 100) << 16) ^ (pedIndex * 265443576);
                        float pseudoRandomDeg = MathF.Abs(seed % 360);
                        headingRad = pseudoRandomDeg * (MathF.PI / 180.0f);
                    }

                    float pedY = p.Position.Y;
                    if (terrainRaycaster != null)
                    {
                        // Start raycast 10 cm above authored position to stay strictly below indoor ceilings/roofs/bridges,
                        // and search downward up to 50 meters to snap elevated pedestrians to the ground, pier, deck, or terrain below.
                        if (terrainRaycaster.RaycastGround(p.Position.X, p.Position.Z, p.Position.Y + 0.1f, 50.0f, out float hitY))
                        {
                            // Only snap downward onto supporting surface — never pull upwards through ceilings or bridge decks
                            if (p.Position.Y >= hitY)
                            {
                                pedY = hitY;
                            }
                        }
                    }

                    Matrix4x4 pedMat = Matrix4x4.CreateRotationY(-headingRad) * Matrix4x4.CreateTranslation(p.Position.X, pedY, p.Position.Z);
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
                        ModelHieName = skinMesh,
                        WorldTransform = pedMat,
                        Tag = texName,
                        TypeId = p.SkinIndex
                    });
                }
            }

            // 4b. Race Start Official / Flag Waver
            // TDR2000 dynamically spawns the Flag Waver at the race starting grid (START_POS / START_ANGLE)
            // using the last texture entry in PedDescriptor (// Flag Waver: Must be Last in List).
            if (assets.StartPosition.HasValue)
            {
                PedDescriptor? activePedDesc = null;
                foreach (string descName in pedDescs)
                {
                    if (!descName.EndsWith("Placement.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[]? data = vfs.LoadFileContext(descName, trackContext ?? cleanTrackName) ?? vfs.LoadFile(descName);
                        if (data != null && data.Length > 0)
                        {
                            activePedDesc = PedDescriptor.Load(data);
                            if (activePedDesc != null && activePedDesc.Textures.Count > 0) break;
                        }
                    }
                }

                if (activePedDesc != null && activePedDesc.Textures.Count > 0)
                {
                    var flagWaverTex = activePedDesc.Textures.LastOrDefault();
                    if (flagWaverTex != null)
                    {
                        string flagSkinMesh = "FlagWoman.ski";
                        if (flagWaverTex.SkinIndex >= 0 && flagWaverTex.SkinIndex < activePedDesc.SkinMeshes.Count)
                        {
                            flagSkinMesh = activePedDesc.SkinMeshes[flagWaverTex.SkinIndex];
                        }

                        string face = !string.IsNullOrEmpty(flagWaverTex.FaceTexture) ? flagWaverTex.FaceTexture : flagWaverTex.BodyTexture;
                        string body = !string.IsNullOrEmpty(flagWaverTex.BodyTexture) ? flagWaverTex.BodyTexture : flagWaverTex.FaceTexture;
                        string flagTexName = $"{face}|{body}";

                        Vector3 startPos = assets.StartPosition.Value;
                        float startAngle = assets.StartAngle ?? 0.0f;

                        // Place Flag Woman on the side of the starting line facing the starting grid
                        float perpAngle = startAngle + (MathF.PI / 2.0f);
                        Vector3 flagPos = startPos + new Vector3(MathF.Cos(perpAngle) * 3.5f, 0.0f, MathF.Sin(perpAngle) * 3.5f);

                        // Raycast to accurately snap Flag Woman feet onto the asphalt/terrain mesh
                        if (terrainRaycaster != null && terrainRaycaster.RaycastGround(flagPos.X, flagPos.Z, startPos.Y + 0.5f, 20.0f, out float groundY))
                        {
                            flagPos.Y = groundY;
                        }

                        // Orient Flag Woman to face directly towards the player's car on the starting line
                        Vector3 toCar = startPos - flagPos;
                        float flagHeading = MathF.Atan2(toCar.X, toCar.Z);

                        Matrix4x4 flagMat = Matrix4x4.CreateRotationY(-flagHeading) * Matrix4x4.CreateTranslation(flagPos.X, flagPos.Y, flagPos.Z);
                        if (useLocalCoords && globalOrigin.HasValue)
                        {
                            flagMat.M41 -= globalOrigin.Value.X;
                            flagMat.M42 -= globalOrigin.Value.Y;
                            flagMat.M43 -= globalOrigin.Value.Z;
                        }

                        pedIndex++;
                        entities.Add(new PlacedEntity
                        {
                            Category = EntityCategory.Pedestrian,
                            InstanceId = $"Pedestrian_{pedIndex:D3}_FlagWoman",
                            ModelHieName = flagSkinMesh,
                            WorldTransform = flagMat,
                            Tag = flagTexName,
                            TypeId = activePedDesc.Textures.Count - 1
                        });

                        log?.Invoke($"    [+] Placed Flag Woman race official ({flagSkinMesh}, {flagTexName}) at starting grid ({flagPos.X:F1}, {flagPos.Y:F1}, {flagPos.Z:F1})");
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
}

