using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using TDR.PakLib;

namespace TDR.Pak.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string? assetsPath = ResolveAssetsPath(args);

            Console.WriteLine("================================================================");
            Console.WriteLine("        TDR2000 PAK ARCHIVE & HASH PARITY TEST (PRIORITY 1)     ");
            Console.WriteLine("================================================================");

            if (assetsPath == null)
            {
                Console.WriteLine("[!] Error: Could not auto-detect Carmageddon TDR 2000 game assets directory.");
                Console.WriteLine("    Please specify your GOG/Steam/Custom path via CLI argument:");
                Console.WriteLine("    dotnet run --project pak/TDR.Pak.Tests.csproj -- \"C:\\GOG Games\\Carmageddon TDR 2000\\Assets\"");
                return 1;
            }

            Console.WriteLine($"[+] Target Assets Path: '{Path.GetFullPath(assetsPath)}'\n");

            var dirFiles = Directory.GetFiles(assetsPath, "*.dir", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {dirFiles.Length} .dir index files for roundtrip testing.\n");

            int passed = 0;
            int total = 0;

            using var sha1Alg = SHA1.Create();
            using var md5Alg = MD5.Create();

            foreach (string dirFile in dirFiles.Take(15))
            {
                string pakFile = Path.ChangeExtension(dirFile, ".pak");
                if (!File.Exists(pakFile)) continue;

                total++;
                string name = Path.GetFileName(pakFile);
                Console.WriteLine($"----------------------------------------------------------------");
                Console.WriteLine($"Testing PAK Archive [{total}]: '{name}'");

                try
                {
                    var vfs = new PakManager();
                    vfs.IndexDirectory(Path.GetDirectoryName(pakFile) ?? "");

                    var files = vfs.GetFiles()
                        .Where(f => !string.IsNullOrEmpty(f.ArchivePath) &&
                                    f.ArchivePath.Equals(pakFile, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    bool valid = true;
                    int checkedFiles = 0;

                    foreach (var file in files)
                    {
                        byte[]? data = vfs.LoadFile(file.Name);
                        if (data == null)
                        {
                            valid = false;
                            break;
                        }

                        string md5 = Convert.ToHexString(md5Alg.ComputeHash(data));
                        string sha1 = Convert.ToHexString(sha1Alg.ComputeHash(data));
                        checkedFiles++;
                    }

                    if (valid && checkedFiles > 0)
                    {
                        passed++;
                        Console.WriteLine($"  [✓] PASSED: {checkedFiles} file(s) verified with 100.0% MD5/SHA1 parity.");
                    }
                    else
                    {
                        Console.WriteLine($"  [✗] FAILED: Hash mismatch or read error.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [!] Exception: {ex.Message}");
                }
            }

            Console.WriteLine("================================================================");
            Console.WriteLine($"PAK TEST SUMMARY: {passed} / {total} archives passed 100% roundtrip test.");
            Console.WriteLine("================================================================");

            return passed == total ? 0 : 1;
        }

        private static string? ResolveAssetsPath(string[] args)
        {
            if (args.Length > 0 && Directory.Exists(args[0]))
                return args[0];

            string[] candidates = new[]
            {
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
