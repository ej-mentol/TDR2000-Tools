namespace TDR.PakLib
{
    public sealed class FileEntry
    {
        public string Name { get; set; } = string.Empty;
        public uint Offset { get; set; }
        public uint Size { get; set; }
    }
}
