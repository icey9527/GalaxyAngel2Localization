// Utils/TagCodec.cs
// ---------------------------------------------------------------
// TAG 格式编解码: Utils.TagDecoder / Utils.TagEncoder
//
// TAG 是预构建的 PS2 显示列表（DMA 风格命令链 + 自定义 GS 寄存器记录），
// 由 CAgi::Load 加载，sub_152C28 做 word1 += 基址的重定位后提交渲染。
// 文件内的地址全部是文件相对偏移。
//
// 结构:
//   +0x00  命令 tag（NEXT/CALL），word1 = 链入口（恒 0x30）
//   命令 = 16 字节: word0[31:28]=ID, word0[15:0]=QWC, word1=地址
//     ID: 1=CNT(数据 QWC*16B) 2=NEXT 3/4=REF 5=CALL 6=RET 7=END
//   CNT 包 = [tag][包头16B][N条记录16B]，包头 word0 = 0x5100000N（N=记录数）
//   记录 = [v0][v1][fl][reg]，reg 为 GS 寄存器号:
//     TRXREG(0x52): 宽=v1 高=fl
//   每张图 = [CNT 设置包][CNT2 包][REF 调色板][CNT 设置包][CNT2 包][REF 像素]
//     调色板: TRXREG 8x2=16色(4bpp) / 16x16=256色(8bpp)，RGBA 直接顺序
//     像素: 行优先线性，4bpp 低半字节在前；块大小可能比理论值多/少 8 字节
//
// 编码策略: 以原始文件为模板，仅就地替换 REF 指向的调色板/像素载荷，
// 其余字节（命令流、文件头、对齐尾部）逐字节保留。
// ---------------------------------------------------------------

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
    internal sealed class TagImageLayout
    {
        public int Index;
        public int PaletteOffset;
        public int PaletteSize;
        public int ColorCount;
        public int Width;
        public int Height;
        public int BitsPerPixel;
        public int PixelOffset;
        public int PixelSize;
    }

    internal static class TagChain
    {
        public const int CommandSize = 16;

        public static bool TryParse(byte[] data, out List<TagImageLayout> images, out string? error)
        {
            images = new List<TagImageLayout>();
            error = null;

            try
            {
                var events = WalkTransferEvents(data);
                if (events.Count == 0 || events.Count % 2 != 0)
                    throw new InvalidDataException($"TAG chain yielded {events.Count} transfer events (expected even pairs).");

                for (int i = 0; i < events.Count; i += 2)
                {
                    var palette = events[i];
                    var pixels = events[i + 1];

                    int colorCount = palette.Width * palette.Height;
                    if (colorCount != 16 && colorCount != 256)
                        throw new InvalidDataException($"Image {i / 2}: unsupported palette geometry {palette.Width}x{palette.Height}.");
                    if (palette.Size != colorCount * 4)
                        throw new InvalidDataException($"Image {i / 2}: palette block {palette.Size} != {colorCount * 4} bytes.");

                    int bpp = colorCount == 16 ? 4 : 8;
                    int needed = pixels.Width * pixels.Height * bpp / 8;
                    // REF 块按 16 字节四字对齐，实测会向上或向下取整（相差最多 16 字节）
                    if (Math.Abs(pixels.Size - needed) > 16)
                        throw new InvalidDataException(
                            $"Image {i / 2}: pixel block {pixels.Size} bytes does not match {pixels.Width}x{pixels.Height}@{bpp}bpp ({needed} bytes).");

                    images.Add(new TagImageLayout
                    {
                        Index = i / 2,
                        PaletteOffset = palette.Offset,
                        PaletteSize = palette.Size,
                        ColorCount = colorCount,
                        Width = pixels.Width,
                        Height = pixels.Height,
                        BitsPerPixel = bpp,
                        PixelOffset = pixels.Offset,
                        PixelSize = pixels.Size
                    });
                }
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException)
            {
                error = ex.Message;
                return false;
            }
        }

        sealed class TransferEvent
        {
            public int Offset;
            public int Size;
            public int Width;
            public int Height;
        }

        /// <summary>
        /// 按 sub_152C28 的语义走命令链：CNT 记录里最新的 TRXREG 应用到下一个 REF。
        /// CALL 递归处理后继续本层，RET/END/REFE 结束本层。
        /// </summary>
        static List<TransferEvent> WalkTransferEvents(byte[] data)
        {
            if (data.Length < 0x30)
                throw new InvalidDataException("Truncated TAG file.");

            var events = new List<TransferEvent>();
            var seen = new HashSet<int>();
            int pendingWidth = 0, pendingHeight = 0;

            void Walk(int cursor)
            {
                while (cursor >= 0 && cursor + CommandSize <= data.Length)
                {
                    if (!seen.Add(cursor))
                        return;
                    uint w0 = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor));
                    uint address = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(cursor + 4));
                    int opcode = (int)(w0 & 0x70000000);
                    int qwc = (int)(w0 & 0xFFFF);

                    switch (opcode)
                    {
                        case 0x10000000: // CNT: 数据区为寄存器记录包
                            if (qwc > 0)
                                ReadPacketRecords(data, cursor + CommandSize, ref pendingWidth, ref pendingHeight);
                            cursor += CommandSize + qwc * CommandSize;
                            break;
                        case 0x20000000: // NEXT
                            cursor = (int)address;
                            break;
                        case 0x30000000: // REF
                        case 0x40000000: // REFS
                            events.Add(new TransferEvent
                            {
                                Offset = checked((int)address),
                                Size = checked(qwc * CommandSize),
                                Width = pendingWidth,
                                Height = pendingHeight
                            });
                            pendingWidth = 0;
                            pendingHeight = 0;
                            cursor += CommandSize;
                            break;
                        case 0x50000000: // CALL: 先处理子链，再继续本层
                            Walk(checked((int)address));
                            cursor += CommandSize + qwc * CommandSize;
                            break;
                        case 0x60000000: // RET
                        case 0x70000000: // END
                            return;
                        default: // 0x00000000 (REFE) 或非法 ID
                            return;
                    }
                }
            }

            Walk(checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4))));
            return events;
        }

        static void ReadPacketRecords(byte[] data, int packetOffset, ref int width, ref int height)
        {
            int recordCount = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(packetOffset)) & 0xFFFF);
            for (int i = 0; i < recordCount; i++)
            {
                int record = packetOffset + CommandSize + i * CommandSize;
                if (record + CommandSize > data.Length)
                    throw new InvalidDataException("TAG packet record extends beyond the file.");
                uint reg = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 12));
                if (reg == 0x52) // TRXREG
                {
                    width = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 4)));
                    height = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(record + 8)));
                }
            }
        }
    }

    internal static class TagDecoder
    {
        public static bool DecodeTagToPngs(
            byte[] data,
            string extractRoot,
            string relativePath,
            out int imageCount,
            out IReadOnlyList<TagImageLayout> images,
            out string? error)
        {
            imageCount = 0;
            images = Array.Empty<TagImageLayout>();
            if (!TagChain.TryParse(data, out var parsed, out error))
                return false;

            foreach (var image in parsed)
            {
                string pngRelPath = relativePath + "." + image.Index + ".png";
                string pngPath = Path.Combine(
                    extractRoot,
                    pngRelPath.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    var dir = Path.GetDirectoryName(pngPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    using var bitmap = DecodeImage(data, image);
                    bitmap.Save(pngPath, ImageFormat.Png);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
                {
                    error = $"Unable to write TAG PNG {pngRelPath}: {ex.Message}";
                    return false;
                }
            }

            imageCount = parsed.Count;
            images = parsed;
            return true;
        }

        static Bitmap DecodeImage(byte[] data, TagImageLayout image)
        {
            if (image.Width <= 0 || image.Height <= 0 || image.Width > 4096 || image.Height > 4096)
                throw new InvalidDataException("Invalid TAG image dimensions.");

            EnsureRange(data.Length, image.PaletteOffset, image.ColorCount * 4, "Palette");
            var paletteRaw = new byte[image.ColorCount * 4];
            Buffer.BlockCopy(data, image.PaletteOffset, paletteRaw, 0, paletteRaw.Length);

            // 256 色与 TEX/AGI 相同的 32 色块交错；16 色为直接顺序
            byte[] paletteBgra = image.ColorCount == 256
                ? ImageUtils.BuildPs2Palette256Bgra_Block32(paletteRaw)
                : ImageUtils.BuildPaletteBgraFromRgba(paletteRaw, image.ColorCount, applyPs2AlphaFix: true);

            int neededBytes = image.Width * image.Height * image.BitsPerPixel / 8;
            int validBytes = Math.Min(image.PixelSize, neededBytes);
            EnsureRange(data.Length, image.PixelOffset, validBytes, "Pixels");
            var pixels = new byte[validBytes];
            Buffer.BlockCopy(data, image.PixelOffset, pixels, 0, validBytes);

            // 短缺块（四字对齐砍尾）之外的纹素不补假数据，保持透明
            long validTexels = (long)validBytes * 8 / image.BitsPerPixel;

            var bitmap = ImageUtils.CreateArgbBitmap(image.Width, image.Height, out var bmpData, out int stride);
            try
            {
                var row = new byte[image.Width * 4];
                var rowSource = new byte[(image.Width * image.BitsPerPixel + 7) / 8];
                int rowBytes = rowSource.Length;

                for (int y = 0; y < image.Height; y++)
                {
                    long rowFirstTexel = (long)y * image.Width;
                    if (rowFirstTexel >= validTexels)
                        break;

                    int rowStart = y * rowBytes;
                    int copy = Math.Min(rowBytes, pixels.Length - rowStart);
                    Buffer.BlockCopy(pixels, rowStart, rowSource, 0, copy);
                    if (copy < rowBytes)
                        Array.Clear(rowSource, copy, rowBytes - copy);

                    if (image.BitsPerPixel == 8)
                        ImageUtils.ConvertRowIndexed8ToBgra(rowSource, row, image.Width, paletteBgra);
                    else
                        ImageUtils.ConvertRowIndexed4ToBgra(rowSource, row, image.Width, paletteBgra);

                    int validInRow = (int)Math.Min(image.Width, validTexels - rowFirstTexel);
                    if (validInRow < image.Width)
                        Array.Clear(row, validInRow * 4, (image.Width - validInRow) * 4);

                    ImageUtils.CopyRowToBitmap(bmpData, y, row, stride);
                }
            }
            catch
            {
                ImageUtils.UnlockBitmap(bmpData, bitmap);
                bitmap.Dispose();
                throw;
            }

            ImageUtils.UnlockBitmap(bmpData, bitmap);
            return bitmap;
        }

        static void EnsureRange(int fileLength, int offset, int length, string name)
        {
            if (offset < 0 || length < 0 || (long)offset + length > fileLength)
                throw new InvalidDataException($"{name} data extends beyond the TAG file.");
        }
    }

    internal static class TagEncoder
    {
        /// <summary>
        /// 以 originalTag 为模板，把 pngBasePath + "." + index + ".png" 对应的图像
        /// 载荷就地替换进输出。metadata（tag.xml 条目）会与模板解析结果交叉校验。
        /// 没有 PNG 的图像保持原字节。没有任何 PNG 时返回 true 且 replacedCount = 0。
        /// </summary>
        public static bool EncodePngsToTagBytes(
            byte[] originalTag,
            TagFileMetadata metadata,
            string pngBasePath,
            out byte[] tagData,
            out int replacedCount,
            out string? error)
        {
            tagData = Array.Empty<byte>();
            replacedCount = 0;
            error = null;

            if (!TagChain.TryParse(originalTag, out var images, out error))
                return false;

            if (metadata.Images.Count != images.Count)
                throw new InvalidDataException(
                    $"tag.xml lists {metadata.Images.Count} images but the original TAG has {images.Count}.");
            for (int i = 0; i < images.Count; i++)
            {
                var xml = metadata.Images[i];
                var tpl = images[i];
                if (xml.Width != tpl.Width || xml.Height != tpl.Height || xml.BitsPerPixel != tpl.BitsPerPixel)
                    throw new InvalidDataException(
                        $"tag.xml image {i} ({xml.Width}x{xml.Height}@{xml.BitsPerPixel}bpp) does not match the original TAG ({tpl.Width}x{tpl.Height}@{tpl.BitsPerPixel}bpp).");
            }

            var output = (byte[])originalTag.Clone();
            int replaced = 0;

            try
            {
                foreach (var image in images)
                {
                    string pngPath = pngBasePath + "." + image.Index + ".png";
                    if (!File.Exists(pngPath))
                        continue;

                    using var loaded = SixLabors.ImageSharp.Image.Load<Rgba32>(pngPath);
                    if (loaded.Width != image.Width || loaded.Height != image.Height)
                        throw new InvalidDataException(
                            $"Image {image.Index}: PNG dimensions {loaded.Width}x{loaded.Height} do not match {image.Width}x{image.Height}.");

                    var sourcePixels = new Rgba32[checked(image.Width * image.Height)];
                    loaded.CopyPixelDataTo(sourcePixels);

                    var indices = BuildIndices(sourcePixels, image, out var palette);
                    WritePalette(output, image, palette);
                    WritePixels(output, image, indices);
                    replaced++;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or OverflowException or ArgumentException)
            {
                error = ex.Message;
                return false;
            }

            tagData = output;
            replacedCount = replaced;
            return true;
        }

        /// <summary>
        /// 通用策略（IndexedQuantizer）: 精确调色板优先，超出槽位数才 Wu 量化。
        /// </summary>
        static byte[] BuildIndices(
            Rgba32[] sourcePixels,
            TagImageLayout image,
            out List<(byte R, byte G, byte B, byte A)> palette) =>
            IndexedQuantizer.Build(sourcePixels, image.Width, image.Height, image.ColorCount, out palette);

        static void WritePalette(byte[] output, TagImageLayout image, List<(byte R, byte G, byte B, byte A)> palette)
        {
            int length = image.ColorCount * 4;
            if (image.PaletteOffset + length > output.Length)
                throw new InvalidDataException($"Image {image.Index}: palette block outside the TAG file.");

            for (int i = 0; i < image.ColorCount; i++)
            {
                // 256 色按 GS CLUT 存储交错写入（与 TexEncoder.WritePalette 一致），16 色直接顺序
                int storedIndex = image.ColorCount == 256
                    ? (i & 0xE7) | ((i & 0x08) << 1) | ((i & 0x10) >> 1)
                    : i;
                var color = palette[i];
                int p = image.PaletteOffset + storedIndex * 4;
                output[p] = color.R;
                output[p + 1] = color.G;
                output[p + 2] = color.B;
                output[p + 3] = ImageUtils.EncodePs2Alpha(color.A);
            }
        }

        static void WritePixels(byte[] output, TagImageLayout image, byte[] indices)
        {
            int neededBytes = image.Width * image.Height * image.BitsPerPixel / 8;
            int writable = Math.Min(image.PixelSize, neededBytes);
            if (image.PixelOffset + writable > output.Length)
                throw new InvalidDataException($"Image {image.Index}: pixel block outside the TAG file.");

            if (image.BitsPerPixel == 8)
            {
                for (int i = 0; i < writable; i++)
                    output[image.PixelOffset + i] = indices[i];
            }
            else
            {
                // 4bpp 平铺半字节流：低半字节在前
                for (int i = 0; i < writable; i++)
                    output[image.PixelOffset + i] =
                        (byte)((indices[i * 2] & 0x0F) | ((indices[i * 2 + 1] & 0x0F) << 4));
            }
        }
    }
}
