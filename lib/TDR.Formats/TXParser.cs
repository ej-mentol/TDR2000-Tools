using System;
using System.IO;

namespace TDR.PakLib.Formats
{
    public enum TxTransparencyMode
    {
        Opaque = 0,
        Blend = 1,
        Mask = 2
    }

    public sealed class TxDescriptor
    {
        public string Name { get; set; } = string.Empty;
        public int Version { get; set; }
        public int Levels { get; set; }
        public int Flags { get; set; }

        public TxTransparencyMode TransparencyMode
        {
            get
            {
                if ((Flags & 1) != 0) return TxTransparencyMode.Blend;
                if ((Flags & 2) != 0) return TxTransparencyMode.Mask;
                return TxTransparencyMode.Opaque;
            }
        }

        public bool IsTransparent => TransparencyMode != TxTransparencyMode.Opaque;

        public static TxDescriptor? Load(byte[]? data, string name = "")
        {
            if (data == null || data.Length < 16) return null;

            if (data[0] != 'T' || data[1] != 'T' || data[2] != 'E' || data[3] != 'X')
            {
                return null;
            }

            var tx = new TxDescriptor
            {
                Name = name
            };

            using var ms = new MemoryStream(data);
            using var br = new BinaryReader(ms);

            br.ReadInt32(); // Skip 'TTEX' signature

            tx.Version = br.ReadInt32();
            tx.Levels = br.ReadInt32();
            tx.Flags = br.ReadInt32();

            return tx;
        }
    }
}
