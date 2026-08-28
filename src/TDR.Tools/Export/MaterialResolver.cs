using System;
using System.IO;
using TDR.PakLib;
using TDR.PakLib.Formats;

namespace TDR.Tools.Export
{
    public enum MaterialAlphaMode
    {
        Opaque,
        Mask,
        Blend
    }

    /// <summary>
    /// Format-agnostic description of material physical properties extracted from
    /// TDR2000 TTEX descriptors, TGA bit depths, and semantic naming conventions.
    /// </summary>
    public sealed class MaterialDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string? TextureFileName { get; set; }
        public MaterialAlphaMode AlphaMode { get; set; } = MaterialAlphaMode.Opaque;
        public float Opacity { get; set; } = 1.0f;
        public float AlphaCutoff { get; set; } = 0.5f;
        public float Roughness { get; set; } = 1.0f;
        public float Metallic { get; set; } = 0.0f;
        public bool IsDoubleSided { get; set; } = true;
        public bool IsEmissive { get; set; }
        public float[] EmissiveColor { get; set; } = new[] { 0.0f, 0.0f, 0.0f };
        public bool HasBumpMap { get; set; }
        public string? BumpMapFileName { get; set; }

        public bool IsWater { get; set; }
        public bool IsShadow { get; set; }
        public bool IsSky { get; set; }

        /// <summary>
        /// Writes Classic Wavefront MTL Phong properties.
        /// </summary>
        public void WriteToMtl(TextWriter writer, string? savedTextureName, string? savedBumpName = null)
        {
            writer.WriteLine($"newmtl {Name}");
            writer.WriteLine("illum 2");
            writer.WriteLine("Ka 1.000 1.000 1.000");
            writer.WriteLine("Kd 1.000 1.000 1.000");

            if (IsWater)
            {
                writer.WriteLine("Ks 0.500 0.500 0.500");
                writer.WriteLine("Ns 150"); // High specular sharpness for water
                writer.WriteLine($"d {Opacity:0.00}\nTr {(1.0f - Opacity):0.00}");
            }
            else if (IsShadow)
            {
                writer.WriteLine("Ks 0.000 0.000 0.000");
                writer.WriteLine("Ns 0"); // 100% matte
                writer.WriteLine($"d {Opacity:0.00}\nTr {(1.0f - Opacity):0.00}");
            }
            else if (AlphaMode == MaterialAlphaMode.Blend)
            {
                writer.WriteLine("Ks 0.200 0.200 0.200");
                writer.WriteLine("Ns 50");
                writer.WriteLine($"d {Opacity:0.00}\nTr {(1.0f - Opacity):0.00}");
            }
            else if (AlphaMode == MaterialAlphaMode.Mask)
            {
                writer.WriteLine("Ks 0.000 0.000 0.000");
                writer.WriteLine("Ns 10");
                writer.WriteLine("d 1.00");
            }
            else
            {
                writer.WriteLine("Ks 0.000 0.000 0.000");
                writer.WriteLine("Ns 10");
                writer.WriteLine("d 1.00");
            }

            if (!string.IsNullOrEmpty(savedTextureName))
            {
                writer.WriteLine($"map_Kd {savedTextureName}");
                if (AlphaMode != MaterialAlphaMode.Opaque)
                {
                    writer.WriteLine($"map_d {savedTextureName}");
                }
            }

            if (!string.IsNullOrEmpty(savedBumpName))
            {
                writer.WriteLine($"map_Bump -bm 1.0 {savedBumpName}");
            }
        }
    }

    /// <summary>
    /// Central authority for analyzing TDR2000 materials, TTEX flags, TGA alpha channels,
    /// and semantic rules (water, shadows, emissive glows, skies).
    /// </summary>
    public static class MaterialResolver
    {
        public static MaterialDefinition Resolve(
            string materialName,
            string? resolvedTextureFile,
            string? archivePath,
            PakManager vfs,
            string? trackContext = null)
        {
            var def = new MaterialDefinition
            {
                Name = materialName,
                TextureFileName = resolvedTextureFile,
                IsDoubleSided = true,
                Roughness = 1.0f,
                Metallic = 0.0f
            };

            string normMat = materialName.ToLowerInvariant();
            string normFile = (resolvedTextureFile ?? "").ToLowerInvariant();

            // 1. Primary Authority: Read native TTEX (.tx) descriptor if available
            byte[]? txBytes = (!string.IsNullOrEmpty(archivePath) ? vfs.LoadFileContext($"{materialName}.tx", archivePath) : null) ??
                              (!string.IsNullOrEmpty(trackContext) ? vfs.LoadFileContext($"{materialName}.tx", trackContext) : null) ??
                              vfs.LoadFile($"{materialName}.tx");
            var txDesc = TxDescriptor.Load(txBytes, materialName);

            if (txDesc != null)
            {
                // Strict compliance with engine binary TTEX descriptor:
                if (txDesc.TransparencyMode == TxTransparencyMode.Blend)
                {
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.Roughness = 0.5f;
                }
                else if (txDesc.TransparencyMode == TxTransparencyMode.Mask)
                {
                    def.AlphaMode = MaterialAlphaMode.Mask;
                    def.AlphaCutoff = 0.5f;
                }
                else
                {
                    def.AlphaMode = MaterialAlphaMode.Opaque;
                }

                // Check TTEX hardware shader flags (Additive glow / Water)
                if ((txDesc.Flags & 4) != 0) // Additive Glow / Emissive
                {
                    def.IsEmissive = true;
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.EmissiveColor = new[] { 1.0f, 1.0f, 1.0f };
                    def.Roughness = 1.0f;
                }

                if ((txDesc.Flags & 16) != 0) // Water surface shader
                {
                    def.IsWater = true;
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.Opacity = 0.65f;
                    def.Roughness = 0.25f;
                    def.Metallic = 0.0f;
                    def.HasBumpMap = true;
                }
            }
            else
            {
                // Fallback: When no native .tx descriptor exists, deduce from TGA bytes and semantic naming

                // 2. Secondary Authority: Inspect actual TGA bit depth and alpha channel
                if (!string.IsNullOrEmpty(resolvedTextureFile))
                {
                    byte[]? tgaBytes = (!string.IsNullOrEmpty(archivePath) ? vfs.LoadFileContext(resolvedTextureFile, archivePath) : null) ??
                                       (!string.IsNullOrEmpty(trackContext) ? vfs.LoadFileContext(resolvedTextureFile, trackContext) : null) ??
                                       vfs.LoadFile(resolvedTextureFile);
                    var tgaMode = TgaDecoder.DetectTgaTransparency(tgaBytes);

                    if (tgaMode == TxTransparencyMode.Mask)
                    {
                        def.AlphaMode = MaterialAlphaMode.Mask;
                        def.AlphaCutoff = 0.5f;
                    }
                    else if (tgaMode == TxTransparencyMode.Blend)
                    {
                        // Only assign BLEND for genuine translucent effects (glass, water, coronas, shadows).
                        // Generic solid meshes (buildings, wheels, chassis, terrain) with 32-bit TGA edge anti-aliasing use MASK
                        // to preserve depth-buffer (Z-write) and prevent see-through / X-ray artifacts!
                        bool isKnownTranslucent = (normMat.Contains("glass") || normFile.Contains("glass") ||
                                                   normMat.Contains("windshield") || normFile.Contains("windshield") ||
                                                   normMat.Contains("windscreen") || normFile.Contains("windscreen") ||
                                                   normMat.Contains("water") || normFile.Contains("water") ||
                                                   normMat.Contains("sea")   || normFile.Contains("sea") ||
                                                   normMat.Contains("shadow") || normFile.Contains("shadow") ||
                                                   normMat.Contains("corona") || normFile.Contains("corona") ||
                                                   normMat.Contains("glow")   || normFile.Contains("glow") ||
                                                   normMat.Contains("flare")  || normFile.Contains("flare")) &&
                                                  !normMat.Contains("wall") && !normMat.Contains("bld") && !normMat.Contains("facade");

                        if (isKnownTranslucent)
                        {
                            def.AlphaMode = MaterialAlphaMode.Blend;
                        }
                        else
                        {
                            def.AlphaMode = MaterialAlphaMode.Mask;
                            def.AlphaCutoff = 0.5f;
                        }
                    }
                    else
                    {
                        def.AlphaMode = MaterialAlphaMode.Opaque;
                    }
                }

                // 3. Fallback Heuristic: Water & Liquids
                if (normMat.Contains("water") || normFile.Contains("water") ||
                    normMat.Contains("sea")   || normFile.Contains("sea")   ||
                    normMat.Contains("river") || normFile.Contains("river") ||
                    normMat.Contains("ocean") || normFile.Contains("ocean") ||
                    normMat.Contains("lake")  || normFile.Contains("lake")  ||
                    normMat.Contains("bay")   || normFile.Contains("bay")   ||
                    normMat.Contains("pool")  || normFile.Contains("pool")  ||
                    normMat.Contains("pond")  || normFile.Contains("pond")  ||
                    normMat.Contains("swamp") || normFile.Contains("swamp") ||
                    normMat.Contains("harbour") || normFile.Contains("harbour") ||
                    normMat.Contains("bumpfx") || normFile.Contains("bumpfx"))
                {
                    def.IsWater = true;
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.Opacity = 0.65f;
                    def.Roughness = 0.25f;
                    def.Metallic = 0.0f;
                    def.IsDoubleSided = true;
                    def.HasBumpMap = true;
                }

                // 4. Fallback Heuristic: Shadows & Ground Decals
                if (normMat.Contains("shadow") || normFile.Contains("shadow") ||
                    normMat.StartsWith("shd_") || normFile.StartsWith("shd_") ||
                    normMat.EndsWith("_shd")   || normFile.EndsWith("_shd"))
                {
                    def.IsShadow = true;
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.Opacity = 0.6f;
                    def.Roughness = 1.0f;
                    def.Metallic = 0.0f;
                    def.IsDoubleSided = true;
                }

                // 5. Fallback Heuristic: Glass & Windshields
                if ((normMat.Contains("clearglass") || normFile.Contains("clearglass") ||
                     normMat.Contains("windshield") || normFile.Contains("windshield") ||
                     normMat.Contains("windscreen") || normFile.Contains("windscreen")) &&
                    !normMat.Contains("wall") && !normMat.Contains("bld") && !normMat.Contains("facade"))
                {
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.Opacity = 0.5f;
                    def.Roughness = 0.1f;
                    def.Metallic = 0.0f;
                    def.IsDoubleSided = true;
                }

                // 6. Fallback Heuristic: Emissive overlays (Halos, Coronas, Glows, Flares)
                if (normMat.Contains("corona") || normFile.Contains("corona") ||
                    normMat.Contains("halo")   || normFile.Contains("halo")   ||
                    normMat.Contains("glow")   || normFile.Contains("glow")   ||
                    normMat.Contains("flare")  || normFile.Contains("flare")  ||
                    normMat.Contains("beam")   || normFile.Contains("beam"))
                {
                    def.IsEmissive = true;
                    def.AlphaMode = MaterialAlphaMode.Blend;
                    def.Opacity = 0.8f;
                    def.EmissiveColor = new[] { 1.0f, 1.0f, 1.0f };
                    def.Roughness = 1.0f;
                    def.Metallic = 0.0f;
                    def.IsDoubleSided = true;
                }

                // 7. Fallback Heuristic: Sky Sphere / Sky Dome
                if (normMat.Contains("sky") || normFile.Contains("sky") ||
                    normMat.Contains("cloud") || normMat.Contains("horizon"))
                {
                    def.IsSky = true;
                    def.IsEmissive = true;
                    def.EmissiveColor = new[] { 1.0f, 1.0f, 1.0f };
                    def.Roughness = 1.0f;
                    def.Metallic = 0.0f;
                    def.IsDoubleSided = true;
                }
            }

            return def;
        }

        public static bool IsDoubleSidedTexture(string texName)
        {
            if (string.IsNullOrEmpty(texName)) return false;
            string nTex = texName.ToLowerInvariant();
            return nTex.Contains("water") || nTex.Contains("sea") || nTex.Contains("ocean") ||
                   nTex.Contains("river") || nTex.Contains("lake") || nTex.Contains("bay") ||
                   nTex.Contains("pool") || nTex.Contains("pond") || nTex.Contains("harbour") ||
                   nTex.Contains("swamp") || nTex.Contains("liquid") || nTex.Contains("tank") ||
                   nTex.Contains("bumpfx") || nTex.Contains("glass") || nTex.Contains("clearglass") ||
                   nTex.Contains("windshield") || nTex.Contains("windscreen") || nTex.Contains("fence") ||
                   nTex.Contains("sign") || nTex.Contains("foliage") || nTex.Contains("tree") ||
                   nTex.Contains("corona") || nTex.Contains("grate") || nTex.Contains("shadow") ||
                   nTex.Contains("shd_") || nTex.Contains("halo");
        }
    }
}
