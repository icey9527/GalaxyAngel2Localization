using System;
using System.Collections.Generic;
using System.IO;
using SDColor = System.Drawing.Color;
using Utils;

namespace GalaxyAngel2Localization.Utils
{
    internal static class AgiEncoder
    {
        /// <summary>
        /// 按 agi.xml 元数据重建（仅支持 4/8bpp；无 meta 默认 8bpp）。
        /// 头部由格式常量 + 尺寸/偏移推导生成，meta 里记录的 4 个 VRAM 簿记字段逐字节回填。
        /// 提供 originalAgi 时走槽位保留路径: 原调色板字节为底(多文件共用同一 VRAM 调色板槽，
        /// 不可重排)，像素颜色映射回原槽位，新颜色只占用本图未引用的槽——未修改的图输出与原版逐字节相同。
        /// </summary>
        public static bool EncodePngToAgiBytes(string pngPath, AgiFileMetadata? meta, byte[]? originalAgi,
            out byte[] agiData, out string? error)
        {
            agiData = Array.Empty<byte>();
            error = null;
            int bitsPerPixel = meta?.BitsPerPixel ?? 8;

            if (bitsPerPixel != 4 && bitsPerPixel != 8)
            {
                error = $"不支持的 bpp: {bitsPerPixel}";
                return false;
            }

            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngPath);
            int width = image.Width;
            int height = image.Height;
            var pixels = new SixLabors.ImageSharp.PixelFormats.Rgba32[checked(width * height)];
            image.CopyPixelDataTo(pixels);

            int colorCount = bitsPerPixel == 4 ? 16 : 256;

            byte[] paletteFile;   // 文件序调色板字节
            byte[] indices;
            if (!TryBuildPreservedPalette(originalAgi, bitsPerPixel, pixels, width, height,
                    out paletteFile, out indices, out error))
            {
                if (error != null)
                    return false;
                indices = IndexedQuantizer.Build(pixels, width, height, colorCount, out var palette);
                paletteFile = bitsPerPixel == 4
                    ? ToFilePalette4(palette)
                    : EncodePalette(ToSdColors(palette));
            }

            bool is4 = bitsPerPixel == 4;
            int rowBytes = is4 ? (width + 1) / 2 : width;
            int pixelOffset = 0x30;
            int clutOffset = pixelOffset + rowBytes * height;

            var hdr = new byte[0x30];
            if (meta != null)
            {
                WriteUInt16(hdr, 0x0C, meta.TextureBase);
                WriteUInt32(hdr, 0x14, meta.Reg3);
                WriteUInt32(hdr, 0x20, meta.ClutBase);
                WriteUInt32(hdr, 0x28, meta.ClutReg3);
            }
            WriteUInt32(hdr, 0x00, 0x20);
            WriteUInt16(hdr, 0x04, 1);
            WriteUInt16(hdr, 0x06, 1);
            WriteUInt32(hdr, 0x08, (uint)pixelOffset);
            WriteUInt16(hdr, 0x0E, is4 ? (ushort)0x14 : (ushort)0x13);
            WriteUInt16(hdr, 0x10, is4 ? (ushort)0x08 : (ushort)0x04);
            WriteUInt16(hdr, 0x12, is4 ? (ushort)0x0200 : (ushort)width);
            WriteUInt16(hdr, 0x18, (ushort)width);
            WriteUInt16(hdr, 0x1A, (ushort)height);
            WriteUInt32(hdr, 0x1C, (uint)clutOffset);
            WriteUInt32(hdr, 0x24, 1);
            WriteUInt16(hdr, 0x2C, is4 ? (ushort)8 : (ushort)0x10);
            WriteUInt16(hdr, 0x2E, is4 ? (ushort)2 : (ushort)0x10);

            // 0x12 是 VRAM 槽行宽(4bpp 随槽位为 384/512 等)，无法从图片本身推导，
            // 有 xml 条目且有原文件时照抄——与上面 4 个 VRAM 簿记字段的回填同一策略
            if (meta != null && originalAgi != null && originalAgi.Length >= 0x14)
            {
                hdr[0x12] = originalAgi[0x12];
                hdr[0x13] = originalAgi[0x13];
            }

            var data = new byte[clutOffset + colorCount * 4];
            Buffer.BlockCopy(hdr, 0, data, 0, 0x30);
            Buffer.BlockCopy(paletteFile, 0, data, clutOffset, colorCount * 4);

            if (is4)
            {
                for (int y = 0; y < height; y++)
                {
                    int row = pixelOffset + y * rowBytes;
                    for (int x = 0; x < width; x++)
                    {
                        byte index = (byte)(indices[y * width + x] & 0x0F);
                        if ((x & 1) == 0)
                            data[row + (x >> 1)] = index;
                        else
                            data[row + (x >> 1)] |= (byte)(index << 4);
                    }
                }
            }
            else
            {
                Buffer.BlockCopy(indices, 0, data, pixelOffset, indices.Length);
            }

            agiData = data;
            return true;
        }

        /// <summary>
        /// 槽位保留: 用原调色板为底做颜色映射。返回 false 且 error 为 null 表示
        /// 不适用(无原件/格式或尺寸不符)，调用方走完整重建。
        /// </summary>
        static bool TryBuildPreservedPalette(
            byte[]? originalAgi,
            int bitsPerPixel,
            SixLabors.ImageSharp.PixelFormats.Rgba32[] pixels,
            int width,
            int height,
            out byte[] paletteFile,
            out byte[] indices,
            out string? error)
        {
            paletteFile = Array.Empty<byte>();
            indices = Array.Empty<byte>();
            error = null;
            if (originalAgi == null || originalAgi.Length < 0x30) return false;

            using var ms = new MemoryStream(originalAgi, false);
            int imageCount = ms.ReadUInt16LEAt(0x04);
            int clutCount = ms.ReadUInt16LEAt(0x06);
            byte psm = ms.ReadByteAt(0x0E);
            int origWidth = ms.ReadUInt16LEAt(0x18);
            int origHeight = ms.ReadUInt16LEAt(0x1A);
            int clutOffset = (int)ms.ReadUInt32LEAt(0x1C);
            if (imageCount != 1 || clutCount != 1) return false;
            if (psm != (bitsPerPixel == 4 ? 0x14 : 0x13)) return false;
            if (origWidth != width || origHeight != height) return false;

            int colorCount = bitsPerPixel == 4 ? 16 : 256;
            if (clutOffset + colorCount * 4 > originalAgi.Length) return false;

            paletteFile = originalAgi[clutOffset..(clutOffset + colorCount * 4)];
            bool swap = colorCount == 256;

            // 逻辑序 -> 文件序的位置（256 色为 CLUT 存储交错）
            int FilePos(int logical) => (swap
                ? (logical & 0xE7) | ((logical & 0x08) << 1) | ((logical & 0x10) >> 1)
                : logical) * 4;

            var slotOfColor = new Dictionary<uint, int>(colorCount);
            var logicalColors = new (byte R, byte G, byte B, byte A)[colorCount];
            for (int i = 0; i < colorCount; i++)
            {
                int p = clutOffset + FilePos(i);
                logicalColors[i] = (originalAgi[p], originalAgi[p + 1], originalAgi[p + 2],
                    ImageUtils.FixAlphaPs2(originalAgi[p + 3]));
                uint key = ((uint)ImageUtils.FixAlphaPs2(originalAgi[p + 3]) << 24) |
                           ((uint)originalAgi[p] << 16) |
                           ((uint)originalAgi[p + 1] << 8) |
                           originalAgi[p + 2];
                slotOfColor.TryAdd(key, i);
            }

            indices = new byte[pixels.Length];
            int transparentSlot = -1;
            for (int i = 0; i < colorCount; i++)
                if (logicalColors[i].A == 0) { transparentSlot = i; break; }

            // 调色板一个字节都不动: 精确匹配保持原索引, 其余映射到最近色号
            // (alpha=0 统一落到透明色号; 同透明度优先由 NearestPaletteSlot 保证)
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                if (slotOfColor.TryGetValue(key, out int slot))
                    indices[i] = (byte)slot;
                else if (c.A == 0 && transparentSlot >= 0)
                    indices[i] = (byte)transparentSlot;
                else
                    indices[i] = (byte)ImageUtils.NearestPaletteSlot(logicalColors, colorCount, (c.R, c.G, c.B, c.A));
            }
            return true;
        }

        static byte[] ToFilePalette4(List<(byte R, byte G, byte B, byte A)> palette)
        {
            var data = new byte[16 * 4];
            for (int i = 0; i < 16; i++)
            {
                var c = palette[i];
                data[i * 4] = c.R;
                data[i * 4 + 1] = c.G;
                data[i * 4 + 2] = c.B;
                data[i * 4 + 3] = c.A == 0 ? (byte)0 : (byte)((c.A + 1) / 2);
            }
            return data;
        }

        static List<SDColor> ToSdColors(List<(byte R, byte G, byte B, byte A)> palette)
        {
            var list = new List<SDColor>(palette.Count);
            foreach (var c in palette)
                list.Add(SDColor.FromArgb(c.A, c.R, c.G, c.B));
            return list;
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