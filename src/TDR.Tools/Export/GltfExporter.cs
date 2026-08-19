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
        private readonly bool _enableGroundSnap;
        private readonly string? _trackContext;
        private readonly Action<string>? _logger;
        private readonly HashSet<string>? _selectedHieFiles;
        private readonly Dictionary<string, MSHSContainer> _meshCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TDRHierarchy> _hieCache = new(StringComparer.OrdinalIgnoreCase);

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

        public GltfExporter(PakManager vfs, string exportDir, bool useLocalCoords = false, bool verbose = false, string? trackContext = null, Action<string>? logger = null, bool convertTexturesToPng = true, IEnumerable<string>? selectedHieFiles = null, bool enableGroundSnap = false)
        {
            _vfs = vfs;
            _exportDir = exportDir;
            _useLocalCoords = useLocalCoords;
            _verbose = verbose;
            _trackContext = trackContext;
            _logger = logger;
            _convertTexturesToPng = convertTexturesToPng;
            _enableGroundSnap = enableGroundSnap;
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

                    // By default, render DoubleSided for game assets to avoid inverted normal / backface culling issues
                    mat.DoubleSided = true;

                    // Set AlphaMode & PBR parameters for materials:
                    // 1. Water & Fluid surfaces: Opaque with clean depth write, single-sided to prevent z-fighting in Blender
                    if (texName.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                        texFileName.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("river", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("ocean", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("sea", StringComparison.OrdinalIgnoreCase))
                    {
                        mat.AlphaMode = "OPAQUE";
                        mat.DoubleSided = false;
                        mat.PbrMetallicRoughness.RoughnessFactor = 1.0f;
                        mat.PbrMetallicRoughness.MetallicFactor = 0.0f;
                    }
                    // 2. Smooth Additive Alpha Blending (BLEND + Emissive Unlit) for halos, coronas, glows, flares, powerups, lens effects
                    else if (texName.Contains("corona", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("corona", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("halo", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("glow", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("flare", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("beam", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("powerup", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("powerup", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("offen", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("offen", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("defen", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("defen", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("halo", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("glow", StringComparison.OrdinalIgnoreCase) ||
                             texFileName.Contains("flare", StringComparison.OrdinalIgnoreCase))
                    {
                        mat.AlphaMode = "BLEND";
                        mat.EmissiveFactor = new[] { 1.0f, 1.0f, 1.0f };
                        mat.EmissiveTexture = new GltfTextureInfo { Index = texIdx };
                        mat.PbrMetallicRoughness.RoughnessFactor = 1.0f;
                        mat.PbrMetallicRoughness.MetallicFactor = 0.0f;
                    }
                    // 3. 32-bit cutout textures (with alpha channel) like foliage, fences, grates, signs
                    else if (texFileName.Contains("_32", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("sign", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("tree", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("fence", StringComparison.OrdinalIgnoreCase) ||
                             texName.Contains("grate", StringComparison.OrdinalIgnoreCase))
                    {
                        mat.AlphaMode = "MASK";
                        mat.AlphaCutoff = 0.5f;
                    }

                    // Sky Sphere / Sky Dome: make unlit with emissive luminance so it never casts shadows or renders black from inside
                    if (texName.Contains("sky", StringComparison.OrdinalIgnoreCase) ||
                        texFileName.Contains("sky", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
                        texName.Contains("horizon", StringComparison.OrdinalIgnoreCase))
                    {
                        mat.EmissiveFactor = new[] { 1.0f, 1.0f, 1.0f };
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

                    string? archivePath = _vfs.GetArchivePath(hieName);
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
                    processedHieNames.Contains(cleanNoExt) ||
                    assets.HieInstances.Any(inst => Path.GetFileName(inst.HieName).Equals(cleanHieName, StringComparison.OrdinalIgnoreCase) ||
                                                    Path.GetFileNameWithoutExtension(inst.HieName).Equals(cleanNoExt, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                int pct = (int)((float)(i + 1) / (totalHies + 1) * 80.0f);
                progressCallback?.Invoke(pct, $"Processing glTF mesh ({i + 1}/{totalHies}): {hieName}");

                string? archivePath = _vfs.GetArchivePath(hieName);
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

            // 2. Bake Movables (Cumulative Base Track + Variant Track Descriptors)
            if (includeMovables)
            {
                string cleanTrackName = TrackDiscovery.GetBaseTrackName(levelName);
                var allMovDescs = new List<string>(assets.MovableDescriptors);
                string defaultVarMov = $"{levelName}_MoveableDescriptor.txt";
                string defaultBaseMov = $"{cleanTrackName}_MoveableDescriptor.txt";

                if (_vfs.FileExists(defaultVarMov) && !allMovDescs.Contains(defaultVarMov, StringComparer.OrdinalIgnoreCase))
                {
                    allMovDescs.Add(defaultVarMov);
                }
                else if (allMovDescs.Count == 0 && _vfs.FileExists(defaultBaseMov))
                {
                    allMovDescs.Add(defaultBaseMov);
                }

                var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var spawnedMovableLocations = new List<(string Model, Vector3 Pos)>();

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

                        // Spatial deduplication: avoid spawning exact same model at exact same position across cumulative descriptors
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
                            rootNode.AddChild(propNodeIdx);

                            if (IsVerboseEnabled)
                            {
                                Log($"      [PROP PLACED] '{instanceId}' -> Pos: ({px:F2}, {py:F2}, {pz:F2}) | Quat: ({qx:F2}, {qy:F2}, {qz:F2}, {qw:F2})", Services.LogLevel.Debug);
                            }
                        }
                    }
                }

                // Bake Powerup Files (.pup) into glTF scene with spatial deduplication
                string cleanBaseTrack = TrackDiscovery.GetBaseTrackName(levelName);
                var pupNames = new List<string>();
                string varPup = $"{levelName}.pup";
                if (_vfs.FileExists(varPup)) pupNames.Add(varPup);

                string basePup = $"{cleanBaseTrack}.pup";
                if (!pupNames.Contains(basePup, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(basePup))
                    pupNames.Add(basePup);

                string race1Pup = $"{cleanBaseTrack}_Race1.pup";
                if (!pupNames.Contains(race1Pup, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(race1Pup))
                    pupNames.Add(race1Pup);

                var spawnedPupPositions = new List<Vector3>();
                int runningPupIndex = 0;
                foreach (string pupFile in pupNames)
                {
                    byte[]? pupData = _vfs.LoadFileContext(pupFile, _trackContext ?? cleanBaseTrack);
                    if (pupData != null)
                    {
                        runningPupIndex = AppendPowerupsToGltf(pupData, gltf, rootNode, meshMap, bw, cleanBaseTrack, GetOrAddMaterial, globalOrigin, spawnedPupPositions, runningPupIndex);
                    }
                }

                // Extract base terrain triangles for ground snapping
                var baseTriangles = new List<GroundSnapUtil.Triangle>();
                if (_enableGroundSnap)
                {
                    var snapHies = new HashSet<string>(assets.HieFiles, StringComparer.OrdinalIgnoreCase);
                    foreach (var inst in assets.HieInstances) snapHies.Add(inst.HieName);

                    foreach (string hieName in snapHies)
                    {
                        if (hieName.Contains("sky", StringComparison.OrdinalIgnoreCase) ||
                            hieName.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                            hieName.Contains("ocean", StringComparison.OrdinalIgnoreCase) ||
                            hieName.Contains("river", StringComparison.OrdinalIgnoreCase) ||
                            hieName.Contains("scol", StringComparison.OrdinalIgnoreCase) ||
                            hieName.Contains("trigger", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        string? archivePath = _vfs.GetArchivePath(hieName);
                        var hie = GetOrLoadHierarchy(hieName, archivePath, name => _vfs.LoadFileContext(name, _trackContext ?? levelName));
                        if (hie != null)
                        {
                            var tris = GroundSnapUtil.ExtractBaseTriangles(hie, (path) => _vfs.LoadFileContext(path, _trackContext ?? levelName));
                            if (tris.Count > 0) baseTriangles.AddRange(tris);
                        }
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
                    AppendDronesToGltf(droneDescs, gltf, rootNode, meshMap, bw, cleanBaseTrack, GetOrAddMaterial, globalOrigin, baseTriangles);
                }

                // 4. Bake Pedestrians (PEDS_DESCRIPTOR) into glTF scene
                var pedDescs = new List<string>(assets.PedestrianDescriptors);
                string defaultPed = $"{cleanBaseTrack}_PedDescriptor.txt";
                if (!pedDescs.Contains(defaultPed, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(defaultPed))
                {
                    pedDescs.Add(defaultPed);
                }
                if (pedDescs.Count > 0)
                {
                    AppendPedestriansToGltf(pedDescs, gltf, rootNode, meshMap, bw, cleanBaseTrack, GetOrAddMaterial, globalOrigin, baseTriangles);
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

        private void AppendDronesToGltf(
            List<string> droneDescs,
            GltfManifest gltf,
            GltfNode rootNode,
            Dictionary<string, int> meshMap,
            BinaryWriter bw,
            string cleanTrackName,
            Func<string, string?, int> getMaterial,
            Vector3? globalOrigin,
            List<GroundSnapUtil.Triangle>? baseTriangles = null)
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
                    $"cars\\{req.Name}\\{req.Name}.hie",
                    clean,
                    clean + ".hie",
                    $"cars/{clean}/{clean}.hie",
                    $"cars\\{clean}\\{clean}.hie",
                    $"drones/{clean}/{clean}.hie",
                    $"drones\\{clean}\\{clean}.hie"
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

                        if (_enableGroundSnap && baseTriangles != null && baseTriangles.Count > 0)
                        {
                            Vector3 origPos = new Vector3(spawnMat.M41, spawnMat.M42, spawnMat.M43);
                            Vector3 snappedPos = GroundSnapUtil.SnapPointToSurface(origPos, baseTriangles, maxDropDistance: 500f, rayStartHeight: 2.0f);
                            spawnMat.M41 = snappedPos.X;
                            spawnMat.M42 = snappedPos.Y + 0.15f;
                            spawnMat.M43 = snappedPos.Z;
                        }

                        if (_useLocalCoords && globalOrigin.HasValue)
                        {
                            spawnMat.M41 -= globalOrigin.Value.X;
                            spawnMat.M42 -= globalOrigin.Value.Y;
                            spawnMat.M43 -= globalOrigin.Value.Z;
                        }

                        int propNodeIdx = gltf.Nodes.Count;
                        var propNode = new GltfNode
                        {
                            Name = $"TrafficDrone_{i + 1:D2}_{clean}",
                            Mesh = gltfMeshIdx,
                            Matrix = ToGltfMatrix(spawnMat)
                        };
                        gltf.Nodes.Add(propNode);
                        rootNode.AddChild(propNodeIdx);
                    }
                }
            }
        }

        private void AppendPedestriansToGltf(
            List<string> pedDescs,
            GltfManifest gltf,
            GltfNode rootNode,
            Dictionary<string, int> meshMap,
            BinaryWriter bw,
            string cleanTrackName,
            Func<string, string?, int> getMaterial,
            Vector3? globalOrigin,
            List<GroundSnapUtil.Triangle>? baseTriangles)
        {
            if (pedDescs == null || pedDescs.Count == 0) return;

            string pedHieName = "pedestrian_placeholder.hie";
            string? archivePath = _vfs.GetArchivePath(pedHieName);
            string meshKey = string.IsNullOrEmpty(archivePath) ? pedHieName : $"{archivePath}#{pedHieName}";

            int gltfMeshIdx = -1;
            byte[]? pedHieBytes = _vfs.LoadFileContext(pedHieName, _trackContext ?? cleanTrackName) ?? _vfs.LoadFile(pedHieName);
            if (pedHieBytes != null && pedHieBytes.Length > 0)
            {
                var hie = GetOrLoadHierarchy(pedHieName, archivePath, _ => pedHieBytes);
                if (hie != null)
                {
                    gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, getMaterial);
                    if (gltfMeshIdx >= 0) meshMap[meshKey] = gltfMeshIdx;
                }
            }

            // If no 3D pedestrian mesh found in VFS, generate a standard proxy billboard / capsule mesh
            if (gltfMeshIdx < 0)
            {
                string proxyKey = "__pedestrian_proxy__";
                if (!meshMap.TryGetValue(proxyKey, out gltfMeshIdx))
                {
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
                    // 12 Triangles (6 faces)
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
                        gltfMeshIdx = gltf.Meshes.Count;
                        gltf.Meshes.Add(gMesh);
                        meshMap[proxyKey] = gltfMeshIdx;
                    }
                }
            }

            int pedIndex = 0;
            foreach (string descName in pedDescs)
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
                    if (parts.Length >= 3 &&
                        float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) &&
                        float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) &&
                        float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz))
                    {
                        pedIndex++;
                        Vector3 pos = new Vector3(px, py, pz);
                        if (_enableGroundSnap && baseTriangles != null && baseTriangles.Count > 0)
                        {
                            pos = GroundSnapUtil.SnapPointToSurface(pos, baseTriangles, maxDropDistance: 500f, rayStartHeight: 25.0f);
                        }

                        Matrix4x4 pedMat = Matrix4x4.CreateTranslation(pos.X, pos.Y, pos.Z);
                        if (_useLocalCoords && globalOrigin.HasValue)
                        {
                            pedMat.M41 -= globalOrigin.Value.X;
                            pedMat.M42 -= globalOrigin.Value.Y;
                            pedMat.M43 -= globalOrigin.Value.Z;
                        }

                        if (gltfMeshIdx >= 0)
                        {
                            int nodeIdx = gltf.Nodes.Count;
                            var pedNode = new GltfNode
                            {
                                Name = $"Pedestrian_{pedIndex:D3}",
                                Mesh = gltfMeshIdx,
                                Matrix = ToGltfMatrix(pedMat)
                            };
                            gltf.Nodes.Add(pedNode);
                            rootNode.AddChild(nodeIdx);
                        }
                    }
                }
            }
        }

        private int AppendPowerupsToGltf(
            byte[] pupData,
            GltfManifest gltf,
            GltfNode rootNode,
            Dictionary<string, int> meshMap,
            BinaryWriter bw,
            string cleanTrackName,
            Func<string, string?, int> getOrAddMaterial,
            Vector3? globalOrigin = null,
            List<Vector3>? spawnedPositions = null,
            int initialPupIndex = 0)
        {
            string text = Encoding.ASCII.GetString(pupData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            string lastCommentName = "Powerup";
            int lastTypeId = 0;
            int pupIndex = initialPupIndex;

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
                    if (spawnedPositions != null)
                    {
                        if (spawnedPositions.Any(p => Vector3.DistanceSquared(p, pos) < 0.25f))
                            continue; // Skip duplicate powerup already spawned at this position
                        spawnedPositions.Add(pos);
                    }

                    pupIndex++;
                    string iconHieName = ResolvePowerupIconHie(lastTypeId, lastCommentName);
                    string cleanComment = lastCommentName.Replace(' ', '_').Replace('!', '_').Replace('.', '_');
                    string instanceId = $"Powerup_{pupIndex:D3}_{cleanComment}";

                    string? archivePath = _vfs.GetArchivePath(iconHieName);
                    string meshKey = string.IsNullOrEmpty(archivePath) ? iconHieName : $"{archivePath}#{iconHieName}";
                    int gltfMeshIdx = -1;
                    if (meshMap.TryGetValue(meshKey, out int cachedIdx))
                    {
                        gltfMeshIdx = cachedIdx;
                    }
                    else
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
                        rootNode.AddChild(propNodeIdx);

                        if (IsVerboseEnabled)
                        {
                            Log($"      [POWERUP PLACED] '{instanceId}' ({iconHieName}) -> Pos: ({px:F2}, {py:F2}, {pz:F2})", Services.LogLevel.Debug);
                        }
                    }
                }
            }
            return pupIndex;
        }

        private static string ResolvePowerupIconHie(int typeId, string name) =>
            TextureResolver.ResolvePowerupIconHie(typeId, name);

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

            byte[]? meshData = (!string.IsNullOrEmpty(archivePath) ? _vfs.LoadFileContext(meshName, archivePath) : null) ??
                               _vfs.LoadFileContext(meshName, _trackContext);
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

            var stream = new TriangleStream();
            MeshGeometryReader.AppendTriangles(subMesh, transform, stream);

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
            var texService = new TextureResolutionService(_vfs, _exportDir, _trackContext, _convertTexturesToPng);
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

        [JsonPropertyName("extensionsUsed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ExtensionsUsed { get; set; }

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }

        [JsonPropertyName("buffers")]
        public List<GltfBuffer> Buffers { get; set; } = new();
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
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool DoubleSided { get; set; }

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
