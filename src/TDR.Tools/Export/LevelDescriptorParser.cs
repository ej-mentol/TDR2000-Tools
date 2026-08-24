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
        public Vector3? StartPosition { get; set; }
        public float? StartAngle { get; set; }
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
            "SPECIAL_VOLUMES", "SPECIAL_VOLUMES_0", "LIGHTS_DESCRIPTOR", "PATH_FOLLOWERS"
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

        public static byte[]? LoadDescriptorBytes(PakManager vfs, string? trackContext, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName)) return null;

            // Tier 1: Exact track context (e.g. "Hollowood_Race1")
            byte[]? bytes = !string.IsNullOrEmpty(trackContext) ? vfs.LoadFileContext(fileName, trackContext) : null;

            // Tier 2: Base track family (e.g. "Hollowood" if track is "Hollowood_Race1")
            if (bytes == null && !string.IsNullOrEmpty(trackContext))
            {
                string baseFamily = TrackDiscovery.GetBaseTrackName(trackContext);
                if (!string.IsNullOrEmpty(baseFamily) && !baseFamily.Equals(trackContext, StringComparison.OrdinalIgnoreCase))
                {
                    bytes = vfs.LoadFileContext(fileName, baseFamily);
                }
            }

            // Tier 3: Global VFS search
            bytes ??= vfs.LoadFile(fileName);
            return bytes;
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

            // Dynamic Path Followers (e.g. Sharks, Boats, Biplanes, Stukas)
            if (text.Contains("@") || text.Contains("sway", StringComparison.OrdinalIgnoreCase) || text.Contains("follower", StringComparison.OrdinalIgnoreCase))
            {
                var pathFollowers = PathFollowerDescriptor.Load(text);
                if (pathFollowers.Followers.Count > 0)
                {
                    foreach (var follower in pathFollowers.Followers)
                    {
                        if (string.IsNullOrWhiteSpace(follower.ModelHie)) continue;

                        TDRSpline? followerSpline = null;
                        Matrix4x4 splineNodeTransform = Matrix4x4.Identity;

                        if (!string.IsNullOrWhiteSpace(follower.PathHie))
                        {
                            byte[]? pathHieBytes = LoadDescriptorBytes(vfs, trackContext, follower.PathHie);
                            if (pathHieBytes != null && pathHieBytes.Length > 0)
                            {
                                var hie = TDRHierarchy.Load(pathHieBytes, follower.PathHie);
                                var lineNames = ExtractLineNamesFromHie(pathHieBytes);
                                foreach (var node in hie.Nodes)
                                {
                                    if (node.Type == TDRNode.NodeType.Spline && lineNames.Count > 0 && node.Index < lineNames.Count)
                                    {
                                        string splineFileName = lineNames[node.Index];
                                        byte[]? spBytes = LoadDescriptorBytes(vfs, trackContext, splineFileName);
                                        if (spBytes != null && spBytes.Length > 0)
                                        {
                                            string optShortName = Path.ChangeExtension(Path.GetFileName(splineFileName), ".txt");
                                            byte[]? optBytes = LoadDescriptorBytes(vfs, trackContext, optShortName);
                                            var container = TDRSplineContainer.Load(spBytes, splineFileName, optBytes);
                                            var sp = container.Splines.Find(s => s.Points.Count >= 2);
                                            if (sp != null)
                                            {
                                                followerSpline = sp;
                                                splineNodeTransform = node.Transform;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        if (followerSpline != null && followerSpline.Points.Count >= 2)
                        {
                            float totalSplineLen = 0f;
                            for (int pi = 0; pi < followerSpline.Points.Count - 1; pi++)
                            {
                                totalSplineLen += Vector3.Distance(followerSpline.Points[pi], followerSpline.Points[pi + 1]);
                            }
                            float targetDist = (follower.StartProgress % 1.0f) * totalSplineLen;
                            Matrix4x4 localSplineMat = SplineResolver.SampleSplineAtDistance(followerSpline, targetDist, 0.0f);
                            Matrix4x4 spawnMat = splineNodeTransform * localSplineMat * parentMatrix;

                            assets.HieInstances.Add(new HieInstanceInfo
                            {
                                HieName = follower.ModelHie,
                                Transform = spawnMat
                            });
                            assets.HieInitialTransforms[follower.ModelHie] = spawnMat;
                        }
                    }

                    // Path followers have been placed explicitly along their splines; do not fall through to unplaced loading
                    return;
                }
            }

            // Spline-guided vehicle/train initial placement (e.g. DocksTrain, SlumsTrain)
            if (hieCandidates.Count > 0)
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
                    return;
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

                string firstEntry = tokens[0].Trim('"');
                string hieCandidate = firstEntry.EndsWith(".hie", StringComparison.OrdinalIgnoreCase) ? firstEntry : $"{firstEntry}.hie";
                float px = 0, py = 0, pz = 0, qx = 0, qy = 0, qz = 0, qw = 1;
                if (tokens.Length >= 8 &&
                    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out px) &&
                    float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out py) &&
                    float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out pz) &&
                    float.TryParse(tokens[4], NumberStyles.Float, CultureInfo.InvariantCulture, out qx) &&
                    float.TryParse(tokens[5], NumberStyles.Float, CultureInfo.InvariantCulture, out qy) &&
                    float.TryParse(tokens[6], NumberStyles.Float, CultureInfo.InvariantCulture, out qz) &&
                    float.TryParse(tokens[7], NumberStyles.Float, CultureInfo.InvariantCulture, out qw))
                {
                    var q = new Quaternion(qx, qy, qz, qw);
                    Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(q);
                    Matrix4x4 localTransform = rotMat * Matrix4x4.CreateTranslation(px, py, pz);
                    Matrix4x4 worldTransform = localTransform * parentMatrix;

                    assets.HieInstances.Add(new HieInstanceInfo
                    {
                        HieName = hieCandidate,
                        Transform = worldTransform
                    });
                    continue;
                }

                foreach (string rawToken in tokens)
                {
                    string entry = rawToken.Trim('"');
                    if (string.IsNullOrWhiteSpace(entry)) continue;

                    if (entry.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!assets.HieFiles.Contains(entry, StringComparer.OrdinalIgnoreCase))
                        {
                            assets.HieFiles.Add(entry);
                        }
                    }
                    else if (entry.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(entry))
                    {
                        byte[]? subBytes = LoadDescriptorBytes(vfs, trackContext, entry);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            ParseSubDescriptorHieFiles(vfs, trackContext, subBytes, visitedDescriptors, assets, parentMatrix, isStaticMeshDescriptor);
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

                if (firstToken.Equals("START_POS", StringComparison.OrdinalIgnoreCase) && tokens.Length >= 4)
                {
                    if (float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float sx) &&
                        float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float sy) &&
                        float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float sz))
                    {
                        result.StartPosition = new Vector3(sx, sy, sz);
                    }
                    continue;
                }

                if (firstToken.Equals("START_ANGLE", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(secondToken, NumberStyles.Float, CultureInfo.InvariantCulture, out float sa))
                    {
                        result.StartAngle = sa;
                    }
                    continue;
                }

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
                    firstToken.Equals("ZOMBIES_DESCRIPTOR", StringComparison.OrdinalIgnoreCase) ||
                    firstToken.Equals("ALIENS_DESCRIPTOR", StringComparison.OrdinalIgnoreCase) ||
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
                        byte[]? subBytes = LoadDescriptorBytes(vfs, trackContext, secondToken);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            ParseSubDescriptorHieFiles(vfs, trackContext, subBytes, visitedDescriptors, result, Matrix4x4.Identity, isStaticMeshDescriptor: true);
                        }
                    }
                    continue;
                }

                if (SubDescriptorKeywords.Contains(firstToken, StringComparer.OrdinalIgnoreCase))
                {
                    if (secondToken.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && visitedDescriptors.Add(secondToken))
                    {
                        byte[]? subBytes = LoadDescriptorBytes(vfs, trackContext, secondToken);
                        if (subBytes != null && subBytes.Length > 0)
                        {
                            ParseSubDescriptorHieFiles(vfs, trackContext, subBytes, visitedDescriptors, result, Matrix4x4.Identity, isStaticMeshDescriptor: true);
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
