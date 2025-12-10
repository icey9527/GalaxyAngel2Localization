using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using GalaxyAngel2Localization.Utils; // IsoImage
using GalaxyAngel2Localization.Workspace;

namespace GalaxyAngel2Localization.UI
{
    public partial class MainForm : Form
    {
        #region 新建项目

        void btnBrowseIso_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "ISO 镜像 (*.iso)|*.iso|所有文件 (*.*)|*.*",
                Title = "选择 ISO 镜像"
            };

            if (ofd.ShowDialog(this) != DialogResult.OK)
                return;

            txtIsoPath.Text = ofd.FileName;
            LoadIsoEntries(ofd.FileName);
        }

        void LoadIsoEntries(string isoPath)
        {
            try
            {
                _isoEntries = IsoImage.Load(isoPath).ToArray();

                checkedListBoxFiles.Items.Clear();

                var datFiles = _isoEntries
                    .Where(e => !e.IsDirectory)
                    .Where(e => e.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
                    .Where(e => !e.Path.EndsWith("idx.dat", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase);

                foreach (var entry in datFiles)
                    checkedListBoxFiles.Items.Add(entry, false);

                lblStatus.Text =
                    $"读取到 {_isoEntries.Length} 个文件，DAT 可选 {checkedListBoxFiles.Items.Count} 个";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "解析 ISO 失败：\n" + ex.Message,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void btnBuildWorkspace_Click(object? sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtIsoPath.Text) || !File.Exists(txtIsoPath.Text))
            {
                MessageBox.Show(this, "请先选择有效的 ISO 文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (checkedListBoxFiles.CheckedItems.Count == 0)
            {
                MessageBox.Show(this, "请先勾选要处理的 DAT 文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var isoPath = txtIsoPath.Text;
            var imageName = Path.GetFileNameWithoutExtension(isoPath);
            var workspaceRoot = Path.Combine(AppPaths.AppRoot, imageName);

            try
            {
                var builder = new WorkspaceBuilder(workspaceRoot);

                using var isoStream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                foreach (var item in checkedListBoxFiles.CheckedItems)
                {
                    if (item is not IsoEntry entry)
                        continue;

                    const int sectorSize = 2048;
                    long offset = (long)entry.Lba * sectorSize;
                    long remaining = entry.Size;

                    isoStream.Position = offset;

                    using var ms = new MemoryStream((int)remaining);
                    var buffer = new byte[81920];

                    while (remaining > 0)
                    {
                        int toRead = (int)Math.Min(remaining, buffer.Length);
                        int read = isoStream.Read(buffer, 0, toRead);
                        if (read <= 0)
                            break;

                        ms.Write(buffer, 0, read);
                        remaining -= read;
                    }

                    ms.Position = 0;

                    var datName = Path.GetFileName(entry.Path);
                    builder.AddArtdinkDat(ms, datName);
                }

            {
                const int sectorSize = 2048;

                var idxEntry = Array.Find(
                    _isoEntries,
                    e => !e.IsDirectory &&
                        e.Path.EndsWith("idx.dat", StringComparison.OrdinalIgnoreCase));

                if (idxEntry != null)
                {
                    long idxOffset = (long)idxEntry.Lba * sectorSize;
                    long idxRemaining = idxEntry.Size;

                    isoStream.Position = idxOffset;

                    var buffer = new byte[81920];
                    var originalIdxPath = Path.Combine(workspaceRoot, "original", "idx.dat");
                    Directory.CreateDirectory(Path.GetDirectoryName(originalIdxPath)!);

                    using var fsIdx = new FileStream(originalIdxPath, FileMode.Create, FileAccess.Write, FileShare.None);

                    while (idxRemaining > 0)
                    {
                        int toRead = (int)Math.Min(idxRemaining, buffer.Length);
                        int read = isoStream.Read(buffer, 0, toRead);
                        if (read <= 0)
                            break;

                        fsIdx.Write(buffer, 0, read);
                        idxRemaining -= read;
                    }
                }
            }

                builder.SaveIndex();
                lblStatus.Text = "完成：已创建 original/、modified/、packed/ 以及 list.json";

                RegisterProject(imageName);

                cmbProjects.SelectedItem = imageName;

                MessageBox.Show(this,
                    "处理完成！\n\n工作目录：\n" + workspaceRoot,
                    "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "创建工作目录失败：\n" + ex,
                    "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        #endregion
    }
}