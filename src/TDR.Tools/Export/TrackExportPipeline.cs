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
        bool NoMaterials = false,
        bool UseLocalCoords = false,
        bool UseGrouping = true,
        bool DumpAll = false,
        bool Verbose = false,
        bool EnableGroundSnap = false,
        List<string>? SelectedHieFiles = null
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
                        string fn = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();
                        return fn.Equals($"{tName}_{vLower}", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals($"{tName}{vLower}", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals($"{tName}_race{vLower}", StringComparison.OrdinalIgnoreCase) ||
                               fn.Equals($"{tName}_mission{vLower}", StringComparison.OrdinalIgnoreCase);
                    });

                if (vfsMatch != null) return vfsMatch.Name;
            }

            string targetPattern = $"tracks/{tName}/{tName}.txt";
            var baseMatch = vfs.GetFiles()
                .FirstOrDefault(f => f.Name.Replace('\\', '/').ToLowerInvariant().EndsWith(targetPattern) ||
                                     Path.GetFileName(f.Name).Equals($"{tName}.txt", StringComparison.OrdinalIgnoreCase));

            if (baseMatch != null) return baseMatch.Name;

            string direct = $"{trackName}.txt";
            if (vfs.FileExists(direct)) return direct;

            return null;
        }

        public static bool ExportTrack(PakManager vfs, string trackName, string? variantSuffix, string outputDir, TrackExportOptions options, Action<string>? log = null, Action<int, string>? progressCallback = null)
        {
            if (string.IsNullOrWhiteSpace(trackName)) return false;

            // Fallback: If no format option selected, default to ExportObj = true
            if (!options.ExportObj && !options.ExportGltf && !options.ExportSceneJson && !options.DumpAll)
            {
                log?.Invoke("[!] No format selected. Defaulting to ExportObj = true.");
                options = options with { ExportObj = true };
            }

            log?.Invoke($"[+] Starting Track Export Pipeline for '{trackName}' (Variant: {variantSuffix ?? "Base"}) → '{outputDir}'");
            Directory.CreateDirectory(outputDir);

            string cleanName = TrackDiscovery.GetBaseTrackName(trackName);
            TrackExportResult? exportResult = null;

            // 1. OBJ Export (Standard Descriptor Pipeline with Keyword Blacklisting/Filtering)
            if (options.ExportObj)
            {
                string? activeTxtPath = ResolveTrackDescriptor(vfs, cleanName, variantSuffix);
                byte[]? descriptorData = activeTxtPath != null ? vfs.LoadFile(activeTxtPath) : null;
                string variantTrackName = !string.IsNullOrWhiteSpace(variantSuffix) ? $"{cleanName}_{variantSuffix}" : cleanName;

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
                        cleanName,
                        log,
                        options.EnableGroundSnap,
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
                string? activeTxtPath = ResolveTrackDescriptor(vfs, cleanName, variantSuffix);
                byte[]? descriptorData = activeTxtPath != null ? vfs.LoadFile(activeTxtPath) : null;
                string variantTrackName = !string.IsNullOrWhiteSpace(variantSuffix) ? $"{cleanName}_{variantSuffix}" : cleanName;

                if (descriptorData != null && descriptorData.Length > 0)
                {
                    var gltfExporter = new GltfExporter(vfs, outputDir, options.UseLocalCoords, options.Verbose, cleanName, log, options.ExportPngTextures);
                    string outputGltfPath = Path.Combine(outputDir, variantTrackName + ".gltf");
                    log?.Invoke($"[►] Exporting Modern glTF 2.0 Scene: '{variantTrackName}.gltf'");
                    gltfExporter.ExportLevelToGltf(descriptorData, variantTrackName, outputGltfPath, options.IncludeMovableProps, progressCallback);
                }
            }

            // 1b. DumpAll Brute-force Mode (-da): export all matching .hie files for the track without blacklisting
            if (options.DumpAll)
            {
                log?.Invoke($"[+] [DUMP-ALL MODE (-da)] Brute-Force Track Mesh Export for '{cleanName}'...");
                string tPrefix = cleanName.ToLowerInvariant();
                string trackFolderPrefix = $"tracks/{tPrefix}";

                var matchingHieFiles = vfs.GetFiles()
                    .Where(f => f.Name.EndsWith(".hie", StringComparison.OrdinalIgnoreCase))
                    .Where(f => {
                        string normName = f.Name.Replace('\\', '/').ToLowerInvariant();
                        string normArchive = (f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant();
                        string fileName = Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant();

                        bool inTrackFolder = normName.StartsWith(trackFolderPrefix + "/") ||
                                             normName.StartsWith(trackFolderPrefix + "_") ||
                                             normArchive.Contains(trackFolderPrefix + "/") ||
                                             normArchive.Contains(trackFolderPrefix + "_");

                        bool startsWithName = fileName.StartsWith(tPrefix, StringComparison.OrdinalIgnoreCase);

                        return inTrackFolder || startsWithName;
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
                    cleanName,
                    log,
                    options.EnableGroundSnap
                );

                int dumpedCount = 0;
                foreach (var hieFile in matchingHieFiles)
                {
                    byte[]? hieBytes = vfs.LoadFile(hieFile);
                    if (hieBytes != null && hieBytes.Length > 0)
                    {
                        string subName = Path.GetFileNameWithoutExtension(hieFile);
                        string outPath = Path.Combine(outputDir, $"{subName}.obj");
                        objExporter.ExportHieToObj(hieBytes, hieFile, outPath, vfs.GetArchivePath(hieFile));
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
                    options.Verbose,
                    log
                );
                log?.Invoke($"[+] 'scene.json' manifest successfully generated.");
            }

            // Clean up intermediate .tx descriptor files in export folder
            try
            {
                foreach (string txPath in Directory.GetFiles(outputDir, "*.tx", SearchOption.AllDirectories))
                {
                    try { File.Delete(txPath); } catch { }
                }
            }
            catch { }

            log?.Invoke($"[+] Track Export Pipeline completed for '{trackName}'.\n");
            return true;
        }
    }
}
