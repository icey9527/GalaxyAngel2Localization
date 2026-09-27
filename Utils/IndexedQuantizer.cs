// Utils/IndexedQuantizer.cs
// ---------------------------------------------------------------
// 通用索引色量化: Utils.IndexedQuantizer
//
// 精确调色板优先（颜色装得下时零损失，亮度排序），
// 仅当去重后颜色数超过 maxColors 才 Wu 量化（无抖动）。
// TEX 保持原有实现未迁移；TAG / AGI 共用本函数。
// ---------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Quantization;
using Utils;

namespace GalaxyAngel2Localization.Utils
{
    internal static class IndexedQuantizer
    {
        public static byte[] Build(
            Rgba32[] pixels,
            int width,
            int height,
            int maxColors,
            out List<(byte R, byte G, byte B, byte A)> palette)
        {
            var colors = new (byte R, byte G, byte B, byte A)[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
                colors[i] = (pixels[i].R, pixels[i].G, pixels[i].B, pixels[i].A);

            if (ImageUtils.TryBuildBrightnessSortedPalette(colors, maxColors, out var indices, out palette))
                return indices;

            using var quantized = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(pixels, width, height);
            quantized.Mutate(x => x.Quantize(new WuQuantizer(new QuantizerOptions
            {
                MaxColors = maxColors,
                Dither = null
            })));
            var quantizedPixels = new Rgba32[pixels.Length];
            quantized.CopyPixelDataTo(quantizedPixels);
            for (int i = 0; i < quantizedPixels.Length; i++)
                colors[i] = (quantizedPixels[i].R, quantizedPixels[i].G, quantizedPixels[i].B, quantizedPixels[i].A);

            if (!ImageUtils.TryBuildBrightnessSortedPalette(colors, maxColors, out indices, out palette))
                throw new InvalidDataException($"量化后仍超过 {maxColors} 色");
            return indices;
        }
    }
}
