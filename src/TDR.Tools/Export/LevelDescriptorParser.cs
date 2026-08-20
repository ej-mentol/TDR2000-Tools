using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Export
{
    public sealed record HieInstanceInfo
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
        public List<string> DroneDescriptors { get; } = new();
        public Dictionary<string, Matrix4x4> HieInitialTransforms { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Canonical parser for TDR2000 level master descriptors, sub-descriptors, and placements.
    /// Single Source of Truth for OBJ, glTF, and SceneJson.
    /// </summary>
    public static class LevelDescriptorParser
    {
        public static readonly string[] DirectHieKeywords = new[]
        {
            "STATIC_MESH", "ROAD_SPLINES", "DYNAMIC_TRACK_OBJECT", "ENVIRONMENT",
            "NON_CAR_OBJECT", "WATER_MESH", "SKY_MESH", "SKY_SPHERE", "CAMERA", "LIGHT_SOURCES",
            "BASE_CONSOFT", "CONSOFT", "LEVEL_MESH", "HARDSHADOW_HIE"
        };

        public static readonly string[] SubDescriptorKeywords = new[]
        {
            "BREAKABLES_DESCRIPTOR", "STATIC_MESH_DESCRIPTOR", "CONSOFT_DESCRIPTOR",
            "LEVEL_CONSOFT", "PRE_CALCULATED_SPLINES", "ANIMATED_SPECIAL_EFFECTS",
            "ANIMATED_PROPS", "ARTICULATED_BRIDGES", "SKY_DESCRIPTOR",
            "SPECIAL_VOLUMES", "SPECIAL_VOLUMES_0", "LIGHTS_DESCRIPTOR"
        };

        public static List<string> ExtractLineNamesFromHie(byte[] hieBytes)
        {
            var list = new List<string>();
            if (hieBytes == null || hieBytes.Length == 0) return list;

            string text = Encoding.ASCII.GetString(hieBytes);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim().Equals("// line name list", StringComparison.OrdinalIgnoreCase))
                {
                    while (i + 1 < lines.Length)
                    {
                        string next = lines[++i].Trim();
                        if (next.StartsWith("//")) break;
                        string clean = next.Replace("\"", "").Trim();
                        if (!string.IsNullOrWhiteSpace(clean)) list.Add(clean);
                    }
                    break;
                }
            }
            return list;
        }

        public static void ParseSubDescriptorHieFiles(
            PakManager vfs,
            string? trackContext,
            byte[] subDescriptorBytes,
            HashSet<string> visitedDescriptors,
            DescriptorAssets assets,
            Matrix4x4 parentMatrix,
            bool isStaticMeshDescriptor = false)
        {
            if (subDescriptorBytes == null || subDescriptorBytes.Length == 0) return;

            string text = Encoding.ASCII.GetString(subDescriptorBytes);
            var hieCandidates = new List<string>();
            var linCandidates = new List<string>();

            foreach (string rawLine in text.Split('\n'))
            {
                string cleanLine = rawLine;
                int cIdx = cleanLine.IndexOf("//", StringComparison.Ordinal);
                if (cIdx >= 0) cleanLine = cleanLine.Substring(0, cIdx);
                cleanLine = cleanLine.Trim();
                if (string.IsNullOrWhiteSpace(cleanLine)) continue;

                string[] tokens = cleanLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawToken in tokens)
                {
                    string entry = rawToken.Trim('"');
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    if (entry.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!hieCandidates.Contains(entry, StringComparer.OrdinalIgnoreCase))
                            hieCandidates.Add(entry);
                    }
                    else if (entry.EndsWith(".lin", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".lins", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!linCandidates.Contains(entry, StringComparer.OrdinalIgnoreCase))
                            linCandidates.Add(entry);
                    }
                }
            }

            // Spline-guided vehicle/train initial placement
            if (hieCandidates.Count > 0 && (hieCandidates.Count > 1 || linCandidates.Count > 0))
            {
                TDRSpline? propSpline = null;
                Matrix4x4 splineNodeTransform = Matrix4x4.Identity;

                foreach (string linName in linCandidates)
                {
                    byte[]? linBytes = vfs.LoadFileContext(linName, trackContext);
                    if (linBytes != null && linBytes.Length > 0)
                    {
                        string optShortName = Path.ChangeExtension(Path.GetFileName(linName), ".txt");
                        byte[]? optBytes = vfs.LoadFileContext(optShortName, trackContext);
                        var container = TDRSplineContainer.Load(linBytes, linName, optBytes);
                        var sp = container.Splines.Find(s => s.Points.Count >= 2);
                        if (sp != null)
                        {
                            propSpline = sp;
                            break;
                        }
                    }
                }

                var modelHies = new List<string>();
                foreach (string hieCandidate in hieCandidates)
                {
                    byte[]? hieBytes = vfs.LoadFileContext(hieCandidate, trackContext);
                    if (hieBytes != null && hieBytes.Length > 0)
                    {
                        var hie = TDRHierarchy.Load(hieBytes, hieCandidate);
                        if (hie.Meshes.Count > 0)
                        {
                            modelHies.Add(hieCandidate);
                        }
                        else if (propSpline == null)
                        {
                            var lineNames = ExtractLineNamesFromHie(hieBytes);
                            foreach (var node in hie.Nodes)
                            {
                                if (node.Type == TDRNode.NodeType.Spline && lineNames.Count > 0 && node.Index < lineNames.Count)
                                {
                                    string splineFileName = lineNames[node.Index];
                                    byte[]? spBytes = vfs.LoadFileContext(splineFileName, trackContext);
                                    if (spBytes != null && spBytes.Length > 0)
                                    {
                                        string optShortName = Path.ChangeExtension(Path.GetFileName(splineFileName), ".txt");
                                        byte[]? optBytes = vfs.LoadFileContext(optShortName, trackContext);
                                        var container = TDRSplineContainer.Load(spBytes, splineFileName, optBytes);
                                        var sp = container.Splines.Find(s => s.Points.Count >= 2);
                                        if (sp != null)
                                        {
                                            propSpline = sp;
                                            splineNodeTransform = node.Transform;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (modelHies.Count > 0 && propSpline != null && propSpline.Points.Count > 0)
                {
                    float carriageSpacing = 14.0f; // Standard TDR2000 train wagon length in meters
                    for (int hIdx = 0; hIdx < modelHies.Count; hIdx++)
                    {
                        string modelHie = modelHies[hIdx];
                        float targetDist = hIdx * carriageSpacing;
                        Matrix4x4 localSplineMat = SplineResolver.SampleSplineAtDistance(propSpline, targetDist, 0.35f);

                        // If this is the rear head / caboose locomotive (e.g. DocksTrain4 / Choo_Choo4), rotate 180 deg around Y (<===>)
                        bool isRearHead = (hIdx == modelHies.Count - 1 && modelHies.Count > 1) &&
                                          (modelHie.Contains("train4", StringComparison.OrdinalIgnoreCase) ||
                                           modelHie.Contains("train5b", StringComparison.OrdinalIgnoreCase) ||
                                           modelHie.Contains("choo_choo4", StringComparison.OrdinalIgnoreCase) ||
                                           modelHie.Contains("rear", StringComparison.OrdinalIgnoreCase) ||
                                           modelHie.Contains("caboose", StringComparison.OrdinalIgnoreCase));

                        if (isRearHead)
                        {
                            localSplineMat = Matrix4x4.CreateRotationY(MathF.PI) * localSplineMat;
                        }

                        Matrix4x4 spawnMat = splineNodeTransform * localSplineMat * parentMatrix;
                        assets.HieInstances.Add(new HieInstanceInfo
                        {
                            HieName = modelHie,
                            Transform = spawnMat
                        });
                        assets.HieInitialTransforms[modelHie] = spawnMat;
                    }
                }
            }

            foreach (string rawLine in text.Split('\n'))
            {
                string cleanLine = rawLine;
                int cIdx = cleanLine.IndexOf("//", StringComparison.Ordinal);
                if (cIdx >= 0) cleanLine = cleanLine.Substring(0, cIdx);
                cleanLine = cleanLine.Trim();
                if (string.IsNullOrWhiteSpace(cleanLine)) continue;

                string[] tokens = cleanLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) continue;

                int coordStart = -1;
                for (int i = 0; i < tokens.Length - 2; i++)
                {
                    if (float.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                        float.TryParse(tokens[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                        float.TryParse(tokens[i + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    {
                        coordStart = i;
                        break;
                    }
                }

                Matrix4x4 localTransform = Matrix4x4.Identity;
                if (coordStart >= 0)
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

                foreach (string rawToken in tokens)
                {
                    string entry = rawToken.Trim('"');
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    if (entry.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        if (coordStart >= 0)
                        {
                            assets.HieInstances.Add(new HieInstanceInfo
                            {
                                HieName = entry,
                                Transform = worldTransform
                            });
                        }
                        else if (isStaticMeshDescriptor)
                        {
                            // If this HIE was already bound to a spline instance (like DocksTrain), do not treat it as unpositioned static scene layer
                            bool alreadyInstanced = assets.HieInstances.Any(inst => inst.HieName.Equals(entry, StringComparison.OrdinalIgnoreCase));
                            if (!alreadyInstanced && !assets.HieFiles.Contains(entry, StringComparer.OrdinalIgnoreCase))
                                assets.HieFiles.Add(entry);
                        }
                    }
                    else if (entry.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(entry))
                    {
                        byte[]? subBytes = vfs.LoadFileContext(entry, trackContext);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            ParseSubDescriptorHieFiles(vfs, trackContext, subBytes, visitedDescriptors, assets, worldTransform, isStaticMeshDescriptor);
                        }
                    }
                }
            }
        }

        public static DescriptorAssets ParseLevelDescriptorAssets(
            PakManager vfs,
            string? trackContext,
            byte[] descriptorBytes,
            HashSet<string>? visitedDescriptors = null)
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
                if (string.IsNullOrWhiteSpace(secondToken)) continue;

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
                    if (secondToken.EndsWith(".hie", StringComparison.OrdinalIgnoreCase) && !result.HieFiles.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                    {
                        result.HieFiles.Add(secondToken);
                    }
                    continue;
                }

                if (firstToken.Equals("MOVABLE_OBJECTS", StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.MovableDescriptors.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                        result.MovableDescriptors.Add(secondToken);
                    continue;
                }

                if (firstToken.Equals("PEDS_DESCRIPTOR", StringComparison.OrdinalIgnoreCase) ||
                    firstToken.Equals("PEDESTRIAN_PLACEMENT", StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.PedestrianDescriptors.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                        result.PedestrianDescriptors.Add(secondToken);
                    continue;
                }

                if (firstToken.Equals("DRONE_DESCRIPTOR", StringComparison.OrdinalIgnoreCase))
                {
                    if (!result.DroneDescriptors.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                        result.DroneDescriptors.Add(secondToken);
                    continue;
                }

                if (DirectHieKeywords.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
                {
                    if (secondToken.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!result.HieFiles.Contains(secondToken, StringComparer.OrdinalIgnoreCase))
                            result.HieFiles.Add(secondToken);
                    }
                    else if (secondToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(secondToken))
                    {
                        byte[]? subBytes = vfs.LoadFileContext(secondToken, trackContext);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            bool isStatic = firstToken.Equals("STATIC_MESH", StringComparison.OrdinalIgnoreCase) ||
                                            firstToken.Equals("LEVEL_MESH", StringComparison.OrdinalIgnoreCase);
                            ParseSubDescriptorHieFiles(vfs, trackContext, subBytes, visitedDescriptors, result, Matrix4x4.Identity, isStatic);
                        }
                    }
                    continue;
                }

                if (SubDescriptorKeywords.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
                {
                    if (secondToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(secondToken))
                    {
                        byte[]? subBytes = vfs.LoadFileContext(secondToken, trackContext);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            bool isStatic = !firstToken.Equals("BREAKABLES_DESCRIPTOR", StringComparison.OrdinalIgnoreCase);
                            ParseSubDescriptorHieFiles(vfs, trackContext, subBytes, visitedDescriptors, result, Matrix4x4.Identity, isStatic);
                        }
                    }
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(pendingWaterMesh) && waterLevel.HasValue)
            {
                result.HieInitialTransforms[pendingWaterMesh] = Matrix4x4.CreateTranslation(0, waterLevel.Value, 0);
            }

            return result;
        }
    }
}
