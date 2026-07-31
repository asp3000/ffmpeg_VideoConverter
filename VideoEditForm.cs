// ============================================================================
//  VideoEditForm.cs — visual trim + crop editor for a single ConversionTask.
//  Tabs: 剪切 (split/merge timeline) | 裁剪 (crop/rotate preview).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VideoConverter
{
    public partial class VideoEditForm : Form
    {
        // ---- public I/O -------------------------------------------------------
        public string InputPath { get; set; }
        public double SourceDurationSeconds { get; set; }
        public int SourceWidth { get; set; }
        public int SourceHeight { get; set; }
        public double FrameRate { get; set; }

        public List<VideoSegment> Segments { get; set; }
        public CropRegion Crop { get; set; }
        public int Rotation { get; set; }
        public bool MergeSegments { get; set; } = true;

        // ---- working state ----------------------------------------------------
        private List<VideoSegment> _segments;
        private List<List<VideoSegment>> _undoStack = new List<List<VideoSegment>>();
        private int _undoPos = -1;

        private long _currentTimeMs;
        private bool _isPlaying;
        private Timer _playTimer;
        private long _totalMs;
        private long _frameIntervalMs;

        private List<long> _keyframes = new List<long>();
        private List<Image> _thumbnails = new List<Image>();
        private int _thumbnailCount = 20;

        private CropRegion _crop;
        private int _rotation;
        private bool _draggingCrop;
        private Point _dragStart;
        private Rectangle _cropRect;

        // ---- controls ---------------------------------------------------------
        private TabControl tabControl;
        private TabPage tabTrim;
        private TabPage tabCrop;

        // trim tab
        private PictureBox picPreview;
        private Button btnPrevKeyframe;
        private Button btnPrevFrame;
        private Button btnPlay;
        private Button btnNextFrame;
        private Button btnNextKeyframe;
        private Label lblTime;
        private Panel panelTimeline;
        private TrackBar trackHead;
        private Button btnSplit;
        private Button btnDelete;
        private Button btnUndo;
        private Button btnRedo;
        private CheckBox chkMerge;
        private Button btnOK;
        private Button btnCancel;

        // crop tab
        private PictureBox picCropPreview;
        private PictureBox picCropOutput;
        private Button btnRotLeft;
        private Button btnRotRight;
        private Button btnFlipH;
        private Button btnFlipV;
        private Label lblOriginalSize;
        private Label lblCropSize;
        private TextBox txtCropX;
        private TextBox txtCropY;
        private TextBox txtCropW;
        private TextBox txtCropH;
        private Button btnCenter;
        private Button btnRatio169;
        private Button btnRatio916;
        private Button btnRatio11;
        private Button btnRatio43;
        private ComboBox cmbAspect;
        private Label lblCropTime;
        private Button btnCropPlay;

        public VideoEditForm()
        {
            InitializeComponent();
            this.StartPosition = FormStartPosition.CenterParent;
            this.Text = "视频编辑";
            this.BackColor = Color.White;
            this.Font = new Font("Microsoft YaHei UI", 9F);
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MinimizeBox = false;
            this.MaximizeBox = false;
            this.ClientSize = new Size(1040, 720);
            this.DoubleBuffered = true;
        }

        private void InitializeComponent()
        {
            tabControl = new TabControl();
            tabControl.Location = new Point(12, 12);
            tabControl.Size = new Size(1000, 640);
            tabControl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;

            tabTrim = new TabPage("剪切");
            tabTrim.BackColor = Color.White;

            tabCrop = new TabPage("裁剪");
            tabCrop.BackColor = Color.White;

            tabControl.TabPages.Add(tabTrim);
            tabControl.TabPages.Add(tabCrop);
            tabControl.SelectedIndexChanged += TabControl_SelectedIndexChanged;
            this.Controls.Add(tabControl);

            BuildTrimTab();
            BuildCropTab();
            BuildBottomButtons();
        }

        #region Trim Tab

        private void BuildTrimTab()
        {
            int margin = 16;
            int topY = 16;
            int previewH = 360;

            picPreview = new PictureBox();
            picPreview.Location = new Point(margin, topY);
            picPreview.Size = new Size(tabTrim.ClientSize.Width - margin * 2, previewH);
            picPreview.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            picPreview.BackColor = Color.Black;
            picPreview.SizeMode = PictureBoxSizeMode.Zoom;
            tabTrim.Controls.Add(picPreview);

            int ctrlY = topY + previewH + 12;
            int btnH = 32;
            int btnW = 40;
            int startX = (tabTrim.ClientSize.Width - btnW * 5 - 16 * 4) / 2;

            btnPrevKeyframe = CreateIconButton("⏮", startX, ctrlY, btnW, btnH, "上一关键帧");
            btnPrevFrame = CreateIconButton("◀", startX + btnW + 8, ctrlY, btnW, btnH, "上一帧");
            btnPlay = CreateIconButton("▶", startX + (btnW + 8) * 2, ctrlY, btnW + 20, btnH, "播放/停止");
            btnNextFrame = CreateIconButton("▶", startX + (btnW + 8) * 3 + 20, ctrlY, btnW, btnH, "下一帧");
            btnNextKeyframe = CreateIconButton("⏭", startX + (btnW + 8) * 4 + 20, ctrlY, btnW, btnH, "下一关键帧");

            btnPrevKeyframe.Click += (s, e) => SeekPrevKeyframe();
            btnPrevFrame.Click += (s, e) => StepFrame(-1);
            btnPlay.Click += (s, e) => TogglePlay();
            btnNextFrame.Click += (s, e) => StepFrame(1);
            btnNextKeyframe.Click += (s, e) => SeekNextKeyframe();

            tabTrim.Controls.Add(btnPrevKeyframe);
            tabTrim.Controls.Add(btnPrevFrame);
            tabTrim.Controls.Add(btnPlay);
            tabTrim.Controls.Add(btnNextFrame);
            tabTrim.Controls.Add(btnNextKeyframe);

            lblTime = new Label();
            lblTime.Location = new Point(tabTrim.ClientSize.Width - margin - 180, ctrlY + 4);
            lblTime.Size = new Size(180, 24);
            lblTime.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblTime.TextAlign = ContentAlignment.MiddleRight;
            lblTime.Text = "00:00:00.000 / 00:00:00.000";
            tabTrim.Controls.Add(lblTime);

            int timelineY = ctrlY + btnH + 16;
            panelTimeline = new Panel();
            panelTimeline.Location = new Point(margin, timelineY);
            panelTimeline.Size = new Size(tabTrim.ClientSize.Width - margin * 2, 110);
            panelTimeline.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            panelTimeline.BackColor = Color.FromArgb(245, 245, 245);
            panelTimeline.Paint += Timeline_Paint;
            panelTimeline.MouseDown += Timeline_MouseDown;
            panelTimeline.MouseMove += Timeline_MouseMove;
            panelTimeline.MouseUp += Timeline_MouseUp;
            tabTrim.Controls.Add(panelTimeline);

            trackHead = new TrackBar();
            trackHead.Location = new Point(margin, timelineY - 8);
            trackHead.Size = new Size(panelTimeline.Width, 30);
            trackHead.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            trackHead.Minimum = 0;
            trackHead.Maximum = 1000;
            trackHead.TickStyle = TickStyle.None;
            trackHead.ValueChanged += TrackHead_ValueChanged;
            tabTrim.Controls.Add(trackHead);

            int toolY = timelineY + panelTimeline.Height + 16;
            btnSplit = CreateTextButton("✂ 剪切", margin, toolY, 80, 32, "在播放头处分割");
            btnDelete = CreateTextButton("🗑 删除", margin + 90, toolY, 80, 32, "删除选中段");
            btnUndo = CreateTextButton("↩ 撤销", margin + 180, toolY, 80, 32, "撤销");
            btnRedo = CreateTextButton("↪ 重做", margin + 270, toolY, 80, 32, "重做");

            btnSplit.Click += BtnSplit_Click;
            btnDelete.Click += BtnDelete_Click;
            btnUndo.Click += BtnUndo_Click;
            btnRedo.Click += BtnRedo_Click;

            tabTrim.Controls.Add(btnSplit);
            tabTrim.Controls.Add(btnDelete);
            tabTrim.Controls.Add(btnUndo);
            tabTrim.Controls.Add(btnRedo);

            chkMerge = new CheckBox();
            chkMerge.Text = "合并到一个文件";
            chkMerge.Location = new Point(tabTrim.ClientSize.Width - margin - 140, toolY + 6);
            chkMerge.Size = new Size(140, 24);
            chkMerge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            chkMerge.Checked = true;
            tabTrim.Controls.Add(chkMerge);

            tabTrim.Resize += (s, e) =>
            {
                picPreview.Width = tabTrim.ClientSize.Width - margin * 2;
                panelTimeline.Width = tabTrim.ClientSize.Width - margin * 2;
                trackHead.Width = panelTimeline.Width;
                lblTime.Left = tabTrim.ClientSize.Width - margin - 180;
                chkMerge.Left = tabTrim.ClientSize.Width - margin - 140;
                int cx = (tabTrim.ClientSize.Width - btnW * 5 - 8 * 4) / 2;
                btnPrevKeyframe.Left = cx;
                btnPrevFrame.Left = cx + btnW + 8;
                btnPlay.Left = cx + (btnW + 8) * 2;
                btnNextFrame.Left = cx + (btnW + 8) * 3 + 20;
                btnNextKeyframe.Left = cx + (btnW + 8) * 4 + 20;
            };
        }

        #endregion

        #region Crop Tab

        private void BuildCropTab()
        {
            int margin = 16;
            int topY = 16;
            int previewW = 600;
            int previewH = 420;

            picCropPreview = new PictureBox();
            picCropPreview.Location = new Point(margin, topY);
            picCropPreview.Size = new Size(previewW, previewH);
            picCropPreview.BackColor = Color.Black;
            picCropPreview.SizeMode = PictureBoxSizeMode.Zoom;
            picCropPreview.Paint += PicCropPreview_Paint;
            picCropPreview.MouseDown += PicCropPreview_MouseDown;
            picCropPreview.MouseMove += PicCropPreview_MouseMove;
            picCropPreview.MouseUp += PicCropPreview_MouseUp;
            tabCrop.Controls.Add(picCropPreview);

            int rightX = margin + previewW + 20;
            int y = topY;

            var lblRot = new Label();
            lblRot.Text = "旋转/翻转:";
            lblRot.Location = new Point(rightX, y);
            lblRot.Size = new Size(100, 20);
            tabCrop.Controls.Add(lblRot);

            y += 24;
            btnRotLeft = CreateIconButton("↺ 90°", rightX, y, 60, 32, "逆时针 90°");
            btnRotRight = CreateIconButton("↻ 90°", rightX + 68, y, 60, 32, "顺时针 90°");
            btnFlipH = CreateIconButton("⇄", rightX + 136, y, 40, 32, "水平翻转");
            btnFlipV = CreateIconButton("⇅", rightX + 184, y, 40, 32, "垂直翻转");
            btnRotLeft.Click += (s, e) => ApplyRotation(2);
            btnRotRight.Click += (s, e) => ApplyRotation(1);
            btnFlipH.Click += (s, e) => ApplyRotation(4);
            btnFlipV.Click += (s, e) => ApplyRotation(5);
            tabCrop.Controls.Add(btnRotLeft);
            tabCrop.Controls.Add(btnRotRight);
            tabCrop.Controls.Add(btnFlipH);
            tabCrop.Controls.Add(btnFlipV);

            y += 48;
            lblOriginalSize = new Label();
            lblOriginalSize.Location = new Point(rightX, y);
            lblOriginalSize.Size = new Size(200, 20);
            lblOriginalSize.Text = "原始大小: -";
            tabCrop.Controls.Add(lblOriginalSize);

            y += 28;
            var lblCropSizeTitle = new Label();
            lblCropSizeTitle.Text = "裁剪区域大小:";
            lblCropSizeTitle.Location = new Point(rightX, y);
            lblCropSizeTitle.Size = new Size(100, 20);
            tabCrop.Controls.Add(lblCropSizeTitle);

            y += 24;
            txtCropW = CreateNumberInput(rightX, y, 60, 24);
            var lblX = new Label(); lblX.Text = "×"; lblX.Location = new Point(rightX + 66, y + 2); lblX.Size = new Size(12, 20);
            txtCropH = CreateNumberInput(rightX + 82, y, 60, 24);
            var lblAt = new Label(); lblAt.Text = "@"; lblAt.Location = new Point(rightX + 148, y + 2); lblAt.Size = new Size(12, 20);
            txtCropX = CreateNumberInput(rightX + 164, y, 60, 24);
            var lblComma = new Label(); lblComma.Text = ","; lblComma.Location = new Point(rightX + 228, y + 2); lblComma.Size = new Size(8, 20);
            txtCropY = CreateNumberInput(rightX + 238, y, 60, 24);

            txtCropX.TextChanged += CropInput_TextChanged;
            txtCropY.TextChanged += CropInput_TextChanged;
            txtCropW.TextChanged += CropInput_TextChanged;
            txtCropH.TextChanged += CropInput_TextChanged;

            tabCrop.Controls.Add(txtCropW);
            tabCrop.Controls.Add(lblX);
            tabCrop.Controls.Add(txtCropH);
            tabCrop.Controls.Add(lblAt);
            tabCrop.Controls.Add(txtCropX);
            tabCrop.Controls.Add(lblComma);
            tabCrop.Controls.Add(txtCropY);

            y += 36;
            btnCenter = CreateTextButton("居中对齐", rightX, y, 80, 28, "裁剪区居中");
            btnCenter.Click += (s, e) => CenterCrop();
            tabCrop.Controls.Add(btnCenter);

            y += 48;
            var lblRatio = new Label();
            lblRatio.Text = "固定比例:";
            lblRatio.Location = new Point(rightX, y);
            lblRatio.Size = new Size(80, 20);
            tabCrop.Controls.Add(lblRatio);

            y += 24;
            btnRatio169 = CreateTextButton("16:9", rightX, y, 50, 28, "16:9");
            btnRatio916 = CreateTextButton("9:16", rightX + 56, y, 50, 28, "9:16");
            btnRatio11 = CreateTextButton("1:1", rightX + 112, y, 50, 28, "1:1");
            btnRatio43 = CreateTextButton("4:3", rightX + 168, y, 50, 28, "4:3");
            btnRatio169.Click += (s, e) => ApplyCropRatio(16.0 / 9.0);
            btnRatio916.Click += (s, e) => ApplyCropRatio(9.0 / 16.0);
            btnRatio11.Click += (s, e) => ApplyCropRatio(1.0);
            btnRatio43.Click += (s, e) => ApplyCropRatio(4.0 / 3.0);
            tabCrop.Controls.Add(btnRatio169);
            tabCrop.Controls.Add(btnRatio916);
            tabCrop.Controls.Add(btnRatio11);
            tabCrop.Controls.Add(btnRatio43);

            y += 44;
            var lblAspect = new Label();
            lblAspect.Text = "宽高比:";
            lblAspect.Location = new Point(rightX, y);
            lblAspect.Size = new Size(60, 20);
            tabCrop.Controls.Add(lblAspect);

            cmbAspect = new ComboBox();
            cmbAspect.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbAspect.Items.AddRange(new object[] { "保留原件", "16:9", "9:16", "1:1", "4:3" });
            cmbAspect.SelectedIndex = 0;
            cmbAspect.Location = new Point(rightX + 64, y);
            cmbAspect.Size = new Size(120, 24);
            cmbAspect.SelectedIndexChanged += (s, e) =>
            {
                switch (cmbAspect.SelectedIndex)
                {
                    case 1: ApplyCropRatio(16.0 / 9.0); break;
                    case 2: ApplyCropRatio(9.0 / 16.0); break;
                    case 3: ApplyCropRatio(1.0); break;
                    case 4: ApplyCropRatio(4.0 / 3.0); break;
                }
            };
            tabCrop.Controls.Add(cmbAspect);

            y += 50;
            y += 30;
            var btnApplyAll = CreateTextButton("应用全部", rightX, y, 80, 28, "应用当前裁剪到整段视频");
            btnApplyAll.Click += (s, e) =>
            {
                // Crop is already global for the task; refresh output preview.
                UpdateCropOutputPreview();
            };
            tabCrop.Controls.Add(btnApplyAll);

            y += 38;
            lblCropSize = new Label();
            lblCropSize.Location = new Point(rightX, y);
            lblCropSize.Size = new Size(220, 20);
            lblCropSize.Text = "裁剪区域: -";
            tabCrop.Controls.Add(lblCropSize);

            int outY = topY + previewH + 16;
            var lblOut = new Label();
            lblOut.Text = "输出预览";
            lblOut.Location = new Point(margin, outY);
            lblOut.Size = new Size(80, 20);
            tabCrop.Controls.Add(lblOut);

            lblCropTime = new Label();
            lblCropTime.Location = new Point(margin + 90, outY);
            lblCropTime.Size = new Size(220, 20);
            lblCropTime.Text = "00:00:00.000 / 00:00:00.000";
            tabCrop.Controls.Add(lblCropTime);

            picCropOutput = new PictureBox();
            picCropOutput.Location = new Point(margin, outY + 24);
            picCropOutput.Size = new Size(320, 180);
            picCropOutput.BackColor = Color.Black;
            picCropOutput.SizeMode = PictureBoxSizeMode.Zoom;
            tabCrop.Controls.Add(picCropOutput);

            btnCropPlay = CreateIconButton("▶", margin + 340, outY + 24, 60, 32, "播放/停止");
            btnCropPlay.Click += (s, e) => TogglePlay();
            tabCrop.Controls.Add(btnCropPlay);
        }

        #endregion

        #region Bottom Buttons

        private void BuildBottomButtons()
        {
            int y = this.ClientSize.Height - 50;
            btnOK = new Button();
            btnOK.Text = "确定";
            btnOK.Location = new Point(this.ClientSize.Width - 200, y);
            btnOK.Size = new Size(80, 32);
            btnOK.BackColor = Color.FromArgb(124, 77, 255);
            btnOK.ForeColor = Color.White;
            btnOK.FlatStyle = FlatStyle.Flat;
            btnOK.FlatAppearance.BorderSize = 0;
            btnOK.DialogResult = DialogResult.OK;
            btnOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOK.Click += BtnOK_Click;

            btnCancel = new Button();
            btnCancel.Text = "取消";
            btnCancel.Location = new Point(this.ClientSize.Width - 100, y);
            btnCancel.Size = new Size(80, 32);
            btnCancel.BackColor = Color.White;
            btnCancel.ForeColor = Color.FromArgb(80, 80, 80);
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);

            this.Resize += (s, e) =>
            {
                int by = this.ClientSize.Height - 50;
                btnOK.Location = new Point(this.ClientSize.Width - 200, by);
                btnCancel.Location = new Point(this.ClientSize.Width - 100, by);
            };
        }

        #endregion

        #region Lifecycle

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            _totalMs = Math.Max(1, (long)(SourceDurationSeconds * 1000));
            _frameIntervalMs = FrameRate > 0 ? Math.Max(1, (long)(1000.0 / FrameRate)) : 40;
            _currentTimeMs = 0;

            _segments = new List<VideoSegment>();
            if (Segments != null && Segments.Count > 0)
                foreach (var s in Segments) _segments.Add(s.Clone());
            else if (TrimStartSeconds > 0 || TrimEndSeconds > 0 && TrimEndSeconds < SourceDurationSeconds)
            {
                long start = Math.Max(0, (long)(TrimStartSeconds * 1000));
                long end = TrimEndSeconds > 0 ? Math.Min(_totalMs, (long)(TrimEndSeconds * 1000)) : _totalMs;
                _segments.Add(new VideoSegment { StartMs = start, EndMs = end });
            }
            else
            {
                _segments.Add(new VideoSegment { StartMs = 0, EndMs = _totalMs });
            }
            NormalizeSegments();
            PushUndo();

            _crop = Crop?.Clone() ?? new CropRegion { X = 0, Y = 0, Width = SourceWidth, Height = SourceHeight };
            _rotation = Rotation;
            chkMerge.Checked = MergeSegments;

            _playTimer = new Timer();
            _playTimer.Interval = (int)_frameIntervalMs;
            _playTimer.Tick += PlayTimer_Tick;

            lblOriginalSize.Text = $"原始大小: {SourceWidth} × {SourceHeight}";
            UpdateCropInputs();

            UpdateTimeLabel();
            UpdateTimelineSelectionFromTime();
            panelTimeline.Invalidate();

            try
            {
                var info = await FFmpegHelper.ProbeDetailedAsync(InputPath);
                if (info.FrameRate > 0) _frameIntervalMs = Math.Max(1, (long)(1000.0 / info.FrameRate));
                if (SourceWidth <= 0) SourceWidth = info.Width;
                if (SourceHeight <= 0) SourceHeight = info.Height;
                if (SourceDurationSeconds <= 0) SourceDurationSeconds = info.DurationSeconds;
                _totalMs = Math.Max(1, (long)(SourceDurationSeconds * 1000));
                if (_crop.Width == 0 || _crop.Height == 0)
                    _crop = new CropRegion { X = 0, Y = 0, Width = SourceWidth, Height = SourceHeight };
                UpdateCropInputs();

                _keyframes = await FFmpegHelper.GetKeyframesAsync(InputPath);
                await ExtractThumbnailsAsync();
                RefreshPreviewAsync();
                panelTimeline.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载视频信息失败: " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopPlay();
            _playTimer?.Dispose();
            foreach (var img in _thumbnails) img?.Dispose();
            _thumbnails.Clear();
            base.OnFormClosing(e);
        }

        #endregion

        #region Helpers

        private Button CreateIconButton(string text, int x, int y, int w, int h, string tooltip)
        {
            var btn = new Button();
            btn.Text = text;
            btn.Location = new Point(x, y);
            btn.Size = new Size(w, h);
            btn.FlatStyle = FlatStyle.Flat;
            btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btn.BackColor = Color.White;
            btn.Font = new Font("Microsoft YaHei UI", 9F);
            if (!string.IsNullOrEmpty(tooltip)) toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        private Button CreateTextButton(string text, int x, int y, int w, int h, string tooltip)
        {
            var btn = CreateIconButton(text, x, y, w, h, tooltip);
            return btn;
        }

        private TextBox CreateNumberInput(int x, int y, int w, int h)
        {
            var tb = new TextBox();
            tb.Location = new Point(x, y);
            tb.Size = new Size(w, h);
            tb.Text = "0";
            tb.TextAlign = HorizontalAlignment.Center;
            return tb;
        }

        private ToolTip toolTip = new ToolTip();

        private string FormatTime(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            return string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds);
        }

        private void UpdateTimeLabel()
        {
            lblTime.Text = $"{FormatTime(_currentTimeMs)} / {FormatTime(_totalMs)}";
            lblCropTime.Text = lblTime.Text;
        }

        #endregion

        #region Timeline Logic

        private void NormalizeSegments()
        {
            if (_segments.Count == 0)
                _segments.Add(new VideoSegment { StartMs = 0, EndMs = _totalMs });

            _segments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            for (int i = _segments.Count - 1; i >= 0; i--)
            {
                var s = _segments[i];
                if (s.EndMs <= s.StartMs) _segments.RemoveAt(i);
                else
                {
                    s.StartMs = Math.Max(0, Math.Min(_totalMs, s.StartMs));
                    s.EndMs = Math.Max(0, Math.Min(_totalMs, s.EndMs));
                }
            }
            if (_segments.Count == 0)
                _segments.Add(new VideoSegment { StartMs = 0, EndMs = _totalMs });
        }

        private void PushUndo()
        {
            var snap = _segments.Select(s => s.Clone()).ToList();
            // Remove redo entries if we branch from middle.
            if (_undoPos < _undoStack.Count - 1)
                _undoStack.RemoveRange(_undoPos + 1, _undoStack.Count - _undoPos - 1);
            _undoStack.Add(snap);
            _undoPos = _undoStack.Count - 1;
            UpdateUndoButtons();
        }

        private void Undo()
        {
            if (_undoPos > 0)
            {
                _undoPos--;
                _segments = _undoStack[_undoPos].Select(s => s.Clone()).ToList();
                panelTimeline.Invalidate();
            }
            UpdateUndoButtons();
        }

        private void Redo()
        {
            if (_undoPos < _undoStack.Count - 1)
            {
                _undoPos++;
                _segments = _undoStack[_undoPos].Select(s => s.Clone()).ToList();
                panelTimeline.Invalidate();
            }
            UpdateUndoButtons();
        }

        private void UpdateUndoButtons()
        {
            btnUndo.Enabled = _undoPos > 0;
            btnRedo.Enabled = _undoPos < _undoStack.Count - 1;
        }

        private void Timeline_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(panelTimeline.BackColor);

            int h = panelTimeline.Height;
            int top = 24;
            int thumbH = h - top - 24;
            int w = panelTimeline.Width;

            // Draw thumbnails.
            if (_thumbnails.Count > 0)
            {
                int thumbW = w / _thumbnails.Count;
                for (int i = 0; i < _thumbnails.Count; i++)
                {
                    var img = _thumbnails[i];
                    if (img != null)
                    {
                        int x = i * thumbW;
                        g.DrawImage(img, new Rectangle(x, top, thumbW, thumbH));
                        g.DrawRectangle(Pens.LightGray, x, top, thumbW - 1, thumbH - 1);
                    }
                }
            }
            else
            {
                using (var brush = new SolidBrush(Color.FromArgb(230, 230, 230)))
                    g.FillRectangle(brush, 0, top, w, thumbH);
            }

            // Draw segments overlay.
            float pxPerMs = w / (float)_totalMs;
            foreach (var seg in _segments)
            {
                int x = (int)(seg.StartMs * pxPerMs);
                int rw = Math.Max(2, (int)((seg.EndMs - seg.StartMs) * pxPerMs));
                using (var brush = new SolidBrush(seg.IsSelected ? Color.FromArgb(160, 124, 77, 255) : Color.FromArgb(100, 124, 77, 255)))
                    g.FillRectangle(brush, x, top, rw, thumbH);
                g.DrawRectangle(seg.IsSelected ? Pens.Red : Pens.Purple, x, top, rw - 1, thumbH - 1);
            }

            // Draw time ruler.
            using (var pen = new Pen(Color.Gray))
            {
                int marks = 6;
                for (int i = 0; i <= marks; i++)
                {
                    int x = (int)(i * w / (double)marks);
                    g.DrawLine(pen, x, top + thumbH, x, top + thumbH + 6);
                    string t = FormatTime((long)(i * _totalMs / (double)marks));
                    g.DrawString(t, this.Font, Brushes.Gray, x - 20, top + thumbH + 8);
                }
            }

            // Draw playhead.
            int hx = (int)(_currentTimeMs * pxPerMs);
            using (var pen = new Pen(Color.Red, 2))
            {
                g.DrawLine(pen, hx, top - 4, hx, top + thumbH + 4);
            }
            g.FillPolygon(Brushes.Red, new[] { new Point(hx, top - 8), new Point(hx - 5, top - 2), new Point(hx + 5, top - 2) });
        }

        private bool _timelineDragging;

        private void Timeline_MouseDown(object sender, MouseEventArgs e)
        {
            _timelineDragging = true;
            SetTimeFromTimelineX(e.X, e.Button == MouseButtons.Left);
        }

        private void Timeline_MouseMove(object sender, MouseEventArgs e)
        {
            if (_timelineDragging)
                SetTimeFromTimelineX(e.X, true);
        }

        private void Timeline_MouseUp(object sender, MouseEventArgs e)
        {
            _timelineDragging = false;
        }

        private void SetTimeFromTimelineX(int x, bool selectSegment)
        {
            float pxPerMs = panelTimeline.Width / (float)_totalMs;
            long ms = Math.Max(0, Math.Min(_totalMs, (long)(x / pxPerMs)));
            _currentTimeMs = ms;
            trackHead.Value = (int)(_currentTimeMs * 1000.0 / _totalMs);
            UpdateTimeLabel();
            panelTimeline.Invalidate();
            if (selectSegment)
            {
                int idx = SegmentIndexAt(ms);
                for (int i = 0; i < _segments.Count; i++) _segments[i].IsSelected = (i == idx);
                panelTimeline.Invalidate();
            }
            RefreshPreviewAsync();
        }

        private void TrackHead_ValueChanged(object sender, EventArgs e)
        {
            if (_timelineDragging) return;
            long ms = (long)(trackHead.Value * _totalMs / 1000.0);
            _currentTimeMs = Math.Min(_totalMs, ms);
            UpdateTimeLabel();
            panelTimeline.Invalidate();
            RefreshPreviewAsync();
        }

        private void UpdateTimelineSelectionFromTime()
        {
            int idx = SegmentIndexAt(_currentTimeMs);
            for (int i = 0; i < _segments.Count; i++) _segments[i].IsSelected = (i == idx);
        }

        private int SegmentIndexAt(long ms)
        {
            for (int i = 0; i < _segments.Count; i++)
                if (ms >= _segments[i].StartMs && ms < _segments[i].EndMs)
                    return i;
            return _segments.Count - 1;
        }

        private void BtnSplit_Click(object sender, EventArgs e)
        {
            int idx = SegmentIndexAt(_currentTimeMs);
            var seg = _segments[idx];
            if (_currentTimeMs <= seg.StartMs + 50 || _currentTimeMs >= seg.EndMs - 50)
                return;

            var left = new VideoSegment { StartMs = seg.StartMs, EndMs = _currentTimeMs };
            var right = new VideoSegment { StartMs = _currentTimeMs, EndMs = seg.EndMs };
            _segments.RemoveAt(idx);
            _segments.Insert(idx, right);
            _segments.Insert(idx, left);
            NormalizeSegments();
            PushUndo();
            panelTimeline.Invalidate();
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            var toRemove = _segments.Where(s => s.IsSelected).ToList();
            if (toRemove.Count == 0) return;
            foreach (var s in toRemove) _segments.Remove(s);
            NormalizeSegments();
            PushUndo();
            panelTimeline.Invalidate();
        }

        private void BtnUndo_Click(object sender, EventArgs e) => Undo();
        private void BtnRedo_Click(object sender, EventArgs e) => Redo();

        #endregion

        #region Playback

        private void TogglePlay()
        {
            if (_isPlaying) StopPlay();
            else StartPlay();
        }

        private void StartPlay()
        {
            _isPlaying = true;
            btnPlay.Text = "⏸";
            btnCropPlay.Text = "⏸";
            _playTimer.Start();
        }

        private void StopPlay()
        {
            _isPlaying = false;
            btnPlay.Text = "▶";
            btnCropPlay.Text = "▶";
            _playTimer?.Stop();
        }

        private void PlayTimer_Tick(object sender, EventArgs e)
        {
            _currentTimeMs += _frameIntervalMs;
            if (_currentTimeMs >= _totalMs)
            {
                _currentTimeMs = _totalMs;
                StopPlay();
            }
            trackHead.Value = (int)(_currentTimeMs * 1000.0 / _totalMs);
            UpdateTimeLabel();
            panelTimeline.Invalidate();
            UpdateTimelineSelectionFromTime();
            RefreshPreviewAsync();
        }

        private void StepFrame(int dir)
        {
            StopPlay();
            _currentTimeMs = Math.Max(0, Math.Min(_totalMs, _currentTimeMs + dir * _frameIntervalMs));
            trackHead.Value = (int)(_currentTimeMs * 1000.0 / _totalMs);
            UpdateTimeLabel();
            panelTimeline.Invalidate();
            UpdateTimelineSelectionFromTime();
            RefreshPreviewAsync();
        }

        private void SeekPrevKeyframe()
        {
            StopPlay();
            long t = _keyframes.LastOrDefault(k => k < _currentTimeMs);
            if (t == 0 && _keyframes.Count > 0 && _keyframes[0] < _currentTimeMs) t = _keyframes[0];
            if (t == 0 && _currentTimeMs > 0) t = 0;
            _currentTimeMs = t;
            trackHead.Value = (int)(_currentTimeMs * 1000.0 / _totalMs);
            UpdateTimeLabel();
            panelTimeline.Invalidate();
            UpdateTimelineSelectionFromTime();
            RefreshPreviewAsync();
        }

        private void SeekNextKeyframe()
        {
            StopPlay();
            long t = _keyframes.FirstOrDefault(k => k > _currentTimeMs);
            if (t == 0 && _keyframes.Count > 0 && _keyframes[_keyframes.Count - 1] > _currentTimeMs)
                t = _keyframes[_keyframes.Count - 1];
            if (t == 0) t = _totalMs;
            _currentTimeMs = Math.Min(_totalMs, t);
            trackHead.Value = (int)(_currentTimeMs * 1000.0 / _totalMs);
            UpdateTimeLabel();
            panelTimeline.Invalidate();
            UpdateTimelineSelectionFromTime();
            RefreshPreviewAsync();
        }

        private async void RefreshPreviewAsync()
        {
            try
            {
                var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, _currentTimeMs,
                    picPreview.Width, picPreview.Height);
                if (img != null)
                {
                    var old = picPreview.Image;
                    picPreview.Image = img;
                    old?.Dispose();
                    if (tabControl.SelectedTab == tabCrop)
                    {
                        var oldCrop = picCropPreview.Image;
                        picCropPreview.Image = new Bitmap(img);
                        oldCrop?.Dispose();
                        picCropPreview.Invalidate();
                    }
                }
            }
            catch { }
        }

        private async Task ExtractThumbnailsAsync()
        {
            int count = _thumbnailCount;
            var list = new List<Image>();
            for (int i = 0; i < count; i++)
            {
                long ms = (long)(i * _totalMs / (double)(count - 1));
                try
                {
                    var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, ms,
                        panelTimeline.Width / count, panelTimeline.Height - 48);
                    list.Add(img);
                }
                catch
                {
                    list.Add(null);
                }
            }
            foreach (var old in _thumbnails) old?.Dispose();
            _thumbnails = list;
        }

        #endregion

        #region Crop Logic

        private void ApplyRotation(int rotation)
        {
            if (rotation == 4 || rotation == 5)
            {
                _rotation = (_rotation == rotation) ? 0 : rotation;
            }
            else
            {
                int current = _rotation;
                if (current == 4 || current == 5) current = 0;
                int next = current + (rotation == 1 ? 1 : rotation == 2 ? -1 : 2);
                next = ((next % 4) + 4) % 4;
                _rotation = next;
            }
            picCropPreview.Invalidate();
            UpdateCropOutputPreview();
        }

        private void CenterCrop()
        {
            int cw = _crop.Width;
            int ch = _crop.Height;
            _crop.X = (SourceWidth - cw) / 2;
            _crop.Y = (SourceHeight - ch) / 2;
            UpdateCropInputs();
            picCropPreview.Invalidate();
            UpdateCropOutputPreview();
        }

        private void ApplyCropRatio(double ratio)
        {
            if (ratio <= 0) return;
            int h = Math.Min(SourceHeight, (int)(SourceWidth / ratio));
            int w = (int)(h * ratio);
            if (w > SourceWidth) { w = SourceWidth; h = (int)(w / ratio); }
            _crop.Width = w;
            _crop.Height = h;
            CenterCrop();
        }

        private void UpdateCropInputs()
        {
            txtCropX.Text = _crop.X.ToString();
            txtCropY.Text = _crop.Y.ToString();
            txtCropW.Text = _crop.Width.ToString();
            txtCropH.Text = _crop.Height.ToString();
            lblCropSize.Text = $"裁剪区域: {_crop.X},{_crop.Y} {_crop.Width}×{_crop.Height}";
        }

        private void CropInput_TextChanged(object sender, EventArgs e)
        {
            int x, y, w, h;
            if (int.TryParse(txtCropX.Text, out x) && int.TryParse(txtCropY.Text, out y) &&
                int.TryParse(txtCropW.Text, out w) && int.TryParse(txtCropH.Text, out h))
            {
                _crop.X = Math.Max(0, Math.Min(SourceWidth, x));
                _crop.Y = Math.Max(0, Math.Min(SourceHeight, y));
                _crop.Width = Math.Max(1, Math.Min(SourceWidth - _crop.X, w));
                _crop.Height = Math.Max(1, Math.Min(SourceHeight - _crop.Y, h));
                picCropPreview.Invalidate();
                UpdateCropOutputPreview();
                lblCropSize.Text = $"裁剪区域: {_crop.X},{_crop.Y} {_crop.Width}×{_crop.Height}";
            }
        }

        private void PicCropPreview_Paint(object sender, PaintEventArgs e)
        {
            if (picCropPreview.Image == null) return;
            var g = e.Graphics;
            _cropRect = GetCropRectInPictureBox();
            using (var pen = new Pen(Color.FromArgb(124, 77, 255), 2))
            {
                g.DrawRectangle(pen, _cropRect);
                // Draw handles.
                int hsize = 8;
                DrawHandle(g, _cropRect.Left, _cropRect.Top, hsize);
                DrawHandle(g, _cropRect.Right, _cropRect.Top, hsize);
                DrawHandle(g, _cropRect.Left, _cropRect.Bottom, hsize);
                DrawHandle(g, _cropRect.Right, _cropRect.Bottom, hsize);
            }
            // Dim outside.
            var path = new GraphicsPath();
            path.AddRectangle(new Rectangle(0, 0, picCropPreview.Width, picCropPreview.Height));
            path.AddRectangle(_cropRect);
            using (var brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            {
                g.FillPath(brush, path);
            }
        }

        private void DrawHandle(Graphics g, int x, int y, int size)
        {
            g.FillRectangle(Brushes.White, x - size / 2, y - size / 2, size, size);
            g.DrawRectangle(Pens.Purple, x - size / 2, y - size / 2, size, size);
        }

        private Rectangle GetCropRectInPictureBox()
        {
            if (picCropPreview.Image == null) return Rectangle.Empty;
            var imgSize = picCropPreview.Image.Size;
            var boxSize = picCropPreview.ClientSize;
            double scaleX = boxSize.Width / (double)imgSize.Width;
            double scaleY = boxSize.Height / (double)imgSize.Height;
            double scale = Math.Min(scaleX, scaleY);
            int drawW = (int)(imgSize.Width * scale);
            int drawH = (int)(imgSize.Height * scale);
            int offX = (boxSize.Width - drawW) / 2;
            int offY = (boxSize.Height - drawH) / 2;

            int x = offX + (int)(_crop.X * scale);
            int y = offY + (int)(_crop.Y * scale);
            int w = (int)(_crop.Width * scale);
            int h = (int)(_crop.Height * scale);
            return new Rectangle(x, y, w, h);
        }

        private void PicCropPreview_MouseDown(object sender, MouseEventArgs e)
        {
            _cropRect = GetCropRectInPictureBox();
            if (_cropRect.Contains(e.Location))
            {
                _draggingCrop = true;
                _dragStart = e.Location;
            }
        }

        private void PicCropPreview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_draggingCrop) return;
            int dx = e.X - _dragStart.X;
            int dy = e.Y - _dragStart.Y;
            _dragStart = e.Location;

            if (picCropPreview.Image == null) return;
            var imgSize = picCropPreview.Image.Size;
            var boxSize = picCropPreview.ClientSize;
            double scaleX = boxSize.Width / (double)imgSize.Width;
            double scaleY = boxSize.Height / (double)imgSize.Height;
            double scale = Math.Min(scaleX, scaleY);

            _crop.X = Math.Max(0, Math.Min(SourceWidth - _crop.Width, _crop.X + (int)(dx / scale)));
            _crop.Y = Math.Max(0, Math.Min(SourceHeight - _crop.Height, _crop.Y + (int)(dy / scale)));
            UpdateCropInputs();
            picCropPreview.Invalidate();
            UpdateCropOutputPreview();
        }

        private void PicCropPreview_MouseUp(object sender, MouseEventArgs e)
        {
            _draggingCrop = false;
        }

        private async void UpdateCropOutputPreview()
        {
            try
            {
                var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, _currentTimeMs,
                    picCropOutput.Width * 2, picCropOutput.Height * 2);
                if (img == null) return;
                using (img)
                {
                    var cropped = CropAndRotateBitmap((Bitmap)img, _crop, _rotation);
                    var old = picCropOutput.Image;
                    picCropOutput.Image = cropped;
                    old?.Dispose();
                }
            }
            catch { }
        }

        private Bitmap CropAndRotateBitmap(Bitmap src, CropRegion crop, int rotation)
        {
            var rect = new Rectangle(crop.X, crop.Y, crop.Width, crop.Height);
            rect.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            if (rect.Width <= 0 || rect.Height <= 0) return new Bitmap(src);

            Bitmap cropped;
            try { cropped = src.Clone(rect, System.Drawing.Imaging.PixelFormat.Format24bppRgb); }
            catch { cropped = new Bitmap(src); }

            switch (rotation)
            {
                case 1: cropped.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                case 2: cropped.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                case 3: cropped.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                case 4: cropped.RotateFlip(RotateFlipType.RotateNoneFlipX); break;
                case 5: cropped.RotateFlip(RotateFlipType.RotateNoneFlipY); break;
            }
            return cropped;
        }

        #endregion

        private async void TabControl_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (tabControl.SelectedTab == tabCrop)
            {
                // Refresh crop source preview from current frame.
                RefreshPreviewAsync();
                await Task.Delay(200);
                UpdateCropOutputPreview();
            }
        }

        #region OK / Cancel

        private void BtnOK_Click(object sender, EventArgs e)
        {
            NormalizeSegments();
            // Remove transient selection flag.
            foreach (var s in _segments) s.IsSelected = false;
            Segments = _segments;
            Crop = _crop;
            Rotation = _rotation;
            MergeSegments = chkMerge.Checked;

            // Keep old TrimStart/TrimEnd in sync for code that hasn't migrated.
            if (Segments != null && Segments.Count > 0)
            {
                TrimStartSeconds = Segments[0].StartMs / 1000.0;
                TrimEndSeconds = Segments[Segments.Count - 1].EndMs / 1000.0;
            }
        }

        #endregion

        // Backward-compatible I/O used by legacy callers.
        public double TrimStartSeconds { get; set; }
        public double TrimEndSeconds { get; set; }
    }
}
