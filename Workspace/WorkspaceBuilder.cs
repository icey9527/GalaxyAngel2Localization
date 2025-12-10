using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using GalaxyAngel2Localization.Archives.Artdink;

namespace GalaxyAngel2Localization.Workspace
{
    /// <summary>
    /// 负责创建 workspace 目录：original / modified / packed + list.json
    /// </summary>
    public sealed class WorkspaceBuilder
    {
        private readonly string _workspaceRoot;
        private readonly string _originalRoot;
        private readonly string _modifiedRoot;
        private readonly string _packedRoot;

        // "all.dat" / "adv.dat" -> DatIndex
        private readonly Dictionary<string, DatIndex> _index =
            new(StringComparer.OrdinalIgnoreCase);

        // original/ 下已经写过的路径，用于去重
        private readonly HashSet<string> _writtenFiles =
            new(StringComparer.OrdinalIgnoreCase);

        public WorkspaceBuilder(string workspaceRoot)
        {
            _workspaceRoot = workspaceRoot;
            _originalRoot = Path.Combine(workspaceRoot, "original");
            _modifiedRoot = Path.Combine(workspaceRoot, "modified");
            _packedRoot = Path.Combine(workspaceRoot, "packed");

            Directory.CreateDirectory(_workspaceRoot);
            Directory.CreateDirectory(_originalRoot);
            Directory.CreateDirectory(_modifiedRoot);
            Directory.CreateDirectory(_packedRoot);
        }

        /// <summary>
        /// 方便直接传文件路径的旧接口（仍然保留）
        /// </summary>
        public void AddArtdinkDat(string datPath)
        {
            if (!File.Exists(datPath))
                throw new FileNotFoundException("DAT 文件不存在", datPath);

            using var fs = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var datName = Path.GetFileName(datPath);
            AddArtdinkDat(fs, datName);
        }

        /// <summary>
        /// 新接口：直接从流中解析 DAT（不需要把 DAT 落盘）
        /// </summary>
        public void AddArtdinkDat(Stream datStream, string datName)
        {
            if (datStream == null || !datStream.CanRead)
                throw new ArgumentException("datStream 不可读。", nameof(datStream));

            var datIndex = new DatIndex();
            _index[datName] = datIndex;

            // 为了方便，要求流支持 Seek；MemoryStream、FileStream 都可以
            if (!datStream.CanSeek)
                throw new NotSupportedException("DAT 流必须支持 Seek。");

            using var br = new BinaryReader(datStream, Encoding.ASCII, leaveOpen: true);

            datStream.Position = 0;
            var header = ArtdinkDatParser.ReadHeader(br);


            var table1 = ArtdinkDatParser.ParseTable1(datStream, br, header);
            for (int i = 4; i < 8; i++)
                datIndex.Tab1.Add(table1[i]);

            // ---- Table2 ----
            if (header.Table2Count > 0)
            {
                var table2 = ArtdinkDatParser.ParseTable2(datStream, br, header);
                var paths = ArtdinkDatParser.BuildTable2Paths(table2, header);

                for (int i = 0; i < table2.Count; i++)
                {
                    var entry = table2[i];
                    if (entry.IsDirectory)
                        continue;

                    if (!paths.TryGetValue(i, out var path))
                        path = entry.Name ?? string.Empty;

                    var normPath = NormalizePath(path);
                    if (string.IsNullOrEmpty(normPath))
                        continue;

                    datIndex.Tab2.Add(normPath);

                    int rawSize = entry.CompressedSize != 0
                        ? (int)entry.CompressedSize
                        : (int)entry.DecompressedSize;

                    if (rawSize <= 0)
                        continue;

                    TryCopyOriginal(datStream, normPath, entry.DataOffset, rawSize);
                }
            }

            // ---- Table3 / FSTS ----
            if (header.Table3Size > 0)
            {
                var blocks = ArtdinkDatParser.ParseTable3(datStream, br, header);

                for (int b = 0; b < blocks.Count; b++)
                {
                    var block = blocks[b];

                    // 优先用块名；没有名字则退回 block_序号
                    string blockKey = !string.IsNullOrWhiteSpace(block.Name)
                        ? block.Name
                        : $"block_{b}";

                    var list = new List<string>();
                    datIndex.Tab3[blockKey] = list;

                    foreach (var e in block.Entries)
                    {
                        var normPath = NormalizePath(e.Path);
                        if (string.IsNullOrEmpty(normPath))
                            continue;

                        list.Add(normPath);

                        int rawSize = e.CompressedSize != 0
                            ? e.CompressedSize
                            : e.UncompressedSize;

                        if (rawSize <= 0)
                            continue;

                        TryCopyOriginal(datStream, normPath, e.Offset, rawSize);
                    }
                }
            }
        }

        /// <summary>
        /// 写出 [workspace]/list.json
        /// </summary>
        public void SaveIndex()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };

            var json = JsonSerializer.Serialize(_index, options);
            var path = Path.Combine(_workspaceRoot, "list.json");
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private void TryCopyOriginal(Stream datStream, string relativePath, long offset, int size)
        {
            // 去重：同一路径只写一次
            if (!_writtenFiles.Add(relativePath))
                return;

            var destPath = Path.Combine(
                _originalRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));

            string dir = Path.GetDirectoryName(destPath) ?? string.Empty;
            if (dir.Length > 0)
                Directory.CreateDirectory(dir);

            datStream.Position = offset;

            using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            CopyExact(datStream, outFs, size);
        }

        private static void CopyExact(Stream input, Stream output, int size)
        {
            var buffer = new byte[81920];
            int remaining = size;

            while (remaining > 0)
            {
                int toRead = Math.Min(remaining, buffer.Length);
                int read = input.Read(buffer, 0, toRead);
                if (read <= 0)
                    break;

                output.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var p = path.Replace('\\', '/').TrimStart('/');
            return p;
        }
    }
}