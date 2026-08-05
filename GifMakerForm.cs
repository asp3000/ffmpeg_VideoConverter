// ============================================================================
//  GifMakerForm.cs — GIF 制作器：从视频片段生成 GIF 动画。
//  使用 ffmpeg 的 palettegen + paletteuse 两遍法生成高质量 GIF。
// ============================================================================

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoConverter
{
    public class GifMakerForm : Form
    {
        private TextBox txtInput;
        private Button btnBrowse;
        private TextBox txtOutput;
        private Button btnBrowseOut;
        private NumericUpDown numStart;
        private NumericUpDown numDuration;
        private NumericUpDown numWidth;
        private NumericUpDown numFps;
        private NumericUpDown numLoop;
        private Button btnGenerate;
        private ProgressBar progressBar;
        private Label lblStatus;

        public GifMakerForm()
        {
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = "GIF 制作器";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.White;
            this.Size = new Size(540, 420);
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int y = 16;
            int labelW = 80;
            int inputX = 100;
            int inputW = 340;

            // 输入文件
            var lblIn = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "视频文件", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblIn);

            txtInput = new TextBox { Location = new Point(inputX, y), Size = new Size(inputW - 40, 23), ReadOnly = true };
            this.Controls.Add(txtInput);

            btnBrowse = new Button { Location = new Point(inputX + inputW - 32, y - 1), Size = new Size(32, 25), Text = "...", FlatStyle = FlatStyle.Flat };
            btnBrowse.Click += (s, e) =>
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "选择视频文件";
                    ofd.Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm|所有文件|*.*";
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        txtInput.Text = ofd.FileName;
                        txtOutput.Text = Path.ChangeExtension(ofd.FileName, ".gif");
                    }
                }
            };
            this.Controls.Add(btnBrowse);
            y += 36;

            // 输出文件
            var lblOut = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "输出 GIF", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblOut);

            txtOutput = new TextBox { Location = new Point(inputX, y), Size = new Size(inputW - 40, 23) };
            this.Controls.Add(txtOutput);

            btnBrowseOut = new Button { Location = new Point(inputX + inputW - 32, y - 1), Size = new Size(32, 25), Text = "...", FlatStyle = FlatStyle.Flat };
            btnBrowseOut.Click += (s, e) =>
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = "保存 GIF";
                    sfd.Filter = "GIF 动画|*.gif";
                    if (sfd.ShowDialog(this) == DialogResult.OK)
                        txtOutput.Text = sfd.FileName;
                }
            };
            this.Controls.Add(btnBrowseOut);
            y += 36;

            // 起始时间
            var lblStart = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "起始时间(秒)", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblStart);
            numStart = new NumericUpDown { Location = new Point(inputX, y), Size = new Size(80, 23), Minimum = 0, Maximum = 36000, Value = 0, DecimalPlaces = 1 };
            this.Controls.Add(numStart);
            y += 32;

            // 持续时间
            var lblDur = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "持续时长(秒)", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblDur);
            numDuration = new NumericUpDown { Location = new Point(inputX, y), Size = new Size(80, 23), Minimum = 0.5M, Maximum = 36000, Value = 5, DecimalPlaces = 1 };
            this.Controls.Add(numDuration);
            y += 32;

            // 宽度
            var lblWidth = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "宽度(像素)", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblWidth);
            numWidth = new NumericUpDown { Location = new Point(inputX, y), Size = new Size(80, 23), Minimum = 64, Maximum = 1920, Value = 480 };
            this.Controls.Add(numWidth);
            y += 32;

            // 帧率
            var lblFps = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "帧率(fps)", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblFps);
            numFps = new NumericUpDown { Location = new Point(inputX, y), Size = new Size(80, 23), Minimum = 5, Maximum = 30, Value = 15 };
            this.Controls.Add(numFps);

            // 循环次数
            var lblLoop = new Label { Location = new Point(220, y + 4), Size = new Size(80, 20), Text = "循环次数", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblLoop);
            numLoop = new NumericUpDown { Location = new Point(304, y), Size = new Size(80, 23), Minimum = 0, Maximum = 100, Value = 0 };
            var lblLoopHint = new Label { Location = new Point(390, y + 4), Size = new Size(100, 20), Text = "(0=无限)", ForeColor = Color.Gray };
            this.Controls.Add(lblLoopHint);
            this.Controls.Add(numLoop);
            y += 40;

            // 生成按钮
            btnGenerate = new Button
            {
                Location = new Point(16, y),
                Size = new Size(160, 36),
                Text = "生成 GIF",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold)
            };
            btnGenerate.FlatAppearance.BorderSize = 0;
            btnGenerate.Click += BtnGenerate_Click;
            this.Controls.Add(btnGenerate);
            y += 44;

            // 进度条
            progressBar = new ProgressBar { Location = new Point(16, y), Size = new Size(500, 20), Style = ProgressBarStyle.Marquee, Visible = false };
            this.Controls.Add(progressBar);

            lblStatus = new Label { Location = new Point(16, y + 24), Size = new Size(500, 20), Text = "", ForeColor = Color.Gray };
            this.Controls.Add(lblStatus);
        }

        private async void BtnGenerate_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtInput.Text) || !File.Exists(txtInput.Text))
            {
                MessageBox.Show(this, "请选择有效的视频文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (string.IsNullOrEmpty(txtOutput.Text))
            {
                MessageBox.Show(this, "请指定输出 GIF 路径。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string ffmpeg = FFmpegHelper.FFmpegPath;
            if (!File.Exists(ffmpeg))
            {
                MessageBox.Show(this, "未找到 ffmpeg.exe。", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            btnGenerate.Enabled = false;
            progressBar.Visible = true;
            lblStatus.Text = "正在生成 GIF（两遍编码）...";

            try
            {
                string input = txtInput.Text;
                string output = txtOutput.Text;
                double start = (double)numStart.Value;
                double duration = (double)numDuration.Value;
                int width = (int)numWidth.Value;
                int fps = (int)numFps.Value;
                int loop = (int)numLoop.Value;

                string palette = Path.Combine(Path.GetTempPath(), "gif_palette_" + Guid.NewGuid().ToString("N") + ".png");

                // 第一遍：生成调色板
                string args1 = string.Format("-y -ss {0:F1} -t {1:F1} -i \"{2}\" -vf \"fps={3},scale={4}:-1:flags=lanczos,palettegen\" \"{5}\"",
                    start, duration, input, fps, width, palette);

                // 第二遍：使用调色板生成 GIF
                string loopArg = loop > 0 ? string.Format(" -loop {0}", loop) : " -loop 0";
                string args2 = string.Format("-y -ss {0:F1} -t {1:F1} -i \"{2}\" -i \"{3}\" -lavfi \"fps={4},scale={5}:-1:flags=lanczos [x]; [x][1:v] paletteuse\"{6} \"{7}\"",
                    start, duration, input, palette, fps, width, loopArg, output);

                await RunFfmpegAsync(ffmpeg, args1);
                await RunFfmpegAsync(ffmpeg, args2);

                try { File.Delete(palette); } catch { }

                lblStatus.Text = "GIF 生成完成！";
                MessageBox.Show(this, "GIF 已生成至：\n" + output, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                lblStatus.Text = "生成失败：" + ex.Message;
                MessageBox.Show(this, "生成失败：\n" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnGenerate.Enabled = true;
                progressBar.Visible = false;
            }
        }

        private static async System.Threading.Tasks.Task RunFfmpegAsync(string ffmpeg, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-nostdin " + args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using (var proc = new Process { StartInfo = psi })
            {
                proc.Start();
                string stderr = await proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                if (proc.ExitCode != 0)
                    throw new InvalidOperationException("ffmpeg error: " + stderr.Substring(0, Math.Min(500, stderr.Length)));
            }
        }
    }
}
