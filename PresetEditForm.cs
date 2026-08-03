// ============================================================================
//  PresetEditForm.cs — edit the parameters of a PresetOption.
//  Dropdown values are assembled at runtime from the three-layer spec:
//    * 公共下拉项（分辨率/帧率/码率/采样率/声道） → options_spec/common_options.json
//    * 特定类型下拉项（该封装格式支持的编码器） → options_spec/format_options.json
//    * 默认选中值                                → 预设自身 (options_spec/presets.json)
//  Built-in presets cannot be overwritten; saving a modified built-in preset
//  automatically creates a custom preset with the suffix "（自定义）".
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoConverter
{
    public partial class PresetEditForm : Form
    {
        public PresetOption Preset { get; set; }

        /// <summary>是否勾选硬件编码（由主窗体传入）。预览按此决定 GPU/CPU 编码器。</summary>
        public bool UseHardwareEncoding { get; set; }

        private TextBox txtTitle;
        private ComboBox cmbVideoCodec;
        private ComboBox cmbResolution;
        private ComboBox cmbFrameRate;
        private ComboBox cmbBitrateMode;   // 自动/固定码率/可变码率/质量控制
        private Label lblRate;
        private Label lblQuality;
        private Label lblQualityRange;
        private NumericUpDown numQuality;
        private Label lblMaxRate;
        private ComboBox cmbMaxRate;
        private ComboBox cmbVideoBitrate;
        private ComboBox cmbAudioCodec;
        private ComboBox cmbChannel;
        private ComboBox cmbSampleRate;
        private ComboBox cmbAudioBitrate;
        private TextBox txtCustomArgs;
        private TextBox txtPreview;
        private CheckBox chkSaveAsNew;
        private Button btnSave;
        private Button btnCancel;
        private Label lblAudioSection;

        private FormatOptions _options;
        private FFmpegHelper.HardwareSupport _hw;
        private bool _updatingBitrateUI;

        public PresetEditForm()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "预设编辑";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.AutoScaleMode = AutoScaleMode.None;   // 代码构建窗体需关闭字体自动缩放，否则左边缘控件被缩放/裁剪
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
        }

        private void InitializeComponent()
        {
            int y = 16;
            int labelW = 70;
            int dropW = 160;
            int dropH = 24;
            int padY = 34;
            int labelX = 16;     // 标签列固定左边缘
            int ctrlX = 92;      // 控件列固定左边缘（与标签列留出间距，避免重叠）
            int col2LabelX = 272;
            int col2CtrlX = 346;

            // Title
            var lblTitle = new Label { Text = "标题", Location = new Point(labelX, y), Size = new Size(labelW, 20), AutoSize = false };
            txtTitle = new TextBox
            {
                Location = new Point(ctrlX, y - 2),
                Size = new Size(440, 24),
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            txtTitle.TextChanged += (s, e) => UpdateTitle();
            y += 44;

            // Video section header
            var lblVideo = new Label
            {
                Text = "视频",
                Location = new Point(labelX, y),
                Size = new Size(80, 22),
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            y += 32;

            // Row 1: Encoder + Resolution
            var lblEncoder = new Label { Text = "编码器", Location = new Point(labelX, y + 2), Size = new Size(labelW, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbVideoCodec = CreateDropDown(ctrlX, y, dropW, dropH);
            var lblRes = new Label { Text = "分辨率", Location = new Point(col2LabelX, y + 2), Size = new Size(70, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbResolution = CreateDropDown(col2CtrlX, y, dropW, dropH);
            y += padY;

            // Row 2: Frame Rate + Bitrate Mode
            var lblFps = new Label { Text = "帧率", Location = new Point(labelX, y + 2), Size = new Size(labelW, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbFrameRate = CreateDropDown(ctrlX, y, dropW, dropH);
            var lblBm = new Label { Text = "码率模式", Location = new Point(col2LabelX, y + 2), Size = new Size(70, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbBitrateMode = CreateDropDown(col2CtrlX, y, dropW, dropH);
            y += padY;

            // Row 3: Bitrate (CBR/VBR) or quality control — visibility switched by mode.
            lblRate = new Label { Text = "码率", Location = new Point(labelX, y + 2), Size = new Size(labelW, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbVideoBitrate = CreateDropDown(ctrlX, y, dropW, dropH);
            lblQuality = new Label { Text = "质量控制值", Location = new Point(labelX, y + 2), Size = new Size(labelW, 20), AutoSize = false, ForeColor = Color.Gray, Visible = false };
            numQuality = new NumericUpDown
            {
                Location = new Point(ctrlX, y),
                Size = new Size(70, 24),
                Font = new Font("Microsoft YaHei UI", 9F),
                Visible = false
            };
            lblQualityRange = new Label
            {
                Location = new Point(ctrlX + 76, y + 3),
                Size = new Size(172, 20),
                AutoSize = false,
                ForeColor = Color.FromArgb(90, 60, 160),
                Visible = false
            };
            lblMaxRate = new Label { Text = "最大码率", Location = new Point(col2LabelX, y + 2), Size = new Size(70, 20), AutoSize = false, ForeColor = Color.Gray, Visible = false };
            cmbMaxRate = CreateDropDown(col2CtrlX, y, dropW, dropH);
            cmbMaxRate.Visible = false;
            y += padY + 10;

            // Audio section header
            lblAudioSection = new Label
            {
                Text = "音频",
                Location = new Point(labelX, y),
                Size = new Size(80, 22),
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            y += 32;

            // Row 1: Audio Encoder + Channel
            var lblAEncoder = new Label { Text = "编码器", Location = new Point(labelX, y + 2), Size = new Size(labelW, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbAudioCodec = CreateDropDown(ctrlX, y, dropW, dropH);
            var lblChannel = new Label { Text = "声道", Location = new Point(col2LabelX, y + 2), Size = new Size(70, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbChannel = CreateDropDown(col2CtrlX, y, dropW, dropH);
            y += padY;

            // Row 2: Sample Rate + Audio Bitrate
            var lblSr = new Label { Text = "采样率", Location = new Point(labelX, y + 2), Size = new Size(labelW, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbSampleRate = CreateDropDown(ctrlX, y, dropW, dropH);
            var lblAbr = new Label { Text = "码率", Location = new Point(col2LabelX, y + 2), Size = new Size(70, 20), AutoSize = false, ForeColor = Color.Gray };
            cmbAudioBitrate = CreateDropDown(col2CtrlX, y, dropW, dropH);
            y += padY + 16;

            // Custom parameters (advanced ffmpeg args)
            var lblCustom = new Label
            {
                Text = "自定义参数",
                Location = new Point(labelX, y + 2),
                Size = new Size(70, 20),
                AutoSize = false,
                ForeColor = Color.Gray
            };
            txtCustomArgs = new TextBox
            {
                Location = new Point(ctrlX, y),
                Size = new Size(440, 24),
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            y += 40;

            // ffmpeg 参数解析预览（只读，可复制，随参数变化实时更新）
            var lblPreview = new Label
            {
                Text = "参数预览 (ffmpeg)",
                Location = new Point(labelX, y + 2),
                Size = new Size(160, 20),
                AutoSize = false,
                ForeColor = Color.Gray
            };
            y += 26;
            txtPreview = new TextBox
            {
                Location = new Point(labelX, y),
                Size = new Size(528, 76),
                Multiline = true,
                ReadOnly = true,
                BackColor = Color.FromArgb(248, 246, 252),
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F),
                WordWrap = true
            };
            y += 76 + 14;

            // Save as new preset checkbox
            chkSaveAsNew = new CheckBox
            {
                Text = "另存为新预设",
                Location = new Point(labelX, y),
                Size = new Size(160, 22),
                ForeColor = Color.Gray,
                BackColor = Color.White
            };
            y += 42;

            // Buttons
            btnSave = new Button
            {
                Text = "保存",
                Location = new Point(360, y),
                Size = new Size(80, 32),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.DialogResult = DialogResult.OK;
            btnSave.Click += BtnSave_Click;

            btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(450, y),
                Size = new Size(80, 32),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnCancel.DialogResult = DialogResult.Cancel;

            this.ClientSize = new Size(560, y + 80);

            this.Controls.Add(lblTitle);
            this.Controls.Add(txtTitle);
            this.Controls.Add(lblVideo);
            this.Controls.Add(lblEncoder);
            this.Controls.Add(cmbVideoCodec);
            this.Controls.Add(lblRes);
            this.Controls.Add(cmbResolution);
            this.Controls.Add(lblFps);
            this.Controls.Add(cmbFrameRate);
            this.Controls.Add(lblBm);
            this.Controls.Add(cmbBitrateMode);
            this.Controls.Add(lblRate);
            this.Controls.Add(cmbVideoBitrate);
            this.Controls.Add(lblQuality);
            this.Controls.Add(numQuality);
            this.Controls.Add(lblQualityRange);
            this.Controls.Add(lblMaxRate);
            this.Controls.Add(cmbMaxRate);
            this.Controls.Add(lblAudioSection);
            this.Controls.Add(lblAEncoder);
            this.Controls.Add(cmbAudioCodec);
            this.Controls.Add(lblChannel);
            this.Controls.Add(cmbChannel);
            this.Controls.Add(lblSr);
            this.Controls.Add(cmbSampleRate);
            this.Controls.Add(lblAbr);
            this.Controls.Add(cmbAudioBitrate);
            this.Controls.Add(lblCustom);
            this.Controls.Add(txtCustomArgs);
            this.Controls.Add(lblPreview);
            this.Controls.Add(txtPreview);
            this.Controls.Add(chkSaveAsNew);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnSave;
            this.CancelButton = btnCancel;
        }

        /// <summary>
        /// 把当前界面参数汇总成一个临时 PresetOption（不改动原始 Preset 成员），
        /// 用于实时预览完整的 ffmpeg 参数。
        /// </summary>
        private PresetOption GatherPreviewPreset()
        {
            var snap = Preset != null ? Preset.Clone() : new PresetOption();
            snap.VideoCodec = GetComboValue(cmbVideoCodec, "copy");
            var vitem = cmbVideoCodec.SelectedItem as OptionItem;
            snap.VideoCodecLabel = (cmbVideoCodec.SelectedIndex > 0 && vitem != null) ? vitem.Label : null;
            snap.ResolutionValue = GetComboValue(cmbResolution, null);
            snap.ResolutionLabel = string.IsNullOrEmpty(snap.ResolutionValue)
                ? "与源文件相同"
                : snap.ResolutionValue.Replace("x", " x ");
            snap.FrameRate = GetComboValue(cmbFrameRate, null);
            snap.BitrateMode = SelectedBitrateModeString();
            snap.QualityValue = (int)numQuality.Value;
            snap.QualityMaxRate = GetComboValue(cmbMaxRate, null);
            snap.VideoBitrate = GetComboValue(cmbVideoBitrate, null);
            snap.AudioCodec = GetComboValue(cmbAudioCodec, "copy");
            if (int.TryParse(GetComboValue(cmbChannel, null), out int ch))
                snap.Channels = ch;
            else
                snap.Channels = 0;
            snap.SampleRate = GetComboValue(cmbSampleRate, null);
            snap.AudioBitrate = GetComboValue(cmbAudioBitrate, null);
            snap.CustomArgs = txtCustomArgs.Text.Trim();
            if (!string.IsNullOrWhiteSpace(txtTitle.Text.Trim()))
                snap.Name = txtTitle.Text.Trim();
            return snap;
        }

        /// <summary>
        /// 刷新只读的 ffmpeg 参数预览框，反映当前界面的所有参数选择。
        /// </summary>
        private void UpdatePreview()
        {
            if (txtPreview == null) return;
            try
            {
                var snap = GatherPreviewPreset();
                txtPreview.Text = FFmpegHelper.BuildPresetPreviewArguments(snap, _hw, UseHardwareEncoding);
            }
            catch
            {
                txtPreview.Text = "";
            }
        }

        /// <summary>当前选中的码率模式值（auto/cbr/vbr/quality）。</summary>
        private string SelectedBitrateModeString()
        {
            var it = cmbBitrateMode.SelectedItem as OptionItem;
            return (it != null && !string.IsNullOrEmpty(it.Value)) ? it.Value : "auto";
        }

        /// <summary>
        /// 按当前编码器（CPU/GPU 解析后）重建「码率模式」选项并联动显示：
        ///   固定码率/可变码率 → 显示码率下拉；质量控制 → 显示数值范围控件 + 最大码率。
        /// 编码器不支持的模式不会出现在下拉中（如 copy 不支持任何目标码率，仅"自动"）。
        /// </summary>
        private void UpdateBitrateUI()
        {
            if (_updatingBitrateUI) return;
            _updatingBitrateUI = true;
            try
            {
                string fourCC = GetComboValue(cmbVideoCodec, "copy");
                string encoder = FFmpegHelper.ResolveVideoEncoder(fourCC, UseHardwareEncoding ? _hw : null);
                var spec = FFmpegHelper.GetQualitySpec(encoder);
                bool canTarget = FFmpegHelper.SupportsTargetBitrate(encoder);

                // ComboBox 无法逐项禁用，只能按编码器支持重建选项列表。
                string cur = SelectedBitrateModeString();
                var modes = new List<OptionItem> { new OptionItem("auto", "自动") };
                if (canTarget)
                {
                    modes.Add(new OptionItem("cbr", "固定码率"));
                    modes.Add(new OptionItem("vbr", "可变码率"));
                }
                if (spec != null)
                    modes.Add(new OptionItem("quality", "质量控制"));

                cmbBitrateMode.DataSource = null;
                cmbBitrateMode.DisplayMember = "Label";
                cmbBitrateMode.ValueMember = "Value";
                cmbBitrateMode.DataSource = modes;
                int idx = modes.FindIndex(m => string.Equals(m.Value, cur, StringComparison.OrdinalIgnoreCase));
                cmbBitrateMode.SelectedIndex = idx >= 0 ? idx : 0;

                string mode = SelectedBitrateModeString();
                bool isQuality = string.Equals(mode, "quality", StringComparison.OrdinalIgnoreCase);
                bool isTarget = string.Equals(mode, "cbr", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(mode, "vbr", StringComparison.OrdinalIgnoreCase);

                lblRate.Visible = isTarget;
                cmbVideoBitrate.Visible = isTarget;
                lblQuality.Visible = isQuality;
                numQuality.Visible = isQuality;
                lblQualityRange.Visible = isQuality;
                lblMaxRate.Visible = isQuality;
                cmbMaxRate.Visible = isQuality;

                if (isQuality && spec != null)
                {
                    numQuality.Minimum = spec.Min;
                    numQuality.Maximum = spec.Max;
                    // 推荐值优先取可编辑配置（default_codec_settings.json），未配置回退硬编码。
                    var def = DefaultCodecSettings.GetVideoDefault(encoder);
                    int want = (Preset != null && Preset.QualityValue > 0)
                        ? Preset.QualityValue
                        : (def != null ? def.Recommended : spec.Recommended);
                    // 默认 0 或越界 → 落到推荐值；用户手动调整过的有效值保留。
                    if (numQuality.Value == 0 || numQuality.Value < spec.Min || numQuality.Value > spec.Max)
                        numQuality.Value = want;
                    lblQualityRange.Text = string.Format("{0}（参数 {1}）", spec.ToString(), spec.Param);
                }
            }
            catch
            {
                // 界面初始化的极早期调用：控件尚未就绪时忽略。
            }
            finally
            {
                _updatingBitrateUI = false;
            }
        }

        /// <summary>标题显示「类型 名称」，例如 “MP4 Video 1080P 超清”。</summary>
        private void UpdateTitle()
        {
            string fmt = Preset?.FormatName;
            string name = txtTitle != null ? txtTitle.Text.Trim() : "";
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(fmt)) parts.Add(fmt);
            if (!string.IsNullOrEmpty(name)) parts.Add(name);
            this.Text = parts.Count > 0 ? string.Join(" ", parts) : "预设编辑";
        }

        private ComboBox CreateDropDown(int x, int y, int w, int h)
        {
            return new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                if (Preset == null) Preset = new PresetOption { Name = "自定义" };

                // 下拉项 = 公共选项（common_options.json）+ 该格式的特定编码器
                // （format_options.json）。预设本身只提供默认选中值。
                _options = PresetDataStore.GetFormatOptions(Preset.FormatId) ?? new FormatOptions();
                MergeFallback(_options, Preset);

                LoadCombo(cmbVideoCodec, _options.VideoCodecs, Preset.VideoCodec, "自动");
                LoadCombo(cmbResolution, _options.Resolutions, Preset.ResolutionValue, "与源文件相同");
                LoadCombo(cmbFrameRate, _options.FrameRates, Preset.FrameRate, "自动");
                LoadCombo(cmbVideoBitrate, _options.VideoBitrates, Preset.VideoBitrate, "自动");
                LoadCombo(cmbAudioCodec, _options.AudioCodecs, Preset.AudioCodec, "自动");
                LoadCombo(cmbChannel, _options.Channels, Preset.Channels > 0 ? Preset.Channels.ToString() : null, "自动");
                LoadCombo(cmbSampleRate, _options.SampleRates, Preset.SampleRate, "自动");
                LoadCombo(cmbAudioBitrate, _options.AudioBitrates, Preset.AudioBitrate, "自动");
                LoadCombo(cmbMaxRate, _options.VideoBitrates, Preset.QualityMaxRate, "自动");

                txtTitle.Text = Preset.Name ?? "";
                txtCustomArgs.Text = Preset.CustomArgs ?? "";
                UpdateTitle();

                // 检测机器可用的硬件编码器，用于预览完整的 ffmpeg 视频编码器
                // （如 h264_nvenc）；检测失败则回退到 CPU 编码器。
                try { _hw = FFmpegHelper.DetectHardwareEncodersAsync().GetAwaiter().GetResult(); }
                catch { _hw = new FFmpegHelper.HardwareSupport(); }

                // 初始码率模式下拉（UpdateBitrateUI 会按编码器支持重建并保留当前选中）。
                var initModes = new List<OptionItem>
                {
                    new OptionItem("auto", "自动"),
                    new OptionItem("cbr", "固定码率"),
                    new OptionItem("vbr", "可变码率"),
                    new OptionItem("quality", "质量控制")
                };
                cmbBitrateMode.DataSource = null;
                cmbBitrateMode.DisplayMember = "Label";
                cmbBitrateMode.ValueMember = "Value";
                cmbBitrateMode.DataSource = initModes;
                string pm = string.IsNullOrEmpty(Preset.BitrateMode) ? "auto" : Preset.BitrateMode;
                int pi = initModes.FindIndex(m => string.Equals(m.Value, pm, StringComparison.OrdinalIgnoreCase));
                cmbBitrateMode.SelectedIndex = pi >= 0 ? pi : 0;

                // 码率模式：按编码器支持动态重建选项，并联动质量控制/码率显示。
                cmbBitrateMode.SelectedIndexChanged += (s, e) => { UpdateBitrateUI(); UpdatePreview(); };
                cmbVideoCodec.SelectedIndexChanged += (s, e) => { UpdateBitrateUI(); UpdatePreview(); };
                numQuality.ValueChanged += (s, e) => UpdatePreview();
                cmbMaxRate.SelectedIndexChanged += (s, e) => UpdatePreview();

                // 任一参数变化 → 实时刷新 ffmpeg 参数预览
                cmbResolution.SelectedIndexChanged += (s, e) => UpdatePreview();
                cmbFrameRate.SelectedIndexChanged += (s, e) => UpdatePreview();
                cmbVideoBitrate.SelectedIndexChanged += (s, e) => UpdatePreview();
                cmbAudioCodec.SelectedIndexChanged += (s, e) => UpdatePreview();
                cmbChannel.SelectedIndexChanged += (s, e) => UpdatePreview();
                cmbSampleRate.SelectedIndexChanged += (s, e) => UpdatePreview();
                cmbAudioBitrate.SelectedIndexChanged += (s, e) => UpdatePreview();
                txtCustomArgs.TextChanged += (s, e) => UpdatePreview();
                UpdateBitrateUI();
                UpdatePreview();

                // Built-in presets cannot be overwritten; force "另存为" semantics.
                if (Preset.IsBuiltIn)
                {
                    chkSaveAsNew.Checked = true;
                    chkSaveAsNew.Enabled = false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载预设编辑界面时出错：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Bind an OptionItem list: the list shows friendly labels, the selected
        /// value stays the raw ffmpeg value.
        /// </summary>
        private void LoadCombo(ComboBox cb, List<OptionItem> items, string current, string autoText)
        {
            var data = new List<OptionItem> { new OptionItem(null, autoText) };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (items != null)
            {
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Value)) continue;
                    if (!seen.Add(item.Value)) continue;
                    data.Add(item);
                }
            }

            cb.DataSource = null;
            cb.DisplayMember = "Label";
            cb.ValueMember = "Value";
            cb.DataSource = data;

            int idx = 0;
            if (!string.IsNullOrWhiteSpace(current))
            {
                int found = data.FindIndex(o => string.Equals(o.Value, current, StringComparison.OrdinalIgnoreCase));
                if (found > 0) idx = found;
            }
            cb.SelectedIndex = idx;
        }

        /// <summary>
        /// Guarantee the preset's own default is always selectable, even if the
        /// shared pool happens not to contain it.
        /// </summary>
        private void MergeFallback(FormatOptions o, PresetOption p)
        {
            EnsureValue(o.VideoCodecs, p.VideoCodec, p.VideoCodecLabel ?? p.VideoCodec);
            EnsureValue(o.AudioCodecs, p.AudioCodec, p.AudioCodec);
            EnsureValue(o.Resolutions, p.ResolutionValue, p.ResolutionLabel);
            EnsureValue(o.FrameRates, p.FrameRate, p.FrameRate + " fps");
            EnsureValue(o.VideoBitrates, p.VideoBitrate, TrimK(p.VideoBitrate) + " kbps");
            EnsureValue(o.AudioBitrates, p.AudioBitrate, TrimK(p.AudioBitrate) + " kbps");
            EnsureValue(o.SampleRates, p.SampleRate, p.SampleRate + " Hz");
            if (p.Channels > 0)
                EnsureValue(o.Channels, p.Channels.ToString(), p.Channels + " 声道");
        }

        private static void EnsureValue(List<OptionItem> list, string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            if (list.Any(x => x != null && string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
                return;
            list.Insert(0, new OptionItem(value, string.IsNullOrWhiteSpace(label) ? value : label));
        }

        private static string TrimK(string bitrate)
        {
            if (string.IsNullOrEmpty(bitrate)) return bitrate;
            return bitrate.EndsWith("k", StringComparison.OrdinalIgnoreCase)
                ? bitrate.Substring(0, bitrate.Length - 1)
                : bitrate;
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            try
            {
                if (Preset == null) Preset = new PresetOption();

                string title = txtTitle.Text.Trim();
                bool saveAsNew = chkSaveAsNew.Checked || Preset.IsBuiltIn;

                if (saveAsNew)
                {
                    // Clone so the original built-in preset is untouched.
                    Preset = Preset.Clone();
                    Preset.PresetId = null;
                    Preset.IsBuiltIn = false;
                    Preset.Name = string.IsNullOrWhiteSpace(title)
                        ? AppendCustomSuffix(Preset.Name)
                        : AppendCustomSuffix(title);
                }
                else if (!string.IsNullOrWhiteSpace(title))
                {
                    Preset.Name = title;
                }

                Preset.VideoCodec = GetComboValue(cmbVideoCodec, "copy");
                var vitem = cmbVideoCodec.SelectedItem as OptionItem;
                Preset.VideoCodecLabel = (cmbVideoCodec.SelectedIndex > 0 && vitem != null) ? vitem.Label : null;
                Preset.ResolutionValue = GetComboValue(cmbResolution, null);
                Preset.ResolutionLabel = string.IsNullOrEmpty(Preset.ResolutionValue)
                    ? "与源文件相同"
                    : Preset.ResolutionValue.Replace("x", " x ");
                Preset.FrameRate = GetComboValue(cmbFrameRate, null);
                Preset.BitrateMode = SelectedBitrateModeString();
                Preset.QualityValue = (int)numQuality.Value;
                Preset.QualityMaxRate = GetComboValue(cmbMaxRate, null);
                Preset.VideoBitrate = GetComboValue(cmbVideoBitrate, null);
                Preset.AudioCodec = GetComboValue(cmbAudioCodec, "copy");
                if (int.TryParse(GetComboValue(cmbChannel, null), out int ch))
                    Preset.Channels = ch;
                else
                    Preset.Channels = 0;
                Preset.SampleRate = GetComboValue(cmbSampleRate, null);
                Preset.AudioBitrate = GetComboValue(cmbAudioBitrate, null);
                Preset.CustomArgs = txtCustomArgs.Text.Trim();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存预设时出错：" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                this.DialogResult = DialogResult.None;
            }
        }

        private string AppendCustomSuffix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "自定义";
            const string suffix = "（自定义）";
            if (name.EndsWith(suffix)) return name;
            return name + suffix;
        }

        private string GetComboValue(ComboBox cb, string defaultValue)
        {
            if (cb.SelectedIndex <= 0) return defaultValue;
            var item = cb.SelectedItem as OptionItem;
            if (item == null || string.IsNullOrWhiteSpace(item.Value))
                return defaultValue;
            return item.Value;
        }
    }
}
