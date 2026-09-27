using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GalaxyAngel2Localization.Utils;
using GalaxyAngel2Localization.Workspace;
using Utils;
using ArtdinkCodec = Utils.Artdink;

namespace GalaxyAngel2Localization.Archives.Artdink
{
    internal static partial class ArtdinkDatRebuilder
    {
        sealed class PathSource
        {
            public bool HasOriginal;
            public string OriginalPath = string.Empty;
            public bool OrigCompressed;
            public int OrigRawSize;

            public bool HasModified;
            public byte[]? CompBuffer;
            public int CompSize;
            public int PlainSize;
        }

        readonly struct PathDataInfo
        {
            public readonly long Offset;
            public readonly int CompressedSize;
            public readonly int UncompressedSize;
            public readonly int StoredSize;
            public readonly bool UsedModified;

            public PathDataInfo(long offset, int compressedSize, int uncompressedSize, int storedSize, bool usedModified)
            {
                Offset = offset;
                CompressedSize = compressedSize;
                UncompressedSize = uncompressedSize;
                StoredSize = storedSize;
                UsedModified = usedModified;
            }
        }        

        static HashSet<string> CollectAllPaths(IDictionary<string, DatIndex> indexDict)
        {
            var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in indexDict)
            {
                var idx = kv.Value;

                if (idx.Tab2 != null)
                {
                    foreach (var p in idx.Tab2)
                    {
                        var np = NormalizePath(p);
                        if (!string.IsNullOrEmpty(np))
                            all.Add(np);
                    }
                }

                if (idx.Tab3 != null)
                {
                    foreach (var b in idx.Tab3.Values)
                    {
                        foreach (var p in b)
                        {
                            var np = NormalizePath(p);
                            if (!string.IsNullOrEmpty(np))
                                all.Add(np);
                        }
                    }
                }
            }
            return all;
        }

        static IReadOnlyDictionary<string, PathSource> BuildGlobalPathSources(
            HashSet<string> allPaths,
            string originalRoot,
            string modifiedRoot,
            Action<string>? logCallback)
        {
            TexMetadataDocument? texMetadata = LoadTexMetadataIfNeeded(allPaths, modifiedRoot);
            TagMetadataDocument? tagMetadata = LoadTagMetadataIfNeeded(allPaths, modifiedRoot);
            AgiMetadataDocument? agiMetadata = LoadAgiMetadataIfNeeded(allPaths, modifiedRoot);
            var map = new ConcurrentDictionary<string, PathSource>(StringComparer.OrdinalIgnoreCase);
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.ForEach(allPaths, po, rel =>
            {
                var src = BuildSinglePathSource(rel, originalRoot, modifiedRoot, texMetadata, tagMetadata, agiMetadata, logCallback);
                map[rel] = src;
            });

            return map;
        }

        static PathSource BuildSinglePathSource(
            string rel,
            string originalRoot,
            string modifiedRoot,
            TexMetadataDocument? texMetadata,
            TagMetadataDocument? tagMetadata,
            AgiMetadataDocument? agiMetadata,
            Action<string>? logCallback)
        {
            string normRel = rel.Replace('\\', '/');
            string origPath = Path.Combine(
                originalRoot,
                normRel.Replace('/', Path.DirectorySeparatorChar));
            string modPath = Path.Combine(
                modifiedRoot,
                normRel.Replace('/', Path.DirectorySeparatorChar));
            string ext = Path.GetExtension(normRel);

            var src = new PathSource
            {
                OriginalPath = origPath,
                HasOriginal = false,
                HasModified = false,
                CompBuffer = null,
                CompSize = 0,
                PlainSize = 0,
                OrigCompressed = false,
                OrigRawSize = 0
            };

            if (File.Exists(origPath))
            {
                src.HasOriginal = true;
                using var fs = new FileStream(origPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.SequentialScan
                });
                if (TryReadArzHeader(fs, out int raw))
                {
                    src.OrigCompressed = true;
                    src.OrigRawSize = raw;
                }
                else
                {
                    src.OrigCompressed = false;
                    src.OrigRawSize = (int)fs.Length;
                }
            }

            if (ext.Equals(".tex", StringComparison.OrdinalIgnoreCase))
            {
                string pngPath = modPath + ".png";
                if (File.Exists(pngPath))
                {
                    if (texMetadata == null)
                        throw new InvalidDataException($"{normRel}.png: modified/tex.xml is required for TEX encoding.");
                    if (!texMetadata.TryGetFile(normRel, out var metadata))
                        throw new InvalidDataException($"{normRel}.png: no matching entry in modified/tex.xml.");
                    if (!TexEncoder.EncodePngToTexBytes(pngPath, metadata, out var texBytes, out var err))
                        throw new InvalidDataException($"{normRel}.png: {err ?? "TEX encoding failed"}");

                    logCallback?.Invoke($"[PNG->TEX] {normRel}.png -> {normRel}");
                    SetModifiedBytes(src, texBytes);
                }
                else if (File.Exists(modPath))
                {
                    SetModifiedBytes(src, File.ReadAllBytes(modPath));
                    if (src.OrigCompressed)
                        logCallback?.Invoke(normRel);
                }
            }
            else if (ext.Equals(".agi", StringComparison.OrdinalIgnoreCase))
            {
                string pngPath = modPath + ".png";
                if (File.Exists(pngPath))
                {
                    // agi.xml 只是 bpp 选择表: 有条目用条目的 bpp(+VRAM 簿记)，没有条目一律 8bpp
                    AgiFileMetadata? meta = null;
                    if (agiMetadata != null)
                        agiMetadata.TryGetFile(normRel, out meta);

                    if (!AgiEncoder.EncodePngToAgiBytes(pngPath, meta, out var agiBytes, out var err))
                        throw new InvalidOperationException($"{normRel}.png: {err ?? "AGI 编码失败"}");

                    int bpp = meta?.BitsPerPixel ?? 8;
                    logCallback?.Invoke($"[PNG->AGI] {normRel}.png -> {normRel}" + (bpp == 0 ? " (auto)" : $" ({bpp}bpp)"));
                    SetModifiedBytes(src, agiBytes);
                }
                else if (File.Exists(modPath))
                {
                    SetModifiedBytes(src, File.ReadAllBytes(modPath));
                    if (src.OrigCompressed)
                        logCallback?.Invoke($"{normRel}");
                }
            }
            else if (ext.Equals(".tag", StringComparison.OrdinalIgnoreCase))
            {
                bool handled = false;

                bool hasTagPng = tagMetadata != null
                    ? tagMetadata.TryGetFile(normRel, out var meta) && meta.Images.Any(i =>
                          File.Exists(modPath + "." + i.Index + ".png"))
                    : HasAnyTagImagePng(modPath);

                if (hasTagPng)
                {
                    if (tagMetadata == null || !tagMetadata.TryGetFile(normRel, out var metadata))
                        throw new InvalidDataException($"{normRel}.N.png: no matching entry in modified/tag.xml.");
                    if (!src.HasOriginal)
                        throw new InvalidDataException($"{normRel}.N.png: the original .tag file is required as the encode template.");

                    byte[] originalTag = ReadOriginalPlain(origPath);

                    if (!TagEncoder.EncodePngsToTagBytes(originalTag, metadata, modPath, out var tagBytes, out int replaced, out var err))
                        throw new InvalidDataException($"{normRel}.N.png: {err ?? "TAG encoding failed"}");

                    logCallback?.Invoke($"[PNG->TAG] {normRel} ({replaced} image(s))");
                    SetModifiedBytes(src, tagBytes);
                    handled = true;
                }

                if (!handled && File.Exists(modPath))
                {
                    SetModifiedBytes(src, File.ReadAllBytes(modPath));
                    if (src.OrigCompressed)
                        logCallback?.Invoke(normRel);
                }
            }
            else
            {
                if (File.Exists(modPath))
                {
                    var plain = File.ReadAllBytes(modPath);
                    src.HasModified = true;
                    src.PlainSize = plain.Length;

                    if (!src.HasOriginal || src.OrigCompressed)
                    {
                        var comp = ArtdinkCodec.Compress(plain, 1, true);
                        src.CompBuffer = comp;
                        src.CompSize = comp.Length;

                        logCallback?.Invoke($"{normRel}");
                    }
                    else
                    {
                        src.CompBuffer = plain;
                        src.CompSize = plain.Length;
                    }
                }
            }

            return src;
        }

        static TexMetadataDocument? LoadTexMetadataIfNeeded(HashSet<string> allPaths, string modifiedRoot)
        {
            bool hasTexPng = false;
            foreach (string rel in allPaths)
            {
                if (!Path.GetExtension(rel).Equals(".tex", StringComparison.OrdinalIgnoreCase))
                    continue;
                string pngPath = Path.Combine(
                    modifiedRoot,
                    NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)) + ".png";
                if (File.Exists(pngPath))
                {
                    hasTexPng = true;
                    break;
                }
            }

            if (!hasTexPng)
                return null;

            string xmlPath = Path.Combine(modifiedRoot, "tex.xml");
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("TEX PNG files are present, but modified/tex.xml is missing.", xmlPath);
            return TexMetadataDocument.Load(xmlPath);
        }

        static TagMetadataDocument? LoadTagMetadataIfNeeded(HashSet<string> allPaths, string modifiedRoot)
        {
            bool hasTagPng = false;
            foreach (string rel in allPaths)
            {
                if (!Path.GetExtension(rel).Equals(".tag", StringComparison.OrdinalIgnoreCase))
                    continue;
                string modPath = Path.Combine(
                    modifiedRoot,
                    NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar));
                if (HasAnyTagImagePng(modPath))
                {
                    hasTagPng = true;
                    break;
                }
            }

            if (!hasTagPng)
                return null;

            string xmlPath = Path.Combine(modifiedRoot, "tag.xml");
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException("TAG PNG files are present, but modified/tag.xml is missing.", xmlPath);
            return TagMetadataDocument.Load(xmlPath);
        }

        static AgiMetadataDocument? LoadAgiMetadataIfNeeded(HashSet<string> allPaths, string modifiedRoot)
        {
            bool hasAgiPng = false;
            foreach (string rel in allPaths)
            {
                if (!Path.GetExtension(rel).Equals(".agi", StringComparison.OrdinalIgnoreCase))
                    continue;
                string pngPath = Path.Combine(
                    modifiedRoot,
                    NormalizePath(rel).Replace('/', Path.DirectorySeparatorChar)) + ".png";
                if (File.Exists(pngPath))
                {
                    hasAgiPng = true;
                    break;
                }
            }

            if (!hasAgiPng)
                return null;

            string xmlPath = Path.Combine(modifiedRoot, "agi.xml");
            if (!File.Exists(xmlPath))
                return null;
            return AgiMetadataDocument.Load(xmlPath);
        }

        static byte[] ReadOriginalPlain(string origPath)
        {
            using var fs = new FileStream(origPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.SequentialScan
            });
            if (ArtdinkCodec.Decompress(fs, (int)fs.Length, out var plain))
                return plain;
            fs.Position = 0;
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }

        static bool HasAnyTagImagePng(string modPath)
        {
            string? dir = Path.GetDirectoryName(modPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return false;

            string prefix = Path.GetFileName(modPath) + ".";
            foreach (string file in Directory.EnumerateFiles(dir, prefix + "*.png"))
            {
                string name = Path.GetFileName(file);
                if (name.Length <= prefix.Length + 4 ||
                    !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    continue;

                string digits = name.Substring(prefix.Length, name.Length - prefix.Length - 4);
                if (digits.All(char.IsDigit))
                    return true;
            }
            return false;
        }

        static void SetModifiedBytes(PathSource source, byte[] plain)
        {
            source.HasModified = true;
            source.PlainSize = plain.Length;
            if (!source.HasOriginal || source.OrigCompressed)
            {
                byte[] compressed = ArtdinkCodec.Compress(plain, 1, true);
                source.CompBuffer = compressed;
                source.CompSize = compressed.Length;
            }
            else
            {
                source.CompBuffer = plain;
                source.CompSize = plain.Length;
            }
        }

        static PathDataInfo WriteDataFromSource(FileStream fsOut, PathSource src)
        {
            long offset = fsOut.Position;
            int compSize;
            int decompSize;
            int storedSize;
            bool usedModified = false;

            if (src.HasModified && src.CompBuffer != null)
            {
                fsOut.Write(src.CompBuffer, 0, src.CompSize);
                usedModified = true;
                storedSize = src.CompSize;

                bool treatAsCompressed = !src.HasOriginal || src.OrigCompressed;
                if (treatAsCompressed)
                {
                    compSize = src.CompSize;
                    decompSize = src.PlainSize;
                }
                else
                {
                    compSize = 0;
                    decompSize = src.CompSize;
                }
            }
            else
            {
                if (!src.HasOriginal)
                    throw new FileNotFoundException("找不到原始块", src.OriginalPath);

                using var fs = new FileStream(src.OriginalPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    Options = FileOptions.SequentialScan
                });

                int len = (int)fs.Length;
                fs.CopyTo(fsOut);
                storedSize = len;

                if (src.OrigCompressed)
                {
                    compSize = len;
                    decompSize = src.OrigRawSize;
                }
                else
                {
                    compSize = 0;
                    decompSize = len;
                }
            }

            return new PathDataInfo(offset, compSize, decompSize, storedSize, usedModified);
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;
            return path.Replace('\\', '/').TrimStart('/');
        }

        static bool TryReadArzHeader(FileStream fs, out int rawSize)
        {
            rawSize = 0;
            if (fs.Length < 8) return false;

            long saved = fs.Position;
            fs.Position = 0;
            Span<byte> hdr = stackalloc byte[8];
            int read = fs.Read(hdr);
            fs.Position = saved;
            if (read < 8) return false;

            if (!ValidMagic(hdr))
                return false;

            uint rs = BinaryPrimitives.ReadUInt32LittleEndian(hdr.Slice(4));
            if (rs == 0 || rs > int.MaxValue)
                return false;

            rawSize = (int)rs;
            return true;
        }

        static bool ValidMagic(ReadOnlySpan<byte> h) =>
            (h[0] == (byte)'A' && h[1] == (byte)'R' && h[2] == (byte)'Z') ||
            (h[0] == (byte)' ' && h[1] == (byte)'3' && h[2] == (byte)';');
    }
}
