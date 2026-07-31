namespace VideoConverter
{
    partial class VideoConverter
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.topPanel = new System.Windows.Forms.Panel();
            this.addFilesButton = new System.Windows.Forms.Button();
            this.deleteButton = new System.Windows.Forms.Button();
            this.highSpeedCheck = new System.Windows.Forms.CheckBox();
            this.hardwareCheck = new System.Windows.Forms.CheckBox();
            this.tabCompletedLabel = new System.Windows.Forms.Label();
            this.tabConvertingLabel = new System.Windows.Forms.Label();
            this.convertingCountLabel = new System.Windows.Forms.Label();
            this.taskListPanel = new System.Windows.Forms.FlowLayoutPanel();
            this.bottomPanel = new System.Windows.Forms.Panel();
            this.convertAllButton = new System.Windows.Forms.Button();
            this.mergeCheck = new System.Windows.Forms.CheckBox();
            this.saveToCombo = new System.Windows.Forms.ComboBox();
            this.labelSaveTo = new System.Windows.Forms.Label();
            this.convertToCombo = new System.Windows.Forms.ComboBox();
            this.labelConvertTo = new System.Windows.Forms.Label();
            this.openFileDialog = new System.Windows.Forms.OpenFileDialog();
            this.topPanel.SuspendLayout();
            this.bottomPanel.SuspendLayout();
            this.SuspendLayout();
            // 
            // topPanel
            // 
            this.topPanel.BackColor = System.Drawing.Color.White;
            this.topPanel.Controls.Add(this.addFilesButton);
            this.topPanel.Controls.Add(this.deleteButton);
            this.topPanel.Controls.Add(this.hardwareCheck);
            this.topPanel.Controls.Add(this.highSpeedCheck);
            this.topPanel.Controls.Add(this.tabCompletedLabel);
            this.topPanel.Controls.Add(this.tabConvertingLabel);
            this.topPanel.Controls.Add(this.convertingCountLabel);
            this.topPanel.Dock = System.Windows.Forms.DockStyle.Top;
            this.topPanel.Location = new System.Drawing.Point(0, 0);
            this.topPanel.Name = "topPanel";
            this.topPanel.Size = new System.Drawing.Size(1280, 56);
            this.topPanel.TabIndex = 0;
            // 
            // addFilesButton
            // 
            this.addFilesButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.addFilesButton.BackColor = System.Drawing.Color.White;
            this.addFilesButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(124)))), ((int)(((byte)(77)))), ((int)(((byte)(255)))));
            this.addFilesButton.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.addFilesButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.addFilesButton.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(124)))), ((int)(((byte)(77)))), ((int)(((byte)(255)))));
            this.addFilesButton.Location = new System.Drawing.Point(1150, 12);
            this.addFilesButton.Name = "addFilesButton";
            this.addFilesButton.Size = new System.Drawing.Size(110, 32);
            this.addFilesButton.TabIndex = 5;
            this.addFilesButton.Text = "+ 添加文件";
            this.addFilesButton.UseVisualStyleBackColor = false;
            this.addFilesButton.Click += new System.EventHandler(this.AddFilesButton_Click);
            // 
            // deleteButton
            // 
            this.deleteButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.deleteButton.BackColor = System.Drawing.Color.White;
            this.deleteButton.FlatAppearance.BorderColor = System.Drawing.Color.FromArgb(((int)(((byte)(200)))), ((int)(((byte)(200)))), ((int)(((byte)(200)))));
            this.deleteButton.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.deleteButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.deleteButton.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(80)))), ((int)(((byte)(80)))));
            this.deleteButton.Location = new System.Drawing.Point(1072, 12);
            this.deleteButton.Name = "deleteButton";
            this.deleteButton.Size = new System.Drawing.Size(64, 32);
            this.deleteButton.TabIndex = 4;
            this.deleteButton.Text = "删除";
            this.deleteButton.UseVisualStyleBackColor = false;
            this.deleteButton.Click += new System.EventHandler(this.DeleteButton_Click);
            // 
            // highSpeedCheck
            // 
            this.highSpeedCheck.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.highSpeedCheck.Appearance = System.Windows.Forms.Appearance.Normal;
            this.highSpeedCheck.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(225)))), ((int)(((byte)(225)))), ((int)(((byte)(225)))));
            this.highSpeedCheck.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.highSpeedCheck.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.highSpeedCheck.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(90)))), ((int)(((byte)(90)))), ((int)(((byte)(90)))));
            this.highSpeedCheck.Location = new System.Drawing.Point(700, 16);
            this.highSpeedCheck.Name = "highSpeedCheck";
            this.highSpeedCheck.Size = new System.Drawing.Size(110, 26);
            this.highSpeedCheck.TabIndex = 3;
            this.highSpeedCheck.Text = "高速转换";
            this.highSpeedCheck.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.highSpeedCheck.UseVisualStyleBackColor = false;
            this.highSpeedCheck.CheckedChanged += new System.EventHandler(this.HighSpeedCheck_CheckedChanged);
            // 
            // hardwareCheck
            // 
            this.hardwareCheck.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.hardwareCheck.Appearance = System.Windows.Forms.Appearance.Normal;
            this.hardwareCheck.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(225)))), ((int)(((byte)(225)))), ((int)(((byte)(225)))));
            this.hardwareCheck.Enabled = false;
            this.hardwareCheck.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.hardwareCheck.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.hardwareCheck.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(90)))), ((int)(((byte)(90)))), ((int)(((byte)(90)))));
            this.hardwareCheck.Location = new System.Drawing.Point(820, 16);
            this.hardwareCheck.Name = "hardwareCheck";
            this.hardwareCheck.Size = new System.Drawing.Size(240, 26);
            this.hardwareCheck.TabIndex = 4;
            this.hardwareCheck.Text = "硬件编码";
            this.hardwareCheck.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.hardwareCheck.UseVisualStyleBackColor = false;
            this.hardwareCheck.CheckedChanged += new System.EventHandler(this.HardwareCheck_CheckedChanged);
            // 
            // tabCompletedLabel
            // 
            this.tabCompletedLabel.AutoSize = true;
            this.tabCompletedLabel.Cursor = System.Windows.Forms.Cursors.Hand;
            this.tabCompletedLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.tabCompletedLabel.ForeColor = System.Drawing.Color.Gray;
            this.tabCompletedLabel.Location = new System.Drawing.Point(160, 18);
            this.tabCompletedLabel.Name = "tabCompletedLabel";
            this.tabCompletedLabel.Size = new System.Drawing.Size(69, 20);
            this.tabCompletedLabel.TabIndex = 1;
            this.tabCompletedLabel.Text = "转换完成";
            this.tabCompletedLabel.Click += new System.EventHandler(this.TabCompletedLabel_Click);
            // 
            // tabConvertingLabel
            // 
            this.tabConvertingLabel.AutoSize = true;
            this.tabConvertingLabel.Cursor = System.Windows.Forms.Cursors.Hand;
            this.tabConvertingLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 11F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.tabConvertingLabel.ForeColor = System.Drawing.Color.Black;
            this.tabConvertingLabel.Location = new System.Drawing.Point(24, 18);
            this.tabConvertingLabel.Name = "tabConvertingLabel";
            this.tabConvertingLabel.Size = new System.Drawing.Size(84, 19);
            this.tabConvertingLabel.TabIndex = 0;
            this.tabConvertingLabel.Text = "正在转换";
            this.tabConvertingLabel.Click += new System.EventHandler(this.TabConvertingLabel_Click);
            // 
            // convertingCountLabel
            // 
            this.convertingCountLabel.AutoSize = true;
            this.convertingCountLabel.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.convertingCountLabel.ForeColor = System.Drawing.Color.Gray;
            this.convertingCountLabel.Location = new System.Drawing.Point(108, 19);
            this.convertingCountLabel.Name = "convertingCountLabel";
            this.convertingCountLabel.Size = new System.Drawing.Size(33, 20);
            this.convertingCountLabel.TabIndex = 2;
            this.convertingCountLabel.Text = "(0)";
            // 
            // taskListPanel
            // 
            this.taskListPanel.AutoScroll = true;
            this.taskListPanel.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(248)))), ((int)(((byte)(246)))), ((int)(((byte)(252)))));
            this.taskListPanel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.taskListPanel.FlowDirection = System.Windows.Forms.FlowDirection.TopDown;
            this.taskListPanel.Location = new System.Drawing.Point(0, 56);
            this.taskListPanel.Margin = new System.Windows.Forms.Padding(0);
            this.taskListPanel.Name = "taskListPanel";
            this.taskListPanel.Padding = new System.Windows.Forms.Padding(16, 12, 16, 12);
            this.taskListPanel.Size = new System.Drawing.Size(1280, 680);
            this.taskListPanel.TabIndex = 1;
            this.taskListPanel.WrapContents = false;
            // 
            // bottomPanel
            // 
            this.bottomPanel.BackColor = System.Drawing.Color.White;
            this.bottomPanel.Controls.Add(this.convertAllButton);
            this.bottomPanel.Controls.Add(this.mergeCheck);
            this.bottomPanel.Controls.Add(this.saveToCombo);
            this.bottomPanel.Controls.Add(this.labelSaveTo);
            this.bottomPanel.Controls.Add(this.convertToCombo);
            this.bottomPanel.Controls.Add(this.labelConvertTo);
            this.bottomPanel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.bottomPanel.Location = new System.Drawing.Point(0, 736);
            this.bottomPanel.Name = "bottomPanel";
            this.bottomPanel.Size = new System.Drawing.Size(1280, 64);
            this.bottomPanel.TabIndex = 2;
            // 
            // convertAllButton
            // 
            this.convertAllButton.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.convertAllButton.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(124)))), ((int)(((byte)(77)))), ((int)(((byte)(255)))));
            this.convertAllButton.FlatAppearance.BorderSize = 0;
            this.convertAllButton.FlatStyle = System.Windows.Forms.FlatStyle.Flat;
            this.convertAllButton.Font = new System.Drawing.Font("Microsoft YaHei UI", 10F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.convertAllButton.ForeColor = System.Drawing.Color.White;
            this.convertAllButton.Location = new System.Drawing.Point(1088, 12);
            this.convertAllButton.Name = "convertAllButton";
            this.convertAllButton.Size = new System.Drawing.Size(172, 40);
            this.convertAllButton.TabIndex = 5;
            this.convertAllButton.Text = "全部转换";
            this.convertAllButton.UseVisualStyleBackColor = false;
            this.convertAllButton.Click += new System.EventHandler(this.ConvertAllButton_Click);
            // 
            // mergeCheck
            // 
            this.mergeCheck.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.mergeCheck.AutoSize = true;
            this.mergeCheck.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.mergeCheck.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(80)))), ((int)(((byte)(80)))), ((int)(((byte)(80)))));
            this.mergeCheck.Location = new System.Drawing.Point(952, 22);
            this.mergeCheck.Name = "mergeCheck";
            this.mergeCheck.Size = new System.Drawing.Size(111, 21);
            this.mergeCheck.TabIndex = 4;
            this.mergeCheck.Text = "合并所有文件";
            this.mergeCheck.UseVisualStyleBackColor = true;
            // 
            // saveToCombo
            // 
            this.saveToCombo.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.saveToCombo.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.saveToCombo.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.saveToCombo.FormattingEnabled = true;
            this.saveToCombo.Location = new System.Drawing.Point(460, 20);
            this.saveToCombo.Name = "saveToCombo";
            this.saveToCombo.Size = new System.Drawing.Size(460, 25);
            this.saveToCombo.TabIndex = 3;
            // 
            // labelSaveTo
            // 
            this.labelSaveTo.AutoSize = true;
            this.labelSaveTo.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelSaveTo.ForeColor = System.Drawing.Color.Gray;
            this.labelSaveTo.Location = new System.Drawing.Point(400, 23);
            this.labelSaveTo.Name = "labelSaveTo";
            this.labelSaveTo.Size = new System.Drawing.Size(54, 17);
            this.labelSaveTo.TabIndex = 2;
            this.labelSaveTo.Text = "保存到";
            // 
            // convertToCombo
            // 
            this.convertToCombo.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.convertToCombo.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.convertToCombo.FormattingEnabled = true;
            this.convertToCombo.Location = new System.Drawing.Point(94, 20);
            this.convertToCombo.Name = "convertToCombo";
            this.convertToCombo.Size = new System.Drawing.Size(280, 25);
            this.convertToCombo.TabIndex = 1;
            this.convertToCombo.SelectedIndexChanged += new System.EventHandler(this.ConvertToCombo_SelectedIndexChanged);
            // 
            // labelConvertTo
            // 
            this.labelConvertTo.AutoSize = true;
            this.labelConvertTo.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.labelConvertTo.ForeColor = System.Drawing.Color.Gray;
            this.labelConvertTo.Location = new System.Drawing.Point(24, 23);
            this.labelConvertTo.Name = "labelConvertTo";
            this.labelConvertTo.Size = new System.Drawing.Size(64, 17);
            this.labelConvertTo.TabIndex = 0;
            this.labelConvertTo.Text = "转换到";
            // 
            // openFileDialog
            // 
            this.openFileDialog.Filter = "视频文件|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v|所有文件|*.*";
            this.openFileDialog.Multiselect = true;
            this.openFileDialog.Title = "添加视频文件";
            // 
            // VideoConverter
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.White;
            this.ClientSize = new System.Drawing.Size(1280, 800);
            this.Controls.Add(this.taskListPanel);
            this.Controls.Add(this.bottomPanel);
            this.Controls.Add(this.topPanel);
            this.Font = new System.Drawing.Font("Microsoft YaHei UI", 9F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.Margin = new System.Windows.Forms.Padding(3, 4, 3, 4);
            this.MinimumSize = new System.Drawing.Size(900, 500);
            this.Name = "VideoConverter";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Tag = "no-theme";
            this.Text = "视频转换器";
            this.topPanel.ResumeLayout(false);
            this.topPanel.PerformLayout();
            this.bottomPanel.ResumeLayout(false);
            this.bottomPanel.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.Panel topPanel;
        private System.Windows.Forms.Label tabConvertingLabel;
        private System.Windows.Forms.Label tabCompletedLabel;
        private System.Windows.Forms.Label convertingCountLabel;
        private System.Windows.Forms.CheckBox highSpeedCheck;
        private System.Windows.Forms.CheckBox hardwareCheck;
        private System.Windows.Forms.Button deleteButton;
        private System.Windows.Forms.Button addFilesButton;
        private System.Windows.Forms.FlowLayoutPanel taskListPanel;
        private System.Windows.Forms.Panel bottomPanel;
        private System.Windows.Forms.Label labelConvertTo;
        private System.Windows.Forms.ComboBox convertToCombo;
        private System.Windows.Forms.ComboBox saveToCombo;
        private System.Windows.Forms.Label labelSaveTo;
        private System.Windows.Forms.CheckBox mergeCheck;
        private System.Windows.Forms.Button convertAllButton;
        private System.Windows.Forms.OpenFileDialog openFileDialog;
    }
}
