using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TDR.Tools.Export
{
    #region glTF 2.0 Schema DTOs
    public sealed class GltfManifest
    {
        [JsonPropertyName("asset")]
        public GltfAsset Asset { get; set; } = new();

        [JsonPropertyName("scene")]
        public int Scene { get; set; } = 0;

        [JsonPropertyName("scenes")]
        public List<GltfScene> Scenes { get; set; } = new();

        [JsonPropertyName("nodes")]
        public List<GltfNode> Nodes { get; set; } = new();

        [JsonPropertyName("meshes")]
        public List<GltfMesh> Meshes { get; set; } = new();

        [JsonPropertyName("materials")]
        public List<GltfMaterial> Materials { get; set; } = new();

        [JsonPropertyName("textures")]
        public List<GltfTexture> Textures { get; set; } = new();

        [JsonPropertyName("images")]
        public List<GltfImage> Images { get; set; } = new();

        [JsonPropertyName("accessors")]
        public List<GltfAccessor> Accessors { get; set; } = new();

        [JsonPropertyName("bufferViews")]
        public List<GltfBufferView> BufferViews { get; set; } = new();

        [JsonPropertyName("skins")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<GltfSkin>? Skins { get; set; }

        [JsonPropertyName("extensionsUsed")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? ExtensionsUsed { get; set; }

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }

        [JsonPropertyName("buffers")]
        public List<GltfBuffer> Buffers { get; set; } = new();
    }

    public sealed class GltfSkin
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("inverseBindMatrices")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? InverseBindMatrices { get; set; }

        [JsonPropertyName("joints")]
        public List<int> Joints { get; set; } = new();

        [JsonPropertyName("skeleton")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Skeleton { get; set; }
    }

    public sealed class GltfLightsExtension
    {
        [JsonPropertyName("lights")]
        public List<GltfLight> Lights { get; set; } = new();
    }

    public sealed class GltfLight
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = "directional";

        [JsonPropertyName("color")]
        public float[] Color { get; set; } = new[] { 1.0f, 1.0f, 1.0f };

        [JsonPropertyName("intensity")]
        public float Intensity { get; set; } = 1.0f;
    }

    public sealed class GltfLightNodeExtension
    {
        [JsonPropertyName("light")]
        public int Light { get; set; }
    }

    public sealed class GltfAsset
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "2.0";

        [JsonPropertyName("generator")]
        public string Generator { get; set; } = "TDR2000 Tools glTF 2.0 Pipeline";
    }

    public sealed class GltfScene
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("nodes")]
        public List<int> Nodes { get; set; } = new();
    }

    public sealed class GltfNode
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("mesh")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Mesh { get; set; }

        [JsonPropertyName("matrix")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Matrix { get; set; }

        [JsonPropertyName("translation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Translation { get; set; }

        [JsonPropertyName("rotation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Rotation { get; set; }

        [JsonPropertyName("scale")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? Scale { get; set; }

        [JsonPropertyName("children")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<int>? Children { get; set; }

        public void AddChild(int childIndex)
        {
            Children ??= new List<int>();
            Children.Add(childIndex);
        }

        [JsonPropertyName("skin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Skin { get; set; }

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }
    }

    public sealed class GltfMesh
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("primitives")]
        public List<GltfPrimitive> Primitives { get; set; } = new();
    }

    public sealed class GltfPrimitive
    {
        [JsonPropertyName("attributes")]
        public Dictionary<string, int> Attributes { get; set; } = new();

        [JsonPropertyName("indices")]
        public int? Indices { get; set; }

        [JsonPropertyName("material")]
        public int? Material { get; set; }
    }

    public sealed class GltfMaterial
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("pbrMetallicRoughness")]
        public GltfPbr PbrMetallicRoughness { get; set; } = new();

        [JsonPropertyName("alphaMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AlphaMode { get; set; }

        [JsonPropertyName("alphaCutoff")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float? AlphaCutoff { get; set; }

        [JsonPropertyName("emissiveFactor")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public float[]? EmissiveFactor { get; set; }

        [JsonPropertyName("emissiveTexture")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public GltfTextureInfo? EmissiveTexture { get; set; }

        [JsonPropertyName("doubleSided")]
        public bool DoubleSided { get; set; } = true;

        [JsonPropertyName("extensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, object>? Extensions { get; set; }
    }

    public sealed class GltfPbr
    {
        [JsonPropertyName("baseColorFactor")]
        public float[] BaseColorFactor { get; set; } = new[] { 1.0f, 1.0f, 1.0f, 1.0f };

        [JsonPropertyName("baseColorTexture")]
        public GltfTextureInfo? BaseColorTexture { get; set; }

        [JsonPropertyName("metallicFactor")]
        public float MetallicFactor { get; set; } = 0.0f;

        [JsonPropertyName("roughnessFactor")]
        public float RoughnessFactor { get; set; } = 1.0f;
    }

    public sealed class GltfTextureInfo
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }
    }

    public sealed class GltfTexture
    {
        [JsonPropertyName("source")]
        public int Source { get; set; }
    }

    public sealed class GltfImage
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;
    }

    public sealed class GltfAccessor
    {
        [JsonPropertyName("bufferView")]
        public int BufferView { get; set; }

        [JsonPropertyName("componentType")]
        public int ComponentType { get; set; }

        [JsonPropertyName("count")]
        public int Count { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = "VEC3";

        [JsonPropertyName("min")]
        public float[]? Min { get; set; }

        [JsonPropertyName("max")]
        public float[]? Max { get; set; }
    }

    public sealed class GltfBufferView
    {
        [JsonPropertyName("buffer")]
        public int Buffer { get; set; }

        [JsonPropertyName("byteOffset")]
        public int ByteOffset { get; set; }

        [JsonPropertyName("byteLength")]
        public int ByteLength { get; set; }

        [JsonPropertyName("target")]
        public int Target { get; set; }
    }

    public sealed class GltfBuffer
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("byteLength")]
        public int ByteLength { get; set; }
    }
    #endregion
}
