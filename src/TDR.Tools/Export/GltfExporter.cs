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

        private void Log(string msg) => _logger?.Invoke(msg);

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
                    int imgIdx = gltf.Images.Count;
                    gltf.Images.Add(new GltfImage { Uri = texFileName });
                    gltf.Textures.Add(new GltfTexture { Source = imgIdx });
                    mat.PbrMetallicRoughness.BaseColorTexture = new GltfTextureInfo { Index = imgIdx };
                }

                gltf.Materials.Add(mat);
                materialMap[texName] = matIdx;
                return matIdx;
            }

            // 1. Bake Static Level HIE Hierarchies
            int totalHies = assets.HieFiles.Count;
            for (int i = 0; i < totalHies; i++)
            {
                string hieName = assets.HieFiles[i];
                if (!IsHieSelected(hieName)) continue;

                int pct = (int)((float)(i + 1) / (totalHies + 1) * 80.0f);
                progressCallback?.Invoke(pct, $"Processing glTF mesh ({i + 1}/{totalHies}): {hieName}");

                byte[]? hieBytes = _vfs.LoadFileContext(hieName, _trackContext ?? levelName);
                if (hieBytes == null || hieBytes.Length == 0) continue;

                var hie = TDRHierarchy.Load(hieBytes, hieName);
                string? archivePath = _vfs.GetArchivePath(hieName);

                if (hie.Root != null)
                {
                    Matrix4x4 startMatrix = assets.HieInitialTransforms.TryGetValue(hieName, out var initMat) ? initMat : Matrix4x4.Identity;
                    Vector3? localOrigin = null;
                    if (_useLocalCoords && Matrix4x4.Decompose(hie.Root.Transform * startMatrix, out _, out _, out Vector3 rootPos))
                    {
                        localOrigin = rootPos;
                    }

                    int layerNodeIdx = AddHieNodeToGltf(hie.Root, startMatrix, hie, gltf, archivePath, bw, GetOrAddMaterial, meshMap, localOrigin);
                    rootNode.Children.Add(layerNodeIdx);
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
                        if (!IsHieSelected(hieName)) continue;

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

                        if (!meshMap.TryGetValue(hieName, out int gltfMeshIdx))
                        {
                            byte[]? hieData = _vfs.LoadFileContext(hieName, _trackContext ?? cleanTrackName);
                            if (hieData != null)
                            {
                                var hie = TDRHierarchy.Load(hieData, hieName);
                                string? archivePath = _vfs.GetArchivePath(hieName);
                                gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, GetOrAddMaterial);
                                if (gltfMeshIdx >= 0) meshMap[hieName] = gltfMeshIdx;
                            }
                        }

                        if (gltfMeshIdx >= 0)
                        {
                            int propNodeIdx = gltf.Nodes.Count;
                            var propNode = new GltfNode
                            {
                                Name = instanceId,
                                Mesh = gltfMeshIdx,
                                Translation = new[] { px, py, pz },
                                Rotation = new[] { qx, qy, qz, qw }
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

                foreach (string pName in pupNames)
                {
                    byte[]? pupData = _vfs.LoadFileContext(pName, _trackContext ?? cleanBaseTrack);
                    if (pupData != null)
                    {
                        AppendPowerupsToGltf(pupData, gltf, rootNode, meshMap, bw, cleanBaseTrack, GetOrAddMaterial);
                    }
                }
            }

            // Write .bin file
            byte[] binBytes = binStream.ToArray();
            File.WriteAllBytes(binPath, binBytes);
            gltf.Buffers.Add(new GltfBuffer { Uri = binFileName, ByteLength = binBytes.Length });

            var jsonOptions = new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            File.WriteAllText(outputGltfPath, JsonSerializer.Serialize(gltf, jsonOptions));

            Log($"    └─ glTF 2.0 Scene Saved: {Path.GetFileName(outputGltfPath)} ({gltf.Nodes.Count} nodes, {gltf.Meshes.Count} meshes)");
            return true;
        }

        private void AppendPowerupsToGltf(
            byte[] pupData,
            GltfManifest gltf,
            GltfNode rootNode,
            Dictionary<string, int> meshMap,
            BinaryWriter bw,
            string cleanTrackName,
            Func<string, string?, int> getOrAddMaterial)
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
                    if (!IsHieSelected(iconHieName)) continue;
                    string cleanComment = lastCommentName.Replace(' ', '_').Replace('!', '_').Replace('.', '_');
                    string instanceId = $"Powerup_{pupIndex:D3}_{cleanComment}";

                    if (!meshMap.TryGetValue(iconHieName, out int gltfMeshIdx))
                    {
                        byte[]? hieData = _vfs.LoadFileContext(iconHieName, _trackContext ?? cleanTrackName);
                        if (hieData != null)
                        {
                            var hie = TDRHierarchy.Load(hieData, iconHieName);
                            string? archivePath = _vfs.GetArchivePath(iconHieName);
                            gltfMeshIdx = BuildGltfMeshFromHie(hie, gltf, archivePath, bw, getOrAddMaterial);
                            if (gltfMeshIdx >= 0) meshMap[iconHieName] = gltfMeshIdx;
                        }
                    }

                    if (gltfMeshIdx >= 0)
                    {
                        int propNodeIdx = gltf.Nodes.Count;
                        var propNode = new GltfNode
                        {
                            Name = instanceId,
                            Mesh = gltfMeshIdx,
                            Translation = new[] { px, py, pz }
                        };
                        gltf.Nodes.Add(propNode);
                        rootNode.Children.Add(propNodeIdx);
                    }
                }
            }
        }

        private static string ResolvePowerupIconHie(int typeId, string name)
        {
            // If typeId comes in as raw VB Long uint32 representation (e.g. 1116733440), convert back to float ID
            if (typeId > 100000)
            {
                byte[] bytes = BitConverter.GetBytes(typeId);
                float floatVal = BitConverter.ToSingle(bytes, 0);
                if (!float.IsNaN(floatVal) && floatVal >= 0 && floatVal < 500)
                {
                    typeId = (int)Math.Round(floatVal);
                }
            }

            string lowerName = name.ToLowerInvariant();

            // 1. Mission / Quest / System Special Items (from official TDR2000.exe disassembly)
            if (lowerName.Contains("arrow")) return "ArrowArrow.hie";
            if (lowerName.Contains("bigbomb") || lowerName.Contains("big_bomb")) return "BIG_BOMBBomb.hie";
            if (lowerName.Contains("spike")) return "MortarTailSpike.hie";
            if (lowerName.Contains("bomb")) return "BombPiececube1.hie";
            if (lowerName.Contains("fuse")) return "fuseFuse_NULL.hie";
            if (lowerName.Contains("enginepart") || lowerName.Contains("engine_part")) return "EnginePartobj3.hie";
            if (lowerName.Contains("moneybag") || lowerName.Contains("money_bag")) return "DingablesMoneyBagPowerup.hie";
            if (lowerName.Contains("artillery") || lowerName.Contains("shell")) return "DingablesArtilleryShellPow.hie";
            if (lowerName.Contains("mortar")) return "mortarTail_Render.hie";
            if (lowerName.Contains("oil") || lowerName.Contains("drum")) return "Oil_DrumDrum_null.hie";

            if (lowerName.Contains("spanner") || lowerName.Contains("repair") || lowerName.Contains("fix"))
                return "newIconsSPANNER.hie";

            if (lowerName.Contains("cash") || lowerName.Contains("credit") || lowerName.Contains("money"))
                return "newIconsWADOCASH.hie";

            if (lowerName.Contains("time")) return "newIconsTIME.hie";

            if (lowerName.Contains("zombie") || lowerName.Contains("pedestrian") || lowerName.Contains("flamethrower") ||
                lowerName.Contains("ray") || lowerName.Contains("dismember"))
                return "newIconsPEDSIGN.hie";

            if (lowerName.Contains("armour") || lowerName.Contains("defense") || lowerName.Contains("helmet") || lowerName.Contains("invulnerability"))
                return "newIconsHELMET.hie";

            if (lowerName.Contains("fist") || lowerName.Contains("offensive") || lowerName.Contains("damage"))
                return "newIconsFIST.hie";

            if (lowerName.Contains("engine") || lowerName.Contains("turbo") || lowerName.Contains("burner") || lowerName.Contains("speed") || lowerName.Contains("hot rod"))
                return "newIconsENGINE.hie";

            if (lowerName.Contains("apo")) return "newIconsAPOall.hie";

            return typeId switch
            {
                1 or 29 or 30 => "newIconsHELMET.hie",
                2 or 24 or 27 or 28 or 80 => "newIconsENGINE.hie",
                3 or 36 or 43 => "newIconsFIST.hie",
                4 or 73 or 74 or 75 => "newIconsSPANNER.hie",
                5 or 70 or 71 or 72 => "newIconsWADOCASH.hie",
                64 or 68 => "newIconsTIME.hie",
                45 or 46 or 47 or 49 or 50 or 51 or 52 or 55 or 56 or 57 or 58 or 62 or 66 or 78 or 93 or 114 or 118 => "newIconsPEDSIGN.hie",
                94 => "mortarTail_Render.hie",
                92 => "Oil_DrumDrum_null.hie",
                _ => "newIconsSPANNER.hie"
            };
        }

        private int AddHieNodeToGltf(
            TDRNode node,
            Matrix4x4 parentMatrix,
            TDRHierarchy hie,
            GltfManifest gltf,
            string? archivePath,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial,
            Dictionary<string, int> meshMap,
            Vector3? localOrigin = null)
        {
            if (node == null) return -1;
            Matrix4x4 worldMatrix = node.Transform * parentMatrix;

            int nodeIdx = gltf.Nodes.Count;
            var gNode = new GltfNode { Name = $"{node.Name}_{node.ID}" };

            if (Matrix4x4.Decompose(worldMatrix, out Vector3 scale, out Quaternion rot, out Vector3 trans))
            {
                if (_useLocalCoords && localOrigin.HasValue && node == hie.Root)
                {
                    trans -= localOrigin.Value;
                }
                gNode.Translation = new[] { trans.X, trans.Y, trans.Z };
                gNode.Rotation = new[] { rot.X, rot.Y, rot.Z, rot.W };
                gNode.Scale = new[] { scale.X, scale.Y, scale.Z };
            }

            gltf.Nodes.Add(gNode);

            if (node.Type == TDRNode.NodeType.Mesh)
            {
                string? meshName = hie.Meshes.Count == 1 ? hie.Meshes[0] : (node.Index >= 0 && node.Index < hie.Meshes.Count ? hie.Meshes[node.Index] : null);
                if (meshName != null)
                {
                    int mIdx = BuildGltfMeshFromContainer(meshName, hie, gltf, archivePath, bw, getMaterial);
                    if (mIdx >= 0) gNode.Mesh = mIdx;
                }
            }

            if (node.Child >= 0 && node.Child < hie.Nodes.Count)
            {
                var childNode = hie.Nodes[node.Child];
                int childIdx = AddHieNodeToGltf(childNode, worldMatrix, hie, gltf, archivePath, bw, getMaterial, meshMap, localOrigin);
                if (childIdx >= 0) gNode.Children.Add(childIdx);
            }

            if (node.Sibling >= 0 && node.Sibling < hie.Nodes.Count)
            {
                var siblingNode = hie.Nodes[node.Sibling];
                AddHieNodeToGltf(siblingNode, parentMatrix, hie, gltf, archivePath, bw, getMaterial, meshMap, localOrigin);
            }

            return nodeIdx;
        }

        private int BuildGltfMeshFromHie(
            TDRHierarchy hie,
            GltfManifest gltf,
            string? archivePath,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial)
        {
            if (hie.Meshes.Count == 0) return -1;
            string meshName = hie.Meshes[0];
            return BuildGltfMeshFromContainer(meshName, hie, gltf, archivePath, bw, getMaterial);
        }

        private int BuildGltfMeshFromContainer(
            string meshName,
            TDRHierarchy hie,
            GltfManifest gltf,
            string? archivePath,
            BinaryWriter bw,
            Func<string, string?, int> getMaterial)
        {
            if (!_meshCache.TryGetValue(meshName, out var container))
            {
                byte[]? meshData = _vfs.LoadFileContext(meshName, _trackContext);
                if (meshData != null)
                {
                    container = MSHSContainer.Load(meshData, meshName);
                    _meshCache[meshName] = container;
                }
            }

            if (container == null) return -1;

            int meshIdx = gltf.Meshes.Count;
            var gMesh = new GltfMesh { Name = Path.GetFileNameWithoutExtension(meshName) };
            string defaultTex = hie.Textures.Count > 0 ? hie.Textures[0].Trim('"') : "Default";

            for (int i = 0; i < container.Meshes.Count; i++)
            {
                var subMesh = container.Meshes[i];
                if (subMesh.Vertices.Count == 0 || subMesh.Faces.Count == 0) continue;

                string texName = (i < hie.Textures.Count) ? hie.Textures[i].Trim('"') : defaultTex;
                int matIdx = getMaterial(texName, archivePath);

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

                foreach (var v in subMesh.Vertices)
                {
                    float x = v.Position.X, y = v.Position.Y, z = v.Position.Z;
                    bw.Write(x); bw.Write(y); bw.Write(z);

                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                    if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                }
                int posByteLength = (int)(bw.BaseStream.Position - posOffset);

                // 2. Write Normals
                long normOffset = bw.BaseStream.Position;
                foreach (var v in subMesh.Vertices)
                {
                    bw.Write(v.Normal.X); bw.Write(v.Normal.Y); bw.Write(v.Normal.Z);
                }
                int normByteLength = (int)(bw.BaseStream.Position - normOffset);

                // 3. Write UVs
                long uvOffset = bw.BaseStream.Position;
                foreach (var v in subMesh.Vertices)
                {
                    bw.Write(v.UV.X); bw.Write(1.0f - v.UV.Y);
                }
                int uvByteLength = (int)(bw.BaseStream.Position - uvOffset);

                // 4. Write Indices (Triangles)
                long idxOffset = bw.BaseStream.Position;
                int indexCount = 0;
                foreach (var f in subMesh.Faces)
                {
                    bw.Write((uint)f.V1);
                    bw.Write((uint)f.V2);
                    bw.Write((uint)f.V3);
                    indexCount += 3;
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

                // Register Accessors with REQUIRED MIN and MAX bounds for POSITION
                int posAccIdx = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor
                {
                    BufferView = posViewIdx,
                    ComponentType = 5126, // FLOAT
                    Count = subMesh.Vertices.Count,
                    Type = "VEC3",
                    Min = new[] { minX, minY, minZ },
                    Max = new[] { maxX, maxY, maxZ }
                });

                int normAccIdx = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor
                {
                    BufferView = normViewIdx,
                    ComponentType = 5126,
                    Count = subMesh.Vertices.Count,
                    Type = "VEC3"
                });

                int uvAccIdx = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor
                {
                    BufferView = uvViewIdx,
                    ComponentType = 5126,
                    Count = subMesh.Vertices.Count,
                    Type = "VEC2"
                });

                int idxAccIdx = gltf.Accessors.Count;
                gltf.Accessors.Add(new GltfAccessor
                {
                    BufferView = idxViewIdx,
                    ComponentType = 5125, // UNSIGNED_INT
                    Count = indexCount,
                    Type = "SCALAR"
                });

                // Primitive definition
                var prim = new GltfPrimitive
                {
                    Indices = idxAccIdx,
                    Material = matIdx >= 0 ? matIdx : null
                };
                prim.Attributes["POSITION"] = posAccIdx;
                prim.Attributes["NORMAL"] = normAccIdx;
                prim.Attributes["TEXCOORD_0"] = uvAccIdx;

                gMesh.Primitives.Add(prim);
            }

            gltf.Meshes.Add(gMesh);
            return meshIdx;
        }

        private string? ResolveTextureFile(string texName, string? archivePath)
        {
            var vfsFiles = _vfs.GetFiles();
            var match = vfsFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals(texName, StringComparison.OrdinalIgnoreCase));
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
                if (!File.Exists(pngPath))
                {
                    try
                    {
                        var bmp = TgaDecoder.DecodeTga(data);
                        if (bmp != null)
                        {
#pragma warning disable CS0618
                            bmp.Save(pngPath);
#pragma warning restore CS0618
                            return pngName;
                        }
                    }
                    catch
                    {
                        // Fallback to original tga if decode failed
                    }
                }
                else
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
        public int? Mesh { get; set; }

        [JsonPropertyName("translation")]
        public float[]? Translation { get; set; }

        [JsonPropertyName("rotation")]
        public float[]? Rotation { get; set; }

        [JsonPropertyName("scale")]
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
