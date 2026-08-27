using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Export;
using TDR.Tools.ViewModels;

namespace TDR.Tools.Services
{
    /// <summary>
    /// Service responsible for constructing and classifying hierarchical scene trees (layers, variants, HIE meshes,
    /// semantic subfolders, and asset origins) for track conversion and export modals.
    /// </summary>
    public static class TrackTreeBuilder
    {
        public static void PopulateModalTree(PakManager vfs, ConvertTrackModalViewModel modalVm, string? sourceRootPath)
        {
            if (vfs == null || modalVm == null || string.IsNullOrWhiteSpace(modalVm.TrackName))
                return;

            modalVm.HieTreeNodes.Clear();
            string cleanName = TrackDiscovery.GetBaseTrackName(modalVm.TrackName);
            string tPrefix = cleanName.ToLowerInvariant();
            string trackFolderPrefix = $"tracks/{tPrefix}";

            // 1. Gather all HIE files referenced by level descriptors for this track family (base and all variants)
            var descriptorHies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var baseDescriptorHies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var variantDescriptorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string? baseTxt = TrackExportPipeline.ResolveTrackDescriptor(vfs, cleanName, null);
            if (baseTxt != null)
            {
                byte[]? baseBytes = vfs.LoadFile(baseTxt);
                if (baseBytes != null && baseBytes.Length > 0)
                {
                    var baseAssets = LevelDescriptorParser.ParseLevelDescriptorAssets(vfs, cleanName, baseBytes);
                    foreach (var h in baseAssets.HieFiles)
                    {
                        descriptorHies.Add(h);
                        baseDescriptorHies.Add(h);
                        variantDescriptorMap[h] = cleanName;
                    }
                    foreach (var inst in baseAssets.HieInstances)
                    {
                        descriptorHies.Add(inst.HieName);
                        baseDescriptorHies.Add(inst.HieName);
                        variantDescriptorMap[inst.HieName] = cleanName;
                    }
                }
            }

            var discoveredVariants = TrackDiscoveryService.DiscoverRawVariants(cleanName, vfs, sourceRootPath ?? string.Empty);
            foreach (var variant in discoveredVariants)
            {
                if (variant.Equals(cleanName, StringComparison.OrdinalIgnoreCase)) continue;
                string? vTxt = TrackExportPipeline.ResolveTrackDescriptor(vfs, cleanName, variant);
                if (vTxt != null)
                {
                    string vLayerKey = variant.StartsWith(cleanName + "_", StringComparison.OrdinalIgnoreCase) || variant.StartsWith(cleanName, StringComparison.OrdinalIgnoreCase)
                        ? variant
                        : $"{cleanName}_{variant}";
                    byte[]? vBytes = vfs.LoadFileContext(vTxt, vLayerKey) ?? vfs.LoadFile(vTxt);
                    if (vBytes != null && vBytes.Length > 0)
                    {
                        var vAssets = LevelDescriptorParser.ParseLevelDescriptorAssets(vfs, vLayerKey, vBytes);
                        foreach (var h in vAssets.HieFiles)
                        {
                            descriptorHies.Add(h);
                            if (!variantDescriptorMap.ContainsKey(h) && !baseDescriptorHies.Contains(h))
                                variantDescriptorMap[h] = vLayerKey;
                        }
                        foreach (var inst in vAssets.HieInstances)
                        {
                            descriptorHies.Add(inst.HieName);
                            if (!variantDescriptorMap.ContainsKey(inst.HieName) && !baseDescriptorHies.Contains(inst.HieName))
                                variantDescriptorMap[inst.HieName] = vLayerKey;
                        }
                    }
                }
            }

            var matchingFiles = vfs.GetFiles()
                .Where(f => f.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                .Where(f => {
                    string normName = f.Name.Replace('\\', '/').ToLowerInvariant();
                    string normArchive = (f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();
                    string fileName = Path.GetFileName(f.Name);
                    string fileNameWithoutExt = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();

                    bool isDescriptorReferenced = descriptorHies.Contains(fileName) || descriptorHies.Contains(normName);

                    bool inTrackFolder = normName.StartsWith(trackFolderPrefix + "/") ||
                                         normName.StartsWith(trackFolderPrefix + "_") ||
                                         normArchive.Contains(trackFolderPrefix + "/") ||
                                         normArchive.Contains(trackFolderPrefix + "_");

                    bool startsWithName = fileNameWithoutExt.StartsWith(tPrefix, StringComparison.OrdinalIgnoreCase);

                    return isDescriptorReferenced || inTrackFolder || startsWithName;
                })
                .ToList();

            if (matchingFiles.Count == 0)
            {
                matchingFiles = vfs.GetFiles()
                    .Where(f => f.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // 1st tier: Layer Root nodes (e.g. Hollowood, Hollowood_Race1, Hollowood_Mission1)
            var layerRootNodes = new Dictionary<string, HieNodeViewModel>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in matchingFiles)
            {
                string path = file.Name.Replace('\\', '/');
                string fileName = Path.GetFileName(path);
                string fLower = fileName.ToLowerInvariant();
                string pathLower = path.ToLowerInvariant();
                string archiveLower = (file.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();

                bool isBaseReferenced = baseDescriptorHies.Contains(fileName) || baseDescriptorHies.Contains(path);

                // 1. Determine physical layer root (Hollowood, Hollowood_Race1, Hollowood_Mission1, Hollowood_Mission3, etc.)
                string layerRootKey = cleanName;
                if (variantDescriptorMap.TryGetValue(fileName, out var mappedLayer) || variantDescriptorMap.TryGetValue(path, out mappedLayer))
                {
                    layerRootKey = mappedLayer;
                }
                else if (!isBaseReferenced)
                {
                    var layerMatch = Regex.Match($"{fLower} {pathLower} {archiveLower}", @"(race\d+|mission\d+|multiplayer)");
                    if (layerMatch.Success)
                    {
                        string matchedVariant = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(layerMatch.Value);
                        layerRootKey = $"{cleanName}_{matchedVariant}";
                    }
                }

                string cleanTrackNorm = cleanName.ToLowerInvariant();
                string layerRootNorm = layerRootKey.ToLowerInvariant();

                bool isInternalToTrack = pathLower.Contains($"tracks/{cleanTrackNorm}/") ||
                                         pathLower.Contains($"tracks/{cleanTrackNorm}_") ||
                                         archiveLower.Contains($"tracks/{cleanTrackNorm}") ||
                                         archiveLower.Contains($"{cleanTrackNorm}.pak");

                bool isInternalToVariant = pathLower.Contains($"tracks/{layerRootNorm}/") ||
                                           pathLower.Contains($"tracks/{layerRootNorm}_") ||
                                           archiveLower.Contains($"tracks/{layerRootNorm}") ||
                                           archiveLower.Contains($"{layerRootNorm}.pak");

                AssetOrigin fileOrigin;
                if (!isInternalToTrack && !isInternalToVariant)
                {
                    fileOrigin = AssetOrigin.ExternalShared;
                }
                else if (isBaseReferenced || layerRootKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                {
                    fileOrigin = AssetOrigin.LocalToTrack;
                }
                else
                {
                    fileOrigin = AssetOrigin.LocalToVariant;
                }

                if (!layerRootNodes.TryGetValue(layerRootKey, out var layerRootNode))
                {
                    string displayLayerName = FormatLayerDisplayName(layerRootKey, cleanName);

                    layerRootNode = new HieNodeViewModel
                    {
                        Name = displayLayerName,
                        VirtualPath = layerRootKey,
                        IsDirectory = true,
                        IsSelected = true,
                        IsBaseTrackAsset = isBaseReferenced || layerRootKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase),
                        Origin = layerRootKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase) ? AssetOrigin.LocalToTrack : AssetOrigin.LocalToVariant,
                        ShowTopSeparator = false,
                        NodeType = "TrackLayerRoot",
                        OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                    };
                    layerRootNodes[layerRootKey] = layerRootNode;
                }

                // 2. Determine semantic subfolder inside this layer
                string displaySubfolder = DetermineSemanticSubfolder(fileName, path, cleanName, isBaseReferenced);
                if (fileOrigin == AssetOrigin.ExternalShared && (displaySubfolder == "Base Terrain & Buildings" || displaySubfolder == "Track Geometry"))
                {
                    displaySubfolder = "External Shared Links";
                }

                HieNodeViewModel parentFolderNode = layerRootNode;

                if (!string.IsNullOrWhiteSpace(displaySubfolder))
                {
                    string folderKey = $"{layerRootKey}/{displaySubfolder}";
                    var existingSub = layerRootNode.Children.FirstOrDefault(c => c.VirtualPath.Equals(folderKey, StringComparison.OrdinalIgnoreCase));
                    if (existingSub == null)
                    {
                        existingSub = new HieNodeViewModel
                        {
                            Name = displaySubfolder,
                            VirtualPath = folderKey,
                            IsDirectory = true,
                            IsSelected = true,
                            IsBaseTrackAsset = isBaseReferenced || layerRootKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase),
                            Origin = fileOrigin,
                            NodeType = "VfsSubfolder",
                            Parent = layerRootNode,
                            OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                        };
                        layerRootNode.Children.Add(existingSub);
                    }
                    parentFolderNode = existingSub;
                }

                var fileNode = new HieNodeViewModel
                {
                    Name = fileName,
                    VirtualPath = file.Name,
                    IsDirectory = false,
                    IsSelected = true,
                    IsBaseTrackAsset = isBaseReferenced || layerRootKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase),
                    Origin = fileOrigin,
                    NodeType = "MeshFile",
                    Parent = parentFolderNode,
                    OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                };
                parentFolderNode.Children.Add(fileNode);
            }

            // Embed Base Geometry into every variant layer so each variant is a complete, self-contained scene
            if (layerRootNodes.TryGetValue(cleanName, out var baseLayerNode))
            {
                foreach (var (layerKey, vNode) in layerRootNodes)
                {
                    if (vNode == baseLayerNode) continue;

                    int insertIdx = 0;
                    foreach (var baseSub in baseLayerNode.Children)
                    {
                        var clonedSub = new HieNodeViewModel
                        {
                            Name = baseSub.Name,
                            VirtualPath = $"{vNode.VirtualPath}/{baseSub.Name}",
                            IsDirectory = true,
                            IsSelected = true,
                            IsBaseTrackAsset = true,
                            Origin = AssetOrigin.InheritedFromBase,
                            NodeType = baseSub.NodeType,
                            Parent = vNode,
                            OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                        };
                        foreach (var baseFile in baseSub.Children)
                        {
                            var clonedFile = new HieNodeViewModel
                            {
                                Name = baseFile.Name,
                                VirtualPath = baseFile.VirtualPath,
                                IsDirectory = false,
                                IsSelected = true,
                                IsBaseTrackAsset = true,
                                Origin = AssetOrigin.InheritedFromBase,
                                NodeType = "MeshFile",
                                Parent = clonedSub,
                                OnSelectionChangedCallback = () => modalVm.NotifyUserTreeToggled()
                            };
                            clonedSub.Children.Add(clonedFile);
                        }
                        vNode.Children.Insert(insertIdx++, clonedSub);
                    }
                }
            }

            // Natural hierarchical sorting: Base Track first, then Races (1..N), Missions (1..N), Multiplayer, Others
            var sortedLayers = layerRootNodes.Values.OrderBy(node =>
            {
                string key = node.VirtualPath;
                if (key.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                    return (0, 0, key);

                var raceMatch = Regex.Match(key, @"race(\d+)", RegexOptions.IgnoreCase);
                if (raceMatch.Success && int.TryParse(raceMatch.Groups[1].Value, out int rNum))
                    return (1, rNum, key);

                var missionMatch = Regex.Match(key, @"mission(\d+)", RegexOptions.IgnoreCase);
                if (missionMatch.Success && int.TryParse(missionMatch.Groups[1].Value, out int mNum))
                    return (2, mNum, key);

                if (key.Contains("multiplayer", StringComparison.OrdinalIgnoreCase))
                    return (3, 0, key);

                return (4, 0, key);
            }).ToList();

            for (int i = 0; i < sortedLayers.Count; i++)
            {
                sortedLayers[i].ShowTopSeparator = i > 0;
                modalVm.HieTreeNodes.Add(sortedLayers[i]);
            }
        }

        public static string FormatLayerDisplayName(string layerKey, string cleanName)
        {
            if (layerKey.Equals(cleanName, StringComparison.OrdinalIgnoreCase))
                return $"{cleanName} (Base Track)";

            if (layerKey.StartsWith(cleanName + "_", StringComparison.OrdinalIgnoreCase))
            {
                string suffix = layerKey.Substring(cleanName.Length + 1);
                return $"{cleanName} ({suffix})";
            }

            return layerKey.Replace('_', ' ');
        }

        public static string DetermineSemanticSubfolder(string fileName, string fullPath, string cleanName, bool isBase)
        {
            string fLower = fileName.ToLowerInvariant();

            if (fLower.Contains("sky") || fLower.Contains("cloud") || fLower.Contains("sun") || fLower.Contains("weather"))
                return "Skybox & Atmosphere";

            if (fLower.Contains("water") || fLower.Contains("sea") || fLower.Contains("ocean") || fLower.Contains("river"))
                return "Water & Liquids";

            if (fLower.Contains("checkpoint") || fLower.Contains("start") || fLower.Contains("grid") ||
                fLower.Contains("gate") || fLower.Contains("arrow") || fLower.Contains("sign") || fLower.Contains("beacon"))
                return "Race Layout & Checkpoints";

            if (fLower.Contains("ped") || fLower.Contains("drone") || fLower.Contains("traffic") || fLower.Contains("follower"))
                return "Characters & Drones";

            if (fLower.Contains("lift") || fLower.Contains("bridge") || fLower.Contains("door") ||
                fLower.Contains("ding") || fLower.Contains("break") || fLower.Contains("prop") || fLower.Contains("steam"))
                return "Dynamic Props & Objects";

            // Check if there is a known game folder name like "Level Convsoft" or "Level Props"
            string normPath = fullPath.Replace('\\', '/');
            int tIdx = normPath.IndexOf("tracks/", StringComparison.OrdinalIgnoreCase);
            if (tIdx >= 0)
            {
                string sub = normPath.Substring(tIdx + "tracks/".Length);
                string[] parts = sub.Split('/', StringSplitOptions.RemoveEmptyEntries);
                for (int p = 0; p < parts.Length - 1; p++)
                {
                    string part = parts[p].Replace(".pak", "", StringComparison.OrdinalIgnoreCase).Replace(".dir", "", StringComparison.OrdinalIgnoreCase).Trim();
                    if (!part.Equals(cleanName, StringComparison.OrdinalIgnoreCase) &&
                        !part.StartsWith(cleanName + "_", StringComparison.OrdinalIgnoreCase) &&
                        !part.Contains(':') &&
                        part.Length > 2)
                    {
                        return part;
                    }
                }
            }

            return isBase ? "Base Terrain & Buildings" : "Track Geometry";
        }
    }
}
