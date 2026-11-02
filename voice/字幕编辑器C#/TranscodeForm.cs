using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VoiceSubEditor
{
    /// <summary>转码导出对话框: UTF-8 tbl → CP932(应用映射码表), 输出到独立目录</summary>
    public class TranscodeForm : Form
    {
        readonly MainForm main;
        TextBox txtMap, txtOut;
        Label lblMapInfo;
        Button btnBrowseMap, btnBrowseOut, btnStart;
        CheckBox chkOnlyModified;
        TextBox txtLog;
        MapTable map;

        public TranscodeForm(MainForm main)
        {
            this.main = main;
            Text = "转码导出 — UTF-8 → CP932(应用映射码表)";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(680, 460);
            BuildLayout();
            txtMap.Text = main.MapPath ?? "";
            txtOut.Text = main.LastExportDir ?? "";
            TryLoadMap(false);
        }

        void BuildLayout()
        {
            int y = 14;
            var lbl1 = new Label { Text = "映射码表 (font.tbl, UTF-16):", Location = new Point(14, y), AutoSize = true };
            Controls.Add(lbl1);
            y += 24;
            txtMap = new TextBox { Location = new Point(14, y), Width = 520, ReadOnly = true };
            Controls.Add(txtMap);
            btnBrowseMap = new Button { Text = "浏览...", Location = new Point(545, y - 2), AutoSize = true };
            btnBrowseMap.Click += (s, e) => BrowseMap();
            Controls.Add(btnBrowseMap);
            y += 30;
            lblMapInfo = new Label { Location = new Point(14, y), AutoSize = true, ForeColor = Color.DimGray, Text = "未加载" };
            Controls.Add(lblMapInfo);
            y += 28;

            var lbl2 = new Label { Text = "输出目录 (CP932 版 tbl, 请勿选原目录):", Location = new Point(14, y), AutoSize = true };
            Controls.Add(lbl2);
            y += 24;
            txtOut = new TextBox { Location = new Point(14, y), Width = 520 };
            Controls.Add(txtOut);
            btnBrowseOut = new Button { Text = "浏览...", Location = new Point(545, y - 2), AutoSize = true };
            btnBrowseOut.Click += (s, e) => BrowseOut();
            Controls.Add(btnBrowseOut);
            y += 34;

            chkOnlyModified = new CheckBox
            {
                Text = "仅导出已修改的文件 (不勾=导出全部, 结果才是完整可用的文件夹)",
                Location = new Point(14, y), AutoSize = true,
            };
            Controls.Add(chkOnlyModified);
            y += 32;

            btnStart = new Button
            {
                Text = "开始转码", Location = new Point(14, y), Size = new Size(140, 34),
                BackColor = Color.FromArgb(0, 120, 215), ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
            };
            btnStart.Click += (s, e) => DoExport();
            Controls.Add(btnStart);

            var hint = new Label
            {
                Text = "规则: 编码≥889F 的字 → 查码表映射; 编码<889F(ASCII/半角等) → 直接CP932; 查不到 → 报错并跳过该文件。\n只转 '=' 右边的值; 注释与其它行原样保留。缺字的文件不会写出。",
                Location = new Point(170, y + 2), AutoSize = true, ForeColor = Color.DimGray,
            };
            Controls.Add(hint);
            y += 48;

            txtLog = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(14, y), Size = new Size(ClientSize.Width - 28, ClientSize.Height - y - 14),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Consolas", 9F), BackColor = Color.White,
            };
            Controls.Add(txtLog);
        }

        void BrowseMap()
        {
            using (var dlg = new OpenFileDialog { Filter = "码表 (*.tbl;*.txt)|*.tbl;*.txt|所有文件|*.*", Title = "选择 font.tbl 映射码表" })
            {
                if (File.Exists(txtMap.Text)) dlg.InitialDirectory = Path.GetDirectoryName(txtMap.Text);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    txtMap.Text = dlg.FileName;
                    TryLoadMap(true);
                }
            }
        }

        void BrowseOut()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择 CP932 输出目录" })
            {
                if (Directory.Exists(txtOut.Text)) dlg.SelectedPath = txtOut.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    txtOut.Text = dlg.SelectedPath;
            }
        }

        void TryLoadMap(bool verbose)
        {
            map = null;
            lblMapInfo.ForeColor = Color.DimGray;
            lblMapInfo.Text = "未加载";
            if (txtMap.Text.Trim().Length == 0) return;
            try
            {
                map = MapTable.Load(txtMap.Text.Trim());
                main.SetMap(map, txtMap.Text.Trim());
                lblMapInfo.ForeColor = Color.Green;
                lblMapInfo.Text = string.Format("已加载 {0} 字 (阈值 {1:X4}: 之上走映射, 之下直接CP932)", map.Count, map.Threshold);
            }
            catch (Exception ex)
            {
                lblMapInfo.ForeColor = Color.Firebrick;
                lblMapInfo.Text = "加载失败: " + ex.Message;
            }
        }

        void DoExport()
        {
            TryLoadMap(true);
            if (map == null) { MessageBox.Show(this, "请先选择有效的映射码表", "转码导出"); return; }
            string outDir = txtOut.Text.Trim();
            if (outDir.Length == 0) { MessageBox.Show(this, "请选择输出目录", "转码导出"); return; }
            if (Path.GetFullPath(outDir).TrimEnd('\\') == Path.GetFullPath(main.Store.Folder).TrimEnd('\\'))
            {
                MessageBox.Show(this, "输出目录不能和 TBL 原目录相同 (会覆盖 UTF-8 工作文件)", "转码导出");
                return;
            }

            HashSet<string> only = null;
            if (chkOnlyModified.Checked)
            {
                only = new HashSet<string>();
                foreach (string f in main.Store.Modified)
                    only.Add(Path.GetFullPath(Path.Combine(main.Store.Folder, f)));
            }

            var sb = new StringBuilder();
            sb.AppendLine("码表: " + map.Path + " (" + map.Count + " 字, 阈值 " + map.Threshold.ToString("X4") + ")");
            sb.AppendLine("输出: " + outDir);
            sb.AppendLine(chkOnlyModified.Checked ? "范围: 仅已修改文件" : "范围: 全部文件");
            sb.AppendLine(new string('-', 60));

            Transcoder.Result res;
            try
            {
                Cursor = Cursors.WaitCursor;
                btnStart.Enabled = false;
                res = Transcoder.ExportFolder(main.Store.Folder, outDir, map, only);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败: " + ex.Message, "转码导出");
                return;
            }
            finally { Cursor = Cursors.Default; btnStart.Enabled = true; }

            int totalMapped = 0;
            foreach (var f in res.Files)
            {
                if (f.Success)
                {
                    totalMapped += f.MappedChars;
                    sb.AppendLine("  √ " + f.File + (f.MappedChars > 0 ? "  (映射 " + f.MappedChars + " 字)" : ""));
                }
                else
                {
                    sb.AppendLine("  × " + f.File + "  — 未导出");
                    if (f.BadChars.Count > 0)
                    {
                        var detail = new List<string>();
                        foreach (char c in f.BadChars)
                            detail.Add(c + "(U+" + ((int)c).ToString("X4") + ")");
                        sb.AppendLine("      缺字: " + string.Join(" ", detail.ToArray()));
                    }
                    if (!string.IsNullOrEmpty(f.Error))
                        sb.AppendLine("      错误: " + f.Error);
                }
            }
            sb.AppendLine(new string('-', 60));
            sb.AppendLine(string.Format("完成: 成功 {0} / 失败 {1}  (共映射 {2} 字)", res.OkCount, res.FailCount, totalMapped));
            txtLog.Text = sb.ToString();
            main.LastExportDir = outDir;
            main.SetStatus(string.Format("转码导出完成: 成功 {0}, 失败 {1} → {2}", res.OkCount, res.FailCount, outDir));
        }
    }
}
