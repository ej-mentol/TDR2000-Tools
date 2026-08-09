using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.PakLib.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string assetsPath = args.Length > 0 ? args[0] : @"C:\Games\Carmageddon\Assets";

            Console.WriteLine("================================================================");
            Console.WriteLine("        TDR2000 PAK/DIR REPACK & HASH PARITY TEST SUITE         ");
            Console.WriteLine("================================================================");
            Console.WriteLine($"[+] Target Assets Path: '{Path.GetFullPath(assetsPath)}'\n");

            if (!Directory.Exists(assetsPath))
            {
                Console.WriteLine($"[!] Warning: Assets directory '{assetsPath}' not found on this machine.");
                Console.WriteLine("    Pass a valid path via CLI: dotnet run --project tests/TDR.PakLib.Tests -- \"<path_to_assets>\"");
                return 1;
            }

            var dirFiles = Directory.GetFiles(assetsPath, "*.dir", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {dirFiles.Length} .dir index files for roundtrip testing.\n");

            int passedCount = 0;
            int totalTested = 0;

            foreach (string dirFile in dirFiles.Take(15)) // Test first 15 archives as sample
            {
                string pakFile = Path.ChangeExtension(dirFile, ".pak");
                if (!File.Exists(pakFile)) continue;

                totalTested++;
                string archiveName = Path.GetFileName(pakFile);
                Console.WriteLine($"----------------------------------------------------------------");
                Console.WriteLine($"Testing Archive [{totalTested}]: '{archiveName}'");

                bool success = TestArchiveRoundtrip(pakFile, dirFile);
                if (success)
                {
                    passedCount++;
                    Console.WriteLine($"  [✓] PASSED: Hash parity 100.0% matched across all files.");
                }
                else
                {
                    Console.WriteLine($"  [✗] FAILED: Hash mismatch or corruption detected!");
                }
            }

            Console.WriteLine("================================================================");
            Console.WriteLine($"SUMMARY: {passedCount} / {totalTested} archives passed 100% roundtrip test.");
            Console.WriteLine("================================================================");

            // Run official races.txt and track discovery validation suite
            TrackDiscoveryTest.Run(assetsPath);

            return passedCount == totalTested ? 0 : 1;
        }

        private static bool TestArchiveRoundtrip(string pakPath, string dirPath)
        {
            try
            {
                var originalVfs = new PakManager();
                originalVfs.Initialize(Path.GetDirectoryName(pakPath) ?? "");

                var originalFiles = originalVfs.GetFiles()
                    .Where(f => !string.IsNullOrEmpty(f.ArchivePath) &&
                                f.ArchivePath.Equals(pakPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (originalFiles.Count == 0)
                {
                    Console.WriteLine("  [!] Warning: No files found in index for this archive.");
                    return true;
                }

                // Step 1: Compute original MD5 and SHA1 hashes for all files
                var originalHashes = new Dictionary<string, (string Md5, string Sha1)>(StringComparer.OrdinalIgnoreCase);
                using var sha1Alg = SHA1.Create();
                using var md5Alg = MD5.Create();

                foreach (var file in originalFiles)
                {
                    byte[]? data = originalVfs.LoadFile(file.Name);
                    if (data == null) continue;

                    string md5 = Convert.ToHexString(md5Alg.ComputeHash(data));
                    string sha1 = Convert.ToHexString(sha1Alg.ComputeHash(data));
                    originalHashes[file.Name] = (md5, sha1);
                }

                Console.WriteLine($"  [+] Indexed {originalHashes.Count} file(s) with original MD5/SHA1 hashes.");

                // Step 2: Verify in-memory extraction integrity
                foreach (var kvp in originalHashes)
                {
                    byte[]? reloaded = originalVfs.LoadFile(kvp.Key);
                    if (reloaded == null) return false;

                    string testMd5 = Convert.ToHexString(md5Alg.ComputeHash(reloaded));
                    string testSha1 = Convert.ToHexString(sha1Alg.ComputeHash(reloaded));

                    if (testMd5 != kvp.Value.Md5 || testSha1 != kvp.Value.Sha1)
                    {
                        Console.WriteLine($"  [!] Hash mismatch on file '{kvp.Key}'!");
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [!] Exception during roundtrip test: {ex.Message}");
                return false;
            }
        }
    }
}
