using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Utils;

namespace GalaxyAngel2Localization.Utils
{
    internal static class TexDecoder
    {
        public static bool DecodeTexToPng(
            byte[] data,
            string outputPath,
            string relativePath,
            out TexFileMetadata? metadata,
            out string? error)
        {
            metadata = null;
            error = null;
            if (!TryDecode(data, relativePath, out var bitmap, out metadata, out error))
                return false;

            Bitmap decodedBitmap = bitmap!;
            using (decodedBitmap)
            {
                try
                {
                    var dir = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    decodedBitmap.Save(outputPath, ImageFormat.Png);
                    return true;
                }
                catch (Exception ex)
                {
                    error = "Unable to write TEX PNG: " + ex.Message;
                    metadata = null;
                    return false;
                }
            }
        }

        public static bool TryDecode(
            byte[] data,
            string relativePath,
            out Bitmap? bitmap,
            out TexFileMetadata? metadata,
            out string? error)
        {
            bitmap = null;
            if (!TexStructure.TryParse(data, relativePath, out metadata, out error) || metadata == null)
                return false;

            try
            {
                int width = checked((int)metadata.RealWidth);
                int height = checked((int)metadata.RealHeight);
                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                    throw new InvalidDataException("Invalid TEX canvas dimensions.");

                byte[]? palette8 = null;
                byte[]? palette4 = null;
                bool uses8 = false;
                bool uses4 = false;
                foreach (var sprite in metadata.Sprites)
                {
                    ValidatePsm(sprite.Psm);
                    uses8 |= sprite.Psm == 0x13;
                    uses4 |= sprite.Psm == 0x14;
                    ValidateDataRange(data.Length, metadata.BaseOffset, sprite);
                }

                if (uses8 || uses4)
                {
                    var clut = metadata.Clut ?? throw new InvalidDataException("Indexed TEX has no CLUT entry.");
                    int colorCount = uses8 ? 256 : 16;
                    int declaredColors = checked(clut.Width * clut.Height);
                    if (declaredColors != colorCount)
                        throw new InvalidDataException($"Expected a {colorCount}-color CLUT but metadata declares {declaredColors} colors.");
                    int paletteOffset = GetAbsoluteOffset(metadata.BaseOffset, clut.RelativeOffset);
                    int paletteLength = checked(colorCount * 4);
                    EnsureRange(data.Length, paletteOffset, paletteLength, "CLUT");
                    byte[] rawPalette = data.AsSpan(paletteOffset, paletteLength).ToArray();
                    if (uses8)
                        palette8 = ImageUtils.BuildPs2Palette256Bgra_Block32(rawPalette);
                    if (uses4)
                        palette4 = ImageUtils.BuildPaletteBgraFromRgba(rawPalette, 16, true);
                }

                bitmap = DecodeSprites(data, metadata, width, height, palette8, palette4);
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentException)
            {
                bitmap?.Dispose();
                bitmap = null;
                metadata = null;
                error = ex.Message;
                return false;
            }
        }

        static Bitmap DecodeSprites(
            byte[] data,
            TexFileMetadata metadata,
            int width,
            int height,
            byte[]? palette8,
            byte[]? palette4)
        {
            var bitmap = ImageUtils.CreateArgbBitmap(width, height, out var bitmapData, out int stride);
            var clearRow = new byte[width * 4];
            for (int y = 0; y < height; y++)
                ImageUtils.CopyRowToBitmap(bitmapData, y, clearRow, stride);

            int currentY = 0;
            try
            {
                foreach (var sprite in metadata.Sprites)
                {
                    int sourceOffset = GetAbsoluteOffset(metadata.BaseOffset, sprite.RelativeOffset);
                    int rowSize = GetRowSize(sprite.Psm, sprite.Width);
                    int copyWidth = Math.Min(sprite.Width, width);
                    var decodedRow = new byte[copyWidth * 4];

                    for (int y = 0; y < sprite.Height; y++)
                    {
                        int destinationY = currentY + y;
                        if (destinationY >= height)
                            break;

                        byte[] row = data.AsSpan(sourceOffset + y * rowSize, rowSize).ToArray();
                        switch (sprite.Psm)
                        {
                            case 0x00: ImageUtils.ConvertRowRgba32ToBgraWithPs2Alpha(row, decodedRow, copyWidth); break;
                            case 0x01: ImageUtils.ConvertRowRgb24ToBgra(row, decodedRow, copyWidth); break;
                            case 0x02: ImageUtils.ConvertRowRgb555ToBgra(row, decodedRow, copyWidth); break;
                            case 0x13: ImageUtils.ConvertRowIndexed8ToBgra(row, decodedRow, copyWidth, palette8!); break;
                            case 0x14: ImageUtils.ConvertRowIndexed4ToBgra(row, decodedRow, copyWidth, palette4!); break;
                        }
                        ImageUtils.CopyRowToBitmap(bitmapData, destinationY, decodedRow, stride);
                    }
                    currentY = checked(currentY + sprite.Height);
                }
            }
            catch
            {
                ImageUtils.UnlockBitmap(bitmapData, bitmap);
                bitmap.Dispose();
                throw;
            }

            ImageUtils.UnlockBitmap(bitmapData, bitmap);
            return bitmap;
        }

        internal static int GetRowSize(byte psm, int width) => psm switch
        {
            0x00 => checked(width * 4),
            0x01 => checked(width * 3),
            0x02 => checked(width * 2),
            0x13 => width,
            0x14 => checked((width + 1) / 2),
            _ => throw new InvalidDataException($"Unsupported TEX PSM 0x{psm:X2}.")
        };

        internal static int GetAbsoluteOffset(uint baseOffset, uint relativeOffset)
        {
            long value = (long)baseOffset + relativeOffset;
            if (value < 0 || value > int.MaxValue)
                throw new InvalidDataException("TEX data offset is outside the supported range.");
            return (int)value;
        }

        internal static void EnsureRange(int fileLength, int offset, int length, string name)
        {
            if (offset < 0 || length < 0 || (long)offset + length > fileLength)
                throw new InvalidDataException($"{name} data extends beyond the TEX file.");
        }

        static void ValidateDataRange(int fileLength, uint baseOffset, TexEntryMetadata entry)
        {
            int offset = GetAbsoluteOffset(baseOffset, entry.RelativeOffset);
            int length = checked(GetRowSize(entry.Psm, entry.Width) * entry.Height);
            EnsureRange(fileLength, offset, length, "Sprite");
        }

        static void ValidatePsm(byte psm) => _ = GetRowSize(psm, 0);
    }

    internal static class TexEncoder
    {
        public static bool EncodePngToTexBytes(
            string pngPath,
            TexFileMetadata metadata,
            out byte[] texData,
            out string? error)
        {
            texData = Array.Empty<byte>();
            error = null;
            try
            {
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(pngPath);
                if ((uint)image.Width != metadata.RealWidth || (uint)image.Height != metadata.RealHeight)
                    throw new InvalidDataException(
                        $"PNG dimensions {image.Width}x{image.Height} do not match tex.xml {metadata.RealWidth}x{metadata.RealHeight}.");

                ValidateMetadata(metadata);
                var sourcePixels = new Rgba32[checked(image.Width * image.Height)];
                image.CopyPixelDataTo(sourcePixels);

                texData = new byte[metadata.FileLength];
                WriteHeader(texData, metadata);

                IndexedPixels? indexed = BuildIndexedPixels(sourcePixels, image.Width, image.Height, metadata);
                WriteSprites(texData, sourcePixels, image.Width, image.Height, metadata, indexed);
                if (indexed != null)
                    WritePalette(texData, metadata, indexed);
                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException)
            {
                error = ex.Message;
                texData = Array.Empty<byte>();
                return false;
            }
        }

        sealed class IndexedPixels
        {
            public required byte[] Indices { get; init; }
            public required List<Rgba32> Palette { get; init; }
            public required int Width { get; init; }
            public required int ColorCount { get; init; }
        }

        static IndexedPixels? BuildIndexedPixels(
            Rgba32[] source,
            int sourceWidth,
            int sourceHeight,
            TexFileMetadata metadata)
        {
            bool uses8 = false;
            bool uses4 = false;
            int virtualWidth = 0;
            int virtualHeight = 0;
            foreach (var sprite in metadata.Sprites)
            {
                uses8 |= sprite.Psm == 0x13;
                uses4 |= sprite.Psm == 0x14;
                virtualWidth = Math.Max(virtualWidth, sprite.Width);
                virtualHeight = checked(virtualHeight + sprite.Height);
            }
            if (!uses8 && !uses4)
                return null;

            int colorCount = uses4 ? 16 : 256;
            var clut = metadata.Clut ?? throw new InvalidDataException("Indexed TEX has no CLUT metadata.");
            if (checked(clut.Width * clut.Height) != colorCount)
                throw new InvalidDataException($"Expected a {colorCount}-color CLUT in tex.xml.");

            var virtualPixels = new Rgba32[checked(virtualWidth * virtualHeight)];
            int spriteY = 0;
            foreach (var sprite in metadata.Sprites)
            {
                for (int y = 0; y < sprite.Height; y++)
                {
                    int sourceY = spriteY + y;
                    if (sourceY >= sourceHeight)
                        break;
                    int copyWidth = Math.Min(sprite.Width, sourceWidth);
                    Array.Copy(source, sourceY * sourceWidth, virtualPixels, (spriteY + y) * virtualWidth, copyWidth);
                }
                spriteY += sprite.Height;
            }

            Quantize(virtualPixels, virtualWidth, virtualHeight, colorCount, out var indices, out var palette);
            return new IndexedPixels { Indices = indices, Palette = palette, Width = virtualWidth, ColorCount = colorCount };
        }

        static void Quantize(
            Rgba32[] pixels,
            int width,
            int height,
            int maxColors,
            out byte[] indices,
            out List<Rgba32> palette)
        {
            if (TryBuildExactPalette(pixels, maxColors, out indices, out palette))
                return;

            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(pixels, width, height);
            image.Mutate(x => x.Quantize(new WuQuantizer(new QuantizerOptions
            {
                MaxColors = maxColors,
                Dither = null
            })));
            var quantized = new Rgba32[pixels.Length];
            image.CopyPixelDataTo(quantized);
            if (!TryBuildExactPalette(quantized, maxColors, out indices, out palette))
                throw new InvalidDataException("TEX palette quantization produced too many colors.");
        }

        static bool TryBuildExactPalette(
            Rgba32[] pixels,
            int maxColors,
            out byte[] indices,
            out List<Rgba32> palette)
        {
            indices = new byte[pixels.Length];
            palette = new List<Rgba32>(maxColors);
            var map = new Dictionary<uint, byte>(maxColors);
            for (int i = 0; i < pixels.Length; i++)
            {
                Rgba32 color = pixels[i];
                uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
                if (!map.TryGetValue(key, out byte index))
                {
                    if (palette.Count == maxColors)
                        return false;
                    index = (byte)palette.Count;
                    map.Add(key, index);
                    palette.Add(color);
                }
                indices[i] = index;
            }
            while (palette.Count < maxColors)
                palette.Add(default);
            return true;
        }

        static void WriteHeader(byte[] output, TexFileMetadata metadata)
        {
            output[0] = (byte)'T';
            output[1] = (byte)'E';
            output[2] = (byte)'X';
            output[3] = (byte)' ';
            WriteUInt16(output, 0x04, metadata.Cw);
            WriteUInt16(output, 0x06, metadata.Ch);
            metadata.Unknown1.CopyTo(output, 0x08);
            WriteUInt32(output, 0x14, metadata.RealWidth);
            WriteUInt32(output, 0x18, metadata.RealHeight);
            metadata.Unknown2.CopyTo(output, 0x1C);
            WriteUInt32(output, 0x20, metadata.BaseOffset);
            WriteUInt16(output, 0x24, checked((ushort)metadata.Sprites.Count));
            WriteUInt16(output, 0x26, metadata.HasClut);

            for (int i = 0; i < metadata.Sprites.Count; i++)
                WriteEntry(output, 0x28 + i * 20, metadata.Sprites[i]);
            if (metadata.Clut != null)
                WriteEntry(output, 0x28 + metadata.Sprites.Count * 20, metadata.Clut);
        }

        static void WriteSprites(
            byte[] output,
            Rgba32[] source,
            int sourceWidth,
            int sourceHeight,
            TexFileMetadata metadata,
            IndexedPixels? indexed)
        {
            int spriteY = 0;
            foreach (var sprite in metadata.Sprites)
            {
                int destination = TexDecoder.GetAbsoluteOffset(metadata.BaseOffset, sprite.RelativeOffset);
                int rowSize = TexDecoder.GetRowSize(sprite.Psm, sprite.Width);
                TexDecoder.EnsureRange(output.Length, destination, checked(rowSize * sprite.Height), "Sprite");

                for (int y = 0; y < sprite.Height; y++)
                {
                    int rowOffset = destination + y * rowSize;
                    for (int x = 0; x < sprite.Width; x++)
                    {
                        int sourceY = spriteY + y;
                        Rgba32 color = x < sourceWidth && sourceY < sourceHeight
                            ? source[sourceY * sourceWidth + x]
                            : default;

                        switch (sprite.Psm)
                        {
                            case 0x00:
                                int p32 = rowOffset + x * 4;
                                output[p32] = color.R;
                                output[p32 + 1] = color.G;
                                output[p32 + 2] = color.B;
                                output[p32 + 3] = EncodePs2Alpha(color.A);
                                break;
                            case 0x01:
                                int p24 = rowOffset + x * 3;
                                output[p24] = color.R;
                                output[p24 + 1] = color.G;
                                output[p24 + 2] = color.B;
                                break;
                            case 0x02:
                                ushort rgb555 = (ushort)((color.R >> 3) | ((color.G >> 3) << 5) | ((color.B >> 3) << 10));
                                WriteUInt16(output, rowOffset + x * 2, rgb555);
                                break;
                            case 0x13:
                                output[rowOffset + x] = indexed!.Indices[(spriteY + y) * indexed.Width + x];
                                break;
                            case 0x14:
                                byte index = indexed!.Indices[(spriteY + y) * indexed.Width + x];
                                int packedOffset = rowOffset + x / 2;
                                if ((x & 1) == 0)
                                    output[packedOffset] = index;
                                else
                                    output[packedOffset] |= (byte)(index << 4);
                                break;
                            default:
                                throw new InvalidDataException($"Unsupported TEX PSM 0x{sprite.Psm:X2}.");
                        }
                    }
                }
                spriteY = checked(spriteY + sprite.Height);
            }
        }

        static void WritePalette(byte[] output, TexFileMetadata metadata, IndexedPixels indexed)
        {
            var clut = metadata.Clut!;
            int offset = TexDecoder.GetAbsoluteOffset(metadata.BaseOffset, clut.RelativeOffset);
            int length = checked(indexed.ColorCount * 4);
            TexDecoder.EnsureRange(output.Length, offset, length, "CLUT");

            for (int i = 0; i < indexed.ColorCount; i++)
            {
                int storedIndex = indexed.ColorCount == 256
                    ? (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)
                    : i;
                Rgba32 color = indexed.Palette[i];
                int p = offset + storedIndex * 4;
                output[p] = color.R;
                output[p + 1] = color.G;
                output[p + 2] = color.B;
                output[p + 3] = EncodePs2Alpha(color.A);
            }
        }

        static void ValidateMetadata(TexFileMetadata metadata)
        {
            if (metadata.FileLength < 0x28 || metadata.Unknown1.Length != 12 || metadata.Unknown2.Length != 4)
                throw new InvalidDataException("Invalid TEX header metadata.");
            if (metadata.RealWidth == 0 || metadata.RealHeight == 0 || metadata.Sprites.Count == 0)
                throw new InvalidDataException("Invalid TEX dimensions or sprite count in tex.xml.");

            int tableLength = checked(0x28 + (metadata.Sprites.Count + (metadata.HasClut == 0 ? 0 : 1)) * 20);
            if (tableLength > metadata.FileLength)
                throw new InvalidDataException("TEX entry table extends beyond the recorded file length.");
            if ((metadata.HasClut != 0) != (metadata.Clut != null))
                throw new InvalidDataException("CLUT presence does not match hasClut in tex.xml.");

            foreach (var sprite in metadata.Sprites)
            {
                _ = TexDecoder.GetRowSize(sprite.Psm, sprite.Width);
                int offset = TexDecoder.GetAbsoluteOffset(metadata.BaseOffset, sprite.RelativeOffset);
                int length = checked(TexDecoder.GetRowSize(sprite.Psm, sprite.Width) * sprite.Height);
                TexDecoder.EnsureRange(metadata.FileLength, offset, length, "Sprite");
            }
        }

        static void WriteEntry(byte[] output, int offset, TexEntryMetadata entry)
        {
            WriteUInt32(output, offset, entry.RelativeOffset);
            WriteUInt16(output, offset + 4, entry.Tbp);
            output[offset + 6] = entry.Psm;
            output[offset + 7] = entry.Unknown;
            WriteUInt16(output, offset + 8, entry.Tbw);
            WriteUInt16(output, offset + 10, entry.SharedVram);
            WriteUInt16(output, offset + 12, entry.OffsetX);
            WriteUInt16(output, offset + 14, entry.OffsetY);
            WriteUInt16(output, offset + 16, entry.Width);
            WriteUInt16(output, offset + 18, entry.Height);
        }

        static byte EncodePs2Alpha(byte alpha) => alpha == 0 ? (byte)0 : (byte)((alpha + 1) / 2);

        static void WriteUInt16(byte[] output, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(offset, 2), value);

        static void WriteUInt32(byte[] output, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset, 4), value);
    }
}
