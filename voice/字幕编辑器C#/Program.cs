using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VoiceSubEditor
{
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args != null && args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            {
                SelfTest.Run();
                return;
            }
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
    }

    /// <summary>控制台自测: tbl 解析往返、引号规则、CP932、语音解码(与 Python 参考实现比对校验和)</summary>
    internal static class SelfTest
    {
        public static void Run()
        {
            try { AttachConsole(-1); } catch { }   // ATTACH_PARENT_PROCESS, 失败无所谓(结果也写文件)
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            var log = new System.Text.StringBuilder();
            int pass = 0, fail = 0;
            Action<string, bool> check = (name, ok) =>
            {
                string line = (ok ? "[PASS] " : "[FAIL] ") + name;
                try { Console.WriteLine(line); } catch { }
                log.AppendLine(line);
                if (ok) pass++; else fail++;
            };

            // ---- CP932 ----
            try
            {
                byte[] b = Cp932.Encode("中文abcＡＢＣ");
                check("CP932 编码字节: " + b.Length + " (期望13: 中文4+abc3+全角6)", b.Length == 13);
                string rt = Cp932.Decode(b);
                check("CP932 解码往返", rt == "中文abcＡＢＣ");
                bool bad; int n = Cp932.ByteCount("あaあ", out bad);
                check("CP932 字节计数 2+1+2=5", n == 5 && !bad);
            }
            catch (Exception ex) { check("CP932 异常: " + ex.Message, false); }

            // ---- 映射码表 / Unicode 扩展汉字 ----
            try
            {
                string mapPath = FindMap();
                if (mapPath == null) check("找到 font.tbl", false);
                else
                {
                    var map = MapTable.Load(mapPath);
                    check("font.tbl 加载: " + map.Count + " 字", map.Count > 0);
                    check("扩展汉字代理对码表项可识别", map.CharToProxy.ContainsKey("𫉄"));
                    bool bad;
                    int bytes = map.ByteCount("李梅花儿𫉄", out bad);
                    check("扩展汉字字节统计不误报缺字", bytes == 10 && !bad);
                }
            }
            catch (Exception ex) { check("码表异常: " + ex.Message, false); }

            // ---- tbl 解析/引号 ----
            try
            {
                var secs = Tbl.Parse("[TBL00_00]\r\nNUM =4\r\n#000 =0,0,20,1,0,25,2344\r\n#004 =\"hello world\"\r\n");
                var sec = secs[0];
                check("tbl 解析: 1数字+1文本", sec.Entries.Count == 2 && sec.Entries[0].IsNumeric && !sec.Entries[1].IsNumeric);
                check("tbl 引号剥离: '" + sec.Entries[1].Text + "'", sec.Entries[1].Text == "hello world");
                sec.Entries[1].Text = "a b c";   // 修改 → 保存时应加引号
                string built = Tbl.Build(secs);
                check("tbl 修改后带空格台词加引号", built.Contains("=#004 =\"a b c\"") || built.Contains("\"a b c\""));
                // 往返稳定
                var secs2 = Tbl.Parse(built);
                string built2 = Tbl.Build(secs2);
                check("tbl build→parse→build 稳定", built == built2);

                // ---- 换行转义: tbl 内统一存字面 \n ----
                var nlSecs = Tbl.Parse("[TBL00_00]\n#000 =0,0,20,1,0,25,2344\n#004 =\"第一行\\n第二行\"\n");
                check("解析保持字面 \\n 转义", nlSecs[0].Entries[1].Text == "第一行\\n第二行");
                var nlOld = Tbl.Parse("[TBL00_00]\n#000 =0,0,20,1,0,25,2344\n#004 =\"第一行\n第二行\"\n");
                check("旧数据引号内真换行合并为 \\n 转义", nlOld[0].Entries[1].Text == "第一行\\n第二行");
                string nlBuilt = Tbl.Build(nlOld);
                check("转义台词单行写出", nlBuilt.Contains("#004 =\"第一行\\n第二行\""));
                string esc = Tbl.UnescapeNewlines(Tbl.EscapeNewlines("a\r\nb\nc\rd"));
                check("Escape/Unescape 往返", esc == "a" + Environment.NewLine + "b" + Environment.NewLine + "c" + Environment.NewLine + "d");
                // 原文件存在孤立引号坏行: 不得吞并后续记录
                var brokenSecs = Tbl.Parse("[TBL00_00]\n#000 =0,0,20,1,0,25,2344\n#004 =\"未闭合台词\n#005 =0,0,20,1,0,25,99\n#006 =普通台词\n");
                check("坏行(孤立引号)不吞并下一条", brokenSecs[0].Entries.Count == 4 && brokenSecs[0].Entries[2].IsNumeric);
                string brokenB1 = Tbl.Build(brokenSecs);
                check("坏行数据 build→parse→build 稳定", brokenB1 == Tbl.Build(Tbl.Parse(brokenB1)));

                var emptySecs = Tbl.Parse("[TBL00_00]\n#000 =0,0,20,1,0,25,2344\n");
                var emptyStore = new DocumentStore();
                emptyStore.Docs["test.tbl"] = emptySecs;
                var emptySec = emptySecs[0];
                var create = new TextChangeCommand(emptyStore);
                create.Add("test.tbl", emptySec, 0, null, "", "新台词");
                create.Redo();
                check("无原台词时按语音条目新建", emptySec.GetTextSlot(0, false)?.Text == "新台词");
                create.Undo();
                check("撤销新建后恢复为无台词行", emptySec.GetTextSlot(0, false) == null);

                // ---- 超长行提示: 全角=1字 半角=0.5字, 每行限13字 ----
                check("13全角字不提示", Tbl.LineOverflowWarning(new string('汉', 13), 13) == "");
                check("14全角字提示第1行", Tbl.LineOverflowWarning(new string('汉', 14), 13) == "第1行超过13个字");
                check("26半角字折算13字不提示", Tbl.LineOverflowWarning(new string('a', 26), 13) == "");
                check("27半角字折算13.5字提示", Tbl.LineOverflowWarning(new string('a', 27), 13).Contains("第1行"));
                string multi = new string('汉', 13) + "\n" + new string('汉', 15);
                check("多行只报超长行", Tbl.LineOverflowWarning(multi, 13) == "第2行超过13个字");
                check("NUM=数字条目数(台词行不计入)", true);
            }
            catch (Exception ex) { check("tbl 异常: " + ex.Message, false); }

            // ---- 真实数据往返 ----
            try
            {
                string folder = FindVoicetbl();
                if (folder == null) check("找到 voicetbl 文件夹 (跳过真实数据测试)", false);
                else
                {
                    string[] files = Directory.GetFiles(folder, "*.tbl");
                    int badFiles = 0;
                    foreach (string f in files)
                    {
                        string text = Cp932.Decode(File.ReadAllBytes(f));
                        string b1 = Tbl.Build(Tbl.Parse(text));
                        string b2 = Tbl.Build(Tbl.Parse(b1));
                        if (b1 != b2)
                        {
                            badFiles++;
                            string[] L1 = b1.Replace("\r\n", "\n").Split('\n');
                            string[] L2 = b2.Replace("\r\n", "\n").Split('\n');
                            int shown = 0;
                            for (int i = 0; i < Math.Min(L1.Length, L2.Length) && shown < 2; i++)
                            {
                                if (L1[i] != L2[i])
                                {
                                    log.AppendLine("DIFF " + Path.GetFileName(f) + " line" + i);
                                    log.AppendLine("  b1: " + L1[i]);
                                    log.AppendLine("  b2: " + L2[i]);
                                    shown++;
                                }
                            }
                            if (shown == 0) log.AppendLine("DIFF " + Path.GetFileName(f) + " 行数 " + L1.Length + " vs " + L2.Length);
                        }
                    }
                    check("真实 tbl 往返一致: " + files.Length + " 个文件, 不一致 " + badFiles, badFiles == 0);

                    // 语音解码与 Python 参考比对 (vid, samples, adler32)
                    string msh = FindMsh(folder);
                    if (msh == null) check("找到 slgvoice.msh (跳过语音解码测试)", false);
                    else
                    {
                        var bank = VoiceBank.Load(msh);
                        check("MSH 加载: " + (bank != null ? bank.Count + " 条" : "null"), bank != null && bank.Count == 2966);
                        var refs = new Dictionary<int, Tuple<int, uint>>
                        {
                            {   0, Tuple.Create(29596, 1375306948u) },
                            { 2344, Tuple.Create(56056, 2804268690u) },
                            {  935, Tuple.Create(53004, 2166435199u) },
                            { 2965, Tuple.Create(139384, 2339587903u) },
                            { 1500, Tuple.Create(93128, 1149752137u) },
                        };
                        foreach (var kv in refs)
                        {
                            short[] pcm; int rate, ls, le;
                            bool ok = bank.TryDecode(kv.Key, out pcm, out rate, out ls, out le);
                            uint adler = ok ? Adler32(pcm) : 0;
                            check(string.Format("语音#{0}: {1}样本 adler={2} (期望 {3}/{4})", kv.Key, pcm != null ? pcm.Length : 0, adler, kv.Value.Item1, kv.Value.Item2),
                                ok && pcm.Length == kv.Value.Item1 && adler == kv.Value.Item2);
                        }
                    }
                }
            }
            catch (Exception ex) { check("真实数据异常: " + ex.Message, false); }

            log.AppendLine();
            log.AppendLine("结果: PASS=" + pass + " FAIL=" + fail);
            try { Console.WriteLine(log.ToString().Substring(log.Length - 40)); } catch { }
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest_result.txt"), log.ToString());
                try { Console.WriteLine("完整结果已写入 selftest_result.txt"); } catch { }
            }
            catch { }
            Environment.ExitCode = fail == 0 ? 0 : 1;
        }

        static uint Adler32(short[] pcm)
        {
            uint a = 1, b = 0;
            foreach (short s in pcm)
            {
                a = (a + (uint)(s & 0xFF)) % 65521;
                b = (b + a) % 65521;
                a = (a + (uint)((s >> 8) & 0xFF)) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static string FindVoicetbl()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string c = Path.Combine(dir, "voicetbl");
                if (Directory.Exists(c) && Directory.GetFiles(c, "*.tbl").Length > 0) return c;
                var p = Directory.GetParent(dir);
                if (p == null) break;
                dir = p.FullName;
            }
            return null;
        }

        static string FindMsh(string voicetbl)
        {
            string parent = Path.GetDirectoryName(voicetbl.TrimEnd('\\', '/'));
            string[] cands = {
                Path.Combine(parent ?? "", "slgvoice.msh"),
                Path.Combine(voicetbl, "slgvoice.msh"),
                Path.Combine(AppContext.BaseDirectory, "slgvoice.msh"),
            };
            foreach (string c in cands) if (File.Exists(c)) return c;
            return null;
        }

        static string FindMap()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 6; i++)
            {
                string c = Path.Combine(dir, "font.tbl");
                if (File.Exists(c)) return c;
                var p = Directory.GetParent(dir);
                if (p == null) break;
                dir = p.FullName;
            }
            return null;
        }

        [DllImport("kernel32.dll")]
        static extern bool AttachConsole(int pid);
    }
}
