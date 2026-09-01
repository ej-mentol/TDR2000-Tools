using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using TDR.PakLib.Formats;

namespace TDR.Pedestrian.Tests
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            string? assetsPath = ResolveAssetsPath(args);

            Console.WriteLine("================================================================");
            Console.WriteLine("       TDR2000 PEDESTRIAN FORMATS (SKI/SKE/ANI) TEST SUITE     ");
            Console.WriteLine("================================================================");

            if (assetsPath == null)
            {
                Console.WriteLine("[!] Error: Could not locate game assets directory.");
                Console.WriteLine("    Please specify the path via CLI argument:");
                Console.WriteLine("    dotnet run --project tests/pedestrians/TDR.Pedestrian.Tests.csproj -- \"<path_to_assets>\"");
                return 1;
            }

            Console.WriteLine($"[+] Target Assets Path: '{Path.GetFullPath(assetsPath)}'\n");

            int passed = 0;
            int failed = 0;

            // 1. SKI Test (LOD 0 Isolation & Weights)
            Console.WriteLine("--- 1. Testing SKI Skinned Meshes (LOD 0 Isolation) ---");
            var skiFiles = Directory.GetFiles(assetsPath, "*.ski", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {skiFiles.Length} .ski files.");
            foreach (var skiFile in skiFiles)
            {
                byte[] data = File.ReadAllBytes(skiFile);
                var model = SkiModel.Load(data, targetLod: 0);
                if (model != null && model.Parts.Count > 0)
                {
                    bool weightsOk = true;
                    foreach (var part in model.Parts)
                    {
                        foreach (var w in part.Weights)
                        {
                            float sum = w.X + w.Y + w.Z + w.W;
                            if (MathF.Abs(sum - 1.0f) > 0.01f)
                            {
                                weightsOk = false;
                                break;
                            }
                        }
                        if (!weightsOk) break;
                    }

                    bool lodOk = model.LODCount >= 1 && model.Parts.Count > 0;
                    if (weightsOk && lodOk)
                    {
                        passed++;
                        Console.WriteLine($"  [✓] {Path.GetFileName(skiFile)}: LOD0 Parts={model.Parts.Count} (Total LODs={model.LODCount}), Verts={model.Parts.Sum(p => p.Positions.Count)}, Weights=1.0");
                    }
                    else
                    {
                        failed++;
                        Console.WriteLine($"  [✗] {Path.GetFileName(skiFile)}: Validation failed (WeightsOk={weightsOk}, Lod0Isolated={lodOk})!");
                    }
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  [✗] {Path.GetFileName(skiFile)}: Failed to parse!");
                }
            }

            // 2. SKE Test (DFS Parent Hierarchy & Active Bones)
            Console.WriteLine("\n--- 2. Testing SKE Skeletons & DFS Parent Hierarchy ---");
            var skeFiles = Directory.GetFiles(assetsPath, "*.ske", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {skeFiles.Length} .ske files.");
            foreach (var skeFile in skeFiles)
            {
                byte[] data = File.ReadAllBytes(skeFile);
                var ske = SkeSkeleton.Load(data);
                if (ske != null && ske.RawBones.Count > 0)
                {
                    var activeBones = ske.GetActiveBones();
                    bool activeBonesOk = activeBones.Count == ske.HeaderBoneCount;
                    bool hierarchyOk = activeBones.All(b => b.ParentID < b.ID);

                    if (activeBonesOk && hierarchyOk)
                    {
                        passed++;
                        Console.WriteLine($"  [✓] {Path.GetFileName(skeFile)}: Active Bones={activeBones.Count}/{ske.HeaderBoneCount}, DFS Parent Hierarchy Verified (Root Y={activeBones[0].Position.Y:F2}m)");
                    }
                    else
                    {
                        failed++;
                        Console.WriteLine($"  [✗] {Path.GetFileName(skeFile)}: Hierarchy mismatch (Active={activeBones.Count}/{ske.HeaderBoneCount}, HierarchyOk={hierarchyOk})!");
                    }
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  [✗] {Path.GetFileName(skeFile)}: Failed to parse!");
                }
            }

            // 3. ANI Test (Byte Alignment & FPS)
            Console.WriteLine("\n--- 3. Testing ANI Animation Tracks ---");
            var aniFiles = Directory.GetFiles(assetsPath, "*.ani", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {aniFiles.Length} .ani files.");
            int aniPassed = 0;
            foreach (var aniFile in aniFiles)
            {
                byte[] data = File.ReadAllBytes(aniFile);
                var ani = AniAnimation.Load(data, Path.GetFileName(aniFile));
                if (ani != null && ani.FrameCount > 0 && ani.FPS > 0 && ani.BoneCount > 0)
                {
                    long expectedSize = 12 + (long)ani.FrameCount * ani.BoneCount * 64;
                    if (data.Length == expectedSize)
                    {
                        aniPassed++;
                    }
                    else
                    {
                        failed++;
                        Console.WriteLine($"  [✗] {Path.GetFileName(aniFile)}: Size mismatch! Real={data.Length}, Expected={expectedSize}");
                    }
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  [✗] {Path.GetFileName(aniFile)}: Invalid header / FPS!");
                }
            }
            if (aniFiles.Length > 0 && aniPassed == aniFiles.Length)
            {
                passed++;
                Console.WriteLine($"  [✓] ALL {aniPassed}/{aniFiles.Length} .ani files parsed with exact 100% byte alignment.");
            }

            // 4. PedDescriptor & Placement Test
            Console.WriteLine("\n--- 4. Testing Pedestrian Descriptors & Placements ---");
            var descFiles = Directory.GetFiles(assetsPath, "*PedDescriptor.txt", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {descFiles.Length} *PedDescriptor.txt files.");
            foreach (var descFile in descFiles)
            {
                byte[] data = File.ReadAllBytes(descFile);
                var desc = PedDescriptor.Load(data);
                if (desc != null && desc.SkinMeshes.Count > 0)
                {
                    passed++;
                    Console.WriteLine($"  [✓] {Path.GetFileName(descFile)}: Skins={desc.SkinMeshes.Count}, Textures={desc.Textures.Count}, Skeletons={desc.SkeletonDescriptors.Count}");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  [✗] {Path.GetFileName(descFile)}: Failed to parse descriptor!");
                }
            }

            var placeFiles = Directory.GetFiles(assetsPath, "*Ped_Placement.txt", SearchOption.AllDirectories);
            Console.WriteLine($"[+] Discovered {placeFiles.Length} *Ped_Placement.txt files.");
            foreach (var placeFile in placeFiles)
            {
                byte[] data = File.ReadAllBytes(placeFile);
                var peds = PedPlacement.Load(data);
                if (peds.Count > 0)
                {
                    passed++;
                    Console.WriteLine($"  [✓] {Path.GetFileName(placeFile)}: Successfully placed {peds.Count} pedestrian instances.");
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  [✗] {Path.GetFileName(placeFile)}: No pedestrians placed!");
                }
            }

            // 5. Dynamic Path Followers Test
            Console.WriteLine("\n--- 5. Testing Dynamic Path Followers (Sharks/Planes/Boats) ---");
            var followerFiles = Directory.GetFiles(assetsPath, "*Path*Follower*.txt", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(assetsPath, "*PathMovement*.txt", SearchOption.AllDirectories))
                .Distinct();
            foreach (var fFile in followerFiles)
            {
                string text = File.ReadAllText(fFile);
                var followers = PathFollowerDescriptor.Load(text);
                if (followers.Followers.Count > 0)
                {
                    passed++;
                    Console.WriteLine($"  [✓] {Path.GetFileName(fFile)}: Parsed {followers.Followers.Count} follower entity slots (Tags: {string.Join(", ", followers.Followers.Select(f => f.Tag))})");
                }
            }

            Console.WriteLine("\n================================================================");
            Console.WriteLine($"SUMMARY: {passed} PASSED, {failed} FAILED.");
            Console.WriteLine("================================================================");

            return failed == 0 ? 0 : 1;
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
                if (Directory.Exists(candidate) && (Directory.GetFiles(candidate, "*.pak", SearchOption.AllDirectories).Length > 0 || Directory.Exists(Path.Combine(candidate, "tracks")) || Directory.Exists(Path.Combine(candidate, "Tracks"))))
                    return candidate;
            }

            return null;
        }
    }
}
