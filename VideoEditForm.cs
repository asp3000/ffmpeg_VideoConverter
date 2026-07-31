// ============================================================================
//  VideoEditForm.cs — simple trim editor for a single ConversionTask.
// ============================================================================

using System;
using System.Drawing;
using System.Windows.Forms;

namespace VideoConverter
{
    public partial class VideoEditForm : Form
    {
        public double TrimStartSeconds { get; set; }
        public double TrimEndSeconds { get; set; }
        public double SourceDurationSeconds { get; set; }

        public VideoEditForm()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "视频编辑";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
        }

        private void InitializeComponent()
        {
            this.lblStart = new Label();
            this.txtStart = new TextBox();
            this.lblEnd = new Label();
            this.txtEnd = new TextBox();
            this.lblHint = new Label();
            this.btnOK = new Button();
            this.btnCancel = new Button();

            this.lblStart.Text = "开始时间 (秒):";
            this.lblStart.Location = new Point(16, 16);
            this.lblStart.Size = new Size(120, 20);

            this.txtStart.Location = new Point(140, 14);
            this.txtStart.Size = new Size(120, 23);

            this.lblEnd.Text = "结束时间 (秒):";
            this.lblEnd.Location = new Point(16, 50);
            this.lblEnd.Size = new Size(120, 20);

            this.txtEnd.Location = new Point(140, 48);
            this.txtEnd.Size = new Size(120, 23);

            this.lblHint.Location = new Point(16, 84);
            this.lblHint.Size = new Size(360, 34);
            this.lblHint.ForeColor = Color.Gray;
            this.lblHint.Text = "留空表示不剪切。结束时间必须大于开始时间。";

            this.btnOK.Text = "确定";
            this.btnOK.Location = new Point(210, 130);
            this.btnOK.Size = new Size(80, 30);
            this.btnOK.BackColor = Color.FromArgb(124, 77, 255);
            this.btnOK.ForeColor = Color.White;
            this.btnOK.FlatStyle = FlatStyle.Flat;
            this.btnOK.FlatAppearance.BorderSize = 0;
            this.btnOK.DialogResult = DialogResult.OK;
            this.btnOK.Click += BtnOK_Click;

            this.btnCancel.Text = "取消";
            this.btnCancel.Location = new Point(300, 130);
            this.btnCancel.Size = new Size(80, 30);
            this.btnCancel.BackColor = Color.White;
            this.btnCancel.ForeColor = Color.FromArgb(80, 80, 80);
            this.btnCancel.FlatStyle = FlatStyle.Flat;
            this.btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            this.btnCancel.DialogResult = DialogResult.Cancel;

            this.ClientSize = new Size(400, 180);
            this.Controls.Add(this.lblStart);
            this.Controls.Add(this.txtStart);
            this.Controls.Add(this.lblEnd);
            this.Controls.Add(this.txtEnd);
            this.Controls.Add(this.lblHint);
            this.Controls.Add(this.btnOK);
            this.Controls.Add(this.btnCancel);
            this.AcceptButton = this.btnOK;
            this.CancelButton = this.btnCancel;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            this.txtStart.Text = TrimStartSeconds > 0 ? TrimStartSeconds.ToString("0.00") : "";
            this.txtEnd.Text = (TrimEndSeconds > 0 && TrimEndSeconds < SourceDurationSeconds)
                ? TrimEndSeconds.ToString("0.00")
                : (SourceDurationSeconds > 0 ? SourceDurationSeconds.ToString("0.00") : "");
        }

        private void BtnOK_Click(object sender, EventArgs e)
        {
            double start = 0, end = 0;
            if (!string.IsNullOrWhiteSpace(txtStart.Text) &&
                !double.TryParse(txtStart.Text, out start))
            {
                MessageBox.Show(this, "开始时间格式错误", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }
            if (!string.IsNullOrWhiteSpace(txtEnd.Text) &&
                !double.TryParse(txtEnd.Text, out end))
            {
                MessageBox.Show(this, "结束时间格式错误", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }
            if (end > 0 && end <= start)
            {
                MessageBox.Show(this, "结束时间必须大于开始时间", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.None;
                return;
            }
            TrimStartSeconds = start;
            TrimEndSeconds = end;
        }

        private Label lblStart;
        private TextBox txtStart;
        private Label lblEnd;
        private TextBox txtEnd;
        private Label lblHint;
        private Button btnOK;
        private Button btnCancel;
    }
}
