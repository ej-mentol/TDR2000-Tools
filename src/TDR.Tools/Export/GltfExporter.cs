using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Utilities;

namespace TDR.Tools.Export
{
    public sealed class GltfExporter
    {
        private readonly PakManager _vfs;
        private readonly string _exportDir;
        private readonly bool _useLocalCoords;
        private readonly bool _verbose;
        private readonly bool _convertTexturesToPng;
        private readonly string? _trackContext;
        private readonly Action<string>? _logger;
        private readonly HashSet<string>? _selectedHieFiles;
        private readonly Dictionary<string, MSHSContainer> _meshCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TDRHierarchy> _hieCache = new(StringComparer.OrdinalIgnoreCase);

        private TDRHierarchy? GetOrLoadHierarchy(string hieName, string? archivePath, Func<string, byte[]?> loader)
        {
            string cacheKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";
            if (_hieCache.TryGetValue(cacheKey, out var cached)) return cached;
            byte[]? hieData = loader(hieName);
            if (hieData == null || hieData.Length == 0) return null;
            try
            {
                var hie = TDRHierarchy.Load(hieData, hieName);
                _hieCache[cacheKey] = hie;
                return hie;
            }
            catch
            {
                return null;
            }
        }

        public GltfExporter(PakManager vfs, string exportDir, bool useLocalCoords = false, bool verbose = false, string? trackContext = null, Action<string>? logger = null, bool convertTexturesToPng = true, IEnumerable<string>? selectedHieFiles = null)
        {
            _vfs = vfs;
            _exportDir = exportDir;
            _useLocalCoords = useLocalCoords;
            _verbose = verbose;
            _trackContext = trackContext;
            _logger = logger;
            _convertTexturesToPng = convertTexturesToPng;
            if (selectedHieFiles != null)
            {
                _selectedHieFiles = new HashSet<string>(selectedHieFiles, StringComparer.OrdinalIgnoreCase);
            }
        }

        private bool IsHieSelected(string hiePath)
        {
            if (_selectedHieFiles == null || _selectedHieFiles.Count == 0) return true;

            string normFull = hiePath.Replace('\\', '/').ToLowerInvariant();
            string normFileName = Path.GetFileName(hiePath).ToLowerInvariant();

            return _selectedHieFiles.Any(sel =>
            {
                string normSel = sel.Replace('\\', '/').ToLowerInvariant();
                string selFileName = Path.GetFileName(sel).ToLowerInvariant();

                return normFull.EndsWith(normSel, StringComparison.OrdinalIgnoreCase) ||
                       normSel.EndsWith(normFull, StringComparison.OrdinalIgnoreCase) ||
                       normFileName.Equals(selFileName, StringComparison.OrdinalIgnoreCase);
            });
        }

        private bool IsVerboseEnabled => _verbose || Services.LogService.Instance.IsEnabled(Services.LogLevel.Debug);

        private void Log(string msg, Services.LogLevel level = Services.LogLevel.Info)
        {
            if (level == Services.LogLevel.Debug && !IsVerboseEnabled) return;

            if (_logger != null) _logger(msg);
            else Services.LogService.Instance.Log(level, msg);
        }

        public bool ExportLevelToGltf(byte[] levelData, string levelName, string outputGltfPath, bool includeMovables = true, Action<int, string>? progressCallback = null)
        {
            var assets = ParseLevelDescriptorAssets(levelData);
            if (assets.HieFiles.Count == 0 && assets.MovableDescriptors.Count == 0)
            {
                Log($"    [!] Warning: No valid HIE geometry found for glTF export.");
                return false;
            }

            var gltf = new GltfManifest();
            string binFileName = Path.ChangeExtension(Path.GetFileName(outputGltfPath), ".bin");
            string binPath = Path.Combine(_exportDir, binFileName);

            using var binStream = new MemoryStream();
            using var bw = new BinaryWriter(binStream);

            var materialMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var imageMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var textureMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var meshMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var rootNode = new GltfNode { Name = levelName };
            gltf.Nodes.Add(rootNode);
            gltf.Scenes.Add(new GltfScene { Name = levelName, Nodes = new List<int> { 0 } });

            int GetOrAddMaterial(string texName, string? archivePath)
            {
                if (string.IsNullOrWhiteSpace(texName) || texName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    return -1;

                if (materialMap.TryGetValue(texName, out int matIdx))
                    return matIdx;

                matIdx = gltf.Materials.Count;
                var mat = new GltfMaterial
                {
                    Name = texName,
                    PbrMetallicRoughness = new GltfPbr
                    {
                        BaseColorFactor = new[] { 1.0f, 1.0f, 1.0f, 1.0f },
                        MetallicFactor = 0.0f,
                        RoughnessFactor = 0.8f
                    }
                };

                string? texFileName = ResolveTextureFile(texName, archivePath);
                if (texFileName != null)
                {
                    if (!imageMap.TryGetValue(texFileName, out int imgIdx))
                    {
                        imgIdx = gltf.Images.Count;
                        gltf.Images.Add(new GltfImage { Uri = texFileName });
                        imageMap[texFileName] = imgIdx;
                    }

                    if (!textureMap.TryGetValue(texFileName, out int texIdx))
                    {
                        texIdx = gltf.Textures.Count;
                        gltf.Textures.Add(new GltfTexture { Source = imgIdx });
                        textureMap[texFileName] = texIdx;
                    }

                    mat.PbrMetallicRoughness.BaseColorTexture = new GltfTextureInfo { Index = texIdx };

                    // Set AlphaMode for materials with alpha textures (e.g. tree2b, foliage, glass)
                    if (texFileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("fence", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("leaf", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("rail", StringComparison.OrdinalIgnoreCase))
                    {
                        mat.AlphaMode = "MASK";
                        mat.AlphaCutoff = 0.5f;
                    }
                }

                gltf.Materials.Add(mat);
                materialMap[texName] = matIdx;
                return matIdx;
            }

            // LocalCoords Variant 2: pre-compute global terrain floor minY
            Vector3? globalOrigin = null;
            if (_useLocalCoords)
            {
                float minY = float.MaxValue;
                var terrainHies = new HashSet<string>(assets.HieFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var inst in assets.HieInstances) terrainHies.Add(inst.HieName);

                foreach (string hieName in terrainHies)
                {
                    if (hieName.Contains("sky", StringComparison.OrdinalIgnoreCase)) continue;
                    string? archivePath = _vfs.GetArchivePath(hieName);
                    var preHie = GetOrLoadHierarchy(hieName, archivePath, name => _vfs.LoadFileContext(name, _trackContext ?? levelName));
                    if (preHie == null) continue;
                    var preTris = GroundSnapUtil.ExtractBaseTriangles(preHie, (path) => _vfs.LoadFileContext(path, _trackContext ?? levelName));
                    foreach (var tri in preTris)
                    {
                        if (tri.A.Y < minY) minY = tri.A.Y;
                        if (tri.B.Y < minY) minY = tri.B.Y;
                        if (tri.C.Y < minY) minY = tri.C.Y;
                    }
                }

                if (minY < float.MaxValue)
                {
                    globalOrigin = new Vector3(0f, minY, 0f);
                    if (_verbose) Log($"[LocalCoords glTF] Terrain floor Y = {minY:F2} → globalOrigin set to (0, {minY:F2}, 0)");
                }
            }

            var instancedHies = new HashSet<string>(assets.HieInstances.Select(inst => inst.HieName), StringComparer.OrdinalIgnoreCase);

            // 1. Bake Static Top-Level Level HIE Hierarchies (terrain, sky, water, etc.)
            int totalHies = assets.HieFiles.Count;
            for (int i = 0; i < totalHies; i++)
            {
                string hieName = assets.HieFiles[i];
                if (!IsHieSelected(hieName)) continue;
                if (instancedHies.Contains(hieName)) continue; // Processed with actual instance matrices below

                int pct = (int)((float)(i + 1) / (totalHies + 1) * 80.0f);
                progressCallback?.Invoke(pct, $"Processing glTF mesh ({i + 1}/{totalHies}): {hieName}");

                string? archivePath = _vfs.GetArchivePath(hieName);
                string meshKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";

                if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                {
                    byte[]? hieBytes = _vfs.LoadFileContext(hieName, _trackContext ?? levelName);
                    if (hieBytes != null && hieBytes.Length > 0)
                    {
                        var hie = GetOrLoadHierarchy(hieName, archivePath, _ => hieBytes);
                        if (hie != null)
                        {
                            gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, GetOrAddMaterial);
                            if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                        }
                    }
                }

                if (gltfMeshIdx >= 0)
                {
                    Matrix4x4 startMatrix = assets.HieInitialTransforms.TryGetValue(hieName, out var initMat) ? initMat : Matrix4x4.Identity;
                    if (_useLocalCoords && globalOrigin.HasValue)
                    {
                        startMatrix.M41 -= globalOrigin.Value.X;
                        startMatrix.M42 -= globalOrigin.Value.Y;
                        startMatrix.M43 -= globalOrigin.Value.Z;
                    }
                    string layerName = Path.GetFileNameWithoutExtension(hieName);
                    var layerNode = new GltfNode
                    {
                        Name = layerName,
                        Mesh = gltfMeshIdx,
                        Matrix = ToGltfMatrix(startMatrix)
                    };
                    int nodeIdx = gltf.Nodes.Count;
                    gltf.Nodes.Add(layerNode);
                    rootNode.Children.Add(nodeIdx);
                }
            }

            // 1b. Bake Instanced HIEs (Breakables, Trees, Consoft, Dingables with explicit sub-descriptor placements)
            if (assets.HieInstances.Count > 0)
            {
                var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var inst in assets.HieInstances)
                {
                    string hieName = inst.HieName;
                    if (!IsHieSelected(hieName)) continue;

                    string? archivePath = _vfs.GetArchivePath(hieName);
                    string meshKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";

                    if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                    {
                        byte[]? hieBytes = _vfs.LoadFileContext(hieName, _trackContext ?? levelName);
                        if (hieBytes != null && hieBytes.Length > 0)
                        {
                            var hie = GetOrLoadHierarchy(hieName, archivePath, _ => hieBytes);
                            if (hie != null)
                            {
                                gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, GetOrAddMaterial);
                                if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                            }
                        }
                    }

                    if (gltfMeshIdx >= 0)
                    {
                        string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                        int instIdx = instCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                        instCounts[modelBaseName] = instIdx;
                        string instanceId = $"{modelBaseName}_{instIdx:D3}";

                        Matrix4x4 instMat = inst.Transform;
                        if (_useLocalCoords && globalOrigin.HasValue)
                        {
                            instMat.M41 -= globalOrigin.Value.X;
                            instMat.M42 -= globalOrigin.Value.Y;
                            instMat.M43 -= globalOrigin.Value.Z;
                        }

                        var instNode = new GltfNode
                        {
                            Name = instanceId,
                            Mesh = gltfMeshIdx,
                            Matrix = ToGltfMatrix(instMat)
                        };
                        int nodeIdx = gltf.Nodes.Count;
                        gltf.Nodes.Add(instNode);
                        rootNode.Children.Add(nodeIdx);
                    }
                }
            }

            // 2. Bake Movables (Cumulative Base Track + Variant Track Descriptors)
            if (includeMovables)
            {
                string cleanTrackName = TrackDiscovery.GetBaseTrackName(levelName);
                var allMovDescs = new List<string>(assets.MovableDescriptors);
                string defaultBaseMov = $"{cleanTrackName}_MoveableDescriptor.txt";
                if (_vfs.FileExists(defaultBaseMov) && !allMovDescs.Contains(defaultBaseMov, StringComparer.OrdinalIgnoreCase))
                {
                    allMovDescs.Insert(0, defaultBaseMov);
                }
                string defaultVarMov = $"{levelName}_MoveableDescriptor.txt";
                if (_vfs.FileExists(defaultVarMov) && !allMovDescs.Contains(defaultVarMov, StringComparer.OrdinalIgnoreCase))
                {
                    allMovDescs.Add(defaultVarMov);
                }

                var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (string movDesc in allMovDescs)
                {
                    byte[]? movData = _vfs.LoadFileContext(movDesc, _trackContext ?? cleanTrackName);
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
                        int instIdx = instCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                        instCounts[modelBaseName] = instIdx;
                        string instanceId = $"{modelBaseName}_{instIdx:D3}";

                        string? archivePath = _vfs.GetArchivePath(hieName);
                        string meshKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";

                        if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                        {
                            var hie = GetOrLoadHierarchy(hieName, archivePath, name => _vfs.LoadFileContext(name, _trackContext ?? cleanTrackName));
                            if (hie != null)
                            {
                                gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, GetOrAddMaterial);
                                if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                            }
                        }

                        if (_useLocalCoords && globalOrigin.HasValue)
                        {
                            px -= globalOrigin.Value.X;
                            py -= globalOrigin.Value.Y;
                            pz -= globalOrigin.Value.Z;
                        }

                        if (gltfMeshIdx >= 0)
                        {
                            Matrix4x4 rot = Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, qw));
                            Matrix4x4 movMat = rot with { M41 = px, M42 = py, M43 = pz };
                            int propNodeIdx = gltf.Nodes.Count;
                            var propNode = new GltfNode
                            {
                                Name = instanceId,
                                Mesh = gltfMeshIdx,
                                Matrix = ToGltfMatrix(movMat)
                            };
                            gltf.Nodes.Add(propNode);
                            rootNode.Children.Add(propNodeIdx);
                        }
                    }
                }

                // Bake ALL Powerup Files (.pup) into glTF scene (Base Track .pup + Variant .pup + Race1 .pup)
                string cleanBaseTrack = TrackDiscovery.GetBaseTrackName(levelName);
                var pupNames = new List<string>();
                string basePup = $"{cleanBaseTrack}.pup";
                if (_vfs.FileExists(basePup)) pupNames.Add(basePup);

                string varPup = $"{levelName}.pup";
                if (!varPup.Equals(basePup, StringComparison.OrdinalIgnoreCase) && _vfs.FileExists(varPup))
                    pupNames.Add(varPup);

                string race1Pup = $"{cleanBaseTrack}_Race1.pup";
                if (!pupNames.Contains(race1Pup, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(race1Pup))
                    pupNames.Add(race1Pup);

                foreach (string pupFile in pupNames)
                {
                    byte[]? pupData = _vfs.LoadFileContext(pupFile, _trackContext ?? cleanBaseTrack);
                    if (pupData != null)
                    {
                        AppendPowerupsToGltf(pupData, gltf, rootNode, meshMap, bw, cleanBaseTrack, GetOrAddMaterial, globalOrigin);
                    }
                }

                // 3. Bake Traffic Drones (DRONE_DESCRIPTOR) into glTF scene
                var droneDescs = new List<string>(assets.DroneDescriptors);
                string defaultDrone = $"{cleanBaseTrack}_DroneDescriptor.txt";
                if (!droneDescs.Contains(defaultDrone, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(defaultDrone))
                {
                    droneDescs.Add(defaultDrone);
                }
                if (droneDescs.Count > 0)
                {
                    AppendDronesToGltf(droneDescs, gltf, rootNode, meshMap, bw, cleanBaseTrack, GetOrAddMaterial, globalOrigin);
                }
            }

            // Write .bin file
            byte[] binBytes = binStream.ToArray();
            File.WriteAllBytes(binPath, binBytes);
            gltf.Buffers.Add(new GltfBuffer { Uri = binFileName, ByteLength = binBytes.Length });

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            File.WriteAllText(outputGltfPath, JsonSerializer.Serialize(gltf, jsonOptions));

            Log($"    └─ glTF 2.0 Scene Saved: {Path.GetFileName(outputGltfPath)} ({gltf.Nodes.Count} nodes, {gltf.Meshes.Count} meshes)", Services.LogLevel.Summary);
            return true;
        }

        private void AppendDronesToGltf(
            List<string> droneDescs,
            GltfManifest gltf,
            GltfNode rootNode,
            Dictionary<string, int> meshMap,
            BinaryWriter bw,
            string cleanTrackName,
            Func<string, string?, int> getMaterial,
            Vector3? globalOrigin)
        {
            if (droneDescs == null || droneDescs.Count == 0) return;

            var droneRequests = new List<(string Name, int Count)>();
            foreach (string descName in droneDescs)
            {
                byte[]? data = _vfs.LoadFileContext(descName, _trackContext ?? cleanTrackName);
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

            if (droneRequests.Count == 0) return;

            var roadSplines = SplineResolver.ResolveRoadSplines(_vfs, cleanTrackName, _trackContext, msg => Log(msg));
            int totalDrones = droneRequests.Sum(r => r.Count);
            var spawnMatrices = SplineResolver.GenerateSpawnMatrices(roadSplines, totalDrones);

            int spawnIdx = 0;
            foreach (var req in droneRequests)
            {
                string clean = req.Name
                    .Replace("MAIN_NULL_PED", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("MAIN_NULL", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_PED", "", StringComparison.OrdinalIgnoreCase)
                    .Trim('_');

                var candidates = new[]
                {
                    req.Name,
                    req.Name + ".hie",
                    $"cars/{req.Name}/{req.Name}.hie",
                    clean,
                    clean + ".hie",
                    $"cars/{clean}/{clean}.hie",
                    $"drones/{clean}/{clean}.hie"
                };

                string? resolvedHie = null;
                byte[]? droneBytes = null;
                foreach (var cand in candidates)
                {
                    droneBytes = _vfs.LoadFile(cand);
                    if (droneBytes != null && droneBytes.Length > 0)
                    {
                        resolvedHie = cand;
                        break;
                    }
                }

                if (droneBytes == null || resolvedHie == null) continue;
                string? archivePath = _vfs.GetArchivePath(resolvedHie);
                string meshKey = string.IsNullOrEmpty(archivePath) ? resolvedHie : $"{archivePath}#{resolvedHie}";

                if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                {
                    var hie = GetOrLoadHierarchy(resolvedHie, archivePath, _ => droneBytes);
                    if (hie != null)
                    {
                        gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, getMaterial);
                        if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                    }
                }

                if (gltfMeshIdx >= 0)
                {
                    for (int i = 0; i < req.Count; i++)
                    {
                        Matrix4x4 spawnMat = spawnMatrices[spawnIdx % spawnMatrices.Count];
                        spawnIdx++;

                        if (_useLocalCoords && globalOrigin.HasValue)
                        {
                            spawnMat.M41 -= globalOrigin.Value.X;
                            spawnMat.M42 -= globalOrigin.Value.Y;
                            spawnMat.M43 -= globalOrigin.Value.Z;
                        }

                        int propNodeIdx = gltf.Nodes.Count;
                        var propNode = new GltfNode
                        {
                            Name = $"Drone_{clean}_{i + 1:D2}",
                            Mesh = gltfMeshIdx,
                            Matrix = ToGltfMatrix(spawnMat)
                        };
                        gltf.Nodes.Add(propNode);
                        rootNode.Children.Add(propNodeIdx);
                    }
                }
            }
        }

        private void AppendPowerupsToGltf(
            byte[] pupData,
            GltfManifest gltf,
            GltfNode rootNode,
            Dictionary<string, int> meshMap,
            BinaryWriter bw,
            string cleanTrackName,
            Func<string, string?, int> getOrAddMaterial,
            Vector3? globalOrigin = null)
        {
            string text = Encoding.ASCII.GetString(pupData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            string lastCommentName = "Powerup";
            int lastTypeId = 0;
            int pupIndex = 0;

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
                    pupIndex++;
                    string iconHieName = ResolvePowerupIconHie(lastTypeId, lastCommentName);
                    string cleanComment = lastCommentName.Replace(' ', '_').Replace('!', '_').Replace('.', '_');
                    string instanceId = $"Powerup_{pupIndex:D3}_{cleanComment}";

                    string? archivePath = _vfs.GetArchivePath(iconHieName);
                    string meshKey = string.IsNullOrEmpty(archivePath) ? iconHieName : $"{archivePath}#{iconHieName}";

                    if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                    {
                        var hie = GetOrLoadHierarchy(iconHieName, archivePath, name => _vfs.LoadFileContext(name, _trackContext ?? cleanTrackName));
                        if (hie != null)
                        {
                            gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, getOrAddMaterial);
                            if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                        }
                    }

                    if (gltfMeshIdx >= 0)
                    {
                        Matrix4x4 pupMat = Matrix4x4.CreateTranslation(px, py, pz);
                        if (_useLocalCoords && globalOrigin.HasValue)
                        {
                            pupMat.M41 -= globalOrigin.Value.X;
                            pupMat.M42 -= globalOrigin.Value.Y;
                            pupMat.M43 -= globalOrigin.Value.Z;
                        }

                        int propNodeIdx = gltf.Nodes.Count;
                        var propNode = new GltfNode
                        {
                            Name = instanceId,
                            Mesh = gltfMeshIdx,
                            Matrix = ToGltfMatrix(pupMat)
                        };
                        gltf.Nodes.Add(propNode);
                        rootNode.Children.Add(propNodeIdx);
                    }
                }
            }
        }

        private static string ResolvePowerupIconHie(int typeId, string name) =>
            TextureResolver.ResolvePowerupIconHie(typeId, name);

        private static float[] ToGltfMatrix(Matrix4x4 m)
        {
            return new[]
            {
                m.M11, m.M12, m.M13, m.M14,
                m.M21, m.M22, m.M23, m.M24,
                m.M31, m.M32, m.M33, m.M34,
                m.M41, m.M42, m.M43, m.M44
            };
        }

        private bool TryLoadMesh(string meshName, string? archivePath, out MSHSContainer? container)
        {
            string cacheKey = string.IsNullOrEmpty(archivePath) ? meshName : $"{archivePath}#{meshName}";
            if (_meshCache.TryGetValue(cacheKey, out container)) return container != null;

            byte[]? meshData = _vfs.LoadFileContext(meshName, archivePath ?? _trackContext);
            if (meshData != null)
            {
                container = MSHSContainer.Load(meshData, meshName);
                _meshCache[cacheKey] = container;
                return true;
            }

            container = null;
            return false;
        }

        private int BuildGltfMeshFromHie(
            TDRHierarchy hie,
            GltfManifest gltf,
            string? archivePath,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial)
        {
            if (hie.Meshes.Count == 0) return -1;

            var gMesh = new GltfMesh { Name = Path.GetFileNameWithoutExtension(hie.Name) };
            string currentTex = hie.Textures.Count > 0 ? hie.Textures[0].Trim('"') : "Default";

            void ProcessHieNode(TDRNode? node, Matrix4x4 parentMat, ref string activeTex, HashSet<TDRNode> visited, int depth = 0)
            {
                if (node == null || depth > 200 || !visited.Add(node)) return;

                Matrix4x4 localMat = node.Transform * parentMat;

                if (node.Type == TDRNode.NodeType.Texture && node.Index >= 0 && node.Index < hie.Textures.Count)
                {
                    activeTex = hie.Textures[node.Index].Trim('"');
                }
                if (node.Type == TDRNode.NodeType.Material && node.Index >= 0 && node.Index < hie.Materials.Count)
                {
                    var mat = hie.Materials[node.Index];
                    if (mat.TextureIndex >= 0 && mat.TextureIndex < hie.Textures.Count)
                    {
                        activeTex = hie.Textures[mat.TextureIndex].Trim('"');
                    }
                }
                if (node.Type == TDRNode.NodeType.Mesh)
                {
                    string? meshName = hie.Meshes.Count == 1 ? hie.Meshes[0] : (node.Index >= 0 && node.Index < hie.Meshes.Count ? hie.Meshes[node.Index] : null);
                    if (meshName != null && TryLoadMesh(meshName, archivePath, out var container) && container != null)
                    {
                        int subIndex = hie.Meshes.Count == 1 ? node.Index : -1;
                        if (subIndex >= 0 && subIndex < container.Meshes.Count)
                        {
                            var prim = BuildGltfPrimitive(container.Meshes[subIndex], activeTex, archivePath, gltf, bw, getMaterial);
                            if (prim != null) gMesh.Primitives.Add(prim);
                        }
                        else
                        {
                            for (int i = 0; i < container.Meshes.Count; i++)
                            {
                                string subTex = (i < hie.Textures.Count) ? hie.Textures[i].Trim('"') : activeTex;
                                var prim = BuildGltfPrimitive(container.Meshes[i], subTex, archivePath, gltf, bw, getMaterial);
                                if (prim != null) gMesh.Primitives.Add(prim);
                            }
                        }
                    }
                }

                foreach (var child in node.Children)
                {
                    string childTex = activeTex;
                    ProcessHieNode(child, localMat, ref childTex, visited, depth + 1);
                }
            }

            if (hie.Root != null)
            {
                var visited = new HashSet<TDRNode>();
                ProcessHieNode(hie.Root, Matrix4x4.Identity, ref currentTex, visited);
            }
            else
            {
                foreach (var meshName in hie.Meshes)
                {
                    if (TryLoadMesh(meshName, archivePath, out var container) && container != null)
                    {
                        for (int i = 0; i < container.Meshes.Count; i++)
                        {
                            string subTex = (i < hie.Textures.Count) ? hie.Textures[i].Trim('"') : currentTex;
                            var prim = BuildGltfPrimitive(container.Meshes[i], subTex, archivePath, gltf, bw, getMaterial);
                            if (prim != null) gMesh.Primitives.Add(prim);
                        }
                    }
                }
            }

            if (gMesh.Primitives.Count == 0) return -1;
            int meshIdx = gltf.Meshes.Count;
            gltf.Meshes.Add(gMesh);
            return meshIdx;
        }

        private GltfPrimitive? BuildGltfPrimitive(
            TDRMeshData subMesh,
            string texName,
            string? archivePath,
            GltfManifest gltf,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial)
        {
            if (subMesh == null) return null;

            int matIdx = getMaterial(texName, archivePath);

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<uint>();

            if (subMesh.Mode == MeshMode.TriIndexedPosition || (subMesh.Positions.Count > 0 && subMesh.Faces.Count > 0))
            {
                uint idx = 0;
                foreach (var face in subMesh.Faces)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        var vert = face.Vertices[i];
                        if (vert.PositionIndex < 0 || vert.PositionIndex >= subMesh.Positions.Count) continue;

                        positions.Add(subMesh.Positions[vert.PositionIndex]);
                        normals.Add(vert.Normal);
                        uvs.Add(new Vector2(vert.UV.X, vert.UV.Y));
                        indices.Add(idx++);
                    }
                }
            }
            else if (subMesh.Vertices.Count > 0 && subMesh.Faces.Count > 0)
            {
                foreach (var v in subMesh.Vertices)
                {
                    positions.Add(v.Position);
                    normals.Add(v.Normal);
                    uvs.Add(new Vector2(v.UV.X, v.UV.Y));
                }
                foreach (var f in subMesh.Faces)
                {
                    indices.Add((uint)f.V1);
                    indices.Add((uint)f.V2);
                    indices.Add((uint)f.V3);
                }
            }

            if (positions.Count == 0 || indices.Count == 0) return null;

            // Alignment padding to 4 bytes
            long currentPos = bw.BaseStream.Position;
            long remainder = currentPos % 4;
            if (remainder != 0)
            {
                for (int pad = 0; pad < 4 - remainder; pad++) bw.Write((byte)0);
            }

            // 1. Write Positions
            long posOffset = bw.BaseStream.Position;
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;

            foreach (var p in positions)
            {
                bw.Write(p.X); bw.Write(p.Y); bw.Write(p.Z);
                if (p.X < minX) minX = p.X; if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y; if (p.Y > maxY) maxY = p.Y;
                if (p.Z < minZ) minZ = p.Z; if (p.Z > maxZ) maxZ = p.Z;
            }
            int posByteLength = (int)(bw.BaseStream.Position - posOffset);

            // 2. Write Normals
            long normOffset = bw.BaseStream.Position;
            foreach (var n in normals)
            {
                bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
            }
            int normByteLength = (int)(bw.BaseStream.Position - normOffset);

            // 3. Write UVs
            long uvOffset = bw.BaseStream.Position;
            foreach (var uv in uvs)
            {
                bw.Write(uv.X); bw.Write(uv.Y);
            }
            int uvByteLength = (int)(bw.BaseStream.Position - uvOffset);

            // 4. Write Indices
            long idxOffset = bw.BaseStream.Position;
            foreach (var idx in indices)
            {
                bw.Write(idx);
            }
            int idxByteLength = (int)(bw.BaseStream.Position - idxOffset);

            // Register BufferViews
            int posViewIdx = gltf.BufferViews.Count;
            gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)posOffset, ByteLength = posByteLength, Target = 34962 });

            int normViewIdx = gltf.BufferViews.Count;
            gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)normOffset, ByteLength = normByteLength, Target = 34962 });

            int uvViewIdx = gltf.BufferViews.Count;
            gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)uvOffset, ByteLength = uvByteLength, Target = 34962 });

            int idxViewIdx = gltf.BufferViews.Count;
            gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)idxOffset, ByteLength = idxByteLength, Target = 34963 });

            // Register Accessors
            int posAccIdx = gltf.Accessors.Count;
            gltf.Accessors.Add(new GltfAccessor
            {
                BufferView = posViewIdx,
                ComponentType = 5126, // FLOAT
                Count = positions.Count,
                Type = "VEC3",
                Min = new[] { minX, minY, minZ },
                Max = new[] { maxX, maxY, maxZ }
            });

            int normAccIdx = gltf.Accessors.Count;
            gltf.Accessors.Add(new GltfAccessor
            {
                BufferView = normViewIdx,
                ComponentType = 5126,
                Count = normals.Count,
                Type = "VEC3"
            });

            int uvAccIdx = gltf.Accessors.Count;
            gltf.Accessors.Add(new GltfAccessor
            {
                BufferView = uvViewIdx,
                ComponentType = 5126,
                Count = uvs.Count,
                Type = "VEC2"
            });

            int idxAccIdx = gltf.Accessors.Count;
            gltf.Accessors.Add(new GltfAccessor
            {
                BufferView = idxViewIdx,
                ComponentType = 5125, // UNSIGNED_INT
                Count = indices.Count,
                Type = "SCALAR"
            });

            var prim = new GltfPrimitive
            {
                Indices = idxAccIdx,
                Material = matIdx >= 0 ? matIdx : null
            };
            prim.Attributes["POSITION"] = posAccIdx;
            prim.Attributes["NORMAL"] = normAccIdx;
            prim.Attributes["TEXCOORD_0"] = uvAccIdx;

            return prim;
        }

        private string? ResolveTextureFile(string texName, string? archivePath)
        {
            var matchResult = TextureResolver.ResolveBestMatch(_vfs, texName, archivePath, _trackContext);
            var match = matchResult?.File;
            if (match == null) return null;

            byte[]? data = (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(match.Name, archivePath) : null) ?? _vfs.LoadFile(match);
            if (data == null) return null;

            string outTexName = Path.GetFileName(match.Name);
            string outTexPath = Path.Combine(_exportDir, outTexName);
            if (!File.Exists(outTexPath)) File.WriteAllBytes(outTexPath, data);

            if (_convertTexturesToPng && outTexName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                string pngName = Path.ChangeExtension(outTexName, ".png");
                string pngPath = Path.Combine(_exportDir, pngName);
                if (File.Exists(pngPath) || TgaDecoder.SaveTgaAsPng(data, pngPath))
                {
                    return pngName;
                }
            }

            return outTexName;
        }

        private ObjExporter.DescriptorAssets ParseLevelDescriptorAssets(byte[] levelData)
        {
            var objExporter = new ObjExporter(_vfs, _exportDir, false, false);
            return objExporter.ParseLevelDescriptorAssets(levelData);
        }
    }

    #region glTF 2.0 Schema DTOs
    public sealed class GltfManifest
    {
        [JsonPropertyName("asset")]
        public GltfAsset Asset { get; set; } = new();

        [JsonPropertyName("scene")]
        public int Scene { get; set; } = 0;

        [JsonPropertyName("scenes")]
        public List<GltfScene> Scenes { get; set; } = new();

        [JsonPropertyName("nodes")]
        public List<GltfNode> Nodes { get; set; } = new();

        [JsonPropertyName("meshes")]
        public List<GltfMesh> Meshes { get; set; } = new();

        [JsonPropertyName("materials")]
        public List<GltfMaterial> Materials { get; set; } = new();

        [JsonPropertyName("textures")]
        public List<GltfTexture> Textures { get; set; } = new();

        [JsonPropertyName("images")]
        public List<GltfImage> Images { get; set; } = new();

        [JsonPropertyName("accessors")]
        public List<GltfAccessor> Accessors { get; set; } = new();

        [JsonPropertyName("bufferViews")]
        public List<GltfBufferView> BufferViews { get; set; } = new();

        [JsonPropertyName("buffers")]
        public List<GltfBuffer> Buffers { get; set; } = new();
    }

    public sealed class GltfAsset
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "2.0";

        [JsonPropertyName("generator")]
        public string Generator { get; set; } = "TDR2000 Tools glTF 2.0 Pipeline";
    }

    public sealed class GltfScene
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nodes")]
        public List<int> Nodes { get; set; } = new();
    }

    public sealed class GltfNode
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mesh")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Mesh { get; set; }

        [JsonPropertyName("matrix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Matrix { get; set; }

        [JsonPropertyName("translation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Translation { get; set; }

        [JsonPropertyName("rotation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Rotation { get; set; }

        [JsonPropertyName("scale")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Scale { get; set; }

        [JsonPropertyName("children")]
        public List<int> Children { get; set; } = new();
    }

    public sealed class GltfMesh
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("primitives")]
        public List<GltfPrimitive> Primitives { get; set; } = new();
    }

    public sealed class GltfPrimitive
    {
        [JsonPropertyName("attributes")]
        public Dictionary<string, int> Attributes { get; set; } = new();

        [JsonPropertyName("indices")]
        public int? Indices { get; set; }

        [JsonPropertyName("material")]
        public int? Material { get; set; }
    }

    public sealed class GltfMaterial
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("pbrMetallicRoughness")]
        public GltfPbr PbrMetallicRoughness { get; set; } = new();

        [JsonPropertyName("alphaMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AlphaMode { get; set; }

        [JsonPropertyName("alphaCutoff")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? AlphaCutoff { get; set; }
    }

    public sealed class GltfPbr
    {
        [JsonPropertyName("baseColorFactor")]
        public float[] BaseColorFactor { get; set; } = new[] { 1.0f, 1.0f, 1.0f, 1.0f };

        [JsonPropertyName("baseColorTexture")]
        public GltfTextureInfo? BaseColorTexture { get; set; }

        [JsonPropertyName("metallicFactor")]
        public float MetallicFactor { get; set; } = 0.0f;

        [JsonPropertyName("roughnessFactor")]
        public float RoughnessFactor { get; set; } = 0.8f;
    }

    public sealed class GltfTextureInfo
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    public sealed class GltfTexture
    {
        [JsonPropertyName("source")]
        public int Source { get; set; }
    }

    public sealed class GltfImage
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;
    }

    public sealed class GltfAccessor
    {
        [JsonPropertyName("bufferView")]
        public int BufferView { get; set; }

        [JsonPropertyName("componentType")]
        public int ComponentType { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "VEC3";

        [JsonPropertyName("min")]
        public float[]? Min { get; set; }

        [JsonPropertyName("max")]
        public float[]? Max { get; set; }
    }

    public sealed class GltfBufferView
    {
        [JsonPropertyName("buffer")]
        public int Buffer { get; set; }

        [JsonPropertyName("byteOffset")]
        public int ByteOffset { get; set; }

        [JsonPropertyName("byteLength")]
        public int ByteLength { get; set; }

        [JsonPropertyName("target")]
        public int Target { get; set; }
    }

    public sealed class GltfBuffer
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("byteLength")]
        public int ByteLength { get; set; }
    }
    #endregion
}
