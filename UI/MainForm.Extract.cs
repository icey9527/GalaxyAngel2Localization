using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using GalaxyAngel2Localization.Archives.Artdink;
using GalaxyAngel2Localization.Utils;        // AgiDecoder
using GalaxyAngel2Localization.Workspace;   // DatIndex
using Utils;                                // Artdink.Decompress

namespace GalaxyAngel2Localization.UI
{
    public partial class MainForm : Form
    {
        static readonly string[] DefaultExtractExtensions =
        {
            "tbl", "txt", "scn", "isb", "asb", "dat", "agi"
        };

        void InitExtractExtensions()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in DefaultExtractExtensions)
                names.Add(s);

            foreach (var s in _config.ExtractExtensions)
                if (!string.IsNullOrWhiteSpace(s))
                    names.Add(s.Trim());

            _config.ExtractExtensions.Clear();
            _config.ExtractExtensions.AddRange(names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
            _config.Save(_configIniPath);

            BuildExtractExtCheckboxes();
        }

        void BuildExtractExtCheckboxes()
        {
            flpExtractExts.SuspendLayout();
            flpExtractExts.Controls.Clear();
            _extractCheckBoxes.Clear();

            foreach (var name in _config.ExtractExtensions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                string ext = "." + name.TrimStart('.');

                var cb = new CheckBox
                {
                    AutoSize = true,
                    Text = name,
                    Tag = ext,
                    Margin = new Padding(4)
                };

                flpExtractExts.Controls.Add(cb);
                _extractCheckBoxes.Add(cb);
            }

            flpExtractExts.ResumeLayout();
        }

        static string? NormalizeExtName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return null;

            var s = input.Trim();
            if (s.StartsWith("#"))
                return null;

            s = s.TrimStart('.');
            if (s.Length == 0)
                return null;

            return s;
        }

        HashSet<string> GetSelectedExtensions()
        {
            var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var cb in _extractCheckBoxes)
            {
                if (cb.Checked && cb.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                    exts.Add(tag);
            }

            return exts;
        }

        void btnExtractSelectAll_Click(object? sender, EventArgs e)
        {
            if (_extractCheckBoxes.Count == 0)
                return;

            bool allChecked = _extractCheckBoxes.All(cb => cb.Checked);
            bool target = !allChecked;

            foreach (var cb in _extractCheckBoxes)
                cb.Checked = target;
        }

        void btnAddExt_Click(object? sender, EventArgs e)
        {
            var name = NormalizeExtName(txtCustomExt.Text);
            if (name == null)
            {
                MessageBox.Show(this,
                    "请输入合法的扩展名，例如：txt 或 .txt",
                    "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_config.ExtractExtensions.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                MessageBox.Show(this,
                    $"扩展名 {name} 已存在。",
                    "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _config.ExtractExtensions.Add(name);
            _config.Save(_configIniPath);

            BuildExtractExtCheckboxes();

            var justAdded = _extractCheckBoxes
                .FirstOrDefault(cb => string.Equals(cb.Text, name, StringComparison.OrdinalIgnoreCase));
            if (justAdded != null)
                justAdded.Checked = true;

            txtCustomExt.Clear();
        }

        async void btnExtractStart_Click(object? sender, EventArgs e)
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

            var exts = GetSelectedExtensions();
            if (exts.Count == 0)
            {
                MessageBox.Show(this,
                    "请至少勾选一个要提取的类型，或者先添加一个自定义后缀名。",
                    "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            btnExtractStart.Enabled = false;
            btnExtractSelectAll.Enabled = false;
            btnAddExt.Enabled = false;
            txtCustomExt.Enabled = false;

            lblExtractStatus.Text = "正在提取，请稍候...";
            txtExtractLog.Clear();

            try
            {
                void LogCallback(string msg)
                {
                    if (!IsHandleCreated) return;
                    BeginInvoke(new Action(() =>
                    {
                        txtExtractLog.AppendText(msg + Environment.NewLine);
                    }));
                }

                var extsCopy = new HashSet<string>(exts, StringComparer.OrdinalIgnoreCase);
                var result = await Task.Run(() => ExtractForProjectCore(projectName, extsCopy, LogCallback));

                // 如果你不在乎最终 summary.Log，可以去掉这行
                // txtExtractLog.Text = result.Log;
                lblExtractStatus.Text = $"提取完成：成功 {result.OkCount}，失败 {result.FailCount}。";

                MessageBox.Show(this,
                    $"提取完成：成功 {result.OkCount}，失败 {result.FailCount}。\n\n" +
                    $"提取目录：\n{result.ExtractRoot}\n\n" +
                    $"提示：只把你“真改过”的 PNG/TBL 等复制到 modified/，\n" +
                    $"不要把整包原始文件都搬过去，这样后面做差异补丁才不会很大。",
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblExtractStatus.Text = "提取失败。";
                MessageBox.Show(this, "提取过程中出错：\n" + ex,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnExtractStart.Enabled = true;
                btnExtractSelectAll.Enabled = true;
                btnAddExt.Enabled = true;
                txtCustomExt.Enabled = true;
            }
        }

        sealed class ExtractResult
        {
            public int OkCount { get; init; }
            public int FailCount { get; init; }
            public string ExtractRoot { get; init; } = string.Empty;
            public string Log { get; init; } = string.Empty;
        }

        ExtractResult ExtractForProjectCore(
            string projectName,
            HashSet<string> exts,
            Action<string>? logCallback)
        {
            var workspaceRoot = Path.Combine(AppContext.BaseDirectory, projectName);
            var listPath = Path.Combine(workspaceRoot, "list.json");
            var originalRoot = Path.Combine(workspaceRoot, "original");
            var extractRoot = Path.Combine(workspaceRoot, "extract");

            if (!File.Exists(listPath))
                throw new FileNotFoundException("找不到 list.json", listPath);
            if (!Directory.Exists(originalRoot))
                throw new DirectoryNotFoundException("找不到 original 目录: " + originalRoot);

            Directory.CreateDirectory(extractRoot);

            var json = File.ReadAllText(listPath);
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var indexDict = JsonSerializer.Deserialize<Dictionary<string, DatIndex>>(json, options)
                           ?? new Dictionary<string, DatIndex>();

            var pathsToExtract = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void ConsiderPath(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                var ext = Path.GetExtension(path);
                if (!exts.Contains(ext))
                    return;

                if (ext.Equals(".dat", StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = Path.GetFileName(path);
                    if (!fileName.Equals("slg_opdemo.dat", StringComparison.OrdinalIgnoreCase))
                        return;
                }

                pathsToExtract.Add(path);
            }

            foreach (var kvp in indexDict)
            {
                var datIndex = kvp.Value;
                foreach (var p in datIndex.Tab2)
                    ConsiderPath(p);
                foreach (var block in datIndex.Tab3.Values)
                {
                    foreach (var p in block)
                        ConsiderPath(p);
                }
            }

            if (pathsToExtract.Count == 0)
            {
                return new ExtractResult
                {
                    OkCount = 0,
                    FailCount = 0,
                    ExtractRoot = extractRoot,
                    Log = "没有匹配要提取的文件。\r\n"
                };
            }

            int okCount = 0;
            int failCount = 0;
            var logLines = new ConcurrentQueue<string>();

            var po = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
            int total = pathsToExtract.Count;
            int processed = 0;

            Parallel.ForEach(pathsToExtract, po, relPath =>
            {
                try
                {
                    var srcPath = Path.Combine(
                        originalRoot,
                        relPath.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(srcPath))
                    {
                        var msg = $"[缺失] {relPath}";
                        logLines.Enqueue(msg);
                        logCallback?.Invoke(msg);
                        Interlocked.Increment(ref failCount);
                        return;
                    }

                    byte[] content;

                    using (var fs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (Artdink.Decompress(fs, (int)fs.Length, out var dec))
                            content = dec;
                        else
                        {
                            fs.Position = 0;
                            using var ms = new MemoryStream();
                            fs.CopyTo(ms);
                            content = ms.ToArray();
                        }
                    }

                    var ext = Path.GetExtension(relPath);
                    string msgOk;

                    if (ext.Equals(".agi", StringComparison.OrdinalIgnoreCase))
                    {
                        string outRelPath = relPath + ".png";

                        var destPathPng = Path.Combine(
                            extractRoot,
                            outRelPath.Replace('/', Path.DirectorySeparatorChar));

                        var dirPng = Path.GetDirectoryName(destPathPng);
                        if (!string.IsNullOrEmpty(dirPng))
                            Directory.CreateDirectory(dirPng);

                        if (AgiDecoder.DecodeAgiToPng(content, destPathPng))
                        {
                            msgOk = $"[AGI->PNG] {relPath} -> {relPath}.png";
                        }
                        else
                        {
                            outRelPath = relPath;
                            var destPathAgi = Path.Combine(
                                extractRoot,
                                outRelPath.Replace('/', Path.DirectorySeparatorChar));

                            var dirAgi = Path.GetDirectoryName(destPathAgi);
                            if (!string.IsNullOrEmpty(dirAgi))
                                Directory.CreateDirectory(dirAgi);

                            File.WriteAllBytes(destPathAgi, content);
                            msgOk = $"[AGI保持原样] {relPath}";
                        }
                    }
                    else
                    {
                        string outRelPath = relPath;

                        var destPath = Path.Combine(
                            extractRoot,
                            outRelPath.Replace('/', Path.DirectorySeparatorChar));

                        var dir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);

                        File.WriteAllBytes(destPath, content);
                        msgOk = $"[普通] {relPath}";
                    }

                    logLines.Enqueue(msgOk);
                    logCallback?.Invoke(msgOk);
                    Interlocked.Increment(ref okCount);
                }
                catch
                {
                    var msg = $"[失败] {relPath}";
                    logLines.Enqueue(msg);
                    logCallback?.Invoke(msg);
                    Interlocked.Increment(ref failCount);
                }
                finally
                {
                    int done = Interlocked.Increment(ref processed);
                    if (done % 50 == 0)
                    {
                        var msg = $"[进度] {done}/{total}";
                        logLines.Enqueue(msg);
                        logCallback?.Invoke(msg);
                    }
                }
            });

            var logText = string.Join(Environment.NewLine, logLines);

            return new ExtractResult
            {
                OkCount = okCount,
                FailCount = failCount,
                ExtractRoot = extractRoot,
                Log = logText
            };
        }
    }
}