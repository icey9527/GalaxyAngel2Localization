using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GalaxyAngel2Localization.Utils
{
    internal sealed class IsoEntry
    {
        /// <summary>ISO 内相对路径，用 / 分隔</summary>
        public string Path { get; set; } = string.Empty;

        public bool IsDirectory { get; set; }

        public uint Lba { get; set; }

        public uint Size { get; set; }

        public override string ToString() => Path;
    }

    internal static class IsoImage
    {
        private const int SectorSize = 2048;

        public static List<IsoEntry> Load(string isoPath)
        {
            using var fs = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Load(fs);
        }

        public static List<IsoEntry> Load(FileStream fs)
        {
            var result = new List<IsoEntry>();

            // Primary Volume Descriptor 在 LBA 16
            var pvd = new byte[SectorSize];
            fs.Position = 16 * SectorSize;
            int read = fs.Read(pvd, 0, pvd.Length);
            if (read != SectorSize)
                throw new InvalidDataException("无法读取 Primary Volume Descriptor");

            if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001")
                throw new InvalidDataException("不是有效的 ISO9660 镜像");

            // Root Directory Record 偏移 156
            int rootOffset = 156;
            byte lenDr = pvd[rootOffset];
            if (lenDr <= 0)
                throw new InvalidDataException("Root Directory Record 无效");

            uint rootLba = BitConverter.ToUInt32(pvd, rootOffset + 2);
            uint rootSize = BitConverter.ToUInt32(pvd, rootOffset + 10);

            var root = new IsoEntry
            {
                Path = string.Empty,
                IsDirectory = true,
                Lba = rootLba,
                Size = rootSize
            };

            ReadDirectory(fs, root, result);
            return result;
        }

        private static void ReadDirectory(FileStream fs, IsoEntry dir, List<IsoEntry> result)
        {
            if (!dir.IsDirectory)
                return;

            long dirStart = (long)dir.Lba * SectorSize;
            long length = dir.Size;
            if (length <= 0)
                return;

            var buffer = new byte[length];
            fs.Position = dirStart;
            int read = fs.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                return;

            int offset = 0;
            while (offset < read)
            {
                byte lenDr = buffer[offset];

                if (lenDr == 0)
                {
                    int nextSector = (offset / SectorSize + 1) * SectorSize;
                    if (nextSector <= offset || nextSector >= read)
                        break;
                    offset = nextSector;
                    continue;
                }

                if (offset + lenDr > read)
                    break;

                ParseDirectoryRecord(buffer, offset, lenDr, dir, fs, result);
                offset += lenDr;
            }
        }

        private static void ParseDirectoryRecord(
            byte[] buffer,
            int offset,
            int lenDr,
            IsoEntry parent,
            FileStream fs,
            List<IsoEntry> result)
        {
            uint lba = BitConverter.ToUInt32(buffer, offset + 2);
            uint dataLength = BitConverter.ToUInt32(buffer, offset + 10);
            byte flags = buffer[offset + 25];
            bool isDirectory = (flags & 0x02) != 0;
            byte nameLen = buffer[offset + 32];

            string name;
            if (nameLen == 1)
            {
                byte id = buffer[offset + 33];
                if (id == 0) name = ".";
                else if (id == 1) name = "..";
                else name = ((char)id).ToString();
            }
            else
            {
                name = Encoding.ASCII.GetString(buffer, offset + 33, nameLen);
            }

            if (name == "." || name == "..")
                return;

            if (!isDirectory)
            {
                int semicolon = name.IndexOf(';');
                if (semicolon >= 0)
                    name = name.Substring(0, semicolon);
                name = name.TrimEnd('.');
            }

            string relPath = string.IsNullOrEmpty(parent.Path)
                ? name
                : parent.Path + "/" + name;

            var entry = new IsoEntry
            {
                Path = relPath,
                IsDirectory = isDirectory,
                Lba = lba,
                Size = dataLength
            };

            if (isDirectory)
                ReadDirectory(fs, entry, result);
            else
                result.Add(entry);
        }
    }
}