using System.Collections.Generic;

namespace TDR.Tools.Export
{
    public sealed record TrackExportResult
    {
        public bool Success { get; set; } = false;
        public string TrackName { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = string.Empty;

        public string? BaseMeshFileName { get; set; }
        public string? WaterMeshFileName { get; set; }
        public string? SkyMeshFileName { get; set; }

        public List<string> ProducedObjFiles { get; set; } = new();
        public List<string> ProducedMtlFiles { get; set; } = new();
        public List<string> ResolvedHieFiles { get; set; } = new();
    }
}
