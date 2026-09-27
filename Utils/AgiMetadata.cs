// Utils/AgiMetadata.cs
// ---------------------------------------------------------------
// agi.xml 元数据文档: Utils.AgiMetadataDocument
//
// 提取时随 PNG 一起写出；回封时从 modified/agi.xml 读取。
// modified 里没有 agi.xml 时，AGI 一律按 8bpp 完整重建(旧流程)。
// ---------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace GalaxyAngel2Localization.Utils
{
    internal sealed class AgiFileMetadata
    {
        public string Path { get; init; } = string.Empty;
        public int BitsPerPixel { get; init; }

        // 0x30 头里仅有的 4 个每文件字段（VRAM 簿记，其余均为常量/可推导）
        public ushort TextureBase { get; init; }   // 0x0C: 纹理 VRAM 基址 (TEX0 的 TBP0)
        public uint Reg3 { get; init; }            // 0x14
        public uint ClutBase { get; init; }        // 0x20: 调色板 VRAM 基址
        public uint ClutReg3 { get; init; }        // 0x28: 官方工具的调色板分配计数
    }

    internal sealed class AgiMetadataDocument
    {
        readonly Dictionary<string, AgiFileMetadata> _files;

        public AgiMetadataDocument(IEnumerable<AgiFileMetadata> files)
        {
            _files = new Dictionary<string, AgiFileMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string path = NormalizePath(file.Path);
                if (path.Length == 0)
                    throw new InvalidDataException("agi.xml contains an empty path.");
                if (!_files.TryAdd(path, file))
                    throw new InvalidDataException($"agi.xml contains a duplicate path: {path}");
            }
        }

        public bool TryGetFile(string path, out AgiFileMetadata metadata) =>
            _files.TryGetValue(NormalizePath(path), out metadata!);

        public static AgiMetadataDocument Load(string path)
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path, LoadOptions.SetLineInfo);
            }
            catch (Exception ex) when (ex is IOException or XmlException)
            {
                throw new InvalidDataException($"Unable to read agi.xml: {ex.Message}", ex);
            }

            XElement? root = document.Root;
            if (root == null || root.Name != "agi")
                throw new InvalidDataException("agi.xml root element must be <agi>.");

            return new AgiMetadataDocument(root.Elements("file").Select(ReadFile));
        }

        public static void Save(string path, IEnumerable<AgiFileMetadata> files)
        {
            var root = new XElement("agi");
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
                    Encoding = new System.Text.UTF8Encoding(false),
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

        static AgiFileMetadata ReadFile(XElement element)
        {
            string path = NormalizePath(ReadRequired(element, "path"));
            try
            {
                return new AgiFileMetadata
                {
                    Path = path,
                    BitsPerPixel = ReadInt(element, "bpp"),
                    TextureBase = ReadUInt16(element, "tb"),
                    Reg3 = ReadUInt32(element, "reg3"),
                    ClutBase = ReadUInt32(element, "cbp"),
                    ClutReg3 = ReadUInt32(element, "creg3")
                };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid AGI metadata for '{path}': {ex.Message}", ex);
            }
        }

        static XElement WriteFile(AgiFileMetadata file)
        {
            var element = new XElement("file",
                new XAttribute("path", NormalizePath(file.Path)),
                new XAttribute("bpp", file.BitsPerPixel));
            if (file.TextureBase != 0)
                element.Add(new XAttribute("tb", file.TextureBase));
            if (file.Reg3 != 0)
                element.Add(new XAttribute("reg3", file.Reg3));
            if (file.ClutBase != 0)
                element.Add(new XAttribute("cbp", file.ClutBase));
            if (file.ClutReg3 != 0)
                element.Add(new XAttribute("creg3", file.ClutReg3));
            return element;
        }

        static ushort ReadUInt16(XElement element, string name)
        {
            string? value = (string?)element.Attribute(name);
            return value == null ? (ushort)0 : ushort.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        static uint ReadUInt32(XElement element, string name)
        {
            string? value = (string?)element.Attribute(name);
            return value == null ? 0 : uint.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        static string ReadRequired(XElement element, string name) =>
            element.Attribute(name)?.Value ?? throw new InvalidDataException($"Missing attribute '{name}'.");

        static int ReadInt(XElement element, string name) =>
            int.Parse(ReadRequired(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    }
}
