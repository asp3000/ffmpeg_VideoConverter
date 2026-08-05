// ============================================================================
//  VideoEditForm.cs — visual trim + crop + effects + subtitle editor.
//  Tabs: 剪切 (split/merge timeline) | 裁剪 (crop/rotate) | 效果 | 字幕.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
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

        // 效果参数
        public double Speed { get; set; } = 1.0;
        public double Brightness { get; set; } = 0.0;
        public double Contrast { get; set; } = 1.0;
        public double Saturation { get; set; } = 1.0;
        public string WatermarkPath { get; set; }
        public int WatermarkPosition { get; set; } = 3;
        public double WatermarkOpacity { get; set; } = 0.8;
        public double WatermarkScalePercent { get; set; } = 0.0;

        // 字幕参数
        public SubtitleSettings SubSettings { get; set; } = new SubtitleSettings();
        public List<SubtitleTrackInfo> SubTracks { get; set; } = new List<SubtitleTrackInfo>();
        public string DefaultExternalSubPath { get; set; }

        public int StartTabIndex { get; set; } = 0;

        // 兼容旧调用方的 I/O
        public double TrimStartSeconds { get; set; }
        public double TrimEndSeconds { get; set; }

        public bool ApplyToAll { get; private set; }

        // ---- working state ----------------------------------------------------
        private List<VideoSegment> _segments;
        private List<List<VideoSegment>> _undoStack = new List<List<VideoSegment>>();
        private int _undoPos = -1;

        private long _currentTimeMs;
        private bool _isPlaying;
        private long _totalMs;
        private long _frameIntervalMs;

        private List<long> _keyframes = new List<long>();
        private List<Image> _thumbnails = new List<Image>();
        private int _thumbnailCount = 20;

        private CropRegion _crop;
        private int _rotation;

        private CancellationTokenSource _playCts;
        private bool _seeking;

        // ---- controls ---------------------------------------------------------
        private TabControl tabControl;
        private TabPage tabTrim;
        private TabPage tabCrop;
        private TabPage tabEffects;
        private TabPage tabSubtitle;

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
        private Button btnApplyAllGlobal;

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
        private TrackBar trackCropHead;

        // effects tab
        private PictureBox picEffectPreview;
        private TrackBar trkSpeed;
        private Label lblSpeedVal;
        private TrackBar trkBrightness;
        private Label lblBrightVal;
        private TrackBar trkContrast;
        private Label lblContrastVal;
        private TrackBar trkSaturation;
        private Label lblSatVal;
        private TextBox txtWatermark;
        private Button btnWatermarkBrowse;
        private TrackBar trkWatermarkOpacity;
        private Label lblWmOpacityVal;
        private TrackBar trkWatermarkScale;
        private Label lblWmScaleVal;
        private TrackBar trackEffectHead;
        private Button btnEffectPlay;

        // subtitle tab
        private ComboBox cmbSubTrack;
        private TextBox txtExternalSub;
        private Button btnBrowseSub;
        private ComboBox cmbFontName;
        private NumericUpDown numFontSize;
        private Button btnFontColor;
        private Panel pnlFontColor;
        private CheckBox chkBold;
        private CheckBox chkItalic;
        private CheckBox chkUnderline;
        private NumericUpDown numOutlineW;
        private Button btnOutlineColor;
        private Panel pnlOutlineColor;
        private NumericUpDown numTransparency;
        private CheckBox chkBackEnabled;
        private Button btnBackColor;
        private Panel pnlBackColor;
        private NumericUpDown numBackAlpha;
        private ComboBox cmbAlignment;
        private NumericUpDown numMarginV;
        private PictureBox picSubPreview;

        private ToolTip toolTip = new ToolTip();

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

            tabTrim = new TabPage("剪切") { BackColor = Color.White, AutoScroll = true };
            tabCrop = new TabPage("裁剪") { BackColor = Color.White, AutoScroll = true };
            tabEffects = new TabPage("效果") { BackColor = Color.White, AutoScroll = true };
            tabSubtitle = new TabPage("字幕") { BackColor = Color.White, AutoScroll = true };
            tabControl.TabPages.Add(tabTrim);
            tabControl.TabPages.Add(tabCrop);
            tabControl.TabPages.Add(tabEffects);
            tabControl.TabPages.Add(tabSubtitle);

            tabControl.SelectedIndexChanged += TabControl_SelectedIndexChanged;
            this.Controls.Add(tabControl);

            BuildTrimTab();
            BuildCropTab();
            BuildEffectsTab();
            BuildSubtitleTab();
            BuildBottomButtons();
        }

        #region Trim Tab

        private void BuildTrimTab()
        {
            int margin = 16;
            int topY = 16;
            int previewH = 360;

            picPreview = new PictureBox
            {
                Location = new Point(margin, topY),
                Size = new Size(tabTrim.ClientSize.Width - margin * 2, previewH),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
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

            lblTime = new Label
            {
                Location = new Point(tabTrim.ClientSize.Width - margin - 200, ctrlY + 4),
                Size = new Size(200, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
                Text = "00:00:00.000 / 00:00:00.000"
            };
            tabTrim.Controls.Add(lblTime);

            int timelineY = ctrlY + btnH + 16;
            panelTimeline = new Panel
            {
                Location = new Point(margin, timelineY),
                Size = new Size(tabTrim.ClientSize.Width - margin * 2, 110),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Color.FromArgb(245, 245, 245)
            };
            panelTimeline.Paint += Timeline_Paint;
            panelTimeline.MouseDown += Timeline_MouseDown;
            panelTimeline.MouseMove += Timeline_MouseMove;
            panelTimeline.MouseUp += Timeline_MouseUp;
            tabTrim.Controls.Add(panelTimeline);

            trackHead = new TrackBar
            {
                Location = new Point(margin, timelineY - 10),
                Size = new Size(panelTimeline.Width, 30),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None
            };
            trackHead.ValueChanged += (s, e) => TrackSeek(trackHead);
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

            chkMerge = new CheckBox
            {
                Text = "合并到一个文件",
                Location = new Point(tabTrim.ClientSize.Width - margin - 140, toolY + 6),
                Size = new Size(140, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Checked = true
            };
            tabTrim.Controls.Add(chkMerge);

            // 剪切页签底部：应用 / 应用到全部
            int applyY = toolY + 56;
            tabTrim.Controls.Add(MakeApplyButton("应用", margin, applyY, 90, 32, false));
            tabTrim.Controls.Add(MakeApplyButton("应用到全部", margin + 100, applyY, 110, 32, true));

            tabTrim.Resize += (s, e) =>
            {
                picPreview.Width = tabTrim.ClientSize.Width - margin * 2;
                panelTimeline.Width = tabTrim.ClientSize.Width - margin * 2;
                trackHead.Width = panelTimeline.Width;
                lblTime.Left = tabTrim.ClientSize.Width - margin - 200;
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
            int previewW = 560;
            int previewH = 400;

            picCropPreview = new PictureBox
            {
                Location = new Point(margin, topY),
                Size = new Size(previewW, previewH),
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            picCropPreview.Paint += PicCropPreview_Paint;
            picCropPreview.MouseDown += PicCropPreview_MouseDown;
            picCropPreview.MouseMove += PicCropPreview_MouseMove;
            picCropPreview.MouseUp += PicCropPreview_MouseUp;
            tabCrop.Controls.Add(picCropPreview);

            int rightX = margin + previewW + 20;
            int y = topY;

            var lblRot = new Label { Text = "旋转/翻转:", Location = new Point(rightX, y), Size = new Size(100, 20) };
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
            lblOriginalSize = new Label { Location = new Point(rightX, y), Size = new Size(200, 20), Text = "原始大小: -" };
            tabCrop.Controls.Add(lblOriginalSize);

            y += 28;
            var lblCropSizeTitle = new Label { Text = "裁剪区域大小:", Location = new Point(rightX, y), Size = new Size(100, 20) };
            tabCrop.Controls.Add(lblCropSizeTitle);

            y += 24;
            txtCropW = CreateNumberInput(rightX, y, 60, 24);
            var lblX = new Label { Text = "×", Location = new Point(rightX + 66, y + 2), Size = new Size(12, 20) };
            txtCropH = CreateNumberInput(rightX + 82, y, 60, 24);
            var lblAt = new Label { Text = "@", Location = new Point(rightX + 148, y + 2), Size = new Size(12, 20) };
            txtCropX = CreateNumberInput(rightX + 164, y, 60, 24);
            var lblComma = new Label { Text = ",", Location = new Point(rightX + 228, y + 2), Size = new Size(8, 20) };
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
            var lblRatio = new Label { Text = "固定比例:", Location = new Point(rightX, y), Size = new Size(80, 20) };
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
            var lblAspect = new Label { Text = "宽高比:", Location = new Point(rightX, y), Size = new Size(60, 20) };
            tabCrop.Controls.Add(lblAspect);

            cmbAspect = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(rightX + 64, y),
                Size = new Size(120, 24)
            };
            cmbAspect.Items.AddRange(new object[] { "保留原件", "16:9", "9:16", "1:1", "4:3" });
            cmbAspect.SelectedIndex = 0;
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

            // ---- 播放进度条 + 播放控制 ----
            y += 50;
            trackCropHead = new TrackBar
            {
                Location = new Point(rightX, y),
                Size = new Size(280, 30),
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None
            };
            trackCropHead.ValueChanged += (s, e) => TrackSeek(trackCropHead);
            tabCrop.Controls.Add(trackCropHead);

            y += 36;
            btnCropPlay = CreateIconButton("▶", rightX, y, 60, 32, "播放/停止");
            btnCropPlay.Click += (s, e) => TogglePlay();
            tabCrop.Controls.Add(btnCropPlay);

            y += 44;
            tabCrop.Controls.Add(MakeApplyButton("应用", rightX, y, 90, 32, false));
            tabCrop.Controls.Add(MakeApplyButton("应用到全部", rightX + 100, y, 110, 32, true));

            y += 44;
            lblCropSize = new Label { Location = new Point(rightX, y), Size = new Size(280, 20), Text = "裁剪区域: -" };
            tabCrop.Controls.Add(lblCropSize);

            // ---- 输出预览 ----
            int outY = topY + previewH + 16;
            var lblOut = new Label { Text = "输出预览", Location = new Point(margin, outY), Size = new Size(120, 20) };
            tabCrop.Controls.Add(lblOut);

            lblCropTime = new Label { Location = new Point(margin + 90, outY), Size = new Size(240, 20), Text = "00:00:00.000 / 00:00:00.000" };
            tabCrop.Controls.Add(lblCropTime);

            picCropOutput = new PictureBox
            {
                Location = new Point(margin, outY + 24),
                Size = new Size(320, 180),
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            tabCrop.Controls.Add(picCropOutput);
        }

        #endregion

        #region Effects Tab

        private void BuildEffectsTab()
        {
            int margin = 16;
            int y = 16;

            picEffectPreview = new PictureBox
            {
                Location = new Point(margin, y),
                Size = new Size(520, 380),
                BackColor = Color.Black,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            tabEffects.Controls.Add(picEffectPreview);

            // 进度条 + 播放控制（视频播放）
            int playY = y + 380 + 12;
            trackEffectHead = new TrackBar
            {
                Location = new Point(margin, playY),
                Size = new Size(400, 30),
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None
            };
            trackEffectHead.ValueChanged += (s, e) => TrackSeek(trackEffectHead);
            tabEffects.Controls.Add(trackEffectHead);

            btnEffectPlay = CreateIconButton("▶", margin + 410, playY, 60, 32, "播放/停止");
            btnEffectPlay.Click += (s, e) => TogglePlay();
            tabEffects.Controls.Add(btnEffectPlay);

            // 效果页签底部：应用 / 应用到全部
            tabEffects.Controls.Add(MakeApplyButton("应用", margin, playY + 48, 90, 32, false));
            tabEffects.Controls.Add(MakeApplyButton("应用到全部", margin + 100, playY + 48, 110, 32, true));

            int rightX = margin + 560;
            int rightW = 420;
            int valW = 70;

            var lblSpeed = new Label { Text = "播放速度:", Location = new Point(rightX, y), Size = new Size(rightW, 20) };
            tabEffects.Controls.Add(lblSpeed);
            y += 22;
            trkSpeed = new TrackBar { Location = new Point(rightX, y), Size = new Size(rightW - valW, 40), Minimum = 25, Maximum = 400, Value = (int)(Speed * 100), TickStyle = TickStyle.None };
            lblSpeedVal = new Label { Location = new Point(rightX + rightW - valW, y + 8), Size = new Size(valW, 20), TextAlign = ContentAlignment.MiddleRight };
            trkSpeed.ValueChanged += (s, e) => { Speed = trkSpeed.Value / 100.0; lblSpeedVal.Text = Speed.ToString("F2") + "x"; RefreshEffectPreview(); };
            tabEffects.Controls.Add(trkSpeed);
            tabEffects.Controls.Add(lblSpeedVal);
            y += 48;

            var lblBright = new Label { Text = "亮度:", Location = new Point(rightX, y), Size = new Size(rightW, 20) };
            tabEffects.Controls.Add(lblBright);
            y += 22;
            trkBrightness = new TrackBar { Location = new Point(rightX, y), Size = new Size(rightW - valW, 40), Minimum = -100, Maximum = 100, Value = (int)Brightness, TickStyle = TickStyle.None };
            lblBrightVal = new Label { Location = new Point(rightX + rightW - valW, y + 8), Size = new Size(valW, 20), TextAlign = ContentAlignment.MiddleRight };
            trkBrightness.ValueChanged += (s, e) => { Brightness = trkBrightness.Value; lblBrightVal.Text = Brightness.ToString(); RefreshEffectPreview(); };
            tabEffects.Controls.Add(trkBrightness);
            tabEffects.Controls.Add(lblBrightVal);
            y += 48;

            var lblContrast = new Label { Text = "对比度:", Location = new Point(rightX, y), Size = new Size(rightW, 20) };
            tabEffects.Controls.Add(lblContrast);
            y += 22;
            trkContrast = new TrackBar { Location = new Point(rightX, y), Size = new Size(rightW - valW, 40), Minimum = 0, Maximum = 200, Value = (int)(Contrast * 100), TickStyle = TickStyle.None };
            lblContrastVal = new Label { Location = new Point(rightX + rightW - valW, y + 8), Size = new Size(valW, 20), TextAlign = ContentAlignment.MiddleRight };
            trkContrast.ValueChanged += (s, e) => { Contrast = trkContrast.Value / 100.0; lblContrastVal.Text = Contrast.ToString("F2"); RefreshEffectPreview(); };
            tabEffects.Controls.Add(trkContrast);
            tabEffects.Controls.Add(lblContrastVal);
            y += 48;

            var lblSat = new Label { Text = "饱和度:", Location = new Point(rightX, y), Size = new Size(rightW, 20) };
            tabEffects.Controls.Add(lblSat);
            y += 22;
            trkSaturation = new TrackBar { Location = new Point(rightX, y), Size = new Size(rightW - valW, 40), Minimum = 0, Maximum = 200, Value = (int)(Saturation * 100), TickStyle = TickStyle.None };
            lblSatVal = new Label { Location = new Point(rightX + rightW - valW, y + 8), Size = new Size(valW, 20), TextAlign = ContentAlignment.MiddleRight };
            trkSaturation.ValueChanged += (s, e) => { Saturation = trkSaturation.Value / 100.0; lblSatVal.Text = Saturation.ToString("F2"); RefreshEffectPreview(); };
            tabEffects.Controls.Add(trkSaturation);
            tabEffects.Controls.Add(lblSatVal);
            y += 60;

            var grpWm = new GroupBox { Text = "水印", Location = new Point(rightX, y), Size = new Size(rightW, 195) };
            tabEffects.Controls.Add(grpWm);
            int wy = 20;

            var lblWmFile = new Label { Text = "文件:", Location = new Point(10, wy), Size = new Size(35, 20) };
            grpWm.Controls.Add(lblWmFile);
            txtWatermark = new TextBox { Location = new Point(48, wy), Size = new Size(140, 23), Text = WatermarkPath ?? "" };
            grpWm.Controls.Add(txtWatermark);
            btnWatermarkBrowse = new Button { Text = "浏览...", Location = new Point(192, wy), Size = new Size(60, 23) };
            btnWatermarkBrowse.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif" })
                { if (dlg.ShowDialog() == DialogResult.OK) { txtWatermark.Text = dlg.FileName; WatermarkPath = dlg.FileName; RefreshEffectPreview(); } }
            };
            grpWm.Controls.Add(btnWatermarkBrowse);
            wy += 28;

            var lblWmPos = new Label { Text = "位置:", Location = new Point(10, wy), Size = new Size(35, 40) };
            grpWm.Controls.Add(lblWmPos);
            string[] posLabels = { "左上", "中上", "右上", "中左", "中中", "中右", "左下", "中下", "右下" };
            for (int p = 0; p < 9; p++)
            {
                int col = p % 3, row = p / 3;
                var btn = new Button
                {
                    Location = new Point(48 + col * 50, wy + row * 24),
                    Size = new Size(46, 22),
                    Text = posLabels[p],
                    Tag = p,
                    Font = new Font("Microsoft YaHei UI", 7.5F),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = WatermarkPosition == p ? Color.FromArgb(124, 77, 255) : Color.White,
                    ForeColor = WatermarkPosition == p ? Color.White : Color.FromArgb(45, 45, 45)
                };
                btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                btn.Click += (s, ev) =>
                {
                    WatermarkPosition = (int)((Button)s).Tag;
                    foreach (Control c in grpWm.Controls)
                    {
                        if (c is Button b && b.Tag is int tg && tg >= 0 && tg <= 8)
                        {
                            b.BackColor = tg == WatermarkPosition ? Color.FromArgb(124, 77, 255) : Color.White;
                            b.ForeColor = tg == WatermarkPosition ? Color.White : Color.FromArgb(45, 45, 45);
                        }
                    }
                    RefreshEffectPreview();
                };
                grpWm.Controls.Add(btn);
            }
            wy += 72;

            var lblWmOpacity = new Label { Text = "不透明度:", Location = new Point(10, wy), Size = new Size(55, 20) };
            grpWm.Controls.Add(lblWmOpacity);
            trkWatermarkOpacity = new TrackBar { Location = new Point(68, wy), Size = new Size(130, 30), Minimum = 0, Maximum = 100, Value = (int)(WatermarkOpacity * 100), TickStyle = TickStyle.None };
            lblWmOpacityVal = new Label { Text = ((int)(WatermarkOpacity * 100)) + "%", Location = new Point(202, wy + 2), Size = new Size(45, 20) };
            trkWatermarkOpacity.ValueChanged += (s, e) => { WatermarkOpacity = trkWatermarkOpacity.Value / 100.0; lblWmOpacityVal.Text = trkWatermarkOpacity.Value + "%"; RefreshEffectPreview(); };
            grpWm.Controls.Add(trkWatermarkOpacity);
            grpWm.Controls.Add(lblWmOpacityVal);
            wy += 36;

            var lblWmScale = new Label { Text = "缩放:", Location = new Point(10, wy), Size = new Size(55, 20) };
            grpWm.Controls.Add(lblWmScale);
            trkWatermarkScale = new TrackBar { Location = new Point(68, wy), Size = new Size(130, 30), Minimum = 0, Maximum = 100, Value = (int)WatermarkScalePercent, TickStyle = TickStyle.None };
            lblWmScaleVal = new Label { Text = WatermarkScalePercent == 0 ? "自动" : WatermarkScalePercent + "%", Location = new Point(202, wy + 2), Size = new Size(45, 20) };
            trkWatermarkScale.ValueChanged += (s, e) => { WatermarkScalePercent = trkWatermarkScale.Value; lblWmScaleVal.Text = WatermarkScalePercent == 0 ? "自动" : WatermarkScalePercent + "%"; RefreshEffectPreview(); };
            grpWm.Controls.Add(trkWatermarkScale);
            grpWm.Controls.Add(lblWmScaleVal);
        }

        /// <summary>使用 ColorMatrix 实时调整预览图像（按当前播放头时间取帧）。</summary>
        private async void RefreshEffectPreview()
        {
            if (picEffectPreview == null || string.IsNullOrEmpty(InputPath)) return;
            try
            {
                int fw = SourceWidth > 0 ? SourceWidth : 1280;
                int fh = SourceHeight > 0 ? SourceHeight : 720;
                var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, _currentTimeMs, fw, fh);
                if (img == null) return;
                var result = ApplyColorMatrix(new Bitmap(img));
                img.Dispose();
                SwapImage(picEffectPreview, result);
            }
            catch { }
        }

        private Bitmap ApplyColorMatrix(Bitmap src)
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            float b = (float)Brightness / 255f;
            float c = (float)Contrast;
            float t = (1f - c) * 0.5f;
            float s = (float)Saturation;

            const float LumR = 0.2126f, LumG = 0.7152f, LumB = 0.0722f;
            float satR = (1f - s) * LumR + s;
            float satG = (1f - s) * LumG;
            float satB = (1f - s) * LumB;

            var cm = new ColorMatrix(new float[][] {
                new float[] { c * satR,       c * satG,        c * satB,        0, 0 },
                new float[] { c * (1f - s) * LumR, c * satR,   c * satB,        0, 0 },
                new float[] { c * (1f - s) * LumR, c * satG,   c * satR,        0, 0 },
                new float[] { 0,                   0,            0,               1, 0 },
                new float[] { t + b + satG * t,  t + b + satB * t, t + b,       0, 1 }
            });

            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    g.DrawImage(src, new Rectangle(0, 0, bmp.Width, bmp.Height),
                        0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                }
            }

            string wmPath = txtWatermark?.Text ?? WatermarkPath;
            if (!string.IsNullOrEmpty(wmPath) && File.Exists(wmPath))
                DrawWatermark(bmp, wmPath);

            return bmp;
        }

        private void DrawWatermark(Bitmap bmp, string wmPath)
        {
            try
            {
                using (var wmImg = Image.FromFile(wmPath))
                {
                    float scale;
                    if (WatermarkScalePercent <= 0)
                        scale = Math.Min(1f, Math.Min(bmp.Width / (float)wmImg.Width, bmp.Height / (float)wmImg.Height) * 0.2f);
                    else
                        scale = (float)WatermarkScalePercent / 100f;

                    int w = (int)(wmImg.Width * scale);
                    int h = (int)(wmImg.Height * scale);
                    if (w <= 0 || h <= 0) return;

                    int col = WatermarkPosition % 3;
                    int row = WatermarkPosition / 3;
                    int x = col == 0 ? 10 : (col == 1 ? (bmp.Width - w) / 2 : bmp.Width - w - 10);
                    int y = row == 0 ? 10 : (row == 1 ? (bmp.Height - h) / 2 : bmp.Height - h - 10);

                    var cmWm = new ColorMatrix { Matrix33 = (float)WatermarkOpacity };
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(cmWm);
                        using (var g = Graphics.FromImage(bmp))
                            g.DrawImage(wmImg, new Rectangle(x, y, w, h), 0, 0, wmImg.Width, wmImg.Height, GraphicsUnit.Pixel, ia);
                    }
                }
            }
            catch { }
        }

        #endregion

        #region Subtitle Tab

        private void BuildSubtitleTab()
        {
            int margin = 16;
            int y = 16;

            // ---- 左列（控件）----
            var lblTrack = new Label { Text = "字幕轨道:", Location = new Point(margin, y), Size = new Size(80, 20) };
            tabSubtitle.Controls.Add(lblTrack);
            cmbSubTrack = new ComboBox { Location = new Point(margin + 85, y), Size = new Size(200, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbSubTrack.SelectedIndexChanged += (s, e) =>
            {
                if (cmbSubTrack.SelectedItem is SubtitleTrackInfo info && info.IsExternal)
                    txtExternalSub.Text = info.FilePath;
            };
            tabSubtitle.Controls.Add(cmbSubTrack);
            y += 32;

            var lblExt = new Label { Text = "外挂字幕:", Location = new Point(margin, y), Size = new Size(80, 20) };
            tabSubtitle.Controls.Add(lblExt);
            // 宽度收窄，避免与右侧预览图重叠
            txtExternalSub = new TextBox { Location = new Point(margin + 85, y), Size = new Size(250, 23) };
            tabSubtitle.Controls.Add(txtExternalSub);
            btnBrowseSub = new Button { Text = "浏览...", Location = new Point(margin + 345, y), Size = new Size(60, 23) };
            btnBrowseSub.Click += (s, e) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "字幕文件|*.srt;*.ass;*.ssa;*.vtt;*.sub" })
                {
                    if (dlg.ShowDialog() == DialogResult.OK) txtExternalSub.Text = dlg.FileName;
                }
            };
            tabSubtitle.Controls.Add(btnBrowseSub);
            y += 40;

            var grpFont = new GroupBox { Text = "字体设置", Location = new Point(margin, y), Size = new Size(360, 200) };
            tabSubtitle.Controls.Add(grpFont);
            int fy = 22;

            var lblFontName = new Label { Text = "字体:", Location = new Point(10, fy), Size = new Size(50, 20) };
            grpFont.Controls.Add(lblFontName);
            cmbFontName = new ComboBox { Location = new Point(65, fy), Size = new Size(150, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbFontName.Items.AddRange(new object[] { "Arial", "Microsoft YaHei", "SimSun", "SimHei", "KaiTi", "FangSong", "Source Han Sans CN", "Source Han Serif CN", "Noto Sans CJK SC", "Noto Serif CJK SC" });
            cmbFontName.SelectedItem = "Arial";
            grpFont.Controls.Add(cmbFontName);
            fy += 30;

            var lblFontSize = new Label { Text = "字号:", Location = new Point(10, fy), Size = new Size(50, 20) };
            grpFont.Controls.Add(lblFontSize);
            numFontSize = new NumericUpDown { Location = new Point(65, fy), Size = new Size(60, 23), Minimum = 8, Maximum = 72, Value = 24 };
            grpFont.Controls.Add(numFontSize);

            var lblColor = new Label { Text = "颜色:", Location = new Point(140, fy), Size = new Size(50, 20) };
            grpFont.Controls.Add(lblColor);
            pnlFontColor = new Panel { Location = new Point(195, fy), Size = new Size(30, 23), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle };
            grpFont.Controls.Add(pnlFontColor);
            btnFontColor = new Button { Text = "选择...", Location = new Point(230, fy), Size = new Size(50, 23) };
            btnFontColor.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = pnlFontColor.BackColor })
                    if (dlg.ShowDialog() == DialogResult.OK) pnlFontColor.BackColor = dlg.Color;
            };
            grpFont.Controls.Add(btnFontColor);
            fy += 32;

            chkBold = new CheckBox { Text = "粗体", Location = new Point(10, fy), Size = new Size(55, 20) };
            grpFont.Controls.Add(chkBold);
            chkItalic = new CheckBox { Text = "斜体", Location = new Point(70, fy), Size = new Size(55, 20) };
            grpFont.Controls.Add(chkItalic);
            chkUnderline = new CheckBox { Text = "下划线", Location = new Point(130, fy), Size = new Size(60, 20) };
            grpFont.Controls.Add(chkUnderline);
            fy += 30;

            var lblOutline = new Label { Text = "描边宽度:", Location = new Point(10, fy), Size = new Size(65, 20) };
            grpFont.Controls.Add(lblOutline);
            numOutlineW = new NumericUpDown { Location = new Point(80, fy), Size = new Size(50, 23), Minimum = 0, Maximum = 10, Value = 1 };
            grpFont.Controls.Add(numOutlineW);

            var lblOLColor = new Label { Text = "描边色:", Location = new Point(140, fy), Size = new Size(50, 20) };
            grpFont.Controls.Add(lblOLColor);
            pnlOutlineColor = new Panel { Location = new Point(195, fy), Size = new Size(30, 23), BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };
            grpFont.Controls.Add(pnlOutlineColor);
            btnOutlineColor = new Button { Text = "选择...", Location = new Point(230, fy), Size = new Size(50, 23) };
            btnOutlineColor.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = pnlOutlineColor.BackColor })
                    if (dlg.ShowDialog() == DialogResult.OK) pnlOutlineColor.BackColor = dlg.Color;
            };
            grpFont.Controls.Add(btnOutlineColor);
            fy += 32;

            var lblTrans = new Label { Text = "透明度:", Location = new Point(10, fy), Size = new Size(55, 20) };
            grpFont.Controls.Add(lblTrans);
            numTransparency = new NumericUpDown { Location = new Point(65, fy), Size = new Size(50, 23), Minimum = 0, Maximum = 100, Value = 100 };
            grpFont.Controls.Add(numTransparency);

            var lblAlign = new Label { Text = "对齐:", Location = new Point(140, fy), Size = new Size(45, 20) };
            grpFont.Controls.Add(lblAlign);
            cmbAlignment = new ComboBox { Location = new Point(190, fy), Size = new Size(90, 23), DropDownStyle = ComboBoxStyle.DropDownList };
            cmbAlignment.Items.AddRange(new object[] { "左下", "中下", "右下", "中左", "中中", "中右", "左上", "中上", "右上" });
            cmbAlignment.SelectedIndex = 1;
            grpFont.Controls.Add(cmbAlignment);
            fy += 32;

            var lblMargin = new Label { Text = "垂直边距:", Location = new Point(10, fy), Size = new Size(65, 20) };
            grpFont.Controls.Add(lblMargin);
            numMarginV = new NumericUpDown { Location = new Point(80, fy), Size = new Size(50, 23), Minimum = 0, Maximum = 200, Value = 20 };
            grpFont.Controls.Add(numMarginV);

            // 背景设置（仍属于左列，放在字体组下方）
            var grpBg = new GroupBox { Text = "背景", Location = new Point(margin, y + grpFont.Height + 10), Size = new Size(360, 90) };
            tabSubtitle.Controls.Add(grpBg);
            int by = 22;

            chkBackEnabled = new CheckBox { Text = "启用背景", Location = new Point(10, by), Size = new Size(80, 20) };
            grpBg.Controls.Add(chkBackEnabled);

            var lblBgColor = new Label { Text = "颜色:", Location = new Point(100, by), Size = new Size(40, 20) };
            grpBg.Controls.Add(lblBgColor);
            pnlBackColor = new Panel { Location = new Point(145, by), Size = new Size(30, 23), BackColor = Color.Black, BorderStyle = BorderStyle.FixedSingle };
            grpBg.Controls.Add(pnlBackColor);
            btnBackColor = new Button { Text = "选择...", Location = new Point(180, by), Size = new Size(50, 23) };
            btnBackColor.Click += (s, e) =>
            {
                using (var dlg = new ColorDialog { Color = pnlBackColor.BackColor })
                    if (dlg.ShowDialog() == DialogResult.OK) pnlBackColor.BackColor = dlg.Color;
            };
            grpBg.Controls.Add(btnBackColor);
            by += 32;

            var lblBga = new Label { Text = "背景透明度:", Location = new Point(10, by), Size = new Size(75, 20) };
            grpBg.Controls.Add(lblBga);
            numBackAlpha = new NumericUpDown { Location = new Point(90, by), Size = new Size(50, 23), Minimum = 0, Maximum = 100, Value = 80 };
            grpBg.Controls.Add(numBackAlpha);

            // ---- 右列：预览图（与左列留足间距，避免重叠）----
            picSubPreview = new PictureBox
            {
                Location = new Point(margin + 440, 20),
                Size = new Size(520, 340),
                BackColor = Color.FromArgb(80, 80, 80),
                SizeMode = PictureBoxSizeMode.Normal
            };
            picSubPreview.Paint += (s, e) => PaintSubtitlePreview(e, picSubPreview);
            tabSubtitle.Controls.Add(picSubPreview);

            // ---- 底部按钮：应用 / 应用到全部 / 保存 / 保存为默认 ----
            int btnY = grpBg.Location.Y + grpBg.Height + 16;
            tabSubtitle.Controls.Add(MakeApplyButton("应用", margin, btnY, 90, 32, false));
            tabSubtitle.Controls.Add(MakeApplyButton("应用到全部", margin + 100, btnY, 110, 32, true));

            var btnSaveSub = new Button
            {
                Text = "保存",
                Location = new Point(margin + 220, btnY),
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            btnSaveSub.FlatAppearance.BorderSize = 0;
            btnSaveSub.Click += (s, e) =>
            {
                SyncSubSettings();
                MessageBox.Show(this, "字幕设置已保存，仅对当前视频生效。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            tabSubtitle.Controls.Add(btnSaveSub);

            var btnSaveDefaultSub = new Button
            {
                Text = "保存为默认",
                Location = new Point(margin + 330, btnY),
                Size = new Size(120, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(124, 77, 255),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            btnSaveDefaultSub.FlatAppearance.BorderColor = Color.FromArgb(124, 77, 255);
            btnSaveDefaultSub.Click += (s, e) =>
            {
                SyncSubSettings();
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "subtitle_default.json");
                    var dto = new SubSettingsDto
                    {
                        FontName = SubSettings.FontName, FontSize = SubSettings.FontSize, FontColorArgb = SubSettings.FontColorArgb,
                        Bold = SubSettings.Bold, Italic = SubSettings.Italic, Underline = SubSettings.Underline,
                        OutlineWidth = SubSettings.OutlineWidth, OutlineColorArgb = SubSettings.OutlineColorArgb,
                        Transparency = SubSettings.Transparency, BackEnabled = SubSettings.BackEnabled,
                        BackColorArgb = SubSettings.BackColorArgb, BackAlpha = SubSettings.BackAlpha,
                        Alignment = SubSettings.Alignment, MarginV = SubSettings.MarginV
                    };
                    using (var ms = new MemoryStream())
                    {
                        new DataContractJsonSerializer(typeof(SubSettingsDto)).WriteObject(ms, dto);
                        File.WriteAllText(path, Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8);
                    }
                    MessageBox.Show(this, "已保存为默认字幕设置。\n文件: " + Path.GetFileName(path), "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存默认设置失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            tabSubtitle.Controls.Add(btnSaveDefaultSub);

            // Update preview on change
            EventHandler updatePreview = (s, e) =>
            {
                SyncSubSettings();
                picSubPreview.Invalidate();
            };
            cmbFontName.SelectedIndexChanged += updatePreview;
            numFontSize.ValueChanged += updatePreview;
            chkBold.CheckedChanged += updatePreview;
            chkItalic.CheckedChanged += updatePreview;
            chkUnderline.CheckedChanged += updatePreview;
            pnlFontColor.BackColorChanged += updatePreview;
            numOutlineW.ValueChanged += updatePreview;
            pnlOutlineColor.BackColorChanged += updatePreview;
            numTransparency.ValueChanged += updatePreview;
            chkBackEnabled.CheckedChanged += updatePreview;
            pnlBackColor.BackColorChanged += updatePreview;
            numBackAlpha.ValueChanged += updatePreview;
            cmbAlignment.SelectedIndexChanged += updatePreview;
            numMarginV.ValueChanged += updatePreview;
            updatePreview(null, EventArgs.Empty);
        }

        private void SyncSubSettings()
        {
            SubSettings.FontName = cmbFontName.Text;
            SubSettings.FontSize = (int)numFontSize.Value;
            SubSettings.FontColorArgb = pnlFontColor.BackColor.ToArgb();
            SubSettings.Bold = chkBold.Checked;
            SubSettings.Italic = chkItalic.Checked;
            SubSettings.Underline = chkUnderline.Checked;
            SubSettings.OutlineWidth = (int)numOutlineW.Value;
            SubSettings.OutlineColorArgb = pnlOutlineColor.BackColor.ToArgb();
            SubSettings.Transparency = (int)numTransparency.Value;
            SubSettings.BackEnabled = chkBackEnabled.Checked;
            SubSettings.BackColorArgb = pnlBackColor.BackColor.ToArgb();
            SubSettings.BackAlpha = (int)numBackAlpha.Value;
            SubSettings.Alignment = cmbAlignment.SelectedIndex + 1;
            SubSettings.MarginV = (int)numMarginV.Value;
            SubSettings.ExternalSubPath = string.IsNullOrWhiteSpace(txtExternalSub.Text) ? null : txtExternalSub.Text.Trim();
        }

        private void PaintSubtitlePreview(PaintEventArgs e, Control ctrl)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Color.FromArgb(80, 80, 80)))
                g.FillRectangle(bg, ctrl.ClientRectangle);

            string sample = "字幕预览示例文本";
            float fontSize = (float)numFontSize.Value;
            FontStyle fs = FontStyle.Regular;
            if (chkBold.Checked) fs |= FontStyle.Bold;
            if (chkItalic.Checked) fs |= FontStyle.Italic;
            if (chkUnderline.Checked) fs |= FontStyle.Underline;
            Font font;
            try { font = new Font(cmbFontName.Text, fontSize, fs, GraphicsUnit.Pixel); }
            catch { font = new Font("Arial", fontSize, fs, GraphicsUnit.Pixel); }
            using (font)
            {
                var sz = g.MeasureString(sample, font);
                int align = cmbAlignment.SelectedIndex + 1;
                int col = (align - 1) % 3, row = 2 - (align - 1) / 3;
                float mx = col == 0 ? 16 : col == 1 ? (ctrl.Width - sz.Width) / 2 : ctrl.Width - sz.Width - 16;
                float my = row == 0 ? 16 : row == 1 ? (ctrl.Height - sz.Height) / 2 : ctrl.Height - sz.Height - (float)numMarginV.Value - 16;

                int alpha = Math.Max(0, Math.Min(255, 255 * (int)numTransparency.Value / 100));
                Color fc = Color.FromArgb(alpha, pnlFontColor.BackColor.R, pnlFontColor.BackColor.G, pnlFontColor.BackColor.B);

                if (chkBackEnabled.Checked)
                {
                    int ba = Math.Max(0, Math.Min(255, 255 * (100 - (int)numBackAlpha.Value) / 100));
                    using (var bgBrush = new SolidBrush(Color.FromArgb(ba, pnlBackColor.BackColor.R, pnlBackColor.BackColor.G, pnlBackColor.BackColor.B)))
                        g.FillRectangle(bgBrush, mx - 2, my - 2, sz.Width + 4, sz.Height + 4);
                }

                int ow = (int)numOutlineW.Value;
                if (ow > 0)
                {
                    Color oc = pnlOutlineColor.BackColor;
                    using (var ob = new SolidBrush(Color.FromArgb(alpha, oc.R, oc.G, oc.B)))
                    {
                        for (int dx = -ow; dx <= ow; dx += Math.Max(1, ow))
                            for (int dy = -ow; dy <= ow; dy += Math.Max(1, ow))
                            {
                                if (dx == 0 && dy == 0) continue;
                                g.DrawString(sample, font, ob, mx + dx, my + dy);
                            }
                    }
                }

                using (var fg = new SolidBrush(fc))
                    g.DrawString(sample, font, fg, mx, my);
            }
        }

        #endregion

        #region Bottom Buttons

        private void BuildBottomButtons()
        {
            int y = this.ClientSize.Height - 50;

            btnApplyAllGlobal = new Button
            {
                Text = "应用到全部",
                Location = new Point(12, y),
                Size = new Size(100, 32),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(124, 77, 255),
                FlatStyle = FlatStyle.Flat
            };
            btnApplyAllGlobal.FlatAppearance.BorderColor = Color.FromArgb(124, 77, 255);
            btnApplyAllGlobal.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnApplyAllGlobal.Click += (s, e) => { ApplyToAll = true; ApplySettings(); this.DialogResult = DialogResult.OK; this.Close(); };

            btnOK = new Button
            {
                Text = "确定",
                Location = new Point(this.ClientSize.Width - 200, y),
                Size = new Size(80, 32),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnOK.FlatAppearance.BorderSize = 0;
            btnOK.DialogResult = DialogResult.OK;
            btnOK.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            btnOK.Click += (s, e) => ApplySettings();

            btnCancel = new Button
            {
                Text = "取消",
                Location = new Point(this.ClientSize.Width - 100, y),
                Size = new Size(80, 32),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                FlatStyle = FlatStyle.Flat
            };
            btnCancel.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnCancel.DialogResult = DialogResult.Cancel;
            btnCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            this.Controls.Add(btnApplyAllGlobal);
            this.Controls.Add(btnOK);
            this.Controls.Add(btnCancel);

            this.Resize += (s, e) =>
            {
                int by = this.ClientSize.Height - 50;
                btnApplyAllGlobal.Location = new Point(12, by);
                btnOK.Location = new Point(this.ClientSize.Width - 200, by);
                btnCancel.Location = new Point(this.ClientSize.Width - 100, by);
            };
        }

        /// <summary>统一生成「应用 / 应用到全部」按钮（点击即保存并关闭对话框）。</summary>
        private Button MakeApplyButton(string text, int x, int y, int w, int h, bool applyAll)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = applyAll ? Color.FromArgb(124, 77, 255) : Color.White,
                ForeColor = applyAll ? Color.White : Color.FromArgb(124, 77, 255),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(124, 77, 255);
            btn.FlatAppearance.BorderSize = applyAll ? 0 : 1;
            btn.Click += (s, e) =>
            {
                if (applyAll) ApplyToAll = true;
                ApplySettings();
                this.DialogResult = DialogResult.OK;
                this.Close();
            };
            return btn;
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
            else if (TrimStartSeconds > 0 || (TrimEndSeconds > 0 && TrimEndSeconds < SourceDurationSeconds))
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

                // Init effects controls
                trkSpeed.Value = (int)(Speed * 100);
                trkBrightness.Value = (int)Brightness;
                trkContrast.Value = (int)(Contrast * 100);
                trkSaturation.Value = (int)(Saturation * 100);
                txtWatermark.Text = WatermarkPath ?? "";
                trkWatermarkOpacity.Value = (int)(WatermarkOpacity * 100);
                trkWatermarkScale.Value = (int)WatermarkScalePercent;

                // Init subtitle controls
                if (SubSettings != null)
                {
                    cmbFontName.SelectedItem = SubSettings.FontName ?? "Arial";
                    numFontSize.Value = Math.Max(8, Math.Min(72, SubSettings.FontSize));
                    pnlFontColor.BackColor = Color.FromArgb(SubSettings.FontColorArgb);
                    chkBold.Checked = SubSettings.Bold;
                    chkItalic.Checked = SubSettings.Italic;
                    chkUnderline.Checked = SubSettings.Underline;
                    numOutlineW.Value = Math.Max(0, Math.Min(10, SubSettings.OutlineWidth));
                    pnlOutlineColor.BackColor = Color.FromArgb(SubSettings.OutlineColorArgb);
                    numTransparency.Value = Math.Max(0, Math.Min(100, SubSettings.Transparency));
                    chkBackEnabled.Checked = SubSettings.BackEnabled;
                    pnlBackColor.BackColor = Color.FromArgb(SubSettings.BackColorArgb);
                    numBackAlpha.Value = Math.Max(0, Math.Min(100, SubSettings.BackAlpha));
                    cmbAlignment.SelectedIndex = Math.Max(0, Math.Min(8, SubSettings.Alignment - 1));
                    numMarginV.Value = Math.Max(0, Math.Min(200, SubSettings.MarginV));
                    txtExternalSub.Text = SubSettings.ExternalSubPath ?? DefaultExternalSubPath ?? "";
                }

                // Load subtitle tracks
                cmbSubTrack.Items.Clear();
                if (SubTracks != null && SubTracks.Count > 0)
                {
                    foreach (var t in SubTracks) cmbSubTrack.Items.Add(t);
                    cmbSubTrack.SelectedIndex = 0;
                }
                else
                {
                    cmbSubTrack.Items.Add("无字幕轨道");
                }

                _keyframes = await FFmpegHelper.GetKeyframesAsync(InputPath);
                await ExtractThumbnailsAsync();

                // 初始渲染：保证各页签都有首帧图像（裁剪纸框交互依赖 picCropPreview.Image 非空）
                await RenderInitialFrameAsync();
                UpdateCropOutputPreview();
                RefreshEffectPreview();
                panelTimeline.Invalidate();

                // 应用调用方指定的起始页签（修正：之前未生效，导致字幕弹窗总停在剪切页）
                if (StartTabIndex >= 0 && StartTabIndex < tabControl.TabPages.Count)
                    tabControl.SelectedIndex = StartTabIndex;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "加载视频信息失败: " + ex.Message, "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            StopPlay();
            _playCts?.Cancel();
            _playCts?.Dispose();
            _playCts = null;
            foreach (var img in _thumbnails) img?.Dispose();
            _thumbnails.Clear();
            base.OnFormClosing(e);
        }

        #endregion

        #region Helpers

        private Button CreateIconButton(string text, int x, int y, int w, int h, string tooltip)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(w, h),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 9F)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            if (!string.IsNullOrEmpty(tooltip)) toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        private Button CreateTextButton(string text, int x, int y, int w, int h, string tooltip)
            => CreateIconButton(text, x, y, w, h, tooltip);

        private TextBox CreateNumberInput(int x, int y, int w, int h)
        {
            var tb = new TextBox
            {
                Location = new Point(x, y),
                Size = new Size(w, h),
                Text = "0",
                TextAlign = HorizontalAlignment.Center
            };
            return tb;
        }

        private string FormatTime(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(ms);
            return string.Format("{0:D2}:{1:D2}:{2:D2}.{3:D3}", ts.Hours, ts.Minutes, ts.Seconds, ts.Milliseconds);
        }

        private void UpdateTimeLabel()
        {
            lblTime.Text = $"{FormatTime(_currentTimeMs)} / {FormatTime(_totalMs)}";
            if (lblCropTime != null) lblCropTime.Text = lblTime.Text;
        }

        private void SwapImage(PictureBox pb, Image newImg)
        {
            if (pb == null) { newImg?.Dispose(); return; }
            var old = pb.Image;
            pb.Image = newImg;
            old?.Dispose();
        }

        #endregion

        #region Playback (unified sequential loop)

        private void TogglePlay()
        {
            if (_isPlaying) StopPlay();
            else StartPlay();
        }

        private void StartPlay()
        {
            if (_isPlaying || _totalMs <= 0) return;
            _isPlaying = true;
            SetPlayButtons(true);
            PlayLoop();
        }

        private void StopPlay()
        {
            _isPlaying = false;
            _playCts?.Cancel();
            SetPlayButtons(false);
        }

        private void SetPlayButtons(bool playing)
        {
            string t = playing ? "⏸" : "▶";
            if (btnPlay != null) btnPlay.Text = t;
            if (btnCropPlay != null) btnCropPlay.Text = t;
            if (btnEffectPlay != null) btnEffectPlay.Text = t;
        }

        private async void PlayLoop()
        {
            _playCts = new CancellationTokenSource();
            var ct = _playCts.Token;
            try
            {
                while (_isPlaying && !ct.IsCancellationRequested)
                {
                    if (_currentTimeMs >= _totalMs)
                    {
                        _currentTimeMs = _totalMs;
                        SyncTimeUi();
                        StopPlay();
                        break;
                    }
                    await RenderCurrentFrameAsync();
                    if (ct.IsCancellationRequested) break;
                    SyncTimeUi();
                    UpdateTimelineSelectionFromTime();
                    _currentTimeMs = Math.Min(_totalMs, _currentTimeMs + _frameIntervalMs);
                }
            }
            catch { }
            finally
            {
                try { _playCts?.Dispose(); } catch { }
                _playCts = null;
            }
        }

        /// <summary>按当前页签渲染一帧视频（裁剪页签同时刷新输出预览）。</summary>
        private async Task RenderCurrentFrameAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(InputPath)) return;
                var tab = tabControl.SelectedTab;
                int fw = SourceWidth > 0 ? SourceWidth : 1280;
                int fh = SourceHeight > 0 ? SourceHeight : 720;
                var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, _currentTimeMs, fw, fh);
                if (img == null) return;

                if (tab == tabEffects)
                {
                    var result = ApplyColorMatrix(new Bitmap(img));
                    img.Dispose();
                    SwapImage(picEffectPreview, result);
                }
                else if (tab == tabCrop)
                {
                    var clone = new Bitmap(img);
                    SwapImage(picCropPreview, clone);
                    img.Dispose();
                    picCropPreview.Invalidate();
                    // 用同一帧直接裁剪输出，避免播放时重复取帧
                    var cropped = CropAndRotateBitmap(clone, _crop, _rotation);
                    SwapImage(picCropOutput, cropped);
                }
                else
                {
                    // 修剪页签（或未知页签）只更新主预览
                    SwapImage(picPreview, img);
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

        private async Task RenderInitialFrameAsync()
        {
            try
            {
                if (string.IsNullOrEmpty(InputPath)) return;
                int fw = SourceWidth > 0 ? SourceWidth : 1280;
                int fh = SourceHeight > 0 ? SourceHeight : 720;
                var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, _currentTimeMs, fw, fh);
                if (img == null) return;
                SwapImage(picPreview, new Bitmap(img));
                SwapImage(picCropPreview, new Bitmap(img));
                picCropPreview.Invalidate();
                var cropped = CropAndRotateBitmap((Bitmap)picCropPreview.Image, _crop, _rotation);
                SwapImage(picCropOutput, cropped);
                img.Dispose();
            }
            catch { }
        }

        private void SyncTimeUi()
        {
            _seeking = true;
            int v = _totalMs > 0 ? (int)(_currentTimeMs * 1000.0 / _totalMs) : 0;
            if (trackHead != null) trackHead.Value = Math.Max(trackHead.Minimum, Math.Min(trackHead.Maximum, v));
            if (trackCropHead != null) trackCropHead.Value = Math.Max(trackCropHead.Minimum, Math.Min(trackCropHead.Maximum, v));
            if (trackEffectHead != null) trackEffectHead.Value = Math.Max(trackEffectHead.Minimum, Math.Min(trackEffectHead.Maximum, v));
            _seeking = false;
            UpdateTimeLabel();
            panelTimeline?.Invalidate();
        }

        private void TrackSeek(TrackBar tb)
        {
            if (_seeking || tb == null) return;
            StopPlay();
            long ms = tb.Maximum > 0 ? (long)(tb.Value * _totalMs / tb.Maximum) : 0;
            _currentTimeMs = Math.Max(0, Math.Min(_totalMs, ms));
            SyncTimeUi();
            UpdateTimelineSelectionFromTime();
            var _ = RenderCurrentFrameAsync();
        }

        private void StepFrame(int dir)
        {
            StopPlay();
            _currentTimeMs = Math.Max(0, Math.Min(_totalMs, _currentTimeMs + dir * _frameIntervalMs));
            SyncTimeUi();
            UpdateTimelineSelectionFromTime();
            var _ = RenderCurrentFrameAsync();
        }

        private void SeekPrevKeyframe()
        {
            StopPlay();
            long t = _keyframes.LastOrDefault(k => k < _currentTimeMs);
            if (t == 0 && _keyframes.Count > 0 && _keyframes[0] < _currentTimeMs) t = _keyframes[0];
            if (t == 0 && _currentTimeMs > 0) t = 0;
            _currentTimeMs = t;
            SyncTimeUi();
            UpdateTimelineSelectionFromTime();
            var _ = RenderCurrentFrameAsync();
        }

        private void SeekNextKeyframe()
        {
            StopPlay();
            long t = _keyframes.FirstOrDefault(k => k > _currentTimeMs);
            if (t == 0 && _keyframes.Count > 0 && _keyframes[_keyframes.Count - 1] > _currentTimeMs)
                t = _keyframes[_keyframes.Count - 1];
            if (t == 0) t = _totalMs;
            _currentTimeMs = Math.Min(_totalMs, t);
            SyncTimeUi();
            UpdateTimelineSelectionFromTime();
            var _ = RenderCurrentFrameAsync();
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

            float pxPerMs = w / (float)_totalMs;
            foreach (var seg in _segments)
            {
                int x = (int)(seg.StartMs * pxPerMs);
                int rw = Math.Max(2, (int)((seg.EndMs - seg.StartMs) * pxPerMs));
                using (var brush = new SolidBrush(seg.IsSelected ? Color.FromArgb(160, 124, 77, 255) : Color.FromArgb(100, 124, 77, 255)))
                    g.FillRectangle(brush, x, top, rw, thumbH);
                g.DrawRectangle(seg.IsSelected ? Pens.Red : Pens.Purple, x, top, rw - 1, thumbH - 1);
            }

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

            int hx = (int)(_currentTimeMs * pxPerMs);
            using (var pen = new Pen(Color.Red, 2))
                g.DrawLine(pen, hx, top - 4, hx, top + thumbH + 4);
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
            SyncTimeUi();
            if (selectSegment)
            {
                int idx = SegmentIndexAt(ms);
                for (int i = 0; i < _segments.Count; i++) _segments[i].IsSelected = (i == idx);
                panelTimeline.Invalidate();
            }
            var _ = RenderCurrentFrameAsync();
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
                int hsize = 8;
                DrawHandle(g, _cropRect.Left, _cropRect.Top, hsize);
                DrawHandle(g, _cropRect.Right, _cropRect.Top, hsize);
                DrawHandle(g, _cropRect.Left, _cropRect.Bottom, hsize);
                DrawHandle(g, _cropRect.Right, _cropRect.Bottom, hsize);
            }
            // 用 Alternate 填充规则在裁剪框内挖空，框外变暗。
            var path = new GraphicsPath(FillMode.Alternate);
            path.AddRectangle(new Rectangle(0, 0, picCropPreview.ClientSize.Width, picCropPreview.ClientSize.Height));
            path.AddRectangle(_cropRect);
            using (var brush = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
                g.FillPath(brush, path);
        }

        private Rectangle _cropRect;

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

        private double GetDisplayScale()
        {
            if (picCropPreview.Image == null) return 0;
            var imgSize = picCropPreview.Image.Size;
            var boxSize = picCropPreview.ClientSize;
            if (imgSize.Width <= 0 || imgSize.Height <= 0) return 0;
            return Math.Min(boxSize.Width / (double)imgSize.Width, boxSize.Height / (double)imgSize.Height);
        }

        private enum CropDragMode { None, Move, ResizeTL, ResizeTR, ResizeBL, ResizeBR, DrawNew }
        private CropDragMode _cropDragMode = CropDragMode.None;
        private Point _cropDragStart;
        private const int HandleHitSize = 10;

        private void PicCropPreview_MouseDown(object sender, MouseEventArgs e)
        {
            if (picCropPreview.Image == null) return;
            _cropRect = GetCropRectInPictureBox();
            _cropDragStart = e.Location;

            if (Math.Abs(e.X - _cropRect.Left) <= HandleHitSize && Math.Abs(e.Y - _cropRect.Top) <= HandleHitSize)
            { _cropDragMode = CropDragMode.ResizeTL; return; }
            if (Math.Abs(e.X - _cropRect.Right) <= HandleHitSize && Math.Abs(e.Y - _cropRect.Top) <= HandleHitSize)
            { _cropDragMode = CropDragMode.ResizeTR; return; }
            if (Math.Abs(e.X - _cropRect.Left) <= HandleHitSize && Math.Abs(e.Y - _cropRect.Bottom) <= HandleHitSize)
            { _cropDragMode = CropDragMode.ResizeBL; return; }
            if (Math.Abs(e.X - _cropRect.Right) <= HandleHitSize && Math.Abs(e.Y - _cropRect.Bottom) <= HandleHitSize)
            { _cropDragMode = CropDragMode.ResizeBR; return; }

            if (_cropRect.Contains(e.Location))
            { _cropDragMode = CropDragMode.Move; return; }

            // 点在框外 → 画新选择框
            _cropDragMode = CropDragMode.DrawNew;
            _crop.X = 0; _crop.Y = 0; _crop.Width = 0; _crop.Height = 0;
        }

        private void PicCropPreview_MouseMove(object sender, MouseEventArgs e)
        {
            if (_cropDragMode == CropDragMode.None) return;
            if (picCropPreview.Image == null) return;

            double scale = GetDisplayScale();
            if (scale <= 0) return;
            var imgSize = picCropPreview.Image.Size;
            var boxSize = picCropPreview.ClientSize;
            int drawW = (int)(imgSize.Width * scale);
            int drawH = (int)(imgSize.Height * scale);
            int offX = (boxSize.Width - drawW) / 2;
            int offY = (boxSize.Height - drawH) / 2;

            int sx = Math.Max(0, Math.Min(SourceWidth, (int)((e.X - offX) / scale)));
            int sy = Math.Max(0, Math.Min(SourceHeight, (int)((e.Y - offY) / scale)));

            if (_cropDragMode == CropDragMode.DrawNew)
            {
                int sx0 = Math.Max(0, Math.Min(SourceWidth, (int)((_cropDragStart.X - offX) / scale)));
                int sy0 = Math.Max(0, Math.Min(SourceHeight, (int)((_cropDragStart.Y - offY) / scale)));
                _crop.X = Math.Min(sx0, sx);
                _crop.Y = Math.Min(sy0, sy);
                _crop.Width = Math.Abs(sx - sx0);
                _crop.Height = Math.Abs(sy - sy0);
            }
            else if (_cropDragMode == CropDragMode.Move)
            {
                int dx = e.X - _cropDragStart.X;
                int dy = e.Y - _cropDragStart.Y;
                _cropDragStart = e.Location;
                _crop.X = Math.Max(0, Math.Min(SourceWidth - _crop.Width, _crop.X + (int)(dx / scale)));
                _crop.Y = Math.Max(0, Math.Min(SourceHeight - _crop.Height, _crop.Y + (int)(dy / scale)));
            }
            else
            {
                int minSize = 10;
                switch (_cropDragMode)
                {
                    case CropDragMode.ResizeTL:
                        int nl = Math.Max(0, Math.Min(_crop.X + _crop.Width - minSize, sx));
                        int nt = Math.Max(0, Math.Min(_crop.Y + _crop.Height - minSize, sy));
                        _crop.Width = _crop.X + _crop.Width - nl;
                        _crop.Height = _crop.Y + _crop.Height - nt;
                        _crop.X = nl; _crop.Y = nt;
                        break;
                    case CropDragMode.ResizeTR:
                        int nr = Math.Max(_crop.X + minSize, Math.Min(SourceWidth, sx));
                        int nt2 = Math.Max(0, Math.Min(_crop.Y + _crop.Height - minSize, sy));
                        _crop.Width = nr - _crop.X;
                        _crop.Height = _crop.Y + _crop.Height - nt2;
                        _crop.Y = nt2;
                        break;
                    case CropDragMode.ResizeBL:
                        int nl2 = Math.Max(0, Math.Min(_crop.X + _crop.Width - minSize, sx));
                        int nb = Math.Max(_crop.Y + minSize, Math.Min(SourceHeight, sy));
                        _crop.Width = _crop.X + _crop.Width - nl2;
                        _crop.Height = nb - _crop.Y;
                        _crop.X = nl2;
                        break;
                    case CropDragMode.ResizeBR:
                        _crop.Width = Math.Max(minSize, Math.Min(SourceWidth - _crop.X, sx - _crop.X));
                        _crop.Height = Math.Max(minSize, Math.Min(SourceHeight - _crop.Y, sy - _crop.Y));
                        break;
                }
            }
            UpdateCropInputs();
            picCropPreview.Invalidate();
        }

        private void PicCropPreview_MouseUp(object sender, MouseEventArgs e)
        {
            _cropDragMode = CropDragMode.None;
            picCropPreview.Invalidate();
            UpdateCropOutputPreview();
        }

        private async void UpdateCropOutputPreview()
        {
            try
            {
                if (string.IsNullOrEmpty(InputPath)) return;
                var img = await FFmpegHelper.GetFrameAtTimeAsync(InputPath, _currentTimeMs,
                    picCropOutput.Width * 2, picCropOutput.Height * 2);
                if (img == null) return;
                using (img)
                {
                    var cropped = CropAndRotateBitmap((Bitmap)img, _crop, _rotation);
                    SwapImage(picCropOutput, cropped);
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
                var _ = RenderCurrentFrameAsync();
                UpdateCropOutputPreview();
            }
            else if (tabControl.SelectedTab == tabEffects)
            {
                var _ = RenderCurrentFrameAsync();
            }
            else if (tabControl.SelectedTab == tabTrim)
            {
                var _ = RenderCurrentFrameAsync();
            }
        }

        #region OK / Apply

        private void ApplySettings()
        {
            NormalizeSegments();
            foreach (var s in _segments) s.IsSelected = false;
            Segments = _segments;
            Crop = _crop;
            Rotation = _rotation;
            MergeSegments = chkMerge.Checked;

            Speed = trkSpeed.Value / 100.0;
            Brightness = trkBrightness.Value;
            Contrast = trkContrast.Value / 100.0;
            Saturation = trkSaturation.Value / 100.0;
            WatermarkPath = txtWatermark.Text;

            SyncSubSettings();

            if (Segments != null && Segments.Count > 0)
            {
                TrimStartSeconds = Segments[0].StartMs / 1000.0;
                TrimEndSeconds = Segments[Segments.Count - 1].EndMs / 1000.0;
            }
        }

        private void BtnOK_Click(object sender, EventArgs e) => ApplySettings();

        #endregion

        #region Subtitle default serialization

        public static SubtitleSettings LoadSubtitleOrDefault(ConversionTask task)
        {
            if (task?.SubtitleSettings != null && HasNonDefaultSettings(task.SubtitleSettings))
                return CloneSubSettings(task.SubtitleSettings);
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "subtitle_default.json");
                if (File.Exists(path))
                {
                    using (var ms = new MemoryStream(File.ReadAllBytes(path)))
                    {
                        var dto = (SubSettingsDto)new DataContractJsonSerializer(typeof(SubSettingsDto)).ReadObject(ms);
                        if (dto != null) return DtoToSubSettings(dto);
                    }
                }
            }
            catch { }
            return task?.SubtitleSettings ?? new SubtitleSettings();
        }

        private static bool HasNonDefaultSettings(SubtitleSettings s)
            => s.FontName != "Arial" || s.FontSize != 24 || s.FontColorArgb != unchecked((int)0xFFFFFFFF)
            || s.Bold || s.Italic || s.Underline || s.Alignment != 2 || s.MarginV != 20;

        private static SubtitleSettings CloneSubSettings(SubtitleSettings s)
            => new SubtitleSettings { FontName = s.FontName, FontSize = s.FontSize, FontColorArgb = s.FontColorArgb, Bold = s.Bold, Italic = s.Italic, Underline = s.Underline, OutlineWidth = s.OutlineWidth, OutlineColorArgb = s.OutlineColorArgb, Transparency = s.Transparency, BackEnabled = s.BackEnabled, BackColorArgb = s.BackColorArgb, BackAlpha = s.BackAlpha, Alignment = s.Alignment, MarginV = s.MarginV };

        private static SubtitleSettings DtoToSubSettings(SubSettingsDto d)
            => new SubtitleSettings { FontName = d.FontName ?? "Arial", FontSize = d.FontSize > 0 ? d.FontSize : 24, FontColorArgb = d.FontColorArgb != 0 ? d.FontColorArgb : unchecked((int)0xFFFFFFFF), Bold = d.Bold, Italic = d.Italic, Underline = d.Underline, OutlineWidth = d.OutlineWidth, OutlineColorArgb = d.OutlineColorArgb != 0 ? d.OutlineColorArgb : unchecked((int)0xFF000000), Transparency = d.Transparency, BackEnabled = d.BackEnabled, BackColorArgb = d.BackColorArgb, BackAlpha = d.BackAlpha, Alignment = d.Alignment > 0 ? d.Alignment : 2, MarginV = d.MarginV };

        [DataContract]
        private class SubSettingsDto
        {
            [DataMember] public string FontName { get; set; }
            [DataMember] public int FontSize { get; set; }
            [DataMember] public int FontColorArgb { get; set; }
            [DataMember] public bool Bold { get; set; }
            [DataMember] public bool Italic { get; set; }
            [DataMember] public bool Underline { get; set; }
            [DataMember] public int OutlineWidth { get; set; }
            [DataMember] public int OutlineColorArgb { get; set; }
            [DataMember] public int Transparency { get; set; }
            [DataMember] public bool BackEnabled { get; set; }
            [DataMember] public int BackColorArgb { get; set; }
            [DataMember] public int BackAlpha { get; set; }
            [DataMember] public int Alignment { get; set; }
            [DataMember] public int MarginV { get; set; }
        }

        #endregion
    }
}
