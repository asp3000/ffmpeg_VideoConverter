// ============================================================================
//  PresetEditForm.cs — edit the parameters of a PresetOption.
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoConverter
{
    public partial class PresetEditForm : Form
    {
        public PresetOption Preset { get; set; }

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
            int h = 26;

            this.lblVideoCodec = new Label { Text = "视频编码:", Location = new Point(16, y), Size = new Size(80, 20) };
            this.txtVideoCodec = new TextBox { Location = new Point(100, y - 2), Size = new Size(160, h) };
            y += 34;

            this.lblResolution = new Label { Text = "分辨率:", Location = new Point(16, y), Size = new Size(80, 20) };
            this.txtResolution = new TextBox { Location = new Point(100, y - 2), Size = new Size(160, h) };
            y += 34;

            this.lblVideoBitrate = new Label { Text = "视频码率:", Location = new Point(16, y), Size = new Size(80, 20) };
            this.txtVideoBitrate = new TextBox { Location = new Point(100, y - 2), Size = new Size(160, h) };
            y += 34;

            this.lblFrameRate = new Label { Text = "帧率:", Location = new Point(16, y), Size = new Size(80, 20) };
            this.txtFrameRate = new TextBox { Location = new Point(100, y - 2), Size = new Size(160, h) };
            y += 34;

            this.lblAudioCodec = new Label { Text = "音频编码:", Location = new Point(16, y), Size = new Size(80, 20) };
            this.txtAudioCodec = new TextBox { Location = new Point(100, y - 2), Size = new Size(160, h) };
            y += 34;

            this.lblAudioBitrate = new Label { Text = "音频码率:", Location = new Point(16, y), Size = new Size(80, 20) };
            this.txtAudioBitrate = new TextBox { Location = new Point(100, y - 2), Size = new Size(160, h) };
            y += 44;

            this.btnOK = new Button
            {
                Text = "确定",
                Location = new Point(180, y),
                Size = new Size(80, 30),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            this.btnOK.FlatAppearance.BorderSize = 0;
            this.btnOK.DialogResult = DialogResult.OK;
            this.btnOK.Click += BtnOK_Click;

            this.btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(270, y),
                Size = new Size(80, 30),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat
            };
            this.btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            this.btnCancel.DialogResult = DialogResult.Cancel;

            this.ClientSize = new Size(370, y + 70);
            this.Controls.Add(this.lblVideoCodec);
            this.Controls.Add(this.txtVideoCodec);
            this.Controls.Add(this.lblResolution);
            this.Controls.Add(this.txtResolution);
            this.Controls.Add(this.lblVideoBitrate);
            this.Controls.Add(this.txtVideoBitrate);
            this.Controls.Add(this.lblFrameRate);
            this.Controls.Add(this.txtFrameRate);
            this.Controls.Add(this.lblAudioCodec);
            this.Controls.Add(this.txtAudioCodec);
            this.Controls.Add(this.lblAudioBitrate);
            this.Controls.Add(this.txtAudioBitrate);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.btnCancel);
            this.AcceptButton = this.btnOK;
            this.CancelButton = this.btnCancel;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (Preset == null) Preset = new PresetOption { Name = "Custom" };
            txtVideoCodec.Text = Preset.VideoCodec ?? "";
            txtResolution.Text = Preset.ResolutionValue ?? "";
            txtVideoBitrate.Text = Preset.VideoBitrate ?? "";
            txtFrameRate.Text = Preset.FrameRate ?? "";
            txtAudioCodec.Text = Preset.AudioCodec ?? "";
            txtAudioBitrate.Text = Preset.AudioBitrate ?? "";
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            if (Preset == null) Preset = new PresetOption { Name = "Custom" };
            Preset.VideoCodec = txtVideoCodec.Text.Trim();
            Preset.ResolutionValue = txtResolution.Text.Trim();
            Preset.ResolutionLabel = string.IsNullOrEmpty(Preset.ResolutionValue)
                ? "Same as source"
                : Preset.ResolutionValue.Replace("x", " x ");
            Preset.VideoBitrate = txtVideoBitrate.Text.Trim();
            Preset.FrameRate = txtFrameRate.Text.Trim();
            Preset.AudioCodec = txtAudioCodec.Text.Trim();
            Preset.AudioBitrate = txtAudioBitrate.Text.Trim();
        }

        private Label lblVideoCodec;
        private TextBox txtVideoCodec;
        private Label lblResolution;
        private TextBox txtResolution;
        private Label lblVideoBitrate;
        private TextBox txtVideoBitrate;
        private Label lblFrameRate;
        private TextBox txtFrameRate;
        private Label lblAudioCodec;
        private TextBox txtAudioCodec;
        private Label lblAudioBitrate;
        private TextBox txtAudioBitrate;
        private Button btnOK;
        private Button btnCancel;
    }
}
