// ============================================================================
//  MediaInfoEditorForm.cs — 媒体信息编辑器：查看并编辑元数据和高级编码参数。
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace VideoConverter
{
    public class MediaInfoEditorForm : Form
    {
        private readonly ConversionTask _task;

        private TextBox txtTitle;
        private TextBox txtAuthor;
        private TextBox txtYear;
        private TextBox txtComment;
        private CheckBox chkDeinterlace;
        private CheckBox chkTwoPass;
        private CheckBox chkLossless;
        private ComboBox cmbProfile;
        private ComboBox cmbLevel;
        private CheckBox chkBurnSubtitle;
        private TextBox txtSubtitleStyle;

        public MediaInfoEditorForm(ConversionTask task)
        {
            _task = task;
            InitializeUI();
            LoadValues();
        }

        private void InitializeUI()
        {
            this.Text = "媒体信息编辑";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.White;
            this.Size = new Size(560, 620);
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int y = 12;
            int labelW = 90;
            int inputX = 110;
            int inputW = 420;

            // 标题
            var lblTitle = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "标题", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblTitle);
            txtTitle = new TextBox { Location = new Point(inputX, y), Size = new Size(inputW, 23) };
            this.Controls.Add(txtTitle);
            y += 32;

            // 作者
            var lblAuthor = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "作者/艺术家", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblAuthor);
            txtAuthor = new TextBox { Location = new Point(inputX, y), Size = new Size(inputW, 23) };
            this.Controls.Add(txtAuthor);
            y += 32;

            // 年份
            var lblYear = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "年份", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblYear);
            txtYear = new TextBox { Location = new Point(inputX, y), Size = new Size(120, 23) };
            this.Controls.Add(txtYear);
            y += 32;

            // 备注
            var lblComment = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "备注/描述", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblComment);
            txtComment = new TextBox { Location = new Point(inputX, y), Size = new Size(inputW, 60), Multiline = true };
            this.Controls.Add(txtComment);
            y += 72;

            // 分隔线
            var sep1 = new Label { Location = new Point(16, y), Size = new Size(510, 1), BackColor = Color.FromArgb(220, 220, 220) };
            this.Controls.Add(sep1);
            y += 12;

            // 编码选项标题
            var lblEnc = new Label { Location = new Point(16, y), Size = new Size(200, 20), Text = "编码选项：", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            this.Controls.Add(lblEnc);
            y += 24;

            // 去隔行
            chkDeinterlace = new CheckBox { Location = new Point(16, y), Size = new Size(120, 24), Text = "去隔行 (yadif)" };
            this.Controls.Add(chkDeinterlace);

            // 双通道
            chkTwoPass = new CheckBox { Location = new Point(160, y), Size = new Size(120, 24), Text = "双通道编码" };
            this.Controls.Add(chkTwoPass);

            // 无损
            chkLossless = new CheckBox { Location = new Point(300, y), Size = new Size(120, 24), Text = "无损转换" };
            this.Controls.Add(chkLossless);
            y += 32;

            // H264 Profile
            var lblProfile = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "H264 Profile", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblProfile);
            cmbProfile = new ComboBox { Location = new Point(inputX, y), Size = new Size(120, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbProfile.Items.AddRange(new object[] { "（不指定）", "baseline", "main", "high", "high444" });
            cmbProfile.SelectedIndex = 0;
            this.Controls.Add(cmbProfile);
            y += 32;

            // H264 Level
            var lblLevel = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "H264 Level", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblLevel);
            cmbLevel = new ComboBox { Location = new Point(inputX, y), Size = new Size(80, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbLevel.Items.AddRange(new object[] { "（不指定）", "3.0", "3.1", "4.0", "4.1", "4.2", "5.0", "5.1", "5.2" });
            cmbLevel.SelectedIndex = 0;
            this.Controls.Add(cmbLevel);
            y += 36;

            // 分隔线
            var sep2 = new Label { Location = new Point(16, y), Size = new Size(510, 1), BackColor = Color.FromArgb(220, 220, 220) };
            this.Controls.Add(sep2);
            y += 12;

            // 字幕选项标题
            var lblSub = new Label { Location = new Point(16, y), Size = new Size(200, 20), Text = "字幕选项：", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            this.Controls.Add(lblSub);
            y += 24;

            // 烧录字幕
            chkBurnSubtitle = new CheckBox { Location = new Point(16, y), Size = new Size(200, 24), Text = "烧录字幕到画面（硬字幕）" };
            this.Controls.Add(chkBurnSubtitle);
            y += 28;

            // 字幕样式
            var lblStyle = new Label { Location = new Point(16, y + 4), Size = new Size(labelW, 20), Text = "字幕样式", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblStyle);
            txtSubtitleStyle = new TextBox { Location = new Point(inputX, y), Size = new Size(inputW, 23) };
            var lblStyleHint = new Label { Location = new Point(inputX, y + 24), Size = new Size(inputW, 20), Text = "如：FontSize=24,PrimaryColour=&H00FFFFFF", ForeColor = Color.Gray, Font = new Font("Consolas", 8F) };
            this.Controls.Add(txtSubtitleStyle);
            this.Controls.Add(lblStyleHint);
            y += 56;

            // 保存/取消按钮
            var btnSave = new Button
            {
                Location = new Point(320, y),
                Size = new Size(100, 32),
                Text = "保存",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Click += BtnSave_Click;
            this.Controls.Add(btnSave);

            var btnCancel = new Button
            {
                Location = new Point(430, y),
                Size = new Size(100, 32),
                Text = "取消",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                DialogResult = DialogResult.Cancel
            };
            this.Controls.Add(btnCancel);

            this.CancelButton = btnCancel;
        }

        private void LoadValues()
        {
            txtTitle.Text = _task.MetaTitle ?? "";
            txtAuthor.Text = _task.MetaAuthor ?? "";
            txtYear.Text = _task.MetaYear ?? "";
            txtComment.Text = _task.MetaComment ?? "";
            chkDeinterlace.Checked = _task.Deinterlace;
            chkTwoPass.Checked = _task.TwoPass;
            chkLossless.Checked = _task.Lossless;
            if (!string.IsNullOrEmpty(_task.H264Profile))
            {
                int idx = cmbProfile.Items.IndexOf(_task.H264Profile);
                if (idx >= 0) cmbProfile.SelectedIndex = idx;
            }
            if (!string.IsNullOrEmpty(_task.H264Level))
            {
                int idx = cmbLevel.Items.IndexOf(_task.H264Level);
                if (idx >= 0) cmbLevel.SelectedIndex = idx;
            }
            chkBurnSubtitle.Checked = _task.BurnSubtitle;
            txtSubtitleStyle.Text = _task.SubtitleStyle ?? "";
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            _task.MetaTitle = string.IsNullOrWhiteSpace(txtTitle.Text) ? null : txtTitle.Text.Trim();
            _task.MetaAuthor = string.IsNullOrWhiteSpace(txtAuthor.Text) ? null : txtAuthor.Text.Trim();
            _task.MetaYear = string.IsNullOrWhiteSpace(txtYear.Text) ? null : txtYear.Text.Trim();
            _task.MetaComment = string.IsNullOrWhiteSpace(txtComment.Text) ? null : txtComment.Text.Trim();
            _task.Deinterlace = chkDeinterlace.Checked;
            _task.TwoPass = chkTwoPass.Checked;
            _task.Lossless = chkLossless.Checked;
            _task.H264Profile = cmbProfile.SelectedIndex > 0 ? cmbProfile.SelectedItem.ToString() : null;
            _task.H264Level = cmbLevel.SelectedIndex > 0 ? cmbLevel.SelectedItem.ToString() : null;
            _task.BurnSubtitle = chkBurnSubtitle.Checked;
            _task.SubtitleStyle = string.IsNullOrWhiteSpace(txtSubtitleStyle.Text) ? null : txtSubtitleStyle.Text.Trim();

            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
