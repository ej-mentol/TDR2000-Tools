using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.PakLib.Tests
{
    public static class TrackDiscoveryTest
    {
        public static void Run(string assetsPath)
        {
            Console.WriteLine("\n================================================================");
            Console.WriteLine("        OFFICIAL RACES.TXT & TRACK DISCOVERY TEST SUITE         ");
            Console.WriteLine("================================================================");

            var vfs = new PakManager();
            vfs.IndexDirectory(assetsPath);

            // 1. Load races.txt from VFS
            byte[]? racesBytes = vfs.LoadFile("races.txt");
            if (racesBytes == null || racesBytes.Length == 0)
            {
                Console.WriteLine("  [!] 'races.txt' not found in VFS context.");
                return;
            }

            var officialTracks = TrackDiscovery.ParseRacesTxt(racesBytes);
            Console.WriteLine($"  [+] Discovered {officialTracks.Count} official track entry(ies) in 'races.txt':");
            foreach (var t in officialTracks)
            {
                Console.WriteLine($"      - {t}");
            }

            // 2. Validate Track Descriptors & Assets for each official track
            Console.WriteLine("\n  [+] Validating Track Descriptors & 3D Level Assets:");
            using var md5Alg = MD5.Create();

            foreach (string trackName in officialTracks)
            {
                string cleanTrack = TrackDiscovery.GetBaseTrackName(trackName);
                string trackTxtName = $"{cleanTrack}.txt";

                byte[]? descriptorData = vfs.LoadFileContext(trackTxtName, cleanTrack);
                if (descriptorData != null && descriptorData.Length > 0)
                {
                    string hash = Convert.ToHexString(md5Alg.ComputeHash(descriptorData));
                    bool isValid3D = TrackDiscovery.IsStrongTrackContent(descriptorData);
                    Console.WriteLine($"      [✓] TRACK '{cleanTrack:15}' -> Descriptor '{trackTxtName}' Found ({descriptorData.Length} bytes, MD5: {hash}) | Valid 3D Engine Content: {isValid3D}");
                }
                else
                {
                    Console.WriteLine($"      [!] TRACK '{cleanTrack:15}' -> Descriptor '{trackTxtName}' MISSING in VFS context!");
                }
            }

            Console.WriteLine("================================================================");
        }
    }
}
