using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using GalaxyAngel2Localization.Archives.Artdink;
using GalaxyAngel2Localization.Utils; // IsoEntry

namespace GalaxyAngel2Localization.UI
{
    public partial class MainForm : Form
    {
        IsoEntry[] _isoEntries = Array.Empty<IsoEntry>();

        readonly List<string> _projects = new();
        readonly string _configIniPath = Path.Combine(AppContext.BaseDirectory, "GA2config.ini");
        AppConfig _config = new();

        List<CheckBox> _extractCheckBoxes = new();

        public MainForm()
        {
            InitializeComponent();

            _config = AppConfig.Load(_configIniPath);

            InitExtractExtensions();
            LoadProjects();
            LoadPrePackCommandsToUi();

            cmbProjects.SelectedIndexChanged += cmbProjects_SelectedIndexChanged;
        }

        void LoadProjects()
        {
            _projects.Clear();
            cmbProjects.Items.Clear();

            foreach (var name in _config.Projects)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                _projects.Add(name);
                cmbProjects.Items.Add(name);
            }

            if (!string.IsNullOrWhiteSpace(_config.CurrentProject))
            {
                int idx = cmbProjects.Items.IndexOf(_config.CurrentProject);
                if (idx >= 0)
                {
                    cmbProjects.SelectedIndex = idx;
                    return;
                }
            }

            if (cmbProjects.Items.Count > 0)
                cmbProjects.SelectedIndex = 0;
        }

        void RegisterProject(string projectName)
        {
            bool exists = _projects.Any(p => string.Equals(p, projectName, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                _projects.Add(projectName);
                cmbProjects.Items.Add(projectName);

                _config.Projects.Add(projectName);
            }

            _config.CurrentProject = projectName;
            _config.Save(_configIniPath);

            int idx = cmbProjects.Items.IndexOf(projectName);
            if (idx >= 0)
                cmbProjects.SelectedIndex = idx;
        }

        void cmbProjects_SelectedIndexChanged(object? sender, EventArgs e)
        {
            var name = cmbProjects.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                txtPrePackCommands.Text = string.Empty;
                return;
            }

            _config.CurrentProject = name;
            if (!_config.Projects.Contains(name))
                _config.Projects.Add(name);
            _config.Save(_configIniPath);

            LoadPrePackCommandsToUi();
        }

        void LoadPrePackCommandsToUi()
        {
            var name = cmbProjects.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(name))
            {
                txtPrePackCommands.Text = string.Empty;
                return;
            }

            if (_config.PrePackCommandsPerProject.TryGetValue(name, out var list))
                txtPrePackCommands.Text = string.Join(Environment.NewLine, list);
            else
                txtPrePackCommands.Text = string.Empty;
        }

        void btnPrePackSave_Click(object? sender, EventArgs e)
        {
            var projectName = cmbProjects.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                MessageBox.Show(this, "请先在顶部下拉框选择一个项目。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var cmds = txtPrePackCommands.Lines
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            _config.PrePackCommandsPerProject[projectName] = cmds;
            _config.Save(_configIniPath);
        }

        async void btnPackStart_Click(object? sender, EventArgs e)
        {
            var projectName = cmbProjects.SelectedItem as string;
            if (string.IsNullOrWhiteSpace(projectName))
            {
                MessageBox.Show(this, "请先在顶部下拉框选择一个项目。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var workspaceRoot = Path.Combine(AppContext.BaseDirectory, projectName);
            var listPath = Path.Combine(workspaceRoot, "list.json");

            if (!File.Exists(listPath))
            {
                MessageBox.Show(this,
                    "当前项目还没有生成工作目录，请先在“新建项目”里处理一次该 ISO。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnPackStart.Enabled = false;
            lblPackStatus.Text = "正在执行预处理任务...";
            txtPackLog.Clear();

            try
            {
                bool preOk = true;

                if (_config.PrePackCommandsPerProject.TryGetValue(projectName, out var cmdsForProj) &&
                    cmdsForProj.Count > 0)
                {
                    var cmds = cmdsForProj.ToArray();
                    var preResult = await Task.Run(() => RunPrePackCommands(cmds, workspaceRoot));

                    if (preResult.Log.Length > 0)
                        txtPackLog.AppendText(preResult.Log + Environment.NewLine);

                    if (!preResult.Success)
                    {
                        MessageBox.Show(this,
                            "预处理任务失败，已中止生成。",
                            "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        preOk = false;
                    }
                }
                else
                {
                    txtPackLog.AppendText("[TASK] 无预处理任务" + Environment.NewLine);
                }

                if (!preOk)
                {
                    lblPackStatus.Text = "预处理失败。";
                    return;
                }

                lblPackStatus.Text = "正在重建 DAT，请稍候...";

                void LogCallback(string msg)
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        txtPackLog.AppendText(msg + Environment.NewLine);
                    }));
                }

                var summary = await Task.Run(() =>
                    ArtdinkDatRebuilder.RebuildAllDats(workspaceRoot, LogCallback));

                ArtdinkIdxUpdater.UpdateIdxForAllDats(
                    workspaceRoot,
                    msg =>
                    {
                        if (!IsHandleCreated) return;
                        BeginInvoke(new Action(() =>
                        {
                            txtPackLog.AppendText(msg + Environment.NewLine);
                        }));
                    });    

                lblPackStatus.Text =
                    $"完成：重建 {summary.DatCount} 个 DAT，合计 {summary.TotalPaths} 条，" +
                    $"modified {summary.ModifiedCount} 条，original {summary.OriginalCount} 条。";

                MessageBox.Show(this,
                    $"重建完成：\n" +
                    $"  DAT 数量：{summary.DatCount}\n" +
                    $"  总路径数：{summary.TotalPaths}\n" +
                    $"  使用 modified：{summary.ModifiedCount}\n" +
                    $"  使用 original：{summary.OriginalCount}\n\n" +
                    $"用时 {summary.Elapsed.TotalSeconds:F1} 秒\n" +
                    $"输出目录：\n{summary.PackedRoot}\n\n" +
                    $"说明：这是全新构建的 DAT，可以直接用于生成游戏镜像或做差分补丁。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblPackStatus.Text = "重建失败。";
                MessageBox.Show(this, "重建 DAT 过程中出错：\n" + ex,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnPackStart.Enabled = true;
            }
        }

        (bool Success, string Log) RunPrePackCommands(string[] cmds, string workspaceRoot)
        {
            var sb = new System.Text.StringBuilder();
            bool ok = true;

            foreach (var cmd in cmds)
            {
                var line = cmd.Trim();
                if (line.Length == 0)
                    continue;

                sb.AppendLine($"[TASK] {line}");

                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/C " + line,
                    WorkingDirectory = workspaceRoot,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                try
                {
                    using var p = Process.Start(psi);
                    if (p == null)
                    {
                        sb.AppendLine("[ERROR] 无法启动进程");
                        ok = false;
                        break;
                    }

                    var stdout = p.StandardOutput.ReadToEnd();
                    var stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit();

                    if (stdout.Length > 0)
                        sb.AppendLine(stdout.TrimEnd());
                    if (stderr.Length > 0)
                        sb.AppendLine(stderr.TrimEnd());

                    if (p.ExitCode != 0)
                    {
                        sb.AppendLine($"[ERROR] 退出代码 {p.ExitCode}");
                        ok = false;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("[EXCEPTION] " + ex.Message);
                    ok = false;
                    break;
                }
            }

            return (ok, sb.ToString().TrimEnd());
        }
    }
}