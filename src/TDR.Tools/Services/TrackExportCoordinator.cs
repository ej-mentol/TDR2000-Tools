using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TDR.PakLib;
using TDR.Tools.Export;
using TDR.Tools.ViewModels;

namespace TDR.Tools.Services
{
    /// <summary>
    /// Service responsible for batch scene export orchestration across all layers/variants of a track,
    /// progress scaling, and batch error/warning summaries.
    /// </summary>
    public static class TrackExportCoordinator
    {
        public static string? GetVariantSuffix(string rawVariant, string trackName)
        {
            if (string.IsNullOrWhiteSpace(rawVariant)) return null;
            string tName = trackName.ToLowerInvariant();
            string selLower = rawVariant.ToLowerInvariant().Trim();
            if (selLower == tName ||
                selLower.Equals(ConvertTrackModalViewModel.PresetAllSupported, StringComparison.OrdinalIgnoreCase) ||
                selLower.Equals(ConvertTrackModalViewModel.PresetCustom, StringComparison.OrdinalIgnoreCase) ||
                selLower.StartsWith("all ", StringComparison.OrdinalIgnoreCase) ||
                selLower.StartsWith("base track", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            if (selLower.StartsWith(tName + "_", StringComparison.Ordinal))
                return rawVariant.Substring(tName.Length + 1);
            if (selLower.StartsWith(tName, StringComparison.Ordinal))
                return rawVariant.Substring(tName.Length).TrimStart('_');
            return rawVariant;
        }

        public static async Task<bool> ExportTrackBatchAsync(
            PakManager vfs,
            ConvertTrackModalViewModel vm,
            Action<string>? log = null,
            Action<int, string>? progressCallback = null,
            Action<int, string>? subProgressCallback = null)
        {
            if (vfs == null || vm == null || string.IsNullOrWhiteSpace(vm.TrackName))
                return false;

            log ??= msg => LogService.Instance.Info(msg);

            var baseOptions = new TrackExportOptions(
                ExportObj: vm.ExportObj,
                ExportGltf: vm.ExportGltf,
                ExportArmatures: vm.ExportArmatures,
                ExportPngTextures: vm.ExportPngTextures,
                IncludeMovableProps: vm.IncludeMovableProps,
                ExportSceneJson: vm.ExportSceneJson,
                UseZeroOriginForJsonAssets: vm.UseZeroOriginForJsonAssets,
                NoMaterials: false,
                UseLocalCoords: vm.UseLocalCoords,
                UseGrouping: vm.UseGrouping,
                DumpAll: false,
                Verbose: vm.VerboseLog,
                SelectedHieFiles: null
            );

            progressCallback?.Invoke(5, $"Starting export for '{vm.TrackName}'...");

            return await Task.Run(() =>
            {
                DateTime batchStartTime = DateTime.Now;
                var activeLayers = vm.HieTreeNodes
                    .Where(n => n.IsSelected != false && (n.IsSelected == true || n.Children.Any(c => c.IsSelected != false)))
                    .ToList();

                int successCount = 0;
                int total = activeLayers.Count > 0 ? activeLayers.Count : 1;
                var sceneResults = new List<(string Name, bool Ok, int Errors, int Warnings)>();

                if (activeLayers.Count > 0)
                {
                    var baseNode = vm.HieTreeNodes.FirstOrDefault(n => n.VirtualPath.Equals(vm.TrackName, StringComparison.OrdinalIgnoreCase));
                    var baseHies = new List<string>();
                    if (baseNode != null)
                    {
                        void CollectAllBaseMeshHies(IEnumerable<HieNodeViewModel> nodes)
                        {
                            foreach (var node in nodes)
                            {
                                if (!node.IsDirectory && !string.IsNullOrEmpty(node.VirtualPath) && node.IsSelected == true)
                                {
                                    string fn = Path.GetFileName(node.VirtualPath).ToLowerInvariant();
                                    if (!fn.Contains("campaths") && !fn.Contains("intpaths") && !fn.Contains("zoomin") && !fn.Contains("look"))
                                    {
                                        baseHies.Add(node.VirtualPath);
                                    }
                                }
                                if (node.Children.Count > 0) CollectAllBaseMeshHies(node.Children);
                            }
                        }
                        CollectAllBaseMeshHies(baseNode.Children);
                    }

                    for (int i = 0; i < total; i++)
                    {
                        var layerNode = activeLayers[i];
                        string? suffix = GetVariantSuffix(layerNode.VirtualPath, vm.TrackName);
                        string displayLayer = !string.IsNullOrEmpty(suffix) ? $"{vm.TrackName} ({suffix})" : vm.TrackName;

                        int layerBaseProgress = (int)((i * 100f) / total);
                        progressCallback?.Invoke(layerBaseProgress, $"Exporting scene '{displayLayer}' ({i + 1}/{total})...");

                        var layerHies = new List<string>();
                        ConvertTrackModalViewModel.CollectSelectedHiePaths(layerNode.Children, layerHies);

                        // If exporting a variant layer (Race, Mission, MP), unconditionally include base track terrain, water, and environment meshes
                        if (!string.IsNullOrEmpty(suffix) && baseHies.Count > 0)
                        {
                            foreach (var bh in baseHies)
                            {
                                if (!layerHies.Contains(bh, StringComparer.OrdinalIgnoreCase))
                                {
                                    layerHies.Add(bh);
                                }
                            }
                        }

                        var layerOptions = baseOptions with { SelectedHieFiles = layerHies.Count > 0 ? layerHies : null };
                        DateTime sceneStartTime = DateTime.Now;

                        bool ok = TrackExportPipeline.ExportTrack(vfs, vm.TrackName, suffix, vm.OutputDirectory, layerOptions, log, (subPct, subMsg) =>
                        {
                            int scaledProgress = (int)(((i * 100f) + subPct) / total);
                            subProgressCallback?.Invoke(subPct, subMsg);
                            progressCallback?.Invoke(scaledProgress, $"[{i + 1}/{total}] {subMsg}");
                        });

                        int sErr = LogService.Instance.GetErrorCount(sceneStartTime);
                        int sWarn = LogService.Instance.GetWarningCount(sceneStartTime);
                        sceneResults.Add((displayLayer, ok, sErr, sWarn));

                        if (ok) successCount++;
                    }
                }
                else
                {
                    var customHies = vm.GetSelectedHiePaths();
                    var customOptions = baseOptions with { SelectedHieFiles = customHies.Count > 0 ? customHies : null };

                    progressCallback?.Invoke(15, $"Exporting custom selection for '{vm.TrackName}'...");
                    DateTime sceneStartTime = DateTime.Now;
                    bool ok = TrackExportPipeline.ExportTrack(vfs, vm.TrackName, null, vm.OutputDirectory, customOptions, log, (subPct, subMsg) =>
                    {
                        subProgressCallback?.Invoke(subPct, subMsg);
                    });

                    int sErr = LogService.Instance.GetErrorCount(sceneStartTime);
                    int sWarn = LogService.Instance.GetWarningCount(sceneStartTime);
                    sceneResults.Add(($"{vm.TrackName} (Custom)", ok, sErr, sWarn));

                    if (ok) successCount++;
                }

                int batchErrors = LogService.Instance.GetErrorCount(batchStartTime);
                int batchWarnings = LogService.Instance.GetWarningCount(batchStartTime);
                double elapsedSeconds = (DateTime.Now - batchStartTime).TotalSeconds;

                log("════════════════════════════════════════════════════════════════════════════════");
                string summaryHeader = batchErrors > 0 || successCount < total
                    ? $"[!] EXPORT SUMMARY: {successCount}/{total} Succeeded ({batchErrors} errors, {batchWarnings} warnings) [{elapsedSeconds:F1}s]"
                    : $"[+] EXPORT SUMMARY: {total}/{total} Succeeded ({batchErrors} errors, {batchWarnings} warnings) [{elapsedSeconds:F1}s]";
                log(summaryHeader);

                foreach (var (name, sOk, sErr, sWarn) in sceneResults)
                {
                    string statusText = sOk ? "OK" : "FAILED";
                    string detail = (sErr > 0 || sWarn > 0) ? $"({sErr} errors, {sWarn} warnings)" : "(Clean)";
                    log($"    • {name,-30} → {statusText,-6} {detail}");
                }
                log("════════════════════════════════════════════════════════════════════════════════");

                progressCallback?.Invoke(100, $"Completed export for '{vm.TrackName}': {successCount}/{total} scenes, {batchErrors} error(s), {batchWarnings} warning(s)");

                return batchErrors == 0 && successCount == total;
            });
        }
    }
}
