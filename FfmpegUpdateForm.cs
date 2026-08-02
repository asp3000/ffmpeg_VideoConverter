// ============================================================================
//  FfmpegUpdateForm.cs — download a newer ffmpeg build and replace the one
//  used by the app.
//    * Source 1: BtbN FFmpeg-Builds master zip (auto-extract, no dependency)
//    * Source 2: gyan.dev release-essentials 7z (requires system 7-Zip)
//  Latest stable version is probed from gyan.dev's release-version endpoint,
//  the same mechanism FFBatch uses.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoConverter
{
    public class FfmpegUpdateForm : Form
    {
        private readonly string _currentVersion;

        private Label lblCurrentValue;
        private Label lblLatestValue;
        private RadioButton rbBtbN;
        private RadioButton rbGyan;
        private ProgressBar pbProgress;
        private Label lblStatus;
        private Button btnDownload;
        private Button btnClose;
        private Button btnCheck;

        private const string GyanReleaseVersionUrl = "https://www.gyan.dev/ffmpeg/builds/release-version";
        private const string BtbNZipUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        private const string Gyan7zUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.7z";

        public FfmpegUpdateForm(string currentVersion)
        {
            _currentVersion = currentVersion;
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "ffmpeg 更新";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.AutoScaleMode = AutoScaleMode.None;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
        }

        private void InitializeComponent()
        {
            int labelX = 20;
            int valueX = 116;
            int y = 22;

            var lblCur = new Label { Text = "当前版本", Location = new Point(labelX, y + 3), Size = new Size(84, 20), AutoSize = false, ForeColor = Color.Gray };
            lblCurrentValue = new Label { Location = new Point(valueX, y), Size = new Size(360, 26), ForeColor = Color.FromArgb(45, 45, 45), Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold) };
            y += 38;

            var lblLatest = new Label { Text = "最新版本", Location = new Point(labelX, y + 3), Size = new Size(84, 20), AutoSize = false, ForeColor = Color.Gray };
            lblLatestValue = new Label { Text = "正在检查...", Location = new Point(valueX, y + 3), Size = new Size(280, 20), ForeColor = Color.FromArgb(90, 60, 160) };
            btnCheck = new Button
            {
                Text = "检查更新",
                Location = new Point(420, y),
                Size = new Size(82, 26),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat
            };
            btnCheck.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnCheck.Click += async (s, e) => await CheckLatestVersionAsync();
            y += 44;

            var lblSrc = new Label { Text = "下载源", Location = new Point(labelX, y), Size = new Size(80, 22), Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold), ForeColor = Color.FromArgb(45, 45, 45) };
            y += 30;
            rbBtbN = new RadioButton
            {
                Text = "BtbN 开发版 (zip，自动解压，推荐)",
                Location = new Point(labelX, y),
                Size = new Size(460, 24),
                Checked = true,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            y += 28;
            rbGyan = new RadioButton
            {
                Text = "gyan.dev 稳定版 (7z，需要系统安装 7-Zip)",
                Location = new Point(labelX, y),
                Size = new Size(460, 24),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            y += 46;

            lblStatus = new Label { Text = "", Location = new Point(labelX, y), Size = new Size(470, 20), ForeColor = Color.Gray };
            y += 26;
            pbProgress = new ProgressBar { Location = new Point(labelX, y), Size = new Size(470, 16) };
            y += 44;

            btnDownload = new Button
            {
                Text = "下载并更新",
                Location = new Point(236, y),
                Size = new Size(130, 34),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            btnDownload.FlatAppearance.BorderSize = 0;
            btnDownload.Click += BtnDownload_Click;

            btnClose = new Button
            {
                Text = "关闭",
                Location = new Point(382, y),
                Size = new Size(90, 34),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat
            };
            btnClose.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnClose.Click += (s, e) => this.Close();

            this.ClientSize = new Size(520, y + 60);

            this.Controls.Add(lblCur);
            this.Controls.Add(lblCurrentValue);
            this.Controls.Add(lblLatest);
            this.Controls.Add(lblLatestValue);
            this.Controls.Add(btnCheck);
            this.Controls.Add(lblSrc);
            this.Controls.Add(rbBtbN);
            this.Controls.Add(rbGyan);
            this.Controls.Add(lblStatus);
            this.Controls.Add(pbProgress);
            this.Controls.Add(btnDownload);
            this.Controls.Add(btnClose);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            lblCurrentValue.Text = string.IsNullOrEmpty(_currentVersion) ? "未安装" : _currentVersion;
            _ = CheckLatestVersionAsync();
        }

        /// <summary>从 gyan.dev 的 release-version 端点读取最新稳定版号（FFBatch 同款机制）。</summary>
        private async Task CheckLatestVersionAsync()
        {
            lblLatestValue.Text = "正在检查...";
            try
            {
                string v = await Task.Run(() =>
                {
                    using (var wc = new WebClientWithTimeout())
                        return wc.DownloadString(GyanReleaseVersionUrl).Trim();
                });
                lblLatestValue.Text = string.IsNullOrWhiteSpace(v) ? "未知" : v;
            }
            catch
            {
                lblLatestValue.Text = "获取失败（网络不可用？）";
            }
        }

        private async void BtnDownload_Click(object sender, EventArgs e)
        {
            bool zip = rbBtbN.Checked;
            if (!zip && Find7z() == null)
            {
                MessageBox.Show(this,
                    "未找到 7-Zip，无法解压 7z 文件。\n请安装 7-Zip，或改用 BtbN (zip) 来源。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnDownload.Enabled = false;
            btnCheck.Enabled = false;
            pbProgress.Value = 0;
            string tempDir = null;
            try
            {
                string url = zip ? BtbNZipUrl : Gyan7zUrl;
                string kind = zip ? "BtbN (zip)" : "gyan.dev (7z)";
                SetStatus("正在下载 " + kind + " ...");
                tempDir = Path.Combine(Path.GetTempPath(), "VideoConverter_ffmpeg_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string dlPath = Path.Combine(tempDir, zip ? "ffmpeg.zip" : "ffmpeg.7z");

                using (var wc = new WebClient())
                {
                    wc.DownloadProgressChanged += (s, ev) =>
                    {
                        if (IsDisposed) return;
                        pbProgress.Value = Math.Max(0, Math.Min(100, ev.ProgressPercentage));
                        SetStatus(string.Format("正在下载 {0} ... {1}%", kind, ev.ProgressPercentage));
                    };
                    await wc.DownloadFileTaskAsync(url, dlPath);
                }
                pbProgress.Value = 100;
                SetStatus("下载完成，正在解压...");
                await Task.Run(() => Extract(dlPath, tempDir, zip));

                string srcDir = FindBinDir(tempDir);
                string srcFfmpeg = Path.Combine(srcDir, "ffmpeg.exe");
                if (!File.Exists(srcFfmpeg)) throw new InvalidOperationException("解压后未找到 ffmpeg.exe。");
                string srcFfprobe = Path.Combine(srcDir, "ffprobe.exe");

                string dest = FFmpegHelper.GetFFmpegFolder();
                SetStatus("正在替换 " + Path.Combine(dest, "ffmpeg.exe") + " ...");
                BackupAndReplace(srcFfmpeg, srcFfprobe, dest);

                SetStatus("替换完成，验证新版本...");
                string newVer = await FFmpegHelper.GetInstalledVersionAsync();
                SetStatus("更新完成：ffmpeg " + (newVer ?? "未知"));
                MessageBox.Show(this,
                    "ffmpeg 已更新" + (newVer != null ? " 到 " + newVer : "") + "。\n正在进行的转换请重启程序后继续。",
                    "更新完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                SetStatus("更新失败：" + ex.Message);
                MessageBox.Show(this, "更新失败：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnDownload.Enabled = true;
                btnCheck.Enabled = true;
                if (tempDir != null)
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        private void SetStatus(string text)
        {
            if (IsDisposed || lblStatus == null) return;
            lblStatus.Text = text;
        }

        private static void Extract(string archive, string destDir, bool zip)
        {
            if (zip)
            {
                ZipFile.ExtractToDirectory(archive, destDir);
            }
            else
            {
                string sevenZip = Find7z();
                if (sevenZip == null) throw new InvalidOperationException("未找到 7-Zip。");
                var psi = new ProcessStartInfo
                {
                    FileName = sevenZip,
                    Arguments = "x -y \"" + archive + "\" -o\"" + destDir + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    if (p != null) p.WaitForExit();
                }
            }
        }

        private static string Find7z()
        {
            string[] candidates =
            {
                @"C:\Program Files\7-Zip\7z.exe",
                @"C:\Program Files (x86)\7-Zip\7z.exe"
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            try
            {
                var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';');
                foreach (var p in paths)
                {
                    string f = Path.Combine(p.Trim(), "7z.exe");
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
            return null;
        }

        /// <summary>递归查找含 ffmpeg.exe 的目录（BtbN/gyan.dev 解压后位于 bin\ 子目录）。</summary>
        private static string FindBinDir(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                if (File.Exists(Path.Combine(dir, "ffmpeg.exe"))) return dir;
                try
                {
                    foreach (var d in Directory.GetDirectories(dir)) stack.Push(d);
                }
                catch { }
            }
            return root;
        }

        /// <summary>备份旧 ffmpeg.exe → .old 后覆盖替换（含 ffprobe.exe）。</summary>
        private static void BackupAndReplace(string srcFfmpeg, string srcFfprobe, string destDir)
        {
            ReplaceOne(srcFfmpeg, destDir, "ffmpeg.exe");
            if (File.Exists(srcFfprobe))
                ReplaceOne(srcFfprobe, destDir, "ffprobe.exe");
        }

        private static void ReplaceOne(string src, string destDir, string name)
        {
            string target = Path.Combine(destDir, name);
            string backup = target + ".old";
            try { if (File.Exists(backup)) File.Delete(backup); } catch { }
            if (File.Exists(target))
            {
                try { File.Copy(target, backup, true); } catch { }
                File.Delete(target);   // throws IOException if the file is in use
            }
            File.Copy(src, target);
        }

        /// <summary>WebClient with a hard timeout for version probes.</summary>
        private class WebClientWithTimeout : WebClient
        {
            protected override WebRequest GetWebRequest(Uri address)
            {
                var req = base.GetWebRequest(address);
                if (req != null) req.Timeout = 10000;
                return req;
            }
        }
    }
}
