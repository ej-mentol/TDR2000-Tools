using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        public static void GenerateManifest(string trackName, string? variantArg, PakManager vfs, string outDir, Func<string, byte[]?> loader, TrackExportResult? exportResult = null, bool verbose = false, Action<string>? log = null)
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

            // 1. Base Environment & Atmosphere (from master txt)
            byte[]? trackTxtData = loader($"{trackName}.txt");
            if (trackTxtData != null)
            {
                ParseEnvironment(trackTxtData, manifest.Environment);
                if (verbose) log?.Invoke($"  [JSON Env] Parsed base atmosphere parameters from '{trackName}.txt'");
            }

            // 1b. Variant Environment Overrides (if specified)
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

            // 2. Lights
            byte[]? lightsData = loader($"{trackName}_LightsDescriptor.txt");
            if (lightsData != null)
            {
                ParseLights(lightsData, manifest.Lights);
                if (verbose) log?.Invoke($"  [JSON Lights] Parsed {manifest.Lights.Count} light fixture(s) from '{trackName}_LightsDescriptor.txt'");
            }

            // 2b. 3D Sound Emitters
            byte[]? sndData = loader($"{trackName}_AmbientSndDescriptor.txt");
            if (sndData != null)
            {
                ParseSoundEmitters(sndData, manifest.SoundEmitters);
                if (verbose) log?.Invoke($"  [JSON Sound Emitters] Parsed {manifest.SoundEmitters.Count} 3D sound emitter(s) from '{trackName}_AmbientSndDescriptor.txt'");
            }

            // 2c. Surface Physics Materials
            byte[]? hData = loader($"{trackName}Volumes.h");
            if (hData != null)
            {
                var mats = TDR.PakLib.Formats.HParser.Parse(hData);
                foreach (var kvp in mats) manifest.SurfaceMaterials[kvp.Key] = kvp.Value;
                if (verbose) log?.Invoke($"  [JSON Surface Physics] Parsed {mats.Count} surface material physics entry(ies) from '{trackName}Volumes.h'");
            }

            // 3. Movables
            byte[]? movablesData = loader($"{trackName}_MoveableDescriptor.txt");
            if (movablesData != null)
            {
                int before = manifest.Entities.Count;
                ParseMovables(movablesData, manifest.Entities);
                if (verbose) log?.Invoke($"  [JSON Movables] Parsed {manifest.Entities.Count - before} movable entity placement(s)");
            }

            // 4. Pedestrians
            byte[]? pedData = loader($"{trackName}_Ped_Placement.txt");
            if (pedData != null)
            {
                int before = manifest.Entities.Count;
                byte[]? pedDescData = loader($"{trackName}_PedDescriptor.txt");
                ParsePedestrians(pedData, pedDescData, manifest.Entities);
                if (verbose) log?.Invoke($"  [JSON Pedestrians] Parsed {manifest.Entities.Count - before} pedestrian placement(s)");
            }

            // 4b. Powerups (.pup)
            string targetPup = !string.IsNullOrWhiteSpace(variantArg) ? $"{trackName}_{variantArg}.pup" : $"{trackName}_Race1.pup";
            byte[]? pupData = loader(targetPup) ?? loader($"{trackName}.pup");
            if (pupData != null)
            {
                int before = manifest.Entities.Count;
                ParsePowerups(pupData, manifest.Entities);
                if (verbose) log?.Invoke($"  [JSON Powerups] Parsed {manifest.Entities.Count - before} powerup placement(s) from '{targetPup}'");
            }

            // 4c. Breakables
            byte[]? breakData = loader($"{trackName}_BreakDescriptor.txt");
            if (breakData != null)
            {
                ParseBreakables(breakData, manifest.Breakables);
                if (verbose) log?.Invoke($"  [JSON Breakables] Parsed {manifest.Breakables.Count} breakable entry(ies) from '{trackName}_BreakDescriptor.txt'");
            }

            // 4d. Animated Textures
            byte[]? animTexData = loader($"{trackName}_TexAnimDescriptor.txt");
            if (animTexData != null)
            {
                ParseAnimatedTextures(animTexData, manifest.AnimatedTextures);
                if (verbose) log?.Invoke($"  [JSON TexAnim] Parsed {manifest.AnimatedTextures.Count} animated texture script mapping(s)");
            }

            // 5. Variants
            PopulateVariants(trackName, variantArg, vfs, manifest.Variants);
            if (verbose) log?.Invoke($"  [JSON Variants] Registered {manifest.Variants.Count} variant entry(ies) in manifesto");

            // 6. Export Single Prefab 3D Models for Engine Mode
            string prefabsDir = Path.Combine(outDir, "prefabs");
            Directory.CreateDirectory(prefabsDir);
            var prefabExporter = new ObjExporter(vfs, prefabsDir, false, true, verbose, true, true, null, log);
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

            string cleanTrackName = TrackDiscovery.GetBaseTrackName(trackName);
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

dir_path = os.path.dirname(os.path.realpath(__file__))
json_files = [f for f in os.listdir(dir_path) if f.endswith('.json') and not f.startswith('.')]

if json_files:
    json_path = os.path.join(dir_path, json_files[0])
    with open(json_path, 'r', encoding='utf-8') as f:
        data = json.load(f)

    # 1. Import Base Combined Track Mesh if present
    base_mesh = data.get('baseMesh')
    if base_mesh and os.path.exists(os.path.join(dir_path, base_mesh)):
        bpy.ops.import_scene.obj(filepath=os.path.join(dir_path, base_mesh))

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
        inst_id = ent.get('instanceId', 'entity')

        if prefab_rel not in prefab_cache:
            before = set(bpy.context.scene.objects)
            bpy.ops.import_scene.obj(filepath=prefab_path)
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

        private static void ParseMovables(byte[] data, List<SceneEntity> entities)
        {
            string[] lines = Encoding.ASCII.GetString(data).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in lines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 8) continue;

                string hieName = parts[0].Trim('"');
                string modelBaseName = Path.GetFileNameWithoutExtension(hieName);
                int instIdx = counts.GetValueOrDefault(modelBaseName, 0) + 1;
                counts[modelBaseName] = instIdx;

                float px = float.Parse(parts[1], CultureInfo.InvariantCulture);
                float py = float.Parse(parts[2], CultureInfo.InvariantCulture);
                float pz = float.Parse(parts[3], CultureInfo.InvariantCulture);
                float qx = float.Parse(parts[4], CultureInfo.InvariantCulture);
                float qy = float.Parse(parts[5], CultureInfo.InvariantCulture);
                float qz = float.Parse(parts[6], CultureInfo.InvariantCulture);
                float qw = float.Parse(parts[7], CultureInfo.InvariantCulture);

                entities.Add(new SceneEntity
                {
                    Id = $"{modelBaseName}_{instIdx:D3}",
                    Category = "movable",
                    Prefab = $"prefabs/{modelBaseName}.obj",
                    Position = new[] { px, py, pz },
                    Rotation = new[] { qx, qy, qz, qw }
                });
            }
        }

        private static void ParsePedestrians(byte[] pedData, byte[]? pedDescData, List<SceneEntity> entities)
        {
            var pedClasses = new List<string>();
            if (pedDescData != null)
            {
                string[] descLines = Encoding.ASCII.GetString(pedDescData).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string line in descLines)
                {
                    string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                    if (clean.EndsWith("Descriptor.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        string className = clean.Replace("Skeleton Descriptor.txt", "").Trim('"').Trim();
                        pedClasses.Add(className);
                    }
                }
            }

            string[] lines = Encoding.ASCII.GetString(pedData).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in lines)
            {
                string clean = line.Contains("//") ? line[..line.IndexOf("//")].Trim() : line.Trim();
                if (string.IsNullOrWhiteSpace(clean)) continue;

                string[] parts = clean.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 7) continue;

                if (parts[0] != "1") continue; // Skip disabled spawners

                int classId = int.Parse(parts[1], CultureInfo.InvariantCulture);
                float px = float.Parse(parts[3], CultureInfo.InvariantCulture);
                float py = float.Parse(parts[4], CultureInfo.InvariantCulture);
                float pz = float.Parse(parts[5], CultureInfo.InvariantCulture);
                float heading = float.Parse(parts[6], CultureInfo.InvariantCulture);

                string className = classId >= 0 && classId < pedClasses.Count ? pedClasses[classId] : $"Pedestrian_Class_{classId}";
                int instIdx = counts.GetValueOrDefault(className, 0) + 1;
                counts[className] = instIdx;

                entities.Add(new SceneEntity
                {
                    Id = $"{className}_{instIdx:D3}",
                    Category = "pedestrian",
                    Prefab = $"prefabs/{className}.obj",
                    Position = new[] { px, py, pz },
                    Heading = heading
                });
            }
        }

        private static void ParsePowerups(byte[] pupData, List<SceneEntity> entities)
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
                    string cleanComment = lastCommentName.Replace(' ', '_').Replace('!', '_').Replace('.', '_');
                    entities.Add(new SceneEntity
                    {
                        Id = $"powerup_{pupIndex:D3}_{cleanComment}",
                        Category = "powerup",
                        Prefab = $"prefabs/powerups/{cleanComment}.obj",
                        Position = new[] { px, py, pz },
                        Heading = 0.0f
                    });
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
    }
}
