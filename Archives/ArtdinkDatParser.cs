using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalaxyAngel2Localization.Archives.Artdink
{
    internal sealed class PidxHeader
    {
        public string Magic = string.Empty;
        public uint Table1Offset;
        public uint Table1Count;
        public uint Table2Offset;
        public uint Table2Count;
        public uint RootDirectoryChildCount;
        public uint Table3Offset;
        public uint Table3Size;
        public uint StringPoolOffset;
        public uint StringPoolSize;
    }

    internal sealed class Table2Entry
    {
        public string Name = string.Empty;
        public bool IsDirectory;
        public uint ChildCount;
        public uint ChildStart;
        public uint DataOffset;
        public uint DecompressedSize;
        public uint CompressedSize;
    }

    internal sealed class FstsEntry
    {
        public string Path = string.Empty;
        public long Offset;
        public int CompressedSize;
        public int UncompressedSize;
    }

    internal sealed class FstsBlock
    {
        public string Name { get; set; } = string.Empty;
        public List<FstsEntry> Entries { get; } = new();
    }

    internal static class ArtdinkDatParser
    {
        private static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public static PidxHeader ReadHeader(BinaryReader br)
        {
            br.BaseStream.Position = 0;

            return new PidxHeader
            {
                Magic = Encoding.ASCII.GetString(br.ReadBytes(4)),
                Table1Offset = br.ReadUInt32(),
                Table1Count = br.ReadUInt32(),
                Table2Offset = br.ReadUInt32(),
                Table2Count = br.ReadUInt32(),
                RootDirectoryChildCount = br.ReadUInt32(),
                Table3Offset = br.ReadUInt32(),
                Table3Size = br.ReadUInt32(),
                StringPoolOffset = br.ReadUInt32(),
                StringPoolSize = br.ReadUInt32()
            };
        }

        public static List<Table2Entry> ParseTable2(Stream fs, BinaryReader br, PidxHeader header)
        {
            var list = new List<Table2Entry>((int)header.Table2Count);

            for (uint i = 0; i < header.Table2Count; i++)
            {
                fs.Position = header.Table2Offset + i * 24;
                uint type = br.ReadUInt32();
                uint nameOffset = br.ReadUInt32();
                uint field08 = br.ReadUInt32();
                uint field0C = br.ReadUInt32();
                uint field10 = br.ReadUInt32();
                uint field14 = br.ReadUInt32();

                var entry = new Table2Entry
                {
                    Name = ReadStringAt(fs, header.StringPoolOffset + nameOffset),
                    IsDirectory = type == 1
                };

                if (entry.IsDirectory)
                {
                    entry.ChildCount = field08;
                    entry.ChildStart = field0C;
                }
                else
                {
                    entry.DataOffset = field0C;
                    entry.DecompressedSize = field10;
                    entry.CompressedSize = field14;
                }

                list.Add(entry);
            }

            return list;
        }

        /// <summary>根据 Table2 父子关系，算出每个条目的完整路径（index -> path）</summary>
        public static Dictionary<int, string> BuildTable2Paths(List<Table2Entry> table2, PidxHeader header)
        {
            var visited = new HashSet<int>();
            var paths = new Dictionary<int, string>();

            int rootCount = (int)Math.Min(header.RootDirectoryChildCount, (uint)table2.Count);
            if (rootCount > 0)
            {
                for (int i = 0; i < rootCount; i++)
                    BuildPaths(table2, i, string.Empty, paths, visited);
            }
            else if (table2.Count > 0)
            {
                BuildPaths(table2, 0, string.Empty, paths, visited);
            }

            return paths;
        }

        private static void BuildPaths(
            List<Table2Entry> table2,
            int index,
            string parentPath,
            Dictionary<int, string> paths,
            HashSet<int> visited)
        {
            if (index < 0 || index >= table2.Count || visited.Contains(index))
                return;

            visited.Add(index);
            var entry = table2[index];

            string path = string.IsNullOrEmpty(parentPath)
                ? entry.Name
                : parentPath + "/" + entry.Name;

            paths[index] = path;

            if (!entry.IsDirectory)
                return;

            for (uint i = 0; i < entry.ChildCount; i++)
            {
                BuildPaths(table2, (int)(entry.ChildStart + i), path, paths, visited);
            }
        }

        /// <summary>
        /// 解析 Table3 / FSTS，返回按块分组的条目列表（块名来自 header.StringPoolOffset）
        /// </summary>
        public static List<FstsBlock> ParseTable3(Stream fs, BinaryReader br, PidxHeader header)
        {
            var result = new List<FstsBlock>();

            fs.Position = header.Table3Offset;
            uint blockCount = br.ReadUInt32(); // sub_index_count

            // 每个 4 字节的偏移都是相对 Table3Offset 的
            var relPointers = new uint[blockCount];
            for (uint i = 0; i < blockCount; i++)
                relPointers[i] = br.ReadUInt32();

            for (uint i = 0; i < blockCount; i++)
            {
                long blockMetaPos = header.Table3Offset + relPointers[i];

                if (blockMetaPos + 20 > fs.Length)
                    continue;

                fs.Position = blockMetaPos;

                uint nameOff    = br.ReadUInt32(); // name_offset
                uint dummy      = br.ReadUInt32(); // 占位
                uint fstsOffset = br.ReadUInt32(); // fst_offset
                uint fstsSize   = br.ReadUInt32(); // fst_size
                uint num        = br.ReadUInt32(); // num（暂时不用）

                // 从 header.StringPoolOffset + nameOff 取块名
                string blockName = string.Empty;
                if (header.StringPoolOffset != 0 && nameOff != 0xFFFFFFFF)
                {
                    blockName = ReadStringAt(fs, header.StringPoolOffset + nameOff);
                }

                if (fstsOffset + 16 > fs.Length)
                    continue;

                fs.Position = fstsOffset;
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "FSTS")
                    continue;

                uint entryCount       = br.ReadUInt32();
                uint entriesOffset    = br.ReadUInt32();
                uint stringPoolOffset = br.ReadUInt32();

                var block = new FstsBlock { Name = blockName };

                for (uint j = 0; j < entryCount; j++)
                {
                    fs.Position = fstsOffset + entriesOffset + j * 16;

                    uint nameOff2   = br.ReadUInt32();
                    uint dataOff    = br.ReadUInt32();
                    uint decompSize = br.ReadUInt32();
                    uint compSize   = br.ReadUInt32();

                    string name = ReadStringAt(fs, fstsOffset + stringPoolOffset + nameOff2);
                    string path = name.Replace('\\', '/').TrimStart('/');

                    var entry = new FstsEntry
                    {
                        Path = path,
                        Offset = fstsOffset + dataOff,
                        CompressedSize = (int)compSize,
                        UncompressedSize = (int)decompSize
                    };

                    block.Entries.Add(entry);
                }

                result.Add(block);
            }

            return result;
        }

        private static string ReadStringAt(Stream fs, long offset)
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
            return bytes.Count == 0 ? string.Empty : ShiftJis.GetString(bytes.ToArray());
        }


        public static uint[] ParseTable1(Stream fs, BinaryReader br, PidxHeader header)
        {
            var result = new uint[8];
            fs.Position = header.Table1Offset;
            for (int i = 0; i < 8; i++)
                result[i] = br.ReadUInt32();
            return result;
        }
    }
}