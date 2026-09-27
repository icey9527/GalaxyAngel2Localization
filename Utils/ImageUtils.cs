// Utils/ImageUtils.cs
// ---------------------------------------------------------------
// 公共图像解码工具: Utils.ImageUtils
//
// 目标:
//   1) 提供通用的 Bitmap 锁定/写入辅助;
//   2) 提供常见像素格式(4/8bpp 调色板, 1555, 4444, 24/32bpp 等)到 32bpp BGRA 的行转换;
//   3) 提供 PS2 风格的 Alpha 映射函数;
//   4) 提供调色板构建与“按 32 色块重排”的函数。
// ---------------------------------------------------------------

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Utils
{
    internal static class ImageUtils
    {
        // -------------------------------------------------------
        // 1. Bitmap 创建 / 锁定 / 写入一行
        // -------------------------------------------------------

        public static Bitmap CreateArgbBitmap(int width, int height, out BitmapData bmpData, out int stride)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var rect = new Rectangle(0, 0, width, height);
            bmpData = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            stride = bmpData.Stride;
            return bmp;
        }

        public static void UnlockBitmap(BitmapData bmpData, Bitmap bmp)
        {
            if (bmpData != null && bmp != null)
                bmp.UnlockBits(bmpData);
        }

        public static void CopyRowToBitmap(BitmapData bmpData, int y, byte[] row, int stride)
        {
            if (row == null) throw new ArgumentNullException(nameof(row));

            IntPtr dest = IntPtr.Add(bmpData.Scan0, y * stride);
            Marshal.Copy(row, 0, dest, row.Length);
        }

        // -------------------------------------------------------
        // 2. 像素格式行转换 => 统一输出 BGRA
        // -------------------------------------------------------

        public static void ConvertRowIndexed4ToBgra(byte[] packed, byte[] destRow, int width, byte[] paletteBgra)
        {
            if (packed == null) throw new ArgumentNullException(nameof(packed));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (paletteBgra == null) throw new ArgumentNullException(nameof(paletteBgra));
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int dst = 0;
            for (int x = 0; x < width; x++)
            {
                int b = packed[x >> 1];
                int idx = (x & 1) == 0 ? (b & 0x0F) : ((b >> 4) & 0x0F);
                int pi = idx * 4;

                destRow[dst + 0] = paletteBgra[pi + 0];
                destRow[dst + 1] = paletteBgra[pi + 1];
                destRow[dst + 2] = paletteBgra[pi + 2];
                destRow[dst + 3] = paletteBgra[pi + 3];
                dst += 4;
            }
        }

        public static void ConvertRowIndexed8ToBgra(byte[] indices, byte[] destRow, int width, byte[] paletteBgra)
        {
            if (indices == null) throw new ArgumentNullException(nameof(indices));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (paletteBgra == null) throw new ArgumentNullException(nameof(paletteBgra));
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int dst = 0;
            for (int x = 0; x < width; x++)
            {
                int idx = indices[x];
                int pi = idx * 4;

                destRow[dst + 0] = paletteBgra[pi + 0];
                destRow[dst + 1] = paletteBgra[pi + 1];
                destRow[dst + 2] = paletteBgra[pi + 2];
                destRow[dst + 3] = paletteBgra[pi + 3];
                dst += 4;
            }
        }

        public static void ConvertRowRgb555ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 2) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                ushort px = (ushort)(src[srcIndex] | (src[srcIndex + 1] << 8));
                srcIndex += 2;

                int b5 = (px >> 10) & 0x1F;
                int g5 = (px >> 5) & 0x1F;
                int r5 = px & 0x1F;

                byte r = (byte)((r5 << 3) | (r5 >> 2));
                byte g = (byte)((g5 << 3) | (g5 >> 2));
                byte b = (byte)((b5 << 3) | (b5 >> 2));

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = 255;
                dst += 4;
            }
        }

        public static void ConvertRowArgb1555ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 2) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                ushort v = (ushort)(src[srcIndex] | (src[srcIndex + 1] << 8));
                srcIndex += 2;

                DecodeArgb1555(v, out byte a, out byte r, out byte g, out byte b);

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = a;
                dst += 4;
            }
        }

        public static void ConvertRowArgb4444ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 2) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                ushort v = (ushort)(src[srcIndex] | (src[srcIndex + 1] << 8));
                srcIndex += 2;

                DecodeArgb4444(v, out byte a, out byte r, out byte g, out byte b);

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = a;
                dst += 4;
            }
        }

        public static void ConvertRowBgr24ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 3) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                byte b = src[srcIndex++];
                byte g = src[srcIndex++];
                byte r = src[srcIndex++];

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = 255;
                dst += 4;
            }
        }

        public static void ConvertRowRgb24ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 3) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                byte r = src[srcIndex++];
                byte g = src[srcIndex++];
                byte b = src[srcIndex++];

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = 255;
                dst += 4;
            }
        }

        public static void ConvertRowRgba32ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 4) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                byte r = src[srcIndex++];
                byte g = src[srcIndex++];
                byte b = src[srcIndex++];
                byte a = src[srcIndex++];

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = a;
                dst += 4;
            }
        }

        public static void ConvertRowRgba32ToBgraWithPs2Alpha(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 4) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                byte r = src[srcIndex++];
                byte g = src[srcIndex++];
                byte b = src[srcIndex++];
                byte a = src[srcIndex++];

                a = FixAlphaPs2(a);

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = a;
                dst += 4;
            }
        }

        // -------------------------------------------------------
        // 3. 像素级解码 (ARGB1555 / ARGB4444)
        // -------------------------------------------------------

        public static void DecodeArgb1555(ushort v, out byte a, out byte r, out byte g, out byte b)
        {
            a = (byte)((v & 0x8000) != 0 ? 255 : 0);

            int r5 = (v >> 10) & 0x1F;
            int g5 = (v >> 5) & 0x1F;
            int b5 = v & 0x1F;

            r = (byte)((r5 << 3) | (r5 >> 2));
            g = (byte)((g5 << 3) | (g5 >> 2));
            b = (byte)((b5 << 3) | (b5 >> 2));
        }

        public static void DecodeArgb4444(ushort v, out byte a, out byte r, out byte g, out byte b)
        {
            int a4 = (v >> 12) & 0xF;
            int r4 = (v >> 8) & 0xF;
            int g4 = (v >> 4) & 0xF;
            int b4 = v & 0xF;

            a = (byte)((a4 << 4) | a4);
            r = (byte)((r4 << 4) | r4);
            g = (byte)((g4 << 4) | g4);
            b = (byte)((b4 << 4) | b4);
        }

        // -------------------------------------------------------
        // 4. 调色板 / PS2 透明度 / 调色板重排
        // -------------------------------------------------------

        /// <summary>
        /// PS2 风格 Alpha 修正: v = a * 2 - 1, clamp 到 [0,255]。
        /// </summary>
        public static byte FixAlphaPs2(byte a)
        {
            int v = a * 2 - 1;
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return (byte)v;
        }

        /// <summary>
        /// FixAlphaPs2 的逆映射: 0 保持 0，其余 (a+1)/2 落回 PS2 的 0..128 区间。
        /// </summary>
        public static byte EncodePs2Alpha(byte a) => a == 0 ? (byte)0 : (byte)((a + 1) / 2);

        /// <summary>
        /// 逻辑色号 -> CLUT 文件槽位(单位: 4 字节)。
        /// 256 色为 PS2 的 32 色块交错顺序，16 色为直接顺序。
        /// </summary>
        public static int Ps2ClutFileSlot(int logical, int colorCount) =>
            colorCount == 256
                ? (logical & 0xE7) | ((logical & 0x08) << 1) | ((logical & 0x10) >> 1)
                : logical;

        /// <summary>
        /// 在调色板里找与目标颜色最接近的色号: 把 alpha 差当作 RGB 偏移,
        /// 取"黑底/白底"最坏情况误差(感知权重 绿4 红/蓝2.5)。
        /// 目标色 alpha=0 时优先落到调色板里的透明色号（不同工具导出的"透明黑/透明白"RGB 不同）。
        /// 用于"新颜色装不下"时的量化: 不改调色板字节，只把像素落到现有色号。
        /// </summary>
        public static int NearestPaletteSlot(
            (byte R, byte G, byte B, byte A)[] palette,
            int count,
            (byte R, byte G, byte B, byte A) color)
        {
            if (color.A == 0)
            {
                int transparent = -1;
                long transparentDist = long.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    var p = palette[i];
                    if (p.A != 0) continue;
                    long d = RgbDistance(p, color);
                    if (d < transparentDist) { transparentDist = d; transparent = i; }
                }
                if (transparent >= 0) return transparent;
            }

            int best = 0;
            long bestDist = long.MaxValue;
            for (int i = 0; i < count; i++)
            {
                var p = palette[i];
                long dr = color.R - p.R;
                long dg = color.G - p.G;
                long db = color.B - p.B;
                long da = color.A - p.A;
                // 把 alpha 差当作 RGB 上的偏移, 取"黑底 / 白底"两种背景假设下的最坏误差
                // (合成后可见误差取决于背景色; 取最坏值 = 跨背景最稳健, 见 ImageMagick 讨论)
                long black = Weighted(dr - da, dg - da, db - da);
                long white = Weighted(dr + da, dg + da, db + da);
                long dist = Math.Max(black, white);
                if (dist < bestDist) { bestDist = dist; best = i; }
            }
            return best;

            // 感知权重: 绿 4, 红/蓝 2.5
            static long Weighted(long r, long g, long b) => 5 * r * r / 2 + 4 * g * g + 5 * b * b / 2;

            static long RgbDistance((byte R, byte G, byte B, byte A) p, (byte R, byte G, byte B, byte A) c)
            {
                long dr = c.R - p.R;
                long dg = c.G - p.G;
                long db = c.B - p.B;
                return dr * dr + dg * dg + db * db;
            }
        }

        /// <summary>
        /// 通用: 从像素流构建索引色编码用的调色板与索引。
        /// 调色板按亮度降序排列（亮色在前，字节过渡平滑，利于 LZ/ARZ 压缩），
        /// 同亮度按首次出现顺序。颜色数超过 maxColors 时返回 false（调用方自行量化）。
        /// </summary>
        public static bool TryBuildBrightnessSortedPalette(
            (byte R, byte G, byte B, byte A)[] pixels,
            int maxColors,
            out byte[] indices,
            out List<(byte R, byte G, byte B, byte A)> palette)
        {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            indices = new byte[pixels.Length];
            palette = new List<(byte, byte, byte, byte)>(maxColors);

            var distinct = new Dictionary<uint, int>(maxColors);   // key -> 首次出现序号
            var colors = new List<(byte R, byte G, byte B, byte A)>(maxColors);
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                if (!distinct.ContainsKey(key))
                {
                    if (colors.Count == maxColors)
                        return false;
                    distinct.Add(key, colors.Count);
                    colors.Add(c);
                }
            }

            int[] order = new int[colors.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, Comparer<int>.Create((a, b) =>
            {
                var ca = colors[a];
                var cb = colors[b];
                int la = ca.R * 299 + ca.G * 587 + ca.B * 114;
                int lb = cb.R * 299 + cb.G * 587 + cb.B * 114;
                if (la != lb) return lb.CompareTo(la);      // 亮色在前
                return a.CompareTo(b);                       // 同亮度按首次出现
            }));

            var slotOfOrder = new byte[colors.Count];
            for (int slot = 0; slot < order.Length; slot++)
            {
                slotOfOrder[order[slot]] = (byte)slot;
                palette.Add(colors[order[slot]]);
            }
            while (palette.Count < maxColors)
                palette.Add(default);

            for (int i = 0; i < pixels.Length; i++)
            {
                var c = pixels[i];
                uint key = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                indices[i] = slotOfOrder[distinct[key]];
            }
            return true;
        }

        /// <summary>
        /// 从 RGBA 调色板数据构建 BGRA 调色板。
        /// 不做重排，只做 RGBA-&gt;BGRA + 可选 PS2 Alpha 映射。
        /// </summary>
        public static byte[] BuildPaletteBgraFromRgba(byte[] srcRgba, int colorCount, bool applyPs2AlphaFix)
        {
            if (srcRgba == null) throw new ArgumentNullException(nameof(srcRgba));
            if (colorCount < 0) throw new ArgumentOutOfRangeException(nameof(colorCount));
            if (srcRgba.Length < colorCount * 4) throw new ArgumentException("srcRgba too short.");

            var dst = new byte[colorCount * 4];

            for (int i = 0; i < colorCount; i++)
            {
                int s = i * 4;
                int d = i * 4;

                byte r = srcRgba[s + 0];
                byte g = srcRgba[s + 1];
                byte b = srcRgba[s + 2];
                byte a = srcRgba[s + 3];

                if (applyPs2AlphaFix)
                    a = FixAlphaPs2(a);

                dst[d + 0] = b;
                dst[d + 1] = g;
                dst[d + 2] = r;
                dst[d + 3] = a;
            }

            return dst;
        }

        /// <summary>
        /// 对 RGBA 调色板做“按 32 色块重排”(典型 PS2 逻辑)。
        /// 输入/输出均为 RGBA。
        /// </summary>
        public static byte[] ReorderPalettePs2Block32Rgba(byte[] srcRgba, int colorCount)
        {
            if (srcRgba == null) throw new ArgumentNullException(nameof(srcRgba));
            if (colorCount <= 0 || colorCount % 32 != 0)
                throw new ArgumentOutOfRangeException(nameof(colorCount), "colorCount must be positive and multiple of 32.");
            if (srcRgba.Length < colorCount * 4)
                throw new ArgumentException("srcRgba too short.");

            var dst = new byte[colorCount * 4];

            int dstColorIndex = 0;
            for (int block = 0; block < colorCount; block += 32)
            {
                CopyRange(srcRgba, block + 0,  8, dst, ref dstColorIndex);  // 0-7
                CopyRange(srcRgba, block + 16, 8, dst, ref dstColorIndex);  // 16-23
                CopyRange(srcRgba, block + 8,  8, dst, ref dstColorIndex);  // 8-15
                CopyRange(srcRgba, block + 24, 8, dst, ref dstColorIndex);  // 24-31
            }

            return dst;

            static void CopyRange(byte[] src, int startIndex, int count, byte[] dst, ref int dstColorIndex)
            {
                for (int i = 0; i < count; i++)
                {
                    int srcColor = startIndex + i;
                    int s = srcColor * 4;
                    int d = dstColorIndex * 4;

                    dst[d + 0] = src[s + 0];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = src[s + 3];

                    dstColorIndex++;
                }
            }
        }

        /// <summary>
        /// 从 256*4 RGBA 调色板构建 PS2 风格的 BGRA 调色板（FAC/AGI/TEX/TFX 通用）:
        /// 1) 对每个颜色的 A 应用 FixAlphaPs2;
        /// 2) 按 32 色块重排 (0-7,16-23,8-15,24-31);
        /// 3) 最终输出 BGRA 数组。
        /// 这是把你 FAC / AGI / TEX / TXF 原来的逻辑打包成一个公共函数。
        /// </summary>
        public static byte[] BuildPs2Palette256Bgra_Block32(byte[] srcRgba256)
        {
            if (srcRgba256 == null) throw new ArgumentNullException(nameof(srcRgba256));
            if (srcRgba256.Length < 256 * 4)
                throw new ArgumentException("srcRgba256 too short (need 256*4 bytes).");

            // 1) 先在 RGBA 域里做 Alpha 修正
            var tmp = new byte[256 * 4];
            for (int i = 0; i < 256; i++)
            {
                int off = i * 4;
                byte r = srcRgba256[off + 0];
                byte g = srcRgba256[off + 1];
                byte b = srcRgba256[off + 2];
                byte a = FixAlphaPs2(srcRgba256[off + 3]); // PS2 Alpha

                tmp[off + 0] = r;
                tmp[off + 1] = g;
                tmp[off + 2] = b;
                tmp[off + 3] = a;
            }

            // 2) 再按 32 色块重排 + 转成 BGRA
            var palette = new byte[256 * 4];
            int dst = 0;

            for (int major = 0; major < 256; major += 32)
            {
                // 0-7
                for (int i = 0; i < 8; i++)
                {
                    int src = (major + i) * 4;
                    palette[dst + 0] = tmp[src + 2]; // B
                    palette[dst + 1] = tmp[src + 1]; // G
                    palette[dst + 2] = tmp[src + 0]; // R
                    palette[dst + 3] = tmp[src + 3]; // A
                    dst += 4;
                }
                // 16-23
                for (int i = 16; i < 24; i++)
                {
                    int src = (major + i) * 4;
                    palette[dst + 0] = tmp[src + 2];
                    palette[dst + 1] = tmp[src + 1];
                    palette[dst + 2] = tmp[src + 0];
                    palette[dst + 3] = tmp[src + 3];
                    dst += 4;
                }
                // 8-15
                for (int i = 8; i < 16; i++)
                {
                    int src = (major + i) * 4;
                    palette[dst + 0] = tmp[src + 2];
                    palette[dst + 1] = tmp[src + 1];
                    palette[dst + 2] = tmp[src + 0];
                    palette[dst + 3] = tmp[src + 3];
                    dst += 4;
                }
                // 24-31
                for (int i = 24; i < 32; i++)
                {
                    int src = (major + i) * 4;
                    palette[dst + 0] = tmp[src + 2];
                    palette[dst + 1] = tmp[src + 1];
                    palette[dst + 2] = tmp[src + 0];
                    palette[dst + 3] = tmp[src + 3];
                    dst += 4;
                }
            }

            return palette;
        }

/// <summary>
        /// 将一行 16bpp RGBA5650 (R5 G6 B5, 无 Alpha) 转为 BGRA。
        /// src.Length 应为 width * 2。
        /// </summary>
        public static void ConvertRowRgba5650ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 2) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                ushort v = (ushort)(src[srcIndex] | (src[srcIndex + 1] << 8));
                srcIndex += 2;

                int r5 = v & 0x1F;
                int g6 = (v >> 5) & 0x3F;
                int b5 = (v >> 11) & 0x1F;

                byte r = (byte)((r5 << 3) | (r5 >> 2));          // 5 -> 8
                byte g = (byte)((g6 << 2) | (g6 >> 4));          // 6 -> 8
                byte b = (byte)((b5 << 3) | (b5 >> 2));          // 5 -> 8

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = 255;
                dst += 4;
            }
        }

        /// <summary>
        /// 将一行 16bpp RGBA5551 (R5 G5 B5 A1) 转为 BGRA。
        /// src.Length 应为 width * 2。
        /// </summary>
        public static void ConvertRowRgba5551ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 2) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                ushort v = (ushort)(src[srcIndex] | (src[srcIndex + 1] << 8));
                srcIndex += 2;

                int r5 =  v        & 0x1F;
                int g5 = (v >> 5)  & 0x1F;
                int b5 = (v >> 10) & 0x1F;
                int a1 = (v >> 15) & 0x01;

                byte r = (byte)((r5 << 3) | (r5 >> 2));
                byte g = (byte)((g5 << 3) | (g5 >> 2));
                byte b = (byte)((b5 << 3) | (b5 >> 2));
                byte a = (byte)(a1 != 0 ? 255 : 0);

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = a;
                dst += 4;
            }
        }

        /// <summary>
        /// 将一行 16bpp RGBA4444 (R4 G4 B4 A4) 转为 BGRA。
        /// src.Length 应为 width * 2。
        /// </summary>
        public static void ConvertRowRgba4444ToBgra(byte[] src, byte[] destRow, int width)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (destRow == null) throw new ArgumentNullException(nameof(destRow));
            if (src.Length < width * 2) throw new ArgumentException("src too short.");
            if (destRow.Length < width * 4) throw new ArgumentException("destRow too short.");

            int srcIndex = 0;
            int dst = 0;

            for (int x = 0; x < width; x++)
            {
                ushort v = (ushort)(src[srcIndex] | (src[srcIndex + 1] << 8));
                srcIndex += 2;

                int r4 =  v        & 0xF;
                int g4 = (v >> 4)  & 0xF;
                int b4 = (v >> 8)  & 0xF;
                int a4 = (v >> 12) & 0xF;

                byte r = (byte)((r4 << 4) | r4);
                byte g = (byte)((g4 << 4) | g4);
                byte b = (byte)((b4 << 4) | b4);
                byte a = (byte)((a4 << 4) | a4);

                destRow[dst + 0] = b;
                destRow[dst + 1] = g;
                destRow[dst + 2] = r;
                destRow[dst + 3] = a;
                dst += 4;
            }
        }


    }
}