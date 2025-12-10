using System.Windows.Forms;

namespace GalaxyAngel2Localization.UI
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null!;
        private Label lblProjectTitle;
        private ComboBox cmbProjects;
        private TabControl tabControl;
        private TabPage tabNew;
        private TabPage tabExtract;
        private TabPage tabPrePack;
        private TabPage tabPack;

        // tabNew
        private Button btnBrowseIso;
        private TextBox txtIsoPath;
        private CheckedListBox checkedListBoxFiles;
        private Button btnBuildWorkspace;
        private Label lblStatus;

        // tabExtract
        private Label lblExtractHint;
        private FlowLayoutPanel flpExtractExts;
        private Button btnExtractSelectAll;
        private TextBox txtCustomExt;
        private Button btnAddExt;
        private Button btnExtractStart;
        private Label lblExtractStatus;
        private TextBox txtExtractLog;

        // tabPrePack
        private Label lblPrePackHint;
        private TextBox txtPrePackCommands;
        private Button btnPrePackSave;

        // tabPack
        private Label lblPackHint;
        private Button btnPackStart;
        private Label lblPackStatus;
        private TextBox txtPackLog;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null)
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.lblProjectTitle = new System.Windows.Forms.Label();
            this.cmbProjects = new System.Windows.Forms.ComboBox();
            this.tabControl = new System.Windows.Forms.TabControl();
            this.tabNew = new System.Windows.Forms.TabPage();
            this.lblStatus = new System.Windows.Forms.Label();
            this.btnBuildWorkspace = new System.Windows.Forms.Button();
            this.checkedListBoxFiles = new System.Windows.Forms.CheckedListBox();
            this.txtIsoPath = new System.Windows.Forms.TextBox();
            this.btnBrowseIso = new System.Windows.Forms.Button();
            this.tabExtract = new System.Windows.Forms.TabPage();
            this.txtExtractLog = new System.Windows.Forms.TextBox();
            this.lblExtractStatus = new System.Windows.Forms.Label();
            this.btnExtractStart = new System.Windows.Forms.Button();
            this.btnAddExt = new System.Windows.Forms.Button();
            this.txtCustomExt = new System.Windows.Forms.TextBox();
            this.btnExtractSelectAll = new System.Windows.Forms.Button();
            this.flpExtractExts = new System.Windows.Forms.FlowLayoutPanel();
            this.lblExtractHint = new System.Windows.Forms.Label();
            this.tabPrePack = new System.Windows.Forms.TabPage();
            this.txtPrePackCommands = new System.Windows.Forms.TextBox();
            this.btnPrePackSave = new System.Windows.Forms.Button();
            this.lblPrePackHint = new System.Windows.Forms.Label();
            this.tabPack = new System.Windows.Forms.TabPage();
            this.txtPackLog = new System.Windows.Forms.TextBox();
            this.lblPackStatus = new System.Windows.Forms.Label();
            this.btnPackStart = new System.Windows.Forms.Button();
            this.lblPackHint = new System.Windows.Forms.Label();
            this.tabControl.SuspendLayout();
            this.tabNew.SuspendLayout();
            this.tabExtract.SuspendLayout();
            this.tabPrePack.SuspendLayout();
            this.tabPack.SuspendLayout();
            this.SuspendLayout();
            // 
            // lblProjectTitle
            // 
            this.lblProjectTitle.AutoSize = true;
            this.lblProjectTitle.Location = new System.Drawing.Point(12, 9);
            this.lblProjectTitle.Name = "lblProjectTitle";
            this.lblProjectTitle.Size = new System.Drawing.Size(68, 15);
            this.lblProjectTitle.TabIndex = 0;
            this.lblProjectTitle.Text = "当前项目：";
            // 
            // cmbProjects
            // 
            this.cmbProjects.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.cmbProjects.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbProjects.FormattingEnabled = true;
            this.cmbProjects.Location = new System.Drawing.Point(86, 6);
            this.cmbProjects.Name = "cmbProjects";
            this.cmbProjects.Size = new System.Drawing.Size(679, 23);
            this.cmbProjects.TabIndex = 1;
            // 
            // tabControl
            // 
            this.tabControl.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.tabControl.Controls.Add(this.tabNew);
            this.tabControl.Controls.Add(this.tabExtract);
            this.tabControl.Controls.Add(this.tabPrePack);
            this.tabControl.Controls.Add(this.tabPack);
            this.tabControl.Location = new System.Drawing.Point(12, 35);
            this.tabControl.Name = "tabControl";
            this.tabControl.SelectedIndex = 0;
            this.tabControl.Size = new System.Drawing.Size(753, 502);
            this.tabControl.TabIndex = 2;
            // 
            // tabNew
            // 
            this.tabNew.Controls.Add(this.lblStatus);
            this.tabNew.Controls.Add(this.btnBuildWorkspace);
            this.tabNew.Controls.Add(this.checkedListBoxFiles);
            this.tabNew.Controls.Add(this.txtIsoPath);
            this.tabNew.Controls.Add(this.btnBrowseIso);
            this.tabNew.Location = new System.Drawing.Point(4, 24);
            this.tabNew.Name = "tabNew";
            this.tabNew.Padding = new System.Windows.Forms.Padding(3);
            this.tabNew.Size = new System.Drawing.Size(745, 474);
            this.tabNew.TabIndex = 0;
            this.tabNew.Text = "新建项目";
            this.tabNew.UseVisualStyleBackColor = true;
            // 
            // lblStatus
            // 
            this.lblStatus.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.lblStatus.Location = new System.Drawing.Point(6, 442);
            this.lblStatus.Name = "lblStatus";
            this.lblStatus.Size = new System.Drawing.Size(650, 23);
            this.lblStatus.TabIndex = 4;
            this.lblStatus.Text = "就绪";
            this.lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // btnBuildWorkspace
            // 
            this.btnBuildWorkspace.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnBuildWorkspace.Location = new System.Drawing.Point(662, 442);
            this.btnBuildWorkspace.Name = "btnBuildWorkspace";
            this.btnBuildWorkspace.Size = new System.Drawing.Size(75, 23);
            this.btnBuildWorkspace.TabIndex = 3;
            this.btnBuildWorkspace.Text = "创建";
            this.btnBuildWorkspace.UseVisualStyleBackColor = true;
            this.btnBuildWorkspace.Click += new System.EventHandler(this.btnBuildWorkspace_Click);
            // 
            // checkedListBoxFiles
            // 
            this.checkedListBoxFiles.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.checkedListBoxFiles.CheckOnClick = true;
            this.checkedListBoxFiles.FormattingEnabled = true;
            this.checkedListBoxFiles.IntegralHeight = false;
            this.checkedListBoxFiles.Location = new System.Drawing.Point(6, 39);
            this.checkedListBoxFiles.Name = "checkedListBoxFiles";
            this.checkedListBoxFiles.Size = new System.Drawing.Size(731, 391);
            this.checkedListBoxFiles.TabIndex = 2;
            // 
            // txtIsoPath
            // 
            this.txtIsoPath.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtIsoPath.Location = new System.Drawing.Point(6, 6);
            this.txtIsoPath.Name = "txtIsoPath";
            this.txtIsoPath.ReadOnly = true;
            this.txtIsoPath.Size = new System.Drawing.Size(650, 23);
            this.txtIsoPath.TabIndex = 1;
            // 
            // btnBrowseIso
            // 
            this.btnBrowseIso.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.btnBrowseIso.Location = new System.Drawing.Point(662, 6);
            this.btnBrowseIso.Name = "btnBrowseIso";
            this.btnBrowseIso.Size = new System.Drawing.Size(75, 23);
            this.btnBrowseIso.TabIndex = 0;
            this.btnBrowseIso.Text = "选择 ISO...";
            this.btnBrowseIso.UseVisualStyleBackColor = true;
            this.btnBrowseIso.Click += new System.EventHandler(this.btnBrowseIso_Click);
            // 
            // tabExtract
            // 
            this.tabExtract.Controls.Add(this.txtExtractLog);
            this.tabExtract.Controls.Add(this.lblExtractStatus);
            this.tabExtract.Controls.Add(this.btnExtractStart);
            this.tabExtract.Controls.Add(this.btnAddExt);
            this.tabExtract.Controls.Add(this.txtCustomExt);
            this.tabExtract.Controls.Add(this.btnExtractSelectAll);
            this.tabExtract.Controls.Add(this.flpExtractExts);
            this.tabExtract.Controls.Add(this.lblExtractHint);
            this.tabExtract.Location = new System.Drawing.Point(4, 24);
            this.tabExtract.Name = "tabExtract";
            this.tabExtract.Padding = new System.Windows.Forms.Padding(3);
            this.tabExtract.Size = new System.Drawing.Size(745, 474);
            this.tabExtract.TabIndex = 1;
            this.tabExtract.Text = "提取";
            this.tabExtract.UseVisualStyleBackColor = true;
            // 
            // txtExtractLog
            // 
            this.txtExtractLog.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtExtractLog.Location = new System.Drawing.Point(6, 128);
            this.txtExtractLog.Multiline = true;
            this.txtExtractLog.Name = "txtExtractLog";
            this.txtExtractLog.ReadOnly = true;
            this.txtExtractLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtExtractLog.Size = new System.Drawing.Size(733, 311);
            this.txtExtractLog.TabIndex = 11;
            // 
            // lblExtractStatus
            // 
            this.lblExtractStatus.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.lblExtractStatus.Location = new System.Drawing.Point(6, 445);
            this.lblExtractStatus.Name = "lblExtractStatus";
            this.lblExtractStatus.Size = new System.Drawing.Size(652, 23);
            this.lblExtractStatus.TabIndex = 10;
            this.lblExtractStatus.Text = "就绪";
            this.lblExtractStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // btnExtractStart
            // 
            this.btnExtractStart.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnExtractStart.Location = new System.Drawing.Point(664, 445);
            this.btnExtractStart.Name = "btnExtractStart";
            this.btnExtractStart.Size = new System.Drawing.Size(75, 23);
            this.btnExtractStart.TabIndex = 9;
            this.btnExtractStart.Text = "开始提取";
            this.btnExtractStart.UseVisualStyleBackColor = true;
            this.btnExtractStart.Click += new System.EventHandler(this.btnExtractStart_Click);
            // 
            // btnAddExt
            // 
            this.btnAddExt.Location = new System.Drawing.Point(315, 99);
            this.btnAddExt.Name = "btnAddExt";
            this.btnAddExt.Size = new System.Drawing.Size(75, 23);
            this.btnAddExt.TabIndex = 8;
            this.btnAddExt.Text = "添加后缀";
            this.btnAddExt.UseVisualStyleBackColor = true;
            this.btnAddExt.Click += new System.EventHandler(this.btnAddExt_Click);
            // 
            // txtCustomExt
            // 
            this.txtCustomExt.Location = new System.Drawing.Point(102, 99);
            this.txtCustomExt.Name = "txtCustomExt";
            this.txtCustomExt.Size = new System.Drawing.Size(207, 23);
            this.txtCustomExt.TabIndex = 7;
            // 
            // btnExtractSelectAll
            // 
            this.btnExtractSelectAll.Location = new System.Drawing.Point(6, 99);
            this.btnExtractSelectAll.Name = "btnExtractSelectAll";
            this.btnExtractSelectAll.Size = new System.Drawing.Size(90, 23);
            this.btnExtractSelectAll.TabIndex = 6;
            this.btnExtractSelectAll.Text = "全选/全不选";
            this.btnExtractSelectAll.UseVisualStyleBackColor = true;
            this.btnExtractSelectAll.Click += new System.EventHandler(this.btnExtractSelectAll_Click);
            // 
            // flpExtractExts
            // 
            this.flpExtractExts.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.flpExtractExts.AutoScroll = true;
            this.flpExtractExts.Location = new System.Drawing.Point(6, 43);
            this.flpExtractExts.Name = "flpExtractExts";
            this.flpExtractExts.Size = new System.Drawing.Size(733, 50);
            this.flpExtractExts.TabIndex = 5;
            // 
            // lblExtractHint
            // 
            this.lblExtractHint.AutoSize = true;
            this.lblExtractHint.Location = new System.Drawing.Point(6, 10);
            this.lblExtractHint.Name = "lblExtractHint";
            this.lblExtractHint.Size = new System.Drawing.Size(623, 30);
            this.lblExtractHint.TabIndex = 0;
            this.lblExtractHint.Text = "在上方选择项目，勾选要提取的类型，然后点击“开始提取”。\r\nAGI：工具会把 .agi 转成 .agi.png。请只把你修改过的 PNG 复制到 modified/，避免未修改图片参与差分补丁导致补丁过大。";
            // 
            // tabPrePack
            // 
            this.tabPrePack.Controls.Add(this.txtPrePackCommands);
            this.tabPrePack.Controls.Add(this.btnPrePackSave);
            this.tabPrePack.Controls.Add(this.lblPrePackHint);
            this.tabPrePack.Location = new System.Drawing.Point(4, 24);
            this.tabPrePack.Name = "tabPrePack";
            this.tabPrePack.Padding = new System.Windows.Forms.Padding(3);
            this.tabPrePack.Size = new System.Drawing.Size(745, 474);
            this.tabPrePack.TabIndex = 3;
            this.tabPrePack.Text = "重建前任务";
            this.tabPrePack.UseVisualStyleBackColor = true;
            // 
            // txtPrePackCommands
            // 
            this.txtPrePackCommands.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtPrePackCommands.Location = new System.Drawing.Point(6, 43);
            this.txtPrePackCommands.Multiline = true;
            this.txtPrePackCommands.Name = "txtPrePackCommands";
            this.txtPrePackCommands.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtPrePackCommands.Size = new System.Drawing.Size(733, 396);
            this.txtPrePackCommands.TabIndex = 2;
            // 
            // btnPrePackSave
            // 
            this.btnPrePackSave.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnPrePackSave.Location = new System.Drawing.Point(664, 445);
            this.btnPrePackSave.Name = "btnPrePackSave";
            this.btnPrePackSave.Size = new System.Drawing.Size(75, 23);
            this.btnPrePackSave.TabIndex = 1;
            this.btnPrePackSave.Text = "保存";
            this.btnPrePackSave.UseVisualStyleBackColor = true;
            this.btnPrePackSave.Click += new System.EventHandler(this.btnPrePackSave_Click);
            // 
            // lblPrePackHint
            // 
            this.lblPrePackHint.AutoSize = true;
            this.lblPrePackHint.Location = new System.Drawing.Point(6, 10);
            this.lblPrePackHint.Name = "lblPrePackHint";
            this.lblPrePackHint.Size = new System.Drawing.Size(401, 30);
            this.lblPrePackHint.TabIndex = 0;
            this.lblPrePackHint.Text = "在下方每行输入一个命令，例如：python 1.py 或 a.bat。\r\n重建前会按顺序执行所有命令，全部成功后才会开始重建 DAT。";
            // 
            // tabPack
            // 
            this.tabPack.Controls.Add(this.txtPackLog);
            this.tabPack.Controls.Add(this.lblPackStatus);
            this.tabPack.Controls.Add(this.btnPackStart);
            this.tabPack.Controls.Add(this.lblPackHint);
            this.tabPack.Location = new System.Drawing.Point(4, 24);
            this.tabPack.Name = "tabPack";
            this.tabPack.Padding = new System.Windows.Forms.Padding(3);
            this.tabPack.Size = new System.Drawing.Size(745, 474);
            this.tabPack.TabIndex = 2;
            this.tabPack.Text = "重建封包";
            this.tabPack.UseVisualStyleBackColor = true;
            // 
            // txtPackLog
            // 
            this.txtPackLog.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtPackLog.Location = new System.Drawing.Point(6, 58);
            this.txtPackLog.Multiline = true;
            this.txtPackLog.Name = "txtPackLog";
            this.txtPackLog.ReadOnly = true;
            this.txtPackLog.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtPackLog.Size = new System.Drawing.Size(733, 381);
            this.txtPackLog.TabIndex = 3;
            // 
            // lblPackStatus
            // 
            this.lblPackStatus.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.lblPackStatus.Location = new System.Drawing.Point(6, 445);
            this.lblPackStatus.Name = "lblPackStatus";
            this.lblPackStatus.Size = new System.Drawing.Size(652, 23);
            this.lblPackStatus.TabIndex = 2;
            this.lblPackStatus.Text = "就绪";
            this.lblPackStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // btnPackStart
            // 
            this.btnPackStart.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Right)));
            this.btnPackStart.Location = new System.Drawing.Point(664, 445);
            this.btnPackStart.Name = "btnPackStart";
            this.btnPackStart.Size = new System.Drawing.Size(75, 23);
            this.btnPackStart.TabIndex = 1;
            this.btnPackStart.Text = "重建";
            this.btnPackStart.UseVisualStyleBackColor = true;
            this.btnPackStart.Click += new System.EventHandler(this.btnPackStart_Click);
            // 
            // lblPackHint
            // 
            this.lblPackHint.AutoSize = true;
            this.lblPackHint.Location = new System.Drawing.Point(6, 10);
            this.lblPackHint.Name = "lblPackHint";
            this.lblPackHint.Size = new System.Drawing.Size(497, 30);
            this.lblPackHint.TabIndex = 0;
            this.lblPackHint.Text = "根据 list.json 重建 DAT 并更新 idx.dat 索引，输出到 packed/ 。\r\n优先使用modified/ (修改文件) ，没有则使用 original/ (原始文件)。\r\n为了最大程度减少差异补丁体积，modified/ 不应包含任何未修改的文件。";
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(777, 549);
            this.Controls.Add(this.tabControl);
            this.Controls.Add(this.cmbProjects);
            this.Controls.Add(this.lblProjectTitle);
            this.MinimumSize = new System.Drawing.Size(640, 400);
            this.Name = "MainForm";
            this.Text = "GA2本地化工具";
            this.tabControl.ResumeLayout(false);
            this.tabNew.ResumeLayout(false);
            this.tabNew.PerformLayout();
            this.tabExtract.ResumeLayout(false);
            this.tabExtract.PerformLayout();
            this.tabPrePack.ResumeLayout(false);
            this.tabPrePack.PerformLayout();
            this.tabPack.ResumeLayout(false);
            this.tabPack.PerformLayout();
            this.ResumeLayout(false);
            this.PerformLayout();
        }
    }
}