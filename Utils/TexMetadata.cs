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
        public ushort Cw { get; init; }
        public ushort Ch { get; init; }
        public byte[] Unknown { get; init; } = Array.Empty<byte>();

        // Runtime-only values read from TEX or inferred from the PNG.
        public uint RealWidth { get; init; }
        public uint RealHeight { get; init; }
        public uint BaseOffset { get; init; } = 0x20;

        public IReadOnlyList<TexEntryMetadata> Sprites { get; init; } = Array.Empty<TexEntryMetadata>();
        public TexEntryMetadata? Clut { get; init; }
    }

    internal sealed class TexMetadataDocument
    {
        readonly Dictionary<string, TexFileMetadata> _files;

        public TexMetadataDocument(IEnumerable<TexFileMetadata> files)
        {
            _files = new Dictionary<string, TexFileMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string path = NormalizePath(file.Path);
                if (path.Length == 0)
                    throw new InvalidDataException("tex.xml contains an empty path.");
                if (!_files.TryAdd(path, file))
                    throw new InvalidDataException($"tex.xml contains a duplicate path: {path}");
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

            XElement? root = document.Root;
            if (root == null || root.Name != "tex")
                throw new InvalidDataException("tex.xml root element must be <tex>.");

            return new TexMetadataDocument(root.Elements("file").Select(ReadFile));
        }

        public static void Save(string path, IEnumerable<TexFileMetadata> files)
        {
            var root = new XElement("tex");
            foreach (var file in files.OrderBy(x => NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase))
                root.Add(WriteFile(file));

            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string tempPath = path + ".tmp";
            try
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = new UTF8Encoding(false),
                    Indent = true,
                    NewLineChars = Environment.NewLine,
                    OmitXmlDeclaration = true
                };
                using (var writer = XmlWriter.Create(tempPath, settings))
                    new XDocument(root).Save(writer);
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
            string path = NormalizePath(ReadRequired(element, "path"));
            try
            {
                var sprites = element.Elements("sprite").Select(x => ReadEntry(x, false)).ToArray();
                if (sprites.Length == 0)
                    throw new InvalidDataException("No sprite entries were found.");

                XElement? clut = element.Element("clut");
                return new TexFileMetadata
                {
                    Path = path,
                    Cw = ReadUInt16(element, "cw"),
                    Ch = ReadUInt16(element, "ch"),
                    Unknown = ReadHex(element, "unknown", 12),
                    Sprites = sprites,
                    Clut = clut == null ? null : ReadEntry(clut, true)
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
                new XAttribute("cw", file.Cw),
                new XAttribute("ch", file.Ch),
                new XAttribute("unknown", Convert.ToHexString(file.Unknown)));

            foreach (var sprite in file.Sprites)
                element.Add(WriteEntry("sprite", sprite, false));
            if (file.Clut != null)
                element.Add(WriteEntry("clut", file.Clut, true));
            return element;
        }

        static XElement WriteEntry(string name, TexEntryMetadata entry, bool isClut)
        {
            var element = new XElement(name,
                new XAttribute("offset", entry.RelativeOffset),
                new XAttribute("tbp", entry.Tbp),
                new XAttribute("tbw", entry.Tbw),
                new XAttribute("vram", entry.SharedVram),
                new XAttribute("x", entry.OffsetX),
                new XAttribute("y", entry.OffsetY));

            if (!isClut)
            {
                element.Add(
                    new XAttribute("psm", entry.Psm),
                    new XAttribute("w", entry.Width),
                    new XAttribute("h", entry.Height));
            }
            return element;
        }

        static TexEntryMetadata ReadEntry(XElement element, bool isClut) => new()
        {
            RelativeOffset = ReadUInt32(element, "offset"),
            Tbp = ReadUInt16(element, "tbp"),
            Psm = isClut ? (byte)0 : ReadByte(element, "psm"),
            Tbw = ReadUInt16(element, "tbw"),
            SharedVram = ReadUInt16(element, "vram"),
            OffsetX = ReadUInt16(element, "x"),
            OffsetY = ReadUInt16(element, "y"),
            Width = isClut ? (ushort)0 : ReadUInt16(element, "w"),
            Height = isClut ? (ushort)0 : ReadUInt16(element, "h")
        };

        static string ReadRequired(XElement element, string name) =>
            element.Attribute(name)?.Value ?? throw new InvalidDataException($"Missing attribute '{name}'.");

        static uint ReadUInt32(XElement element, string name) =>
            uint.Parse(ReadRequired(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static ushort ReadUInt16(XElement element, string name) =>
            ushort.Parse(ReadRequired(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static byte ReadByte(XElement element, string name) =>
            byte.Parse(ReadRequired(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static byte[] ReadHex(XElement element, string name, int expectedLength)
        {
            byte[] value = Convert.FromHexString(ReadRequired(element, name));
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
                bool hasClut = ReadUInt16(data, 0x26) != 0;
                int entryCount = checked(count + (hasClut ? 1 : 0));
                if (count == 0 || 0x28L + entryCount * 20L > data.Length)
                    throw new InvalidDataException("Invalid TEX entry table.");

                var sprites = new TexEntryMetadata[count];
                for (int i = 0; i < count; i++)
                    sprites[i] = ReadEntry(data, 0x28 + i * 20);

                metadata = new TexFileMetadata
                {
                    Path = path.Replace('\\', '/').TrimStart('/'),
                    Cw = ReadUInt16(data, 0x04),
                    Ch = ReadUInt16(data, 0x06),
                    Unknown = data.AsSpan(0x08, 12).ToArray(),
                    RealWidth = ReadUInt32(data, 0x14),
                    RealHeight = ReadUInt32(data, 0x18),
                    BaseOffset = ReadUInt32(data, 0x20),
                    Sprites = sprites,
                    Clut = hasClut ? ReadEntry(data, 0x28 + count * 20) : null
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
