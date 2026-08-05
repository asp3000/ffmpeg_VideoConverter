// ============================================================================
//  MediaInfoForm.cs — 媒体信息窗：显示输入/输出文件的详细媒体信息。
//  从 ConversionTask 已缓存的元数据 + ffprobe 结果组装只读文本展示。
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace VideoConverter
{
    public class MediaInfoForm : Form
    {
        private readonly ConversionTask _task;

        public MediaInfoForm(ConversionTask task)
        {
            _task = task;
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = "媒体信息";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.White;
            this.Size = new Size(520, 560);
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // 标题：文件名
            var lblTitle = new Label
            {
                Location = new Point(16, 12),
                Size = new Size(480, 24),
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                Text = Path.GetFileName(_task.InputPath ?? ""),
                AutoEllipsis = true
            };
            toolTipFor(lblTitle, _task.InputPath);
            this.Controls.Add(lblTitle);

            // 信息正文
            var txtInfo = new TextBox
            {
                Location = new Point(16, 44),
                Size = new Size(480, 430),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(248, 246, 252),
                BorderStyle = BorderStyle.FixedSingle,
                Text = BuildInfoText()
            };
            this.Controls.Add(txtInfo);

            // 关闭按钮
            var btnClose = new Button
            {
                Location = new Point(396, 484),
                Size = new Size(100, 32),
                Text = "关闭",
                Font = new Font("Microsoft YaHei UI", 9F),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(124, 77, 255),
                DialogResult = DialogResult.OK
            };
            btnClose.FlatAppearance.BorderColor = Color.FromArgb(124, 77, 255);
            this.Controls.Add(btnClose);

            this.AcceptButton = btnClose;
        }

        private string BuildInfoText()
        {
            var sb = new StringBuilder();
            var t = _task;

            sb.AppendLine("──── 输入文件 ────");
            sb.AppendLine("文件名: " + SafeFileName(t.InputPath));
            sb.AppendLine("路径:   " + (t.InputPath ?? "-"));
            sb.AppendLine("格式:   " + (t.SourceFormat ?? "-"));
            sb.AppendLine("分辨率: " + (t.SourceResolution ?? "-"));
            sb.AppendLine("大小:   " + (t.SourceFileSize ?? "-"));
            sb.AppendLine("时长:   " + (t.SourceDuration ?? "-"));
            sb.AppendLine("视频编码: " + (t.SourceVideoCodec ?? "-"));
            sb.AppendLine("音频编码: " + (t.SourceAudioCodec ?? "-"));

            if (t.AudioTracks != null && t.AudioTracks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("──── 音轨 ────");
                for (int i = 0; i < t.AudioTracks.Count; i++)
                {
                    var tr = t.AudioTracks[i];
                    string sel = (t.SelectedAudioTrack != null && t.SelectedAudioTrack.Index == tr.Index) ? " ★" : "";
                    sb.AppendLine(string.Format("  [{0}] {1}{2}", tr.Index, tr.DisplayName, sel));
                }
            }

            if (t.SubtitleTracks != null && t.SubtitleTracks.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("──── 字幕轨 ────");
                for (int i = 0; i < t.SubtitleTracks.Count; i++)
                {
                    var st = t.SubtitleTracks[i];
                    string sel = (t.SelectedSubtitleTrack != null && t.SelectedSubtitleTrack.Index == st.Index
                                  && t.SelectedSubtitleTrack.IsExternal == st.IsExternal) ? " ★" : "";
                    sb.AppendLine(string.Format("  [{0}] {1}{2}", st.Index, st.DisplayName, sel));
                }
            }

            if (t.Preset != null)
            {
                sb.AppendLine();
                sb.AppendLine("──── 输出设置 ────");
                sb.AppendLine("目标格式:   " + (t.TargetFormat ?? "-"));
                sb.AppendLine("目标分辨率: " + (t.TargetResolution ?? "-"));
                sb.AppendLine("视频编码:   " + (t.TargetVideoEncoder ?? t.Preset.VideoCodec ?? "-"));
                sb.AppendLine("音频编码:   " + (t.TargetAudioEncoder ?? t.Preset.AudioCodec ?? "-"));
                if (!string.IsNullOrEmpty(t.Preset.VideoBitrate))
                    sb.AppendLine("视频码率:   " + t.Preset.VideoBitrate);
                if (!string.IsNullOrEmpty(t.Preset.AudioBitrate))
                    sb.AppendLine("音频码率:   " + t.Preset.AudioBitrate);
                if (!string.IsNullOrEmpty(t.Preset.FrameRate))
                    sb.AppendLine("帧率:       " + t.Preset.FrameRate);
            }

            if (t.Segments != null && t.Segments.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("──── 剪切段 ────");
                for (int i = 0; i < t.Segments.Count; i++)
                {
                    var seg = t.Segments[i];
                    sb.AppendLine(string.Format("  段 {0}: {1} → {2}",
                        i + 1,
                        FFmpegHelper.FormatDuration(seg.StartMs / 1000.0),
                        FFmpegHelper.FormatDuration(seg.EndMs / 1000.0)));
                }
                sb.AppendLine("合并模式: " + (t.MergeSegments ? "是" : "否（分多个文件输出）"));
            }

            if (t.Crop != null)
            {
                sb.AppendLine();
                sb.AppendLine("──── 裁剪 ────");
                sb.AppendLine(string.Format("  区域: {0}x{1} @ ({2},{3})", t.Crop.Width, t.Crop.Height, t.Crop.X, t.Crop.Y));
            }

            if (t.Rotation != 0)
            {
                sb.AppendLine();
                sb.AppendLine("──── 旋转 ────");
                string[] rotNames = { "无", "顺时针90°", "逆时针90°", "180°", "水平翻转", "垂直翻转" };
                sb.AppendLine("  " + (t.Rotation < rotNames.Length ? rotNames[t.Rotation] : t.Rotation.ToString()));
            }

            return sb.ToString();
        }

        private static string SafeFileName(string path)
        {
            try { return string.IsNullOrEmpty(path) ? "-" : Path.GetFileName(path); }
            catch { return "-"; }
        }

        private void toolTipFor(Control c, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var tt = new ToolTip();
            tt.SetToolTip(c, text);
        }
    }
}
