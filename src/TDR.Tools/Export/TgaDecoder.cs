using System;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TDR.Tools.Export
{
    public static class TgaDecoder
    {
        public static Bitmap? DecodeTga(byte[] tgaBytes)
        {
            if (tgaBytes == null || tgaBytes.Length < 18) return null;

            try
            {
                using var ms = new MemoryStream(tgaBytes);
                using var reader = new BinaryReader(ms);

                byte idLength = reader.ReadByte();
                byte colorMapType = reader.ReadByte();
                byte imageType = reader.ReadByte();

                // Color Map Specification (5 bytes)
                ushort colorMapFirstEntry = reader.ReadUInt16();
                ushort colorMapLength = reader.ReadUInt16();
                byte colorMapEntrySize = reader.ReadByte(); // Size in bits (15, 16, 24, 32)

                short xOrigin = reader.ReadInt16();
                short yOrigin = reader.ReadInt16();
                ushort width = reader.ReadUInt16();
                ushort height = reader.ReadUInt16();
                byte bpp = reader.ReadByte();
                byte descriptor = reader.ReadByte();

                if (width == 0 || height == 0 || width > 8192 || height > 8192) return null;

                // Read Image ID if present
                if (idLength > 0 && ms.Position + idLength <= ms.Length)
                {
                    ms.Seek(idLength, SeekOrigin.Current);
                }

                // Read Color Map (Palette) if present
                byte[]? palette = null;
                if (colorMapType == 1 && colorMapLength > 0)
                {
                    int bytesPerColorMapEntry = (colorMapEntrySize + 7) / 8;
                    palette = new byte[colorMapLength * 4];
                    for (int i = 0; i < colorMapLength; i++)
                    {
                        if (bytesPerColorMapEntry == 3)
                        {
                            byte b = reader.ReadByte();
                            byte g = reader.ReadByte();
                            byte r = reader.ReadByte();
                            palette[i * 4] = b;
                            palette[i * 4 + 1] = g;
                            palette[i * 4 + 2] = r;
                            palette[i * 4 + 3] = 255;
                        }
                        else if (bytesPerColorMapEntry == 4)
                        {
                            byte b = reader.ReadByte();
                            byte g = reader.ReadByte();
                            byte r = reader.ReadByte();
                            byte a = reader.ReadByte();
                            palette[i * 4] = b;
                            palette[i * 4 + 1] = g;
                            palette[i * 4 + 2] = r;
                            palette[i * 4 + 3] = a;
                        }
                        else if (bytesPerColorMapEntry == 2)
                        {
                            ushort val = reader.ReadUInt16();
                            byte r = (byte)(((val & 0x7C00) >> 10) << 3);
                            byte g = (byte)(((val & 0x03E0) >> 5) << 3);
                            byte b = (byte)((val & 0x001F) << 3);
                            palette[i * 4] = b;
                            palette[i * 4 + 1] = g;
                            palette[i * 4 + 2] = r;
                            palette[i * 4 + 3] = 255;
                        }
                    }
                }

                long totalBytes = (long)width * height * 4;
                if (totalBytes <= 0 || totalBytes > 64 * 1024 * 1024) return null;

                byte[] rawPixels = new byte[totalBytes];
                int totalPixels = width * height;
                int currentPixel = 0;

                bool isRle = (imageType == 9 || imageType == 10 || imageType == 11);
                bool isColorMapped = (imageType == 1 || imageType == 9);
                bool isGrayscale = (imageType == 3 || imageType == 11);
                bool isTrueColor = (imageType == 2 || imageType == 10);

                if (!isColorMapped && !isGrayscale && !isTrueColor) return null;

                int bytesPerPixel = (bpp + 7) / 8;

                Action<int> readAndWritePixel = (dstIdx) =>
                {
                    if (isColorMapped && palette != null)
                    {
                        int index = (bpp == 8) ? reader.ReadByte() : reader.ReadUInt16();
                        if (index < colorMapLength)
                        {
                            rawPixels[dstIdx] = palette[index * 4];
                            rawPixels[dstIdx + 1] = palette[index * 4 + 1];
                            rawPixels[dstIdx + 2] = palette[index * 4 + 2];
                            rawPixels[dstIdx + 3] = palette[index * 4 + 3];
                        }
                    }
                    else if (isGrayscale)
                    {
                        byte gray = reader.ReadByte();
                        rawPixels[dstIdx] = gray;
                        rawPixels[dstIdx + 1] = gray;
                        rawPixels[dstIdx + 2] = gray;
                        rawPixels[dstIdx + 3] = (bpp == 16) ? reader.ReadByte() : (byte)255;
                    }
                    else if (isTrueColor)
                    {
                        if (bytesPerPixel == 3 || bytesPerPixel == 4)
                        {
                            byte b = reader.ReadByte();
                            byte g = reader.ReadByte();
                            byte r = reader.ReadByte();
                            byte a = (bytesPerPixel == 4) ? reader.ReadByte() : (byte)255;
                            rawPixels[dstIdx] = b;
                            rawPixels[dstIdx + 1] = g;
                            rawPixels[dstIdx + 2] = r;
                            rawPixels[dstIdx + 3] = a;
                        }
                        else if (bytesPerPixel == 2)
                        {
                            ushort val = reader.ReadUInt16();
                            byte r = (byte)(((val & 0x7C00) >> 10) << 3);
                            byte g = (byte)(((val & 0x03E0) >> 5) << 3);
                            byte b = (byte)((val & 0x001F) << 3);
                            rawPixels[dstIdx] = b;
                            rawPixels[dstIdx + 1] = g;
                            rawPixels[dstIdx + 2] = r;
                            rawPixels[dstIdx + 3] = 255;
                        }
                    }
                };

                if (!isRle)
                {
                    while (currentPixel < totalPixels && ms.Position < ms.Length)
                    {
                        readAndWritePixel(currentPixel * 4);
                        currentPixel++;
                    }
                }
                else
                {
                    while (currentPixel < totalPixels && ms.Position < ms.Length)
                    {
                        byte packetHeader = reader.ReadByte();
                        int count = (packetHeader & 0x7F) + 1;

                        if ((packetHeader & 0x80) != 0)
                        {
                            // RLE packet
                            int pixelStart = currentPixel * 4;
                            readAndWritePixel(pixelStart);
                            byte b = rawPixels[pixelStart];
                            byte g = rawPixels[pixelStart + 1];
                            byte r = rawPixels[pixelStart + 2];
                            byte a = rawPixels[pixelStart + 3];
                            currentPixel++;

                            for (int i = 1; i < count && currentPixel < totalPixels; i++)
                            {
                                int dstIdx = currentPixel * 4;
                                rawPixels[dstIdx] = b;
                                rawPixels[dstIdx + 1] = g;
                                rawPixels[dstIdx + 2] = r;
                                rawPixels[dstIdx + 3] = a;
                                currentPixel++;
                            }
                        }
                        else
                        {
                            // Raw packet
                            for (int i = 0; i < count && currentPixel < totalPixels; i++)
                            {
                                readAndWritePixel(currentPixel * 4);
                                currentPixel++;
                            }
                        }
                    }
                }

                // Check origin orientation (top-to-bottom vs bottom-to-top)
                bool topToBottom = (descriptor & 0x20) != 0;
                if (!topToBottom)
                {
                    byte[] flipped = new byte[rawPixels.Length];
                    int rowBytes = width * 4;
                    for (int y = 0; y < height; y++)
                    {
                        int srcRow = (height - 1 - y) * rowBytes;
                        int dstRow = y * rowBytes;
                        Array.Copy(rawPixels, srcRow, flipped, dstRow, rowBytes);
                    }
                    rawPixels = flipped;
                }

                var writeable = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                using (var frameBuffer = writeable.Lock())
                {
                    int minRowBytes = Math.Min(width * 4, frameBuffer.RowBytes);
                    for (int y = 0; y < height; y++)
                    {
                        IntPtr destRowPtr = frameBuffer.Address + (y * frameBuffer.RowBytes);
                        int srcOffset = y * width * 4;
                        System.Runtime.InteropServices.Marshal.Copy(rawPixels, srcOffset, destRowPtr, minRowBytes);
                    }
                }

                return writeable;
            }
            catch
            {
                return null;
            }
        }

        public static bool SaveTgaAsPng(byte[] tgaBytes, string pngPath)
        {
            if (tgaBytes == null || tgaBytes.Length < 18) return false;

            try
            {
                using var ms = new MemoryStream(tgaBytes);
                using var reader = new BinaryReader(ms);

                byte idLength = reader.ReadByte();
                byte colorMapType = reader.ReadByte();
                byte imageType = reader.ReadByte();

                ushort colorMapFirstEntry = reader.ReadUInt16();
                ushort colorMapLength = reader.ReadUInt16();
                byte colorMapEntrySize = reader.ReadByte();

                short xOrigin = reader.ReadInt16();
                short yOrigin = reader.ReadInt16();
                ushort width = reader.ReadUInt16();
                ushort height = reader.ReadUInt16();
                byte bpp = reader.ReadByte();
                byte descriptor = reader.ReadByte();

                if (width == 0 || height == 0 || width > 8192 || height > 8192) return false;

                if (idLength > 0 && ms.Position + idLength <= ms.Length)
                {
                    ms.Seek(idLength, SeekOrigin.Current);
                }

                byte[]? palette = null;
                if (colorMapType == 1 && colorMapLength > 0)
                {
                    int bytesPerColorMapEntry = (colorMapEntrySize + 7) / 8;
                    palette = new byte[colorMapLength * 4];
                    for (int i = 0; i < colorMapLength; i++)
                    {
                        if (bytesPerColorMapEntry == 3)
                        {
                            byte b = reader.ReadByte();
                            byte g = reader.ReadByte();
                            byte r = reader.ReadByte();
                            palette[i * 4] = b;
                            palette[i * 4 + 1] = g;
                            palette[i * 4 + 2] = r;
                            palette[i * 4 + 3] = 255;
                        }
                        else if (bytesPerColorMapEntry == 4)
                        {
                            byte b = reader.ReadByte();
                            byte g = reader.ReadByte();
                            byte r = reader.ReadByte();
                            byte a = reader.ReadByte();
                            palette[i * 4] = b;
                            palette[i * 4 + 1] = g;
                            palette[i * 4 + 2] = r;
                            palette[i * 4 + 3] = a;
                        }
                        else if (bytesPerColorMapEntry == 2)
                        {
                            ushort val = reader.ReadUInt16();
                            byte r = (byte)(((val & 0x7C00) >> 10) << 3);
                            byte g = (byte)(((val & 0x03E0) >> 5) << 3);
                            byte b = (byte)((val & 0x001F) << 3);
                            palette[i * 4] = b;
                            palette[i * 4 + 1] = g;
                            palette[i * 4 + 2] = r;
                            palette[i * 4 + 3] = 255;
                        }
                    }
                }

                long totalBytes = (long)width * height * 4;
                if (totalBytes <= 0 || totalBytes > 64 * 1024 * 1024) return false;

                byte[] rawPixels = new byte[totalBytes];
                int totalPixels = width * height;
                int currentPixel = 0;

                bool isRle = (imageType == 9 || imageType == 10 || imageType == 11);
                bool isColorMapped = (imageType == 1 || imageType == 9);
                bool isGrayscale = (imageType == 3 || imageType == 11);
                bool isTrueColor = (imageType == 2 || imageType == 10);

                if (!isColorMapped && !isGrayscale && !isTrueColor) return false;

                int bytesPerPixel = (bpp + 7) / 8;

                Action<int> readAndWritePixel = (dstIdx) =>
                {
                    if (isColorMapped && palette != null)
                    {
                        int index = (bpp == 8) ? reader.ReadByte() : reader.ReadUInt16();
                        if (index < colorMapLength)
                        {
                            rawPixels[dstIdx] = palette[index * 4];
                            rawPixels[dstIdx + 1] = palette[index * 4 + 1];
                            rawPixels[dstIdx + 2] = palette[index * 4 + 2];
                            rawPixels[dstIdx + 3] = palette[index * 4 + 3];
                        }
                    }
                    else if (isGrayscale)
                    {
                        byte gray = reader.ReadByte();
                        rawPixels[dstIdx] = gray;
                        rawPixels[dstIdx + 1] = gray;
                        rawPixels[dstIdx + 2] = gray;
                        rawPixels[dstIdx + 3] = (bpp == 16) ? reader.ReadByte() : (byte)255;
                    }
                    else if (isTrueColor)
                    {
                        if (bytesPerPixel == 3 || bytesPerPixel == 4)
                        {
                            byte b = reader.ReadByte();
                            byte g = reader.ReadByte();
                            byte r = reader.ReadByte();
                            byte a = (bytesPerPixel == 4) ? reader.ReadByte() : (byte)255;
                            rawPixels[dstIdx] = b;
                            rawPixels[dstIdx + 1] = g;
                            rawPixels[dstIdx + 2] = r;
                            rawPixels[dstIdx + 3] = a;
                        }
                        else if (bytesPerPixel == 2)
                        {
                            ushort val = reader.ReadUInt16();
                            byte r = (byte)(((val & 0x7C00) >> 10) << 3);
                            byte g = (byte)(((val & 0x03E0) >> 5) << 3);
                            byte b = (byte)((val & 0x001F) << 3);
                            rawPixels[dstIdx] = b;
                            rawPixels[dstIdx + 1] = g;
                            rawPixels[dstIdx + 2] = r;
                            rawPixels[dstIdx + 3] = 255;
                        }
                    }
                };

                if (!isRle)
                {
                    while (currentPixel < totalPixels && ms.Position < ms.Length)
                    {
                        readAndWritePixel(currentPixel * 4);
                        currentPixel++;
                    }
                }
                else
                {
                    while (currentPixel < totalPixels && ms.Position < ms.Length)
                    {
                        byte packetHeader = reader.ReadByte();
                        int count = (packetHeader & 0x7F) + 1;

                        if ((packetHeader & 0x80) != 0)
                        {
                            int pixelStart = currentPixel * 4;
                            readAndWritePixel(pixelStart);
                            byte b = rawPixels[pixelStart];
                            byte g = rawPixels[pixelStart + 1];
                            byte r = rawPixels[pixelStart + 2];
                            byte a = rawPixels[pixelStart + 3];
                            currentPixel++;

                            for (int i = 1; i < count && currentPixel < totalPixels; i++)
                            {
                                int dstIdx = currentPixel * 4;
                                rawPixels[dstIdx] = b;
                                rawPixels[dstIdx + 1] = g;
                                rawPixels[dstIdx + 2] = r;
                                rawPixels[dstIdx + 3] = a;
                                currentPixel++;
                            }
                        }
                        else
                        {
                            for (int i = 0; i < count && currentPixel < totalPixels; i++)
                            {
                                readAndWritePixel(currentPixel * 4);
                                currentPixel++;
                            }
                        }
                    }
                }

                bool topToBottom = (descriptor & 0x20) != 0;
                if (!topToBottom)
                {
                    byte[] flipped = new byte[rawPixels.Length];
                    int rowBytes = width * 4;
                    for (int y = 0; y < height; y++)
                    {
                        int srcRow = (height - 1 - y) * rowBytes;
                        int dstRow = y * rowBytes;
                        Array.Copy(rawPixels, srcRow, flipped, dstRow, rowBytes);
                    }
                    rawPixels = flipped;
                }

                using var skBitmap = new SkiaSharp.SKBitmap();
                var info = new SkiaSharp.SKImageInfo(width, height, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                if (rawPixels.Length < (long)height * info.RowBytes) return false;

                var handle = System.Runtime.InteropServices.GCHandle.Alloc(rawPixels, System.Runtime.InteropServices.GCHandleType.Pinned);
                try
                {
                    skBitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
                    using var image = SkiaSharp.SKImage.FromBitmap(skBitmap);
                    if (image == null) return false;

                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    if (data == null) return false;

                    using var stream = File.Create(pngPath);
                    data.SaveTo(stream);
                    return true;
                }
                finally
                {
                    handle.Free();
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
