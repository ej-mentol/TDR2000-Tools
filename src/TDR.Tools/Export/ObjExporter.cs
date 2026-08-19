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
        private readonly Dictionary<string, TDRHierarchy> _hieCache = new(StringComparer.OrdinalIgnoreCase);
        // Captured once per export (from the first mesh node encountered) when UseLocalCoords is on.
        // All subsequent mesh translations are offset by this value instead of being zeroed outright,
        // so meshes keep their position relative to each other — only the whole level shifts near origin.
        private Vector3? _localOrigin;

        private TDRHierarchy? GetOrLoadHierarchy(string hieName, Func<string, byte[]?> loader)
        {
            if (_hieCache.TryGetValue(hieName, out var cached)) return cached;
            byte[]? hieData = loader(hieName);
            if (hieData == null || hieData.Length == 0) return null;
            try
            {
                var hie = TDRHierarchy.Load(hieData, hieName);
                _hieCache[hieName] = hie;
                return hie;
            }
            catch
            {
                return null;
            }
        }

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

        private bool IsVerboseEnabled => _verbose || Services.LogService.Instance.IsEnabled(Services.LogLevel.Debug);

        private void Log(string message, Services.LogLevel level = Services.LogLevel.Info)
        {
            if (level == Services.LogLevel.Debug && !IsVerboseEnabled) return;

            if (_logger != null)
            {
                _logger(message);
            }
            else
            {
                Services.LogService.Instance.Log(level, message);
            }
        }

        // Keywords that point directly to a .hie or a .txt sub-descriptor (Stage 1 in EXPORT_FORMAT.md)
        private static readonly string[] DirectHieKeywords = new[]
        {
            "SKY_SPHERE", "SKY_BOX", "SKY_DOME", "SKY_MESH", "SKY", "SKYDOME", "SKYBOX",
            "BACKGROUND_MESH", "BACKGROUND_HIE", "BACKGROUND_SPHERE", "BACKGROUND_DOME", "BACKGROUND_BOX", "BACKGROUND_TEXTURE", "BACKGROUND",
            "WATER_MESH", "HARDSHADOW_HIE", "BASE_CONSOFT", "CONSOFT",
            "LEVEL_MESH", "STATIC_MESH", "OCCLUDER_MESH"
        };

        // Keywords whose value is always a .txt sub-descriptor (Stage 2 in EXPORT_FORMAT.md)
        // Sub-descriptors contain raw .hie paths per line, NO keyword prefix.
        private static readonly string[] SubDescriptorKeywords = new[]
        {
            "STATIC_MESH_DESCRIPTOR", "BREAKABLES_DESCRIPTOR", "ANIMATED_PROPS",
            "CONSOFT_DESCRIPTOR", "LEVEL_CONSOFT", "ARTICULATED_BRIDGES", "LIGHTS_DESCRIPTOR",
            "SPECIAL_VOLUMES", "SPECIAL_VOLUMES_0", "LEVEL_SCRIPT", "SCRIPT", "MISSION_SCRIPT"
        };

        public static List<string> ExtractLineNamesFromHie(byte[] hieBytes) =>
            LevelDescriptorParser.ExtractLineNamesFromHie(hieBytes);

        /// <summary>
        /// Recursively parses sub-descriptor .txt files to discover .hie mesh files and sub-instances.
        /// </summary>
        public void ParseSubDescriptorHieFiles(
            byte[] subDescriptorBytes,
            HashSet<string> visitedDescriptors,
            DescriptorAssets assets,
            Matrix4x4 parentMatrix)
        {
            LevelDescriptorParser.ParseSubDescriptorHieFiles(_vfs, _trackContext, subDescriptorBytes, visitedDescriptors, assets, parentMatrix);
        }

        /// <summary>
        /// Parses a master level descriptor (e.g. Hollowood.txt). Applies exact first-token
        /// keyword filtering as documented in EXPORT_FORMAT.md.
        /// </summary>
        public DescriptorAssets ParseLevelDescriptorAssets(byte[] descriptorBytes, HashSet<string>? visitedDescriptors = null)
        {
            return LevelDescriptorParser.ParseLevelDescriptorAssets(_vfs, _trackContext, descriptorBytes, visitedDescriptors);
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

            // LocalCoords Variant 2: pre-compute the minimum Y across all terrain vertices so that the
            // map floor lands at Y=0 in Blender instead of an arbitrary first-node world position.
            // X and Z are left at 0 (no horizontal shift — only vertical normalisation).
            if (_useLocalCoords)
            {
                float minY = float.MaxValue;
                var terrainHies = new HashSet<string>(assets.HieFiles, StringComparer.OrdinalIgnoreCase);
                foreach (var inst in assets.HieInstances) terrainHies.Add(inst.HieName);

                foreach (string hieName in terrainHies)
                {
                    if (hieName.Contains("sky", StringComparison.OrdinalIgnoreCase)) continue;
                    var preHie = GetOrLoadHierarchy(hieName, name => _vfs.LoadFileContext(name, _trackContext ?? levelName));
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
                    _localOrigin = new Vector3(0f, minY, 0f);
                    if (_verbose) Log($"[LocalCoords] Terrain floor Y = {minY:F2} → _localOrigin set to (0, {minY:F2}, 0)");
                }
                // if no terrain geometry was found, _localOrigin stays null and ??= captures from first processed node
            }

            string mtlPath = Path.ChangeExtension(outputObjPath, ".mtl");
            string tempObj = outputObjPath + ".tmp";
            var textures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            int v = 1, vt = 1, vn = 1;
            var bakedLayers = new List<string>();
            var bakedMovableCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var bakedDroneCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int bakedPowerups = 0;

            try
            {
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
                                bakedLayers.Add(inst.HieName);
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
                            bakedLayers.Add(hieName);
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

                        // Extract base terrain triangles — only when ground snap is enabled to avoid
                        // redundant HIE loading and triangle extraction on every export where snap is off.
                        var baseTriangles = new List<GroundSnapUtil.Triangle>();
                        if (_enableGroundSnap)
                        {
                            var snapHies = new HashSet<string>(assets.HieFiles, StringComparer.OrdinalIgnoreCase);
                            foreach (var inst in assets.HieInstances) snapHies.Add(inst.HieName);

                            foreach (string hieName in snapHies)
                            {
                                // Exclude sky dome, water surface, collision boxes and fog triggers
                                if (hieName.Contains("sky", StringComparison.OrdinalIgnoreCase) ||
                                    hieName.Contains("water", StringComparison.OrdinalIgnoreCase) ||
                                    hieName.Contains("ocean", StringComparison.OrdinalIgnoreCase) ||
                                    hieName.Contains("river", StringComparison.OrdinalIgnoreCase) ||
                                    hieName.Contains("scol", StringComparison.OrdinalIgnoreCase) ||
                                    hieName.Contains("trigger", StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                var hie = GetOrLoadHierarchy(hieName, name => _vfs.LoadFileContext(name, _trackContext ?? levelName));
                                if (hie != null)
                                {
                                    var tris = GroundSnapUtil.ExtractBaseTriangles(hie, (path) => _vfs.LoadFileContext(path, _trackContext ?? levelName));
                                    if (tris.Count > 0) baseTriangles.AddRange(tris);
                                }
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
                                AppendMovablesToWriter(movData, w, textures, ref v, ref vt, ref vn, (path) => _vfs.LoadFileContext(path, _trackContext ?? cleanTrackName), baseTriangles, bakedMovableCounts);
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
                                bakedPowerups += AppendPowerupsToWriter(pupData, w, textures, ref v, ref vt, ref vn, (path) => _vfs.LoadFileContext(path, _trackContext ?? cleanTrackName));
                            }
                        }

                        // 4. Bake Traffic Drones (DRONE_DESCRIPTOR) into the combined scene
                        var droneDescs = new List<string>(assets.DroneDescriptors);
                        string defaultDrone = $"{cleanTrackName}_DroneDescriptor.txt";
                        if (!droneDescs.Contains(defaultDrone, StringComparer.OrdinalIgnoreCase) && _vfs.FileExists(defaultDrone))
                        {
                            droneDescs.Add(defaultDrone);
                        }

                        if (droneDescs.Count > 0)
                        {
                            if (_verbose) Log($"[+] Baking Traffic Drones ({droneDescs.Count} descriptor(s)) into combined scene...");
                            AppendDronesToWriter(droneDescs, w, textures, ref v, ref vt, ref vn, (path) => _vfs.LoadFileContext(path, _trackContext ?? cleanTrackName), cleanTrackName, bakedDroneCounts, baseTriangles);
                        }
                    }
                }

                if (v > 1)
                {
                    WriteMtlFile(mtlPath, textures);
                    if (File.Exists(outputObjPath)) File.Delete(outputObjPath);
                    File.Move(tempObj, outputObjPath);

                    string fn = Path.GetFileName(outputObjPath);
                    result.ProducedObjFiles.Add(fn);
                    result.BaseMeshFileName = fn;

                    // Clean, readable export summary (Option B)
                    Log($"[+] Exported: {fn} ({v - 1:N0} vertices, {textures.Count} textures)", Services.LogLevel.Summary);
                    if (bakedLayers.Count > 0)
                    {
                        Log($"    • Layers ({bakedLayers.Count}): {string.Join(", ", bakedLayers)}", Services.LogLevel.Summary);
                    }
                    if (bakedMovableCounts.Count > 0)
                    {
                        int totalMovs = bakedMovableCounts.Values.Sum();
                        var topProps = bakedMovableCounts.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key} {kv.Value}x");
                        string propSummary = string.Join(", ", topProps);
                        if (bakedMovableCounts.Count > 6) propSummary += $", +{bakedMovableCounts.Count - 6} more";
                        Log($"    • Props ({totalMovs}): {propSummary}", Services.LogLevel.Summary);
                    }
                    if (bakedDroneCounts.Count > 0)
                    {
                        int totalDrones = bakedDroneCounts.Values.Sum();
                        var droneList = bakedDroneCounts.Select(kv => $"{kv.Key} {kv.Value}x");
                        Log($"    • Drones ({totalDrones}): {string.Join(", ", droneList)}", Services.LogLevel.Summary);
                    }
                    if (bakedPowerups > 0)
                    {
                        Log($"    • Spawns: {bakedPowerups} powerups", Services.LogLevel.Summary);
                    }
                }
                else
                {
                    if (IsVerboseEnabled) Log($"    [OBJ Export] Level '{levelName}' yielded 0 vertices — empty OBJ file skipped.", Services.LogLevel.Debug);
                }

                // 5. Generate debug spline visualization OBJ (_splines_debug.obj) with all waypoints and lines
                ExportSplineDebugObj(levelName, outputObjPath);
            }
            finally
            {
                if (File.Exists(tempObj))
                {
                    try { File.Delete(tempObj); } catch { }
                }
            }

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
            // _localOrigin is intentionally NOT reset here: ExportLevelToObj sets it to null once
            // at the start of the whole export so a single global origin is shared across all HIE
            // layers, movables, and powerups, keeping everything positioned relative to each other.

            var hie = GetOrLoadHierarchy(hieName, _ => hieBytes);
            if (hie == null || (hie.Root == null && hie.Meshes.Count == 0)) return;

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
                            string canonicalMat = RegisterAndGetCanonicalTexture(subTex, sourceArchivePath, textures);
                            w.WriteLine($"usemtl {canonicalMat}");

                            WriteSubMesh(subMesh, startMatrix, w, ref v, ref vt, ref vn);
                        }
                    }
                }
            }
        }

        public void ExportHieToObj(byte[] hieData, string hieName, string outputObjPath, string? sourceArchivePath = null, bool resetOrigin = true)
        {
            if (resetOrigin) _localOrigin = null; // fresh origin capture for standalone single HIE export

            var hie = GetOrLoadHierarchy(hieName, _ => hieData);
            if (hie == null || (hie.Root == null && hie.Meshes.Count == 0))
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

            try
            {
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
                                    string canonicalMat = RegisterAndGetCanonicalTexture(subTex, sourceArchivePath, textures);
                                    w.WriteLine($"usemtl {canonicalMat}");

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
                }
            }
            finally
            {
                if (File.Exists(tempObj))
                {
                    try { File.Delete(tempObj); } catch { }
                }
            }
        }

        private void AppendMovablesToWriter(byte[] movData, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, Func<string, byte[]?> loader, List<GroundSnapUtil.Triangle>? baseTriangles = null, Dictionary<string, int>? modelCountsCollector = null)
        {
            string text = Encoding.ASCII.GetString(movData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var instanceCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            // Derive snap distances from the actual terrain Y range so that objects intentionally
            // placed above ground (rooftops, balconies, hanging props) are not falsely pulled down.
            float snapMaxDrop = 500f;
            float snapRayStart = 25.0f;
            float? floorLimitY = null;
            if (_enableGroundSnap && baseTriangles != null && baseTriangles.Count > 0)
            {
                GroundSnapUtil.ComputeTerrainBounds(baseTriangles, out float minTY, out float maxTY);
                float range = maxTY - minTY;
                if (range > 0f)
                {
                    snapMaxDrop = range + 50.0f;
                    floorLimitY = minTY - 1.0f;
                }
            }

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

                Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, qw));
                Matrix4x4 worldMatrix = rotation with { M41 = px, M42 = py, M43 = pz };

                // EXPERIMENTAL GROUND SNAP WITH SLOPE ALIGNMENT BEGIN
                if (_enableGroundSnap && baseTriangles != null && baseTriangles.Count > 0)
                {
                    Vector3 origPos = new Vector3(px, py, pz);
                    var (snappedPos, alignedMat) = GroundSnapUtil.SnapAndAlignToSurface(
                        origPos,
                        worldMatrix,
                        baseTriangles,
                        contactRadius: 0.75f,
                        maxDropDistance: snapMaxDrop,
                        rayStartHeight: snapRayStart,
                        floorLimitY: floorLimitY
                    );
                    worldMatrix = alignedMat;
                    px = snappedPos.X;
                    py = snappedPos.Y;
                    pz = snappedPos.Z;
                }
                // EXPERIMENTAL GROUND SNAP END

                string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                int instIdx = instanceCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                instanceCounts[modelBaseName] = instIdx;
                if (modelCountsCollector != null)
                {
                    modelCountsCollector[modelBaseName] = modelCountsCollector.GetValueOrDefault(modelBaseName, 0) + 1;
                }
                string instanceId = $"{modelBaseName}_{instIdx:D3}";

                if (_useGrouping)
                {
                    w.WriteLine($"o {instanceId}");
                    w.WriteLine($"# Class: {hieName}");
                    w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                    w.WriteLine($"# Quaternion: {F(qx)} {F(qy)} {F(qz)} {F(qw)}");
                }

                var hie = GetOrLoadHierarchy(hieName, loader);
                if (hie?.Root != null)
                {
                    string? movableArchive = _vfs.GetArchivePath(hieName);
                    string defaultTex = "Default";
                    // _localOrigin is NOT reset per movable: the global origin from ExportLevelToObj
                    // keeps movables positioned correctly relative to the terrain.
                    ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, movableArchive, null, 0, instanceId);
                }
                else if (hie != null && hie.Meshes.Count > 0)
                {
                    string? movableArchive = _vfs.GetArchivePath(hieName);
                    string defaultTex = hie.Textures.Count > 0 ? hie.Textures[0].Trim('"') : "Default";
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
                            Matrix4x4 drawMatrix = worldMatrix;
                            if (_useLocalCoords && _localOrigin.HasValue)
                            {
                                drawMatrix.M41 -= _localOrigin.Value.X;
                                drawMatrix.M42 -= _localOrigin.Value.Y;
                                drawMatrix.M43 -= _localOrigin.Value.Z;
                            }
                            string canonicalMat = RegisterAndGetCanonicalTexture(defaultTex, movableArchive, textures);
                            w.WriteLine($"usemtl {canonicalMat}");
                            for (int i = 0; i < container.Meshes.Count; i++)
                            {
                                WriteSubMesh(container.Meshes[i], drawMatrix, w, ref v, ref vt, ref vn);
                            }
                        }
                    }
                }
            }
        }

        private int AppendPowerupsToWriter(byte[] pupData, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, Func<string, byte[]?> loader)
        {
            string text = Encoding.ASCII.GetString(pupData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            string lastCommentName = "Powerup";
            int lastTypeId = 0;
            int pupIndex = 0;
            int bakedCount = 0;

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

                    var hie = GetOrLoadHierarchy(iconHieName, loader);
                    if (hie?.Root != null)
                    {
                        string? movableArchive = _vfs.GetArchivePath(iconHieName);
                        string defaultTex = "Default";
                        // _localOrigin is NOT reset per powerup: global origin from ExportLevelToObj.
                        ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, movableArchive);
                        bakedCount++;
                    }
                }
            }
            return bakedCount;
        }

        private static string ResolvePowerupIconHie(int typeId, string name) =>
            TextureResolver.ResolvePowerupIconHie(typeId, name);
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
            var spawnedLocations = new List<(string Model, Vector3 Pos)>();
            try
            {
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

                        // Spatial deduplication: prevent exact same model from spawning at exact same position across cumulative descriptors
                        string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                        var rawPos = new Vector3(px, py, pz);
                        if (spawnedLocations.Any(loc => loc.Model.Equals(modelBaseName, StringComparison.OrdinalIgnoreCase) &&
                                                        Vector3.DistanceSquared(loc.Pos, rawPos) < 0.01f))
                        {
                            if (_verbose) Log($"    [Dedup] Skipped duplicate instance of '{hieName}' at {rawPos}");
                            continue;
                        }
                        spawnedLocations.Add((modelBaseName, rawPos));

                        Matrix4x4 rotation = Matrix4x4.CreateFromQuaternion(new Quaternion(qx, qy, qz, qw));
                        Matrix4x4 worldMatrix = _useLocalCoords ? rotation : rotation with
                        {
                            M41 = px, M42 = py, M43 = pz
                        };

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
                        var hie = GetOrLoadHierarchy(hieName, _ => hieData);
                        if (hie != null)
                        {
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
                                            string canonicalMat = RegisterAndGetCanonicalTexture(subTex, movableArchive, textures);
                                            w.WriteLine($"usemtl {canonicalMat}");

                                            WriteSubMesh(subMesh, worldMatrix, w, ref v, ref vt, ref vn);
                                        }
                                    }
                                    subMeshIdx++;
                                }
                            }
                        }
                    }
                }

                if (v > 1)
                {
                    WriteMtlFile(mtlPath, textures);
                    if (File.Exists(objPath)) File.Delete(objPath);
                    File.Move(tempObj, objPath);
                    Log($"  [+] Movables -> {Path.GetFileName(objPath)}");
                }
            }
            finally
            {
                if (File.Exists(tempObj))
                {
                    try { File.Delete(tempObj); } catch { }
                }
            }
        }

        public void ExportTrackProps(string trackName, Func<string, byte[]?> loader, Action<int, string>? progressCallback = null)
        {
            progressCallback?.Invoke(10, $"Exporting track props: {trackName}");
            string pedPlacement = $"{trackName}_Ped_Placement.txt";
            byte[]? pedData = loader(pedPlacement);
            if (pedData == null)
            {
                if (_verbose) Log($"  [i] No ped placement file found: {pedPlacement}");
                return;
            }

            // Parse PedDescriptor to get skeleton/class names
            string pedDesc = $"{trackName}_PedDescriptor.txt";
            byte[]? descData = loader(pedDesc);
            var classNames = new List<string>();
            if (descData != null)
            {
                string descText = Encoding.ASCII.GetString(descData);
                foreach (string rawLine in descText.Split('\n'))
                {
                    string line = rawLine.Contains("//") ? rawLine[..rawLine.IndexOf("//")].Trim() : rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        string name = Path.GetFileNameWithoutExtension(line);
                        name = name.Replace("Skeleton Descriptor", "").Replace("Descriptor", "").Trim();
                        classNames.Add(name);
                    }
                }
            }

            string objPath = Path.Combine(_exportDir, $"{trackName}_pedestrians.obj");
            string mtlPath = Path.ChangeExtension(objPath, ".mtl");
            string tempObj = objPath + ".tmp";

            int v = 1, vt = 1, vn = 1;
            var textures = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var w = new StreamWriter(tempObj, false, Encoding.ASCII))
                {
                    w.WriteLine($"# TDR2000 Pedestrian Spawn Placements - {trackName}");
                    w.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");

                    string pedText = Encoding.ASCII.GetString(pedData);
                    string[] lines = pedText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                    int pedIdx = 0;
                    foreach (string rawLine in lines)
                    {
                        string clean = rawLine.Contains("//") ? rawLine[..rawLine.IndexOf("//")].Trim() : rawLine.Trim();
                        if (string.IsNullOrWhiteSpace(clean)) continue;

                        string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 7) continue;

                        if (parts[0] != "1") continue; // 0 = disabled

                        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int classId) ||
                            !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float px) ||
                            !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float py) ||
                            !float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float pz) ||
                            !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float heading))
                        {
                            continue;
                        }

                        pedIdx++;
                        string className = classId >= 0 && classId < classNames.Count ? classNames[classId] : $"PedClass_{classId}";
                        string instanceId = $"{className}_{pedIdx:D3}";

                        Matrix4x4 worldMatrix = Matrix4x4.CreateRotationY(heading * (float)(Math.PI / 180.0)) * Matrix4x4.CreateTranslation(px, py, pz);

                        if (_useGrouping)
                        {
                            w.WriteLine($"o {instanceId}");
                            w.WriteLine($"# WorldPos: {F(px)} {F(py)} {F(pz)}");
                            w.WriteLine($"# Heading: {F(heading)}");
                        }

                        bool exportedMesh = false;
                        string hieName = $"{className}.hie";
                        var hie = GetOrLoadHierarchy(hieName, loader);
                        if (hie?.Root != null)
                        {
                            string? archivePath = _vfs.GetArchivePath(hieName);
                            string defaultTex = "Default";
                            ProcessNode(hie.Root, worldMatrix, ref defaultTex, hie, textures, w, ref v, ref vt, ref vn, archivePath);
                            exportedMesh = true;
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

                if (v > 1)
                {
                    WriteMtlFile(mtlPath, textures);
                    if (File.Exists(objPath)) File.Delete(objPath);
                    File.Move(tempObj, objPath);
                    Log($"  [+] Pedestrians -> {Path.GetFileName(objPath)}");
                }
            }
            finally
            {
                if (File.Exists(tempObj))
                {
                    try { File.Delete(tempObj); } catch { }
                }
            }
        }

        private int AppendPedestriansToWriter(byte[] pedData, StreamWriter w, Dictionary<string, string?> textures, ref int v, ref int vt, ref int vn, Func<string, byte[]?> loader)
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

                    var hie = GetOrLoadHierarchy(pedHieName, loader);
                    if (hie?.Root != null)
                    {
                        string? pedArchive = _vfs.GetArchivePath(pedHieName);
                        string currentTex = "Default";
                        ProcessNode(hie.Root, worldMatrix, ref currentTex, hie, textures, w, ref v, ref vt, ref vn, pedArchive, null, 0, $"Pedestrian_{pedIdx:D3}");
                    }
                }
            }
            return pedIdx;
        }

        private byte[]? LoadDroneHie(string droneName, out string resolvedName)
        {
            string clean = droneName
                .Replace("MAIN_NULL_PED", "", StringComparison.OrdinalIgnoreCase)
                .Replace("MAIN_NULL", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_PED", "", StringComparison.OrdinalIgnoreCase)
                .Trim('_');

            var candidates = new List<string>
            {
                droneName,
                droneName + ".hie",
                $"cars/{droneName}/{droneName}.hie",
                $"drones/{droneName}/{droneName}.hie",
                clean,
                clean + ".hie",
                $"cars/{clean}/{clean}.hie",
                $"drones/{clean}/{clean}.hie",
                $"cars/{clean}/{clean}main_null.hie"
            };

            foreach (var cand in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                byte[]? data = _vfs.LoadFile(cand);
                if (data != null && data.Length > 0)
                {
                    resolvedName = cand;
                    return data;
                }
            }

            resolvedName = droneName;
            return null;
        }

        private int AppendDronesToWriter(
            List<string> droneDescs,
            StreamWriter w,
            Dictionary<string, string?> textures,
            ref int v, ref int vt, ref int vn,
            Func<string, byte[]?> loader,
            string cleanTrackName,
            Dictionary<string, int>? droneCountsCollector = null,
            List<GroundSnapUtil.Triangle>? baseTriangles = null)
        {
            if (droneDescs == null || droneDescs.Count == 0) return 0;

            // 1. Parse drone vehicle types & requested instance counts
            var droneRequests = new List<(string Name, int Count)>();
            foreach (string descName in droneDescs)
            {
                byte[]? data = loader(descName);
                if (data == null || data.Length == 0) continue;

                string text = Encoding.ASCII.GetString(data);
                string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                foreach (string rawLine in lines)
                {
                    string clean = rawLine.Contains("//") ? rawLine[..rawLine.IndexOf("//")].Trim() : rawLine.Trim();
                    if (string.IsNullOrWhiteSpace(clean)) continue;

                    string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string dName = parts[0].Trim('"');
                        if (int.TryParse(parts[1], out int count) && count > 0)
                        {
                            droneRequests.Add((dName, count));
                        }
                    }
                }
            }

            if (droneRequests.Count == 0) return 0;

            var roadSplines = SplineResolver.ResolveRoadSplines(_vfs, cleanTrackName, _trackContext, msg => Log(msg));
            int totalDrones = droneRequests.Sum(r => r.Count);
            var spawnMatrices = SplineResolver.GenerateSpawnMatrices(roadSplines, totalDrones);

            int bakedTotal = 0;
            int spawnIdx = 0;

            foreach (var req in droneRequests)
            {
                byte[]? droneHieBytes = LoadDroneHie(req.Name, out string resolvedHieName);
                if (droneHieBytes == null)
                {
                    if (_verbose)
                        Log($"    [!] Traffic drone '{req.Name}' model not found in VFS (skipped 3D mesh).");
                    continue;
                }

                var hie = GetOrLoadHierarchy(resolvedHieName, _ => droneHieBytes);
                if (hie?.Root == null) continue;

                string? droneArchive = _vfs.GetArchivePath(resolvedHieName);

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

                    string instanceId = $"Drone_{Path.GetFileNameWithoutExtension(req.Name)}_{i + 1:D2}";
                    if (_useGrouping)
                    {
                        w.WriteLine($"o {instanceId}");
                        w.WriteLine($"# Class: {req.Name}");
                        w.WriteLine($"# WorldPos: {F(spawnMat.M41)} {F(spawnMat.M42)} {F(spawnMat.M43)}");
                    }

                    string currentTex = "Default";
                    ProcessNode(hie.Root, spawnMat, ref currentTex, hie, textures, w, ref v, ref vt, ref vn, droneArchive, null, 0, instanceId);
                    bakedTotal++;

                    if (droneCountsCollector != null)
                    {
                        string shortName = Path.GetFileNameWithoutExtension(req.Name);
                        droneCountsCollector[shortName] = droneCountsCollector.GetValueOrDefault(shortName, 0) + 1;
                    }
                }
            }

            return bakedTotal;
        }

        private void ProcessNode(
            TDRNode node,
            Matrix4x4 parentMatrix,
            ref string currentTexture,
            TDRHierarchy hie,
            Dictionary<string, string?> textureSet,
            StreamWriter w,
            ref int v,
            ref int vt,
            ref int vn,
            string? archivePath = null,
            HashSet<TDRNode>? visited = null,
            int depth = 0,
            string? instancePrefix = null)
        {
            if (node == null || depth > 200) return;

            visited ??= new HashSet<TDRNode>();
            if (!visited.Add(node)) return;

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
                            string partName = node.Name.EndsWith($"_{node.ID}", StringComparison.OrdinalIgnoreCase)
                                ? node.Name
                                : $"{node.Name}_{node.ID}";

                            if (!string.IsNullOrEmpty(instancePrefix))
                            {
                                w.WriteLine($"g {partName}");
                            }
                            else
                            {
                                w.WriteLine($"o {partName}");
                            }
                        }
                        Matrix4x4 drawMatrix = worldMatrix;
                        if (_useLocalCoords)
                        {
                            _localOrigin ??= new Vector3(worldMatrix.M41, worldMatrix.M42, worldMatrix.M43);

                            drawMatrix.M41 -= _localOrigin.Value.X;
                            drawMatrix.M42 -= _localOrigin.Value.Y;
                            drawMatrix.M43 -= _localOrigin.Value.Z;
                        }

                        string canonicalMat = RegisterAndGetCanonicalTexture(currentTexture, archivePath, textureSet);
                        w.WriteLine($"usemtl {canonicalMat}");

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
                ProcessNode(child, worldMatrix, ref currentTexture, hie, textureSet, w, ref v, ref vt, ref vn, archivePath, visited, depth + 1, instancePrefix);
            }
        }

        private void WriteSubMesh(TDRMeshData mesh, Matrix4x4 transform, StreamWriter w, ref int v, ref int vt, ref int vn)
        {
            var stream = new TriangleStream();
            MeshGeometryReader.AppendTriangles(mesh, transform, stream);

            for (int i = 0; i < stream.Vertices.Count; i += 3)
            {
                var v0 = stream.Vertices[i];
                var v1 = stream.Vertices[i + 1];
                var v2 = stream.Vertices[i + 2];

                // Wavefront OBJ uses OpenGL texture coordinate convention with origin (0,0) at bottom-left,
                // requiring (1.0f - V). In contrast, TDR2000 native meshes and glTF 2.0 specification
                // both use DirectX/Vulkan top-left convention and write (V) directly without inversion.
                w.WriteLine($"v {F(v0.Position.X)} {F(v0.Position.Y)} {F(v0.Position.Z)}");
                w.WriteLine($"vt {F(v0.UV.X)} {F(1.0f - v0.UV.Y)}");
                w.WriteLine($"vn {F(v0.Normal.X)} {F(v0.Normal.Y)} {F(v0.Normal.Z)}");

                w.WriteLine($"v {F(v1.Position.X)} {F(v1.Position.Y)} {F(v1.Position.Z)}");
                w.WriteLine($"vt {F(v1.UV.X)} {F(1.0f - v1.UV.Y)}");
                w.WriteLine($"vn {F(v1.Normal.X)} {F(v1.Normal.Y)} {F(v1.Normal.Z)}");

                w.WriteLine($"v {F(v2.Position.X)} {F(v2.Position.Y)} {F(v2.Position.Z)}");
                w.WriteLine($"vt {F(v2.UV.X)} {F(1.0f - v2.UV.Y)}");
                w.WriteLine($"vn {F(v2.Normal.X)} {F(v2.Normal.Y)} {F(v2.Normal.Z)}");

                w.WriteLine($"f {v}/{vt}/{vn} {v+1}/{vt+1}/{vn+1} {v+2}/{vt+2}/{vn+2}");
                v += 3; vt += 3; vn += 3;
            }
        }

        private void WriteMtlFile(string mtlPath, Dictionary<string, string?> textures)
        {
            using var mtl = new StreamWriter(mtlPath);
            mtl.WriteLine("newmtl Default\nKd 0.8 0.8 0.8");
            if (_noMaterials) return;

            foreach (var (t, archivePath) in textures)
            {
                if (string.IsNullOrWhiteSpace(t) || t == "Default") continue;
                mtl.WriteLine($"\nnewmtl {t}\nKd 1.0 1.0 1.0");

                var resolveResult = TextureResolver.ResolveBestMatch(_vfs, t, archivePath, _trackContext);
                PakManager.IndexedFile? bestMatch = resolveResult?.File;
                string tierName = resolveResult?.TierName ?? "NOT FOUND";

                if (_verbose)
                {
                    string origin = bestMatch != null
                        ? $"Archive: '{bestMatch.ArchivePath}' -> VirtualFile: '{bestMatch.Name}'"
                        : "NO MATCH IN VFS";

                    Log($"    [MTL RESOLVE] Mat: '{t:<20}' -> {tierName:<25} | {origin}");
                }

                string exportFolder = Path.GetDirectoryName(mtlPath) ?? _exportDir;
                var texService = new TextureResolutionService(_vfs, _exportDir, _trackContext, _convertTexturesToPng);
                string? savedTexName = texService.ResolveAndSave(t, archivePath, exportFolder);

                if (!string.IsNullOrEmpty(savedTexName))
                {
                    mtl.WriteLine($"map_Kd {savedTexName}");
                    mtl.WriteLine($"map_d {savedTexName}");

                    if (t.Contains("water", StringComparison.OrdinalIgnoreCase) || t.Contains("bump", StringComparison.OrdinalIgnoreCase))
                    {
                        byte[]? bumpBytes = _vfs.LoadFileContext("bumpfx_0000_128_128_8.tga", "WATER") ?? _vfs.LoadFile("bumpfx_0000_128_128_8.tga");
                        if (bumpBytes != null)
                        {
                            string bumpName = texService.SaveTextureWithFormat(bumpBytes, "water_bump_0000.tga", exportFolder);
                            mtl.WriteLine($"map_Bump -bm 1.0 {bumpName}");
                        }
                    }
                }
                else
                {
                    Log($"[MTL WARNING] Texture for material '{t}' not found in VFS context.");
                }
            }
        }

        private static string RegisterAndGetCanonicalTexture(string textureName, string? archivePath, Dictionary<string, string?> textureSet)
        {
            if (string.IsNullOrWhiteSpace(textureName)) return "Default";

            foreach (var existing in textureSet.Keys)
            {
                if (existing.Equals(textureName, StringComparison.OrdinalIgnoreCase))
                {
                    return existing;
                }
            }

            textureSet[textureName] = archivePath;
            return textureName;
        }



        private void ExportSplineDebugObj(string levelName, string baseObjPath)
        {
            try
            {
                string cleanTrack = TrackDiscovery.GetBaseTrackName(levelName);
                string outDir = Path.GetDirectoryName(baseObjPath) ?? _exportDir;
                string debugObjPath = Path.Combine(outDir, $"{cleanTrack}_splines_debug.obj");

                // Collect road and track splines
                var splines = SplineResolver.ResolveRoadSplines(_vfs, cleanTrack, _trackContext);
                if (splines.Count == 0) return;

                using var sw = new StreamWriter(debugObjPath, false, Encoding.ASCII);
                sw.WriteLine($"# TDR2000 Spline Debug Visualization for {levelName}");
                sw.WriteLine($"# Total Splines: {splines.Count}");

                int vOffset = 1;
                for (int s = 0; s < splines.Count; s++)
                {
                    var sp = splines[s];
                    if (sp.Points.Count < 2) continue;

                    string spName = string.IsNullOrWhiteSpace(sp.Name) ? $"Spline_{s:D2}" : sp.Name;
                    sw.WriteLine($"o {spName}");
                    sw.WriteLine($"g {spName}");

                    int startV = vOffset;
                    foreach (var pt in sp.Points)
                    {
                        sw.WriteLine($"v {F(pt.X)} {F(pt.Y)} {F(pt.Z)}");
                        vOffset++;
                    }

                    // Write line segments: l v1 v2 v3 ...
                    sw.Write("l");
                    for (int vi = startV; vi < vOffset; vi++)
                    {
                        sw.Write($" {vi}");
                    }
                    sw.WriteLine();
                }

                if (IsVerboseEnabled)
                {
                    Log($"[+] Exported spline debug visualization: {Path.GetFileName(debugObjPath)} ({splines.Count} splines)");
                }
            }
            catch (Exception ex)
            {
                if (IsVerboseEnabled) Log($"    [!] Warning: Failed to export spline debug OBJ: {ex.Message}");
            }
        }

        private static string F(float val) => val.ToString("0.000000", CultureInfo.InvariantCulture);
    }
}
