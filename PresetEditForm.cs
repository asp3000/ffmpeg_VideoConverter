// ============================================================================
//  PresetEditForm.cs — edit the parameters of a PresetOption.
//  Dropdown values come from options_spec/format_options.json.
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

                _options = PresetDataStore.GetFormatOptions(Preset.FormatId);
                if (_options == null || _options.VideoCodecs.Count == 0)
                    _options = BuildFallbackOptions(Preset);

                LoadCombo(cmbVideoCodec, _options.VideoCodecs, Preset.VideoCodec, "自动");
                LoadCombo(cmbResolution, _options.Resolutions, Preset.ResolutionValue, "自动");
                LoadCombo(cmbFrameRate, _options.FrameRates, Preset.FrameRate, "自动");
                LoadCombo(cmbVideoBitrate, _options.VideoBitrates, Preset.VideoBitrate, "自动");
                LoadCombo(cmbAudioCodec, _options.AudioCodecs, Preset.AudioCodec, "自动");
                LoadCombo(cmbChannel, _options.Channels, Preset.Channels > 0 ? Preset.Channels.ToString() : null, "自动");
                LoadCombo(cmbSampleRate, _options.SampleRates, Preset.SampleRate, "自动");
                LoadCombo(cmbAudioBitrate, _options.AudioBitrates, Preset.AudioBitrate, "自动");

                txtTitle.Text = Preset.Name ?? "";

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

        private void LoadCombo(ComboBox cb, List<string> items, string current, string autoText)
        {
            cb.Items.Clear();
            cb.Items.Add(autoText);
            if (items != null)
            {
                foreach (var item in items)
                    if (!string.IsNullOrWhiteSpace(item) && !cb.Items.Contains(item))
                        cb.Items.Add(item);
            }

            if (!string.IsNullOrWhiteSpace(current) && cb.Items.Contains(current))
                cb.SelectedItem = current;
            else
                cb.SelectedIndex = 0;
        }

        private FormatOptions BuildFallbackOptions(PresetOption p)
        {
            var o = new FormatOptions();
            if (!string.IsNullOrWhiteSpace(p.VideoCodec)) o.VideoCodecs.Add(p.VideoCodec);
            if (!string.IsNullOrWhiteSpace(p.ResolutionValue)) o.Resolutions.Add(p.ResolutionValue);
            if (!string.IsNullOrWhiteSpace(p.FrameRate)) o.FrameRates.Add(p.FrameRate);
            if (!string.IsNullOrWhiteSpace(p.VideoBitrate)) o.VideoBitrates.Add(p.VideoBitrate);
            if (!string.IsNullOrWhiteSpace(p.AudioCodec)) o.AudioCodecs.Add(p.AudioCodec);
            if (!string.IsNullOrWhiteSpace(p.SampleRate)) o.SampleRates.Add(p.SampleRate);
            if (!string.IsNullOrWhiteSpace(p.AudioBitrate)) o.AudioBitrates.Add(p.AudioBitrate);
            o.Channels.AddRange(new[] { "1", "2", "6" });
            return o;
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
                Preset.ResolutionValue = GetComboValue(cmbResolution, null);
                Preset.ResolutionLabel = string.IsNullOrEmpty(Preset.ResolutionValue)
                    ? "与源文件相同"
                    : Preset.ResolutionValue.Replace("x", " x ");
                Preset.FrameRate = GetComboValue(cmbFrameRate, null);
                Preset.VideoBitrate = GetComboValue(cmbVideoBitrate, null);
                Preset.AudioCodec = GetComboValue(cmbAudioCodec, "copy");
                if (int.TryParse(GetComboValue(cmbChannel, null), out int ch))
                    Preset.Channels = ch;
                Preset.SampleRate = GetComboValue(cmbSampleRate, null);
                Preset.AudioBitrate = GetComboValue(cmbAudioBitrate, null);
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
            if (cb.SelectedIndex <= 0 || cb.SelectedItem == null)
                return defaultValue;
            return cb.SelectedItem.ToString();
        }
    }
}
