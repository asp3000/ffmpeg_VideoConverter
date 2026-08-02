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

        private TextBox txtTitle;
        private ComboBox cmbVideoCodec;
        private ComboBox cmbResolution;
        private ComboBox cmbFrameRate;
        private ComboBox cmbVideoBitrate;
        private ComboBox cmbAudioCodec;
        private ComboBox cmbChannel;
        private ComboBox cmbSampleRate;
        private ComboBox cmbAudioBitrate;
        private TextBox txtCustomArgs;
        private CheckBox chkSaveAsNew;
        private Button btnSave;
        private Button btnCancel;
        private Label lblAudioSection;

        private FormatOptions _options;

        public PresetEditForm()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "预设编辑";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
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

            // Title
            var lblTitle = new Label { Text = "标题", Location = new Point(16, y), Size = new Size(labelW, 20) };
            txtTitle = new TextBox
            {
                Location = new Point(80, y - 2),
                Size = new Size(420, 24),
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            y += 44;

            // Video section header
            var lblVideo = new Label
            {
                Text = "视频",
                Location = new Point(16, y),
                Size = new Size(80, 22),
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            y += 32;

            // Row 1: Encoder + Resolution
            var lblEncoder = new Label { Text = "编码器", Location = new Point(16, y + 2), Size = new Size(labelW, 20), ForeColor = Color.Gray };
            cmbVideoCodec = CreateDropDown(80, y, dropW, dropH);
            var lblRes = new Label { Text = "分辨率", Location = new Point(260, y + 2), Size = new Size(70, 20), ForeColor = Color.Gray };
            cmbResolution = CreateDropDown(340, y, dropW, dropH);
            y += padY;

            // Row 2: Frame Rate + Bitrate
            var lblFps = new Label { Text = "帧率", Location = new Point(16, y + 2), Size = new Size(labelW, 20), ForeColor = Color.Gray };
            cmbFrameRate = CreateDropDown(80, y, dropW, dropH);
            var lblVbr = new Label { Text = "码率", Location = new Point(260, y + 2), Size = new Size(70, 20), ForeColor = Color.Gray };
            cmbVideoBitrate = CreateDropDown(340, y, dropW, dropH);
            y += padY + 10;

            // Audio section header
            lblAudioSection = new Label
            {
                Text = "音频",
                Location = new Point(16, y),
                Size = new Size(80, 22),
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45)
            };
            y += 32;

            // Row 1: Audio Encoder + Channel
            var lblAEncoder = new Label { Text = "编码器", Location = new Point(16, y + 2), Size = new Size(labelW, 20), ForeColor = Color.Gray };
            cmbAudioCodec = CreateDropDown(80, y, dropW, dropH);
            var lblChannel = new Label { Text = "声道", Location = new Point(260, y + 2), Size = new Size(70, 20), ForeColor = Color.Gray };
            cmbChannel = CreateDropDown(340, y, dropW, dropH);
            y += padY;

            // Row 2: Sample Rate + Audio Bitrate
            var lblSr = new Label { Text = "采样率", Location = new Point(16, y + 2), Size = new Size(labelW, 20), ForeColor = Color.Gray };
            cmbSampleRate = CreateDropDown(80, y, dropW, dropH);
            var lblAbr = new Label { Text = "码率", Location = new Point(260, y + 2), Size = new Size(70, 20), ForeColor = Color.Gray };
            cmbAudioBitrate = CreateDropDown(340, y, dropW, dropH);
            y += padY + 16;

            // Custom parameters (advanced ffmpeg args)
            var lblCustom = new Label
            {
                Text = "自定义参数",
                Location = new Point(16, y + 2),
                Size = new Size(70, 20),
                ForeColor = Color.Gray
            };
            txtCustomArgs = new TextBox
            {
                Location = new Point(90, y),
                Size = new Size(410, 24),
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            y += 40;

            // Save as new preset checkbox
            chkSaveAsNew = new CheckBox
            {
                Text = "另存为新预设",
                Location = new Point(16, y),
                Size = new Size(160, 22),
                ForeColor = Color.Gray,
                BackColor = Color.White
            };
            y += 42;

            // Buttons
            btnSave = new Button
            {
                Text = "保存",
                Location = new Point(340, y),
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
                Location = new Point(430, y),
                Size = new Size(80, 32),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnCancel.DialogResult = DialogResult.Cancel;

            this.ClientSize = new Size(530, y + 80);

            this.Controls.Add(lblTitle);
            this.Controls.Add(txtTitle);
            this.Controls.Add(lblVideo);
            this.Controls.Add(lblEncoder);
            this.Controls.Add(cmbVideoCodec);
            this.Controls.Add(lblRes);
            this.Controls.Add(cmbResolution);
            this.Controls.Add(lblFps);
            this.Controls.Add(cmbFrameRate);
            this.Controls.Add(lblVbr);
            this.Controls.Add(cmbVideoBitrate);
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
            this.Controls.Add(chkSaveAsNew);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnCancel);

            this.AcceptButton = btnSave;
            this.CancelButton = btnCancel;
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

                txtTitle.Text = Preset.Name ?? "";
                txtCustomArgs.Text = Preset.CustomArgs ?? "";

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
