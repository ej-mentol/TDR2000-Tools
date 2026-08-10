using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Utilities;

namespace TDR.Tools.Export
{
    public sealed class ObjExporter
    {
        private readonly PakManager _vfs;
        private readonly string _exportDir;
        private readonly bool _noMaterials;
        private readonly bool _useLocalCoords;
        private readonly bool _verbose;
        private readonly bool _useGrouping;
        private readonly bool _includeMovableProps;
        private readonly bool _enableGroundSnap;
        private readonly bool _convertTexturesToPng;
        private readonly string? _trackContext;
        private readonly Action<string>? _logger;
        private readonly HashSet<string>? _selectedHieFiles;
        private readonly Dictionary<string, MSHSContainer> _meshCache = new(StringComparer.OrdinalIgnoreCase);
        // Captured once per export (from the first mesh node encountered) when UseLocalCoords is on.
        // All subsequent mesh translations are offset by this value instead of being zeroed outright,
        // so meshes keep their position relative to each other — only the whole level shifts near origin.
        private Vector3? _localOrigin;

        public ObjExporter(PakManager vfs, string exportDir, bool noMaterials, bool useLocalCoords, bool verbose = false, bool useGrouping = true, bool includeMovableProps = true, string? trackContext = null, Action<string>? logger = null, bool enableGroundSnap = false, IEnumerable<string>? selectedHieFiles = null, bool convertTexturesToPng = true)
        {
            _vfs = vfs;
            _exportDir = exportDir;
            _noMaterials = noMaterials;
            _useLocalCoords = useLocalCoords;
            _verbose = verbose;
            _useGrouping = useGrouping;
            _includeMovableProps = includeMovableProps;
            _trackContext = trackContext;
            _logger = logger;
            _enableGroundSnap = enableGroundSnap;
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

        private void Log(string message)
        {
            _logger?.Invoke(message);
        }

        // Keywords that point directly to a .hie or a .txt sub-descriptor (Stage 1 in EXPORT_FORMAT.md)
        private static readonly string[] DirectHieKeywords = new[]
        {
            "SKY_SPHERE", "WATER_MESH", "HARDSHADOW_HIE", "BASE_CONSOFT", "CONSOFT",
            "LEVEL_MESH", "STATIC_MESH", "OCCLUDER_MESH", "TRACK_SELECT_MESH", "SPLASH_SCREEN_MESH"
        };

        // Keywords whose value is always a .txt sub-descriptor (Stage 2 in EXPORT_FORMAT.md)
        // Sub-descriptors contain raw .hie paths per line, NO keyword prefix.
        private static readonly string[] SubDescriptorKeywords = new[]
        {
            "STATIC_MESH_DESCRIPTOR", "BREAKABLES_DESCRIPTOR", "ANIMATED_PROPS",
            "CONSOFT_DESCRIPTOR", "LEVEL_CONSOFT", "ARTICULATED_BRIDGES", "LIGHTS_DESCRIPTOR",
            "DRONE_DESCRIPTOR", "SPECIAL_VOLUMES", "SPECIAL_VOLUMES_0"
        };

        public sealed class HieInstanceInfo
        {
            public string HieName { get; set; } = string.Empty;
            public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;
        }

        public sealed class DescriptorAssets
        {
            public List<string> HieFiles { get; } = new();
            public List<HieInstanceInfo> HieInstances { get; } = new();
            public List<string> MovableDescriptors { get; } = new();
            public List<string> PedestrianDescriptors { get; } = new();
            public Dictionary<string, Matrix4x4> HieInitialTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parses a sub-descriptor file (e.g. StaticMeshDescriptor.txt, BreakablesDescriptor.txt).
        /// Parses instance placements (X, Y, Z, QX, QY, QZ, QW) for trees, breakables, and props.
        /// </summary>
        private void ParseSubDescriptorHieFiles(byte[] descriptorBytes, HashSet<string> visitedDescriptors, DescriptorAssets assets, Matrix4x4 parentMatrix)
        {
            if (descriptorBytes == null || descriptorBytes.Length == 0) return;

            string text = Encoding.ASCII.GetString(descriptorBytes);
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine;
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line[..commentIdx];
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                string[] tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                string entry = tokens[0].Trim('"');
                if (string.IsNullOrWhiteSpace(entry)) continue;

                Matrix4x4 localTransform = Matrix4x4.Identity;
                int coordStart = -1;
                for (int i = 1; i <= tokens.Length - 3; i++)
                {
                    if (float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                        float.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                        float.TryParse(tokens[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        coordStart = i;
                        break;
                    }
                }

                if (coordStart >= 1)
                {
                    float.TryParse(tokens[coordStart], NumberStyles.Float, CultureInfo.InvariantCulture, out float px);
                    float.TryParse(tokens[coordStart + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float py);
                    float.TryParse(tokens[coordStart + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz);

                    Matrix4x4 rotMat = Matrix4x4.Identity;
                    if (coordStart + 6 < tokens.Length &&
                        float.TryParse(tokens[coordStart + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float qx) &&
                        float.TryParse(tokens[coordStart + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out float qy) &&
                        float.TryParse(tokens[coordStart + 5], NumberStyles.Float, CultureInfo.InvariantCulture, out float qz) &&
                        float.TryParse(tokens[coordStart + 6], NumberStyles.Float, CultureInfo.InvariantCulture, out float qw))
                    {
                        var q = new Quaternion(qx, qy, qz, qw);
                        rotMat = Matrix4x4.CreateFromQuaternion(q);
                    }

                    localTransform = rotMat * Matrix4x4.CreateTranslation(px, py, pz);
                }

                Matrix4x4 worldTransform = localTransform * parentMatrix;

                if (entry.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                {
                    if (!assets.HieFiles.Contains(entry, StringComparer.OrdinalIgnoreCase))
                        assets.HieFiles.Add(entry);

                    if (coordStart >= 1)
                    {
                        assets.HieInstances.Add(new HieInstanceInfo
                        {
                            HieName = entry,
                            Transform = worldTransform
                        });
                    }
                }
                else if (entry.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(entry))
                {
                    byte[]? subBytes = _vfs.LoadFileContext(entry, _trackContext);
                    if (subBytes != null && subBytes.Length > 0)
                    {
                        ParseSubDescriptorHieFiles(subBytes, visitedDescriptors, assets, worldTransform);
                    }
                }
            }
        }

        /// <summary>
        /// Parses a master level descriptor (e.g. Hollowood.txt). Applies exact first-token
        /// keyword filtering as documented in EXPORT_FORMAT.md.
        /// </summary>
        public DescriptorAssets ParseLevelDescriptorAssets(byte[] descriptorBytes, HashSet<string>? visitedDescriptors = null)
        {
            visitedDescriptors ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new DescriptorAssets();
            if (descriptorBytes == null || descriptorBytes.Length == 0) return result;

            string text = Encoding.ASCII.GetString(descriptorBytes);
            string? pendingWaterMesh = null;
            float? waterLevel = null;

            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine;
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line[..commentIdx];
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                string[] tokens = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;

                string firstToken = tokens[0].ToUpperInvariant();
                string secondToken = tokens[1].Trim('"');

                if (firstToken.Equals("WATER_LEVEL", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(secondToken, NumberStyles.Any, CultureInfo.InvariantCulture, out float wL))
                    {
                        waterLevel = wL;
                    }
                    continue;
                }

                if (firstToken.Equals("WATER_MESH", StringComparison.OrdinalIgnoreCase))
                {
                    pendingWaterMesh = secondToken;
                }

                // Stage 3: MOVABLE_OBJECTS & PEDESTRIAN_PLACEMENT → placement descriptor .txt
                if (firstToken.Equals("MOVABLE_OBJECTS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(secondToken) && !result.MovableDescriptors.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                        result.MovableDescriptors.Add(secondToken);
                    continue;
                }

                if (firstToken.Equals("PEDS_DESCRIPTOR", StringComparison.OrdinalIgnoreCase) ||
                    firstToken.Equals("PEDESTRIAN_PLACEMENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(secondToken) && !result.PedestrianDescriptors.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                        result.PedestrianDescriptors.Add(secondToken);
                    continue;
                }

                // Stage 1: Direct .hie or .txt (for BASE_CONSOFT, LEVEL_MESH, STATIC_MESH, etc.)
                if (DirectHieKeywords.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
                {
                    if (secondToken.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!result.HieFiles.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                            result.HieFiles.Add(secondToken);
                    }
                    else if (secondToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(secondToken))
                    {
                        byte[]? subBytes = _vfs.LoadFileContext(secondToken, _trackContext);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            ParseSubDescriptorHieFiles(subBytes, visitedDescriptors, result, Matrix4x4.Identity);
                        }
                    }
                    continue;
                }

                // Stage 2: Sub-descriptor keywords → always .txt, parsed without keyword filter
                if (SubDescriptorKeywords.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
                {
                    if (secondToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(secondToken))
                    {
                        byte[]? subBytes = _vfs.LoadFileContext(secondToken, _trackContext);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            ParseSubDescriptorHieFiles(subBytes, visitedDescriptors, result, Matrix4x4.Identity);
                        }
                    }
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(pendingWaterMesh) && waterLevel.HasValue)
            {
                result.HieInitialTransforms[pendingWaterMesh] = Matrix4x4.CreateTranslation(0, waterLevel.Value, 0);
            }

            // Discover variant Consoft / Checkpoint HIE files matching track context (e.g. hollowoodRace1Consoft.hie)
            string activeTrackContext = (_trackContext ?? string.Empty).ToLowerInvariant();
            string cleanTrackKey = activeTrackContext.Replace("_", "");
            bool isBaseTrackOnly = !activeTrackContext.Contains("race") && !activeTrackContext.Contains("mission") && !activeTrackContext.Contains("all");

            if (!string.IsNullOrEmpty(cleanTrackKey))
            {
                foreach (var file in _vfs.GetFiles())
                {
                    if (file.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        string fn = Path.GetFileNameWithoutExtension(file.Name).ToLowerInvariant();
                        if (fn.Contains("consoft") || fn.Contains("checkpoint"))
                        {
                            // If Base Track Only is requested, skip variant-specific Consoft files (e.g. race1, race2, mission1)
                            if (isBaseTrackOnly && (fn.Contains("race") || fn.Contains("mission")))
                                continue;

                            string normArchive = (file.ArchivePath ?? "").ToLowerInvariant().Replace("_", "");
                            if (fn.Contains(cleanTrackKey) || normArchive.Contains(cleanTrackKey))
                            {
                                if (IsHieSelected(file.Name) && !result.HieFiles.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
                                {
                                    result.HieFiles.Add(file.Name);
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        public TrackExportResult ExportLevelToObj(byte[] levelData, string levelName, string outputObjPath, Action<int, string>? progressCallback = null)
        {
            _localOrigin = null; // fresh origin capture for this combined level export

            var result = new TrackExportResult
            {
                TrackName = levelName,
                OutputDirectory = Path.GetDirectoryName(outputObjPath) ?? _exportDir
            };

            var assets = ParseLevelDescriptorAssets(levelData);
            if (assets.HieFiles.Count == 0 && assets.MovableDescriptors.Count == 0 && assets.PedestrianDescriptors.Count == 0)
            {
                if (_verbose)
                    Log($"    [!] Warning: No valid HIE hierarchy or Movable references found in descriptor '{levelName}'.");
                return result;
            }

            if (_verbose)
            {
                Log($"[+] Level Descriptor '{levelName}': Discovered {assets.HieFiles.Count} HIE reference(s), {assets.MovableDescriptors.Count} Movable descriptor(s), {assets.PedestrianDescriptors.Count} Pedestrian descriptor(s)");
            }

            string mtlPath = Path.ChangeExtension(outputObjPath, ".mtl");
            string tempObj = outputObjPath + ".tmp";
            var textures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            int v = 1, vt = 1, vn = 1;

            using (var w = new StreamWriter(tempObj, false, Encoding.ASCII))
            {
                w.WriteLine($"# TDR2000 Combined Scene Export - {levelName}");
                w.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");

                // 1. Export ALL HIE Mesh Hierarchies & Sub-descriptor Instances into the combined stream
                var processedHies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                if (assets.HieInstances.Count > 0)
                {
                    int totalInst = assets.HieInstances.Count;
                    for (int i = 0; i < totalInst; i++)
                    {
                        var inst = assets.HieInstances[i];
                        if (!IsHieSelected(inst.HieName)) continue;

                        int pct = (int)((float)(i + 1) / (totalInst + assets.HieFiles.Count + 1) * 70.0f);
                        progressCallback?.Invoke(pct, $"Baking OBJ mesh ({i + 1}/{totalInst}): {inst.HieName}");

                        byte[]? hieBytes = _vfs.LoadFileContext(inst.HieName, _trackContext ?? levelName);
                        if (hieBytes != null && hieBytes.Length > 0)
                        {
                            string? sourceArchivePath = _vfs.GetArchivePath(inst.HieName);
                            if (_verbose) Log($"  [+] Baking HIE instance '{inst.HieName}' at position into combined scene...");

                            AppendHieToWriter(hieBytes, inst.HieName, w, textures, ref v, ref vt, ref vn, sourceArchivePath, inst.Transform);
                            processedHies.Add(inst.HieName);
                            if (!result.ResolvedHieFiles.Contains(inst.HieName, StringComparer.OrdinalIgnoreCase))
                                result.ResolvedHieFiles.Add(inst.HieName);
                        }
                    }
                }

                int totalHies = assets.HieFiles.Count;
                for (int i = 0; i < totalHies; i++)
                {
                    string hieName = assets.HieFiles[i];
                    if (processedHies.Contains(hieName) || !IsHieSelected(hieName)) continue;

                    int pct = (int)(70.0f + (float)(i + 1) / (totalHies + 1) * 20.0f);
                    progressCallback?.Invoke(pct, $"Baking OBJ mesh layer ({i + 1}/{totalHies}): {hieName}");

                    byte[]? hieBytes = _vfs.LoadFileContext(hieName, _trackContext ?? levelName);
                    if (hieBytes != null && hieBytes.Length > 0)
                    {
                        string? sourceArchivePath = _vfs.GetArchivePath(hieName);
                        if (_verbose) Log($"  [+] Baking HIE layer '{hieName}' into combined scene...");

                        Matrix4x4? initMat = assets.HieInitialTransforms.TryGetValue(hieName, out var im) ? im : null;
                        AppendHieToWriter(hieBytes, hieName, w, textures, ref v, ref vt, ref vn, sourceArchivePath, initMat);
                        result.ResolvedHieFiles.Add(hieName);
                    }
                    else if (_verbose)
                    {
                        Log($"    [!] Warning: HIE hierarchy '{hieName}' not found in VFS for level '{levelName}'.");
                    }
                }

                string cleanTrackName = TrackDiscovery.GetBaseTrackName(levelName);

                // 2. Bake Movable Objects into the combined stream (if enabled)
                if (_includeMovableProps)
                {
                    var movDescs = new List<string>(assets.MovableDescriptors);
                    string defaultMov = $"{cleanTrackName}_MoveableDescriptor.txt";
                    if (movDescs.Count == 0 && _vfs.FileExists(defaultMov))
                    {
                        movDescs.Add(defaultMov);
                    }

                    // Extract base terrain triangles from both direct HIE files and sub-descriptor HIE instances
                    var baseTriangles = new List<GroundSnapUtil.Triangle>();
                    var snapHies = new HashSet<string>(assets.HieFiles, StringComparer.OrdinalIgnoreCase);
                    foreach (var inst in assets.HieInstances) snapHies.Add(inst.HieName);

                    foreach (string hieName in snapHies)
                    {
                        if (hieName.Contains("skysphere", StringComparison.OrdinalIgnoreCase) || hieName.Contains("sky", StringComparison.OrdinalIgnoreCase))
                            continue;

                        byte[]? hieBytes = _vfs.LoadFileContext(hieName, _trackContext ?? levelName);
                        if (hieBytes != null && hieBytes.Length > 0)
                        {
                            var hie = TDRHierarchy.Load(hieBytes, hieName);
                            var tris = GroundSnapUtil.ExtractBaseTriangles(hie, (path) => _vfs.LoadFileContext(path, _trackContext ?? levelName));
                            if (tris.Count > 0) baseTriangles.AddRange(tris);
                        }
                    }

                    // 2. Bake Movable Objects descriptors into the combined scene (Cumulative Base + Variant)
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

                    foreach (string movDesc in allMovDescs)
                    {
                        if (_verbose) Log($"[+] Baking Movable Objects descriptor '{movDesc}' into combined scene...");
                        byte[]? movData = _vfs.LoadFileContext(movDesc, _trackContext ?? cleanTrackName);
                        if (movData != null)
                        {
                            AppendMovablesToWriter(movData, w, textures, ref v, ref vt, ref vn, (path) => _vfs.LoadFileContext(path, _trackContext ?? cleanTrackName), baseTriangles);
                        }
                    }

                    // 3. Bake ALL Powerup Files (.pup) into the combined scene (Base Track .pup + Variant .pup + Race1 .pup)
                    var pupNames = new List<string>();
                    string basePup = $"{cleanTrackName}.pup";
                    if (_vfs.FileExists(basePup)) pupNames.Add(basePup);

                    string varPup = $"{levelName}.pup";
                    if (!varPup.Equals(basePup, StringComparison.OrdinalIgnoreCase) && _vfs.FileExists(varPup))
                        pupNames.Add(varPup);

                    string race1Pup = $"{cleanTrackName}_Race1.pup";
                    if (!pupNames.Contains(race1Pup, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(race1Pup))
                        pupNames.Add(race1Pup);

                    foreach (string pName in pupNames)
                    {
                        byte[]? pupData = _vfs.LoadFileContext(pName, _trackContext ?? cleanTrackName);
                        if (pupData != null)
                        {
                            if (_verbose) Log($"[+] Baking Powerup Objects (.pup) '{pName}' into combined scene...");
                            AppendPowerupsToWriter(pupData, w, textures, ref v, ref vt, ref vn, (path) => _vfs.LoadFileContext(path, _trackContext ?? cleanTrackName));
                        }
                    }

                    // 4. Bake Pedestrian Placement Descriptors into the combined scene
                    var pedDescs = new List<string>(assets.PedestrianDescriptors);
                    string defaultPed = $"{cleanTrackName}_Ped_Placement.txt";
                    if (!pedDescs.Contains(defaultPed, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(defaultPed))
                    {
                        pedDescs.Add(defaultPed);
                    }

                    foreach (string pedDesc in pedDescs)
                    {
                        byte[]? pedData = _vfs.LoadFileContext(pedDesc, _trackContext ?? cleanTrackName);
                        if (pedData != null)
                        {
                            if (_verbose) Log($"[+] Baking Pedestrian Placement descriptor '{pedDesc}' into combined scene...");
                            AppendPedestriansToWriter(pedData, w, textures, ref v, ref vt, ref vn, (path) => _vfs.LoadFileContext(path, _trackContext ?? cleanTrackName));
                        }
                    }
                }
            }

            WriteMtlFile(mtlPath, textures);
            if (File.Exists(outputObjPath)) File.Delete(outputObjPath);
            File.Move(tempObj, outputObjPath);

            string fn = Path.GetFileName(outputObjPath);
            result.ProducedObjFiles.Add(fn);
            result.BaseMeshFileName = fn;

            string cleanTrack = TrackDiscovery.GetBaseTrackName(levelName);
            string waterCandidate = $"{cleanTrack}Water.obj";
            if (File.Exists(Path.Combine(result.OutputDirectory, waterCandidate)))
            {
                result.WaterMeshFileName = waterCandidate;
            }
            string skyCandidate = "FilmSkysphereStudio.obj";
            if (File.Exists(Path.Combine(result.OutputDirectory, skyCandidate)))
            {
                result.SkyMeshFileName = skyCandidate;
            }

            result.Success = File.Exists(outputObjPath) && new FileInfo(outputObjPath).Length > 0;
            return result;
        }

        private void AppendHieToWriter(byte[] hieBytes, string hieName, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, string? sourceArchivePath, Matrix4x4? initialTransform = null)
        {
            if (_useLocalCoords)
            {
                _localOrigin = null; // Reset local origin per-HIE hierarchy so each .hie calculates its own root origin
            }

            var hie = TDRHierarchy.Load(hieBytes, hieName);
            if (hie.Root == null && hie.Meshes.Count == 0) return;

            Matrix4x4 startMatrix = initialTransform ?? Matrix4x4.Identity;

            if (_useGrouping)
            {
                w.WriteLine($"o {Path.GetFileNameWithoutExtension(hieName)}");
            }

            if (hie.Root != null)
            {
                string defaultTex = "Default";
                ProcessNode(hie.Root, startMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, sourceArchivePath);
            }
            else if (hie.Meshes.Count > 0)
            {
                string defaultTex = hie.Textures.Count > 0 ? hie.Textures[0].Trim('"') : "Default";
                foreach (var meshName in hie.Meshes)
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

                    if (container != null)
                    {
                        for (int i = 0; i < container.Meshes.Count; i++)
                        {
                            var subMesh = container.Meshes[i];
                            string subTex = (i < hie.Textures.Count) ? hie.Textures[i].Trim('"') : defaultTex;

                            w.WriteLine($"usemtl {subTex}");
                            textures.TryAdd(subTex, sourceArchivePath);

                            WriteSubMesh(subMesh, Matrix4x4.Identity, w, ref v, ref vt, ref vn);
                        }
                    }
                }
            }
        }

        public void ExportHieToObj(byte[] hieData, string hieName, string outputObjPath, string? sourceArchivePath = null, bool resetOrigin = true)
        {
            if (resetOrigin) _localOrigin = null; // fresh origin capture for standalone single HIE export

            var hie = TDRHierarchy.Load(hieData, hieName);
            if (hie.Root == null && hie.Meshes.Count == 0)
            {
                if (_verbose) Log($"    [HIE Parser] '{hieName}' is empty (0 nodes, 0 meshes) — SKIPPED");
                return;
            }

            if (_verbose)
                Log($"    [HIE Parser] '{hieName}': Nodes={hie.Nodes.Count}, Meshes={hie.Meshes.Count}, Textures={hie.Textures.Count}");

            string mtlPath = Path.ChangeExtension(outputObjPath, ".mtl");
            string tempObj = outputObjPath + ".tmp";
            // textureName → archivePath where that texture should be searched first
            var textures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            int v = 1, vt = 1, vn = 1;

            using (var w = new StreamWriter(tempObj))
            {
                w.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");
                if (_useGrouping)
                {
                    w.WriteLine($"o {Path.GetFileNameWithoutExtension(hieName)}");
                }
                if (hie.Root != null)
                {
                    if (_verbose) Log($"      [HIE Tree] Traversing node hierarchy from Root...");
                    string defaultTex = "Default";
                    ProcessNode(hie.Root, Matrix4x4.Identity, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, sourceArchivePath);
                }
                else if (hie.Meshes.Count > 0)
                {
                    if (_verbose) Log($"      [HIE Direct] No root node tree — rendering {hie.Meshes.Count} direct mesh(es)...");
                    string defaultTex = hie.Textures.Count > 0 ? hie.Textures[0].Trim('"') : "Default";
                    foreach (var meshName in hie.Meshes)
                    {
                        if (!_meshCache.TryGetValue(meshName, out var container))
                        {
                            byte[]? meshData = _vfs.LoadFileContext(meshName, _trackContext);
                            if (meshData != null)
                            {
                                container = MSHSContainer.Load(meshData, meshName);
                                _meshCache[meshName] = container;
                                if (_verbose)
                                {
                                    int totalV = container.Meshes.Sum(m => m.VertexCount > 0 ? m.VertexCount : m.Vertices.Count);
                                    int totalF = container.Meshes.Sum(m => m.FaceCount);
                                    Log($"        [MSHS Parser] Loaded '{meshName}': {container.Meshes.Count} submesh(es), {totalV} verts, {totalF} faces");
                                }
                            }
                            else if (_verbose)
                            {
                                Log($"        [MSHS Parser] MISS: Could not locate '{meshName}' in VFS!");
                            }
                        }

                        if (container != null)
                        {
                            w.WriteLine($"o {Path.GetFileNameWithoutExtension(meshName)}");
                            for (int i = 0; i < container.Meshes.Count; i++)
                            {
                                var subMesh = container.Meshes[i];
                                string subTex = (i < hie.Textures.Count) ? hie.Textures[i].Trim('"') : defaultTex;

                                w.WriteLine($"usemtl {subTex}");
                                textures.TryAdd(subTex, sourceArchivePath);

                                WriteSubMesh(subMesh, Matrix4x4.Identity, w, ref v, ref vt, ref vn);
                            }
                        }
                    }
                }
            }

            if (v > 1)
            {
                WriteMtlFile(mtlPath, textures);
                if (File.Exists(outputObjPath)) File.Delete(outputObjPath);
                File.Move(tempObj, outputObjPath);
                Log($"  [+] {Path.GetFileNameWithoutExtension(outputObjPath)} -> {v - 1} verts, {textures.Count} textures");
            }
            else
            {
                if (_verbose) Log($"    [OBJ Export] '{Path.GetFileName(outputObjPath)}' yielded 0 vertices — temp file cleaned");
                if (File.Exists(tempObj)) File.Delete(tempObj);
            }
        }

        private void AppendMovablesToWriter(byte[] movData, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, Func<string, byte[]?> loader, List<GroundSnapUtil.Triangle>? baseTriangles = null)
        {
            string text = Encoding.ASCII.GetString(movData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var instanceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in lines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8) continue;

                string hieName = parts[0].Trim('"');
                if (!hieName.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    hieName += ".hie";

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

                // EXPERIMENTAL GROUND SNAP BEGIN
                if (_enableGroundSnap && baseTriangles != null && baseTriangles.Count > 0)
                {
                    Vector3 origPos = new Vector3(px, py, pz);
                    Vector3 snappedPos = GroundSnapUtil.SnapPointToSurface(origPos, baseTriangles, maxDropDistance: 25.0f, rayStartHeight: 10.0f);
                    px = snappedPos.X;
                    py = snappedPos.Y;
                    pz = snappedPos.Z;
                }
                // EXPERIMENTAL GROUND SNAP END

                Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, qw));
                Matrix4x4 worldMatrix = rotation with { M41 = px, M42 = py, M43 = pz };

                string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                int instIdx = instanceCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                instanceCounts[modelBaseName] = instIdx;
                string instanceId = $"{modelBaseName}_{instIdx:D3}";

                if (_useGrouping)
                {
                    w.WriteLine($"o {instanceId}");
                    w.WriteLine($"# Class: {hieName}");
                    w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                    w.WriteLine($"# Quaternion: {F(qx)} {F(qy)} {F(qz)} {F(qw)}");
                }

                byte[]? hieData = loader(hieName);
                if (hieData == null) continue;
                string? movableArchive = _vfs.GetArchivePath(hieName);
                try
                {
                    var hie = TDRHierarchy.Load(hieData, hieName);
                    if (hie.Root != null)
                    {
                        string defaultTex = "Default";
                        ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, movableArchive);
                    }
                }
                catch { }
            }
        }

        private void AppendPowerupsToWriter(byte[] pupData, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, Func<string, byte[]?> loader)
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
                    Matrix4x4 worldMatrix = Matrix4x4.CreateTranslation(px, py, pz);
                    string cleanComment = lastCommentName.Replace(' ', '_').Replace('!', '_').Replace('.', '_');
                    string instanceId = $"Powerup_{pupIndex:D3}_{cleanComment}";

                    if (_useGrouping)
                    {
                        w.WriteLine($"o {instanceId}");
                        w.WriteLine($"# TypeID: {lastTypeId} ({lastCommentName})");
                        w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                    }

                    byte[]? hieData = loader(iconHieName);
                    if (hieData != null)
                    {
                        string? movableArchive = _vfs.GetArchivePath(iconHieName);
                        try
                        {
                            var hie = TDRHierarchy.Load(hieData, iconHieName);
                            if (hie.Root != null)
                            {
                                string defaultTex = "Default";
                                ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, movableArchive);
                            }
                        }
                        catch { }
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

            // 2. Repair & Spanner -> Spanner Icon (Check early before engine/powerup strings!)
            if (lowerName.Contains("spanner") || lowerName.Contains("repair") || lowerName.Contains("fix"))
                return "newIconsSPANNER.hie";

            // 3. Money & Cash -> Wadocash Icon
            if (lowerName.Contains("cash") || lowerName.Contains("credit") || lowerName.Contains("money"))
                return "newIconsWADOCASH.hie";

            // 4. Time Bonus -> Time Icon
            if (lowerName.Contains("time")) return "newIconsTIME.hie";

            // 5. Pedestrian Powers & Ray Weapons -> Pedestrian Sign Icon
            if (lowerName.Contains("zombie") || lowerName.Contains("pedestrian") || lowerName.Contains("flamethrower") ||
                lowerName.Contains("ray") || lowerName.Contains("dismember"))
                return "newIconsPEDSIGN.hie";

            // 6. Armor & Defense -> Helmet Icon
            if (lowerName.Contains("armour") || lowerName.Contains("defense") || lowerName.Contains("helmet") || lowerName.Contains("invulnerability"))
                return "newIconsHELMET.hie";

            // 7. Offense & Fist -> Fist Icon
            if (lowerName.Contains("fist") || lowerName.Contains("offensive") || lowerName.Contains("damage"))
                return "newIconsFIST.hie";

            // 8. Engine & Speed -> Engine Icon (Do NOT match generic "powerup" string!)
            if (lowerName.Contains("engine") || lowerName.Contains("turbo") || lowerName.Contains("burner") || lowerName.Contains("speed") || lowerName.Contains("hot rod"))
                return "newIconsENGINE.hie";

            // 9. APO All
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
        public void ExportMovablesToObj(string descPath, Func<string, byte[]?> loader)
        {
            byte[]? data = loader(descPath);
            if (data == null)
            {
                Log($"  [?] Movables descriptor not found: {descPath}");
                return;
            }

            string[] lines = Encoding.ASCII.GetString(data)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            string objPath = Path.Combine(_exportDir, Path.GetFileNameWithoutExtension(descPath) + "_movables.obj");
            string mtlPath = Path.ChangeExtension(objPath, ".mtl");
            string tempObj = objPath + ".tmp";

            int v = 1, vt = 1, vn = 1;
            var textures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var instanceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using (var w = new StreamWriter(tempObj))
            {
                w.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");

                foreach (string line in lines)
                {
                    string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;

                    string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 8) continue;

                    if (_verbose) Log($"  [MOV] Line: {clean}");
                    string hieName = parts[0].Trim('"');
                    if (!hieName.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                        hieName += ".hie";

                    float px = float.Parse(parts[1], CultureInfo.InvariantCulture);
                    float py = float.Parse(parts[2], CultureInfo.InvariantCulture);
                    float pz = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    float qx = float.Parse(parts[4], CultureInfo.InvariantCulture);
                    float qy = float.Parse(parts[5], CultureInfo.InvariantCulture);
                    float qz = float.Parse(parts[6], CultureInfo.InvariantCulture);
                    float qw = float.Parse(parts[7], CultureInfo.InvariantCulture);

                    Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, qw));
                    Matrix4x4 worldMatrix = _useLocalCoords ? rotation : rotation with
                    {
                        M41 = px, M42 = py, M43 = pz
                    };

                    string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                    int instIdx = instanceCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                    instanceCounts[modelBaseName] = instIdx;
                    string instanceId = $"{modelBaseName}_{instIdx:D3}";

                    if (_useGrouping)
                    {
                        w.WriteLine($"o {instanceId}");
                        w.WriteLine($"# Class: {hieName}");
                        w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                        w.WriteLine($"# Quaternion: {F(qx)} {F(qy)} {F(qz)} {F(qw)}");
                    }
                    else if (_useLocalCoords)
                    {
                        w.WriteLine($"# WorldPos {F(px)} {F(py)} {F(pz)}");
                    }

                    if (_verbose) Log($"    [MOV] Requesting HIE: {hieName} (Instance: {instanceId})");
                    byte[]? hieData = loader(hieName);
                    if (hieData == null)
                    {
                        Log($"  [?] {hieName} (not found)");
                        continue;
                    }
                    string? movableArchive = _vfs.GetArchivePath(hieName);
                    if (_verbose) Log($"    [MOV] HIE loaded OK : {hieName} ({hieData.Length} bytes), archive: {movableArchive ?? "loose/unknown"}");
                    try
                    {
                        var hie = TDRHierarchy.Load(hieData, hieName);
                        if (_verbose)
                        {
                            Log($"      [HIE] Meshes={hie.Meshes.Count}  Nodes={hie.Nodes.Count}  Textures={hie.Textures.Count}  Matrices={hie.Matrices.Count}");
                            foreach (var nd in hie.Nodes)
                                Log($"      [NODE] Type={nd.Type}({(int)nd.Type})  Index={nd.Index}  Child={nd.Child}  Sib={nd.Sibling}");
                            foreach (var mref in hie.Meshes)
                                Log($"      [MESHREF] '{mref}'");
                        }
                        if (hie.Root != null)
                        {
                            string defaultTex = "Default";
                        ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, movableArchive);
                        }
                        else if (hie.Meshes.Count > 0)
                        {
                            string defaultTex = hie.Textures.Count > 0 ? hie.Textures[0].Trim('"') : "Default";
                            int subMeshIdx = 1;
                            foreach (var meshName in hie.Meshes)
                            {
                                if (!_meshCache.TryGetValue(meshName, out var container))
                                {
                                    byte[]? meshData = loader(meshName);
                                    if (meshData != null)
                                    {
                                        container = MSHSContainer.Load(meshData, meshName);
                                        _meshCache[meshName] = container;
                                    }
                                }

                                if (container != null)
                                {
                                    if (_useGrouping) w.WriteLine($"g Part_{subMeshIdx++}");
                                    for (int i = 0; i < container.Meshes.Count; i++)
                                    {
                                        var subMesh = container.Meshes[i];
                                        string subTex = (i < hie.Textures.Count) ? hie.Textures[i].Trim('"') : defaultTex;

                                        w.WriteLine($"usemtl {subTex}");
                                        textures.TryAdd(subTex, movableArchive);

                                        WriteSubMesh(subMesh, worldMatrix, w, ref v, ref vt, ref vn);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_verbose) Log($"  [!] Error parsing HIE '{hieName}': {ex.Message}");
                    }
                }
            }

            WriteMtlFile(mtlPath, textures);
            if (File.Exists(objPath)) File.Delete(objPath);
            File.Move(tempObj, objPath);
            Log($"  [+] Movables -> {Path.GetFileName(objPath)}");
        }

        public void ExportPedestriansToObj(string pedPlacementPath, string? pedDescPath, Func<string, byte[]?> loader)
        {
            byte[]? pedData = loader(pedPlacementPath);
            if (pedData == null) return;

            var pedClasses = new List<string>();
            if (!string.IsNullOrWhiteSpace(pedDescPath))
            {
                byte[]? pedDescData = loader(pedDescPath);
                if (pedDescData != null)
                {
                    string[] descLines = Encoding.ASCII.GetString(pedDescData).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string l in descLines)
                    {
                        string c = l.Contains("//") ? l[..l.IndexOf("//")].Trim() : l.Trim();
                        if (string.IsNullOrWhiteSpace(c)) continue;
                        string[] tok = c.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tok.Length > 0 && tok[0].EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                        {
                            pedClasses.Add(Path.GetFileNameWithoutExtension(tok[0].Trim('"')));
                        }
                    }
                }
            }

            string outName = Path.GetFileNameWithoutExtension(pedPlacementPath) + "_pedestrians";
            string objPath = Path.Combine(_exportDir, outName + ".obj");
            string mtlPath = Path.Combine(_exportDir, outName + ".mtl");
            string tempObj = objPath + ".tmp";

            var textures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int v = 1, vt = 1, vn = 1;

            using (var w = new StreamWriter(tempObj, false, Encoding.ASCII))
            {
                w.WriteLine($"# TDR2000 Pedestrians OBJ Export");
                w.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");

                string[] lines = Encoding.ASCII.GetString(pedData).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string line in lines)
                {
                    string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;

                    string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 7 || parts[0] != "1") continue;

                    int classId = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    float px = float.Parse(parts[3], CultureInfo.InvariantCulture);
                    float py = float.Parse(parts[4], CultureInfo.InvariantCulture);
                    float pz = float.Parse(parts[5], CultureInfo.InvariantCulture);
                    float heading = float.Parse(parts[6], CultureInfo.InvariantCulture);

                    string className = classId >= 0 && classId < pedClasses.Count ? pedClasses[classId] : $"Pedestrian_Class_{classId}";
                    int instIdx = counts.GetValueOrDefault(className, 0) + 1;
                    counts[className] = instIdx;

                    string instanceId = $"{className}_{instIdx:D3}";
                    Matrix4x4 worldMatrix = Matrix4x4.CreateRotationY(heading * (float)(Math.PI / 180.0)) * Matrix4x4.CreateTranslation(px, py, pz);

                    if (_useGrouping)
                    {
                        w.WriteLine($"o {instanceId}");
                        w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                        w.WriteLine($"# Heading: {F(heading)}");
                    }

                    bool exportedMesh = false;
                    string hieName = $"{className}.hie";
                    byte[]? hieData = loader(hieName);
                    if (hieData != null)
                    {
                        try
                        {
                            var hie = TDRHierarchy.Load(hieData, hieName);
                            string? archivePath = _vfs.GetArchivePath(hieName);
                            if (hie.Root != null)
                            {
                                string defaultTex = "Default";
                                ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, archivePath);
                                exportedMesh = true;
                            }
                        }
                        catch { }
                    }

                    if (!exportedMesh)
                    {
                        // Write 3D Standing Character Proxy Box (0.5m x 1.8m x 0.5m)
                        float hw = 0.25f;
                        float h = 1.80f;
                        float hd = 0.25f;

                        Vector3[] localBox = new[]
                        {
                            new Vector3(-hw, 0, -hd), new Vector3( hw, 0, -hd),
                            new Vector3( hw, 0,  hd), new Vector3(-hw, 0,  hd),
                            new Vector3(-hw, h, -hd), new Vector3( hw, h, -hd),
                            new Vector3( hw, h,  hd), new Vector3(-hw, h,  hd)
                        };

                        w.WriteLine("usemtl PedestrianProxy");
                        textures.TryAdd("PedestrianProxy", null);

                        int startV = v;
                        foreach (var lb in localBox)
                        {
                            Vector3 wv = Vector3.Transform(lb, worldMatrix);
                            w.WriteLine($"v {F(wv.X)} {F(wv.Y)} {F(wv.Z)}");
                            v++;
                        }

                        w.WriteLine($"f {startV+0} {startV+1} {startV+5}");
                        w.WriteLine($"f {startV+0} {startV+5} {startV+4}");

                        w.WriteLine($"f {startV+1} {startV+2} {startV+6}");
                        w.WriteLine($"f {startV+1} {startV+6} {startV+5}");

                        w.WriteLine($"f {startV+2} {startV+3} {startV+7}");
                        w.WriteLine($"f {startV+2} {startV+7} {startV+6}");

                        w.WriteLine($"f {startV+3} {startV+0} {startV+4}");
                        w.WriteLine($"f {startV+3} {startV+4} {startV+7}");

                        w.WriteLine($"f {startV+4} {startV+5} {startV+6}");
                        w.WriteLine($"f {startV+4} {startV+6} {startV+7}");

                        w.WriteLine($"f {startV+0} {startV+3} {startV+2}");
                        w.WriteLine($"f {startV+0} {startV+2} {startV+1}");
                    }
                }
            }

            WriteMtlFile(mtlPath, textures);
            if (File.Exists(objPath)) File.Delete(objPath);
            File.Move(tempObj, objPath);
            Log($"  [+] Pedestrians -> {Path.GetFileName(objPath)}");
        }

        private void AppendPedestriansToWriter(byte[] pedData, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, Func<string, byte[]?> loader)
        {
            string text = Encoding.ASCII.GetString(pedData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            int pedIdx = 0;
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
                    pedIdx++;
                    string pedHieName = "pedestrian_placeholder.hie";
                    Matrix4x4 worldMatrix = Matrix4x4.CreateTranslation(px, py, pz);

                    if (_useGrouping)
                    {
                        w.WriteLine($"o Pedestrian_{pedIdx:D3}");
                        w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                    }

                    byte[]? hieData = loader(pedHieName);
                    if (hieData != null)
                    {
                        string? pedArchive = _vfs.GetArchivePath(pedHieName);
                        try
                        {
                            var hie = TDRHierarchy.Load(hieData, pedHieName);
                            if (hie.Root != null)
                            {
                                string currentTex = "Default";
                                ProcessNode(hie.Root, worldMatrix, ref currentTex, hie, textures, w, ref v, ref vt, ref vn, pedArchive);
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void ProcessNode(TDRNode node, Matrix4x4 parentMatrix, ref string currentTexture, TDRHierarchy hie, Dictionary<string, string?> textureSet, StreamWriter w, ref int v, ref int vt, ref int vn, string? archivePath = null)
        {
            if (node == null) return;

            Matrix4x4 worldMatrix = node.Transform * parentMatrix;

            // Handle Texture Node (NodeType 2)
            if (node.Type == TDRNode.NodeType.Texture && node.Index >= 0 && node.Index < hie.Textures.Count)
            {
                currentTexture = hie.Textures[node.Index].Trim('"');
            }

            // Handle Material Node (NodeType 5)
            if (node.Type == TDRNode.NodeType.Material && node.Index >= 0 && node.Index < hie.Materials.Count)
            {
                var mat = hie.Materials[node.Index];
                if (mat.TextureIndex >= 0 && mat.TextureIndex < hie.Textures.Count)
                {
                    currentTexture = hie.Textures[mat.TextureIndex].Trim('"');
                }
            }

            // Handle Mesh Node (NodeType 3)
            if (node.Type == TDRNode.NodeType.Mesh)
            {
                string? meshName = hie.Meshes.Count == 1 ? hie.Meshes[0] : (node.Index >= 0 && node.Index < hie.Meshes.Count ? hie.Meshes[node.Index] : null);
                if (meshName != null)
                {
                    if (!_meshCache.TryGetValue(meshName, out var container))
                    {
                        byte[]? meshData = _vfs.LoadFileContext(meshName, _trackContext);
                        if (meshData != null)
                        {
                            if (_verbose) Log($"      [MESH] Loaded: {meshName}");
                            container = MSHSContainer.Load(meshData, meshName);
                            _meshCache[meshName] = container;
                        }
                        else if (_verbose)
                        {
                            Log($"      [MESH] MISS : {meshName}");
                        }
                    }

                    if (container != null)
                    {
                        // Directive 'o' creates a distinct Scene Object in Blender/Unity Outliner;
                        // Directive 'g' creates sub-face groups/materials inside that object.
                        if (_useGrouping)
                        {
                            w.WriteLine($"o {node.Name}_{node.ID}");
                        }
                        Matrix4x4 drawMatrix = worldMatrix;
                        if (_useLocalCoords)
                        {
                            _localOrigin ??= new Vector3(worldMatrix.M41, worldMatrix.M42, worldMatrix.M43);

                            drawMatrix.M41 -= _localOrigin.Value.X;
                            drawMatrix.M42 -= _localOrigin.Value.Y;
                            drawMatrix.M43 -= _localOrigin.Value.Z;
                        }

                        w.WriteLine($"usemtl {currentTexture}");
                        textureSet.TryAdd(currentTexture, archivePath);

                        int subIndex = hie.Meshes.Count == 1 ? node.Index : -1;
                        if (subIndex >= 0 && subIndex < container.Meshes.Count)
                        {
                            WriteSubMesh(container.Meshes[subIndex], drawMatrix, w, ref v, ref vt, ref vn);
                        }
                        else
                        {
                            foreach (var subMesh in container.Meshes)
                            {
                                WriteSubMesh(subMesh, drawMatrix, w, ref v, ref vt, ref vn);
                            }
                        }
                    }
                }
            }

            foreach (var child in node.Children)
            {
                ProcessNode(child, worldMatrix, ref currentTexture, hie, textureSet, w, ref v, ref vt, ref vn, archivePath);
            }
        }

        private void WriteSubMesh(TDRMeshData mesh, Matrix4x4 transform, StreamWriter w, ref int v, ref int vt, ref int vn)
        {
            switch (mesh.Mode)
            {
                case MeshMode.TriIndexedPosition:
                    foreach (var face in mesh.Faces)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            var vert = face.Vertices[i];
                            Vector3 pos  = Vector3.Transform(mesh.Positions[vert.PositionIndex], transform);
                            Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);

                            w.WriteLine($"v {F(pos.X)} {F(pos.Y)} {F(pos.Z)}");
                            w.WriteLine($"vt {F(vert.UV.X)} {F(1.0f - vert.UV.Y)}");
                            w.WriteLine($"vn {F(norm.X)} {F(norm.Y)} {F(norm.Z)}");
                        }
                        w.WriteLine($"f {v}/{vt}/{vn} {v+1}/{vt+1}/{vn+1} {v+2}/{vt+2}/{vn+2}");
                        v += 3; vt += 3; vn += 3;
                    }
                    break;

                case MeshMode.Tri:
                    int startV = v;
                    foreach (var vert in mesh.Vertices)
                    {
                        Vector3 pos  = Vector3.Transform(vert.Position, transform);
                        Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);

                        w.WriteLine($"v {F(pos.X)} {F(pos.Y)} {F(pos.Z)}");
                        w.WriteLine($"vt {F(vert.UV.X)} {F(1.0f - vert.UV.Y)}");
                        w.WriteLine($"vn {F(norm.X)} {F(norm.Y)} {F(norm.Z)}");
                        v++; vt++; vn++;
                    }
                    foreach (var face in mesh.Faces)
                    {
                        int a = startV + face.V1, b = startV + face.V2, c = startV + face.V3;
                        w.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                    }
                    break;

                case MeshMode.NGon:
                default:
                    foreach (var face in mesh.Faces)
                    {
                        var faceLine = new StringBuilder("f");
                        foreach (var vert in face.Vertices)
                        {
                            Vector3 pos  = Vector3.Transform(vert.Position, transform);
                            Vector3 norm = Vector3.TransformNormal(vert.Normal, transform);

                            w.WriteLine($"v {F(pos.X)} {F(pos.Y)} {F(pos.Z)}");
                            w.WriteLine($"vt {F(vert.UV.X)} {F(1.0f - vert.UV.Y)}");
                            w.WriteLine($"vn {F(norm.X)} {F(norm.Y)} {F(norm.Z)}");

                            faceLine.Append($" {v}/{vt}/{vn}");
                            v++; vt++; vn++;
                        }
                        w.WriteLine(faceLine.ToString());
                    }
                    break;
            }
        }

        private void WriteMtlFile(string mtlPath, Dictionary<string, string?> textures)
        {
            using var mtl = new StreamWriter(mtlPath);
            mtl.WriteLine("newmtl Default\nKd 0.8 0.8 0.8");
            if (_noMaterials) return;

            static int GetTextureResolutionArea(string filename)
            {
                var match = Regex.Match(filename, @"_(\d+)x(\d+)_", RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
                {
                    return w * h;
                }
                return 0;
            }



            var vfsFiles = _vfs.GetFiles();
            foreach (var (t, archivePath) in textures)
            {
                if (string.IsNullOrWhiteSpace(t) || t == "Default") continue;
                mtl.WriteLine($"\nnewmtl {t}\nKd 1.0 1.0 1.0");

                string cleanMat = t.Trim('"').Trim();

                // nameMatch: texture filename matches HIE material/texture name (TIME -> TIME_256x256_32, timecorona -> timecorona_64x64_32, RepairCorona -> RepairCorona_64x64_32)
                bool NameMatch(PakManager.IndexedFile f)
                {
                    if (!f.Name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) &&
                        !f.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                        return false;

                    string cleanPath = f.Name.Replace('\\', '/');
                    string fileNameOnly = Path.GetFileNameWithoutExtension(cleanPath);

                    // 1. Exact match
                    if (fileNameOnly.Equals(cleanMat, StringComparison.OrdinalIgnoreCase))
                        return true;

                    // 2. Resolution/bitdepth suffix match (e.g. TIME_256x256_32, RepairCorona_64x64_32, timecorona_64x64_32)
                    if (fileNameOnly.StartsWith(cleanMat + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string suffix = fileNameOnly[(cleanMat.Length + 1)..];
                        if (Regex.IsMatch(suffix, @"^(\d+x\d+|\d+)(_\d+)?$", RegexOptions.IgnoreCase))
                            return true;
                    }

                    // 3. Known exact TDR texture aliases
                    if (cleanMat.Equals("span", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("spanner", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fileNameOnly.StartsWith("new_spanner", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("span", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (cleanMat.Equals("eng", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("engine", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fileNameOnly.StartsWith("new_engine", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("eng", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (cleanMat.Equals("helm", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("helmet", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fileNameOnly.StartsWith("new_helmet", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("helm", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (cleanMat.Equals("wad", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("wadocash", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fileNameOnly.StartsWith("new_wadocash", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("wad", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    if (cleanMat.Equals("ped", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("pedsign", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fileNameOnly.StartsWith("new_pedsign", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("ped", StringComparison.OrdinalIgnoreCase)) return true;
                    }

                    return false;
                }

                // 1A. Exact same PAK file as the .hie model
                PakManager.IndexedFile? matchTier1A = (!string.IsNullOrEmpty(archivePath)
                    ? vfsFiles.Where(f => NameMatch(f) &&
                          f.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                          .ThenByDescending(f => f.Name.Contains("_32"))
                          .ThenByDescending(f => f.Name.Contains("_24"))
                          .FirstOrDefault()
                    : null);

                // 1B. Same PAK directory / folder
                string? ctxDir = archivePath != null ? Path.GetDirectoryName(archivePath) : null;
                string mainTrack = (_trackContext ?? "").ToLowerInvariant();

                PakManager.IndexedFile? matchTier1B = matchTier1A ?? (ctxDir != null
                    ? vfsFiles.Where(f => NameMatch(f) &&
                          Path.GetDirectoryName(f.ArchivePath)
                              ?.Equals(ctxDir, StringComparison.OrdinalIgnoreCase) == true)
                          .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                          .ThenByDescending(f => f.Name.Contains("_32"))
                          .ThenByDescending(f => f.Name.Contains("_24"))
                          .FirstOrDefault()
                    : null);

                PakManager.IndexedFile? matchTier2 = matchTier1B ?? (!string.IsNullOrEmpty(mainTrack)
                    ? vfsFiles.Where(f => NameMatch(f) &&
                          ((f.ArchivePath ?? "").ToLowerInvariant().Replace("_", "").Contains(mainTrack.Replace("_", "")) ||
                           f.Name.ToLowerInvariant().Replace("_", "").Contains(mainTrack.Replace("_", ""))))
                          .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                          .ThenByDescending(f => f.Name.Contains("_32"))
                          .ThenByDescending(f => f.Name.Contains("_24"))
                          .FirstOrDefault()
                    : null);

                PakManager.IndexedFile? matchTier3 = matchTier2 ?? vfsFiles.Where(f => NameMatch(f) && !(f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant().Contains("tracks/"))
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault();

                PakManager.IndexedFile? bestMatch = matchTier3;

                if (_verbose)
                {
                    string tierName = matchTier1A != null ? "Tier 1A (Exact PAK File)" :
                                     matchTier1B != null ? "Tier 1B (Same PAK Directory)" :
                                     matchTier2 != null  ? "Tier 2 (Same Track Level)" :
                                     matchTier3 != null  ? "Tier 3 (Shared Assets)" : "NOT FOUND";

                    string origin = bestMatch != null
                        ? $"Archive: '{bestMatch.ArchivePath}' -> VirtualFile: '{bestMatch.Name}'"
                        : "NO MATCH IN VFS";

                    Log($"    [MTL RESOLVE] Mat: '{t:<20}' -> {tierName:<25} | {origin}");
                }

                if (bestMatch != null)
                {
                    string rawTexFileName = Path.GetFileName(bestMatch.Name);
                    string exportFolder = Path.GetDirectoryName(mtlPath) ?? _exportDir;
                    byte[]? data = _vfs.LoadFile(bestMatch);
                    string savedTexName = rawTexFileName;

                    if (data != null)
                    {
                        savedTexName = SaveTextureWithFormat(data, rawTexFileName, exportFolder);
                        if (_verbose) Log($"    [TEX SAVE] Saved '{savedTexName}' -> Archive: '{bestMatch.ArchivePath}'");
                    }

                    mtl.WriteLine($"map_Kd {savedTexName}");
                    mtl.WriteLine($"map_d {savedTexName}");

                    if (t.Contains("water", StringComparison.OrdinalIgnoreCase) || t.Contains("bump", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[]? bumpBytes = _vfs.LoadFileContext("bumpfx_0000_128_128_8.tga", "WATER") ?? _vfs.LoadFile("bumpfx_0000_128_128_8.tga");
                        if (bumpBytes != null)
                        {
                            string bumpName = SaveTextureWithFormat(bumpBytes, "water_bump_0000.tga", exportFolder);
                            mtl.WriteLine($"map_Bump -bm 1.0 {bumpName}");
                        }
                    }
                }
                else
                {
                    // Fallback lookup via _vfs.LoadFile / LoadFileContext for Powerups and global shared textures
                    string cleanT = t.TrimEnd('!');
                    string bangT = cleanT + "!";
                    string[] candidateNames = new[]
                    {
                        $"{t}.tga",
                        $"{bangT}_128x128_32.tga", $"{bangT}_64x64_32.tga", $"{bangT}_32x32_32.tga", $"{bangT}_16x16_32.tga", $"{bangT}_8x8_32.tga", $"{bangT}_4x4_32.tga", $"{bangT}_2x2_32.tga", $"{bangT}_1x1_32.tga",
                        $"{cleanT}.tga", $"{cleanT}_32.tga", $"{cleanT}_128x128_32.tga", $"{cleanT}_64x64_32.tga", $"{cleanT}_32x32_32.tga", $"{cleanT}_16x16_32.tga", $"{cleanT}_8x8_32.tga", $"{cleanT}_4x4_8.tga", $"{cleanT}_2x2_32.tga", $"{cleanT}_1x1_32.tga",
                        $"{bangT}_128x128_8.tga", $"{bangT}_64x64_8.tga", $"{bangT}_32x32_8.tga", $"{bangT}_16x16_8.tga", $"{bangT}_8x8_8.tga", $"{bangT}_4x4_8.tga", $"{bangT}_2x2_8.tga", $"{bangT}_1x1_8.tga",
                        $"{cleanT}_128x128_8.tga", $"{cleanT}_64x64_8.tga", $"{cleanT}_32x32_8.tga", $"{cleanT}_16x16_8.tga", $"{cleanT}_8x8_8.tga", $"{cleanT}_4x4_8.tga", $"{cleanT}_2x2_8.tga", $"{cleanT}_1x1_8.tga"
                    };

                    foreach (string cand in candidateNames)
                    {
                        byte[]? candBytes = _vfs.LoadFileContext(cand, "POWERUPS") ?? _vfs.LoadFile(cand);
                        if (candBytes != null && candBytes.Length > 0)
                        {
                            string rawTexFileName = Path.GetFileName(cand);
                            string exportFolder = Path.GetDirectoryName(mtlPath) ?? _exportDir;
                            string savedTexName = SaveTextureWithFormat(candBytes, rawTexFileName, exportFolder);

                            mtl.WriteLine($"map_Kd {savedTexName}");
                            mtl.WriteLine($"map_d {savedTexName}");

                            if (t.Contains("water", StringComparison.OrdinalIgnoreCase) || t.Contains("bump", StringComparison.OrdinalIgnoreCase))
                            {
                                byte[]? bumpBytes = _vfs.LoadFileContext("bumpfx_0000_128_128_8.tga", "WATER") ?? _vfs.LoadFile("bumpfx_0000_128_128_8.tga");
                                if (bumpBytes != null)
                                {
                                    string bumpName = SaveTextureWithFormat(bumpBytes, "water_bump_0000.tga", exportFolder);
                                    mtl.WriteLine($"map_Bump -bm 1.0 {bumpName}");
                                }
                            }
                            break;
                        }
                    }
                }
            }
        }

        private string SaveTextureWithFormat(byte[] rawData, string originalFileName, string targetDir)
        {
            if (_convertTexturesToPng && originalFileName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var bitmap = TgaDecoder.DecodeTga(rawData);
                    if (bitmap != null)
                    {
                        string pngName = Path.ChangeExtension(originalFileName, ".png");
                        string pngPath = Path.Combine(targetDir, pngName);
#pragma warning disable CS0618
                        bitmap.Save(pngPath);
#pragma warning restore CS0618
                        return pngName;
                    }
                }
                catch
                {
                    // Fallback to raw TGA write on decode error
                }
            }

            string rawPath = Path.Combine(targetDir, originalFileName);
            File.WriteAllBytes(rawPath, rawData);
            return originalFileName;
        }

        private static string GetMd5(byte[] data)
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string F(float val) => val.ToString("0.000000", CultureInfo.InvariantCulture);
    }
}
