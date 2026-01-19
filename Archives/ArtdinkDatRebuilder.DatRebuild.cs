using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GalaxyAngel2Localization.Utils;
using GalaxyAngel2Localization.Workspace;
using Utils;
using ArtdinkCodec = Utils.Artdink;

namespace GalaxyAngel2Localization.Archives.Artdink
{
    internal static partial class ArtdinkDatRebuilder
    {
        sealed class DirNode
        {
            public string Name;
            public Dictionary<string, DirNode> Dirs = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, FileNode> Files = new(StringComparer.OrdinalIgnoreCase);

            public DirNode(string name) => Name = name;
        }

        sealed class FileNode
        {
            public string Name;
            public string FullPath;
            public FileNode(string name, string fullPath)
            {
                Name = name;
                FullPath = fullPath;
            }
        }

        sealed class Table2Build
        {
            public string Name = string.Empty;
            public bool IsDirectory;
            public int ChildStart;
            public int ChildCount;
            public long DataOffset;
            public int CompressedSize;
            public int UncompressedSize;
            public string? FullPath;
        }

        sealed class FstsBlockBuild
        {
            public string Name = string.Empty;
            public List<string> Paths { get; } = new();
        }

        static DatRebuildResult RebuildSingleDat(
            string datName,
            DatIndex datIndex,
            string outputDatPath,
            IReadOnlyDictionary<string, PathSource> sources)
        {
            var tab2Paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in datIndex.Tab2)
            {
                var np = NormalizePath(p);
                if (!string.IsNullOrEmpty(np))
                    tab2Paths.Add(np);
            }

            var blocks = new List<FstsBlockBuild>();
            foreach (var kv in datIndex.Tab3)
            {
                var b = new FstsBlockBuild { Name = kv.Key };
                foreach (var p in kv.Value)
                {
                    var np = NormalizePath(p);
                    if (!string.IsNullOrEmpty(np))
                        b.Paths.Add(np);
                }
                if (b.Paths.Count > 0)
                    blocks.Add(b);
            }

            var allPaths = new HashSet<string>(tab2Paths, StringComparer.OrdinalIgnoreCase);
            foreach (var b in blocks)
                foreach (var p in b.Paths)
                    allPaths.Add(p);

            var table2List = new List<Table2Build>(tab2Paths.Count * 2);
            int rootChildCount = 0;
            bool hasTab2 = tab2Paths.Count > 0;

            if (hasTab2)
            {
                var root = new DirNode(string.Empty);
                foreach (var path in tab2Paths)
                    InsertPathToTree(root, path);

                foreach (var dir in root.Dirs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    table2List.Add(new Table2Build
                    {
                        Name = dir.Name,
                        IsDirectory = true
                    });
                }

                foreach (var file in root.Files.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    table2List.Add(new Table2Build
                    {
                        Name = file.Name,
                        IsDirectory = false,
                        FullPath = file.FullPath
                    });
                }

                rootChildCount = table2List.Count;

                int nextIndex = table2List.Count;
                foreach (var dir in root.Dirs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    int dirIndex = FindRootDirIndex(table2List, dir.Name, rootChildCount);
                    AddDirChildrenRecursive(dir, dirIndex, table2List, ref nextIndex);
                }
            }

            var stringSet = new HashSet<string>(StringComparer.Ordinal);
            if (hasTab2)
            {
                foreach (var t in table2List)
                    stringSet.Add(t.Name);
            }
            foreach (var b in blocks)
                stringSet.Add(b.Name);

            var allStrings = new List<string>(stringSet.Count + 1);
            string datNameLower = datName.ToLowerInvariant();
            allStrings.Add(datNameLower);
            foreach (var s in stringSet)
            {
                if (!string.Equals(s, datNameLower, StringComparison.Ordinal))
                    allStrings.Add(s);
            }

            var stringOffsets = new Dictionary<string, int>(allStrings.Count, StringComparer.Ordinal);
            var stringPoolBytes = BuildStringPool(allStrings, stringOffsets);

            uint datNameOff = (uint)(stringOffsets.TryGetValue(datNameLower, out var dno) ? dno : 0);

            int table2Count = hasTab2 ? table2List.Count : 0;
            int blockCount = blocks.Count;

            int[] fstsEntryCounts = new int[blockCount];
            for (int i = 0; i < blockCount; i++)
                fstsEntryCounts[i] = blocks[i].Paths.Count;

            long headerSize = 0x28;

            long table1Offset = Align16(headerSize);
            long table1Size = 32;
            long regionStart = table1Offset + table1Size;

            long table2Offset = 0;
            long table2Size = 0;
            if (hasTab2)
            {
                table2Offset = Align16(regionStart);
                table2Size = table2Count * 24L;
                regionStart = table2Offset + table2Size;
            }

            long table3Offset = 0;
            long table3Size = 0;
            bool hasTab3 = blockCount > 0;
            if (hasTab3)
            {
                table3Offset = Align16(regionStart);
                table3Size = 4 + 4L * blockCount + 20L * blockCount;
                regionStart = table3Offset + table3Size;
            }
            else
            {
                table3Offset = Align16(regionStart);
                table3Size = 4;
                regionStart = table3Offset + table3Size;
            }

            if (!hasTab2)
            {
                table2Offset = table3Offset;
            }

            long stringPoolOffset = Align16(regionStart);
            long stringPoolSize = stringPoolBytes.Length;
            long dataOffset = AlignData2048(stringPoolOffset + stringPoolSize);

            var fstsOffsets = new long[blockCount];
            var fstsSizes = new int[blockCount];

            using var fsOut = new FileStream(outputDatPath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                BufferSize = 1 << 20
            });

            fsOut.Position = table1Offset;

            uint[] tab1 = new uint[8];

            if (datIndex.Tab1 != null && datIndex.Tab1.Count >= 4)
            {
                for (int i = 0; i < 4; i++)
                    tab1[4 + i] = datIndex.Tab1[i];
            }

            tab1[0] = datNameOff;

            for (int i = 0; i < 8; i++)
                WriteUInt32(fsOut, tab1[i]);
                
            if (hasTab2)
            {
                fsOut.Position = table2Offset;
                foreach (var e in table2List)
                {
                    uint type = e.IsDirectory ? 1u : 0u;
                    uint nameOff = (uint)(stringOffsets.TryGetValue(e.Name, out var no) ? no : 0);

                    uint field08 = 0, field0C = 0, field10 = 0, field14 = 0;

                    if (e.IsDirectory)
                    {
                        field08 = (uint)e.ChildCount;
                        field0C = (uint)e.ChildStart;
                    }

                    WriteUInt32(fsOut, type);
                    WriteUInt32(fsOut, nameOff);
                    WriteUInt32(fsOut, field08);
                    WriteUInt32(fsOut, field0C);
                    WriteUInt32(fsOut, field10);
                    WriteUInt32(fsOut, field14);
                }
            }

            if (hasTab3)
            {
                fsOut.Position = table3Offset;
                WriteUInt32(fsOut, (uint)blockCount);

                long pointersPos = fsOut.Position;
                for (int i = 0; i < blockCount; i++)
                    WriteUInt32(fsOut, 0);

                long indexBase = pointersPos + blockCount * 4L;

                fsOut.Position = pointersPos;
                for (int i = 0; i < blockCount; i++)
                    WriteUInt32(fsOut, (uint)(indexBase + i * 20 - table3Offset));

                fsOut.Position = indexBase;
                for (int i = 0; i < blockCount; i++)
                {
                    var block = blocks[i];
                    uint nameOff = (uint)(stringOffsets.TryGetValue(block.Name, out var no) ? no : 0);
                    WriteUInt32(fsOut, nameOff);
                    WriteUInt32(fsOut, 0);
                    WriteUInt32(fsOut, 0);
                    WriteUInt32(fsOut, 0);
                    WriteUInt32(fsOut, (uint)fstsEntryCounts[i]);
                }
            }
            else
            {
                fsOut.Position = table3Offset;
                WriteUInt32(fsOut, 0);
            }

            fsOut.Position = stringPoolOffset;
            WriteBytes(fsOut, stringPoolBytes);

            long cursor = dataOffset;

            if (hasTab2)
            {
                for (int i = 0; i < table2List.Count; i++)
                {
                    var e = table2List[i];
                    if (e.IsDirectory || e.FullPath == null)
                        continue;

                    fsOut.Position = cursor;

                    if (!sources.TryGetValue(e.FullPath, out var src))
                        throw new InvalidOperationException("缺少路径源信息: " + e.FullPath);

                    var info = WriteDataFromSource(fsOut, src);

                    e.DataOffset = info.Offset;
                    e.CompressedSize = info.CompressedSize;
                    e.UncompressedSize = info.UncompressedSize;
                    table2List[i] = e;

                    cursor += info.StoredSize;

                    cursor = AlignData2048(cursor); // 0x800 对齐
                }

                fsOut.Position = table2Offset;
                foreach (var e in table2List)
                {
                    uint type = e.IsDirectory ? 1u : 0u;
                    uint nameOff = (uint)(stringOffsets.TryGetValue(e.Name, out var no) ? no : 0);

                    uint field08 = 0, field0C = 0, field10 = 0, field14 = 0;

                    if (e.IsDirectory)
                    {
                        field08 = (uint)e.ChildCount;
                        field0C = (uint)e.ChildStart;
                    }
                    else
                    {
                        field0C = (uint)e.DataOffset;
                        field10 = (uint)e.UncompressedSize;
                        field14 = (uint)e.CompressedSize;
                    }

                    WriteUInt32(fsOut, type);
                    WriteUInt32(fsOut, nameOff);
                    WriteUInt32(fsOut, field08);
                    WriteUInt32(fsOut, field0C);
                    WriteUInt32(fsOut, field10);
                    WriteUInt32(fsOut, field14);
                }
            }

            for (int bi = 0; bi < blockCount; bi++)
            {
                var block = blocks[bi];
                cursor = AlignData2048(cursor);
                long fstsOffset = cursor;
                fsOut.Position = fstsOffset;

                int entryCount = block.Paths.Count;

                WriteBytes(fsOut, FstsMagic);
                WriteUInt32(fsOut, (uint)entryCount);
                WriteUInt32(fsOut, 0x20);
                WriteUInt32(fsOut, 0);

                WriteUInt32(fsOut, 0);
                WriteUInt32(fsOut, 0);
                WriteUInt32(fsOut, 0);
                WriteUInt32(fsOut, 0);

                long entriesBase = fstsOffset + 0x20;
                fsOut.Position = entriesBase;
                for (int i = 0; i < entryCount; i++)
                {
                    WriteUInt32(fsOut, 0);
                    WriteUInt32(fsOut, 0);
                    WriteUInt32(fsOut, 0);
                    WriteUInt32(fsOut, 0);
                }

                var fstsNames = new Dictionary<string, int>(entryCount, StringComparer.Ordinal);
                var fstsSpBytes = BuildStringPool(block.Paths, fstsNames);

                long stringPoolPos = entriesBase + entryCount * 16;
                fsOut.Position = stringPoolPos;
                WriteBytes(fsOut, fstsSpBytes);
                long afterSp = fsOut.Position;
                long dataLocalCursor = Align16(afterSp);

                fsOut.Position = fstsOffset + 0x0C;
                WriteUInt32(fsOut, (uint)(stringPoolPos - fstsOffset));

                fsOut.Position = fstsOffset + 0x10;
                WriteUInt32(fsOut, (uint)fstsSpBytes.Length);

                for (int i = 0; i < entryCount; i++)
                {
                    string path = block.Paths[i];
                    if (!sources.TryGetValue(path, out var src))
                        throw new InvalidOperationException("缺少路径源信息(FSTS): " + path);

                    dataLocalCursor = Align16(dataLocalCursor);
                    long localDataOff = dataLocalCursor - fstsOffset;
                    fsOut.Position = dataLocalCursor;

                    var info = WriteDataFromSource(fsOut, src);
                    dataLocalCursor += info.StoredSize;

                    long entPos = entriesBase + i * 16;
                    fsOut.Position = entPos;
                    uint nameOff2 = (uint)(fstsNames.TryGetValue(path, out var off) ? off : 0);
                    WriteUInt32(fsOut, nameOff2);
                    WriteUInt32(fsOut, (uint)localDataOff);
                    WriteUInt32(fsOut, (uint)info.UncompressedSize);
                    WriteUInt32(fsOut, (uint)info.CompressedSize);
                }

                long fstsEnd = dataLocalCursor;
                fstsOffsets[bi] = fstsOffset;
                fstsSizes[bi] = (int)(fstsEnd - fstsOffset);
                cursor = fstsEnd;
            }

            if (hasTab3)
            {
                long indexBase = table3Offset + 4 + blockCount * 4;
                for (int i = 0; i < blockCount; i++)
                {
                    long idxPos = indexBase + i * 20;
                    fsOut.Position = idxPos + 8;
                    WriteUInt32(fsOut, (uint)fstsOffsets[i]);
                    WriteUInt32(fsOut, (uint)fstsSizes[i]);
                }
            }

            fsOut.Position = 0;
            WriteBytes(fsOut, PidxMagic);
            WriteUInt32(fsOut, (uint)table1Offset);
            WriteUInt32(fsOut, 1);
            WriteUInt32(fsOut, (uint)table2Offset);
            WriteUInt32(fsOut, hasTab2 ? (uint)table2Count : 0u);
            WriteUInt32(fsOut, hasTab2 ? (uint)rootChildCount : 0u);
            WriteUInt32(fsOut, (uint)table3Offset);
            WriteUInt32(fsOut, (uint)table3Size);
            WriteUInt32(fsOut, (uint)stringPoolOffset);
            WriteUInt32(fsOut, (uint)stringPoolSize);

            fsOut.Flush();

            int usedModified = 0;
            foreach (var p in allPaths)
            {
                if (sources.TryGetValue(p, out var src) && src.HasModified)
                    usedModified++;
            }
            int usedOriginal = allPaths.Count - usedModified;

            return new DatRebuildResult
            {
                TotalPaths = allPaths.Count,
                ModifiedCount = usedModified,
                OriginalCount = usedOriginal
            };
        }

        static void InsertPathToTree(DirNode root, string path)
        {
            var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            DirNode current = root;

            for (int i = 0; i < parts.Length; i++)
            {
                bool isLast = i == parts.Length - 1;
                string part = parts[i];

                if (isLast)
                {
                    if (!current.Files.ContainsKey(part))
                        current.Files[part] = new FileNode(part, path);
                }
                else
                {
                    if (!current.Dirs.TryGetValue(part, out var next))
                    {
                        next = new DirNode(part);
                        current.Dirs[part] = next;
                    }
                    current = next;
                }
            }
        }

        static int FindRootDirIndex(List<Table2Build> table2, string name, int rootChildCount)
        {
            for (int i = 0; i < rootChildCount; i++)
            {
                if (table2[i].IsDirectory &&
                    string.Equals(table2[i].Name, name, StringComparison.Ordinal))
                    return i;
            }
            throw new InvalidOperationException("未在根目录第一层找到目录: " + name);
        }

        static void AddDirChildrenRecursive(
            DirNode dir,
            int dirIndex,
            List<Table2Build> table2,
            ref int nextIndex)
        {
            int childStart = nextIndex;

            foreach (var childDir in dir.Dirs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                table2.Add(new Table2Build
                {
                    Name = childDir.Name,
                    IsDirectory = true
                });
                nextIndex++;
            }

            foreach (var file in dir.Files.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                table2.Add(new Table2Build
                {
                    Name = file.Name,
                    IsDirectory = false,
                    FullPath = file.FullPath
                });
                nextIndex++;
            }

            int childCount = nextIndex - childStart;

            var me = table2[dirIndex];
            me.ChildStart = childStart;
            me.ChildCount = childCount;
            table2[dirIndex] = me;

            int subDirStartIndex = childStart;
            foreach (var childDir in dir.Dirs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                int idx = subDirStartIndex++;
                AddDirChildrenRecursive(childDir, idx, table2, ref nextIndex);
            }
        }

        static void AddDirRecursive(DirNode dir, List<Table2Build> table2)
        {
            int myIndex = table2.Count;
            var entry = new Table2Build
            {
                Name = dir.Name,
                IsDirectory = true
            };
            table2.Add(entry);

            int childStart = table2.Count;

            foreach (var childDir in dir.Dirs.Values.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                AddDirRecursive(childDir, table2);

            foreach (var file in dir.Files.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                table2.Add(new Table2Build
                {
                    Name = file.Name,
                    IsDirectory = false,
                    FullPath = file.FullPath
                });
            }

            int childCount = table2.Count - childStart;

            var me = table2[myIndex];
            me.ChildStart = childStart;
            me.ChildCount = childCount;
            table2[myIndex] = me;
        }
    }
}