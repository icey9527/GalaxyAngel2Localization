using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using GalaxyAngel2Localization.Workspace;

namespace GalaxyAngel2Localization.Archives.Artdink
{
    internal static class ArtdinkIdxUpdater
    {
        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public static void UpdateIdxForAllDats(string workspaceRoot, Action<string>? logCallback = null)
        {
            var originalIdx = Path.Combine(workspaceRoot, "original", "idx.dat");
            if (!File.Exists(originalIdx))
            {
                logCallback?.Invoke("[IDX] original/idx.dat 不存在，跳过更新");
                return;
            }

            var packedRoot = Path.Combine(workspaceRoot, "packed");
            Directory.CreateDirectory(packedRoot);
            var packedIdx = Path.Combine(packedRoot, "idx.dat");
            File.Copy(originalIdx, packedIdx, overwrite: true);

            var listPath = Path.Combine(workspaceRoot, "list.json");
            if (!File.Exists(listPath))
            {
                logCallback?.Invoke("[IDX] 找不到 list.json，跳过更新");
                return;
            }

            var json = File.ReadAllText(listPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var indexDict = JsonSerializer.Deserialize<Dictionary<string, DatIndex>>(json, options)
                           ?? new Dictionary<string, DatIndex>();

            foreach (var datName in indexDict.Keys)
            {
                if (datName.Equals("idx.dat", StringComparison.OrdinalIgnoreCase))
                    continue;

                var datPath = Path.Combine(packedRoot, datName.ToLowerInvariant());
                if (!File.Exists(datPath))
                {
                    logCallback?.Invoke($"[IDX] 找不到打包后的 {datName}，跳过");
                    continue;
                }

                try
                {
                    UpdateIdxForDat(datPath, packedIdx);
                    logCallback?.Invoke($"[IDX] 已更新 {datName} -> idx.dat");
                }
                catch (Exception ex)
                {
                    logCallback?.Invoke($"[IDX] 更新 {datName} 失败: {ex.Message}");
                }
            }
        }

        public static void UpdateIdxForDat(string datPath, string idxPath)
        {
            var datName = Path.GetFileName(datPath);

            using var datFs = new FileStream(datPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var datBr = new BinaryReader(datFs, ShiftJis, leaveOpen: true);

            ReadDatInfo(datFs, datBr,
                out var fileDict,
                out var fstsDict);

            using var idxFs = new FileStream(idxPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var idxBr = new BinaryReader(idxFs, ShiftJis, leaveOpen: true);

            var header = ArtdinkDatParser.ReadHeader(idxBr);
            if (!string.Equals(header.Magic, "PIDX", StringComparison.Ordinal))
                throw new InvalidDataException("idx.dat magic 不是 PIDX");

            if (header.Table1Count <= 1)
                throw new InvalidDataException("table1Count <= 1，看起来不是 idx.dat");

            uint nameStart = header.StringPoolOffset;
            uint table1Off = header.Table1Offset;
            uint table1Count = header.Table1Count;

            uint datSign = 0;
            bool foundDat = false;

            for (uint i = 0; i < table1Count; i++)
            {
                long pos = table1Off + i * 32;
                if (pos + 32 > idxFs.Length)
                    break;

                idxFs.Position = pos;
                uint nameOff = idxBr.ReadUInt32();

                string name = ReadStringAt(idxFs, nameStart + nameOff);
                if (string.Equals(name, datName, StringComparison.OrdinalIgnoreCase))
                {
                    datSign = nameOff;
                    foundDat = true;
                    break;
                }
            }

            if (!foundDat)
                throw new InvalidDataException("idx.dat 中找不到此 dat: " + datName);

            if (header.Table2Count > 0 && fileDict.Count > 0)
                UpdateFileIdx(idxFs, idxBr, header, nameStart, datSign, fileDict);

            if (header.Table3Size > 0 && header.Table3Offset != 0 && fstsDict.Count > 0)
                UpdateFstsIdx(idxFs, idxBr, header, nameStart, fstsDict);
        }

        static void ReadDatInfo(
            FileStream fs,
            BinaryReader br,
            out Dictionary<string, (uint Offset, uint Unc, uint Comp)> fileDict,
            out Dictionary<string, (uint FstOffset, uint FstSize, uint FstSub)> fstsDict)
        {
            fs.Position = 0;
            var header = ArtdinkDatParser.ReadHeader(br);

            fileDict = new Dictionary<string, (uint, uint, uint)>(StringComparer.OrdinalIgnoreCase);
            fstsDict = new Dictionary<string, (uint, uint, uint)>(StringComparer.OrdinalIgnoreCase);

            if (header.Table2Count > 0)
            {
                var table2 = ArtdinkDatParser.ParseTable2(fs, br, header);
                var paths = ArtdinkDatParser.BuildTable2Paths(table2, header);

                for (int i = 0; i < table2.Count; i++)
                {
                    var e = table2[i];
                    if (e.IsDirectory)
                        continue;

                    if (!paths.TryGetValue(i, out var path))
                        path = e.Name ?? string.Empty;

                    path = path.Replace('\\', '/').TrimStart('/');
                    if (string.IsNullOrEmpty(path))
                        continue;

                    uint off = e.DataOffset;
                    uint decomp = e.DecompressedSize;
                    uint comp = e.CompressedSize;

                    fileDict[path] = (off, decomp, comp);
                }
            }

            if (header.Table3Size > 0 && header.Table3Offset != 0)
            {
                uint table3Off = header.Table3Offset;
                fs.Position = table3Off;
                uint blockCount = br.ReadUInt32();
                var relPointers = new uint[blockCount];
                for (uint i = 0; i < blockCount; i++)
                    relPointers[i] = br.ReadUInt32();

                for (uint i = 0; i < blockCount; i++)
                {
                    long metaPos = table3Off + relPointers[i];
                    if (metaPos + 20 > fs.Length)
                        continue;

                    fs.Position = metaPos;
                    uint nameOff = br.ReadUInt32();
                    uint dummy = br.ReadUInt32();
                    uint fstsOff = br.ReadUInt32();
                    uint fstsSize = br.ReadUInt32();
                    uint fstSub = br.ReadUInt32();

                    string name = ReadStringAt(fs, header.StringPoolOffset + nameOff);
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    fstsDict[name] = (fstsOff, fstsSize, fstSub);
                }
            }
        }

        static void UpdateFileIdx(
            FileStream fs,
            BinaryReader br,
            PidxHeader header,
            uint nameStart,
            uint datSign,
            Dictionary<string, (uint Offset, uint Unc, uint Comp)> fileDict)
        {
            uint table2Off = header.Table2Offset;
            uint table2Count = header.Table2Count;

            var idxTable2 = ArtdinkDatParser.ParseTable2(fs, br, header);
            var idxPaths = ArtdinkDatParser.BuildTable2Paths(idxTable2, header);

            for (uint i = 0; i < table2Count; i++)
            {
                long addr = table2Off + i * 24;
                if (addr + 24 > fs.Length)
                    break;

                fs.Position = addr;
                uint type = br.ReadUInt32();
                uint nameOff = br.ReadUInt32();
                uint sign = br.ReadUInt32();
                uint dataOff = br.ReadUInt32();
                uint decompSize = br.ReadUInt32();
                uint compSize = br.ReadUInt32();

                if (type != 0 || sign != datSign)
                    continue;

                string path;
                if (!idxPaths.TryGetValue((int)i, out path) || string.IsNullOrEmpty(path))
                    path = ReadStringAt(fs, nameStart + nameOff);

                path = path.Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrEmpty(path))
                    continue;

                if (!fileDict.TryGetValue(path, out var info))
                    continue;

                fs.Position = addr + 3 * 4;
                Span<byte> buf = stackalloc byte[4];

                BinaryPrimitives.WriteUInt32LittleEndian(buf, info.Offset);
                fs.Write(buf);

                BinaryPrimitives.WriteUInt32LittleEndian(buf, info.Unc);
                fs.Write(buf);

                BinaryPrimitives.WriteUInt32LittleEndian(buf, info.Comp);
                fs.Write(buf);
            }
        }

        static void UpdateFstsIdx(
            FileStream fs,
            BinaryReader br,
            PidxHeader header,
            uint nameStart,
            Dictionary<string, (uint FstOffset, uint FstSize, uint FstSub)> fstsDict)
        {
            uint table3Off = header.Table3Offset;
            if (table3Off == 0)
                return;

            fs.Position = table3Off;
            uint blockCount = br.ReadUInt32();
            var relPointers = new uint[blockCount];
            for (uint i = 0; i < blockCount; i++)
                relPointers[i] = br.ReadUInt32();

            for (uint i = 0; i < blockCount; i++)
            {
                long ptr = table3Off + relPointers[i];
                if (ptr + 20 > fs.Length)
                    continue;

                fs.Position = ptr;
                uint nameOff = br.ReadUInt32();
                uint dummy = br.ReadUInt32();
                uint fstOff = br.ReadUInt32();
                uint fstSize = br.ReadUInt32();
                uint fstSub = br.ReadUInt32();

                string name = ReadStringAt(fs, nameStart + nameOff);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!fstsDict.TryGetValue(name, out var info))
                    continue;

                fs.Position = ptr + 2 * 4;
                Span<byte> buf = stackalloc byte[4];

                BinaryPrimitives.WriteUInt32LittleEndian(buf, info.FstOffset);
                fs.Write(buf);

                BinaryPrimitives.WriteUInt32LittleEndian(buf, info.FstSize);
                fs.Write(buf);

                BinaryPrimitives.WriteUInt32LittleEndian(buf, info.FstSub);
                fs.Write(buf);
            }
        }

        static string ReadStringAt(Stream fs, long offset)
        {
            if (offset < 0 || offset >= fs.Length)
                return string.Empty;

            long saved = fs.Position;
            fs.Position = offset;

            var bytes = new List<byte>();
            int b;
            while ((b = fs.ReadByte()) > 0)
                bytes.Add((byte)b);

            fs.Position = saved;
            if (bytes.Count == 0)
                return string.Empty;

            return ShiftJis.GetString(bytes.ToArray());
        }
    }
}