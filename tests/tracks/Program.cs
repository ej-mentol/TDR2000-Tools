using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Track.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string? assetsPath = ResolveAssetsPath(args);

            Console.WriteLine("================================================================");
            Console.WriteLine("       TDR2000 TRACK DISCOVERY & RACES.TXT TEST SUITE           ");
            Console.WriteLine("================================================================");

            if (assetsPath == null)
            {
                Console.WriteLine("[!] Error: Could not auto-detect Carmageddon TDR 2000 game assets directory.");
                Console.WriteLine("    Please specify your GOG/Steam/Custom path via CLI argument:");
                Console.WriteLine("    dotnet run --project tracks/TDR.Track.Tests.csproj -- \"C:\\GOG Games\\Carmageddon TDR 2000\\Assets\"");
                return 1;
            }

            Console.WriteLine($"[+] Target Assets Path: '{Path.GetFullPath(assetsPath)}'\n");

            var vfs = new PakManager();
            vfs.IndexDirectory(assetsPath);

            byte[]? racesBytes = vfs.LoadFile("races.txt");
            if (racesBytes == null || racesBytes.Length == 0)
            {
                Console.WriteLine("  [!] 'races.txt' not found in VFS.");
                return 1;
            }

            var officialTracks = TrackDiscovery.ParseRacesTxt(racesBytes);
            Console.WriteLine($"  [+] Discovered {officialTracks.Count} official tracks in 'races.txt':");
            using var md5Alg = MD5.Create();

            foreach (string t in officialTracks)
            {
                string clean = TrackDiscovery.GetBaseTrackName(t);
                string txtName = $"{clean}.txt";

                byte[]? desc = vfs.LoadFileContext(txtName, clean);
                if (desc != null && desc.Length > 0)
                {
                    string md5 = Convert.ToHexString(md5Alg.ComputeHash(desc));
                    bool valid = TrackDiscovery.IsStrongTrackContent(desc);
                    Console.WriteLine($"      [✓] TRACK '{clean:15}' -> Descriptor '{txtName}' (MD5: {md5}) | Valid 3D Content: {valid}");
                }
                else
                {
                    Console.WriteLine($"      [!] TRACK '{clean:15}' -> Descriptor '{txtName}' MISSING!");
                }
            }

            Console.WriteLine("================================================================");
            return 0;
        }

        private static string? ResolveAssetsPath(string[] args)
        {
            if (args.Length > 0 && Directory.Exists(args[0]))
                return args[0];

            string[] candidates = new[]
            {
                @"/home/dev/files/TDR/extracted_assets",
                @"C:\Games\Carmageddon\Assets",
                @"C:\Games\Carmageddon",
                @"C:\GOG Games\Carmageddon TDR 2000\Assets",
                @"C:\GOG Games\Carmageddon TDR 2000",
                @"C:\Program Files (x86)\GOG Galaxy\Games\Carmageddon TDR 2000\Assets",
                @"C:\Program Files (x86)\GOG Galaxy\Games\Carmageddon TDR 2000",
                @"C:\Program Files (x86)\Steam\steamapps\common\Carmageddon TDR 2000\Assets",
                @"C:\Program Files (x86)\Steam\steamapps\common\Carmageddon TDR 2000",
                @"C:\Program Files (x86)\Carmageddon TDR 2000\Assets",
                @".\Assets",
                @"."
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate) && (Directory.GetFiles(candidate, "*.pak", SearchOption.AllDirectories).Length > 0 || Directory.Exists(Path.Combine(candidate, "tracks"))))
                    return candidate;
            }

            return null;
        }
    }
}
