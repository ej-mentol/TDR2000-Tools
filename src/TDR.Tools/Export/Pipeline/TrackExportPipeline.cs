using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Export
{
    public record TrackExportOptions(
        bool ExportObj = true,
        bool ExportGltf = false,
        bool ExportPngTextures = true,
        bool IncludeMovableProps = true,
        bool ExportSceneJson = false,
        bool UseZeroOriginForJsonAssets = true,
        bool NoMaterials = false,
        bool UseLocalCoords = false,
        bool UseGrouping = true,
        bool DumpAll = false,
        bool Verbose = false,
        List<string>? SelectedHieFiles = null,
        bool ExportArmatures = false
    );

    public static class TrackExportPipeline
    {
        public static string? ResolveTrackDescriptor(PakManager vfs, string trackName, string? variantArg)
        {
            if (trackName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) && vfs.FileExists(trackName))
                return trackName;

            string tName = trackName.ToLowerInvariant();

            if (!string.IsNullOrWhiteSpace(variantArg))
            {
                string vLower = variantArg.ToLowerInvariant().Trim();

                var vfsMatch = vfs.GetFiles()
                    .FirstOrDefault(f => {
                        if (!f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
                        string fn = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                        return fn.Equals($"{tName}_{vLower}", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals($"{tName}{vLower}", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals($"{tName}_race{vLower}", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals($"{tName}_mission{vLower}", StringComparison.OrdinalIgnoreCase);
                    });

                if (vfsMatch != null) return vfsMatch.Name;
                return null;
            }

            string targetPattern = $"tracks/{tName}/{tName}.txt";
            var baseMatch = vfs.GetFiles()
                .FirstOrDefault(f => f.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) &&
                                     (f.Name.Replace('\\', '/').ToLowerInvariant().EndsWith(targetPattern) ||
                                      Path.GetFileName(f.Name).Equals($"{tName}.txt", StringComparison.OrdinalIgnoreCase)));

            if (baseMatch != null) return baseMatch.Name;

            string direct = $"{trackName}.txt";
            if (vfs.FileExists(direct)) return direct;

            return null;
        }

        public static bool ExportTrack(PakManager vfs, string trackName, string? variantSuffix, string outputDir, TrackExportOptions options, Action<string>? log = null, Action<int, string>? progressCallback = null)
        {
            log ??= msg => Services.LogService.Instance.Info(msg);
            if (string.IsNullOrWhiteSpace(trackName)) return false;

            // Fallback: If no format option selected, default to ExportObj = true
            if (!options.ExportObj && !options.ExportGltf && !options.ExportSceneJson && !options.DumpAll)
            {
                log("[!] No format selected. Defaulting to ExportObj = true.");
                options = options with { ExportObj = true };
            }

            string cleanName = TrackDiscovery.GetBaseTrackName(trackName);
            Services.LogService.Instance.CurrentTrackContext = cleanName;
            Services.LogService.Instance.CurrentVariantContext = variantSuffix ?? "Base";

            DateTime exportStartTime = DateTime.Now;
            log($"[+] Starting Track Export Pipeline for '{trackName}' (Variant: {variantSuffix ?? "Base"}) → '{outputDir}'");
            Directory.CreateDirectory(outputDir);

            TrackExportResult? exportResult = null;

            // 1. OBJ Export (Standard Descriptor Pipeline with Keyword Blacklisting/Filtering)
            if (options.ExportObj)
            {
                string variantTrackName = !string.IsNullOrWhiteSpace(variantSuffix) ? $"{cleanName}_{variantSuffix}" : cleanName;
                string? activeTxtPath = ResolveTrackDescriptor(vfs, cleanName, variantSuffix);
                byte[]? descriptorData = activeTxtPath != null ? (vfs.LoadFileContext(activeTxtPath, variantTrackName) ?? vfs.LoadFile(activeTxtPath)) : null;

                if (descriptorData != null && descriptorData.Length > 0)
                {
                    var objExporter = new ObjExporter(
                        vfs,
                        outputDir,
                        options.NoMaterials,
                        options.UseLocalCoords,
                        options.Verbose,
                        options.UseGrouping,
                        options.IncludeMovableProps,
                        variantTrackName,
                        log,
                        options.SelectedHieFiles,
                        options.ExportPngTextures
                    );

                    string outputObjPath = Path.Combine(outputDir, variantTrackName + ".obj");
                    log?.Invoke($"[+] Parsing level descriptor '{activeTxtPath}' & converting HIE geometry...");
                    exportResult = objExporter.ExportLevelToObj(descriptorData, variantTrackName, outputObjPath, progressCallback);
                    log?.Invoke($"[+] OBJ export finished for '{variantTrackName}'.");
                }
                else
                {
                    log?.Invoke($"[!] Warning: Descriptor for '{cleanName}' (Variant: {variantSuffix ?? "Base"}) not found in VFS context.");
                }
            }

            // 1c. Modern glTF 2.0 Scene Export (.GLTF / .GLB)
            if (options.ExportGltf)
            {
                string variantTrackName = !string.IsNullOrWhiteSpace(variantSuffix) ? $"{cleanName}_{variantSuffix}" : cleanName;
                string? activeTxtPath = ResolveTrackDescriptor(vfs, cleanName, variantSuffix);
                byte[]? descriptorData = activeTxtPath != null ? (vfs.LoadFileContext(activeTxtPath, variantTrackName) ?? vfs.LoadFile(activeTxtPath)) : null;

                if (descriptorData != null && descriptorData.Length > 0)
                {
                    var gltfExporter = new GltfExporter(vfs, outputDir, options.UseLocalCoords, options.Verbose, variantTrackName, log, options.ExportPngTextures, options.SelectedHieFiles, options.ExportArmatures);
                    string outputGltfPath = Path.Combine(outputDir, variantTrackName + ".gltf");
                    log?.Invoke($"[►] Exporting Modern glTF 2.0 Scene: '{variantTrackName}.gltf'");
                    bool gltfOk = gltfExporter.ExportLevelToGltf(descriptorData, variantTrackName, outputGltfPath, options.IncludeMovableProps, progressCallback);
                    if (!gltfOk)
                    {
                        Services.LogService.Instance.Error($"glTF export failed for '{variantTrackName}'");
                    }
                }
            }

            // 1b. DumpAll Brute-force Mode (-da): export all matching .hie files for the track without blacklisting
            if (options.DumpAll)
            {
                string variantTrackName = !string.IsNullOrWhiteSpace(variantSuffix) ? $"{cleanName}_{variantSuffix}" : cleanName;
                log?.Invoke($"[+] [DUMP-ALL MODE (-da)] Brute-Force Track Mesh Export for '{variantTrackName}'...");
                string tPrefix = cleanName.ToLowerInvariant();
                string trackFolderPrefix = $"tracks/{tPrefix}";

                var matchingHieFiles = vfs.GetFiles()
                    .Where(f =>
                    {
                        if (!f.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                            return false;

                        string normName = f.Name.Replace('\\', '/').ToLowerInvariant();
                        string normArchive = (f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();
                        string fileName = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();

                        bool inTrackFolder = normName.StartsWith(trackFolderPrefix + "/") ||
                                             normName.StartsWith(trackFolderPrefix + "_") ||
                                             normArchive.Contains(trackFolderPrefix + "/") ||
                                             normArchive.Contains(trackFolderPrefix + "_");

                        bool startsWithName = fileName.StartsWith(tPrefix, StringComparison.OrdinalIgnoreCase);

                        if (!inTrackFolder && !startsWithName) return false;

                        // If a specific variant is selected, exclude other variant files
                        if (!string.IsNullOrWhiteSpace(variantSuffix))
                        {
                            string vSuffixLower = variantSuffix.ToLowerInvariant();
                            bool isBaseFile = !fileName.Contains("race") && !fileName.Contains("mission");
                            bool isThisVariantFile = fileName.Contains(vSuffixLower) || normName.Contains(vSuffixLower);
                            return isBaseFile || isThisVariantFile;
                        }

                        return true;
                    })
                    .Select(f => f.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (options.SelectedHieFiles != null && options.SelectedHieFiles.Count > 0)
                {
                    var allowedSet = new HashSet<string>(options.SelectedHieFiles, StringComparer.OrdinalIgnoreCase);
                    matchingHieFiles = matchingHieFiles.Where(f => allowedSet.Contains(f.Replace('\\', '/')) || allowedSet.Contains(f)).ToList();
                }

                var objExporter = new ObjExporter(
                    vfs,
                    outputDir,
                    options.NoMaterials,
                    options.UseLocalCoords,
                    options.Verbose,
                    options.UseGrouping,
                    options.IncludeMovableProps,
                    variantTrackName,
                    log,
                    options.SelectedHieFiles,
                    options.ExportPngTextures
                );

                int dumpedCount = 0;
                foreach (var hieFile in matchingHieFiles)
                {
                    byte[]? hieBytes = vfs.LoadFile(hieFile);
                    if (hieBytes != null && hieBytes.Length > 0)
                    {
                        string subName = Path.GetFileNameWithoutExtension(hieFile);
                        string outPath = Path.Combine(outputDir, $"{subName}.obj");
                        objExporter.ExportHieToObj(hieBytes, hieFile, outPath, vfs.GetArchivePath(hieFile, variantTrackName));
                        dumpedCount++;
                    }
                }
                log?.Invoke($"[+] DumpAll completed: {dumpedCount} .hie files dumped to '{outputDir}'.");
            }

            // 2. Scene JSON Export
            if (options.ExportSceneJson)
            {
                string jsonTrackContext = !string.IsNullOrWhiteSpace(variantSuffix) ? $"{cleanName}_{variantSuffix}" : cleanName;
                log?.Invoke($"[+] Generating 'scene.json' manifest for '{trackName}'...");
                SceneJsonExporter.GenerateManifest(
                    trackName,
                    variantSuffix,
                    vfs,
                    outputDir,
                    (path) => vfs.LoadFileContext(path, jsonTrackContext),
                    exportResult,
                    options.UseZeroOriginForJsonAssets,
                    options.Verbose,
                    log
                );
                log?.Invoke($"[+] 'scene.json' manifest successfully generated.");
            }

            int errCount = Services.LogService.Instance.GetErrorCount(exportStartTime);
            return errCount == 0;
        }
    }
}
