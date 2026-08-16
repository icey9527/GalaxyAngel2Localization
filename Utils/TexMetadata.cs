using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace GalaxyAngel2Localization.Utils
{
    internal sealed class TexEntryMetadata
    {
        public uint RelativeOffset { get; init; }
        public ushort Tbp { get; init; }
        public byte Psm { get; init; }
        public byte Unknown { get; init; }
        public ushort Tbw { get; init; }
        public ushort SharedVram { get; init; }
        public ushort OffsetX { get; init; }
        public ushort OffsetY { get; init; }
        public ushort Width { get; init; }
        public ushort Height { get; init; }
    }

    internal sealed class TexFileMetadata
    {
        public string Path { get; init; } = string.Empty;
        public int FileLength { get; init; }
        public ushort Cw { get; init; }
        public ushort Ch { get; init; }
        public byte[] Unknown1 { get; init; } = Array.Empty<byte>();
        public uint RealWidth { get; init; }
        public uint RealHeight { get; init; }
        public byte[] Unknown2 { get; init; } = Array.Empty<byte>();
        public uint BaseOffset { get; init; }
        public ushort HasClut { get; init; }
        public IReadOnlyList<TexEntryMetadata> Sprites { get; init; } = Array.Empty<TexEntryMetadata>();
        public TexEntryMetadata? Clut { get; init; }
    }

    internal sealed class TexMetadataDocument
    {
        const int CurrentVersion = 1;
        readonly Dictionary<string, TexFileMetadata> _files;

        public TexMetadataDocument(IEnumerable<TexFileMetadata> files)
        {
            _files = new Dictionary<string, TexFileMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string path = NormalizePath(file.Path);
                if (path.Length == 0)
                    throw new InvalidDataException("TEX metadata contains an empty path.");
                if (!_files.TryAdd(path, file))
                    throw new InvalidDataException($"TEX metadata contains a duplicate path: {path}");
            }
        }

        public bool TryGetFile(string path, out TexFileMetadata metadata) =>
            _files.TryGetValue(NormalizePath(path), out metadata!);

        public static TexMetadataDocument Load(string path)
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path, LoadOptions.SetLineInfo);
            }
            catch (Exception ex) when (ex is IOException or XmlException)
            {
                throw new InvalidDataException($"Unable to read tex.xml: {ex.Message}", ex);
            }

            var root = document.Root;
            if (root == null || root.Name != "texMetadata")
                throw new InvalidDataException("tex.xml root element must be <texMetadata>.");
            if (ReadInt(root, "version") != CurrentVersion)
                throw new InvalidDataException($"Unsupported tex.xml version; expected {CurrentVersion}.");

            var files = new List<TexFileMetadata>();
            foreach (var element in root.Elements("file"))
                files.Add(ReadFile(element));

            return new TexMetadataDocument(files);
        }

        public static void Save(string path, IEnumerable<TexFileMetadata> files)
        {
            var root = new XElement("texMetadata", new XAttribute("version", CurrentVersion));
            foreach (var file in files.OrderBy(x => NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase))
                root.Add(WriteFile(file));

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tempPath = path + ".tmp";
            try
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = true,
                    NewLineChars = Environment.NewLine
                };
                using (var writer = XmlWriter.Create(tempPath, settings))
                    new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(writer);

                File.Move(tempPath, path, true);
            }
            finally
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        static TexFileMetadata ReadFile(XElement element)
        {
            string path = NormalizePath(ReadString(element, "path"));
            try
            {
                var sprites = element.Elements("sprite")
                    .Select(x => new
                    {
                        Index = ReadInt(x, "index"),
                        Entry = ReadEntry(x)
                    })
                    .OrderBy(x => x.Index)
                    .ToArray();

                for (int i = 0; i < sprites.Length; i++)
                {
                    if (sprites[i].Index != i)
                        throw new InvalidDataException("Sprite indices must be contiguous and start at zero.");
                }

                ushort spriteCount = ReadUInt16(element, "sprites");
                if (sprites.Length != spriteCount)
                    throw new InvalidDataException($"Expected {spriteCount} sprites but found {sprites.Length}.");

                ushort hasClut = ReadUInt16(element, "hasClut");
                var clutElement = element.Element("clut");
                if ((hasClut != 0) != (clutElement != null))
                    throw new InvalidDataException("CLUT presence does not match hasClut.");

                return new TexFileMetadata
                {
                    Path = path,
                    FileLength = ReadInt(element, "fileLength"),
                    Cw = ReadUInt16(element, "cw"),
                    Ch = ReadUInt16(element, "ch"),
                    Unknown1 = ReadHex(element, "unknown1", 12),
                    RealWidth = ReadUInt32(element, "rw"),
                    RealHeight = ReadUInt32(element, "rh"),
                    Unknown2 = ReadHex(element, "unknown2", 4),
                    BaseOffset = ReadUInt32(element, "baseOffset"),
                    HasClut = hasClut,
                    Sprites = sprites.Select(x => x.Entry).ToArray(),
                    Clut = clutElement == null ? null : ReadEntry(clutElement)
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid TEX metadata for '{path}': {ex.Message}", ex);
            }
        }

        static XElement WriteFile(TexFileMetadata file)
        {
            var element = new XElement("file",
                new XAttribute("path", NormalizePath(file.Path)),
                new XAttribute("fileLength", file.FileLength),
                new XAttribute("cw", file.Cw),
                new XAttribute("ch", file.Ch),
                new XAttribute("rw", file.RealWidth),
                new XAttribute("rh", file.RealHeight),
                new XAttribute("baseOffset", file.BaseOffset),
                new XAttribute("sprites", file.Sprites.Count),
                new XAttribute("hasClut", file.HasClut),
                new XAttribute("unknown1", Convert.ToHexString(file.Unknown1)),
                new XAttribute("unknown2", Convert.ToHexString(file.Unknown2)));

            for (int i = 0; i < file.Sprites.Count; i++)
                element.Add(WriteEntry("sprite", file.Sprites[i], i));
            if (file.Clut != null)
                element.Add(WriteEntry("clut", file.Clut, null));
            return element;
        }

        static XElement WriteEntry(string name, TexEntryMetadata entry, int? index)
        {
            var element = new XElement(name);
            if (index.HasValue)
                element.Add(new XAttribute("index", index.Value));
            element.Add(
                new XAttribute("relativeOffset", entry.RelativeOffset),
                new XAttribute("tbp", entry.Tbp),
                new XAttribute("psm", entry.Psm),
                new XAttribute("unknown", entry.Unknown),
                new XAttribute("tbw", entry.Tbw),
                new XAttribute("sharedVram", entry.SharedVram),
                new XAttribute("offsetX", entry.OffsetX),
                new XAttribute("offsetY", entry.OffsetY),
                new XAttribute("width", entry.Width),
                new XAttribute("height", entry.Height));
            return element;
        }

        static TexEntryMetadata ReadEntry(XElement element) => new()
        {
            RelativeOffset = ReadUInt32(element, "relativeOffset"),
            Tbp = ReadUInt16(element, "tbp"),
            Psm = ReadByte(element, "psm"),
            Unknown = ReadByte(element, "unknown"),
            Tbw = ReadUInt16(element, "tbw"),
            SharedVram = ReadUInt16(element, "sharedVram"),
            OffsetX = ReadUInt16(element, "offsetX"),
            OffsetY = ReadUInt16(element, "offsetY"),
            Width = ReadUInt16(element, "width"),
            Height = ReadUInt16(element, "height")
        };

        static string ReadString(XElement element, string name) =>
            element.Attribute(name)?.Value ?? throw new InvalidDataException($"Missing attribute '{name}'.");

        static int ReadInt(XElement element, string name) =>
            int.Parse(ReadString(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static uint ReadUInt32(XElement element, string name) =>
            uint.Parse(ReadString(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static ushort ReadUInt16(XElement element, string name) =>
            ushort.Parse(ReadString(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static byte ReadByte(XElement element, string name) =>
            byte.Parse(ReadString(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static byte[] ReadHex(XElement element, string name, int expectedLength)
        {
            byte[] value = Convert.FromHexString(ReadString(element, name));
            if (value.Length != expectedLength)
                throw new InvalidDataException($"Attribute '{name}' must contain {expectedLength} bytes.");
            return value;
        }

        static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    }

    internal static class TexStructure
    {
        public static bool TryParse(byte[] data, string path, out TexFileMetadata? metadata, out string? error)
        {
            metadata = null;
            error = null;
            try
            {
                if (data.Length < 0x28 || Encoding.ASCII.GetString(data, 0, 4) != "TEX ")
                    throw new InvalidDataException("Invalid TEX signature or truncated header.");

                ushort count = ReadUInt16(data, 0x24);
                ushort hasClut = ReadUInt16(data, 0x26);
                int entryCount = checked(count + (hasClut == 0 ? 0 : 1));
                if (count == 0 || 0x28L + entryCount * 20L > data.Length)
                    throw new InvalidDataException("Invalid TEX entry table.");

                var sprites = new TexEntryMetadata[count];
                for (int i = 0; i < count; i++)
                    sprites[i] = ReadEntry(data, 0x28 + i * 20);

                metadata = new TexFileMetadata
                {
                    Path = path.Replace('\\', '/').TrimStart('/'),
                    FileLength = data.Length,
                    Cw = ReadUInt16(data, 0x04),
                    Ch = ReadUInt16(data, 0x06),
                    Unknown1 = data.AsSpan(0x08, 12).ToArray(),
                    RealWidth = ReadUInt32(data, 0x14),
                    RealHeight = ReadUInt32(data, 0x18),
                    Unknown2 = data.AsSpan(0x1C, 4).ToArray(),
                    BaseOffset = ReadUInt32(data, 0x20),
                    HasClut = hasClut,
                    Sprites = sprites,
                    Clut = hasClut == 0 ? null : ReadEntry(data, 0x28 + count * 20)
                };
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
            {
                error = ex.Message;
                return false;
            }
        }

        static TexEntryMetadata ReadEntry(byte[] data, int offset) => new()
        {
            RelativeOffset = ReadUInt32(data, offset),
            Tbp = ReadUInt16(data, offset + 4),
            Psm = data[offset + 6],
            Unknown = data[offset + 7],
            Tbw = ReadUInt16(data, offset + 8),
            SharedVram = ReadUInt16(data, offset + 10),
            OffsetX = ReadUInt16(data, offset + 12),
            OffsetY = ReadUInt16(data, offset + 14),
            Width = ReadUInt16(data, offset + 16),
            Height = ReadUInt16(data, offset + 18)
        };

        static ushort ReadUInt16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

        static uint ReadUInt32(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }
}
