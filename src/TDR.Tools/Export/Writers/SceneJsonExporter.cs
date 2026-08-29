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
    public sealed class SceneManifest
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("trackName")]
        public string TrackName { get; set; } = string.Empty;

        [JsonPropertyName("baseMesh")]
        public string? BaseMesh { get; set; }

        [JsonPropertyName("waterMesh")]
        public string? WaterMesh { get; set; }

        [JsonPropertyName("skyMesh")]
        public string? SkyMesh { get; set; }

        [JsonPropertyName("originOffset")]
        public float[]? OriginOffset { get; set; }

        [JsonPropertyName("staticLayers")]
        public List<string> StaticLayers { get; set; } = new();

        [JsonPropertyName("splines")]
        public List<SceneSplineTrack> Splines { get; set; } = new();

        [JsonPropertyName("environment")]
        public SceneEnvironment Environment { get; set; } = new();

        [JsonPropertyName("variants")]
        public Dictionary<string, SceneVariant> Variants { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("entities")]
        public List<SceneEntity> Entities { get; set; } = new();

        [JsonPropertyName("lights")]
        public List<SceneLight> Lights { get; set; } = new();

        [JsonPropertyName("soundEmitters")]
        public List<SceneSoundEmitter> SoundEmitters { get; set; } = new();

        [JsonPropertyName("surfaceMaterials")]
        public Dictionary<string, TDR.PakLib.Formats.SurfaceMaterialPhysics> SurfaceMaterials { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("breakables")]
        public List<SceneBreakable> Breakables { get; set; } = new();

        [JsonPropertyName("animatedTextures")]
        public List<SceneAnimatedTexture> AnimatedTextures { get; set; } = new();

        [JsonPropertyName("paths")]
        public List<ScenePath> Paths { get; set; } = new();
    }

    public sealed class SceneSplineNode
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("position")]
        public float[] Position { get; set; } = new float[3];

        [JsonPropertyName("nextIndex")]
        public int? NextIndex { get; set; }

        [JsonPropertyName("prevIndex")]
        public int? PrevIndex { get; set; }

        [JsonPropertyName("distanceToNext")]
        public float DistanceToNext { get; set; }

        [JsonPropertyName("tangent")]
        public float[] Tangent { get; set; } = new float[3];
    }

    public sealed class SceneSplineTrack
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("pointCount")]
        public int PointCount { get; set; }

        [JsonPropertyName("totalLength")]
        public float TotalLength { get; set; }

        [JsonPropertyName("isClosed")]
        public bool IsClosed { get; set; }

        [JsonPropertyName("points")]
        public List<float[]> Points { get; set; } = new();

        [JsonPropertyName("nodes")]
        public List<SceneSplineNode> Nodes { get; set; } = new();
    }

    public sealed class SceneBreakable
    {
        [JsonPropertyName("hierarchy")]
        public string Hierarchy { get; set; } = string.Empty;

        [JsonPropertyName("textureName")]
        public string TextureName { get; set; } = string.Empty;

        [JsonPropertyName("breakSound")]
        public string BreakSound { get; set; } = string.Empty;
    }

    public sealed class SceneAnimatedTexture
    {
        [JsonPropertyName("animationScript")]
        public string AnimationScript { get; set; } = string.Empty;

        [JsonPropertyName("textureToAnimate")]
        public string TextureToAnimate { get; set; } = string.Empty;
    }

    public sealed class SceneSoundEmitter
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        public float[] Position { get; set; } = new float[3];
    }

    public sealed class SceneEnvironment
    {
        [JsonPropertyName("sunVector")]
        public float[]? SunVector { get; set; }

        [JsonPropertyName("sunColor")]
        public float[]? SunColor { get; set; }

        [JsonPropertyName("ambientLight")]
        public float AmbientLight { get; set; } = 0.3f;

        [JsonPropertyName("ambientColor")]
        public float[]? AmbientColor { get; set; }

        [JsonPropertyName("fog")]
        public SceneFog Fog { get; set; } = new();
    }

    public sealed class SceneFog
    {
        [JsonPropertyName("trackFogEnabled")]
        public bool TrackFogEnabled { get; set; }

        [JsonPropertyName("trackFogColor")]
        public float[]? TrackFogColor { get; set; }

        [JsonPropertyName("skyFogEnabled")]
        public bool SkyFogEnabled { get; set; }

        [JsonPropertyName("skyFogColor")]
        public float[]? SkyFogColor { get; set; }
    }

    public sealed class SceneVariant
    {
        [JsonPropertyName("consoft")]
        public string? Consoft { get; set; }

        [JsonPropertyName("raceline")]
        public string? Raceline { get; set; }

        [JsonPropertyName("checkpoints")]
        public List<float[]> Checkpoints { get; set; } = new();
    }

    public sealed class SceneEntity
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = "movable";

        [JsonPropertyName("prefab")]
        public string Prefab { get; set; } = string.Empty;

        [JsonPropertyName("position")]
        public float[] Position { get; set; } = new float[3];

        [JsonPropertyName("rotation")]
        public float[] Rotation { get; set; } = new float[4];

        [JsonPropertyName("heading")]
        public float? Heading { get; set; }

        [JsonPropertyName("properties")]
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    public sealed class SceneLight
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "point";

        [JsonPropertyName("position")]
        public float[] Position { get; set; } = new float[3];

        [JsonPropertyName("color")]
        public float[] Color { get; set; } = new float[3];

        [JsonPropertyName("radius")]
        public float Radius { get; set; } = 10.0f;

        [JsonPropertyName("attenuation")]
        public float[] Attenuation { get; set; } = new float[] { 1.0f, 0.1f, 0.01f };
    }

    public sealed class ScenePath
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = "drone_path";

        [JsonPropertyName("closedLoop")]
        public bool ClosedLoop { get; set; } = true;

        [JsonPropertyName("points")]
        public List<float[]> Points { get; set; } = new();
    }

    public static class SceneJsonExporter
    {
        public static void GenerateManifest(string trackName, string? variantArg, PakManager vfs, string outDir, Func<string, byte[]?> loader, TrackExportResult? exportResult = null, bool useZeroOriginForPrefabs = true, bool verbose = false, Action<string>? log = null)
        {
            string cleanTrack = TrackDiscovery.GetBaseTrackName(trackName);
            string primaryObjName = $"{cleanTrack}.obj";
            string altObjName = $"{cleanTrack}Mesh.obj";

            string? baseMeshCandidate = exportResult?.BaseMeshFileName 
                ?? (File.Exists(Path.Combine(outDir, primaryObjName)) ? primaryObjName
                : (File.Exists(Path.Combine(outDir, altObjName)) ? altObjName : null));

            string? waterMeshCandidate = exportResult?.WaterMeshFileName
                ?? (File.Exists(Path.Combine(outDir, $"{cleanTrack}Water.obj")) ? $"{cleanTrack}Water.obj" : null);

            string? skyMeshCandidate = exportResult?.SkyMeshFileName
                ?? (File.Exists(Path.Combine(outDir, "FilmSkysphereStudio.obj")) ? "FilmSkysphereStudio.obj" : null);

            var manifest = new SceneManifest
            {
                TrackName = !string.IsNullOrWhiteSpace(variantArg) ? $"{trackName}_{variantArg.ToLower()}" : trackName,
                BaseMesh = baseMeshCandidate,
                WaterMesh = waterMeshCandidate,
                SkyMesh = skyMeshCandidate
            };

            if (verbose) log?.Invoke($"--- [STAGE JSON] Scene Manifest Generation ---");

            // Canonical Level Descriptor & Assets Extraction via LevelDescriptorParser (Single Source of Truth)
            string trackContext = !string.IsNullOrWhiteSpace(variantArg) ? $"{trackName}_{variantArg}" : trackName;
            byte[]? descBytes = loader($"{trackContext}.txt") ?? loader($"{trackName}.txt") ?? Array.Empty<byte>();
            var assets = LevelDescriptorParser.ParseLevelDescriptorAssets(vfs, trackContext, descBytes);

            // 1. Static Geometry Layers
            foreach (string hieFile in assets.HieFiles)
            {
                if (!manifest.StaticLayers.Contains(hieFile, StringComparer.OrdinalIgnoreCase))
                    manifest.StaticLayers.Add(hieFile);
            }

            // 2. Base Environment & Atmosphere (from master txt)
            byte[]? trackTxtData = loader($"{trackName}.txt");
            if (trackTxtData != null)
            {
                ParseEnvironment(trackTxtData, manifest.Environment);
                if (verbose) log?.Invoke($"  [JSON Env] Parsed base atmosphere parameters from '{trackName}.txt'");
            }

            // 2b. Variant Environment Overrides (if specified)
            if (!string.IsNullOrWhiteSpace(variantArg))
            {
                string variantTxtName = $"{trackName}_{variantArg}.txt";
                byte[]? varTxtData = loader(variantTxtName);
                if (varTxtData != null)
                {
                    ParseEnvironment(varTxtData, manifest.Environment);
                    if (verbose) log?.Invoke($"  [JSON Env Override] Applied variant atmosphere overrides from '{variantTxtName}'");
                }
            }

            // 3. Lights
            var lightsRes = LoadDescriptorByKeyOrFallback(loader, trackTxtData, "LIGHTS_DESCRIPTOR", trackName, "LightsDescriptor.txt");
            if (lightsRes.Data != null)
            {
                ParseLights(lightsRes.Data, manifest.Lights);
                if (verbose) log?.Invoke($"  [JSON Lights] Parsed {manifest.Lights.Count} light fixture(s) from '{lightsRes.ResolvedName}'");
            }

            // 3b. 3D Sound Emitters
            var sndRes = LoadDescriptorByKeyOrFallback(loader, trackTxtData, "AMBIENT_SOUNDS", trackName, "AmbientSndDescriptor.txt");
            if (sndRes.Data != null)
            {
                ParseSoundEmitters(sndRes.Data, manifest.SoundEmitters);
                if (verbose) log?.Invoke($"  [JSON Sound Emitters] Parsed {manifest.SoundEmitters.Count} 3D sound emitter(s) from '{sndRes.ResolvedName}'");
            }

            // 3c. Surface Physics Materials
            var hRes = LoadDescriptorByKeyOrFallback(loader, trackTxtData, "SPECIALV_H_ENVIRONMENTS", trackName, "Volumes.h");
            if (hRes.Data != null)
            {
                var mats = TDR.PakLib.Formats.HParser.Parse(hRes.Data);
                foreach (var kvp in mats) manifest.SurfaceMaterials[kvp.Key] = kvp.Value;
                if (verbose) log?.Invoke($"  [JSON Surface Physics] Parsed {mats.Count} surface material physics entry(ies) from '{hRes.ResolvedName}'");
            }

            // 4a. Instanced HIEs (Trains, Sub-descriptor props with explicit matrices)
            var instCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var inst in assets.HieInstances)
            {
                string modelBaseName = Path.GetFileNameWithoutExtension(inst.HieName);
                int instIdx = instCounts.GetValueOrDefault(modelBaseName, 0) + 1;
                instCounts[modelBaseName] = instIdx;

                var q = Quaternion.CreateFromRotationMatrix(inst.Transform);
                manifest.Entities.Add(new SceneEntity
                {
                    Id = $"{modelBaseName}_{instIdx:D3}",
                    Category = "instanced_prop",
                    Prefab = $"prefabs/{modelBaseName}.obj",
                    Position = new[] { inst.Transform.M41, inst.Transform.M42, inst.Transform.M43 },
                    Rotation = new[] { q.X, q.Y, q.Z, q.W }
                });
            }

            // 4b. Dynamic Scene Entities (Movables, Powerups, Drones, Pedestrians) via SceneReconstruction
            var dynamicEntities = SceneReconstruction.ReconstructDynamicEntities(
                vfs,
                trackName,
                assets,
                includeMovables: true,
                useLocalCoords: false,
                globalOrigin: null,
                trackContext: trackContext,
                log: msg => { if (verbose) log?.Invoke(msg); });

            foreach (var entity in dynamicEntities)
            {
                var q = Quaternion.CreateFromRotationMatrix(entity.WorldTransform);
                string category = entity.Category switch
                {
                    EntityCategory.MovableProp => "movable",
                    EntityCategory.TrafficDrone => "traffic_drone",
                    EntityCategory.PowerupItem => "powerup",
                    EntityCategory.Pedestrian => "pedestrian",
                    _ => "dynamic_entity"
                };

                string prefab = entity.Category == EntityCategory.Pedestrian
                    ? "prefabs/pedestrian_proxy.obj"
                    : $"prefabs/{Path.GetFileNameWithoutExtension(entity.ModelHieName)}.obj";

                var jsonEntity = new SceneEntity
                {
                    Id = entity.InstanceId,
                    Category = category,
                    Prefab = prefab,
                    Position = new[] { entity.WorldTransform.M41, entity.WorldTransform.M42, entity.WorldTransform.M43 },
                    Rotation = new[] { q.X, q.Y, q.Z, q.W }
                };

                if (entity.Category == EntityCategory.PowerupItem)
                {
                    jsonEntity.Properties["type_id"] = entity.TypeId;
                    if (!string.IsNullOrEmpty(entity.Tag)) jsonEntity.Properties["powerup_type"] = entity.Tag;
                }
                else if (entity.Category == EntityCategory.TrafficDrone)
                {
                    if (!string.IsNullOrEmpty(entity.Tag)) jsonEntity.Properties["drone_class"] = entity.Tag;
                }
                else if (entity.Category == EntityCategory.Pedestrian)
                {
                    jsonEntity.Properties["skin_index"] = entity.TypeId;
                    if (!string.IsNullOrEmpty(entity.Tag)) jsonEntity.Properties["texture"] = entity.Tag;
                }

                manifest.Entities.Add(jsonEntity);
            }
            if (verbose) log?.Invoke($"  [JSON Dynamic Entities] Reconstructed {dynamicEntities.Count} dynamic entity placement(s)");

            // 4e. Breakables
            var breakRes = LoadDescriptorByKeyOrFallback(loader, trackTxtData, "BREAKABLES_DESCRIPTOR", trackName, "BreakDescriptor.txt");
            if (breakRes.Data != null)
            {
                ParseBreakables(breakRes.Data, manifest.Breakables);
                if (verbose) log?.Invoke($"  [JSON Breakables] Parsed {manifest.Breakables.Count} breakable entry(ies) from '{breakRes.ResolvedName}'");
            }

            // 4f. Animated Textures
            var animTexRes = LoadDescriptorByKeyOrFallback(loader, trackTxtData, "TEXTURE_ANIM_DESCRIPTOR", trackName, "TexAnimDescriptor.txt");
            if (animTexRes.Data != null)
            {
                ParseAnimatedTextures(animTexRes.Data, manifest.AnimatedTextures);
                if (verbose) log?.Invoke($"  [JSON TexAnim] Parsed {manifest.AnimatedTextures.Count} animated texture script mapping(s) from '{animTexRes.ResolvedName}'");
            }

            // 4g. Traffic Drones & Comprehensive Spline Export (.lin / .lins)
            var allDroneDescs = new List<string>(assets.DroneDescriptors);
            string defaultDrone = $"{cleanTrack}_DroneDescriptor.txt";
            if (!allDroneDescs.Contains(defaultDrone, StringComparer.OrdinalIgnoreCase) && vfs.FileExists(defaultDrone))
                allDroneDescs.Add(defaultDrone);

            var roadSplines = SplineResolver.ResolveRoadSplines(vfs, cleanTrack, variantArg != null ? $"{trackName}_{variantArg}" : trackName);
            foreach (var sp in roadSplines)
            {
                var pointsList = new List<float[]>();
                var nodesList = new List<SceneSplineNode>();
                float totalSplineLen = 0f;

                bool isClosed = sp.Points.Count >= 3 && Vector3.Distance(sp.Points[0], sp.Points[^1]) < 2.0f;

                for (int i = 0; i < sp.Points.Count; i++)
                {
                    var pt = sp.Points[i];
                    pointsList.Add(new[] { pt.X, pt.Y, pt.Z });

                    int? nextIdx = (i < sp.Points.Count - 1) ? i + 1 : (isClosed ? 0 : null);
                    int? prevIdx = (i > 0) ? i - 1 : (isClosed ? sp.Points.Count - 1 : null);

                    float distNext = 0f;
                    Vector3 forward = Vector3.UnitZ;

                    if (nextIdx.HasValue && nextIdx.Value < sp.Points.Count)
                    {
                        var nextPt = sp.Points[nextIdx.Value];
                        distNext = Vector3.Distance(pt, nextPt);
                        if (distNext > 1e-4f) forward = Vector3.Normalize(nextPt - pt);
                    }
                    else if (prevIdx.HasValue && prevIdx.Value < sp.Points.Count)
                    {
                        var prevPt = sp.Points[prevIdx.Value];
                        float dPrev = Vector3.Distance(prevPt, pt);
                        if (dPrev > 1e-4f) forward = Vector3.Normalize(pt - prevPt);
                    }

                    if (i < sp.Points.Count - 1)
                    {
                        totalSplineLen += distNext;
                    }

                    nodesList.Add(new SceneSplineNode
                    {
                        Index = i,
                        Position = new[] { pt.X, pt.Y, pt.Z },
                        NextIndex = nextIdx,
                        PrevIndex = prevIdx,
                        DistanceToNext = distNext,
                        Tangent = new[] { forward.X, forward.Y, forward.Z }
                    });
                }

                manifest.Splines.Add(new SceneSplineTrack
                {
                    Name = sp.Name,
                    File = sp.Name,
                    PointCount = sp.Points.Count,
                    TotalLength = totalSplineLen,
                    IsClosed = isClosed,
                    Points = pointsList,
                    Nodes = nodesList
                });

                manifest.Paths.Add(new ScenePath
                {
                    Id = sp.Name,
                    Category = "spline_path",
                    ClosedLoop = isClosed,
                    Points = pointsList
                });
            }

            // 5. Variants
            PopulateVariants(trackName, variantArg, vfs, manifest.Variants);
            if (verbose) log?.Invoke($"  [JSON Variants] Registered {manifest.Variants.Count} variant entry(ies) in manifesto");

            // 6. Export Single Prefab 3D Models for Engine Mode
            string prefabsDir = Path.Combine(outDir, "prefabs");
            Directory.CreateDirectory(prefabsDir);
            string cleanTrackName = TrackDiscovery.GetBaseTrackName(trackName);
            var prefabExporter = new ObjExporter(vfs, prefabsDir, false, useZeroOriginForPrefabs, verbose, true, true, cleanTrackName, log);
            var exportedPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entity in manifest.Entities)
            {
                if (string.IsNullOrWhiteSpace(entity.Prefab)) continue;
                string modelBaseName = Path.GetFileNameWithoutExtension(entity.Prefab);
                if (exportedPrefabs.Add(modelBaseName))
                {
                    string hieName = $"{modelBaseName}.hie";
                    byte[]? hieData = loader(hieName);
                    if (hieData != null)
                    {
                        string outObjPath = Path.Combine(prefabsDir, $"{modelBaseName}.obj");
                        prefabExporter.ExportHieToObj(hieData, hieName, outObjPath);
                    }
                }
            }
            if (verbose) log?.Invoke($"  [JSON Prefabs] Exported {exportedPrefabs.Count} single prefab 3D model(s) to 'EXPORT/prefabs/'");

            string jsonFileName = !string.IsNullOrWhiteSpace(variantArg) 
                ? $"{cleanTrackName}_{variantArg.ToLower()}.json" 
                : $"{cleanTrackName}.json";

            string jsonPath = Path.Combine(outDir, jsonFileName);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string jsonString = JsonSerializer.Serialize(manifest, options);
            File.WriteAllText(jsonPath, jsonString);

            // Generate 1-Click Blender Python Import Script
            string scriptPath = Path.Combine(outDir, "import_to_blender.py");
            string pythonScript = @"# TDR2000 Tools - 1-Click Blender Scene Importer
import os
import json
import bpy
import mathutils

def import_obj_file(filepath):
    if hasattr(bpy.ops.wm, 'obj_import'):
        bpy.ops.wm.obj_import(filepath=filepath)
    elif hasattr(bpy.ops.import_scene, 'obj'):
        bpy.ops.import_scene.obj(filepath=filepath)

dir_path = os.path.dirname(os.path.realpath(__file__))
json_files = [f for f in os.listdir(dir_path) if f.endswith('.json') and not f.startswith('.')]

if json_files:
    json_path = os.path.join(dir_path, json_files[0])
    with open(json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    # 1. Import Base Combined Track Mesh if present
    base_mesh = data.get('baseMesh')
    if base_mesh and os.path.exists(os.path.join(dir_path, base_mesh)):
        import_obj_file(os.path.join(dir_path, base_mesh))

    # 2. Instantiate Dynamic Entity Prefabs with native (0,0,0) origins
    entities = data.get('entities', [])
    prefab_cache = {}

    for ent in entities:
        prefab_rel = ent.get('prefab')
        if not prefab_rel: continue
        prefab_path = os.path.join(dir_path, prefab_rel)
        if not os.path.exists(prefab_path): continue

        pos = ent.get('position', [0, 0, 0])
        rot = ent.get('rotation', [0, 0, 0, 1]) # qx, qy, qz, qw
        inst_id = ent.get('id') or ent.get('instanceId', 'entity')

        if prefab_rel not in prefab_cache:
            before = set(bpy.context.scene.objects)
            import_obj_file(prefab_path)
            new_objs = list(set(bpy.context.scene.objects) - before)
            if new_objs:
                tpl = new_objs[0]
                tpl.name = f'Prefab_{os.path.basename(prefab_rel)}'
                tpl.hide_viewport = True
                tpl.hide_render = True
                prefab_cache[prefab_rel] = tpl

        tpl = prefab_cache.get(prefab_rel)
        if tpl:
            obj = bpy.data.objects.new(inst_id, tpl.data)
            bpy.context.collection.objects.link(obj)
            obj.location = mathutils.Vector((pos[0], pos[1], pos[2]))
            obj.rotation_mode = 'QUATERNION'
            obj.rotation_quaternion = mathutils.Quaternion((rot[3], rot[0], rot[1], rot[2]))
";
            File.WriteAllText(scriptPath, pythonScript);

            log?.Invoke($"  [JSON Complete] Manifest saved to '{jsonPath}' ({manifest.Entities.Count} entities, {manifest.Lights.Count} lights) + 'import_to_blender.py'");
        }

        private static void ParseEnvironment(byte[] data, SceneEnvironment env)
        {
            string[] lines = Encoding.ASCII.GetString(data).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                string[] tokens = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;

                string kw = tokens[0].ToUpperInvariant();
                if (kw == "SUN_VECTOR" && tokens.Length >= 4)
                {
                    env.SunVector = new[] {
                        float.Parse(tokens[1], CultureInfo.InvariantCulture),
                        float.Parse(tokens[2], CultureInfo.InvariantCulture),
                        float.Parse(tokens[3], CultureInfo.InvariantCulture)
                    };
                }
                else if (kw == "SUN_LIGHT_COLOUR" && tokens.Length >= 4)
                {
                    env.SunColor = new[] {
                        float.Parse(tokens[1], CultureInfo.InvariantCulture),
                        float.Parse(tokens[2], CultureInfo.InvariantCulture),
                        float.Parse(tokens[3], CultureInfo.InvariantCulture)
                    };
                }
                else if (kw == "AMBIENT_LIGHT")
                {
                    env.AmbientLight = float.Parse(tokens[1], CultureInfo.InvariantCulture);
                }
                else if (kw == "AMBIENT_LIGHT_COLOUR" && tokens.Length >= 4)
                {
                    env.AmbientColor = new[] {
                        float.Parse(tokens[1], CultureInfo.InvariantCulture),
                        float.Parse(tokens[2], CultureInfo.InvariantCulture),
                        float.Parse(tokens[3], CultureInfo.InvariantCulture)
                    };
                }
                else if (kw == "FOG_TRACK")
                {
                    env.Fog.TrackFogEnabled = tokens[1] == "1";
                }
                else if (kw == "FOG_TRACK_COLOUR" && tokens.Length >= 4)
                {
                    env.Fog.TrackFogColor = new[] {
                        float.Parse(tokens[1], CultureInfo.InvariantCulture),
                        float.Parse(tokens[2], CultureInfo.InvariantCulture),
                        float.Parse(tokens[3], CultureInfo.InvariantCulture)
                    };
                }
            }
        }

        private static void ParseLights(byte[] data, List<SceneLight> lights)
        {
            string[] lines = Encoding.ASCII.GetString(data).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 1;
            foreach (string line in lines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                string[] tokens = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 5) continue;

                if (float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                    float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
                {
                    lights.Add(new SceneLight
                    {
                        Id = $"light_{idx:D3}",
                        Type = "point",
                        Position = new[] { x, y, z },
                        Radius = radius,
                        Color = new[] { 1.0f, 0.9f, 0.7f }
                    });
                    idx++;
                }
            }
        }

        private static void ParseSoundEmitters(byte[] data, List<SceneSoundEmitter> soundEmitters)
        {
            string[] lines = Encoding.ASCII.GetString(data).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            int idx = 1;
            foreach (string line in lines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;

                string soundName = parts[0].Trim('"');
                if (float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    soundEmitters.Add(new SceneSoundEmitter
                    {
                        Id = $"sound_{idx:D3}",
                        Name = soundName,
                        Position = new[] { x, y, z }
                    });
                    idx++;
                }
            }
        }



        private static void ParseBreakables(byte[] breakData, List<SceneBreakable> breakables)
        {
            string text = Encoding.ASCII.GetString(breakData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2) return;

            string hierarchy = lines[0].Trim().Trim('"');
            for (int i = 2; i < lines.Length; i++)
            {
                string line = lines[i];
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line[..commentIdx];
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    breakables.Add(new SceneBreakable
                    {
                        Hierarchy = hierarchy,
                        TextureName = parts[0].Trim('"'),
                        BreakSound = parts[2].Trim('"')
                    });
                }
            }
        }

        private static void ParseAnimatedTextures(byte[] animTexData, List<SceneAnimatedTexture> animatedTextures)
        {
            string text = Encoding.ASCII.GetString(animTexData);
            string[] lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string line in lines)
            {
                string cleanLine = line;
                int commentIdx = cleanLine.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) cleanLine = cleanLine[..commentIdx];
                string trimmed = cleanLine.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                {
                    animatedTextures.Add(new SceneAnimatedTexture
                    {
                        AnimationScript = parts[0].Trim('"'),
                        TextureToAnimate = parts[1].Trim('"')
                    });
                }
            }
        }

        private static void PopulateVariants(string trackName, string? variantArg, PakManager vfs, Dictionary<string, SceneVariant> variants)
        {
            if (!string.IsNullOrWhiteSpace(variantArg))
            {
                string key = variantArg.ToLowerInvariant();
                variants[key] = new SceneVariant
                {
                    Consoft = $"{trackName}_{key}_consoft.obj"
                };
                return;
            }

            string tName = trackName.ToLowerInvariant();
            var varFiles = vfs.GetFiles()
                .Where(f => f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .Where(f => {
                    string fn = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                    if (fn == tName || fn.Contains("script") || fn.Contains("collision")) return false;
                    return fn.StartsWith(tName + "_race") || fn.StartsWith(tName + "_mission") || fn.StartsWith(tName + "_mp");
                })
                .Select(f => Path.GetFileNameWithoutExtension(f.Name))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (string varName in varFiles)
            {
                string key = varName.Substring(tName.Length).TrimStart('_').ToLowerInvariant();
                variants[key] = new SceneVariant
                {
                    Consoft = $"{trackName}_{key}_consoft.obj"
                };
            }
        }

        private static void ParseDrones(byte[] droneData, List<TDRSpline> roadSplines, List<SceneEntity> entities)
        {
            string[] descLines = Encoding.ASCII.GetString(droneData).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var droneRequests = new List<(string Name, int Count)>();
            foreach (string line in descLines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;
                string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], out int count) && count > 0)
                {
                    droneRequests.Add((parts[0].Trim('"'), count));
                }
            }

            if (droneRequests.Count == 0 || roadSplines == null || roadSplines.Count == 0) return;

            int totalDrones = droneRequests.Sum(r => r.Count);
            var spawnMatrices = SplineResolver.GenerateSpawnMatrices(roadSplines, totalDrones);

            int spawnIdx = 0;
            foreach (var req in droneRequests)
            {
                string baseName = req.Name
                    .Replace("MAIN_NULL_PED", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("MAIN_NULL", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("_PED", "", StringComparison.OrdinalIgnoreCase)
                    .Trim('_');

                for (int i = 0; i < req.Count; i++)
                {
                    Matrix4x4 spawnMat = spawnMatrices[spawnIdx % spawnMatrices.Count];
                    spawnIdx++;

                    var q = Quaternion.CreateFromRotationMatrix(spawnMat);

                    entities.Add(new SceneEntity
                    {
                        Id = $"Drone_{baseName}_{i + 1:D2}",
                        Category = "drone",
                        Prefab = $"prefabs/{baseName}.obj",
                        Position = new[] { spawnMat.M41, spawnMat.M42, spawnMat.M43 },
                        Rotation = new[] { q.X, q.Y, q.Z, q.W }
                    });
                }
            }
        }

        private static string? GetDescriptorValue(byte[]? txtData, string keyword)
        {
            if (txtData == null || txtData.Length == 0) return null;
            string text = Encoding.UTF8.GetString(txtData).TrimStart('\uFEFF');
            using var sr = new StringReader(text);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                int commentIdx = line.IndexOf("//", StringComparison.Ordinal);
                if (commentIdx >= 0) line = line[..commentIdx];
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;

                string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return parts[1].Trim('"');
                }
            }
            return null;
        }

        private static (byte[]? Data, string ResolvedName) LoadDescriptorByKeyOrFallback(
            Func<string, byte[]?> loader,
            byte[]? masterTxtData,
            string keyword,
            string trackName,
            string descriptorSuffix)
        {
            string? explicitName = GetDescriptorValue(masterTxtData, keyword);
            if (!string.IsNullOrWhiteSpace(explicitName))
            {
                byte[]? data = loader(explicitName);
                if (data != null) return (data, explicitName);
            }

            // Fallback 1: {trackName}_{descriptorSuffix} (e.g. 1920s_Volumes.h)
            string fallbackWithUnderscore = $"{trackName}_{descriptorSuffix}";
            byte[]? fbData1 = loader(fallbackWithUnderscore);
            if (fbData1 != null) return (fbData1, fallbackWithUnderscore);

            // Fallback 2: {trackName}{descriptorSuffix} (e.g. 1920sVolumes.h)
            string fallbackMerged = $"{trackName}{descriptorSuffix}";
            byte[]? fbData2 = loader(fallbackMerged);
            if (fbData2 != null) return (fbData2, fallbackMerged);

            return (null, explicitName ?? fallbackWithUnderscore);
        }
    }
}
