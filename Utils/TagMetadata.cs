// Utils/TagMetadata.cs
// ---------------------------------------------------------------
// tag.xml 元数据文档: Utils.TagMetadataDocument
//
// 与 tex.xml 同一套工作流: 提取时随 PNG 一起写出，回封时
// 从 modified/tag.xml 读取，与原始 .tag 的解析结果交叉校验。
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
    internal sealed class TagFileMetadata
    {
        public string Path { get; init; } = string.Empty;
        public IReadOnlyList<TagImageLayout> Images { get; init; } = Array.Empty<TagImageLayout>();
    }

    internal sealed class TagMetadataDocument
    {
        readonly Dictionary<string, TagFileMetadata> _files;

        public TagMetadataDocument(IEnumerable<TagFileMetadata> files)
        {
            _files = new Dictionary<string, TagFileMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string path = NormalizePath(file.Path);
                if (path.Length == 0)
                    throw new InvalidDataException("tag.xml contains an empty path.");
                if (!_files.TryAdd(path, file))
                    throw new InvalidDataException($"tag.xml contains a duplicate path: {path}");
            }
        }

        public bool TryGetFile(string path, out TagFileMetadata metadata) =>
            _files.TryGetValue(NormalizePath(path), out metadata!);

        public static TagMetadataDocument Load(string path)
        {
            XDocument document;
            try
            {
                document = XDocument.Load(path, LoadOptions.SetLineInfo);
            }
            catch (Exception ex) when (ex is IOException or XmlException)
            {
                throw new InvalidDataException($"Unable to read tag.xml: {ex.Message}", ex);
            }

            XElement? root = document.Root;
            if (root == null || root.Name != "tag")
                throw new InvalidDataException("tag.xml root element must be <tag>.");

            return new TagMetadataDocument(root.Elements("file").Select(ReadFile));
        }

        public static void Save(string path, IEnumerable<TagFileMetadata> files)
        {
            var root = new XElement("tag");
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

        static TagFileMetadata ReadFile(XElement element)
        {
            string path = NormalizePath(ReadRequired(element, "path"));
            try
            {
                var images = element.Elements("image").Select((e, i) => ReadImage(e, i)).ToArray();
                if (images.Length == 0)
                    throw new InvalidDataException("No image entries were found.");

                return new TagFileMetadata { Path = path, Images = images };
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidDataException)
            {
                throw new InvalidDataException($"Invalid TAG metadata for '{path}': {ex.Message}", ex);
            }
        }

        static XElement WriteFile(TagFileMetadata file)
        {
            var element = new XElement("file", new XAttribute("path", NormalizePath(file.Path)));
            foreach (var image in file.Images)
                element.Add(WriteImage(image));
            return element;
        }

        static TagImageLayout ReadImage(XElement element, int index) => new()
        {
            Index = index,
            Width = ReadInt(element, "w"),
            Height = ReadInt(element, "h"),
            BitsPerPixel = ReadInt(element, "bpp")
        };

        static XElement WriteImage(TagImageLayout image) => new("image",
            new XAttribute("w", image.Width),
            new XAttribute("h", image.Height),
            new XAttribute("bpp", image.BitsPerPixel));

        static string ReadRequired(XElement element, string name) =>
            element.Attribute(name)?.Value ?? throw new InvalidDataException($"Missing attribute '{name}'.");

        static int ReadInt(XElement element, string name) =>
            int.Parse(ReadRequired(element, name), NumberStyles.None, CultureInfo.InvariantCulture);

        static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');
    }
}
