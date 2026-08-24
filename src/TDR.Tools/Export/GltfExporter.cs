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
        public bool ExportArmatures { get; set; }
        private readonly string? _trackContext;
        private readonly Action<string>? _logger;
        private readonly HashSet<string>? _selectedHieFiles;
        private readonly Dictionary<string, MSHSContainer> _meshCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TDRHierarchy> _hieCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<SkeBone>?> _skeletonCache = new(StringComparer.OrdinalIgnoreCase);

        private List<SkeBone>? GetOrLoadActiveBones(string skeName)
        {
            if (_skeletonCache.TryGetValue(skeName, out var cachedBones)) return cachedBones;

            byte[]? skeBytes = _vfs.LoadFileContext(skeName, "humans") ??
                               _vfs.LoadFileContext(skeName, "animals") ??
                               _vfs.LoadFileContext(skeName, "aliens") ??
                               _vfs.LoadFile(skeName);

            SkeSkeleton? ske = (skeBytes != null && skeBytes.Length > 0) ? SkeSkeleton.Load(skeBytes) : null;
            var activeBones = ske?.GetActiveBones();
            _skeletonCache[skeName] = activeBones;
            return activeBones;
        }

        private TDRHierarchy? GetOrLoadHierarchy(string hieName, string? archivePath, Func<string, byte[]?> loader)
        {
            string cacheKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";
            if (_hieCache.TryGetValue(cacheKey, out var cached)) return cached;
            byte[]? hieData = loader(hieName) ??
                              (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(hieName, archivePath) : null) ??
                              _vfs.LoadFileContext(hieName, _trackContext) ??
                              _vfs.LoadFile(hieName);
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

        public GltfExporter(
            PakManager vfs,
            string exportDir,
            bool useLocalCoords = false,
            bool verbose = false,
            string? trackContext = null,
            Action<string>? logger = null,
            bool convertTexturesToPng = true,
            IEnumerable<string>? selectedHieFiles = null,
            bool exportArmatures = false)
        {
            _vfs = vfs;
            _exportDir = exportDir;
            _useLocalCoords = useLocalCoords;
            _verbose = verbose;
            _trackContext = trackContext;
            _logger = logger;
            _convertTexturesToPng = convertTexturesToPng;
            ExportArmatures = exportArmatures;
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

            string tagged = msg.StartsWith("[GLTF]") || msg.StartsWith("    [GLTF]") ? msg : $"[GLTF] {msg}";
            if (_logger != null) _logger(tagged);
            else Services.LogService.Instance.Log(level, tagged);
        }

        public bool ExportLevelToGltf(byte[] levelData, string levelName, string outputGltfPath, bool includeMovables = true, Action<int, string>? progressCallback = null)
        {
            Log($"Exporting level '{levelName}' to glTF 2.0 -> {Path.GetFileName(outputGltfPath)}...");
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

            var rootNode = new GltfNode { Name = levelName, Children = new List<int>() };
            gltf.Nodes.Add(rootNode);
            gltf.Scenes.Add(new GltfScene { Name = levelName, Nodes = new List<int> { 0 } });

            int GetOrAddMaterial(string texName, string? archivePath)
            {
                if (string.IsNullOrWhiteSpace(texName) || texName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                    return -1;

                string matKey = string.IsNullOrEmpty(archivePath) ? texName : $"{archivePath}#{texName}";
                if (materialMap.TryGetValue(matKey, out int matIdx))
                    return matIdx;

                string? texFileName = ResolveTextureFile(texName, archivePath);

                matIdx = gltf.Materials.Count;
                var mat = new GltfMaterial
                {
                    Name = texName,
                    DoubleSided = true,
                    PbrMetallicRoughness = new GltfPbr
                    {
                        BaseColorFactor = new[] { 1.0f, 1.0f, 1.0f, 1.0f },
                        MetallicFactor = 0.0f,
                        RoughnessFactor = 1.0f
                    }
                };

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

                    // 1. Primary Authority: Read official native TDR2000 TTEX (.tx) descriptor
                    byte[]? txBytes = (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext($"{texName}.tx", archivePath) : null) ??
                                      (!string.IsNullOrEmpty(_trackContext) ? _vfs.LoadFileContext($"{texName}.tx", _trackContext) : null) ??
                                      _vfs.LoadFile($"{texName}.tx");
                    var txDesc = TxDescriptor.Load(txBytes, texName);

                    string normTex = texName.ToLowerInvariant();
                    string normFile = (texFileName ?? "").ToLowerInvariant();

                    TxTransparencyMode transMode;
                    if (txDesc != null)
                    {
                        transMode = txDesc.TransparencyMode;
                    }
                    else
                    {
                        // 2. Secondary Authority: Inspect actual TGA image bytes directly
                        byte[]? tgaBytes = (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(texFileName, archivePath) : null) ??
                                           (!string.IsNullOrEmpty(_trackContext) ? _vfs.LoadFileContext(texFileName, _trackContext) : null) ??
                                           _vfs.LoadFile(texFileName);
                        transMode = TgaDecoder.DetectTgaTransparency(tgaBytes);
                    }

                    if (transMode == TxTransparencyMode.Blend)
                    {
                        mat.AlphaMode = "BLEND";
                        mat.DoubleSided = true;
                        mat.PbrMetallicRoughness.MetallicFactor = 0.0f;
                        mat.PbrMetallicRoughness.RoughnessFactor = 0.5f;
                    }
                    else if (transMode == TxTransparencyMode.Mask)
                    {
                        mat.AlphaMode = "MASK";
                        mat.AlphaCutoff = 0.5f;
                        mat.DoubleSided = true;
                    }
                    else
                    {
                        mat.AlphaMode = "OPAQUE";
                    }

                    if (_verbose && txDesc != null)
                    {
                        Log($"      [TX MAT] '{texName}' resolved via TTEX -> Mode: {txDesc.TransparencyMode} (Flags: 0x{txDesc.Flags:X})", Services.LogLevel.Debug);
                    }

                    // 3. Emissive overlays (Halos, Coronas, Glows, Flares) applied additively
                    if (normTex.Contains("corona") || normFile.Contains("corona") ||
                        normTex.Contains("halo") || normFile.Contains("halo") ||
                        normTex.Contains("glow") || normFile.Contains("glow") ||
                        normTex.Contains("flare") || normFile.Contains("flare") ||
                        normTex.Contains("beam") || normFile.Contains("beam"))
                    {
                        mat.AlphaMode = "BLEND";
                        mat.DoubleSided = true;
                        mat.EmissiveFactor = new[] { 1.0f, 1.0f, 1.0f };
                        mat.EmissiveTexture = new GltfTextureInfo { Index = texIdx };
                        mat.PbrMetallicRoughness.BaseColorFactor = new[] { 1.0f, 1.0f, 1.0f, 0.8f };
                        mat.PbrMetallicRoughness.RoughnessFactor = 1.0f;
                        mat.PbrMetallicRoughness.MetallicFactor = 0.0f;
                    }

                    // 6. Sky Sphere / Sky Dome: make unlit with emissive texture
                    if (normTex.Contains("sky") || normFile.Contains("sky") ||
                        normTex.Contains("cloud") || normTex.Contains("horizon"))
                    {
                        mat.EmissiveFactor = new[] { 1.0f, 1.0f, 1.0f };
                        mat.EmissiveTexture = new GltfTextureInfo { Index = texIdx };
                        mat.DoubleSided = true;
                        mat.PbrMetallicRoughness.RoughnessFactor = 1.0f;
                        mat.PbrMetallicRoughness.MetallicFactor = 0.0f;
                    }
                }

                gltf.Materials.Add(mat);
                materialMap[matKey] = matIdx;
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
                    string? archivePath = _vfs.GetArchivePath(hieName, _trackContext);
                    var preHie = GetOrLoadHierarchy(hieName, archivePath, name => _vfs.LoadFileContext(name, _trackContext ?? levelName));
                    if (preHie == null) continue;
                    float hieMinY = MeshGeometryReader.ComputeHierarchyMinimumY(preHie, (path) => _vfs.LoadFileContext(path, _trackContext ?? levelName));
                    if (hieMinY < minY) minY = hieMinY;
                }

                if (minY < float.MaxValue)
                {
                    globalOrigin = new Vector3(0f, minY, 0f);
                    if (_verbose) Log($"[LocalCoords glTF] Terrain floor Y = {minY:F2} → globalOrigin set to (0, {minY:F2}, 0)");
                }
            }

            var processedHieNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1a. Bake Instanced HIEs (Breakables, Trees, Consoft, Dingables with explicit sub-descriptor placements)
            if (assets.HieInstances.Count > 0)
            {
                var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var spawnedHieLocations = new List<(string Model, Vector3 Pos)>();

                foreach (var inst in assets.HieInstances)
                {
                    string hieName = inst.HieName;
                    if (!IsHieSelected(hieName)) continue;

                    string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                    var instPos = new Vector3(inst.Transform.M41, inst.Transform.M42, inst.Transform.M43);

                    // Same-thing spatial deduplication: avoid spawning exact same model at exact same location
                    if (spawnedHieLocations.Any(loc => loc.Model.Equals(modelBaseName, StringComparison.OrdinalIgnoreCase) &&
                                                       Vector3.DistanceSquared(loc.Pos, instPos) < 0.01f))
                    {
                        if (_verbose) Log($"    [Dedup] Skipped duplicate instance of '{hieName}' at {instPos}");
                        continue;
                    }
                    spawnedHieLocations.Add((modelBaseName, instPos));

                    string? archivePath = _vfs.GetArchivePath(hieName, _trackContext);
                    string meshKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";

                    if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                    {
                        byte[]? hieBytes = _vfs.LoadFileContext(hieName, _trackContext ?? levelName) ??
                                           (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(hieName, archivePath) : null) ??
                                           _vfs.LoadFile(hieName);
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
                        int instIdx = instCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                        instCounts[modelBaseName] = instIdx;

                        string prefix = "Prop";
                        if (modelBaseName.Contains("train", StringComparison.OrdinalIgnoreCase))
                            prefix = "Train";
                        else if (modelBaseName.Contains("bridge", StringComparison.OrdinalIgnoreCase))
                            prefix = "Bridge";
                        else if (modelBaseName.Contains("door", StringComparison.OrdinalIgnoreCase) || modelBaseName.Contains("gate", StringComparison.OrdinalIgnoreCase))
                            prefix = "Door";

                        string instanceId = $"{prefix}_{modelBaseName}_{instIdx:D3}";

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
                        rootNode.AddChild(nodeIdx);

                        processedHieNames.Add(hieName);
                        processedHieNames.Add(Path.GetFileName(hieName));
                        processedHieNames.Add(Path.GetFileNameWithoutExtension(hieName));
                    }
                }
            }

            // 1b. Bake Static Top-Level Level HIE Hierarchies (terrain, sky, water, etc.)
            int totalHies = assets.HieFiles.Count;
            for (int i = 0; i < totalHies; i++)
            {
                string hieName = assets.HieFiles[i].Trim('"');
                string cleanHieName = Path.GetFileName(hieName);
                string cleanNoExt = Path.GetFileNameWithoutExtension(hieName);
                if (!IsHieSelected(hieName)) continue;
                if (processedHieNames.Contains(hieName) || 
                    processedHieNames.Contains(cleanHieName) ||
                    processedHieNames.Contains(cleanNoExt))
                {
                    continue;
                }

                int pct = (int)((float)(i + 1) / (totalHies + 1) * 80.0f);
                progressCallback?.Invoke(pct, $"Processing glTF mesh ({i + 1}/{totalHies}): {hieName}");

                string? archivePath = _vfs.GetArchivePath(hieName, _trackContext);
                string meshKey = string.IsNullOrEmpty(archivePath) ? hieName : $"{archivePath}#{hieName}";

                if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                {
                    byte[]? hieBytes = (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(hieName, archivePath) : null) ??
                                       LevelDescriptorParser.LoadDescriptorBytes(_vfs, _trackContext ?? levelName, hieName);
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
                    Matrix4x4 startMatrix = Matrix4x4.Identity;
                    if (assets.HieInitialTransforms.TryGetValue(hieName, out var initMat) ||
                        assets.HieInitialTransforms.TryGetValue(cleanHieName, out initMat) ||
                        assets.HieInitialTransforms.TryGetValue(cleanNoExt, out initMat))
                    {
                        startMatrix = initMat;
                    }

                    if (_useLocalCoords && globalOrigin.HasValue)
                    {
                        startMatrix.M41 -= globalOrigin.Value.X;
                        startMatrix.M42 -= globalOrigin.Value.Y;
                        startMatrix.M43 -= globalOrigin.Value.Z;
                    }

                    string layerName = cleanNoExt;
                    if (cleanNoExt.Contains("water", StringComparison.OrdinalIgnoreCase))
                        layerName = $"Environment_Water_{cleanNoExt}";
                    else if (cleanNoExt.Contains("sky", StringComparison.OrdinalIgnoreCase))
                        layerName = $"Environment_Sky_{cleanNoExt}";

                    var layerNode = new GltfNode
                    {
                        Name = layerName,
                        Mesh = gltfMeshIdx,
                        Matrix = ToGltfMatrix(startMatrix)
                    };
                    int nodeIdx = gltf.Nodes.Count;
                    gltf.Nodes.Add(layerNode);
                    rootNode.AddChild(nodeIdx);

                    processedHieNames.Add(hieName);
                    processedHieNames.Add(cleanHieName);
                }
            }
            // 2. Reconstruct Dynamic Scene Entities (Movables, Powerups, Drones, Pedestrians)
            string cleanBaseTrack = TrackDiscovery.GetBaseTrackName(levelName);
            var dynamicEntities = SceneReconstruction.ReconstructDynamicEntities(
                _vfs,
                levelName,
                assets,
                includeMovables: includeMovables,
                useLocalCoords: _useLocalCoords,
                globalOrigin: globalOrigin,
                trackContext: _trackContext ?? cleanBaseTrack,
                log: msg => Log(msg));

            var ibmMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in dynamicEntities)
            {
                if (entity.Category == EntityCategory.Pedestrian)
                {
                    ExportPedestrianEntity(entity, gltf, meshMap, ibmMap, bw, GetOrAddMaterial, rootNode);
                    continue;
                }

                string? archivePath = _vfs.GetArchivePath(entity.ModelHieName, _trackContext);
                string meshKey = string.IsNullOrEmpty(archivePath) ? entity.ModelHieName : $"{archivePath}#{entity.ModelHieName}";

                if (!meshMap.TryGetValue(meshKey, out int gltfMeshIdx))
                {
                    byte[]? hieBytes = _vfs.LoadFileContext(entity.ModelHieName, _trackContext ?? cleanBaseTrack) ??
                                       (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(entity.ModelHieName, archivePath) : null) ??
                                       _vfs.LoadFile(entity.ModelHieName);
                    if (hieBytes != null && hieBytes.Length > 0)
                    {
                        var hie = GetOrLoadHierarchy(entity.ModelHieName, archivePath, _ => hieBytes);
                        if (hie != null)
                        {
                            gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, GetOrAddMaterial);
                            if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                        }
                    }
                }

                if (gltfMeshIdx >= 0)
                {
                    int nodeIdx = gltf.Nodes.Count;
                    gltf.Nodes.Add(new GltfNode
                    {
                        Name = entity.InstanceId,
                        Mesh = gltfMeshIdx,
                        Matrix = ToGltfMatrix(entity.WorldTransform)
                    });
                    rootNode.AddChild(nodeIdx);

                    if (IsVerboseEnabled)
                    {
                        Log($"      [{entity.Category.ToString().ToUpperInvariant()} PLACED] '{entity.InstanceId}' -> Pos: ({entity.WorldTransform.M41:F2}, {entity.WorldTransform.M42:F2}, {entity.WorldTransform.M43:F2})", Services.LogLevel.Debug);
                    }
                }
            }

            // 5. Add Sun Light (KHR_lights_punctual Directional Light + Ambient Light)
            var lightsExt = new GltfLightsExtension();
            // Sun: Warm Directional light from high angle
            lightsExt.Lights.Add(new GltfLight
            {
                Name = "Sun",
                Type = "directional",
                Color = new[] { 1.0f, 0.96f, 0.88f },
                Intensity = 2.5f
            });
            // Ambient / Sky bounce light
            lightsExt.Lights.Add(new GltfLight
            {
                Name = "SkyAmbient",
                Type = "directional",
                Color = new[] { 0.75f, 0.85f, 1.0f },
                Intensity = 0.8f
            });

            gltf.ExtensionsUsed ??= new List<string>();
            if (!gltf.ExtensionsUsed.Contains("KHR_lights_punctual"))
                gltf.ExtensionsUsed.Add("KHR_lights_punctual");

            gltf.Extensions ??= new Dictionary<string, object>();
            gltf.Extensions["KHR_lights_punctual"] = lightsExt;

            // Sun Node: Rotated ~45 deg pitch down, ~30 deg yaw
            var sunRot = Quaternion.CreateFromYawPitchRoll(0.52f, -0.78f, 0f);
            var sunNode = new GltfNode
            {
                Name = "Sun_Light",
                Rotation = new[] { sunRot.X, sunRot.Y, sunRot.Z, sunRot.W },
                Extensions = new Dictionary<string, object>
                {
                    ["KHR_lights_punctual"] = new GltfLightNodeExtension { Light = 0 }
                }
            };
            int sunNodeIdx = gltf.Nodes.Count;
            gltf.Nodes.Add(sunNode);
            rootNode.AddChild(sunNodeIdx);

            // Ambient Node: Pointed upwards from bottom for ground fill bounce
            var ambRot = Quaternion.CreateFromYawPitchRoll(0f, 1.57f, 0f);
            var ambNode = new GltfNode
            {
                Name = "Ambient_Light",
                Rotation = new[] { ambRot.X, ambRot.Y, ambRot.Z, ambRot.W },
                Extensions = new Dictionary<string, object>
                {
                    ["KHR_lights_punctual"] = new GltfLightNodeExtension { Light = 1 }
                }
            };
            int ambNodeIdx = gltf.Nodes.Count;
            gltf.Nodes.Add(ambNode);
            rootNode.AddChild(ambNodeIdx);

            // Clean empty children lists to strictly adhere to glTF 2.0 schema
            foreach (var node in gltf.Nodes)
            {
                if (node.Children != null && node.Children.Count == 0)
                {
                    node.Children = null;
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

        private int GetOrBuildSkiMesh(string skiName, string texName, GltfManifest gltf, Dictionary<string, int> meshMap, BinaryWriter bw, Func<string, string?, int> getMaterial)
        {
            string cacheKey = $"SKI#{skiName}#{texName}";
            if (meshMap.TryGetValue(cacheKey, out int cachedIdx)) return cachedIdx;

            byte[]? skiBytes =
                (!string.IsNullOrEmpty(_trackContext) ? _vfs.LoadFileContext(skiName, _trackContext) : null) ??
                _vfs.LoadFileContext(skiName, "animation") ??
                _vfs.LoadFileContext(skiName, "humans") ??
                _vfs.LoadFileContext(skiName, "zombies") ??
                _vfs.LoadFileContext(skiName, "animals") ??
                _vfs.LoadFileContext(skiName, "aliens") ??
                _vfs.LoadFile(skiName);

            if (skiBytes == null || skiBytes.Length == 0)
            {
                Log($"    [!] Warning: Could not find '{skiName}', falling back to 'man.ski'.");
                skiBytes = _vfs.LoadFileContext("man.ski", "humans") ?? _vfs.LoadFile("man.ski");
            }
            if (skiBytes == null || skiBytes.Length == 0)
            {
                meshMap[cacheKey] = -1;
                return -1;
            }

            var ski = SkiModel.Load(skiBytes, targetLod: 0);
            if (ski == null || ski.Parts.Count == 0)
            {
                Log($"    [!] Warning: SkiModel.Load failed for '{skiName}'.");
                meshMap[cacheKey] = -1;
                return -1;
            }

            var gMesh = new GltfMesh { Name = Path.GetFileNameWithoutExtension(skiName) };

            string faceTexName = texName;
            string bodyTexName = texName;
            if (texName.Contains('|'))
            {
                var tp = texName.Split('|');
                faceTexName = tp[0];
                bodyTexName = tp[1];
            }

            string? skiArchivePath = _vfs.GetArchivePath(skiName, _trackContext);
            int faceMatIdx = getMaterial(faceTexName, skiArchivePath);
            int bodyMatIdx = getMaterial(bodyTexName, skiArchivePath);

            bool IsHeadPart(SkiPart part)
            {
                if (part.Positions.Count == 0) return false;
                // Only humanoid characters with distinct face/body textures (M_BASIC, F_BASIC, ZOMBIE) split head geometry
                bool bindsToHead = part.Polygons.Any(poly => poly.Vertices.Any(v => v.BoneIndices.Any(b => b == 4 || b == 5)));
                bool bindsToLimbs = part.Polygons.Any(poly => poly.Vertices.Any(v => v.BoneIndices.Any(b => b >= 6)));
                return bindsToHead && !bindsToLimbs;
            }

            // Calculate base ground offset so that creature feet/hooves touch Y = 0 in rest pose
            float feetMinY = 0f;
            foreach (var part in ski.Parts)
            {
                foreach (var pos in part.Positions)
                {
                    if (pos.Y < feetMinY && !float.IsNaN(pos.Y) && !float.IsInfinity(pos.Y))
                    {
                        feetMinY = pos.Y;
                    }
                }
            }
            float groundOffset = feetMinY < -0.01f ? -feetMinY : 0f;

            void AddPrimitiveForParts(IEnumerable<SkiPart> parts, int materialIdx)
            {
                var pList = parts.ToList();
                if (pList.Count == 0) return;

                var positions = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var joints = new List<ushort[]>();
                var weights = new List<Vector4>();
                var indices = new List<int>();

                foreach (var part in pList)
                {
                    int baseV = positions.Count;
                    foreach (var pos in part.Positions)
                    {
                        positions.Add(new Vector3(pos.X, pos.Y + groundOffset, pos.Z));
                    }
                    normals.AddRange(part.Normals);
                    uvs.AddRange(part.UVs);
                    joints.AddRange(part.Joints);
                    weights.AddRange(part.Weights);
                    foreach (var idx in part.Indices)
                    {
                        indices.Add(baseV + idx);
                    }
                }

                if (positions.Count == 0 || indices.Count == 0) return;

                // Alignment padding to 4 bytes
                long currentPos = bw.BaseStream.Position;
                long remainder = currentPos % 4;
                if (remainder != 0)
                {
                    for (int pad = 0; pad < 4 - remainder; pad++) bw.Write((byte)0);
                }

                // 1. Positions
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

                // 2. Normals
                long normOffset = bw.BaseStream.Position;
                foreach (var n in normals)
                {
                    bw.Write(n.X); bw.Write(n.Y); bw.Write(n.Z);
                }
                int normByteLength = (int)(bw.BaseStream.Position - normOffset);

                // 3. UVs
                long uvOffset = bw.BaseStream.Position;
                foreach (var u in uvs)
                {
                    bw.Write(u.X); bw.Write(u.Y);
                }
                int uvByteLength = (int)(bw.BaseStream.Position - uvOffset);

                // 4. Indices (16-bit or 32-bit)
                long idxOffset = bw.BaseStream.Position;
                bool use16Bit = positions.Count <= 65535;
                foreach (var idx in indices)
                {
                    if (use16Bit) bw.Write((ushort)idx);
                    else bw.Write((uint)idx);
                }
                int idxByteLength = (int)(bw.BaseStream.Position - idxOffset);

                bool hasSkinning = joints.Count == positions.Count && weights.Count == positions.Count;
                int accJoints = -1;
                int accWeights = -1;

                if (hasSkinning)
                {
                    // 5. Joints (4 unsigned shorts per vertex)
                    long jointsOffset = bw.BaseStream.Position;
                    foreach (var j in joints)
                    {
                        bw.Write(j[0]); bw.Write(j[1]); bw.Write(j[2]); bw.Write(j[3]);
                    }
                    int jointsByteLength = (int)(bw.BaseStream.Position - jointsOffset);

                    // 6. Weights (4 floats per vertex)
                    long weightsOffset = bw.BaseStream.Position;
                    foreach (var w in weights)
                    {
                        bw.Write(w.X); bw.Write(w.Y); bw.Write(w.Z); bw.Write(w.W);
                    }
                    int weightsByteLength = (int)(bw.BaseStream.Position - weightsOffset);

                    int bvJoints = gltf.BufferViews.Count;
                    gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)jointsOffset, ByteLength = jointsByteLength, Target = 34962 });
                    int bvWeights = gltf.BufferViews.Count;
                    gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)weightsOffset, ByteLength = weightsByteLength, Target = 34962 });

                    accJoints = gltf.Accessors.Count;
                    gltf.Accessors.Add(new GltfAccessor { BufferView = bvJoints, ComponentType = 5123, Count = joints.Count, Type = "VEC4" });
                    accWeights = gltf.Accessors.Count;
                    gltf.Accessors.Add(new GltfAccessor { BufferView = bvWeights, ComponentType = 5126, Count = weights.Count, Type = "VEC4" });
                }

                // BufferViews
                int bvPos = gltf.BufferViews.Count;
                gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)posOffset, ByteLength = posByteLength, Target = 34962 });
                int bvNorm = gltf.BufferViews.Count;
                gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)normOffset, ByteLength = normByteLength, Target = 34962 });
                int bvUv = gltf.BufferViews.Count;
                gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)uvOffset, ByteLength = uvByteLength, Target = 34962 });
                int bvIdx = gltf.BufferViews.Count;
                gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)idxOffset, ByteLength = idxByteLength, Target = 34963 });

                // Accessors
                int accPos = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor
                {
                    BufferView = bvPos,
                    ComponentType = 5126,
                    Count = positions.Count,
                    Type = "VEC3",
                    Min = new[] { minX, minY, minZ },
                    Max = new[] { maxX, maxY, maxZ }
                });
                int accNorm = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor { BufferView = bvNorm, ComponentType = 5126, Count = normals.Count, Type = "VEC3" });
                int accUv = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor { BufferView = bvUv, ComponentType = 5126, Count = uvs.Count, Type = "VEC2" });
                int accIdx = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor
                {
                    BufferView = bvIdx,
                    ComponentType = use16Bit ? 5123 : 5125,
                    Count = indices.Count,
                    Type = "SCALAR"
                });

                var prim = new GltfPrimitive
                {
                    Material = materialIdx >= 0 ? materialIdx : null,
                    Indices = accIdx
                };
                prim.Attributes["POSITION"] = accPos;
                prim.Attributes["NORMAL"] = accNorm;
                prim.Attributes["TEXCOORD_0"] = accUv;
                if (hasSkinning)
                {
                    prim.Attributes["JOINTS_0"] = accJoints;
                    prim.Attributes["WEIGHTS_0"] = accWeights;
                }

                gMesh.Primitives.Add(prim);
            }

            if (faceMatIdx == bodyMatIdx)
            {
                AddPrimitiveForParts(ski.Parts, faceMatIdx);
            }
            else
            {
                var headParts = ski.Parts.Where(IsHeadPart).ToList();
                var bodyParts = ski.Parts.Where(p => !IsHeadPart(p)).ToList();

                if (headParts.Count > 0) AddPrimitiveForParts(headParts, faceMatIdx);
                if (bodyParts.Count > 0) AddPrimitiveForParts(bodyParts, bodyMatIdx);
            }

            if (gMesh.Primitives.Count == 0) return -1;

            int gltfMeshIdx = gltf.Meshes.Count;
            gltf.Meshes.Add(gMesh);
            meshMap[cacheKey] = gltfMeshIdx;
            return gltfMeshIdx;
        }

        private void ExportPedestrianEntity(
            PlacedEntity entity,
            GltfManifest gltf,
            Dictionary<string, int> meshMap,
            Dictionary<string, int> ibmMap,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial,
            GltfNode rootNode)
        {
            int pedMeshIdx = -1;
            if (entity.ModelHieName.EndsWith(".ski", StringComparison.OrdinalIgnoreCase))
            {
                pedMeshIdx = GetOrBuildSkiMesh(entity.ModelHieName, entity.Tag ?? "Default", gltf, meshMap, bw, getMaterial);
            }
            if (pedMeshIdx < 0)
            {
                pedMeshIdx = GetOrBuildPedestrianProxyMesh(gltf, meshMap, bw, getMaterial);
            }

            if (pedMeshIdx < 0) return;

            // 1. Fast Proxy Instancing (Level Scene Mode - zero overhead, instant load)
            if (!ExportArmatures)
            {
                int nodeIdx = gltf.Nodes.Count;
                gltf.Nodes.Add(new GltfNode
                {
                    Name = entity.InstanceId,
                    Mesh = pedMeshIdx,
                    Matrix = ToGltfMatrix(entity.WorldTransform)
                });
                rootNode.AddChild(nodeIdx);
                return;
            }

            string baseSki = Path.GetFileName(entity.ModelHieName).ToLowerInvariant();
            string skeName = ResolveSkeletonName(baseSki);
            var activeBones = GetOrLoadActiveBones(skeName);

            if (activeBones != null && activeBones.Count > 0)
            {
                int accIbm = GetOrBuildIbmAccessor(skeName, activeBones, gltf, bw, ibmMap);

                // 1. Pedestrian Instance Root Node with World Transform
                int pedRootIdx = gltf.Nodes.Count;
                var pedRootNode = new GltfNode
                {
                    Name = entity.InstanceId,
                    Matrix = ToGltfMatrix(entity.WorldTransform)
                };
                gltf.Nodes.Add(pedRootNode);
                rootNode.AddChild(pedRootIdx);

                // 2. Instanced Bones under Pedestrian Root
                var jointNodeIndices = new List<int>(activeBones.Count);
                for (int i = 0; i < activeBones.Count; i++)
                {
                    var bone = activeBones[i];
                    int boneNodeIdx = gltf.Nodes.Count;
                    jointNodeIndices.Add(boneNodeIdx);

                    gltf.Nodes.Add(new GltfNode
                    {
                        Name = $"{entity.InstanceId}_Bone_{bone.ID:D2}",
                        Matrix = ToGltfMatrix(bone.LocalMatrix)
                    });
                }

                // Connect Parent-Child Bone Hierarchy
                for (int i = 0; i < activeBones.Count; i++)
                {
                    var bone = activeBones[i];
                    if (bone.ParentID >= 0 && bone.ParentID < activeBones.Count && bone.ParentID != i)
                    {
                        int parentNodeIdx = jointNodeIndices[bone.ParentID];
                        int childNodeIdx = jointNodeIndices[i];
                        gltf.Nodes[parentNodeIdx].AddChild(childNodeIdx);
                    }
                    else
                    {
                        pedRootNode.AddChild(jointNodeIndices[i]);
                    }
                }

                // 3. Register Skin for this instance
                gltf.Skins ??= new List<GltfSkin>();
                int skinIdx = gltf.Skins.Count;
                gltf.Skins.Add(new GltfSkin
                {
                    Name = $"Armature_{entity.InstanceId}",
                    InverseBindMatrices = accIbm,
                    Joints = jointNodeIndices,
                    Skeleton = jointNodeIndices[0]
                });

                // 4. Mesh Node referencing Skin
                int pedMeshNodeIdx = gltf.Nodes.Count;
                var meshNode = new GltfNode
                {
                    Name = $"{entity.InstanceId}_Mesh",
                    Mesh = pedMeshIdx,
                    Skin = skinIdx
                };
                gltf.Nodes.Add(meshNode);
                pedRootNode.AddChild(pedMeshNodeIdx);
            }
            else
            {
                // Fallback unskinned node
                int nodeIdx = gltf.Nodes.Count;
                gltf.Nodes.Add(new GltfNode
                {
                    Name = entity.InstanceId,
                    Mesh = pedMeshIdx,
                    Matrix = ToGltfMatrix(entity.WorldTransform)
                });
                rootNode.AddChild(nodeIdx);
            }
        }

        private int GetOrBuildIbmAccessor(
            string skeName,
            List<SkeBone> activeBones,
            GltfManifest gltf,
            BinaryWriter bw,
            Dictionary<string, int> ibmMap)
        {
            if (ibmMap.TryGetValue(skeName, out int cachedAcc)) return cachedAcc;

            // Alignment padding to 4 bytes
            long currentPos = bw.BaseStream.Position;
            long remainder = currentPos % 4;
            if (remainder != 0)
            {
                for (int pad = 0; pad < 4 - remainder; pad++) bw.Write((byte)0);
            }

            // Write Inverse Bind Matrices (IBM) (16 floats per bone)
            long ibmOffset = bw.BaseStream.Position;
            foreach (var bone in activeBones)
            {
                Matrix4x4.Invert(bone.WorldMatrix, out Matrix4x4 invBind);
                bw.Write(invBind.M11); bw.Write(invBind.M12); bw.Write(invBind.M13); bw.Write(invBind.M14);
                bw.Write(invBind.M21); bw.Write(invBind.M22); bw.Write(invBind.M23); bw.Write(invBind.M24);
                bw.Write(invBind.M31); bw.Write(invBind.M32); bw.Write(invBind.M33); bw.Write(invBind.M34);
                bw.Write(invBind.M41); bw.Write(invBind.M42); bw.Write(invBind.M43); bw.Write(invBind.M44);
            }
            int ibmByteLength = (int)(bw.BaseStream.Position - ibmOffset);

            int bvIbm = gltf.BufferViews.Count;
            gltf.BufferViews.Add(new GltfBufferView { Buffer = 0, ByteOffset = (int)ibmOffset, ByteLength = ibmByteLength });

            int accIbm = gltf.Accessors.Count;
            gltf.Accessors.Add(new GltfAccessor
            {
                BufferView = bvIbm,
                ComponentType = 5126, // FLOAT
                Count = activeBones.Count,
                Type = "MAT4"
            });

            ibmMap[skeName] = accIbm;
            return accIbm;
        }

        private static string ResolveSkeletonName(string baseSki)
        {
            return baseSki switch
            {
                var s when s.Contains("woman") && s.Contains("flag") => "Flag_woman.ske",
                var s when s.Contains("woman") => "woman.ske",
                var s when s.Contains("horse") => "horse.ske",
                var s when s.Contains("bull") => "bull.ske",
                var s when s.Contains("sheep") => "sheep.ske",
                var s when s.Contains("dog") => "dog.ske",
                var s when s.Contains("cat") => "cat.ske",
                var s when s.Contains("kanga") => "kanga.ske",
                var s when s.Contains("rat") => "GiantRat.ske",
                var s when s.Contains("alien1") => "alien1.ske",
                var s when s.Contains("alien2") => "alien2.ske",
                var s when s.Contains("alien3") => "alien3.ske",
                _ => "man.ske"
            };
        }

        private int GetOrBuildPedestrianProxyMesh(GltfManifest gltf, Dictionary<string, int> meshMap, BinaryWriter bw, Func<string, string?, int> getMaterial)
        {
            string proxyKey = "__pedestrian_proxy__";
            if (meshMap.TryGetValue(proxyKey, out int cachedIdx)) return cachedIdx;

            var proxyMesh = new TDRMeshData
            {
                Mode = MeshMode.Tri
            };
            // Upright proxy box: width 0.5m, height 1.8m, depth 0.5m
            float hx = 0.25f, hy = 1.8f, hz = 0.25f;
            var p0 = new Vector3(-hx, 0, -hz); var p1 = new Vector3(hx, 0, -hz);
            var p2 = new Vector3(hx, 0, hz);  var p3 = new Vector3(-hx, 0, hz);
            var p4 = new Vector3(-hx, hy, -hz); var p5 = new Vector3(hx, hy, -hz);
            var p6 = new Vector3(hx, hy, hz);  var p7 = new Vector3(-hx, hy, hz);

            Vector3[] vList = new[] { p0, p1, p2, p3, p4, p5, p6, p7 };
            foreach (var pt in vList)
            {
                proxyMesh.Vertices.Add(new MeshVertex { Position = pt, Normal = Vector3.UnitY, UV = Vector2.Zero });
            }
            (int, int, int)[] faces = new[]
            {
                (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5),
                (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7),
                (4, 5, 6), (4, 6, 7), (3, 2, 1), (3, 1, 0)
            };
            foreach (var (f0, f1, f2) in faces)
            {
                proxyMesh.Faces.Add(new MeshFace { V1 = f0, V2 = f1, V3 = f2 });
            }

            var prim = BuildGltfPrimitive(proxyMesh, "Default", Matrix4x4.Identity, null, gltf, bw, getMaterial);
            if (prim != null)
            {
                var gMesh = new GltfMesh { Name = "Pedestrian_Proxy" };
                gMesh.Primitives.Add(prim);
                int gltfMeshIdx = gltf.Meshes.Count;
                gltf.Meshes.Add(gMesh);
                meshMap[proxyKey] = gltfMeshIdx;
                return gltfMeshIdx;
            }
            return -1;
        }

        /// <summary>
        /// Converts System.Numerics.Matrix4x4 into the glTF 2.0 16-element column-major array.
        /// NOTE: DO NOT transpose this array. In System.Numerics.Matrix4x4, row 4 (M41, M42, M43)
        /// stores the translation vector (Tx, Ty, Tz). In glTF 2.0 specification (section 5.23),
        /// Column 3 (indices 12..14) MUST contain (Tx, Ty, Tz). Outputting M11..M44 in sequential order
        /// places translation in indices 12..14 and basis vectors in columns 0..2, matching glTF 2.0 1:1.
        /// </summary>
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

            // Tier 1: Direct parent archive of the HIE (e.g. WALL.pak or PATHFOLLOWERS.pak)
            byte[]? meshData = !string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(meshName, archivePath) : null;

            // Tier 2: Specific track context (e.g. Hollowood_Race1)
            if (meshData == null && !string.IsNullOrEmpty(_trackContext))
            {
                meshData = _vfs.LoadFileContext(meshName, _trackContext);
            }

            // Tier 3: Base track family (e.g. "Hollowood" if track is "Hollowood_Race1")
            if (meshData == null && !string.IsNullOrEmpty(_trackContext))
            {
                string baseFamily = TrackDiscovery.GetBaseTrackName(_trackContext);
                if (!string.IsNullOrEmpty(baseFamily) && !baseFamily.Equals(_trackContext, StringComparison.OrdinalIgnoreCase))
                {
                    meshData = _vfs.LoadFileContext(meshName, baseFamily);
                }
            }

            // Tier 4: Global fallback with explicit warning logging
            if (meshData == null)
            {
                meshData = _vfs.LoadFile(meshName);
                if (meshData != null)
                {
                    string? actualArch = _vfs.GetArchivePath(meshName, _trackContext);
                    Log($"[!] Mesh '{meshName}' resolved via GLOBAL fallback (from '{(actualArch ?? "Loose")}') — verify geometry if unexpected.", Services.LogLevel.Warning);
                }
            }

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

            void ProcessHieNode(TDRNode? node, Matrix4x4 parentMat, string activeTex, HashSet<TDRNode> visited, int depth = 0)
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
                    string? meshName = hie.Meshes.Count == 1
                        ? hie.Meshes[0]
                        : (node.Index >= 0 && node.Index < hie.Meshes.Count ? hie.Meshes[node.Index] : null);

                    if (meshName != null && TryLoadMesh(meshName, archivePath, out var container) && container != null)
                    {
                        int subIndex = hie.Meshes.Count == 1 ? node.Index : -1;
                        if (subIndex >= 0 && subIndex < container.Meshes.Count)
                        {
                            var prim = BuildGltfPrimitive(container.Meshes[subIndex], activeTex, localMat, archivePath, gltf, bw, getMaterial, $"{hie.Name} -> {meshName}[{subIndex}]");
                            if (prim != null) gMesh.Primitives.Add(prim);
                        }
                        else
                        {
                            for (int mIdx = 0; mIdx < container.Meshes.Count; mIdx++)
                            {
                                var prim = BuildGltfPrimitive(container.Meshes[mIdx], activeTex, localMat, archivePath, gltf, bw, getMaterial, $"{hie.Name} -> {meshName}[{mIdx}]");
                                if (prim != null) gMesh.Primitives.Add(prim);
                            }
                        }
                    }
                }

                foreach (var child in node.Children)
                {
                    ProcessHieNode(child, localMat, activeTex, visited, depth + 1);
                }
            }

            if (hie.Root != null)
            {
                var visited = new HashSet<TDRNode>();
                ProcessHieNode(hie.Root, Matrix4x4.Identity, currentTex, visited);
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
                            var prim = BuildGltfPrimitive(container.Meshes[i], subTex, Matrix4x4.Identity, archivePath, gltf, bw, getMaterial);
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
            Matrix4x4 transform,
            string? archivePath,
            GltfManifest gltf,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial,
            string? debugMeshName = null)
        {
            if (subMesh == null) return null;

            int matIdx = getMaterial(texName, archivePath);
            bool hasTransform = transform != Matrix4x4.Identity;

            var positions = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();

            string nTex = texName.ToLowerInvariant();
            bool isDoubleSided = nTex.Contains("water") || nTex.Contains("tank") || nTex.Contains("river") ||
                                 nTex.Contains("sea") || nTex.Contains("ocean") || nTex.Contains("glass") ||
                                 nTex.Contains("fence") || nTex.Contains("sign") || nTex.Contains("foliage") ||
                                 nTex.Contains("tree") || nTex.Contains("corona") || nTex.Contains("grate");

            var stream = new TriangleStream();
            MeshGeometryReader.AppendTriangles(subMesh, transform, stream, doubleSided: isDoubleSided);

            // Single unified vertex deduplication across all mesh modes
            var vertMap = new Dictionary<(Vector3 pos, Vector3 norm, Vector2 uv), int>();

            for (int i = 0; i < stream.Vertices.Count; i++)
            {
                var vert = stream.Vertices[i];
                var key = (vert.Position, vert.Normal, vert.UV);

                if (!vertMap.TryGetValue(key, out int vIdx))
                {
                    vIdx = positions.Count;
                    positions.Add(vert.Position);
                    normals.Add(vert.Normal);
                    uvs.Add(vert.UV);
                    vertMap[key] = vIdx;
                }

                indices.Add(vIdx);
            }

            if (positions.Count == 0 || indices.Count == 0)
            {
                string targetDesc = !string.IsNullOrEmpty(debugMeshName) ? $"'{debugMeshName}'" : "primitive";
                Log($"      [ERROR] Skipped degenerate/empty mesh {targetDesc} ({positions.Count} verts, {indices.Count} indices, mode: {subMesh.Mode}, faces: {subMesh.Faces.Count})", Services.LogLevel.Error);
                return null;
            }

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

            // 4. Write Indices (use 16-bit UNSIGNED_SHORT if count <= 65535, else 32-bit UNSIGNED_INT)
            bool useShortIndices = positions.Count <= 65535;
            long idxOffset = bw.BaseStream.Position;
            if (useShortIndices)
            {
                foreach (var idx in indices)
                {
                    bw.Write((ushort)idx);
                }
            }
            else
            {
                foreach (var idx in indices)
                {
                    bw.Write((uint)idx);
                }
            }
            int idxByteLength = (int)(bw.BaseStream.Position - idxOffset);

            // Pad stream to 4-byte boundary for the next primitive
            long endPos = bw.BaseStream.Position;
            long endRem = endPos % 4;
            if (endRem != 0)
            {
                for (int pad = 0; pad < 4 - endRem; pad++) bw.Write((byte)0);
            }

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
                ComponentType = useShortIndices ? 5123 : 5125, // UNSIGNED_SHORT (5123) or UNSIGNED_INT (5125)
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
            var texService = new TextureResolutionService(_vfs, _exportDir, _trackContext, _convertTexturesToPng, Log);
            return texService.ResolveAndSave(texName, archivePath);
        }

        private DescriptorAssets ParseLevelDescriptorAssets(byte[] levelData)
        {
            return LevelDescriptorParser.ParseLevelDescriptorAssets(_vfs, _trackContext, levelData);
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

        [JsonPropertyName("skins")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<GltfSkin>? Skins { get; set; }

        [JsonPropertyName("extensionsUsed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ExtensionsUsed { get; set; }

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }

        [JsonPropertyName("buffers")]
        public List<GltfBuffer> Buffers { get; set; } = new();
    }

    public sealed class GltfSkin
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("inverseBindMatrices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? InverseBindMatrices { get; set; }

        [JsonPropertyName("joints")]
        public List<int> Joints { get; set; } = new();

        [JsonPropertyName("skeleton")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Skeleton { get; set; }
    }

    public sealed class GltfLightsExtension
    {
        [JsonPropertyName("lights")]
        public List<GltfLight> Lights { get; set; } = new();
    }

    public sealed class GltfLight
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "directional";

        [JsonPropertyName("color")]
        public float[] Color { get; set; } = new[] { 1.0f, 1.0f, 1.0f };

        [JsonPropertyName("intensity")]
        public float Intensity { get; set; } = 1.0f;
    }

    public sealed class GltfLightNodeExtension
    {
        [JsonPropertyName("light")]
        public int Light { get; set; }
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<int>? Children { get; set; }

        public void AddChild(int childIndex)
        {
            Children ??= new List<int>();
            Children.Add(childIndex);
        }

        [JsonPropertyName("skin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Skin { get; set; }

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }
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

        [JsonPropertyName("emissiveFactor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? EmissiveFactor { get; set; }

        [JsonPropertyName("emissiveTexture")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GltfTextureInfo? EmissiveTexture { get; set; }

        [JsonPropertyName("doubleSided")]
        public bool DoubleSided { get; set; } = true;

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }
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
        public float RoughnessFactor { get; set; } = 1.0f;
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
