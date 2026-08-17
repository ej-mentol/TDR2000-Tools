using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TDR.PakLib;
using TDR.PakLib.Formats;
using TDR.Tools.Services;

namespace TDR.Tools.Export
{
    public static class TextureResolver
    {
        public static int GetTextureResolutionArea(string filename)
        {
            var match = Regex.Match(filename, @"_(\d+)[x_](\d+)_", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int w) && int.TryParse(match.Groups[2].Value, out int h))
            {
                return w * h;
            }
            return 0;
        }

        public static bool NameMatch(string fileName, string targetMaterial, bool allowStrippedFallback = false)
        {
            if (!fileName.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) &&
                !fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return false;

            string cleanPath = fileName.Replace('\\', '/');
            string fileNameOnly = Path.GetFileNameWithoutExtension(cleanPath);

            string cleanMat = targetMaterial.Trim('"').Trim();
            string baseMat = cleanMat.TrimEnd('!');

            // 1. Exact match
            if (fileNameOnly.Equals(cleanMat, StringComparison.OrdinalIgnoreCase) ||
                fileNameOnly.Equals(baseMat, StringComparison.OrdinalIgnoreCase))
                return true;

            // 2. Direct resolution/bitdepth suffix match (e.g. road_main_128x128_32, tree2b!_256x256_32, tree2b_256_256_32)
            string? matchPrefix = fileNameOnly.StartsWith(cleanMat, StringComparison.OrdinalIgnoreCase) ? cleanMat
                                : (fileNameOnly.StartsWith(baseMat, StringComparison.OrdinalIgnoreCase) ? baseMat : null);

            if (matchPrefix != null && fileNameOnly.Length > matchPrefix.Length)
            {
                string sub = fileNameOnly[matchPrefix.Length..].TrimStart('_', '!');
                if (string.IsNullOrEmpty(sub) || Regex.IsMatch(sub, @"^(\d+[x_]\d+|\d+)(_\d+)?$", RegexOptions.IgnoreCase))
                    return true;
            }

            // 3. Optional fallback for stripped suffix (only if explicitly enabled for Tier 5 fallback)
            if (allowStrippedFallback)
            {
                string strippedMat = Regex.Replace(baseMat, @"[a-zA-Z]$", "");
                if (!string.IsNullOrEmpty(strippedMat) && strippedMat.Length > 2)
                {
                    if (fileNameOnly.Equals(strippedMat, StringComparison.OrdinalIgnoreCase))
                        return true;

                    if (fileNameOnly.StartsWith(strippedMat, StringComparison.OrdinalIgnoreCase) && fileNameOnly.Length > strippedMat.Length)
                    {
                        string sub = fileNameOnly[strippedMat.Length..].TrimStart('_', '!');
                        if (string.IsNullOrEmpty(sub) || Regex.IsMatch(sub, @"^(\d+[x_]\d+|\d+)(_\d+)?$", RegexOptions.IgnoreCase))
                            return true;
                    }
                }
            }

            // 4. Known exact TDR texture aliases
            if (cleanMat.Equals("span", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("spanner", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_spanner", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("span", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("eng", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("engine", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_engine", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("eng", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("helm", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("helmet", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_helmet", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("helm", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("wad", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("wadocash", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_wadocash", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("wad", StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (cleanMat.Equals("ped", StringComparison.OrdinalIgnoreCase) || cleanMat.Equals("pedsign", StringComparison.OrdinalIgnoreCase))
            {
                if (fileNameOnly.StartsWith("new_pedsign", StringComparison.OrdinalIgnoreCase) || fileNameOnly.StartsWith("ped", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static bool IsOtherTrackFile(string? archivePath, string? filePath, string mainTrack)
        {
            if (string.IsNullOrEmpty(mainTrack)) return false;

            string normArchive = (archivePath ?? "").Replace('\\', '/').ToLowerInvariant();
            string normFile = (filePath ?? "").Replace('\\', '/').ToLowerInvariant();

            bool isTrackAsset = normArchive.Contains("tracks/") || normFile.Contains("tracks/") ||
                                normArchive.Contains("/tracks") || normFile.Contains("/tracks") ||
                                normArchive.Contains("tracks\\") || normFile.Contains("tracks\\");
            if (!isTrackAsset) return false;

            bool isCurrentTrack = TrackDiscoveryService.IsTrackOrAliasMatch(normArchive, mainTrack) ||
                                  TrackDiscoveryService.IsTrackOrAliasMatch(normFile, mainTrack);

            return !isCurrentTrack;
        }

        public record MatchResult(PakManager.IndexedFile File, string TierName);

        public static MatchResult? ResolveBestMatch(PakManager vfs, string materialName, string? archivePath, string? trackContext)
        {
            var vfsFiles = vfs.GetFiles();
            string mainTrack = (trackContext ?? "").ToLowerInvariant();
            string? ctxDir = archivePath != null ? Path.GetDirectoryName(archivePath) : null;

            // 1A. Exact same PAK file as the .hie model
            PakManager.IndexedFile? matchTier1A = (!string.IsNullOrEmpty(archivePath)
                ? vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) &&
                      f.ArchivePath.Equals(archivePath, StringComparison.OrdinalIgnoreCase))
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault()
                : null);
            if (matchTier1A != null) return new MatchResult(matchTier1A, "Tier 1A (Exact PAK File)");

            // 1B. Same PAK directory / folder
            PakManager.IndexedFile? matchTier1B = ctxDir != null
                ? vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) &&
                      Path.GetDirectoryName(f.ArchivePath)
                          ?.Equals(ctxDir, StringComparison.OrdinalIgnoreCase) == true)
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault()
                : null;
            if (matchTier1B != null) return new MatchResult(matchTier1B, "Tier 1B (Same PAK Directory)");

            // 2. Same Track Level (including aliases like docks for docksmd, and base track hollowood for hollowood_race1)
            string baseTrack = TrackDiscovery.GetBaseTrackName(mainTrack).ToLowerInvariant();
            PakManager.IndexedFile? matchTier2 = !string.IsNullOrEmpty(mainTrack)
                ? vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) &&
                      (TrackDiscoveryService.IsTrackOrAliasMatch(f.ArchivePath, mainTrack) ||
                       TrackDiscoveryService.IsTrackOrAliasMatch(f.Name, mainTrack) ||
                       (!string.IsNullOrEmpty(baseTrack) && (TrackDiscoveryService.IsTrackOrAliasMatch(f.ArchivePath, baseTrack) ||
                                                             TrackDiscoveryService.IsTrackOrAliasMatch(f.Name, baseTrack)))))
                      .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                      .ThenByDescending(f => f.Name.Contains("_32"))
                      .ThenByDescending(f => f.Name.Contains("_24"))
                      .FirstOrDefault()
                : null;
            if (matchTier2 != null) return new MatchResult(matchTier2, "Tier 2 (Same Track Level)");

            // 3. Shared Assets (Non-track assets, e.g. MovableObjects.pak, Powerups.pak)
            PakManager.IndexedFile? matchTier3 = vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) && !(f.ArchivePath ?? "").Replace('\\', '/').ToLowerInvariant().Contains("tracks/"))
                  .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                  .ThenByDescending(f => f.Name.Contains("_32"))
                  .ThenByDescending(f => f.Name.Contains("_24"))
                  .FirstOrDefault();
            if (matchTier3 != null) return new MatchResult(matchTier3, "Tier 3 (Shared Assets)");

            // 4. Safe VFS Global Match (Excludes other track directories to prevent cross-track asset leaks)
            PakManager.IndexedFile? matchTier4 = vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: false) && !IsOtherTrackFile(f.ArchivePath, f.Name, mainTrack))
                  .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                  .ThenByDescending(f => f.Name.Contains("_32"))
                  .ThenByDescending(f => f.Name.Contains("_24"))
                  .FirstOrDefault();
            if (matchTier4 != null) return new MatchResult(matchTier4, "Tier 4 (VFS Global Match)");

            // 5. Stripped Suffix Fallback (Excludes other track directories)
            PakManager.IndexedFile? matchTier5 = vfsFiles.Where(f => NameMatch(f.Name, materialName, allowStrippedFallback: true) && !IsOtherTrackFile(f.ArchivePath, f.Name, mainTrack))
                  .OrderByDescending(f => GetTextureResolutionArea(f.Name))
                  .ThenByDescending(f => f.Name.Contains("_32"))
                  .ThenByDescending(f => f.Name.Contains("_24"))
                  .FirstOrDefault();
            if (matchTier5 != null) return new MatchResult(matchTier5, "Tier 5 (Stripped Suffix Fallback)");

            return null;
        }

        public static string ResolvePowerupIconHie(int typeId, string name)
        {
            // If typeId comes in as raw VB Long uint32 representation (e.g. 1116733440), convert back to float ID
            if (typeId > 100000)
            {
                byte[] bytes = BitConverter.GetBytes(typeId);
                float floatVal = BitConverter.ToSingle(bytes, 0);
                if (!float.IsNaN(floatVal) && floatVal >= 0 && floatVal < 500)
                {
                    typeId = (int)Math.Round(floatVal);
                }
            }

            string lowerName = name.ToLowerInvariant();

            // 1. Mission / Quest / System Special Items (from official TDR2000.exe disassembly)
            if (lowerName.Contains("arrow")) return "ArrowArrow.hie";
            if (lowerName.Contains("bigbomb") || lowerName.Contains("big_bomb")) return "BIG_BOMBBomb.hie";
            if (lowerName.Contains("spike")) return "MortarTailSpike.hie";
            if (lowerName.Contains("bomb")) return "BombPiececube1.hie";
            if (lowerName.Contains("fuse")) return "fuseFuse_NULL.hie";
            if (lowerName.Contains("enginepart") || lowerName.Contains("engine_part")) return "EnginePartobj3.hie";
            if (lowerName.Contains("moneybag") || lowerName.Contains("money_bag")) return "DingablesMoneyBagPowerup.hie";
            if (lowerName.Contains("artillery") || lowerName.Contains("shell")) return "DingablesArtilleryShellPow.hie";
            if (lowerName.Contains("mortar")) return "mortarTail_Render.hie";
            if (lowerName.Contains("oil") || lowerName.Contains("drum")) return "Oil_DrumDrum_null.hie";

            // 2. Repair & Spanner -> Spanner Icon (Check early before engine/powerup strings!)
            if (lowerName.Contains("spanner") || lowerName.Contains("repair") || lowerName.Contains("fix"))
                return "newIconsSPANNER.hie";

            // 3. Money & Cash -> Wadocash Icon
            if (lowerName.Contains("cash") || lowerName.Contains("credit") || lowerName.Contains("money"))
                return "newIconsWADOCASH.hie";

            // 4. Time Bonus -> Time Icon
            if (lowerName.Contains("time")) return "newIconsTIME.hie";

            // 5. Pedestrian Powers & Ray Weapons -> Pedestrian Sign Icon
            if (lowerName.Contains("zombie") || lowerName.Contains("pedestrian") || lowerName.Contains("flamethrower") ||
                lowerName.Contains("ray") || lowerName.Contains("dismember"))
                return "newIconsPEDSIGN.hie";

            // 6. Direct Type ID Table
            return typeId switch
            {
                1 => "newIconsARMOUR.hie",
                2 => "newIconsPOWER.hie",
                3 => "newIconsOFFENSIVE.hie",
                4 => "newIconsTIME.hie",
                5 => "newIconsWADOCASH.hie",
                6 => "newIconsSPANNER.hie",
                7 => "newIconsFREE_REPAIRS.hie",
                8 => "newIconsINSTANT_HANDBRAKE.hie",
                9 => "newIconsPEDSIGN.hie",
                10 => "newIconsPED_ELECTRIFIER.hie",
                11 => "newIconsPED_EXPLODER.hie",
                12 => "newIconsPED_FLAMETHROWER.hie",
                13 => "newIconsPED_FORCEFIELD.hie",
                14 => "newIconsPED_GUN.hie",
                15 => "newIconsPED_REPULSION.hie",
                16 => "newIconsTURBO_COP_DISGUISE.hie",
                17 => "newIconsTURBO_DRIVE.hie",
                18 => "newIconsTURBO_INVULNERABILITY.hie",
                19 => "newIconsTURBO_JUMP.hie",
                20 => "newIconsWALL_CLIMBER.hie",
                21 => "newIconsBOUNCY_BOUNCY.hie",
                22 => "newIconsEARTHQUAKE.hie",
                23 => "newIconsFROZEN_COPS.hie",
                24 => "newIconsFROZEN_PEDS.hie",
                25 => "newIconsGOLIATH_MINE.hie",
                26 => "newIconsGRAVITY_LOW.hie",
                27 => "newIconsGREASE_WHEELS.hie",
                28 => "newIconsGRIP_MUTANT_GRAVY.hie",
                29 => "newIconsHIGH_GRAVITY.hie",
                30 => "newIconsHOT_ROD.hie",
                31 => "newIconsJUPITER_WEIGHT.hie",
                32 => "newIconsKANGAROO_JUMP.hie",
                33 => "newIconsMEGA_TURBO.hie",
                34 => "newIconsMINE_EXPLOSIVE.hie",
                35 => "newIconsMINE_PROPULSION.hie",
                36 => "newIconsPINBALL_WHEELS.hie",
                37 => "newIconsROCK_SPRINGS.hie",
                38 => "newIconsSUICIDE_PEDS.hie",
                39 => "newIconsTURBO_PEDESTRIANS.hie",
                40 => "newIconsUNDERWATER_DRIVE.hie",
                41 => "newIconsWELDED_HANDBRAKE.hie",
                42 => "newIconsZ_VAPORISER.hie",
                _ => "newIconsSPANNER.hie"
            };
        }
    }
}
