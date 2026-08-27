using Avalonia;
using System;
using System.IO;
using System.Linq;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Export;
using TDR.Tools.Services;

namespace TDR.Tools
{
    internal class Program
    {
        private static readonly PakManager VFS = new();
        private static string ExportDir = "EXPORT";
        private static bool NoMaterials = false;
        private static bool UseLocalCoords = false;
        private static bool Verbose = false;
        private static bool UseGrouping = true;
        private static bool ExportJson = false;
        private static bool ExportGltf = false;
        private static bool ExportArmatures = false;
        private static bool DumpAll = false;
        private static bool IncludeMovableProps = true;

        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                LogService.Instance.FatalCrash(e.ExceptionObject);
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                LogService.Instance.Error($"[TaskScheduler] Unobserved Task Exception: {e.Exception}");
                e.SetObserved();
            };

            // If no arguments or explicitly --gui, launch Avalonia GUI
            if (args.Length == 0 || args.Contains("--gui", StringComparer.OrdinalIgnoreCase))
            {
                // Single instance guard for GUI mode
                using var mutex = new System.Threading.Mutex(true, @"Local\TDR2000_Tools_GUI_Instance", out bool createdNew);
                if (!createdNew)
                {
                    Console.WriteLine("[!] TDR Tools GUI instance is already running in background.");
                    return;
                }

                BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
                return;
            }

            // CLI Mode Execution
            Console.WriteLine("==============================================");
            Console.WriteLine(" TDR2000 Tools — Track Converter & VFS CLI");
            Console.WriteLine("==============================================\n");

            if (args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                PrintHelp();
                return;
            }

            // -l / --level is the canonical flag. -t / --track kept as legacy alias.
            string? levelArg = GetArgumentValue(args, "-l", "--level") ?? GetArgumentValue(args, "-t", "--track");
            if (!string.IsNullOrEmpty(levelArg))
            {
                // Legacy compat: "trackname:variant" colon-syntax still accepted but --variant is preferred
                string cleanLevelArg = levelArg.Contains(':') ? levelArg.Split(':', 2)[0] : levelArg;
                string? legacyVariant = levelArg.Contains(':') ? levelArg.Split(':', 2)[1] : null;

                string assetsRoot = Services.TrackDiscoveryService.ResolveAssetsRootPath(cleanLevelArg);

                if (!TrackDiscovery.ValidateAssetsPath(assetsRoot, out string statusMessage))
                {
                    Console.WriteLine(statusMessage);
                    return;
                }

                Console.WriteLine(statusMessage);
                TrackDiscoveryService.IndexWithSharedFolders(VFS, assetsRoot);

                string? exportArg = GetArgumentValue(args, "-o", "--output");
                if (!string.IsNullOrEmpty(exportArg)) ExportDir = exportArg;

                NoMaterials         = args.Contains("--nomat",       StringComparer.OrdinalIgnoreCase);
                UseLocalCoords      = args.Contains("--local",       StringComparer.OrdinalIgnoreCase);
                Verbose             = args.Contains("--verbose",     StringComparer.OrdinalIgnoreCase)
                                   || args.Contains("-v",            StringComparer.OrdinalIgnoreCase);
                UseGrouping         = !args.Contains("--no-group",   StringComparer.OrdinalIgnoreCase);
                ExportJson          = args.Contains("--json",        StringComparer.OrdinalIgnoreCase);
                ExportGltf          = args.Contains("--gltf",        StringComparer.OrdinalIgnoreCase);
                
                // Track pedestrian armatures disabled pending full kinematic integration
                if (args.Contains("--rigged-peds", StringComparer.OrdinalIgnoreCase) || args.Contains("--export-armatures", StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[!] Notice: --rigged-peds / --export-armatures is temporarily disabled for map conversion. Pedestrians will export as lightweight static instances.");
                }
                ExportArmatures     = false;
                
                DumpAll             = args.Contains("--dump-all",    StringComparer.OrdinalIgnoreCase);
                IncludeMovableProps = !args.Contains("--no-props",   StringComparer.OrdinalIgnoreCase);

                string baseTrackName = TrackDiscovery.GetBaseTrackName(Path.GetFileNameWithoutExtension(cleanLevelArg));

                // Verify short track name against discovered tracks in VFS
                var discoveredTracks = TrackDiscoveryService.DiscoverTracks(VFS, assetsRoot);
                var matchedTrack = discoveredTracks.FirstOrDefault(t => t.Name.Equals(baseTrackName, StringComparison.OrdinalIgnoreCase));

                if (matchedTrack == null && discoveredTracks.Count > 0)
                {
                    Console.WriteLine($"[!] Track '{baseTrackName}' not found in current VFS context.");
                    Console.WriteLine($"[+] Available: {string.Join(", ", discoveredTracks.Select(t => t.Name))}");
                    return;
                }

                Console.WriteLine($"[+] Track '{baseTrackName}' verified in VFS.");

                // Variant resolution priority: --variant > legacy :suffix > --all-variants > base only
                bool allVariants = args.Contains("--all-variants", StringComparer.OrdinalIgnoreCase);
                string? explicitVariant = GetArgumentValue(args, "--variant", "--variant") ?? legacyVariant;

                if (allVariants)
                {
                    // Export base
                    ExportTrackCli(baseTrackName, variantSuffix: null);
                    // Export each discovered variant
                    if (matchedTrack != null)
                    {
                        foreach (var variantFolder in matchedTrack.VariantFolders)
                        {
                            string? suffix = GetVariantSuffix(variantFolder, baseTrackName);
                            if (!string.IsNullOrEmpty(suffix))
                                ExportTrackCli(baseTrackName, suffix);
                        }
                    }
                }
                else
                {
                    ExportTrackCli(baseTrackName, explicitVariant);
                }
                return;
            }

            Console.WriteLine("[!] No level PAK or track specified via '-l'. Launching Avalonia GUI mode...");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();

        private static void ExportTrackCli(string trackName, string? variantSuffix)
        {
            string outDir = Path.Combine(ExportDir, trackName);

            var options = new TrackExportOptions(
                ExportObj:           true,
                ExportGltf:          ExportGltf,
                ExportArmatures:     ExportArmatures,
                IncludeMovableProps: IncludeMovableProps,
                ExportSceneJson:     ExportJson,
                NoMaterials:         NoMaterials,
                UseLocalCoords:      UseLocalCoords,
                UseGrouping:         UseGrouping,
                DumpAll:             DumpAll,
                Verbose:             Verbose
            );

            TrackExportPipeline.ExportTrack(VFS, trackName, variantSuffix, outDir, options, Console.WriteLine);
        }

        /// <summary>
        /// Extracts the suffix portion from a variant folder name relative to the base track name.
        /// e.g. GetVariantSuffix("hollowood_race1", "hollowood") → "race1"
        /// Mirrors GetVariantSuffix in MainViewModel for CLI/GUI parity.
        /// </summary>
        private static string? GetVariantSuffix(string variantFolder, string baseTrackName)
        {
            string tLow = baseTrackName.ToLowerInvariant();
            string vLow = variantFolder.ToLowerInvariant();
            if (vLow == tLow) return null;
            if (vLow.StartsWith(tLow + "_")) return variantFolder.Substring(tLow.Length + 1);
            if (vLow.StartsWith(tLow))      return variantFolder.Substring(tLow.Length).TrimStart('_');
            return variantFolder;
        }

        private static string? GetArgumentValue(string[] args, string shortFlag, string longFlag)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(shortFlag, StringComparison.OrdinalIgnoreCase) ||
                    args[i].Equals(longFlag,  StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }
            return null;
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Options:");
            Console.WriteLine("  --gui                     Launch Avalonia Graphical User Interface");
            Console.WriteLine("  -l, --level <path|name>   Path to level .PAK, folder, or short track name");
            Console.WriteLine("                            (e.g. '.../hollowood.pak' or 'hollowood')");
            Console.WriteLine("  -o, --output <dir>        Target export directory (default: 'EXPORT')");
            Console.WriteLine();
            Console.WriteLine("  Variant selection (--all-variants wins over --variant):");
            Console.WriteLine("  --variant <suffix>        Export specific variant  (e.g. 'race1', 'mission1')");
            Console.WriteLine("  --all-variants            Export base + all discovered variants");
            Console.WriteLine();
            Console.WriteLine("  Export options:");
            Console.WriteLine("  --json                    Generate scene.json manifest");
            Console.WriteLine("  --dump-all                Brute-force dump all matching .hie files");
            Console.WriteLine("  --no-props                Exclude movable props from OBJ export");
            Console.WriteLine("  --nomat                   Disable MTL material file generation");
            Console.WriteLine("  --local                   Export local mesh coordinates (no world transform)");
            Console.WriteLine("  --no-group                Disable 'g <GroupName>' grouping in OBJ output");
            Console.WriteLine("  -v, --verbose             Enable detailed pipeline stage logging");
            Console.WriteLine();
            Console.WriteLine("  Legacy (still accepted):");
            Console.WriteLine("  -t, --track <name>        Alias for -l");
            Console.WriteLine("  -l trackname:variant      Colon-suffix variant syntax (prefer --variant)");
        }
    }
}
