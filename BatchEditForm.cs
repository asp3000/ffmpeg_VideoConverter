// ============================================================================
//  BatchEditForm.cs — 批量编辑器：对多个任务批量应用预设/格式/编码参数。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace VideoConverter
{
    public class BatchEditForm : Form
    {
        private readonly List<ConversionTask> _tasks;
        private CheckedListBox taskList;
        private ComboBox presetCombo;
        private CheckBox chkDeinterlace;
        private CheckBox chkTwoPass;
        private CheckBox chkLossless;
        private TextBox txtProfile;
        private TextBox txtLevel;
        private NumericUpDown speedNum;

        public BatchEditForm(List<ConversionTask> tasks)
        {
            _tasks = tasks ?? new List<ConversionTask>();
            InitializeUI();
        }

        private void InitializeUI()
        {
            this.Text = "批量编辑";
            this.StartPosition = FormStartPosition.CenterParent;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.BackColor = Color.White;
            this.Size = new Size(620, 620);
            try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            int y = 12;

            // 任务列表
            var lblTasks = new Label
            {
                Location = new Point(16, y),
                Size = new Size(200, 20),
                Text = "选择要批量编辑的任务：",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            this.Controls.Add(lblTasks);
            y += 24;

            taskList = new CheckedListBox
            {
                Location = new Point(16, y),
                Size = new Size(570, 200),
                CheckOnClick = true,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            foreach (var t in _tasks)
            {
                string name = System.IO.Path.GetFileName(t.InputPath);
                taskList.Items.Add(name, false);
            }
            this.Controls.Add(taskList);
            y += 210;

            // 全选/取消全选按钮
            var btnAll = new Button { Location = new Point(16, y), Size = new Size(80, 26), Text = "全选", FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            btnAll.Click += (s, e) => { for (int i = 0; i < taskList.Items.Count; i++) taskList.SetItemChecked(i, true); };
            this.Controls.Add(btnAll);

            var btnNone = new Button { Location = new Point(104, y), Size = new Size(80, 26), Text = "取消全选", FlatStyle = FlatStyle.Flat, BackColor = Color.White };
            btnNone.Click += (s, e) => { for (int i = 0; i < taskList.Items.Count; i++) taskList.SetItemChecked(i, false); };
            this.Controls.Add(btnNone);
            y += 36;

            // 分隔线
            var sep = new Label { Location = new Point(16, y), Size = new Size(570, 1), BackColor = Color.FromArgb(220, 220, 220) };
            this.Controls.Add(sep);
            y += 12;

            // 预设选择
            var lblPreset = new Label { Location = new Point(16, y + 4), Size = new Size(80, 20), Text = "转换预设", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblPreset);

            presetCombo = new ComboBox { Location = new Point(100, y), Size = new Size(200, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            presetCombo.Items.Add("（不修改）");
            foreach (var cat in PresetDataStore.Categories)
            {
                if (!PresetDataStore.FormatsByCategory.ContainsKey(cat)) continue;
                foreach (var fmt in PresetDataStore.FormatsByCategory[cat])
                    foreach (var p in fmt.Presets)
                        presetCombo.Items.Add(p.FormatName + " / " + p.Name);
            }
            presetCombo.SelectedIndex = 0;
            this.Controls.Add(presetCombo);
            y += 32;

            // 高级选项
            var lblAdvanced = new Label { Location = new Point(16, y), Size = new Size(200, 20), Text = "高级选项（可选）：", Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
            this.Controls.Add(lblAdvanced);
            y += 24;

            chkDeinterlace = new CheckBox { Location = new Point(16, y), Size = new Size(100, 24), Text = "去隔行" };
            this.Controls.Add(chkDeinterlace);

            chkTwoPass = new CheckBox { Location = new Point(130, y), Size = new Size(100, 24), Text = "双通道编码" };
            this.Controls.Add(chkTwoPass);

            chkLossless = new CheckBox { Location = new Point(250, y), Size = new Size(100, 24), Text = "无损转换" };
            this.Controls.Add(chkLossless);
            y += 28;

            // Profile/Level
            var lblProfile = new Label { Location = new Point(16, y + 4), Size = new Size(80, 20), Text = "H264 Profile", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblProfile);
            txtProfile = new TextBox { Location = new Point(100, y), Size = new Size(100, 23) };
            this.Controls.Add(txtProfile);

            var lblLevel = new Label { Location = new Point(220, y + 4), Size = new Size(60, 20), Text = "Level", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblLevel);
            txtLevel = new TextBox { Location = new Point(280, y), Size = new Size(60, 23) };
            this.Controls.Add(txtLevel);
            y += 32;

            // 调速
            var lblSpeed = new Label { Location = new Point(16, y + 4), Size = new Size(80, 20), Text = "播放速度", TextAlign = ContentAlignment.MiddleLeft };
            this.Controls.Add(lblSpeed);
            speedNum = new NumericUpDown { Location = new Point(100, y), Size = new Size(80, 23), Minimum = 0.25M, Maximum = 4.0M, Value = 1.0M, DecimalPlaces = 2, Increment = 0.25M };
            this.Controls.Add(speedNum);
            y += 40;

            // 应用按钮
            var btnApply = new Button
            {
                Location = new Point(380, y),
                Size = new Size(100, 32),
                Text = "应用",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            btnApply.FlatAppearance.BorderSize = 0;
            btnApply.Click += BtnApply_Click;
            this.Controls.Add(btnApply);

            var btnCancel = new Button
            {
                Location = new Point(490, y),
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

        private void BtnApply_Click(object sender, EventArgs e)
        {
            var selected = new List<ConversionTask>();
            for (int i = 0; i < taskList.Items.Count; i++)
            {
                if (taskList.GetItemChecked(i))
                    selected.Add(_tasks[i]);
            }

            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请至少选择一个任务。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 应用预设
            if (presetCombo.SelectedIndex > 0)
            {
                string selectedText = presetCombo.SelectedItem.ToString();
                PresetOption found = null;
                foreach (var cat in PresetDataStore.Categories)
                {
                    if (!PresetDataStore.FormatsByCategory.ContainsKey(cat)) continue;
                    foreach (var fmt in PresetDataStore.FormatsByCategory[cat])
                        foreach (var p in fmt.Presets)
                            if ((p.FormatName + " / " + p.Name) == selectedText) { found = p.Clone(); break; }
                    if (found != null) break;
                }
                if (found != null)
                {
                    foreach (var t in selected)
                        t.Preset = found.Clone();
                }
            }

            // 应用高级选项
            foreach (var t in selected)
            {
                if (chkDeinterlace.Checked) t.Deinterlace = true;
                if (chkTwoPass.Checked) t.TwoPass = true;
                if (chkLossless.Checked) t.Lossless = true;
                if (!string.IsNullOrWhiteSpace(txtProfile.Text)) t.H264Profile = txtProfile.Text.Trim();
                if (!string.IsNullOrWhiteSpace(txtLevel.Text)) t.H264Level = txtLevel.Text.Trim();
                if (speedNum.Value != 1.0M) t.Speed = (double)speedNum.Value;
            }

            MessageBox.Show(this, string.Format("已对 {0} 个任务应用批量编辑。", selected.Count),
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
