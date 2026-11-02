using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace VoiceSubEditor
{
    public class MainForm : Form
    {
        internal readonly DocumentStore store = new DocumentStore();
        readonly UndoManager undo = new UndoManager();
        readonly VoicePlayer player = new VoicePlayer();
        VoiceBank bank;
        MapTable map;

        // 路径状态
        string cfgPath;
        public string MapPath;
        public string LastExportDir;
        public string FaceFolder;

        // 控件
        MenuStrip menu;
        ToolStrip tool;
        ListView lstFile, lstSec;
        DataGridView grid;
        TextBox txtEdit;
        Label lblFolder, lblMsh, lblMap, lblBytes;
        Label lblFace, lblLineWarn;
        PictureBox picFace;
        Button btnApply, btnDelete;
        CheckBox chkSync;
        StatusStrip statusStrip;
        ToolStripStatusLabel lblStatus;
        ToolStripStatusLabel lblUndo;
        BatchForm batchForm;
        TranscodeForm transcodeForm;

        // 当前状态
        string curFile;
        int curSec = -1, curRow = -1;
        bool suppress = false;      // 刷新期间屏蔽事件
        bool loadingRow = false;    // 程序填入编辑框期间屏蔽提交
        int playingVoiceId = -1;

        public DocumentStore Store { get { return store; } }

        public MainForm()
        {
            Text = "GA2 战斗语音字幕编辑器 (C#版)  —  UTF-8 存储";
            Font = new Font("Microsoft YaHei UI", 9.25F);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1520, 880);
            MinimumSize = new Size(1200, 700);
            KeyPreview = true;

            BuildMenu();
            BuildTool();
            BuildLayout();
            undo.StateChanged += OnUndoStateChanged;
            store.Changed += OnStoreChanged;

            cfgPath = Path.Combine(AppContext.BaseDirectory, "editor.cfg");
            LoadCfgAndInit();
        }

        // ================= 菜单/工具栏 =================

        void BuildMenu()
        {
            menu = new MenuStrip { Dock = DockStyle.Top };
            var mFile = new ToolStripMenuItem("文件(&F)");
            mFile.DropDownItems.Add("选择TBL文件夹...", null, (s, e) => PickFolder());
            mFile.DropDownItems.Add("选择语音库(slgvoice.msh)...", null, (s, e) => PickMsh());
            mFile.DropDownItems.Add("选择映射码表(font.tbl)...", null, (s, e) => PickMap());
            mFile.DropDownItems.Add("选择表情 PNG 文件夹...", null, (s, e) => PickFaceFolder());
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add("退出", null, (s, e) => Close());
            menu.Items.Add(mFile);

            var mEdit = new ToolStripMenuItem("编辑(&E)");
            mEdit.DropDownItems.Add("撤销  Ctrl+Z", null, (s, e) => undo.Undo());
            mEdit.DropDownItems.Add("重做  Ctrl+Y / Ctrl+Shift+Z", null, (s, e) => undo.Redo());
            menu.Items.Add(mEdit);

            var mTool = new ToolStripMenuItem("工具(&T)");
            mTool.DropDownItems.Add("批量编辑(按语音ID统一改)  Ctrl+B", null, (s, e) => OpenBatch());
            mTool.DropDownItems.Add("转码导出(UTF-8→CP932+码表)  Ctrl+E", null, (s, e) => OpenTranscode());
            mTool.DropDownItems.Add("导出所有调用语音为 WAV...", null, (s, e) => ExportVoices());
            mTool.DropDownItems.Add(new ToolStripSeparator());
            mTool.DropDownItems.Add("清除本文件全部台词...", null, (s, e) => ClearCurrentFile());
            menu.Items.Add(mTool);

            MainMenuStrip = menu;
        }

        void BuildTool()
        {
            tool = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(6, 2, 6, 2) };
            Action<string, EventHandler> add = (text, h) =>
            {
                var b = new ToolStripButton(text) { AutoSize = true, Padding = new Padding(4, 1, 4, 1) };
                b.Click += h;
                tool.Items.Add(b);
            };
            Action sep = () => tool.Items.Add(new ToolStripSeparator());

            add("📁 TBL文件夹", (s, e) => PickFolder());
            add("🔊 语音库…", (s, e) => PickMsh());
            add("🔤 码表…", (s, e) => PickMap());
            add("🙂 表情目录", (s, e) => PickFaceFolder());
            sep();
            sep();
            add("↶ 撤销", (s, e) => undo.Undo());
            add("↷ 重做", (s, e) => undo.Redo());
            sep();
            add("🗂 批量编辑", (s, e) => OpenBatch());
            add("📤 转码导出", (s, e) => OpenTranscode());
            add("🔊 导出语音 WAV", (s, e) => ExportVoices());
        }

        // ================= 布局 =================

        void BuildLayout()
        {
            lblFolder = new Label();
            lblMsh = new Label();
            lblMap = new Label();

            var topBar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 2,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            topBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            topBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            topBar.Controls.Add(menu, 0, 0);
            topBar.Controls.Add(tool, 0, 1);

            // 右侧台词编辑区：语音条目在左，台词作为附加编辑项在右
            var editor = new GroupBox
            {
                Dock = DockStyle.Fill,
                Text = "  当前语音的台词（可选）  ",
                Padding = new Padding(12, 8, 12, 10),
            };
            var facePanel = new Panel { Dock = DockStyle.Top, Height = 138, Padding = new Padding(8, 8, 8, 4) };
            picFace = new PictureBox
            {
                Size = new Size(100, 100),
                Height = 100,
                Dock = DockStyle.Top,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.FromArgb(242, 242, 242),
                BorderStyle = BorderStyle.FixedSingle,
            };
            lblFace = new Label { Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.DimGray, AutoEllipsis = true };
            facePanel.Controls.Add(picFace);
            facePanel.Controls.Add(lblFace);
            txtEdit = new TextBox
            {
                Multiline = true, Dock = DockStyle.Fill,
                Font = new Font("MS Gothic", 12.5F),
                ScrollBars = ScrollBars.Vertical,
                Padding = new Padding(6, 4, 6, 4),
                AcceptsReturn = true,   // 回车=换行, 保存只走按钮
            };
            txtEdit.TextChanged += (s, e) => { UpdateByteCount(); UpdateLineWarn(); };

            var btnRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 178, FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(2, 8, 2, 0), WrapContents = false,
            };
            btnApply = new Button { Text = "保存", Width = 365, Height = 32 };
            btnApply.Click += (s, e) => SaveFromEditor();
            btnDelete = new Button { Text = "删除台词行", Width = 365, Height = 32 };
            btnDelete.Click += (s, e) => DeleteCurrentLine();
            chkSync = new CheckBox { Text = "同步修改所有文件中相同语音ID", Checked = true, AutoSize = true, Padding = new Padding(2, 8, 0, 0) };
            btnRow.Controls.AddRange(new Control[] { btnApply, btnDelete, chkSync });

            // 信息行: 字节数 + 超长行红色提示(紧贴输入框下方)
            var infoRow = new Panel { Dock = DockStyle.Bottom, Height = 22 };
            lblBytes = new Label { Dock = DockStyle.Fill, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
            lblLineWarn = new Label
            {
                Dock = DockStyle.Left, AutoSize = true,
                ForeColor = Color.Red, TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(2, 3, 10, 0),
            };
            infoRow.Controls.Add(lblBytes);
            infoRow.Controls.Add(lblLineWarn);

            var hintRow = new Panel { Dock = DockStyle.Bottom, Height = 0 };
            hintRow.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, ForeColor = Color.Gray, TextAlign = ContentAlignment.MiddleLeft,
                Text = "列表以语音条目为准；台词是可选附加项。\n空=不显示字幕；没有台词时输入会新建；切换行自动暂存；\n回车=换行(保存为\\n转义)；半角空格保存时自动加引号；字节上限 126。",
            });

            editor.Controls.Add(txtEdit);
            editor.Controls.Add(facePanel);
            editor.Controls.Add(btnRow);
            editor.Controls.Add(infoRow);
            editor.Controls.Add(hintRow);

            // 中部: 文件 | 节+条目
            var splitMain = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 150, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 120 };
            lstFile = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, ShowItemToolTips = true, Padding = new Padding(4) };
            lstFile.Columns.Add("文件", 105);
            lstFile.Columns.Add("已填", 46);
            lstFile.Columns.Add("状态", 58);
            lstFile.SelectedIndexChanged += (s, e) => { if (!suppress) SelectFile(); };
            splitMain.Panel1.Controls.Add(lstFile);

            var splitRight = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 285, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1, Panel1MinSize = 230 };
            lstSec = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, Padding = new Padding(4) };
            lstSec.Columns.Add("组 / 情境", 210);
            lstSec.Columns.Add("条目", 52);
            lstSec.Columns.Add("已填", 52);
            lstSec.SelectedIndexChanged += (s, e) => { if (!suppress) SelectSection(); };
            splitRight.Panel1.Controls.Add(lstSec);

            grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, MultiSelect = false,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersHeight = 30, RowTemplate = { Height = 28 },
                BackgroundColor = Color.White, BorderStyle = BorderStyle.None, Padding = new Padding(4, 4, 4, 4),
            };
            var cellStyle = new DataGridViewCellStyle { Padding = new Padding(4, 0, 4, 0), Alignment = DataGridViewContentAlignment.MiddleLeft };
            string[] heads = { "序号", "语音ID", "角色ID", "立绘ID", "权重", "状态", "标志", "台词" };
            int[] widths = { 54, 68, 68, 82, 56, 56, 56, 140 };
            for (int i = 0; i < heads.Length; i++)
            {
                grid.Columns.Add("c" + i, heads[i]);
                grid.Columns[i].Width = widths[i];
                grid.Columns[i].SortMode = DataGridViewColumnSortMode.NotSortable;
                if (i < 7) grid.Columns[i].DefaultCellStyle = cellStyle;
            }
            var playColumn = new DataGridViewButtonColumn
            {
                Name = "play",
                HeaderText = "播放",
                Text = "▶",
                UseColumnTextForButtonValue = true,
                Width = 52,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            };
            grid.Columns.Add(playColumn);
            grid.SelectionChanged += (s, e) => { if (!suppress && !loadingRow) SelectRow(); };
            grid.CellContentClick += (s, e) =>
            {
                if (e.RowIndex >= 0 && e.ColumnIndex == grid.Columns["play"].Index)
                {
                    int voiceId = GetVoiceIdAtRow(e.RowIndex);
                    ToggleVoice(voiceId);
                }
            };
            grid.CellDoubleClick += (s, e) => PlayCurrent();
            splitRight.Panel2.Controls.Add(grid);

            splitMain.Panel2.Controls.Add(splitRight);
            var content = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 7,
                FixedPanel = FixedPanel.None,
            };
            content.Panel1.Controls.Add(splitMain);
            content.Panel2.Controls.Add(editor);
            Shown += (s, e) =>
            {
                content.Panel1MinSize = 850;
                content.Panel2MinSize = 390;
                int desired = Math.Min(1080, content.Width - 410 - content.SplitterWidth);
                content.SplitterDistance = Math.Max(850, desired);
                splitMain.Panel1MinSize = 120;
                splitMain.SplitterDistance = Math.Min(150, splitMain.Width - 500);
                splitRight.Panel1MinSize = 220;
                splitRight.SplitterDistance = Math.Min(300, splitRight.Width - 360);
            };

            // 状态栏
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("就绪") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
            lblUndo = new ToolStripStatusLabel("撤销 0 · 重做 0") { Alignment = ToolStripItemAlignment.Right };
            statusStrip.Items.Add(lblStatus);
            statusStrip.Items.Add(lblUndo);

            var shell = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0),
            };
            shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
            shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            shell.Controls.Add(topBar, 0, 0);
            shell.Controls.Add(content, 0, 1);
            shell.Controls.Add(statusStrip, 0, 2);
            Controls.Add(shell);
        }

        // ================= 初始化/路径 =================

        void LoadCfgAndInit()
        {
            string folder = null, msh = null, mapPath = null, outDir = null, faceFolder = null;
            try
            {
                if (File.Exists(cfgPath))
                {
                    foreach (string line in File.ReadAllLines(cfgPath))
                    {
                        if (line.StartsWith("folder=")) folder = line.Substring(7).Trim();
                        else if (line.StartsWith("msh=")) msh = line.Substring(4).Trim();
                        else if (line.StartsWith("map=")) mapPath = line.Substring(4).Trim();
                        else if (line.StartsWith("outdir=")) outDir = line.Substring(7).Trim();
                        else if (line.StartsWith("facedir=")) faceFolder = line.Substring(8).Trim();
                    }
                }
            }
            catch { }
            LastExportDir = outDir;
            FaceFolder = faceFolder;

            if (folder == null || !Directory.Exists(folder))
                folder = ProbeFolder();
            if (folder != null)
                InitFolder(folder, msh, mapPath);
            else
            {
                lblFolder.Text = "TBL文件夹: 未找到 voicetbl, 请点左上角 [TBL文件夹] 手动选择";
                SetStatus("未找到 voicetbl 文件夹");
            }
        }

        string ProbeFolder()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 5; i++)
            {
                string cand = Path.Combine(dir, "voicetbl");
                if (Directory.Exists(cand) && Directory.GetFiles(cand, "*.tbl").Length > 0) return cand;
                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }

        string ProbeMap()
        {
            // 从 exe 往上找 绝对领域之门/font.tbl 或 font.tbl
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 5; i++)
            {
                string cand = Path.Combine(dir, "font.tbl");
                if (File.Exists(cand)) return cand;
                cand = Path.Combine(dir, "绝对领域之门", "font.tbl");
                if (File.Exists(cand)) return cand;
                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null) break;
                dir = parent.FullName;
            }
            return null;
        }

        void PickFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择包含 slgv*.tbl 的文件夹" })
            {
                if (Directory.Exists(store.Folder)) dlg.SelectedPath = store.Folder;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    InitFolder(dlg.SelectedPath, bank != null ? bank.MshPath : null, MapPath);
            }
        }

        void PickMsh()
        {
            using (var dlg = new OpenFileDialog { Filter = "MSH 语音头 (*.msh)|*.msh|所有文件|*.*", Title = "选择 slgvoice.msh" })
            {
                if (bank != null) dlg.InitialDirectory = Path.GetDirectoryName(bank.MshPath);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    var b = VoiceBank.Load(dlg.FileName);
                    if (b == null) { MessageBox.Show(this, "MSH 格式不正确(校验失败)", "错误"); return; }
                    bank = b;
                    lblMsh.Text = "语音库: " + bank.MshPath + "  (" + bank.Count + " 条语音)";
                    SetStatus("语音库已加载: " + bank.Count + " 条");
                }
            }
        }

        void PickMap()
        {
            using (var dlg = new OpenFileDialog { Filter = "映射码表 (*.tbl;*.txt)|*.tbl;*.txt|所有文件|*.*", Title = "选择 font.tbl 映射码表" })
            {
                if (File.Exists(MapPath)) dlg.InitialDirectory = Path.GetDirectoryName(MapPath);
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    SetMap(null, dlg.FileName, true);
            }
        }

        void PickFaceFolder()
        {
            using (var dlg = new FolderBrowserDialog { Description = "选择表情 PNG 文件夹" })
            {
                if (Directory.Exists(FaceFolder)) dlg.SelectedPath = FaceFolder;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    FaceFolder = dlg.SelectedPath;
                    UpdateFacePreview();
                    SetStatus("表情目录已设置: " + FaceFolder);
                }
            }
        }

        /// <summary>设置码表(主动加载或转码窗口传回)</summary>
        public void SetMap(MapTable m, string path)
        {
            SetMap(m, path, false);
        }

        void SetMap(MapTable m, string path, bool verbose)
        {
            MapPath = path;
            try
            {
                map = m ?? MapTable.Load(path);
                lblMap.Text = string.Format("映射码表: {0}  ({1} 字, 阈值 {2:X4})", path, map.Count, map.Threshold);
                if (verbose) SetStatus("码表已加载: " + map.Count + " 字; 字节数将按游戏侧真实编码计算");
            }
            catch (Exception ex)
            {
                map = null;
                lblMap.Text = "映射码表: " + path + "  (加载失败)";
                if (verbose) MessageBox.Show(this, "码表加载失败: " + ex.Message, "错误");
            }
            UpdateByteCount();
        }

        void InitFolder(string folder, string preferredMsh, string preferredMap)
        {
            try
            {
                if (!store.LoadFolder(folder)) { SetStatus("该文件夹内没有 .tbl 文件"); return; }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "读取文件夹失败: " + ex.Message, "错误");
                return;
            }
            lblFolder.Text = "TBL文件夹: " + folder + "  (" + store.Docs.Count + " 个文件, UTF-8 存储)";

            // 语音库: 优先配置 → voicetbl 上级 → voicetbl 内 → exe 目录
            bank = null;
            var cands = new List<string>();
            if (!string.IsNullOrEmpty(preferredMsh)) cands.Add(preferredMsh);
            string parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/'));
            if (parent != null)
            {
                cands.Add(Path.Combine(parent, "slgvoice.msh"));
                cands.Add(Path.Combine(parent, Path.Combine("slg", "slgvoice.msh")));
            }
            cands.Add(Path.Combine(folder, "slgvoice.msh"));
            cands.Add(Path.Combine(AppContext.BaseDirectory, "slgvoice.msh"));
            foreach (string c in cands)
            {
                if (File.Exists(c)) { bank = VoiceBank.Load(c); if (bank != null) break; }
            }
            lblMsh.Text = bank != null
                ? "语音库: " + bank.MshPath + "  (" + bank.Count + " 条语音)"
                : "语音库: 未找到 slgvoice.msh (工具栏[语音库…]可手动选择; 无语音库时不能播放)";

            // 码表
            map = null;
            string mapPath = !string.IsNullOrEmpty(preferredMap) && File.Exists(preferredMap) ? preferredMap : ProbeMap();
            if (mapPath != null) SetMap(null, mapPath, false);
            else lblMap.Text = "映射码表: 未找到 font.tbl (工具栏[码表…]可手动选择; 不影响编辑, 只影响字节数与转码)";

            RefreshFileList(selectFirst: true);
            SetStatus("已加载 " + store.Docs.Count + " 个 tbl 文件" + (bank != null ? ", 语音库 " + bank.Count + " 条" : ""));
        }

        // ================= 文件列表 =================

        int FileFilledCount(string file)
        {
            List<TblSection> secs;
            if (!store.Docs.TryGetValue(file, out secs)) return 0;
            int n = 0;
            foreach (var s in secs) if (s.Name.StartsWith("TBL", StringComparison.OrdinalIgnoreCase)) n += s.FilledCount;
            return n;
        }

        void RefreshFileList(bool selectFirst)
        {
            suppress = true;
            string keep = curFile;
            lstFile.BeginUpdate();
            lstFile.Items.Clear();
            foreach (var kv in store.Docs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new ListViewItem(kv.Key) { UseItemStyleForSubItems = false };
                int filled = FileFilledCount(kv.Key);
                item.SubItems.Add(filled > 0 ? filled.ToString() : "-");
                item.SubItems.Add(store.Modified.Contains(kv.Key) ? "已修改" : "-");
                if (filled > 0) item.SubItems[1].ForeColor = Color.Teal;
                if (store.Modified.Contains(kv.Key)) item.SubItems[2].ForeColor = Color.Firebrick;
                lstFile.Items.Add(item);
            }
            lstFile.EndUpdate();
            suppress = false;
            if (selectFirst && lstFile.Items.Count > 0) lstFile.Items[0].Selected = true;
            else if (keep != null)
            {
                var it = lstFile.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Text == keep);
                if (it != null) it.Selected = true;
            }
        }

        void SelectFile()
        {
            if (lstFile.SelectedItems.Count == 0) return;
            CommitPendingEdit();
            curFile = lstFile.SelectedItems[0].Text;
            curSec = -1; curRow = -1;
            RefreshSectionList(true);
            SetStatus("已加载 " + curFile);
        }

        void RefreshSectionList(bool selectFirst)
        {
            suppress = true;
            lstSec.BeginUpdate();
            lstSec.Items.Clear();
            if (curFile != null)
            {
                var secs = store.Docs[curFile];
                for (int i = 0; i < secs.Count; i++)
                {
                    var sec = secs[i];
                    if (!sec.Name.StartsWith("TBL", StringComparison.OrdinalIgnoreCase)) continue;
                    var nums = sec.NumericEntries;
                    var item = new ListViewItem(Tbl.SituationLabel(sec.Name) + (sec.IsTbl15 ? "  ⚠" : "")) { Tag = i };
                    item.SubItems.Add(nums.Count.ToString());
                    item.SubItems.Add(sec.FilledCount.ToString());
                    if (sec.IsTbl15) item.ForeColor = Color.DarkGoldenrod;
                    else if (sec.FilledCount > 0) item.SubItems[2].ForeColor = Color.Teal;
                    lstSec.Items.Add(item);
                }
            }
            lstSec.EndUpdate();
            suppress = false;
            if (selectFirst && lstSec.Items.Count > 0) lstSec.Items[0].Selected = true;
        }

        void SelectSection()
        {
            if (lstSec.SelectedItems.Count == 0 || curFile == null) return;
            CommitPendingEdit();
            curSec = (int)lstSec.SelectedItems[0].Tag;
            curRow = -1;
            RefreshEntries(true);
            var sec = store.Docs[curFile][curSec];
            SetStatus(sec.Name + (sec.IsTbl15 ? "  (小剧场，可编辑；注意可能与剧情字幕重复)" : "  (可编辑)"));
        }

        void RefreshEntries(bool selectFirst)
        {
            suppress = true;
            grid.Rows.Clear();
            if (curFile != null && curSec >= 0)
            {
                var sec = store.Docs[curFile][curSec];
                var nums = sec.NumericEntries;
                for (int i = 0; i < nums.Count; i++)
                {
                    var slot = sec.GetTextSlot(i, false);
                    string txt = slot != null ? (slot.Text ?? "") : "";
                    var f = nums[i].Fields;
                    grid.Rows.Add(
                        i.ToString(),
                        f.Length > 6 ? f[6].ToString() : "?",
                        f.Length > 2 ? f[2].ToString() : "?",
                        f.Length > 3 ? f[3].ToString() : "?",
                        f.Length > 5 ? f[5].ToString() : "?",
                        f.Length > 0 ? f[0].ToString() : "?",
                        f.Length > 1 ? f[1].ToString() : "?",
                        txt);
                }
            }
            suppress = false;
            if (selectFirst && grid.Rows.Count > 0)
            {
                grid.CurrentCell = grid.Rows[0].Cells[0];
                SelectRow();
            }
            else if (grid.Rows.Count == 0)
            {
                curRow = -1;
                loadingRow = true;
                txtEdit.Text = "";
                loadingRow = false;
                UpdateFacePreview();
            }
        }

        void SelectRow()
        {
            if (grid.CurrentRow == null || curFile == null || curSec < 0) return;
            int newRow = grid.CurrentRow.Index;
            if (newRow == curRow) return;
            CommitPendingEdit();   // 提交上一行编辑(含撤销记录)
            curRow = newRow;
            LoadRowIntoEditor();
        }

        void LoadRowIntoEditor()
        {
            loadingRow = true;
            var slot = CurrentSlot(false);
            txtEdit.Text = Tbl.UnescapeNewlines(slot != null ? (slot.Text ?? "") : "");
            loadingRow = false;
            UpdateByteCount();
            UpdateFacePreview();
        }

        void UpdateFacePreview()
        {
            if (picFace == null || lblFace == null) return;
            string fileName = FindFaceFileName();
            string pngName = fileName != null ? Path.ChangeExtension(fileName, ".agi.png") : null;
            string path = !string.IsNullOrEmpty(FaceFolder) && pngName != null
                ? Path.Combine(FaceFolder, pngName) : null;
            try
            {
                Image old = picFace.Image;
                if (path != null && File.Exists(path))
                {
                    using (var loaded = Image.FromFile(path)) picFace.Image = new Bitmap(loaded);
                }
                else picFace.Image = null;
                if (old != null) old.Dispose();
            }
            catch { picFace.Image = null; }
            lblFace.Text = pngName == null ? "表情：未找到定义" : pngName;
        }

        string FindFaceFileName()
        {
            VoiceRef r = CurrentRef();
            if (r == null || r.Entry.Fields == null || r.Entry.Fields.Length < 4) return null;
            int faceId = r.Entry.Fields[3];
            if (curFile == null || !store.Docs.ContainsKey(curFile)) return null;
            foreach (var sec in store.Docs[curFile])
            {
                if (!string.Equals(sec.Name, "FACEFILES", StringComparison.OrdinalIgnoreCase)) continue;
                var entry = sec.Entries.FirstOrDefault(e => !e.IsNumeric && e.Key == faceId);
                if (entry != null && !string.IsNullOrWhiteSpace(entry.Text)) return entry.Text.Trim();
            }
            return null;
        }

        TblEntry CurrentSlot(bool create)
        {
            if (curFile == null || curSec < 0 || curRow < 0) return null;
            var sec = store.Docs[curFile][curSec];
            return sec.GetTextSlot(curRow, create);
        }

        VoiceRef CurrentRef()
        {
            if (curFile == null || curSec < 0 || curRow < 0) return null;
            var sec = store.Docs[curFile][curSec];
            var nums = sec.NumericEntries;
            if (curRow >= nums.Count) return null;
            return new VoiceRef { File = curFile, SecIndex = curSec, RowIndex = curRow, Entry = nums[curRow], Section = sec };
        }

        // ================= 编辑/应用 =================

        /// <summary>切换行时自动提交未保存的编辑</summary>
        void CommitPendingEdit()
        {
            if (loadingRow || curFile == null || curSec < 0 || curRow < 0) return;
            var slot = CurrentSlot(false);
            string old = slot != null ? (slot.Text ?? "") : "";
            string now = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            if (now == old) return;
            ApplyTextInternal(now, chkSync.Checked, quiet: true);
        }

        void ApplyFromEditor(bool explicitApply)
        {
            if (curFile == null || curSec < 0 || curRow < 0) { SetStatus("请先选择一个语音条目"); return; }
            var sec = store.Docs[curFile][curSec];
            string text = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            ApplyTextInternal(text, chkSync.Checked, quiet: false);
        }

        void SaveFromEditor()
        {
            if (curFile == null || curSec < 0 || curRow < 0)
            {
                SetStatus("请先选择一个语音条目");
                return;
            }
            string text = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            ApplyTextInternal(text, chkSync.Checked, quiet: false);
            SaveAllFiles();
            SetStatus("已保存当前台词及同步修改");
        }

        void ApplyTextInternal(string text, bool syncAll, bool quiet)
        {
            var r = CurrentRef();
            if (r == null) return;
            bool bad;
            int bytes = CountBytes(text, out bad);

            var cmd = new TextChangeCommand(store);
            var slot = r.Section.GetTextSlot(r.RowIndex, false);
            string old = slot != null ? (slot.Text ?? "") : "";
            if (old != text)
                cmd.Add(r.File, r.Section, r.RowIndex, slot, old, text);

            if (syncAll && r.VoiceId >= 0)
            {
                foreach (var other in store.EnumerateVoiceEntries())
                {
                    if (other.VoiceId != r.VoiceId) continue;
                    if (other.File == r.File && other.SecIndex == r.SecIndex && other.RowIndex == r.RowIndex) continue;
                    var oslot = other.Section.GetTextSlot(other.RowIndex, false);
                    string oold = oslot != null ? (oslot.Text ?? "") : "";
                    if (oold != text)
                        cmd.Add(other.File, other.Section, other.RowIndex, oslot, oold, text);
                }
            }
            if (cmd.IsEmpty) { SetStatus("没有变化"); return; }

            cmd.Description = "台词「" + Trunc(text, 12) + "」";
            undo.Execute(cmd);
            int places = cmd.Changes.Count;
            int files = cmd.Changes.Select(c => c.File).Distinct().Count();

            RefreshAfterEdit();
            if (!quiet)
            {
                string msg = (text.Length == 0 ? "已删除台词行" : "已暂存: " + Trunc(text, 30)) +
                             string.Format(" (语音#{0}, {1} 个文件 {2} 处{3})", r.VoiceId, files, places, syncAll ? ", 已同步" : "");
                if (bad) msg += "  ⚠ 有码表/CP932都编不了的字符(按2字节计)";
                if (bytes > 126) msg += string.Format("  ⚠ 超长: {0}/126字节", bytes);
                SetStatus(msg + "  — 记得保存文件");
            }
            else
            {
                SetStatus("已自动暂存上一行编辑 (语音#" + r.VoiceId + ")");
            }
        }

        void DeleteCurrentLine()
        {
            var r = CurrentRef();
            if (r == null) return;
            var slot = r.Section.GetTextSlot(r.RowIndex, false);
            if (slot == null || string.IsNullOrEmpty(slot.Text)) { SetStatus("该行本来就没有台词"); return; }
            txtEdit.Text = "";
            ApplyTextInternal("", chkSync.Checked, false);
        }

        void RefreshAfterEdit()
        {
            int keepRow = curRow;
            RefreshEntries(false);
            if (keepRow >= 0 && keepRow < grid.Rows.Count)
            {
                suppress = true;
                grid.CurrentCell = grid.Rows[keepRow].Cells[0];
                suppress = false;
            }
            RefreshSectionCounts();
            RefreshFileList(selectFirst: false);
        }

        void RefreshSectionCounts()
        {
            if (curFile == null) return;
            var secs = store.Docs[curFile];
            foreach (ListViewItem it in lstSec.Items)
            {
                int idx = (int)it.Tag;
                if (idx < secs.Count)
                {
                    it.SubItems[2].Text = secs[idx].FilledCount.ToString();
                    it.SubItems[2].ForeColor = secs[idx].FilledCount > 0 ? Color.Teal : Control.DefaultForeColor;
                }
            }
        }

        /// <summary>码表感知的字节计数: 有码表按游戏侧真实编码; 无码表退回 CP932</summary>
        int CountBytes(string s, out bool hasBad)
        {
            if (map != null) return map.ByteCount(s, out hasBad);
            return Cp932.ByteCount(s, out hasBad);
        }

        /// <summary>当前已加载的映射码表(可为 null)</summary>
        public MapTable MapLoaded { get { return map; } }

        void UpdateByteCount()
        {
            string s = Tbl.EscapeNewlines(txtEdit.Text.TrimEnd('\r', '\n'));
            bool bad;
            int n = CountBytes(s, out bad);
            string note = s.Contains(' ') ? "  [含半角空格→保存时自动加引号]" : "";
            string prefix = map != null ? "游戏侧字节数" : "字节数(CP932, 未加载码表)";
            if (bad) lblBytes.Text = string.Format("{0}: {1} / 126  ⚠ 有字符无法编码{2}", prefix, n, note);
            else if (n > 126) lblBytes.Text = string.Format("{0}: {1} / 126  ⚠ 超长!{2}", prefix, n, note);
            else lblBytes.Text = string.Format("{0}: {1} / 126  OK{2}", prefix, n, note);
        }

        /// <summary>超长行红色提示: 每行超过13个字(全角)时显示</summary>
        void UpdateLineWarn()
        {
            lblLineWarn.Text = Tbl.LineOverflowWarning(txtEdit.Text, 13);
        }

        // ================= 播放 =================

        void PlayCurrent()
        {
            var r = CurrentRef();
            if (r == null) return;
            ToggleVoice(r.VoiceId);
        }

        int GetVoiceIdAtRow(int row)
        {
            if (curFile == null || curSec < 0 || row < 0) return -1;
            var sec = store.Docs[curFile][curSec];
            var nums = sec.NumericEntries;
            return row < nums.Count && nums[row].Fields.Length > 6 ? nums[row].Fields[6] : -1;
        }

        void ToggleVoice(int voiceId)
        {
            if (voiceId < 0) return;
            if (playingVoiceId == voiceId)
            {
                player.Stop();
                playingVoiceId = -1;
                SetStatus("已停止语音#" + voiceId);
                return;
            }
            if (!PlayVoiceInternal(voiceId)) playingVoiceId = -1;
        }

        public void PlayVoice(int voiceId)
        {
            PlayVoiceInternal(voiceId);
        }

        bool PlayVoiceInternal(int voiceId)
        {
            if (bank == null) { SetStatus("未加载语音库(需要 slgvoice.msh/.msb)"); return false; }
            if (voiceId < 0) { SetStatus("该条目语音ID为 " + voiceId + " (无语音)"); return false; }
            short[] pcm;
            int rate, loopS, loopE;
            try
            {
                if (!bank.TryDecode(voiceId, out pcm, out rate, out loopS, out loopE))
                { SetStatus("语音 #" + voiceId + " 不存在于语音库"); return false; }
            }
            catch (Exception ex)
            {
                SetStatus("解码失败: " + ex.Message);
                return false;
            }
            byte[] wav = Wav.Build(pcm, rate);
            try
            {
                player.Play(wav);
            }
            catch (Exception ex)
            {
                SetStatus("播放失败: " + ex.Message);
                return false;
            }
            playingVoiceId = voiceId;
            double sec = (double)pcm.Length / rate;
            SetStatus(string.Format("▶ 正在播放 语音#{0}  {1}Hz  {2:0.00}秒", voiceId, rate, sec));
            return true;
        }

        // ================= 批量/清除/转码 =================

        void OpenBatch()
        {
            if (store.Docs.Count == 0) { SetStatus("请先选择 TBL 文件夹"); return; }
            if (batchForm == null || batchForm.IsDisposed)
                batchForm = new BatchForm(store, undo, this);
            batchForm.Show(this);
            batchForm.Activate();
        }

        void OpenTranscode()
        {
            if (store.Docs.Count == 0) { SetStatus("请先选择 TBL 文件夹"); return; }
            if (transcodeForm == null || transcodeForm.IsDisposed)
                transcodeForm = new TranscodeForm(this);
            transcodeForm.Show(this);
            transcodeForm.Activate();
        }

        void ExportVoices()
        {
            if (store.Docs.Count == 0) { SetStatus("请先选择 TBL 文件夹"); return; }
            if (bank == null) { SetStatus("未加载语音库(需要 slgvoice.msh/.msb)"); return; }

            var voiceIds = new HashSet<int>();
            foreach (var r in store.EnumerateVoiceEntries(includeTbl15: true))
                if (r.VoiceId >= 0) voiceIds.Add(r.VoiceId);
            if (voiceIds.Count == 0) { SetStatus("当前 TBL 没有调用有效语音"); return; }

            using (var dlg = new FolderBrowserDialog { Description = "选择 WAV 输出目录" })
            {
                if (!string.IsNullOrEmpty(LastExportDir) && Directory.Exists(LastExportDir))
                    dlg.SelectedPath = LastExportDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                string outDir = dlg.SelectedPath;
                int ok = 0, missing = 0, failed = 0;
                var errors = new List<string>();
                Cursor = Cursors.WaitCursor;
                try
                {
                    foreach (int voiceId in voiceIds.OrderBy(id => id))
                    {
                        short[] pcm;
                        int rate, loopStart, loopEnd;
                        try
                        {
                            if (!bank.TryDecode(voiceId, out pcm, out rate, out loopStart, out loopEnd))
                            {
                                missing++;
                                errors.Add(string.Format("语音#{0}: 语音库中不存在或数据无效", voiceId));
                                continue;
                            }
                            string path = Path.Combine(outDir, string.Format("{0:D4}.wav", voiceId));
                            File.WriteAllBytes(path, Wav.Build(pcm, rate));
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            errors.Add(string.Format("语音#{0}: {1}", voiceId, ex.Message));
                        }
                    }
                }
                finally { Cursor = Cursors.Default; }

                LastExportDir = outDir;
                string message = string.Format("导出完成\n\n成功: {0}\n缺失/无效: {1}\n失败: {2}\n总调用语音: {3}\n\n输出目录:\n{4}",
                    ok, missing, failed, voiceIds.Count, outDir);
                if (errors.Count > 0)
                    message += "\n\n详细信息:\n" + string.Join("\n", errors.Take(20)) + (errors.Count > 20 ? "\n..." : "");
                MessageBox.Show(this, message, "导出语音 WAV", MessageBoxButtons.OK,
                    failed == 0 && missing == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                SetStatus(string.Format("语音 WAV 导出完成: 成功 {0}, 缺失 {1}, 失败 {2} → {3}", ok, missing, failed, outDir));
            }
        }

        void ClearCurrentFile()
        {
            if (curFile == null) { SetStatus("请先选择文件"); return; }
            var res = MessageBox.Show(this,
                "清除 " + curFile + " 内全部台词行? (数字条目保留, 可 Ctrl+Z 撤销)",
                "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (res != DialogResult.Yes) return;

            var cmd = new TextChangeCommand(store) { Description = "清除 " + curFile + " 台词" };
            var secs = store.Docs[curFile];
            foreach (var sec in secs)
            {
                var nums = sec.NumericEntries;
                for (int i = 0; i < nums.Count; i++)
                {
                    var slot = sec.GetTextSlot(i, false);
                    if (slot != null && !string.IsNullOrEmpty(slot.Text))
                        cmd.Add(curFile, sec, i, slot, slot.Text, "");
                }
            }
            if (cmd.IsEmpty) { SetStatus("该文件没有已填台词"); return; }
            undo.Execute(cmd);
            RefreshAfterEdit();
            SetStatus("已清除 " + curFile + " 全部台词 (Ctrl+Z 可撤销) — 记得保存");
        }

        // ================= 保存 =================

        void SaveCurrent()
        {
            CommitPendingEdit();
            if (curFile == null) { SetStatus("请先选择文件"); return; }
            try
            {
                store.Save(curFile);
                RefreshFileList(false);
                SetStatus("已保存(UTF-8): " + Path.Combine(store.Folder, curFile));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败: " + ex.Message, "错误");
            }
        }

        void SaveAllFiles()
        {
            CommitPendingEdit();
            if (store.Modified.Count == 0) { SetStatus("没有待保存的修改"); return; }
            try
            {
                store.SaveAll();
                RefreshFileList(false);
                SetStatus("已保存全部修改文件 (UTF-8)");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败: " + ex.Message, "错误");
            }
        }

        // ================= 全局事件 =================

        void OnStoreChanged()
        {
            // 撤销/重做/批量修改后整体刷新(保持当前选择)
            string keepFile = curFile; int keepSec = curSec, keepRow = curRow;
            RefreshFileList(false);
            if (keepFile != null && store.Docs.ContainsKey(keepFile))
            {
                suppress = true;
                var it = lstFile.Items.Cast<ListViewItem>().FirstOrDefault(x => x.Text == keepFile);
                if (it != null) it.Selected = true;
                suppress = false;
                curFile = keepFile;
                RefreshSectionList(false);
                if (keepSec >= 0 && keepSec < store.Docs[keepFile].Count)
                {
                    suppress = true;
                    foreach (ListViewItem s in lstSec.Items)
                        if ((int)s.Tag == keepSec) { s.Selected = true; break; }
                    suppress = false;
                    curSec = keepSec;
                    RefreshEntries(false);
                    if (keepRow >= 0 && keepRow < grid.Rows.Count)
                    {
                        suppress = true;
                        grid.CurrentCell = grid.Rows[keepRow].Cells[0];
                        suppress = false;
                    }
                    curRow = keepRow < grid.Rows.Count ? keepRow : -1;
                    LoadRowIntoEditor();
                }
            }
        }

        void OnUndoStateChanged()
        {
            undo.StackInfo(out int u, out int r);
            lblUndo.Text = string.Format("撤销 {0} · 重做 {1}", u, r);
            if (undo.LastDescription != null) SetStatus(undo.LastDescription);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.Z)) { undo.Undo(); return true; }
            if (keyData == (Keys.Control | Keys.Y) || keyData == (Keys.Control | Keys.Shift | Keys.Z)) { undo.Redo(); return true; }
            if (keyData == (Keys.Control | Keys.B)) { OpenBatch(); return true; }
            if (keyData == (Keys.Control | Keys.E)) { OpenTranscode(); return true; }
            if (keyData == Keys.F5) { PlayCurrent(); return true; }
            // 回车不再绑定保存: 编辑框内回车=换行, 保存只能点保存按钮
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (store.Modified.Count > 0)
            {
                var res = MessageBox.Show(this,
                    "有 " + store.Modified.Count + " 个文件的修改未保存。\n\n是=保存全部并退出, 否=放弃修改退出, 取消=继续编辑",
                    "未保存的修改", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                if (res == DialogResult.Yes)
                {
                    try { store.SaveAll(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "保存失败: " + ex.Message + "\n退出已取消", "错误");
                        e.Cancel = true;
                        return;
                    }
                }
                else if (res == DialogResult.Cancel) { e.Cancel = true; return; }
            }
            player.Stop();
            SaveCfg();
            base.OnFormClosing(e);
        }

        void SaveCfg()
        {
            try
            {
                File.WriteAllLines(cfgPath, new[]
                {
                    "folder=" + (store.Folder ?? ""),
                    "msh=" + (bank != null ? bank.MshPath : ""),
                    "map=" + (MapPath ?? ""),
                    "outdir=" + (LastExportDir ?? ""),
                    "facedir=" + (FaceFolder ?? ""),
                });
            }
            catch { }
        }

        public void SetStatus(string s) { lblStatus.Text = s; }

        static string Trunc(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
