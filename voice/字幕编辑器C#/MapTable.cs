using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceSubEditor
{
    /// <summary>
    /// 映射码表 (font.tbl, 参考 char.py):
    ///   行格式 "889F=亚"; 文件为 UTF-16(BOM) 或 UTF-8。
    ///   码位 ≥ 阈值(默认 0x889F) 的区域是自制字形区(代理区), 翻译文本里的字
    ///   若 CP932 编码 ≥ 阈值 (或无法编码) 必须查表映射到代理字;
    ///   编码 &lt; 阈值的字符(ASCII/半角/部分符号)直接用 CP932 原样编码。
    ///   查不到的字 = 缺字, 报错。
    /// </summary>
    public class MapTable
    {
        public const int DefaultThreshold = 0x889F;

        /// <summary>翻译字 → 代理字(代理字本身是合法CP932字符, 编码后即自制字形码位)</summary>
        public readonly Dictionary<string, char> CharToProxy = new Dictionary<string, char>(StringComparer.Ordinal);
        /// <summary>码位 → 字 (仅信息展示用)</summary>
        public readonly Dictionary<int, char> CodeToChar = new Dictionary<int, char>();
        public int Threshold = DefaultThreshold;
        public string Path;

        public int Count { get { return CharToProxy.Count; } }

        static readonly Regex LineRe = new Regex(@"^\s*([0-9A-Fa-f]{2,4})\s*=\s*(.+?)\s*$");

        /// <summary>加载 font.tbl; 失败抛异常(消息给用户看)</summary>
        public static MapTable Load(string path)
        {
            byte[] raw = File.ReadAllBytes(path);
            string text = DecodeText(raw);
            var map = new MapTable { Path = path };
            int lineNo = 0;
            foreach (string rawLine in text.Split('\n'))
            {
                lineNo++;
                string line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("//")) continue;
                Match m = LineRe.Match(line);
                if (!m.Success) continue;
                int code = Convert.ToInt32(m.Groups[1].Value, 16);
                string rhs = m.Groups[2].Value;
                int c1 = rhs.IndexOf(';'); if (c1 >= 0) rhs = rhs.Substring(0, c1);
                int c2 = rhs.IndexOf("//"); if (c2 >= 0) rhs = rhs.Substring(0, c2);
                rhs = rhs.Trim();
                if (rhs.EnumerateRunes().Count() != 1)
                    throw new Exception(string.Format("码表第 {0} 行右侧不是单个字符: {1}", lineNo, line));
                if (code > 0xFFFF)
                    throw new Exception(string.Format("码表第 {0} 行码位超范围: {1:X}", lineNo, code));
                byte b1 = (byte)((code >> 8) & 0xFF), b2 = (byte)(code & 0xFF);
                // 代理字 = 该码位二字节按 CP932 解码得到的字符 (与 char.py load_map 一致)
                char proxy;
                if (!Cp932.TryDecodeWord((ushort)((b1 << 8) | b2), out proxy))
                    throw new Exception(string.Format("码表第 {0} 行码位 {1:X4} 不是有效CP932双字节编码", lineNo, code));
                map.CharToProxy[rhs] = proxy;
                map.CodeToChar[code] = rhs[0];
            }
            if (map.Count == 0)
                throw new Exception("码表里没有有效条目(格式: 889F=字)");
            return map;
        }

        static string DecodeText(byte[] raw)
        {
            // UTF-16 LE/BE BOM
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
                return Encoding.Unicode.GetString(raw, 2, raw.Length - 2);
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(raw, 2, raw.Length - 2);
            if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
                return Encoding.UTF8.GetString(raw, 3, raw.Length - 3);
            // 无 BOM: 先按严格 UTF-8 试
            try { return new UTF8Encoding(false, true).GetString(raw); }
            catch (DecoderFallbackException) { return Cp932.Decode(raw); }
        }

        /// <summary>单字符映射: 编码&lt;阈值 → 原样; 否则查表; 查不到 → badChars 记录并返回 '?'</summary>
        public char MapChar(char c, List<char> badChars)
        {
            ushort word;
            if (Cp932.TryGetWord(c, out word) && word < Threshold)
                return c;                       // 直接 CP932
            char proxy;
            if (CharToProxy.TryGetValue(c.ToString(), out proxy))
                return proxy;                    // 映射到代理字
            if (badChars != null && !badChars.Contains(c)) badChars.Add(c);
            return '?';
        }

        /// <summary>整段文本映射(不动 ASCII/半角); 返回映射后文本, badChars 收集缺字</summary>
        public string MapText(string text, List<char> badChars)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new StringBuilder(text.Length);
            foreach (var rune in text.EnumerateRunes())
            {
                string source = rune.ToString();
                if (rune.Value <= char.MaxValue)
                {
                    sb.Append(MapChar((char)rune.Value, badChars));
                    continue;
                }
                char proxy;
                if (CharToProxy.TryGetValue(source, out proxy)) sb.Append(proxy);
                else
                {
                    if (badChars != null && !badChars.Contains(source[0])) badChars.Add(source[0]);
                    sb.Append('?');
                }
            }
            return sb.ToString();
        }

        /// <summary>映射后的字节数(游戏侧真实字节长度): &lt;阈值按CP932长度, 其余2字节; 缺字按2字节计</summary>
        public int ByteCount(string text, out bool hasBad)
        {
            hasBad = false;
            if (text == null) return 0;
            int n = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value > char.MaxValue)
                {
                    n += 2;
                    if (!CharToProxy.ContainsKey(rune.ToString())) hasBad = true;
                    continue;
                }
                char c = (char)rune.Value;
                ushort word;
                if (Cp932.TryGetWord(c, out word) && word < Threshold)
                    n += (word > 0xFF) ? 2 : 1;
                else
                {
                    n += 2;
                    if (!CharToProxy.ContainsKey(c.ToString())) hasBad = true;
                }
            }
            return n;
        }
    }

    /// <summary>
    /// UTF-8 tbl → CP932(带映射) tbl 的转码导出。
    /// 只转每行 '=' 右边的值部分; 行内注释(// 或 ;)与其它行原样直编 CP932。
    /// 参考 tbl.py 的 split_comment 逻辑。
    /// </summary>
    public static class Transcoder
    {
        public class FileResult
        {
            public string File;
            public bool Success;
            public List<char> BadChars = new List<char>();
            public string Error;
            public int MappedChars;   // 实际发生映射的字符数
        }

        public class Result
        {
            public List<FileResult> Files = new List<FileResult>();
            public int OkCount { get { return Files.FindAll(f => f.Success).Count; } }
            public int FailCount { get { return Files.FindAll(f => !f.Success).Count; } }
        }

        /// <summary>导出整个文件夹(含子目录)。只处理 .tbl。</summary>
        public static Result ExportFolder(string srcFolder, string outFolder, MapTable map,
            HashSet<string> onlyThese = null)
        {
            var res = new Result();
            ExportDir(new DirectoryInfo(srcFolder), new DirectoryInfo(outFolder), map, onlyThese, res);
            return res;
        }

        static void ExportDir(DirectoryInfo src, DirectoryInfo dst, MapTable map,
            HashSet<string> onlyThese, Result res)
        {
            if (!dst.Exists) dst.Create();
            foreach (FileInfo f in src.GetFiles("*.tbl"))
            {
                string relKey = f.FullName;
                var fr = new FileResult { File = f.Name };
                res.Files.Add(fr);
                if (onlyThese != null && !onlyThese.Contains(relKey)) { fr.Success = true; continue; }
                try
                {
                    string utf8Text = File.ReadAllText(f.FullName, new UTF8Encoding(false));
                    byte[] cp932 = TranscodeText(utf8Text, map, fr);
                    if (fr.BadChars.Count > 0)
                    {
                        fr.Success = false;
                        continue;   // 缺字 → 不写该文件
                    }
                    File.WriteAllBytes(Path.Combine(dst.FullName, f.Name), cp932);
                    fr.Success = true;
                }
                catch (Exception ex)
                {
                    fr.Success = false;
                    fr.Error = ex.Message;
                }
            }
            foreach (DirectoryInfo sd in src.GetDirectories())
            {
                if (sd.Name.StartsWith(".")) continue;
                ExportDir(sd, dst.CreateSubdirectory(sd.Name), map, onlyThese, res);
            }
        }

        /// <summary>单文件转码: 返回 CP932 字节; badChars/统计写入 fr</summary>
        public static byte[] TranscodeText(string utf8Text, MapTable map, FileResult fr)
        {
            var outBytes = new List<byte>(utf8Text.Length);
            string[] lines = utf8Text.Split(new[] { "\r\n" }, StringSplitOptions.None);
            for (int li = 0; li < lines.Length; li++)
            {
                string line = lines[li];
                string mapped = TranscodeLine(line, map, fr);
                byte[] enc = Cp932.Encode(mapped);
                outBytes.AddRange(enc);
                if (li < lines.Length - 1) outBytes.AddRange(new byte[] { 13, 10 });
            }
            return outBytes.ToArray();
        }

        /// <summary>单行转码: 条目行只映射 '=' 右边值(跳过行内注释); 其余行原样。</summary>
        public static string TranscodeLine(string line, MapTable map, FileResult fr)
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#")) return line;   // 节头/参数/独立注释: 原样

            // 解析 "#digits <ws>= <value...>"
            int i = 1;
            while (i < trimmed.Length && char.IsDigit(trimmed[i])) i++;
            if (i <= 1) return line;
            int j = i;
            while (j < trimmed.Length && char.IsWhiteSpace(trimmed[j])) j++;
            if (j >= trimmed.Length || trimmed[j] != '=') return line;
            int valueStart = j + 1;

            string rest = line.Substring(line.IndexOf('=') + 1);

            // 分离行内注释 (与 tbl.py split_comment 一致: 先出现者)
            int p1 = rest.IndexOf("//");
            int p2 = rest.IndexOf(';');
            int p = -1;
            if (p1 >= 0 && p2 >= 0) p = Math.Min(p1, p2);
            else if (p1 >= 0) p = p1;
            else if (p2 >= 0) p = p2;

            string valueRaw, comment;
            if (p >= 0) { valueRaw = rest.Substring(0, p); comment = rest.Substring(p); }
            else { valueRaw = rest; comment = ""; }

            // 值部分逐字映射 (ASCII/数字/引号/空格 < 阈值自动直通)
            var sb = new StringBuilder();
            foreach (var rune in valueRaw.EnumerateRunes())
            {
                string source = rune.ToString();
                if (rune.Value > char.MaxValue)
                {
                    char proxy;
                    if (map.CharToProxy.TryGetValue(source, out proxy))
                    {
                        sb.Append(proxy);
                        fr.MappedChars++;
                    }
                    else
                    {
                        fr.BadChars.Add(source[0]);
                        sb.Append('?');
                    }
                }
                else
                {
                    char mc = map.MapChar((char)rune.Value, fr.BadChars);
                    if (mc != rune.Value) fr.MappedChars++;
                    sb.Append(mc);
                }
            }
            return line.Substring(0, line.IndexOf('=') + 1) + sb.ToString() + comment;
        }
    }
}
