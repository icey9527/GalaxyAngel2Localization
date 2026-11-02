using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceSubEditor
{
    /// <summary>tbl 中的一行: #key =value。数字行(Fields!=null)是语音条目参数; 文本行(Fields==null)是台词。</summary>
    public class TblEntry
    {
        public int Key;
        public int[] Fields;        // 数字行: 7 个字段; 文本行为 null
        public string Raw;          // 原始 value 串(数字行/未修改文本行保存时原样写回)
        public string Text;         // 文本行: 当前台词(去引号); 数字行为 null
        public string ParsedText;   // 文本行: 解析时的台词, 用于判断是否被修改
        public bool IsNumeric => Fields != null;
    }

    public class TblSection
    {
        public string Name;
        public string Comment;
        public string RawHeader;
        public List<string> Params = new List<string>();
        public List<TblEntry> Entries = new List<TblEntry>();
        public bool IsTbl15 => Name != null && Name.StartsWith("TBL15", StringComparison.OrdinalIgnoreCase);

        /// <summary>数字条目(语音参数行)</summary>
        public List<TblEntry> NumericEntries => Entries.Where(e => e.IsNumeric).ToList();

        /// <summary>行 i 的台词槽(键 = 数字条目数 + i)</summary>
        public TblEntry GetTextSlot(int rowIndex, bool create)
        {
            List<TblEntry> nums = NumericEntries;
            int key = nums.Count + rowIndex;
            var slot = Entries.FirstOrDefault(e => !e.IsNumeric && e.Key == key);
            if (slot == null && create)
            {
                slot = new TblEntry { Key = key, Fields = null, Raw = "", Text = "", ParsedText = "" };
                Entries.Add(slot);
            }
            return slot;
        }

        public int FilledCount => Entries.Count(e => !e.IsNumeric && !string.IsNullOrEmpty(e.Text));
    }

    /// <summary>tbl 文本解析/重建(兼容旧 Python 版行为; 台词含半角空格时自动加引号)</summary>
    public static class Tbl
    {
        /// <summary>解析 tbl 文本。行分隔符为 CRLF(与游戏/旧工具一致)。</summary>
        public static List<TblSection> Parse(string text)
        {
            var secs = new List<TblSection>();
            TblSection cur = null;
            string[] lines = MergeQuotedNewlines(text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'));
            foreach (string line in lines)
            {
                string s = line.Trim();
                if (s.StartsWith("["))
                {
                    // [NAME] // comment
                    int close = s.IndexOf(']');
                    if (close > 1)
                    {
                        string name = s.Substring(1, close - 1).Trim();
                        string comment = "";
                        int cpos = s.IndexOf("//", close);
                        if (cpos >= 0) comment = s.Substring(cpos + 2).Trim();
                        cur = new TblSection { Name = name, Comment = comment, RawHeader = line.TrimEnd() };
                        secs.Add(cur);
                        continue;
                    }
                }
                if (cur == null) continue;
                if (s.Length == 0 || s.StartsWith(";"))
                {
                    cur.Params.Add(line);
                    continue;
                }
                TblEntry e = ParseRow(s);
                if (e != null) cur.Entries.Add(e);
                else cur.Params.Add(line);
            }
            return secs;
        }

        static TblEntry ParseRow(string s)
        {
            if (!s.StartsWith("#")) return null;
            int i = 1;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i <= 1) return null;
            int j = i;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j >= s.Length || s[j] != '=') return null;
            j++;
            while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            string val = s.Substring(j).Trim();
            int key = int.Parse(s.Substring(1, i - 1));

            // 数字行: 全部字段为整数
            if (val.Contains(",") && val.Split(',').All(p => IsInt(p.Trim())))
            {
                return new TblEntry
                {
                    Key = key,
                    Fields = val.Split(',').Select(p => int.Parse(p.Trim())).ToArray(),
                    Raw = val,
                    Text = null,
                    ParsedText = null,
                };
            }
            // 文本行(台词)
            string txt = Unquote(val);
            return new TblEntry { Key = key, Fields = null, Raw = val, Text = txt, ParsedText = txt };
        }

        static bool IsInt(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int k = 0;
            if (s[0] == '-') k = 1;
            if (k >= s.Length) return false;
            for (int i = k; i < s.Length; i++) if (!char.IsDigit(s[i])) return false;
            return true;
        }

        /// <summary>
        /// 合并被真换行拆断的引号台词行: # 行值以 " 开头且引号为奇数时, 若下一行
        /// 不是新记录(条目/节/键值/注释/空行)则并入, 换行点转为字面 \n 转义。
        /// 保守策略: 原文件存在孤立引号坏行(如 #010 ="xxx 无闭引号), 遇到任何
        /// 疑似新记录立即停止, 保证坏行按旧行为原样解析, 不吞并后续内容。
        /// </summary>
        static readonly System.Text.RegularExpressions.Regex RxRecordStart =
            new System.Text.RegularExpressions.Regex(@"^(#?\d+\s*=|\w+\s*=|\[|;)");

        static string[] MergeQuotedNewlines(string[] lines)
        {
            var merged = new List<string>(lines.Length);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                string t = line.TrimStart();
                if (t.StartsWith("#") && IsQuotedValueStart(t) && CountChar(line, '"') % 2 == 1)
                {
                    while (CountChar(line, '"') % 2 == 1 && i + 1 < lines.Length)
                    {
                        string next = lines[i + 1].TrimEnd('\r');
                        string nt = next.Trim();
                        if (nt.Length == 0 || RxRecordStart.IsMatch(nt)) break;
                        i++;
                        line = line + "\\n" + next;
                    }
                }
                merged.Add(line);
            }
            return merged.ToArray();
        }

        /// <summary># 行的值是否以引号开头(#NNN ="...)</summary>
        static bool IsQuotedValueStart(string t)
        {
            int eq = t.IndexOf('=');
            if (eq < 0) return false;
            return t.Substring(eq + 1).TrimStart().StartsWith("\"");
        }

        static int CountChar(string s, char c)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) if (s[i] == c) n++;
            return n;
        }

        /// <summary>去掉包裹整句的一对半角双引号</summary>
        public static string Unquote(string s)
        {
            if (s != null && s.Length >= 2 && s.StartsWith("\"") && s.EndsWith("\""))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        /// <summary>台词含半角空格时用引号包裹(游戏解析器按空格分词, 空格台词必须括起来)</summary>
        public static string QuoteIfNeeded(string text)
        {
            if (text != null && text.Contains(' ')) return "\"" + text + "\"";
            return text;
        }

        // ---- 换行转义: tbl 内统一存字面 "\n"(5C 6E), 游戏侧补丁负责还原为 0x0A ----

        /// <summary>真换行(CRLF/CR/LF) → 字面 \n 转义(写入 tbl / 计算字节前调用)</summary>
        public static string EscapeNewlines(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
        }

        /// <summary>字面 \n 转义 → 真换行(载入编辑框显示用)</summary>
        public static string UnescapeNewlines(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s.Replace("\\n", Environment.NewLine);
        }

        // ---- 逐行字数检查: 游戏字幕框一行约13个字, 超长给红色提示 ----

        /// <summary>
        /// 逐行检查字数(全角=1字, 半角=0.5字)。返回超长行的红色提示文本
        /// (如 "第2行、第5行超过13个字"), 无超长行返回 ""。最多列出前3个超长行。
        /// </summary>
        public static string LineOverflowWarning(string text, int limit)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var bad = new List<string>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length && bad.Count < 3; i++)
            {
                if (LineHalfUnits(lines[i]) > limit * 2)
                    bad.Add("第" + (i + 1) + "行");
            }
            if (bad.Count == 0) return "";
            return string.Join("、", bad.ToArray()) + "超过" + limit + "个字";
        }

        /// <summary>一行的显示宽度, 以半角为 1 个单位(全角=2单位)</summary>
        static long LineHalfUnits(string s)
        {
            long u = 0;
            foreach (char c in s)
            {
                if (c <= 0x7F || (c >= 0xFF61 && c <= 0xFF9F)) u += 1;  // 半角(ASCII/半角假名)
                else u += 2;                                            // 全角
            }
            return u;
        }

        /// <summary>重建 tbl 文本(CRLF, 与旧 Python 版相同的规范化)</summary>
        public static string Build(List<TblSection> secs)
        {
            var outLines = new List<string>();
            foreach (var sec in secs)
            {
                outLines.Add(sec.RawHeader ?? ("[" + sec.Name + "]"));
                outLines.AddRange(sec.Params.Where(l => l.Trim().Length > 0));
                foreach (var e in sec.Entries)
                {
                    if (e.IsNumeric)
                    {
                        outLines.Add(string.Format("#{0:D3} ={1}", e.Key, e.Raw));
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(e.Text)) continue; // 空台词不写行
                        string t = (e.Text == e.ParsedText) ? e.Raw : QuoteIfNeeded(EscapeNewlines(e.Text));
                        outLines.Add(string.Format("#{0:D3} ={1}", e.Key, t));
                    }
                }
                outLines.Add("");
            }
            return string.Join("\r\n", outLines.ToArray()) + "\r\n";
        }

        // ---- 情境名(与旧 Python 版一致) ----
        static readonly Dictionary<int, string> Situations = new Dictionary<int, string>
        {
            {0,"命令了解"},{1,"待机继续"},{2,"HP低1"},{3,"HP低2"},{4,"HP低3"},{5,"战斗不能"},
            {6,"合体纹章机不能"},{7,"EP低1"},{8,"EP低2"},{9,"EP低3"},{10,"EP耗尽"},
            {11,"击破敌机"},{12,"受击(HP充足)"},{13,"受击(HP告急)"},{14,"回避成功"},
            {15,"开始攻击(HP充足)"},{16,"开始攻击(HP告急)"},{17,"攻击命中"},{18,"攻击未中"},
            {19,"反击"},{20,"索敌"},{21,"?"},{22,"被修理"},{23,"被补给"},
            {24,"技能就绪"},{25,"技能"},{26,"H武器就绪"},{27,"H武器"},{28,"?"},{29,"拒绝命令"},
            {30,"移动完成"},{31,"?"},{32,"自动修理(急救员)"},{33,"?"},{34,"自动补给"},
            {35,"必杀技目标告知"},{36,"强制闲聊"},{37,"抓拍闲聊1"},{38,"抓拍闲聊2"},
            {39,"?"},{41,"教学小剧场"},{42,"战前小剧场"},{43,"战斗中小剧场"},{44,"战后小剧场"},
            {45,"GameOver(BH击沉)"},{46,"?"},{47,"GameOver(超时)"},{49,"待机确认"},
        };

        /// <summary>TBLxx_yy → "组xx·yy 情境名"</summary>
        public static string SituationLabel(string name)
        {
            if (name == null) return "";
            var m = System.Text.RegularExpressions.Regex.Match(name, @"^TBL(\d+)_(\d+)$");
            if (!m.Success) return name;
            int grp = int.Parse(m.Groups[1].Value), idx = int.Parse(m.Groups[2].Value);
            string sit;
            if (!Situations.TryGetValue(idx, out sit)) sit = "?";
            string tag = grp == 15 ? "小剧场★勿加台词" : string.Format("组{0:D2}", grp);
            return string.Format("{0}·{1:D2} {2}", tag, idx, sit);
        }
    }

    /// <summary>管理文件夹内全部 tbl 文档、修改标记与保存</summary>
    public class DocumentStore
    {
        public string Folder;
        public readonly Dictionary<string, List<TblSection>> Docs = new Dictionary<string, List<TblSection>>();
        public readonly HashSet<string> Modified = new HashSet<string>();
        public event Action Changed;   // 任何内容变化(修改/撤销/重做/保存后 UI 需刷新)

        public bool LoadFolder(string folder)
        {
            Folder = folder;
            Docs.Clear();
            Modified.Clear();
            foreach (string path in System.IO.Directory.GetFiles(folder, "*.tbl").OrderBy(p => p))
            {
                string name = System.IO.Path.GetFileName(path);
                byte[] data;
                try { data = System.IO.File.ReadAllBytes(path); }
                catch (Exception) { continue; }
                Docs[name] = Tbl.Parse(ReadTblText(data));
            }
            return Docs.Count > 0;
        }

        /// <summary>tbl 文本读取: UTF-8(无BOM)优先, 兼容旧 CP932 文件(自动识别)</summary>
        public static string ReadTblText(byte[] data)
        {
            if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                return Encoding.UTF8.GetString(data, 3, data.Length - 3);   // 带 BOM 的 UTF-8(去 BOM)
            try { return new UTF8Encoding(false, true).GetString(data); }   // 严格 UTF-8
            catch (DecoderFallbackException) { return Cp932.Decode(data); } // 旧 CP932 文件
        }

        public void MarkModified(string file)
        {
            if (Docs.ContainsKey(file)) Modified.Add(file);
        }

        public void RaiseChanged() { var h = Changed; if (h != null) h(); }

        public void Save(string file)
        {
            List<TblSection> secs;
            if (!Docs.TryGetValue(file, out secs)) throw new Exception("文档未加载: " + file);
            string text = Tbl.Build(secs);
            // 编辑器一律存 UTF-8 无 BOM; 需要游戏用的 CP932 请走 工具→转码导出
            byte[] data = new UTF8Encoding(false).GetBytes(text);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(Folder, file), data);
            Modified.Remove(file);
        }

        public void SaveAll()
        {
            foreach (string f in Modified.ToArray()) Save(f);
        }

        /// <summary>遍历所有 TBL 数字条目(跳过 TBL15 小剧场)</summary>
        public IEnumerable<VoiceRef> EnumerateVoiceEntries(bool includeTbl15 = false)
        {
            foreach (var kv in Docs.OrderBy(k => k.Key))
            {
                for (int si = 0; si < kv.Value.Count; si++)
                {
                    var sec = kv.Value[si];
                    if (!sec.Name.StartsWith("TBL", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!includeTbl15 && sec.IsTbl15) continue;
                    List<TblEntry> nums = sec.NumericEntries;
                    for (int ri = 0; ri < nums.Count; ri++)
                    {
                        if (nums[ri].Fields != null && nums[ri].Fields.Length > 6)
                            yield return new VoiceRef { File = kv.Key, SecIndex = si, RowIndex = ri, Entry = nums[ri], Section = sec };
                    }
                }
            }
        }
    }

    /// <summary>一个语音条目的定位引用</summary>
    public class VoiceRef
    {
        public string File;
        public int SecIndex, RowIndex;
        public TblEntry Entry;
        public TblSection Section;
        public int VoiceId { get { return Entry.Fields != null && Entry.Fields.Length > 6 ? Entry.Fields[6] : -1; } }
    }
}
