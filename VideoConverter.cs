// ============================================================================
//  VideoConverter.cs — Converter main window for FFBatch.
//
//  Mirrors the VideoConverterUltimate "Converter" screen (see screenshot).
//  All heavy lifting is done by ffmpeg.exe in D:\AI\ffmpeg-8.1.2-full_build.
//
//  This file and its companions are placed in D:\AI\ffmpeg_batch\FFBatch\VideoConverter
//  and do not modify any existing FFBatch source files.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoConverter
{
    public partial class VideoConverter : Form
    {
        private readonly List<ConversionTask> _tasks = new List<ConversionTask>();
        private readonly List<TaskCard> _cards = new List<TaskCard>();
        private readonly ToolTip toolTip = new ToolTip();
        private bool _showCompleted;
        private FFmpegHelper.HardwareSupport _hwSupport;

        private bool _batchConverting;

        // 主界面正中间的 ffmpeg 版本号（点击打开下载更新窗）。
        private Label ffmpegVersionLabel;

        // The single currently-selected card (null when none selected). #42
        private RoundedPanel _selectedCard;
        // Pending hardware-encode preference, applied once HW detection completes. #47
        private bool _pendingHardware;

        public VideoConverter()
        {
            InitializeComponent();

            // Use the embedded application icon (VideoConverter.ico) for the window.
            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            // Load UniConverter preset database once (idempotent global cache). #43
            PresetDataStore.EnsureLoaded();
            if (!PresetDataStore.IsLoaded && PresetDataStore.LoadException != null)
            {
                MessageBox.Show(this,
                    "预设数据加载失败，将使用内置预设。\n错误：" + PresetDataStore.LoadException.Message,
                    "预设加载提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Restore persisted check-box state (高速转换 / 硬件编码). #47
            AppSettings.Load();
            highSpeedCheck.Checked = AppSettings.HighSpeed;
            _pendingHardware = AppSettings.Hardware;

            // Allow dropping files anywhere on the window / list.
            this.AllowDrop = true;
            this.taskListPanel.AllowDrop = true;
            this.DragEnter += VideoConverter_DragEnter;
            this.DragDrop += VideoConverter_DragDrop;
            this.taskListPanel.DragEnter += VideoConverter_DragEnter;
            this.taskListPanel.DragDrop += VideoConverter_DragDrop;

            SetupPresets();
            SetupSaveTo();
            SetupFfmpegVersionLabel();
            ApplyCheckStyle(highSpeedCheck);
            ApplyCheckStyle(hardwareCheck);
            UpdateTabStyles();
            UpdateCount();

            // Probe ffmpeg for hardware encoders in the background; the checkbox
            // is enabled (and labelled with the GPU vendor) only if supported.
            DetectHardwareOnLoad();
        }

        #region Setup

        private PresetOption _globalPreset;
        private bool _suppressSaveToEvent;
        private int _lastSaveToIndex;

        private void SetupPresets()
        {
            // Restore the last chosen "转换到" preset from settings if possible.
            _globalPreset = RestoreSavedPreset()
                ?? FindPresetByName("MP4", "1080")
                ?? FindPresetByName("MP4", "与源文件相同")
                ?? PresetOption.BuiltInAll.FirstOrDefault(p =>
                    string.Equals(p.FormatName, "MP4", StringComparison.OrdinalIgnoreCase))
                ?? PresetOption.MP4_1080;

            UpdateConvertToDisplay();
        }

        /// <summary>按上次保存的 FormatId + Name 恢复「转换到」预设；找不到返回 null。</summary>
        private PresetOption RestoreSavedPreset()
        {
            try
            {
                string fmtId = AppSettings.ConvertToFormatId;
                string name = AppSettings.ConvertToPresetName;
                if (string.IsNullOrWhiteSpace(name)) return null;

                PresetOption found = null;
                if (!string.IsNullOrWhiteSpace(fmtId))
                {
                    var fmt = PresetDataStore.FindFormat(fmtId);
                    if (fmt != null)
                        found = fmt.Presets.FirstOrDefault(p =>
                            string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (found == null)
                        found = PresetDataStore.CustomPresets.FirstOrDefault(p =>
                            string.Equals(p.Name, name, StringComparison.Ordinal) &&
                            string.Equals(p.FormatId, fmtId, StringComparison.Ordinal));
                }
                if (found == null)
                    found = PresetDataStore.CustomPresets.FirstOrDefault(p =>
                        string.Equals(p.Name, name, StringComparison.Ordinal));
                return found != null ? found.Clone() : null;
            }
            catch { return null; }
        }

        private PresetOption FindPresetByName(string formatName, string presetNameHint)
        {
            foreach (var cat in PresetDataStore.Categories)
            {
                if (!PresetDataStore.FormatsByCategory.ContainsKey(cat)) continue;
                foreach (var fmt in PresetDataStore.FormatsByCategory[cat])
                {
                    var p = fmt.Presets.FirstOrDefault(x =>
                        string.Equals(x.FormatName, formatName, StringComparison.OrdinalIgnoreCase) &&
                        (x.Name.Contains(presetNameHint) ||
                         (!string.IsNullOrEmpty(x.ResolutionLabel) && x.ResolutionLabel.Contains(presetNameHint))));
                    if (p != null) return p.Clone();
                }
            }
            return null;
        }

        private void UpdateConvertToDisplay()
        {
            if (_globalPreset == null) _globalPreset = PresetOption.MP4_1080;
            convertToButton.Text = string.Format("{0} / {1}", _globalPreset.FormatName, _globalPreset.Name);
            convertToButton.Tag = _globalPreset;
        }

        private void SetupSaveTo()
        {
            saveToCombo.Items.Clear();
            saveToCombo.Items.Add("与源文件夹相同");
            if (AppSettings.SaveToFolders != null)
            {
                foreach (var dir in AppSettings.SaveToFolders)
                {
                    if (!string.IsNullOrWhiteSpace(dir) && !saveToCombo.Items.Contains(dir))
                        saveToCombo.Items.Add(dir);
                }
            }
            saveToCombo.Items.Add("选择文件夹...");

            saveToCombo.SelectedIndexChanged += SaveToCombo_SelectedIndexChanged;

            // Restore the last chosen "保存到" value.
            _suppressSaveToEvent = true;
            int idx = string.IsNullOrEmpty(AppSettings.SaveToValue)
                ? -1
                : saveToCombo.Items.IndexOf(AppSettings.SaveToValue);
            saveToCombo.SelectedIndex = idx >= 0 ? idx : 0;
            _suppressSaveToEvent = false;
            _lastSaveToIndex = saveToCombo.SelectedIndex;
        }

        /// <summary>
        /// "选择文件夹..." triggers a folder picker; the chosen directory is
        /// appended to the dropdown items and persisted to settings.
        /// </summary>
        private void SaveToCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressSaveToEvent) return;
            string sel = saveToCombo.SelectedItem as string;
            if (string.Equals(sel, "选择文件夹...", StringComparison.Ordinal))
            {
                int fallback = _lastSaveToIndex >= 0 ? _lastSaveToIndex : 0;
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择输出文件夹";
                    if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                    {
                        string dir = fbd.SelectedPath;
                        _suppressSaveToEvent = true;
                        if (!saveToCombo.Items.Contains(dir))
                            saveToCombo.Items.Insert(saveToCombo.Items.Count - 1, dir);
                        saveToCombo.SelectedItem = dir;
                        _suppressSaveToEvent = false;
                        if (!AppSettings.SaveToFolders.Contains(dir))
                            AppSettings.SaveToFolders.Add(dir);
                        PersistSaveTo();
                    }
                    else
                    {
                        // Cancel: revert to the previously selected item.
                        _suppressSaveToEvent = true;
                        saveToCombo.SelectedIndex = fallback;
                        _suppressSaveToEvent = false;
                    }
                }
            }
            else
            {
                PersistSaveTo();
            }
            _lastSaveToIndex = saveToCombo.SelectedIndex;
        }

        private void PersistSaveTo()
        {
            AppSettings.SaveToValue = saveToCombo.SelectedItem as string;
            AppSettings.Save();
        }

        private void PersistConvertToPreset()
        {
            if (_globalPreset == null) return;
            AppSettings.ConvertToFormatId = _globalPreset.FormatId;
            AppSettings.ConvertToPresetName = _globalPreset.Name;
            AppSettings.Save();
        }

        /// <summary>当前「保存到」选中的实际目录；"与源文件夹相同" 或占位项返回 null。</summary>
        private string GetSelectedSaveToFolder()
        {
            string sel = saveToCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(sel) ||
                string.Equals(sel, "与源文件夹相同", StringComparison.Ordinal) ||
                string.Equals(sel, "选择文件夹...", StringComparison.Ordinal))
                return null;
            return sel;
        }

        /// <summary>
        /// 在顶部工具栏正中间创建可点击的 ffmpeg 版本号，点击弹出下载更新窗。
        /// </summary>
        private void SetupFfmpegVersionLabel()
        {
            ffmpegVersionLabel = new Label
            {
                Text = "ffmpeg ...",
                AutoSize = true,
                Cursor = Cursors.Hand,
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(124, 77, 255),
                Tag = "版本号"
            };
            ffmpegVersionLabel.Click += FfmpegVersionLabel_Click;
            topPanel.Controls.Add(ffmpegVersionLabel);
            CenterFfmpegVersionLabel();

            this.Resize += (s, e) => CenterFfmpegVersionLabel();

            // 后台读取本地 ffmpeg 版本，不阻塞 UI。
            _ = LoadFfmpegVersionAsync();
        }

        private void CenterFfmpegVersionLabel()
        {
            if (ffmpegVersionLabel == null || topPanel == null) return;
            int x = Math.Max(8, (topPanel.ClientSize.Width - ffmpegVersionLabel.PreferredWidth) / 2);
            ffmpegVersionLabel.Location = new Point(x, (topPanel.ClientSize.Height - ffmpegVersionLabel.PreferredHeight) / 2 + 1);
        }

        private async Task LoadFfmpegVersionAsync()
        {
            string v = await FFmpegHelper.GetInstalledVersionAsync();
            if (IsDisposed || ffmpegVersionLabel == null) return;
            ffmpegVersionLabel.Text = string.IsNullOrEmpty(v) ? "ffmpeg 未安装" : "ffmpeg " + v;
            CenterFfmpegVersionLabel();
        }

        private void FfmpegVersionLabel_Click(object sender, EventArgs e)
        {
            using (var dlg = new FfmpegUpdateForm(ffmpegVersionLabel.Text.Replace("ffmpeg ", "")))
            {
                dlg.ShowDialog(this);
            }
            _ = LoadFfmpegVersionAsync();
        }

        #endregion

        #region File handling

        private async void AddFilesButton_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog(this) != DialogResult.OK) return;
            await AddFiles(openFileDialog.FileNames);
        }

        /// <summary>
        /// Shared add routine used by the file dialog AND drag-and-drop.
        /// </summary>
        private async Task AddFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;

            var preset = _globalPreset ?? PresetOption.MP4_1080;
            bool added = false;

            foreach (string file in files)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
                if (_tasks.Any(t => string.Equals(t.InputPath, file, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var task = new ConversionTask
                {
                    InputPath = file,
                    Preset = preset,
                    SaveToFolder = GetSelectedSaveToFolder()
                };

                // Try to read source metadata with ffprobe.
                try
                {
                    var info = await FFmpegHelper.ProbeDetailedAsync(file);
                    task.SourceFormat = string.IsNullOrEmpty(info.VideoCodec)
                        ? Path.GetExtension(file).TrimStart('.').ToUpperInvariant()
                        : info.VideoCodec.ToUpperInvariant();
                    task.SourceResolution = info.Width > 0 && info.Height > 0
                        ? string.Format("{0} x {1}", info.Width, info.Height)
                        : "-";
                    task.SourceFileSize = info.SizeBytes > 0
                        ? FFmpegHelper.FormatFileSize(info.SizeBytes)
                        : FFmpegHelper.FormatFileSize(new FileInfo(file).Length);
                    task.SourceDurationSeconds = info.DurationSeconds;
                    task.SourceDuration = FFmpegHelper.FormatDuration(info.DurationSeconds);
                    task.AudioTracks = info.AudioTracks ?? new System.Collections.Generic.List<AudioTrackInfo>();
                    task.SubtitleTracks = info.SubtitleTracks ?? new System.Collections.Generic.List<SubtitleTrackInfo>();
                    task.SelectedAudioTrack = task.AudioTracks.Count > 0 ? task.AudioTracks[0] : null;
                    task.SelectedSubtitleTrack = task.SubtitleTracks.Count > 0 ? task.SubtitleTracks[0] : null;
                    task.EstimatedTargetSize = EstimateTargetSize(info, task.Preset);
                }
                catch
                {
                    task.SourceFormat = Path.GetExtension(file).TrimStart('.').ToUpperInvariant();
                    task.SourceResolution = "-";
                    task.SourceFileSize = FFmpegHelper.FormatFileSize(new FileInfo(file).Length);
                    task.SourceDurationSeconds = 0;
                    task.SourceDuration = "00:00:00";
                    task.EstimatedTargetSize = "-";
                }

                // Thumbnail at 1s.
                try
                {
                    task.Thumbnail = await FFmpegHelper.GetThumbnailAsync(file, 160, 90);
                }
                catch
                {
                    task.Thumbnail = CreatePlaceholderThumbnail();
                }

                _tasks.Add(task);
                added = true;
            }

            if (added) RefreshTaskList();
        }

        private string EstimateTargetSize(MediaInfo info, PresetOption preset)
        {
            try
            {
                double seconds = info.DurationSeconds;
                if (seconds <= 0) return "-";

                long videoBps = ParseBitRate(preset.VideoBitrate);
                long audioBps = ParseBitRate(preset.AudioBitrate);
                if (videoBps <= 0 && audioBps <= 0) return "-";

                long totalBytes = (long)((videoBps + audioBps) * seconds / 8);
                return FFmpegHelper.FormatFileSize(totalBytes);
            }
            catch { return "-"; }
        }

        private long ParseBitRate(string bitrate)
        {
            if (string.IsNullOrWhiteSpace(bitrate)) return 0;
            string s = bitrate.Trim().ToLowerInvariant();
            double value;
            if (s.EndsWith("k"))
            {
                if (double.TryParse(s.Substring(0, s.Length - 1), out value))
                    return (long)(value * 1000);
            }
            else if (s.EndsWith("m"))
            {
                if (double.TryParse(s.Substring(0, s.Length - 1), out value))
                    return (long)(value * 1000000);
            }
            else if (double.TryParse(s, out value))
            {
                return (long)value;
            }
            return 0;
        }

        private Image CreatePlaceholderThumbnail()
        {
            var bmp = new Bitmap(160, 90);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(224, 218, 240));
                using (var pen = new Pen(Color.FromArgb(180, 170, 210), 2))
                {
                    g.DrawRectangle(pen, 2, 2, 155, 85);
                }
            }
            return bmp;
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            // "清空" — remove every queued file (with a safety confirmation). #51
            if (_tasks.Count == 0) return;
            if (MessageBox.Show(this, "确定要清空当前列表中的所有文件吗？", "清空确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (var t in _tasks)
            {
                if (t.Thumbnail != null) t.Thumbnail.Dispose();
            }
            _tasks.Clear();
            _selectedCard = null;
            RefreshTaskList();
        }

        #endregion

        #region Task cards

        private void RefreshTaskList()
        {
            // Dispose old cards.
            foreach (var card in _cards)
            {
                card.Panel.Dispose();
            }
            _cards.Clear();
            _selectedCard = null;
            taskListPanel.Controls.Clear();

            var visible = _showCompleted
                ? _tasks.Where(t => t.Status == TaskStatus.Completed)
                : _tasks.Where(t => t.Status != TaskStatus.Completed);

            foreach (var task in visible)
            {
                var card = BuildTaskCard(task);
                _cards.Add(card);
                taskListPanel.Controls.Add(card.Panel);
            }

            UpdateCount();
        }

        private TaskCard BuildTaskCard(ConversionTask task)
        {
            int cardW = Math.Max(900, taskListPanel.ClientSize.Width - 40);
            int cardH = 158;

            var cardPanel = new RoundedPanel
            {
                Width = cardW,
                Height = cardH,
                Margin = new Padding(0, 0, 0, 12),
                FillColor = Color.FromArgb(245, 242, 252),
                BorderColor = Color.FromArgb(215, 207, 235)
            };

            // Thumbnail.
            var thumb = new PictureBox
            {
                Location = new Point(12, 12),
                Size = new Size(150, 84),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = task.Thumbnail,
                BackColor = Color.FromArgb(235, 231, 247)
            };
            cardPanel.Controls.Add(thumb);

            // Play overlay — hidden until the thumbnail is hovered, then shown
            // centered over the preview. Clicking it (or the thumbnail) opens
            // the OpenGL preview window. #50
            var playOverlay = new Label
            {
                Location = new Point(thumb.Left + 55, thumb.Top + 22),
                Size = new Size(40, 40),
                BackColor = Color.FromArgb(124, 77, 255),
                Text = "▶",
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Regular),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };
            cardPanel.Controls.Add(playOverlay);
            playOverlay.BringToFront();

            // Preview behaviour: show the play button on hover, open the OpenGL
            // preview window on click. #50
            void ShowOverlay(bool show)
            {
                if (!playOverlay.IsDisposed) playOverlay.Visible = show;
            }
            thumb.MouseEnter += (s, e) => ShowOverlay(true);
            thumb.MouseLeave += (s, e) => ShowOverlay(false);
            thumb.Click += (s, e) =>
            {
                SelectCard(cardPanel);
                OpenPlayer(task);
            };
            playOverlay.MouseEnter += (s, e) => ShowOverlay(true);
            playOverlay.MouseLeave += (s, e) => ShowOverlay(false);
            playOverlay.Click += (s, e) =>
            {
                SelectCard(cardPanel);
                OpenPlayer(task);
            };

            // ---- Input column ----
            int inputX = 174;
            int row1Y = 12;
            int row2Y = 36;
            int row3Y = 60;
            int row4Y = 88;

            // Input file name: width for ~15 Chinese chars.
            string inFileName = Path.GetFileName(task.InputPath);
            var lblInName = new Label
            {
                Location = new Point(inputX, row1Y),
                Size = new Size(220, 22),
                Text = inFileName,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            toolTip.SetToolTip(lblInName, inFileName);
            cardPanel.Controls.Add(lblInName);

            var lblInFormat = AddInfoLabel(cardPanel, inputX, row2Y, "格式: " + task.SourceFormat, 110);
            var lblInResolution = AddInfoLabel(cardPanel, inputX + 110, row2Y, "分辨率: " + task.SourceResolution, 110);
            var lblInSize = AddInfoLabel(cardPanel, inputX, row3Y, "大小: " + task.SourceFileSize, 110);
            var lblInDuration = AddInfoLabel(cardPanel, inputX + 110, row3Y, "时长: " + task.SourceDuration, 110);

            // Edit icon on input row 4 -> themed button (light fill, dark text,
            // dark border). #48
            var btnEditVideo = CreateThemeButton("✎", inputX, row4Y, "视频编辑");
            btnEditVideo.Click += (s, e) => OpenVideoEdit(task);
            cardPanel.Controls.Add(btnEditVideo);

            // ---- Output column ----
            int outputX = 430;
            int outputW = Math.Max(200, cardW - outputX - 130); // leave room for convert button

            // Output file name + edit icon + delete icon.
            var lblOutName = new Label
            {
                Location = new Point(outputX, row1Y),
                Size = new Size(outputW - 60, 22),
                Text = task.GetOutputFileName(),
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            toolTip.SetToolTip(lblOutName, task.OutputPath);
            cardPanel.Controls.Add(lblOutName);

            var txtOutName = new TextBox
            {
                Location = new Point(outputX, row1Y - 1),
                Size = new Size(outputW - 60, 23),
                Text = string.IsNullOrWhiteSpace(task.CustomOutputName)
                    ? Path.GetFileNameWithoutExtension(task.OutputPath)
                    : task.CustomOutputName,
                Visible = false,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular)
            };
            cardPanel.Controls.Add(txtOutName);

            Button btnEditName = null;
            btnEditName = CreateThemeButton("✎", outputX + outputW - 54, row1Y - 2, "修改文件名");
            btnEditName.Click += (s, e) =>
            {
                if (txtOutName.Visible)
                {
                    // Already editing; explicit save via Enter/LostFocus.
                    cardPanel.Focus();
                }
                else
                {
                    ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
                }
            };
            cardPanel.Controls.Add(btnEditName);

            txtOutName.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    txtOutName.Text = string.IsNullOrWhiteSpace(task.CustomOutputName)
                        ? Path.GetFileNameWithoutExtension(task.OutputPath)
                        : task.CustomOutputName;
                    ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
                }
            };
            txtOutName.LostFocus += (s, e) =>
            {
                if (txtOutName.Visible) ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
            };

            // Delete icon top-right.
            var btnDelete = CreateIconButton("🗑", cardW - 34, 8, "删除此文件");
            btnDelete.ForeColor = Color.FromArgb(180, 80, 80);
            btnDelete.Click += (s, e) =>
            {
                if (task.Status == TaskStatus.Converting && task.Cancellation != null)
                {
                    try { task.Cancellation.Cancel(); } catch { }
                }
                _tasks.Remove(task);
                if (task.Thumbnail != null) task.Thumbnail.Dispose();
                RefreshTaskList();
            };
            cardPanel.Controls.Add(btnDelete);

            var lblOutFormat = AddInfoLabel(cardPanel, outputX, row2Y, "格式: " + task.TargetFormat, 110);
            var lblOutResolution = AddInfoLabel(cardPanel, outputX + 110, row2Y, "分辨率: " + task.TargetResolution, 130);
            var lblOutSize = AddInfoLabel(cardPanel, outputX, row3Y, "预计大小: " + (task.EstimatedTargetSize ?? "-"), 130);
            var lblOutDuration = AddInfoLabel(cardPanel, outputX + 130, row3Y, "输出时长: " + task.TargetDuration, 130);

            // Row 4: preset selector (white bordered panel, styled like the
            // bottom "转换到" control) + subtitle + audio. #45
            int r4x = outputX;
            var presetPanel = new Panel
            {
                Location = new Point(r4x, row4Y - 1),
                Size = new Size(168, 26),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var btnPreset = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Text = task.Preset.Name,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45),
                UseVisualStyleBackColor = false
            };
            btnPreset.FlatAppearance.BorderSize = 0;
            var btnPresetGear = new Button
            {
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                Text = "⚙",
                Size = new Size(26, 26),
                Font = new Font("Microsoft YaHei UI", 10F),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45),
                UseVisualStyleBackColor = false
            };
            btnPresetGear.FlatAppearance.BorderSize = 0;
            btnPreset.Click += (s, e) => OpenPresetSelection(task, btnPreset, lblOutFormat, lblOutResolution, lblOutSize);
            btnPresetGear.Click += (s, e) => OpenPresetEdit(task, btnPreset, lblOutFormat, lblOutResolution, lblOutSize);
            presetPanel.Controls.Add(btnPreset);
            presetPanel.Controls.Add(btnPresetGear);
            cardPanel.Controls.Add(presetPanel);

            var cmbSubtitle = CreateDropDown(r4x + 150, row4Y, 110);
            cmbSubtitle.Items.Add("无字幕");
            foreach (var st in task.SubtitleTracks)
                cmbSubtitle.Items.Add(st.DisplayName);
            cmbSubtitle.SelectedIndex = task.SelectedSubtitleTrack != null
                ? Math.Max(0, task.SubtitleTracks.IndexOf(task.SelectedSubtitleTrack) + 1)
                : 0;
            cmbSubtitle.SelectedIndexChanged += (s, e) =>
            {
                int idx = cmbSubtitle.SelectedIndex;
                task.SelectedSubtitleTrack = idx > 0 ? task.SubtitleTracks[idx - 1] : null;
            };
            cardPanel.Controls.Add(cmbSubtitle);

            var cmbAudio = CreateDropDown(r4x + 266, row4Y, 150);
            cmbAudio.Items.Add("无音频");
            foreach (var at in task.AudioTracks)
                cmbAudio.Items.Add(at.DisplayName);
            cmbAudio.SelectedIndex = task.SelectedAudioTrack != null
                ? Math.Max(0, task.AudioTracks.IndexOf(task.SelectedAudioTrack) + 1)
                : (task.AudioTracks.Count > 0 ? 1 : 0);
            cmbAudio.SelectedIndexChanged += (s, e) =>
            {
                int idx = cmbAudio.SelectedIndex;
                task.SelectedAudioTrack = idx > 0 ? task.AudioTracks[idx - 1] : null;
            };
            cardPanel.Controls.Add(cmbAudio);

            // ---- Convert / Cancel button ----
            var btnConvert = new Button
            {
                Location = new Point(cardW - 110, 38),
                Size = new Size(90, 72),
                Text = "转换",
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnConvert.FlatAppearance.BorderSize = 0;
            cardPanel.Controls.Add(btnConvert);

            // ---- Progress bar at bottom ----
            var progress = new ProgressBar
            {
                Location = new Point(12, cardH - 18),
                Size = new Size(cardW - 24, 10),
                Maximum = 100,
                Value = 0,
                Visible = false
            };
            cardPanel.Controls.Add(progress);

            var lblStatus = new Label
            {
                Location = new Point(12, cardH - 36),
                Size = new Size(cardW - 140, 16),
                Text = "",
                Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular),
                ForeColor = Color.FromArgb(120, 90, 200),
                BackColor = Color.Transparent,
                Visible = false
            };
            cardPanel.Controls.Add(lblStatus);

            var card = new TaskCard
            {
                Task = task,
                Panel = cardPanel,
                PresetButton = btnPreset,
                SubtitleCombo = cmbSubtitle,
                AudioCombo = cmbAudio,
                ConvertButton = btnConvert,
                ProgressBar = progress,
                StatusLabel = lblStatus,
                OutputNameLabel = lblOutName,
                OutputNameEdit = txtOutName,
                SourceFormatLabel = lblInFormat,
                SourceResolutionLabel = lblInResolution,
                SourceSizeLabel = lblInSize,
                SourceDurationLabel = lblInDuration,
                TargetFormatLabel = lblOutFormat,
                TargetResolutionLabel = lblOutResolution,
                TargetSizeLabel = lblOutSize,
                TargetDurationLabel = lblOutDuration
            };

            // Wire convert button after card is fully built.
            btnConvert.Click += (s, e) => ConvertSingleTask(task, card);

            // Hover / click visual feedback on the whole card.
            WireCardHover(cardPanel);

            return card;
        }

        private void WireCardHover(RoundedPanel cardPanel)
        {
            cardPanel.MouseEnter += (s, e) =>
            {
                cardPanel.IsHovered = true;
                cardPanel.Invalidate();
            };
            cardPanel.MouseLeave += (s, e) =>
            {
                // Only turn off hover if the mouse really left the panel bounds.
                if (!cardPanel.ClientRectangle.Contains(cardPanel.PointToClient(Control.MousePosition)))
                {
                    cardPanel.IsHovered = false;
                    cardPanel.Invalidate();
                }
            };
            cardPanel.Click += (s, e) => SelectCard(cardPanel);

            foreach (Control c in cardPanel.Controls)
            {
                c.MouseEnter += (s, e) =>
                {
                    cardPanel.IsHovered = true;
                    cardPanel.Invalidate();
                };
                c.MouseLeave += (s, e) =>
                {
                    if (!cardPanel.ClientRectangle.Contains(cardPanel.PointToClient(Control.MousePosition)))
                    {
                        cardPanel.IsHovered = false;
                        cardPanel.Invalidate();
                    }
                };
                c.Click += (s, e) =>
                {
                    // Buttons/combos have their own click logic; do not change
                    // the selection state for them. Everything else selects the card.
                    if (c is Button || c is ComboBox || c is TextBox || c is ProgressBar)
                        return;
                    SelectCard(cardPanel);
                };
            }
        }

        /// <summary>
        /// Select a single card. Only one card may be "active" at a time. #42
        /// </summary>
        private void SelectCard(RoundedPanel panel)
        {
            if (_selectedCard == panel) return;
            if (_selectedCard != null && !_selectedCard.IsDisposed)
            {
                _selectedCard.IsActive = false;
                _selectedCard.Invalidate();
            }
            _selectedCard = panel;
            panel.IsActive = true;
            panel.Invalidate();
        }

        private Label AddInfoLabel(Panel parent, int x, int y, string text, int width)
        {
            var lbl = new Label
            {
                Location = new Point(x, y),
                Size = new Size(width, 18),
                Text = text,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private Button CreateIconButton(string symbol, int x, int y, string tooltip)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(30, 26),
                Text = symbol,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(90, 90, 90),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderSize = 0;
            toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        /// <summary>
        /// Themed action button: light fill, dark text, dark (theme) border. #48
        /// </summary>
        private Button CreateThemeButton(string text, int x, int y, string tooltip)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(30, 26),
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(237, 232, 248),
                ForeColor = Color.FromArgb(45, 45, 45),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(124, 77, 255);
            btn.FlatAppearance.BorderSize = 1;
            toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        private ComboBox CreateDropDown(int x, int y, int width)
        {
            return new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular)
            };
        }

        private string EstimateTargetSizeFromTask(ConversionTask task)
        {
            try
            {
                double seconds = task.GetEditedDurationSeconds();
                if (seconds <= 0) return "-";

                long videoBps = ParseBitRate(task.Preset.VideoBitrate);
                long audioBps = ParseBitRate(task.Preset.AudioBitrate);
                if (videoBps <= 0 && audioBps <= 0) return "-";
                long totalBytes = (long)((videoBps + audioBps) * seconds / 8);
                return FFmpegHelper.FormatFileSize(totalBytes);
            }
            catch { return "-"; }
        }

        private void ToggleOutputNameEdit(ConversionTask task, Label lbl, TextBox txt, Button btn)
        {
            if (txt.Visible)
            {
                // Save.
                string newName = txt.Text.Trim();
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    task.CustomOutputName = newName;
                    lbl.Text = task.GetOutputFileName();
                    toolTip.SetToolTip(lbl, task.OutputPath);
                }
                txt.Visible = false;
                lbl.Visible = true;
                btn.Text = "✎";
                toolTip.SetToolTip(btn, "修改文件名");
            }
            else
            {
                txt.Text = string.IsNullOrWhiteSpace(task.CustomOutputName)
                    ? Path.GetFileNameWithoutExtension(task.OutputPath)
                    : task.CustomOutputName;
                txt.Visible = true;
                lbl.Visible = false;
                txt.Focus();
                txt.SelectAll();
                btn.Text = "✓";
                toolTip.SetToolTip(btn, "保存文件名");
            }
        }

        private void OpenPlayer(ConversionTask task)
        {
            try
            {
                var ext = Path.GetExtension(task.InputPath).ToLowerInvariant();
                bool isImage = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }.Contains(ext);
                var dlg = new VideoPlayerForm(task.InputPath, isImage);
                dlg.Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开预览：\n" + ex.Message, "预览",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void OpenVideoEdit(ConversionTask task)
        {
            var info = await FFmpegHelper.ProbeDetailedAsync(task.InputPath);
            using (var dlg = new VideoEditForm())
            {
                dlg.InputPath = task.InputPath;
                dlg.SourceDurationSeconds = info.DurationSeconds > 0 ? info.DurationSeconds : task.SourceDurationSeconds;
                dlg.SourceWidth = info.Width;
                dlg.SourceHeight = info.Height;
                dlg.FrameRate = info.FrameRate;
                dlg.Segments = task.Segments;
                dlg.Crop = task.Crop;
                dlg.Rotation = task.Rotation;
                dlg.MergeSegments = task.MergeSegments;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    task.Segments = dlg.Segments;
                    task.Crop = dlg.Crop;
                    task.Rotation = dlg.Rotation;
                    task.MergeSegments = dlg.MergeSegments;
                    task.TrimStartSeconds = dlg.TrimStartSeconds;
                    task.TrimEndSeconds = dlg.TrimEndSeconds;
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    if (card != null)
                    {
                        card.TargetDurationLabel.Text = "输出时长: " + task.TargetDuration;
                        card.TargetSizeLabel.Text = "预计大小: " + EstimateTargetSizeFromTask(task);
                    }
                }
            }
        }

        private void OpenPresetSelection(ConversionTask task, Button presetButton, Label lblFormat, Label lblResolution, Label lblSize)
        {
            using (var dlg = new PresetSelectionForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPreset != null)
                {
                    task.Preset = dlg.SelectedPreset;
                    RefreshPresetButton(presetButton, task, lblFormat, lblResolution, lblSize);
                }
            }
        }

        private void OpenPresetEdit(ConversionTask task, Button presetButton, Label lblFormat, Label lblResolution, Label lblSize)
        {
            using (var dlg = new PresetEditForm())
            {
                dlg.Preset = task.Preset;
                dlg.UseHardwareEncoding = hardwareCheck.Checked;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    task.Preset = dlg.Preset;
                    RegisterCustomIfNeeded(dlg.Preset);
                    RefreshPresetButton(presetButton, task, lblFormat, lblResolution, lblSize);
                }
            }
        }

        /// <summary>若编辑结果是自定义预设，则登记到 PresetDataStore 以便在“自定义”页签显示。</summary>
        private void RegisterCustomIfNeeded(PresetOption preset)
        {
            if (preset != null && !preset.IsBuiltIn)
                PresetDataStore.AddCustom(preset);
        }

        private void RefreshPresetButton(Button presetButton, ConversionTask task, Label lblFormat, Label lblResolution, Label lblSize)
        {
            if (presetButton != null && !presetButton.IsDisposed)
                presetButton.Text = task.Preset.Name;
            lblFormat.Text = "格式: " + task.TargetFormat;
            lblResolution.Text = "分辨率: " + task.TargetResolution;
            lblSize.Text = "预计大小: " + EstimateTargetSizeFromTask(task);
            var card = _cards.FirstOrDefault(c => c.Task == task);
            if (card != null && card.TargetDurationLabel != null && !card.TargetDurationLabel.IsDisposed)
                card.TargetDurationLabel.Text = "输出时长: " + task.TargetDuration;
        }

        #endregion

        #region Drag & drop, options

        private void VideoConverter_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private async void VideoConverter_DragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            await AddFiles(files);
        }

        /// <summary>
        /// Checked = light-red background; unchecked = grey background.
        /// </summary>
        private void ApplyCheckStyle(CheckBox cb)
        {
            if (cb.Checked)
            {
                cb.BackColor = Color.FromArgb(255, 224, 224);  // 淡红
                cb.ForeColor = Color.FromArgb(200, 40, 40);
            }
            else
            {
                cb.BackColor = Color.FromArgb(225, 225, 225);  // 灰
                cb.ForeColor = Color.FromArgb(90, 90, 90);
            }
        }

        private void HighSpeedCheck_CheckedChanged(object sender, EventArgs e)
        {
            ApplyCheckStyle(highSpeedCheck);
            AppSettings.HighSpeed = highSpeedCheck.Checked;
            AppSettings.Save();
        }

        private void HardwareCheck_CheckedChanged(object sender, EventArgs e)
        {
            ApplyCheckStyle(hardwareCheck);
            AppSettings.Hardware = hardwareCheck.Checked;
            AppSettings.Save();
        }

        /// <summary>
        /// Background-detect which hardware encoders ffmpeg was built with and
        /// enable the "硬件编码" checkbox only when at least one is present.
        /// </summary>
        private async void DetectHardwareOnLoad()
        {
            HardwareSupportResult result;
            try
            {
                var sup = await FFmpegHelper.DetectHardwareEncodersAsync();
                result = new HardwareSupportResult { Support = sup, Ok = true };
            }
            catch
            {
                result = new HardwareSupportResult { Support = new FFmpegHelper.HardwareSupport(), Ok = false };
            }

            this.BeginInvoke((Action)(() =>
            {
                _hwSupport = result.Support;
                if (result.Ok && result.Support.Any)
                {
                    hardwareCheck.Enabled = true;
                    hardwareCheck.Text = "硬件编码 (" + result.Support.DisplayName + ")";
                }
                else
                {
                    hardwareCheck.Enabled = false;
                    hardwareCheck.Checked = false;
                    hardwareCheck.Text = result.Ok ? "硬件编码 (不支持)" : "硬件编码 (检测失败)";
                }
                // Apply persisted hardware-encode preference when supported. #47
                if (hardwareCheck.Enabled) hardwareCheck.Checked = _pendingHardware;
                ApplyCheckStyle(hardwareCheck);
            }));
        }

        private class HardwareSupportResult
        {
            public FFmpegHelper.HardwareSupport Support;
            public bool Ok;
        }

        /// <summary>
        /// High-speed mode applies only when the input and output containers are
        /// identical (stream copy is only valid then). Otherwise the mode is not
        /// used for that particular file and a normal encode is performed.
        /// </summary>
        private bool AppliesStreamCopy(ConversionTask task)
        {
            if (!highSpeedCheck.Checked) return false;
            string inExt = Path.GetExtension(task.InputPath).ToLowerInvariant();
            string outExt = (task.Preset?.Extension ?? ".mp4").ToLowerInvariant();
            return inExt == outExt && inExt.Length > 0;
        }

        /// <summary>
        /// Resolve the hardware encoder for a task's output codec, or null when
        /// the codec has no hardware equivalent (e.g. AVI/Xvid) — caller falls
        /// back to the software encoder.
        /// </summary>
        private string GetHardwareEncoderFor(ConversionTask task)
        {
            // 当“硬件编码”勾选且检测到支持时，解析为对应厂商的 GPU 编码器；
            // 否则（未勾选或不支持）回退为 CPU 编码器。#65
            var hw = (hardwareCheck.Checked && _hwSupport != null && _hwSupport.Any) ? _hwSupport : null;
            string resolved = FFmpegHelper.ResolveVideoEncoder(task.Preset?.VideoCodec, hw);
            if (string.Equals(resolved, "copy", StringComparison.OrdinalIgnoreCase)) return null;
            return resolved;
        }

        #endregion

        #region Tabs and global actions

        private void TabConvertingLabel_Click(object sender, EventArgs e)
        {
            _showCompleted = false;
            UpdateTabStyles();
            RefreshTaskList();
        }

        private void TabCompletedLabel_Click(object sender, EventArgs e)
        {
            _showCompleted = true;
            UpdateTabStyles();
            RefreshTaskList();
        }

        private void UpdateTabStyles()
        {
            if (_showCompleted)
            {
                tabConvertingLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
                tabConvertingLabel.ForeColor = Color.Gray;
                tabCompletedLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
                tabCompletedLabel.ForeColor = Color.Black;
            }
            else
            {
                tabConvertingLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
                tabConvertingLabel.ForeColor = Color.Black;
                tabCompletedLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
                tabCompletedLabel.ForeColor = Color.Gray;
            }
        }

        private void UpdateCount()
        {
            int count = _tasks.Count(t => t.Status != TaskStatus.Completed);
            convertingCountLabel.Text = string.Format("({0})", count);
        }

        private async void ConvertToButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new PresetSelectionForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPreset != null)
                {
                    _globalPreset = dlg.SelectedPreset;
                    PersistConvertToPreset();
                    UpdateConvertToDisplay();
                    await ApplyGlobalPresetToAll();
                }
            }
        }

        private async void ConvertToGearButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new PresetEditForm())
            {
                dlg.Preset = _globalPreset.Clone();
                dlg.UseHardwareEncoding = hardwareCheck.Checked;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _globalPreset = dlg.Preset;
                    RegisterCustomIfNeeded(dlg.Preset);
                    PersistConvertToPreset();
                    UpdateConvertToDisplay();
                    await ApplyGlobalPresetToAll();
                }
            }
        }

        private async Task ApplyGlobalPresetToAll()
        {
            if (_globalPreset == null) return;

            foreach (var task in _tasks)
            {
                // Try to keep the same preset "name" (e.g. 1080) within the new format;
                // fall back to same-as-source, then the first preset of the format.
                var newFormat = PresetDataStore.FindFormat(_globalPreset.FormatId);
                PresetOption newPreset = null;
                if (newFormat != null && newFormat.Presets.Count > 0)
                {
                    newPreset = newFormat.Presets.FirstOrDefault(p => p.Name == task.Preset.Name)
                                ?? newFormat.Presets.FirstOrDefault(p => p.KeepSource)
                                ?? newFormat.Presets[0];
                }
                if (newPreset == null) newPreset = _globalPreset;

                task.Preset = newPreset.Clone();
                var card = _cards.FirstOrDefault(c => c.Task == task);
                if (card != null)
                {
                    if (card.PresetButton != null && !card.PresetButton.IsDisposed)
                        card.PresetButton.Text = task.Preset.Name;
                    card.OutputNameLabel.Text = task.GetOutputFileName();
                    card.TargetFormatLabel.Text = "格式: " + task.TargetFormat;
                    card.TargetResolutionLabel.Text = "分辨率: " + task.TargetResolution;
                }

                // Re-probe source metadata via ffprobe and recompute the output
                // duration/size. #44 / #46
                await RefreshCardMetadata(task);
            }
        }

        /// <summary>
        /// Re-read a task's source resolution/duration with ffprobe and refresh
        /// the relevant card labels (source + output duration/size). #44 / #46
        /// </summary>
        private async Task RefreshCardMetadata(ConversionTask task)
        {
            var card = _cards.FirstOrDefault(c => c.Task == task);
            try
            {
                var info = await FFmpegHelper.ProbeDetailedAsync(task.InputPath);
                if (info.Width > 0 && info.Height > 0)
                    task.SourceResolution = string.Format("{0} x {1}", info.Width, info.Height);
                if (info.DurationSeconds > 0)
                {
                    task.SourceDurationSeconds = info.DurationSeconds;
                    task.SourceDuration = FFmpegHelper.FormatDuration(info.DurationSeconds);
                }
                if (card != null)
                {
                    if (card.SourceResolutionLabel != null && !card.SourceResolutionLabel.IsDisposed)
                        card.SourceResolutionLabel.Text = "分辨率: " + task.SourceResolution;
                    if (card.SourceDurationLabel != null && !card.SourceDurationLabel.IsDisposed)
                        card.SourceDurationLabel.Text = "时长: " + task.SourceDuration;
                }
            }
            catch { /* keep existing metadata on probe failure */ }

            if (card != null)
            {
                if (card.TargetDurationLabel != null && !card.TargetDurationLabel.IsDisposed)
                    card.TargetDurationLabel.Text = "输出时长: " + task.TargetDuration;
                if (card.TargetSizeLabel != null && !card.TargetSizeLabel.IsDisposed)
                    card.TargetSizeLabel.Text = "预计大小: " + EstimateTargetSizeFromTask(task);
            }
        }

        private async void ConvertAllButton_Click(object sender, EventArgs e)
        {
            if (_tasks.Count == 0) return;

            convertAllButton.Enabled = false;
            addFilesButton.Enabled = false;
            _batchConverting = true;

            try
            {
                var pending = _tasks.Where(t => t.Status != TaskStatus.Completed).ToList();
                foreach (var task in pending)
                {
                    if (!_batchConverting) break; // global stop if cancelled
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    await RunTaskConversion(task, card);
                }
            }
            finally
            {
                _batchConverting = false;
                convertAllButton.Enabled = true;
                addFilesButton.Enabled = true;
                UpdateCount();
            }
        }

        private async void ConvertSingleTask(ConversionTask task, TaskCard card)
        {
            if (card == null) return;

            if (card.IsConverting)
            {
                // Cancel.
                try { task.Cancellation?.Cancel(); } catch { }
                return;
            }

            await RunTaskConversion(task, card);
        }

        private async Task RunTaskConversion(ConversionTask task, TaskCard card)
        {
            if (card == null) return;
            if (task.Status == TaskStatus.Converting) return;

            // 转换前检测输入是否为 VC-1（WMV），是则注入容错参数。#74
            try { task.IsVC1Input = await FFmpegHelper.DetectVC1InputAsync(task.InputPath); }
            catch { task.IsVC1Input = false; }

            // Decide per-task conversion mode.
            task.UseStreamCopy = AppliesStreamCopy(task);
            task.HardwareEncoder = GetHardwareEncoderFor(task);
            task.Cancellation = new CancellationTokenSource();
            var token = task.Cancellation.Token;

            card.IsConverting = true;
            card.ConvertButton.Text = "取消";
            card.ProgressBar.Visible = true;
            card.StatusLabel.Visible = true;

            string statusText;
            if (task.UseStreamCopy)
                statusText = "高速转换中 (流复制)...";
            else if (hardwareCheck.Checked && !string.IsNullOrEmpty(task.HardwareEncoder))
                statusText = "硬件编码中 (" + task.HardwareEncoder + ")...";
            else if (hardwareCheck.Checked)
                statusText = "硬件编码不支持，使用软件...";
            else
                statusText = "转换中...";
            card.StatusLabel.Text = statusText;

            task.Status = TaskStatus.Converting;
            var progress = new Progress<double>(p =>
            {
                if (card != null && !card.Panel.IsDisposed)
                {
                    int v = (int)(p * 100);
                    if (v < 0) v = 0;
                    if (v > 100) v = 100;
                    card.ProgressBar.Value = v;
                }
            });

            try
            {
                EnsureOutputDirectory(task);
                await FFmpegHelper.RunAsync(task, progress, token);
                task.Status = TaskStatus.Completed;
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.StatusLabel.Text = "已完成";
                    card.ProgressBar.Value = 100;
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = TaskStatus.Pending;
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.StatusLabel.Text = "已取消";
                    card.StatusLabel.ForeColor = Color.Gray;
                }
            }
            catch (Exception ex)
            {
                task.Status = TaskStatus.Failed;
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.StatusLabel.Text = "失败: " + ex.Message;
                    card.StatusLabel.ForeColor = Color.Red;
                }
            }
            finally
            {
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.IsConverting = false;
                    card.ConvertButton.Text = "转换";
                    if (task.Status != TaskStatus.Converting)
                    {
                        // Hide progress shortly after completion / cancellation.
                        _ = Task.Delay(1500).ContinueWith(_ =>
                        {
                            if (card.Panel.IsHandleCreated)
                                card.Panel.Invoke((Action)(() =>
                                {
                                    if (!card.IsConverting)
                                    {
                                        card.ProgressBar.Visible = false;
                                        card.StatusLabel.Visible = false;
                                    }
                                }));
                        });
                    }
                }
                task.Cancellation?.Dispose();
                task.Cancellation = null;
            }
        }

        private void EnsureOutputDirectory(ConversionTask task)
        {
            foreach (var path in task.GetOutputPaths())
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
            }
        }

        #endregion

        #region Nested types

        private class TaskCard
        {
            public ConversionTask Task { get; set; }
            public RoundedPanel Panel { get; set; }
            public Button PresetButton { get; set; }
            public ComboBox SubtitleCombo { get; set; }
            public ComboBox AudioCombo { get; set; }
            public Button ConvertButton { get; set; }
            public ProgressBar ProgressBar { get; set; }
            public Label StatusLabel { get; set; }
            public Label OutputNameLabel { get; set; }
            public TextBox OutputNameEdit { get; set; }
            public Label SourceFormatLabel { get; set; }
            public Label SourceResolutionLabel { get; set; }
            public Label SourceSizeLabel { get; set; }
            public Label SourceDurationLabel { get; set; }
            public Label TargetFormatLabel { get; set; }
            public Label TargetResolutionLabel { get; set; }
            public Label TargetSizeLabel { get; set; }
            public Label TargetDurationLabel { get; set; }
            public bool IsConverting { get; set; }
        }

        #endregion
    }
}
