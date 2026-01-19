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
            var map = new ConcurrentDictionary<string, PathSource>(StringComparer.OrdinalIgnoreCase);
            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            };

            Parallel.ForEach(allPaths, po, rel =>
            {
                var src = BuildSinglePathSource(rel, originalRoot, modifiedRoot, logCallback);
                map[rel] = src;
            });

            return map;
        }

        static PathSource BuildSinglePathSource(
            string rel,
            string originalRoot,
            string modifiedRoot,
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

            if (ext.Equals(".agi", StringComparison.OrdinalIgnoreCase))
            {
                string pngPath = modPath + ".png";
                if (File.Exists(pngPath))
                {
                    if (!AgiEncoder.EncodePngToAgiBytes(pngPath, out var agiBytes, out var err))
                        throw new InvalidOperationException($"{normRel}.png: {err ?? "AGI 编码失败"}");

                    logCallback?.Invoke($"[PNG->AGI] {normRel}.png -> {normRel}");

                    src.HasModified = true;
                    src.PlainSize = agiBytes.Length;

                    if (!src.HasOriginal || src.OrigCompressed)
                    {
                        var comp = ArtdinkCodec.Compress(agiBytes, 1, true);
                        src.CompBuffer = comp;
                        src.CompSize = comp.Length;
                    }
                    else
                    {
                        src.CompBuffer = agiBytes;
                        src.CompSize = agiBytes.Length;
                    }
                }
                else if (File.Exists(modPath))
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