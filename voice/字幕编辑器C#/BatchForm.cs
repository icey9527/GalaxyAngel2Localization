using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VoiceSubEditor
{
    /// <summary>
    /// 批量编辑界面: 扫描指定文件夹全部 tbl(跳过TBL15), 按语音ID收集所有出现位置,
    /// 一处修改 → 全部相同语音ID同步修改(可撤销)。
    /// </summary>
    public class BatchForm : Form
    {
        readonly DocumentStore store;
        readonly UndoManager undo;
        readonly MainForm main;

        // 索引: voiceId → 出现列表
        Dictionary<int, List<VoiceRef>> groups;
        List<VoiceRef> flatAll;

        ListView lstGroups;
        DataGridView gridOcc;
        TextBox txtSearch, txtEdit;
        Label lblCount, lblBytes, lblLineWarn;
        Button btnSearch, btnApplyAll, btnApplySel, btnPlay;
        int curVoiceId = int.MinValue;
        bool suppress = false;

        public BatchForm(DocumentStore store, UndoManager undo, MainForm main)
        {
            this.store = store; this.undo = undo; this.main = main;
            Text = "批量编辑 — 按语音ID统一修改台词 (TBL15小剧场不参与; Ctrl+Z 撤销)";
            Font = new Font("Microsoft YaHei UI", 9.25F);
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(1120, 720);
            MinimumSize = new Size(900, 560);
            KeyPreview = true;

            BuildLayout();
            RebuildIndex();
            store.Changed += OnStoreChanged;
        }

        void BuildLayout()
        {
            // 顶部搜索
            var top = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 6, 8, 0) };
            txtSearch = new TextBox { Width = 220, Dock = DockStyle.Left };
            txtSearch.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { DoSearch(); e.SuppressKeyPress = true; } };
            btnSearch = new Button { Text = "搜索 (语音ID或台词)", AutoSize = true, Dock = DockStyle.Left, Padding = new Padding(8, 0, 0, 0) };
            btnSearch.Click += (s, e) => DoSearch();
            lblCount = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 0, 0), ForeColor = Color.DimGray };
            top.Controls.Add(lblCount);
            top.Controls.Add(btnSearch);
            top.Controls.Add(txtSearch);
            Controls.Add(top);

            // 中部: ID组 | 出现位置
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 400, FixedPanel = FixedPanel.None };
            lstGroups = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false };
            lstGroups.Columns.Add("语音ID", 62);
            lstGroups.Columns.Add("处数", 42);
            lstGroups.Columns.Add("已填", 42);
            lstGroups.Columns.Add("台词预览", 220);
            lstGroups.SelectedIndexChanged += (s, e) => { if (!suppress) SelectGroup(); };
            split.Panel1.Controls.Add(lstGroups);

            gridOcc = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            };
            gridOcc.Columns.Add("f", "文件");
            gridOcc.Columns.Add("s", "节");
            gridOcc.Columns.Add("r", "行");
            gridOcc.Columns.Add("t", "台词");
            gridOcc.Columns["f"].FillWeight = 90;
            gridOcc.Columns["s"].FillWeight = 120;
            gridOcc.Columns["r"].FillWeight = 30;
            gridOcc.Columns["t"].FillWeight = 200;
            split.Panel2.Controls.Add(gridOcc);
            Controls.Add(split);
            Controls.SetChildIndex(split, 2);
            Controls.SetChildIndex(top, 1);

            // 底部编辑
            var bottom = new GroupBox
            {
                Dock = DockStyle.Bottom, Height = 130,
                Text = "台词 (应用到该语音ID的全部出现位置; 空=删除台词; ≤126字节; 回车=换行, 保存为\\n转义; 半角空格自动加引号)",
            };
            txtEdit = new TextBox { Multiline = true, Dock = DockStyle.Fill, Font = new Font("MS Gothic", 12F), ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
            txtEdit.TextChanged += (s, e) => UpdateBytes();

            var row = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(4, 6, 4, 0), WrapContents = false };
            btnApplyAll = new Button { Text = "应用到该ID全部", AutoSize = true };
            btnApplyAll.Click += (s, e) => ApplyToGroup();
            btnApplySel = new Button { Text = "仅应用选中行", AutoSize = true };
            btnApplySel.Click += (s, e) => ApplyToSelected();
            btnPlay = new Button { Text = "播放该语音", AutoSize = true };
            btnPlay.Click += (s, e) => { if (curVoiceId != int.MinValue) main.PlayVoice(curVoiceId); };
            lblBytes = new Label { AutoSize = true, Padding = new Padding(12, 8, 0, 0), ForeColor = Color.DimGray };
            lblLineWarn = new Label { AutoSize = true, Padding = new Padding(12, 8, 0, 0), ForeColor = Color.Red };
            row.Controls.AddRange(new Control[] { btnApplyAll, btnApplySel, btnPlay, lblBytes, lblLineWarn });

            bottom.Controls.Add(txtEdit);
            bottom.Controls.Add(row);
            bottom.Controls.SetChildIndex(txtEdit, 1);
            bottom.Controls.SetChildIndex(row, 0);
            Controls.Add(bottom);
        }

        // ================= 索引 =================

        void RebuildIndex()
        {
            groups = new Dictionary<int, List<VoiceRef>>();
            flatAll = new List<VoiceRef>();
            foreach (var r in store.EnumerateVoiceEntries())
            {
                if (r.VoiceId < 0) continue;   // 无语音的条目字幕永远不会显示, 跳过
                List<VoiceRef> list;
                if (!groups.TryGetValue(r.VoiceId, out list)) { list = new List<VoiceRef>(); groups[r.VoiceId] = list; }
                list.Add(r);
                flatAll.Add(r);
            }
            DoSearch();
        }

        string CurrentFilter() { return (txtSearch.Text ?? "").Trim(); }

        void DoSearch()
        {
            string f = CurrentFilter();
            suppress = true;
            lstGroups.BeginUpdate();
            lstGroups.Items.Clear();
            var q = groups.OrderBy(k => k.Key).AsEnumerable();
            if (f.Length > 0)
            {
                bool isInt = f.All(char.IsDigit);
                q = q.Where(kv =>
                {
                    if (isInt && kv.Key.ToString().Contains(f)) return true;
                    // 台词包含
                    foreach (var r in kv.Value)
                    {
                        var slot = r.Section.GetTextSlot(r.RowIndex, false);
                        if (slot != null && slot.Text != null && slot.Text.Contains(f, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    return false;
                });
            }
            int shown = 0;
            foreach (var kv in q)
            {
                int filled = kv.Value.Count(r => { var s = r.Section.GetTextSlot(r.RowIndex, false); return s != null && !string.IsNullOrEmpty(s.Text); });
                string preview = "";
                foreach (var r in kv.Value)
                {
                    var s = r.Section.GetTextSlot(r.RowIndex, false);
                    if (s != null && !string.IsNullOrEmpty(s.Text)) { preview = s.Text; break; }
                }
                if (preview.Length > 24) preview = preview.Substring(0, 24) + "…";
                var item = new ListViewItem(kv.Key.ToString()) { Tag = kv.Key };
                item.SubItems.Add(kv.Value.Count.ToString());
                item.SubItems.Add(filled.ToString());
                item.SubItems.Add(preview);
                if (filled == 0) item.ForeColor = Color.Gray;
                lstGroups.Items.Add(item);
                shown++;
            }
            lstGroups.EndUpdate();
            suppress = false;
            lblCount.Text = string.Format("共 {0} 个语音ID / {1} 处条目{2}", groups.Count, flatAll.Count,
                f.Length > 0 ? string.Format(" (筛选出 {0})", shown) : "");
            curVoiceId = int.MinValue;
            gridOcc.Rows.Clear();
            txtEdit.Text = "";
        }

        void SelectGroup()
        {
            if (lstGroups.SelectedItems.Count == 0) return;
            int vid = (int)lstGroups.SelectedItems[0].Tag;
            curVoiceId = vid;
            var list = groups[vid];
            suppress = true;
            gridOcc.Rows.Clear();
            foreach (var r in list)
            {
                var slot = r.Section.GetTextSlot(r.RowIndex, false);
                string txt = slot != null ? (slot.Text ?? "") : "";
                gridOcc.Rows.Add(r.File, Tbl.SituationLabel(r.Section.Name), r.RowIndex.ToString(), txt);
            }
            suppress = false;
            // 编辑框预填: 第一个非空台词
            string first = "";
            foreach (var r in list)
            {
                var s = r.Section.GetTextSlot(r.RowIndex, false);
                if (s != null && !string.IsNullOrEmpty(s.Text)) { first = s.Text; break; }
            }
            txtEdit.Text = Tbl.UnescapeNewlines(first);
            UpdateBytes();
        }

        // ================= 应用 =================

        void ApplyToGroup()
        {
            if (curVoiceId == int.MinValue) { lblBytes.Text = "请先选择一个语音ID"; return; }
            string text = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            var cmd = new TextChangeCommand(store) { Description = "批量·语音#" + curVoiceId + "「" + Trunc(text, 10) + "」" };
            foreach (var r in groups[curVoiceId])
            {
                var slot = r.Section.GetTextSlot(r.RowIndex, false);
                string old = slot != null ? (slot.Text ?? "") : "";
                if (old != text) cmd.Add(r.File, r.Section, r.RowIndex, slot, old, text);
            }
            if (cmd.IsEmpty) { lblBytes.Text = "没有变化"; return; }
            undo.Execute(cmd);
            bool bad;
            int bytes = main.MapLoaded != null
                ? main.MapLoaded.ByteCount(text, out bad)
                : Cp932.ByteCount(text, out bad);
            string warn = bad ? "  ⚠含无法编码字符" : (bytes > 126 ? "  ⚠超长" : "");
            lblBytes.Text = string.Format("语音#{0}: 已应用 {1} 处 (Ctrl+Z 撤销){2}", curVoiceId, cmd.Changes.Count, warn);
        }

        void ApplyToSelected()
        {
            if (curVoiceId == int.MinValue || gridOcc.CurrentRow == null) { lblBytes.Text = "请先选中一个出现位置"; return; }
            int idx = gridOcc.CurrentRow.Index;
            var r = groups[curVoiceId][idx];
            string text = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            var slot = r.Section.GetTextSlot(r.RowIndex, false);
            string old = slot != null ? (slot.Text ?? "") : "";
            if (old == text) { lblBytes.Text = "没有变化"; return; }
            var cmd = new TextChangeCommand(store) { Description = "单处·语音#" + curVoiceId };
            cmd.Add(r.File, r.Section, r.RowIndex, slot, old, text);
            undo.Execute(cmd);
            lblBytes.Text = string.Format("仅修改 {0} / {1} / 行{2}", r.File, r.Section.Name, r.RowIndex);
        }

        void UpdateBytes()
        {
            string s = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            bool bad; int n;
            if (main.MapLoaded != null) n = main.MapLoaded.ByteCount(s, out bad);
            else n = Cp932.ByteCount(s, out bad);
            lblBytes.Text = string.Format("字节数: {0}/126{1}", n,
                bad ? " ⚠有字符无法编码(按2字节计)" : (n > 126 ? " ⚠超长" : ""));
            lblLineWarn.Text = Tbl.LineOverflowWarning(txtEdit.Text, 13);
        }

        // ================= 联动 =================

        void OnStoreChanged()
        {
            // 台词被修改(本窗或主窗/撤销) → 重建索引保持视图
            int keepVid = curVoiceId;
            string keepSearch = CurrentFilter();
            RebuildIndex();
            txtSearch.Text = keepSearch;
            // 尝试恢复选择
            suppress = true;
            foreach (ListViewItem it in lstGroups.Items)
                if ((int)it.Tag == keepVid) { it.Selected = true; break; }
            suppress = false;
            if (lstGroups.SelectedItems.Count > 0) SelectGroup();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Z)) { undo.Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { undo.Redo(); return true; }
            if (keyData == Keys.F5 && curVoiceId != int.MinValue) { main.PlayVoice(curVoiceId); return true; }
            // 回车不再绑定应用: 编辑框内回车=换行
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            store.Changed -= OnStoreChanged;
            base.OnFormClosed(e);
        }

        static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
