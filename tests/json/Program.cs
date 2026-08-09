using System;
using System.IO;
using System.Text.Json;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Export;

namespace TDR.Json.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string assetsPath = args.Length > 0 ? args[0] : @"C:\Games\Carmageddon\Assets";

            Console.WriteLine("================================================================");
            Console.WriteLine("       TDR2000 SCENE JSON MANIFEST TEST SUITE (TEST 3)          ");
            Console.WriteLine("================================================================");
            Console.WriteLine($"[+] Target Assets Path: '{Path.GetFullPath(assetsPath)}'\n");

            if (!Directory.Exists(assetsPath))
            {
                Console.WriteLine($"[!] Warning: Directory '{assetsPath}' not found.");
                Console.WriteLine("    Run: dotnet run --project tests/json/TDR.Json.Tests.csproj -- \"<assets_path>\"");
                return 1;
            }

            var vfs = new PakManager();
            vfs.IndexDirectory(assetsPath);

            string tempOutDir = Path.Combine(Path.GetTempPath(), "TDR_Json_Test_Out");
            Directory.CreateDirectory(tempOutDir);

            try
            {
                Console.WriteLine("  [+] Testing SceneJsonExporter manifest generation for 'Hollowood':");
                
                // Create dummy Hollowood.obj so SceneJsonExporter validates file existence
                File.WriteAllText(Path.Combine(tempOutDir, "Hollowood.obj"), "# dummy obj content");

                var dummyResult = new TrackExportResult
                {
                    Success = true,
                    TrackName = "Hollowood",
                    OutputDirectory = tempOutDir,
                    BaseMeshFileName = "Hollowood.obj",
                    WaterMeshFileName = "HollowoodWater.obj",
                    SkyMeshFileName = "FilmSkysphereStudio.obj"
                };

                SceneJsonExporter.GenerateManifest(
                    "Hollowood",
                    null,
                    vfs,
                    tempOutDir,
                    (path) => vfs.LoadFileContext(path, "Hollowood"),
                    dummyResult,
                    false,
                    null
                );

                string manifestFile = Path.Combine(tempOutDir, "Hollowood.json");
                if (!File.Exists(manifestFile))
                {
                    manifestFile = Path.Combine(tempOutDir, "scene.json");
                }

                if (File.Exists(manifestFile))
                {
                    string jsonText = File.ReadAllText(manifestFile);
                    using var doc = JsonDocument.Parse(jsonText);
                    var root = doc.RootElement;

                    string track = root.GetProperty("trackName").GetString() ?? "";
                    string schema = root.GetProperty("schemaVersion").ToString();
                    string? baseMesh = root.GetProperty("baseMesh").GetString();

                    Console.WriteLine($"      [✓] Manifest generated at '{manifestFile}'");
                    Console.WriteLine($"      [✓] SchemaVersion: '{schema}' | Track: '{track}' | BaseMesh: '{baseMesh}'");
                    Console.WriteLine("  [✓] PASSED: scene.json / Hollowood.json schema validation clean.");
                }
                else
                {
                    Console.WriteLine("  [✗] FAILED: Manifest file was not created.");
                    return 1;
                }
            }
            finally
            {
                if (Directory.Exists(tempOutDir))
                {
                    try { Directory.Delete(tempOutDir, true); } catch { }
                }
            }

            Console.WriteLine("================================================================");
            return 0;
        }
    }
}
