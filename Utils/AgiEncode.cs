using System;
using System.Buffers;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using SDColor = System.Drawing.Color;
using SDRectangle = System.Drawing.Rectangle;

namespace GalaxyAngel2Localization.Utils
{
    internal static class AgiEncoder
    {
        public static bool EncodePngToAgi(string pngPath, string agiOutputPath, out string? error)
        {
            error = null;
            if (!EncodePngToAgiBytes(pngPath, out var agi, out error))
                return false;

            try
            {
                var dir = Path.GetDirectoryName(agiOutputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllBytes(agiOutputPath, agi);
                return true;
            }
            catch (Exception ex)
            {
                error = "写入 AGI 失败: " + ex.Message;
                return false;
            }
        }

        public static bool EncodePngToAgiBytes(string pngPath, out byte[] agiData, out string? error)
        {
            agiData = Array.Empty<byte>();
            error = null;

            if (!File.Exists(pngPath))
            {
                error = "找不到 PNG 文件: " + pngPath;
                return false;
            }

            using var bmpSrc = new Bitmap(pngPath);
            int width = bmpSrc.Width;
            int height = bmpSrc.Height;

            Bitmap? bmp32 = null;
            Bitmap bmp;
            if (bmpSrc.PixelFormat == PixelFormat.Format32bppArgb)
            {
                bmp = bmpSrc;
            }
            else
            {
                bmp32 = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp32))
                    g.DrawImage(bmpSrc, 0, 0, width, height);
                bmp = bmp32;
            }

            var rect = new SDRectangle(0, 0, width, height);
            BitmapData? bd = null;
            byte[]? src = null;

            try
            {
                bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
                int stride = bd.Stride;
                int srcLen = stride * height;
                src = ArrayPool<byte>.Shared.Rent(srcLen);
                Marshal.Copy(bd.Scan0, src, 0, srcLen);

                bool hasSemiTransparent = false;
                HashSet<int>? distinctColors = new HashSet<int>();

                for (int y = 0; y < height; y++)
                {
                    int rowBase = y * stride;
                    for (int x = 0; x < width; x++)
                    {
                        int o = rowBase + (x << 2);
                        byte b = src[o];
                        byte g = src[o + 1];
                        byte r = src[o + 2];
                        byte a = src[o + 3];

                        if (a > 0 && a < 255)
                            hasSemiTransparent = true;

                        if (distinctColors != null)
                        {
                            int argb = (a << 24) | (r << 16) | (g << 8) | b;
                            distinctColors.Add(argb);
                            if (distinctColors.Count > 256)
                                distinctColors = null;
                        }
                    }
                }

                if (distinctColors != null)
                {
                    BuildExact8bpp(src, width, height, stride, out var indices, out var palette);
                    agiData = Encode8bpp(indices, palette, width, height);
                    return true;
                }

                if (!hasSemiTransparent)
                {
                    agiData = Encode16bpp(src, width, height, stride);
                    return true;
                }

                QuantizeTo8bppWu(pngPath, out var qIndices, out var qPalette, width, height);
                agiData = Encode8bpp(qIndices, qPalette, width, height);
                return true;
            }
            finally
            {
                if (bd != null)
                    bmp.UnlockBits(bd);
                if (src != null)
                    ArrayPool<byte>.Shared.Return(src);
                bmp32?.Dispose();
            }
        }

        static void BuildExact8bpp(byte[] src, int width, int height, int stride, out byte[] indices, out List<SDColor> palette)
        {
            indices = new byte[width * height];
            palette = new List<SDColor>(256);
            var map = new Dictionary<int, byte>(256);

            int idx = 0;
            for (int y = 0; y < height; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < width; x++, idx++)
                {
                    int o = rowBase + (x << 2);
                    byte b = src[o];
                    byte g = src[o + 1];
                    byte r = src[o + 2];
                    byte a = src[o + 3];

                    int argb = (a << 24) | (r << 16) | (g << 8) | b;
                    if (!map.TryGetValue(argb, out var pi))
                    {
                        pi = (byte)palette.Count;
                        palette.Add(SDColor.FromArgb(a, r, g, b));
                        map.Add(argb, pi);
                    }

                    indices[idx] = pi;
                }
            }

            while (palette.Count < 256)
                palette.Add(SDColor.FromArgb(0, 0, 0, 0));
        }

        static void QuantizeTo8bppWu(string pngPath, out byte[] indices, out List<SDColor> palette, int expectedWidth, int expectedHeight)
        {
            using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(pngPath);
            var quantizer = new WuQuantizer(new QuantizerOptions { MaxColors = 256, Dither = null });
            image.Mutate(x => x.Quantize(quantizer));

            int width = image.Width;
            int height = image.Height;
            if (width != expectedWidth || height != expectedHeight)
                throw new InvalidOperationException("图像尺寸不一致");

            var pixels = new Rgba32[width * height];
            image.CopyPixelDataTo(pixels);

            indices = new byte[width * height];
            palette = new List<SDColor>(256);
            var map = new Dictionary<uint, byte>(256);

            for (int i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                uint key = ((uint)p.A << 24) | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                if (!map.TryGetValue(key, out var pi))
                {
                    pi = (byte)palette.Count;
                    palette.Add(SDColor.FromArgb(p.A, p.R, p.G, p.B));
                    map.Add(key, pi);
                }
                indices[i] = pi;
            }

            while (palette.Count < 256)
                palette.Add(SDColor.FromArgb(0, 0, 0, 0));
        }

        static byte[] Encode8bpp(byte[] indices, List<SDColor> palette, int width, int height)
        {
            byte[] palData = EncodePalette(palette);

            int pixelOffset = 0x30;
            int clutOffset = pixelOffset + indices.Length;
            var data = new byte[clutOffset + palData.Length];

            WriteUInt32(data, 0x00, 0x20);
            WriteUInt16(data, 0x04, 1);
            WriteUInt16(data, 0x06, 1);
            WriteUInt32(data, 0x08, (uint)pixelOffset);
            WriteUInt16(data, 0x0E, 0x13);
            WriteUInt16(data, 0x10, 0x04);
            WriteUInt16(data, 0x12, (ushort)height);
            WriteUInt16(data, 0x18, (ushort)width);
            WriteUInt16(data, 0x1A, (ushort)height);
            WriteUInt32(data, 0x1C, (uint)clutOffset);
            WriteUInt32(data, 0x24, 1);
            WriteUInt16(data, 0x2C, 0x10);
            WriteUInt16(data, 0x2E, 0x10);

            Buffer.BlockCopy(indices, 0, data, pixelOffset, indices.Length);
            Buffer.BlockCopy(palData, 0, data, clutOffset, palData.Length);

            return data;
        }

        static byte[] Encode16bpp(byte[] src, int width, int height, int stride)
        {
            int pixelOffset = 0x20;
            var data = new byte[pixelOffset + width * height * 2];

            WriteUInt32(data, 0x00, 0x20);
            WriteUInt16(data, 0x04, 1);
            WriteUInt16(data, 0x06, 0);
            WriteUInt32(data, 0x08, (uint)pixelOffset);
            WriteUInt16(data, 0x0E, 0x02);
            WriteUInt16(data, 0x10, 0x0A);
            WriteUInt16(data, 0x12, (ushort)height);
            WriteUInt16(data, 0x18, (ushort)width);
            WriteUInt16(data, 0x1A, (ushort)height);

            int offset = pixelOffset;

            for (int y = 0; y < height; y++)
            {
                int rowBase = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int o = rowBase + (x << 2);
                    byte b = src[o];
                    byte g = src[o + 1];
                    byte r = src[o + 2];
                    byte a = src[o + 3];

                    int r5 = (r >> 3) & 0x1F;
                    int g5 = (g >> 3) & 0x1F;
                    int b5 = (b >> 3) & 0x1F;
                    int a1 = a >= 128 ? 1 : 0;

                    ushort pixel = (ushort)(r5 | (g5 << 5) | (b5 << 10) | (a1 << 15));
                    data[offset++] = (byte)pixel;
                    data[offset++] = (byte)(pixel >> 8);
                }
            }

            return data;
        }

        static byte[] EncodePalette(List<SDColor> palette)
        {
            var palData = new byte[1024];

            for (int major = 0; major < 256; major += 32)
            {
                for (int i = 0; i < 8; i++)
                    WritePaletteColor(palData, major + i, palette[major + i]);
                for (int i = 0; i < 8; i++)
                    WritePaletteColor(palData, major + 8 + i, palette[major + 16 + i]);
                for (int i = 0; i < 8; i++)
                    WritePaletteColor(palData, major + 16 + i, palette[major + 8 + i]);
                for (int i = 0; i < 8; i++)
                    WritePaletteColor(palData, major + 24 + i, palette[major + 24 + i]);
            }

            return palData;
        }

        static void WritePaletteColor(byte[] palData, int index, SDColor c)
        {
            int p = index * 4;
            palData[p] = c.R;
            palData[p + 1] = c.G;
            palData[p + 2] = c.B;
            palData[p + 3] = c.A == 0 ? (byte)0 : (byte)((c.A + 1) / 2);
        }

        static void WriteUInt16(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }

        static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }
    }
}