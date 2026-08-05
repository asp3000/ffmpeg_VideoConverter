// ============================================================================
//  VideoConverter.cs — Converter main window for FFBatch.
//
//  Mirrors the VideoConverterUltimate "Converter" screen (see screenshot).
//  All heavy lifting is done by ffmpeg.exe in D:\AI\ffmpeg-8.1.2-full_build.
//
//  This file and its companions are placed in D:\AI\ffmpeg_batch\FFBatch\VideoConverter
//  and do not modify any existing FFBatch source files.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Globalization;

namespace VideoConverter
{
    public partial class VideoConverter : Form
    {
        private readonly List<ConversionTask> _tasks = new List<ConversionTask>();
        private readonly List<TaskCard> _cards = new List<TaskCard>();
        private readonly ToolTip toolTip = new ToolTip();
        private bool _showCompleted;
        private FFmpegHelper.HardwareSupport _hwSupport;

        private bool _batchConverting;
        private bool _mergeAllMode;

        // 空状态引导：taskListPanel 无可见任务时显示的提示标签。
        private Label _emptyStateLabel;

        // 排序与搜索控件。
        private TextBox searchBox;
        private ComboBox sortCombo;
        private int _sortMode; // 0=添加顺序 1=名A-Z 2=名Z-A 3=大小↓ 4=时长↓

        // 卡片拖拽手工排序状态。
        private RoundedPanel _dragCard;      // 正在拖动的卡片面板
        private Point _dragStartPoint;       // 按下时相对卡片的坐标
        private int _insertionLineY = -1;    // 当前插入指示线的客户区 Y（-1=未画）

        // 搜索防抖：避免每次按键都全量重建卡片列表。
        private System.Windows.Forms.Timer _searchDebounce;


        // The single currently-selected card (null when none selected). #42
        private RoundedPanel _selectedCard;
        // Pending hardware-encode preference, applied once HW detection completes. #47
        private bool _pendingHardware;

        public VideoConverter()
        {
            InitializeComponent();

            // Use the embedded application icon (VideoConverter.ico) for the window.
            try
            {
                this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            // Load UniConverter preset database once (idempotent global cache). #43
            PresetDataStore.EnsureLoaded();
            DefaultCodecSettings.EnsureLoaded();   // 自动码率默认值与容器默认编码（可手工编辑配置）
            HardCodecSettings.EnsureLoaded();       // 硬件编码映射（label→CPU/GPU），用于解析与失败降级
            if (!PresetDataStore.IsLoaded && PresetDataStore.LoadException != null)
            {
                MessageBox.Show(this,
                    "预设数据加载失败，将使用内置预设。\n错误：" + PresetDataStore.LoadException.Message,
                    "预设加载提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            // Restore persisted check-box state (高速转换 / 硬件编码). #47
            AppSettings.Load();
            highSpeedCheck.Checked = AppSettings.HighSpeed;
            chapterCheck.Checked = AppSettings.KeepChapterMarkers;
            _pendingHardware = AppSettings.Hardware;

            // Allow dropping files anywhere on the window / list.
            // 只在 Form 层级处理拖放：子控件 AllowDrop=false（默认）时，
            // 拖放事件会自动穿透到 Form，避免 taskListPanel 或其子控件
            // （卡片、滚动条等）拦截事件导致多文件拖入失效。
            this.AllowDrop = true;
            this.DragEnter += VideoConverter_DragEnter;
            this.DragOver += VideoConverter_DragOver;
            this.DragDrop += VideoConverter_DragDrop;

            SetupPresets();
            SetupSaveTo();
            SetupSearchAndSort();
            SetupToolsButton();
            ApplyCheckStyle(highSpeedCheck);
            ApplyCheckStyle(hardwareCheck);
            mergeCheck.CheckedChanged += MergeCheck_CheckedChanged;
            chapterCheck.CheckedChanged += (s, e) => { AppSettings.KeepChapterMarkers = chapterCheck.Checked; AppSettings.Save(); };
            UpdateTabStyles();
            UpdateCount();

            // 后台读取本地 ffmpeg 版本到缓存（供"更新 ffmpeg"对话框使用）。
            _ = LoadFfmpegVersionAsync();

            // Probe ffmpeg for hardware encoders in the background; the checkbox
            // is enabled (and labelled with the GPU vendor) only if supported.
            DetectHardwareOnLoad();

            // Auto-load the persisted file list after the window is ready, and
            // save it again when the window closes. #93/#94
            this.Load += VideoConverter_Load;
            this.FormClosing += VideoConverter_FormClosing;

            // Window resize (including maximize/restore) syncs all card widths
            // and the empty-state label width so cards don't stay at a fixed width. #95
            taskListPanel.Resize += (s, e) =>
            {
                int newW = Math.Max(900, taskListPanel.ClientSize.Width - 40);
                foreach (var card in _cards)
                {
                    if (card.Panel != null && !card.Panel.IsDisposed && card.Panel.Width != newW)
                        card.Panel.Width = newW;
                }
                if (_emptyStateLabel != null && !_emptyStateLabel.IsDisposed)
                    _emptyStateLabel.Width = taskListPanel.ClientSize.Width - 32;
            };

            // 卡片拖拽手工排序：允许把卡片拖到其它位置以调整顺序。
            taskListPanel.AllowDrop = true;
            taskListPanel.DragOver += TaskListPanel_DragOver;
            taskListPanel.DragLeave += (s, e) => ClearInsertionLine();
            taskListPanel.DragDrop += TaskListPanel_DragDrop;
        }

        private void VideoConverter_Load(object sender, EventArgs e)
        {
            // Defer loading until the window is fully shown via BeginInvoke,
            // so RefreshTaskList runs on a fully laid-out UI instead of during
            // the Load phase where controls may not render properly. #94
            this.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await LoadTaskListAsync();
                    RefreshTaskList();
                }
                catch { }
            }));
        }

        private void VideoConverter_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                var dtos = new List<TaskListStore.TaskDto>();
                foreach (var t in _tasks)
                    dtos.Add(BuildTaskDto(t));
                TaskListStore.Save(dtos);
            }
            catch { }
            AppSettings.Save();
        }

        #region Setup

        private PresetOption _globalPreset;
        private bool _suppressSaveToEvent;
        private int _lastSaveToIndex;

        private void SetupPresets()
        {
            // Restore the last chosen "转换到" preset from settings if possible.
            _globalPreset = RestoreSavedPreset()
                ?? FindPresetByName("MP4", "1080")
                ?? FindPresetByName("MP4", "与源文件相同")
                ?? PresetOption.BuiltInAll.FirstOrDefault(p =>
                    string.Equals(p.FormatName, "MP4", StringComparison.OrdinalIgnoreCase))
                ?? PresetOption.MP4_1080;

            UpdateConvertToDisplay();
        }

        /// <summary>按上次保存的 FormatId + Name 恢复「转换到」预设；找不到返回 null。</summary>
        private PresetOption RestoreSavedPreset()
        {
            try
            {
                string fmtId = AppSettings.ConvertToFormatId;
                string name = AppSettings.ConvertToPresetName;
                if (string.IsNullOrWhiteSpace(name)) return null;

                PresetOption found = null;
                if (!string.IsNullOrWhiteSpace(fmtId))
                {
                    var fmt = PresetDataStore.FindFormat(fmtId);
                    if (fmt != null)
                        found = fmt.Presets.FirstOrDefault(p =>
                            string.Equals(p.Name, name, StringComparison.Ordinal));
                    if (found == null)
                        found = PresetDataStore.CustomPresets.FirstOrDefault(p =>
                            string.Equals(p.Name, name, StringComparison.Ordinal) &&
                            string.Equals(p.FormatId, fmtId, StringComparison.Ordinal));
                }
                if (found == null)
                    found = PresetDataStore.CustomPresets.FirstOrDefault(p =>
                        string.Equals(p.Name, name, StringComparison.Ordinal));
                return found != null ? found.Clone() : null;
            }
            catch { return null; }
        }

        private PresetOption FindPresetByName(string formatName, string presetNameHint)
        {
            foreach (var cat in PresetDataStore.Categories)
            {
                if (!PresetDataStore.FormatsByCategory.ContainsKey(cat)) continue;
                foreach (var fmt in PresetDataStore.FormatsByCategory[cat])
                {
                    var p = fmt.Presets.FirstOrDefault(x =>
                        string.Equals(x.FormatName, formatName, StringComparison.OrdinalIgnoreCase) &&
                        (x.Name.Contains(presetNameHint) ||
                         (!string.IsNullOrEmpty(x.ResolutionLabel) && x.ResolutionLabel.Contains(presetNameHint))));
                    if (p != null) return p.Clone();
                }
            }
            return null;
        }

        private void UpdateConvertToDisplay()
        {
            if (_globalPreset == null) _globalPreset = PresetOption.MP4_1080;
            convertToButton.Text = string.Format("{0} / {1}", _globalPreset.FormatName, _globalPreset.Name);
            convertToButton.Tag = _globalPreset;
        }

        private void SetupSaveTo()
        {
            saveToCombo.Items.Clear();
            saveToCombo.Items.Add("与源文件夹相同");
            if (AppSettings.SaveToFolders != null)
            {
                foreach (var dir in AppSettings.SaveToFolders)
                {
                    if (!string.IsNullOrWhiteSpace(dir) && !saveToCombo.Items.Contains(dir))
                        saveToCombo.Items.Add(dir);
                }
            }
            saveToCombo.Items.Add("选择文件夹...");

            saveToCombo.SelectedIndexChanged += SaveToCombo_SelectedIndexChanged;

            // Restore the last chosen "保存到" value.
            _suppressSaveToEvent = true;
            int idx = string.IsNullOrEmpty(AppSettings.SaveToValue)
                ? -1
                : saveToCombo.Items.IndexOf(AppSettings.SaveToValue);
            saveToCombo.SelectedIndex = idx >= 0 ? idx : 0;
            _suppressSaveToEvent = false;
            _lastSaveToIndex = saveToCombo.SelectedIndex;
        }

        /// <summary>
        /// "选择文件夹..." triggers a folder picker; the chosen directory is
        /// appended to the dropdown items and persisted to settings.
        /// </summary>
        private void SaveToCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressSaveToEvent) return;
            string sel = saveToCombo.SelectedItem as string;
            if (string.Equals(sel, "选择文件夹...", StringComparison.Ordinal))
            {
                int fallback = _lastSaveToIndex >= 0 ? _lastSaveToIndex : 0;
                using (var fbd = new FolderBrowserDialog())
                {
                    fbd.Description = "选择输出文件夹";
                    if (fbd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
                    {
                        string dir = fbd.SelectedPath;
                        _suppressSaveToEvent = true;
                        if (!saveToCombo.Items.Contains(dir))
                            saveToCombo.Items.Insert(saveToCombo.Items.Count - 1, dir);
                        saveToCombo.SelectedItem = dir;
                        _suppressSaveToEvent = false;
                        if (!AppSettings.SaveToFolders.Contains(dir))
                            AppSettings.SaveToFolders.Add(dir);
                        PersistSaveTo();
                    }
                    else
                    {
                        // Cancel: revert to the previously selected item.
                        _suppressSaveToEvent = true;
                        saveToCombo.SelectedIndex = fallback;
                        _suppressSaveToEvent = false;
                    }
                }
            }
            else
            {
                PersistSaveTo();
            }
            // 把当前"保存到"目录同步到所有未开始（Pending）的任务，
            // 使已排队文件也输出到新目录，而不是仍用旧目录（如源文件夹）。#95
            ApplySaveToToPendingTasks();
            _lastSaveToIndex = saveToCombo.SelectedIndex;
        }

        /// <summary>
        /// 把当前"保存到"目录同步到所有尚未开始转换（Pending）的任务，
        /// 并刷新其卡片上的输出文件名/路径提示。已在进行中的任务不改动
        /// （ffmpeg 已按原路径启动）。#95
        /// </summary>
        private void ApplySaveToToPendingTasks()
        {
            string folder = GetSelectedSaveToFolder();
            foreach (var t in _tasks)
            {
                if (t.Status != TaskStatus.Pending) continue;
                t.SaveToFolder = folder;
                t.ResetCachedOutputPath();
                var c = GetCard(t);
                if (c != null && !c.Panel.IsDisposed && c.OutputNameLabel != null && !c.OutputNameLabel.IsDisposed)
                {
                    c.OutputNameLabel.Text = t.GetOutputFileName();
                    toolTip.SetToolTip(c.OutputNameLabel, t.OutputPath);
                }
            }
        }

        private void PersistSaveTo()
        {
            AppSettings.SaveToValue = saveToCombo.SelectedItem as string;
            AppSettings.Save();
        }

        private void PersistConvertToPreset()
        {
            if (_globalPreset == null) return;
            AppSettings.ConvertToFormatId = _globalPreset.FormatId;
            AppSettings.ConvertToPresetName = _globalPreset.Name;
            AppSettings.Save();
        }

        /// <summary>
        /// 在顶部工具栏创建搜索框与排序下拉，位于标签页右侧、高速转换左侧。
        /// </summary>
        private void SetupSearchAndSort()
        {
            searchBox = new TextBox
            {
                Location = new Point(260, 16),
                Size = new Size(180, 23),
                Font = new Font("Microsoft YaHei UI", 9F),
                ForeColor = Color.FromArgb(120, 120, 120),
                BorderStyle = BorderStyle.FixedSingle
            };
            SetPlaceholder(searchBox, "搜索文件名...");
            // 搜索防抖：300ms 内无新按键才触发刷新，避免大量文件时每次按键都全量重建。
            _searchDebounce = new System.Windows.Forms.Timer { Interval = 300 };
            _searchDebounce.Tick += (s, e) =>
            {
                _searchDebounce.Stop();
                RefreshTaskList();
            };
            searchBox.TextChanged += (s, e) =>
            {
                _searchDebounce.Stop();
                _searchDebounce.Start();
            };
            topPanel.Controls.Add(searchBox);

            sortCombo = new ComboBox
            {
                Location = new Point(450, 15),
                Size = new Size(140, 23),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Microsoft YaHei UI", 9F),
                FlatStyle = FlatStyle.Flat
            };
            sortCombo.Items.AddRange(new object[]
            {
                "添加顺序", "文件名 A-Z", "文件名 Z-A", "文件大小↓", "时长↓"
            });
            sortCombo.SelectedIndex = 0;
            sortCombo.SelectedIndexChanged += (s, e) =>
            {
                _sortMode = sortCombo.SelectedIndex;
                RefreshTaskList();
            };
            topPanel.Controls.Add(sortCombo);
        }

        /// <summary>简易占位符文本：空输入时显示灰色提示，有输入时恢复正常。</summary>
        private static void SetPlaceholder(TextBox box, string placeholder)
        {
            box.Text = placeholder;
            box.ForeColor = Color.FromArgb(150, 150, 150);
            box.GotFocus += (s, e) =>
            {
                if (box.Text == placeholder)
                {
                    box.Text = "";
                    box.ForeColor = SystemColors.WindowText;
                }
            };
            box.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(box.Text))
                {
                    box.Text = placeholder;
                    box.ForeColor = Color.FromArgb(150, 150, 150);
                }
            };
        }

        /// <summary>返回搜索关键字（去除占位符文本）；无有效关键字返回 null。</summary>
        private string GetSearchKeyword()
        {
            if (searchBox == null) return null;
            string text = searchBox.Text;
            if (text == "搜索文件名..." || string.IsNullOrWhiteSpace(text)) return null;
            return text.Trim();
        }

        /// <summary>按当前排序模式对任务列表排序后返回新列表。</summary>
        private List<ConversionTask> ApplySort(IEnumerable<ConversionTask> source)
        {
            var list = source.ToList();
            switch (_sortMode)
            {
                case 1: // 文件名 A-Z
                    list.Sort((a, b) => string.Compare(Path.GetFileName(a.InputPath), Path.GetFileName(b.InputPath), StringComparison.OrdinalIgnoreCase));
                    break;
                case 2: // 文件名 Z-A
                    list.Sort((a, b) => -string.Compare(Path.GetFileName(a.InputPath), Path.GetFileName(b.InputPath), StringComparison.OrdinalIgnoreCase));
                    break;
                case 3: // 文件大小↓
                    list.Sort((a, b) => GetFileLength(b.InputPath).CompareTo(GetFileLength(a.InputPath)));
                    break;
                case 4: // 时长↓
                    list.Sort((a, b) => b.SourceDurationSeconds.CompareTo(a.SourceDurationSeconds));
                    break;
            }
            return list;
        }

        /// <summary>安全获取文件大小（字节）；文件不存在返回 0。</summary>
        private static long GetFileLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch { return 0; }
        }

        /// <summary>当前「保存到」选中的实际目录；"与源文件夹相同" 或占位项返回 null。</summary>
        private string GetSelectedSaveToFolder()
        {
            string sel = saveToCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(sel) ||
                string.Equals(sel, "与源文件夹相同", StringComparison.Ordinal) ||
                string.Equals(sel, "选择文件夹...", StringComparison.Ordinal))
                return null;
            return sel;
        }

        /// <summary>缓存当前 ffmpeg 版本字符串（供"更新 ffmpeg"对话框使用）。</summary>
        private string _ffmpegVersionCache;

        /// <summary>后台读取本地 ffmpeg 版本到缓存，不更新任何 UI。</summary>
        private async Task LoadFfmpegVersionAsync()
        {
            try
            {
                _ffmpegVersionCache = await FFmpegHelper.GetInstalledVersionAsync();
            }
            catch
            {
                _ffmpegVersionCache = null;
            }
        }

        /// <summary>在顶部工具栏添加"工具"按钮（GIF 制作器等）。</summary>
        private void SetupToolsButton()
        {
            var btnTools = new Button
            {
                Location = new Point(600, 12),
                Size = new Size(80, 32),
                Text = "工具",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(80, 80, 80),
                Font = new Font("Microsoft YaHei UI", 9F),
                Cursor = Cursors.Hand
            };
            btnTools.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            btnTools.Click += (s, e) =>
            {
                var ctx = new ContextMenuStrip();
                ctx.Items.Add("GIF 制作器", null, (s2, e2) =>
                {
                    using (var dlg = new GifMakerForm())
                        dlg.ShowDialog(this);
                });
                ctx.Items.Add("批量编辑器", null, (s2, e2) =>
                {
                    // 批量编辑器只作用于"正在转换"列表（非已完成）。
                    var pending = _tasks.Where(t => t.Status != TaskStatus.Completed).ToList();
                    using (var dlg = new BatchEditForm(pending))
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK)
                            RefreshTaskList();
                    }
                });
                ctx.Items.Add("-");
                ctx.Items.Add("更新 ffmpeg", null, (s2, e2) =>
                {
                    using (var dlg = new FfmpegUpdateForm(_ffmpegVersionCache ?? string.Empty))
                        dlg.ShowDialog(this);
                    _ = LoadFfmpegVersionAsync();
                });
                ctx.Show(btnTools, new Point(0, btnTools.Height));
            };
            topPanel.Controls.Add(btnTools);
        }

        #endregion

        #region File handling

        private async void AddFilesButton_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog(this) != DialogResult.OK) return;
            await AddFiles(openFileDialog.FileNames);
        }

        #region 添加文件夹

        // 支持递归扫描的媒体扩展名白名单（视频 / 音频 / 图片序列）。
        private static readonly HashSet<string> MediaExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 视频
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".vob", ".ogv", ".3gp", ".rm", ".rmvb",
            // 音频
            ".mp3", ".aac", ".wav", ".flac", ".ogg", ".wma", ".m4a", ".ac3", ".opus", ".aiff",
            // 图片（序列）
            ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".tif", ".webp", ".gif"
        };

        private async void AddFolderButton_Click(object sender, EventArgs e)
        {
            if (folderBrowserDialog.ShowDialog(this) != DialogResult.OK) return;
            string folder = folderBrowserDialog.SelectedPath;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            var files = new List<string>();
            try
            {
                ScanFolderForMedia(folder, files);
            }
            catch
            {
                // 扫描出错时忽略，用已收集到的文件继续。
            }

            if (files.Count == 0)
            {
                MessageBox.Show(this, "所选文件夹中未找到任何媒体文件。",
                    "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            addFolderButton.Enabled = false;
            addFilesButton.Enabled = false;
            try
            {
                await AddFiles(files.ToArray());
            }
            finally
            {
                addFolderButton.Enabled = true;
                addFilesButton.Enabled = true;
            }
        }

        /// <summary>递归扫描目录收集媒体文件，跳过隐藏/系统子目录。</summary>
        private static void ScanFolderForMedia(string dir, List<string> result)
        {
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                string ext = Path.GetExtension(file);
                if (MediaExtensions.Contains(ext))
                    result.Add(file);
            }

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var info = new DirectoryInfo(sub);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    continue;
                ScanFolderForMedia(sub, result);
            }
        }

        #endregion

        /// <summary>Windows 下统一将路径分隔符规范化为反斜杠，空值原样返回。</summary>
        private static string NormalizeBackslash(string path)
        {
            return string.IsNullOrEmpty(path) ? path : path.Replace('/', '\\');
        }

        /// <summary>
        /// Shared add routine used by the file dialog AND drag-and-drop.
        /// </summary>
        private async Task AddFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;

            // Windows 下 ffmpeg 要求路径分隔符为反斜杠；在添加入口统一规范化，
            // 确保后续重复判断、ffprobe 探测、ffmpeg 调用都使用一致的路径格式。
            for (int i = 0; i < files.Length; i++)
            {
                if (!string.IsNullOrEmpty(files[i]))
                    files[i] = files[i].Replace('/', '\\');
            }

            var preset = _globalPreset ?? PresetOption.MP4_1080;

            // ---- Phase 1a：创建任务（占位元数据，不碰 UI）----
            // 旧实现把所有文件的 ffprobe + 缩略图串行探测完才一次性 RefreshTaskList，
            // 导致添加文件时列表长时间无反馈、看起来"很慢"，且必须切换页签才看得到。
            var newTasks = new List<ConversionTask>();
            foreach (string file in files)
            {
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;
                // 重复判断只针对"正在转换"列表（非 Completed），允许已完成文件再次添加。
                if (_tasks.Any(t => t.Status != TaskStatus.Completed &&
                        string.Equals(t.InputPath, file, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var task = new ConversionTask
                {
                    InputPath = file,
                    Preset = preset,
                    SaveToFolder = GetSelectedSaveToFolder(),
                    // 先用占位元数据立即显示卡片，探测完成后原地更新。
                    SourceFormat = Path.GetExtension(file).TrimStart('.').ToUpperInvariant(),
                    SourceResolution = "-",
                    SourceFileSize = FFmpegHelper.FormatFileSize(new FileInfo(file).Length),
                    SourceDurationSeconds = 0,
                    SourceDuration = "00:00:00",
                    SourcePixelFormat = "-",
                    SourceFrameRate = "-",
                    EstimatedTargetSize = "-",
                    Thumbnail = CreatePlaceholderThumbnail(),
                    AudioTracks = new System.Collections.Generic.List<AudioTrackInfo>(),
                    SubtitleTracks = new System.Collections.Generic.List<SubtitleTrackInfo>()
                };

                _tasks.Add(task);
                newTasks.Add(task);
            }

            if (newTasks.Count == 0) return;

            // ---- Phase 1b：若当前停留在"转换完成"页签，自动切回"正在转换"并清空旧卡片 ----
            // 新文件都属于"正在转换"列表，切回该页签让它们立即可见，无需手动切换。
            if (_showCompleted)
            {
                _showCompleted = false;
                UpdateTabStyles();
                RefreshTaskList();
            }

            // ---- Phase 1c：增量显示新卡片，即时反馈 ----
            foreach (var t in newTasks) AppendTaskCard(t);

            // ---- Phase 2：并行探测媒体信息 + 生成缩略图，逐卡原地更新 ----
            // 每个文件的 ffprobe/ffmpeg 都是独立进程，互不干扰，可并行以大幅缩短总耗时。
            // 并发上限由 FFmpegHelper 全局信号量统一约束（ProbeDetailedAsync / GetThumbnailAsync
            // 内部已接入 FFmpegHelper.MaxParallelFfmpeg），此处不再单独加锁以免重复占用槽位死锁。
            var probes = newTasks.Select(t => ProbeAndPopulateTaskAsync(t)).ToArray();
            await Task.WhenAll(probes);

            // 自定义排序 / 搜索过滤激活时，增量追加可能破坏顺序或过滤，做一次全量重建。
            if (_sortMode != 0 || GetSearchKeyword() != null)
                RefreshTaskList();
        }

        /// <summary>
        /// 对单个任务执行 ffprobe 探测 + 外挂字幕检测 + 缩略图生成，
        /// 完成后回到 UI 线程原地刷新对应卡片。在添加文件 Phase 2 中并行调用。
        /// </summary>
        private async Task ProbeAndPopulateTaskAsync(ConversionTask task)
        {
            MediaInfo info = null;
            try
            {
                info = await FFmpegHelper.ProbeDetailedAsync(task.InputPath).ConfigureAwait(false);
                task.SourceFormat = string.IsNullOrEmpty(info.VideoCodec)
                    ? Path.GetExtension(task.InputPath).TrimStart('.').ToUpperInvariant()
                    : info.VideoCodec.ToUpperInvariant();
                task.SourceResolution = info.Width > 0 && info.Height > 0
                    ? string.Format("{0} x {1}", info.Width, info.Height)
                    : "-";
                task.SourceFileSize = info.SizeBytes > 0
                    ? FFmpegHelper.FormatFileSize(info.SizeBytes)
                    : FFmpegHelper.FormatFileSize(new FileInfo(task.InputPath).Length);
                task.SourceDurationSeconds = info.DurationSeconds;
                task.SourceDuration = FFmpegHelper.FormatDuration(info.DurationSeconds);
                task.SourcePixelFormat = !string.IsNullOrEmpty(info.PixelFormat) ? info.PixelFormat : "-";
                task.SourceFrameRate = info.NominalFrameRate > 0
                    ? info.NominalFrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps"
                    : "-";
                task.AudioTracks = info.AudioTracks ?? new System.Collections.Generic.List<AudioTrackInfo>();
                task.SubtitleTracks = info.SubtitleTracks ?? new System.Collections.Generic.List<SubtitleTrackInfo>();
                task.SelectedAudioTrack = task.AudioTracks.Count > 0 ? task.AudioTracks[0] : null;
                task.SelectedSubtitleTrack = task.SubtitleTracks.Count > 0 ? task.SubtitleTracks[0] : null;
                task.EstimatedTargetSize = EstimateTargetSize(info, task.Preset);
            }
            catch
            {
                // 保持占位元数据（扩展名/文件大小），其余为 "-"/"00:00:00"。
                task.SourceFormat = Path.GetExtension(task.InputPath).TrimStart('.').ToUpperInvariant();
                task.SourceResolution = "-";
                task.SourceFileSize = FFmpegHelper.FormatFileSize(new FileInfo(task.InputPath).Length);
                task.SourceDurationSeconds = 0;
                task.SourceDuration = "00:00:00";
                task.SourcePixelFormat = "-";
                task.SourceFrameRate = "-";
                task.EstimatedTargetSize = "-";
            }

            // Detect external subtitle files regardless of ffprobe success.
            var externalSubs = FFmpegHelper.FindExternalSubtitles(task.InputPath);
            if (externalSubs.Count > 0)
            {
                task.SubtitleTracks.AddRange(externalSubs);
                if (task.SelectedSubtitleTrack == null)
                    task.SelectedSubtitleTrack = externalSubs[0];
            }

            // Thumbnail at 1s.
            try
            {
                task.Thumbnail = await FFmpegHelper.GetThumbnailAsync(task.InputPath, 160, 90).ConfigureAwait(false)
                    ?? CreatePlaceholderThumbnail();
            }
            catch
            {
                // 保留占位缩略图。
            }

            // 回到 UI 线程原地刷新卡片内容（探测完成状态）。
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => UpdateTaskCardContent(task)));
        }

        private string EstimateTargetSize(MediaInfo info, PresetOption preset)
        {
            try
            {
                double seconds = info.DurationSeconds;
                if (seconds <= 0) return "-";

                long videoBps = ResolveVideoBitRate(info, preset);
                long audioBps = ResolveAudioBitRate(info, preset);
                if (videoBps <= 0 && audioBps <= 0) return "-";

                long totalBytes = (long)((videoBps + audioBps) * seconds / 8);
                return FFmpegHelper.FormatFileSize(totalBytes);
            }
            catch { return "-"; }
        }

        /// <summary>
        /// 估算目标视频码率：显式码率优先；auto/copy 时用分辨率兜底。
        /// </summary>
        private long ResolveVideoBitRate(MediaInfo info, PresetOption preset)
        {
            long br = ParseBitRate(preset.VideoBitrate);
            if (br > 0) return br;

            // copy / auto：按目标分辨率给经验值（与源文件相同 preset 用源分辨率）。
            int w = 0, h = 0;
            if (!string.IsNullOrEmpty(preset.ResolutionValue) &&
                preset.ResolutionValue.Contains("x"))
            {
                var parts = preset.ResolutionValue.Split('x');
                int.TryParse(parts[0], out w);
                int.TryParse(parts[1], out h);
            }
            if (w <= 0 || h <= 0)
            {
                w = info.Width;
                h = info.Height;
            }
            return EstimateVideoBitRateByResolution(w, h);
        }

        private long EstimateVideoBitRateByResolution(int width, int height)
        {
            long pixels = (long)width * height;
            if (pixels <= 0) return 0;
            if (pixels >= 3840 * 2160) return 20_000_000; // 4K
            if (pixels >= 1920 * 1080) return 8_000_000;  // 1080p
            if (pixels >= 1280 * 720) return 4_000_000;   // 720p
            if (pixels >= 854 * 480) return 2_000_000;    // 480p
            return 1_000_000;
        }

        /// <summary>
        /// 估算目标音频码率：显式码率优先；auto/copy 时用默认码率或源音轨码率兜底。
        /// </summary>
        private long ResolveAudioBitRate(MediaInfo info, PresetOption preset)
        {
            long br = ParseBitRate(preset.AudioBitrate);
            if (br > 0) return br;

            if (string.Equals(preset.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                var src = info.AudioTracks.FirstOrDefault();
                if (src != null) br = ParseBitRate(src.BitRate);
            }
            else
            {
                string def = DefaultCodecSettings.GetAudioDefaultBitrate(preset.AudioCodec);
                br = ParseBitRate(def);
            }
            return br > 0 ? br : 192_000;
        }

        private long ParseBitRate(string bitrate)
        {
            if (string.IsNullOrWhiteSpace(bitrate)) return 0;
            string s = bitrate.Trim().ToLowerInvariant();
            double value;
            if (s.EndsWith("k"))
            {
                if (double.TryParse(s.Substring(0, s.Length - 1), out value))
                    return (long)(value * 1000);
            }
            else if (s.EndsWith("m"))
            {
                if (double.TryParse(s.Substring(0, s.Length - 1), out value))
                    return (long)(value * 1000000);
            }
            else if (double.TryParse(s, out value))
            {
                return (long)value;
            }
            return 0;
        }

        private Image CreatePlaceholderThumbnail()
        {
            var bmp = new Bitmap(160, 90);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(224, 218, 240));
                using (var pen = new Pen(Color.FromArgb(180, 170, 210), 2))
                {
                    g.DrawRectangle(pen, 2, 2, 155, 85);
                }
            }
            return bmp;
        }

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            // 清理动作按当前页签过滤：转换完成页签只清完成列表，正在转换页签只清未完成列表。
            var ctx = new ContextMenuStrip();

            if (_showCompleted)
            {
                // 转换完成页签：仅清除已完成任务。
                int doneCount = _tasks.Count(t => t.Status == TaskStatus.Completed);
                var itemDone = ctx.Items.Add(string.Format("清除已完成 ({0})", doneCount));
                itemDone.Enabled = doneCount > 0;
                itemDone.Click += (s, e) => ClearTasksByStatus(TaskStatus.Completed, "已完成");

                if (doneCount == 0)
                {
                    MessageBox.Show(this, "转换完成列表为空。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }
            else
            {
                // 正在转换页签：清除失败 / 清空未完成（不含已完成）。
                int failedCount = _tasks.Count(t => t.Status == TaskStatus.Failed);
                int pendingCount = _tasks.Count(t => t.Status == TaskStatus.Pending || t.Status == TaskStatus.Converting);

                var itemFailed = ctx.Items.Add(string.Format("清除失败 ({0})", failedCount));
                itemFailed.Enabled = failedCount > 0;
                itemFailed.Click += (s, e) => ClearTasksByStatus(TaskStatus.Failed, "失败");

                ctx.Items.Add("-");

                var itemAll = ctx.Items.Add(string.Format("清空未完成 ({0})", pendingCount));
                itemAll.Enabled = pendingCount > 0;
                itemAll.Click += (s, e) => ClearPendingTasks();

                if (pendingCount == 0 && failedCount == 0)
                {
                    MessageBox.Show(this, "正在转换列表为空。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            ctx.Show(deleteButton, new Point(0, deleteButton.Height));
        }

        /// <summary>清空所有未完成任务（正在转换/待处理/失败），不影响已完成任务。</summary>
        private void ClearPendingTasks()
        {
            var toRemove = _tasks.Where(t => t.Status != TaskStatus.Completed).ToList();
            if (toRemove.Count == 0) return;
            if (MessageBox.Show(this, "确定要清空正在转换列表中的所有文件吗？（不影响已完成列表）", "清空确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            foreach (var t in toRemove)
            {
                if (t.Status == TaskStatus.Converting && t.Cancellation != null)
                {
                    try { t.Cancellation.Cancel(); } catch { }
                }
                if (t.Thumbnail != null) t.Thumbnail.Dispose();
                _tasks.Remove(t);
            }
            if (_selectedCard != null && _selectedCard.IsDisposed) _selectedCard = null;
            RefreshTaskList();
        }

        /// <summary>清除指定状态的任务（含缩略图释放）。</summary>
        private void ClearTasksByStatus(TaskStatus status, string label)
        {
            var toRemove = _tasks.Where(t => t.Status == status).ToList();
            if (toRemove.Count == 0) return;
            foreach (var t in toRemove)
            {
                if (t.Thumbnail != null) t.Thumbnail.Dispose();
                _tasks.Remove(t);
            }
            if (_selectedCard != null && _selectedCard.IsDisposed) _selectedCard = null;
            RefreshTaskList();
        }

        #endregion

        #region Task cards

        private void RefreshTaskList()
        {
            // 重建列表前清理拖拽残留（指示线、拖拽引用）。
            ClearInsertionLine();
            _dragCard = null;

            // 批量重建时挂起布局，减少闪烁和重绘开销。
            taskListPanel.SuspendLayout();
            // Dispose old cards.
            // 注意：先清空缩略图引用，避免 PictureBox.Dispose 把"共享的 task.Thumbnail"
            // 一起 Dispose，导致下次重建卡片时把已释放的图像交给 PictureBox，
            // 触发 ImageAnimator.Animate → Image.FrameDimensionsList 抛"参数无效"崩溃
            // （切换页签重建列表时必现）。#95
            foreach (var card in _cards)
            {
                if (card.ThumbnailBox != null && !card.ThumbnailBox.IsDisposed)
                    card.ThumbnailBox.Image = null;
                card.Panel.Dispose();
            }
            _cards.Clear();
            _selectedCard = null;
            taskListPanel.Controls.Clear();

            var visible = _showCompleted
                ? _tasks.Where(t => t.Status == TaskStatus.Completed)
                : _tasks.Where(t => t.Status != TaskStatus.Completed);

            // 搜索过滤：按输入文件名模糊匹配（不区分大小写）。
            string keyword = GetSearchKeyword();
            if (!string.IsNullOrEmpty(keyword))
            {
                visible = visible.Where(t => Path.GetFileName(t.InputPath)
                    .IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            // 排序。
            var sorted = ApplySort(visible);

            foreach (var task in sorted)
            {
                TaskCard card = (_showCompleted && task.Status == TaskStatus.Completed)
                    ? BuildCompletedCard(task)
                    : BuildTaskCard(task);
                _cards.Add(card);
                taskListPanel.Controls.Add(card.Panel);
            }

            if (_cards.Count == 0)
                ShowEmptyState();
            else
                HideEmptyState();

            UpdateCount();
            taskListPanel.ResumeLayout();
        }

        /// <summary>
        /// 按任务查找当前可见列表中的卡片（不依赖闭包捕获的旧引用）。
        /// 转换过程中列表可能被重建（如切换页签），必须用此方法取"当前"卡片，
        /// 否则进度/状态会写到已释放的旧卡片上。#95
        /// </summary>
        private TaskCard GetCard(ConversionTask task)
        {
            if (task == null) return null;
            return _cards.FirstOrDefault(c => c.Task == task);
        }

        /// <summary>
        /// 局部更新单张卡片的状态标签和进度条，不重建整个列表。
        /// 用于转换完成/失败等场景，避免全量 RefreshTaskList 导致焦点丢失。
        /// </summary>
        private void RefreshSingleCard(ConversionTask task)
        {
            var card = _cards.FirstOrDefault(c => c.Task == task);
            if (card == null || card.Panel.IsDisposed) return;
            card.StatusLabel.Text = task.StatusMessage ?? task.Status.ToString();
            if (task.Status == TaskStatus.Completed)
                card.ProgressBar.Value = 100;
            else if (task.Status == TaskStatus.Failed)
                card.ProgressBar.Value = 0;
        }

        /// <summary>
        /// 增量追加一张任务卡片（不重建整个列表），用于添加文件时即时显示。
        /// 新任务先以占位元数据显示，并给出"探测媒体信息..."状态提示。
        /// </summary>
        private void AppendTaskCard(ConversionTask task)
        {
            // 只追加到当前可见列表：正在转换页签显示未完成任务，完成页签显示已完成任务。
            bool shouldShow = _showCompleted
                ? task.Status == TaskStatus.Completed
                : task.Status != TaskStatus.Completed;
            if (!shouldShow)
            {
                UpdateCount();
                return;
            }

            HideEmptyState();
            var card = BuildTaskCard(task);
            card.Panel.Tag = task;
            _cards.Add(card);
            taskListPanel.Controls.Add(card.Panel);

            // 探测期间给出可见反馈，探测完成后由 UpdateTaskCardContent 隐藏。
            if (card.StatusLabel != null && !card.StatusLabel.IsDisposed)
            {
                card.StatusLabel.Text = "探测媒体信息...";
                card.StatusLabel.ForeColor = Color.FromArgb(120, 90, 200);
                card.StatusLabel.Visible = true;
            }

            UpdateCount();
        }

        /// <summary>
        /// 原地更新单张卡片的源媒体信息、缩略图与音/字幕按钮文案（不重建列表）。
        /// 用于添加文件后 ffprobe / 缩略图完成时刷新对应卡片。
        /// </summary>
        private void UpdateTaskCardContent(ConversionTask task)
        {
            var card = _cards.FirstOrDefault(c => c.Task == task);
            if (card == null || card.Panel.IsDisposed) return;

            if (card.SourceFormatLabel != null && !card.SourceFormatLabel.IsDisposed)
                card.SourceFormatLabel.Text = "格式: " + task.SourceFormat;
            if (card.SourceResolutionLabel != null && !card.SourceResolutionLabel.IsDisposed)
                card.SourceResolutionLabel.Text = "分辨率: " + task.SourceResolution;
            if (card.SourceSizeLabel != null && !card.SourceSizeLabel.IsDisposed)
                card.SourceSizeLabel.Text = "大小: " + task.SourceFileSize;
            if (card.SourceDurationLabel != null && !card.SourceDurationLabel.IsDisposed)
                card.SourceDurationLabel.Text = "时长: " + task.SourceDuration;
            if (card.SourcePixelFormatLabel != null && !card.SourcePixelFormatLabel.IsDisposed)
                card.SourcePixelFormatLabel.Text = "像素格式: " + (task.SourcePixelFormat ?? "-");
            if (card.SourceFrameRateLabel != null && !card.SourceFrameRateLabel.IsDisposed)
                card.SourceFrameRateLabel.Text = "帧率: " + (task.SourceFrameRate ?? "-");
            if (card.TargetSizeLabel != null && !card.TargetSizeLabel.IsDisposed)
                card.TargetSizeLabel.Text = "预计大小: " + (task.EstimatedTargetSize ?? "-");

            // 占位缩略图 → 真实缩略图。
            if (card.ThumbnailBox != null && !card.ThumbnailBox.IsDisposed)
            {
                var old = card.ThumbnailBox.Image;
                card.ThumbnailBox.Image = task.Thumbnail;
                // 仅当 old 不是 task.Thumbnail 本身时才 Dispose，避免误释放被各卡片
                // 共用的共享缩略图引用（见 RefreshTaskList 注释）。#95
                if (old != null && old != task.Thumbnail) old.Dispose();
                card.ThumbnailBox.Invalidate();
            }

            // 音轨/字幕轨探测完成后刷新按钮文案。
            if (card.SubtitleButton != null && !card.SubtitleButton.IsDisposed)
                RefreshSubtitleButtonText(card.SubtitleButton, task);
            if (card.AudioButton != null && !card.AudioButton.IsDisposed)
                RefreshAudioButtonText(card.AudioButton, task);

            // 隐藏"探测媒体信息..."状态。
            if (card.StatusLabel != null && !card.StatusLabel.IsDisposed)
                card.StatusLabel.Visible = false;
        }

        /// <summary>创建（仅一次）空状态引导标签，并随 panel 宽度自适应。</summary>
        private void EnsureEmptyStateLabel()
        {
            if (_emptyStateLabel != null) return;
            _emptyStateLabel = new Label
            {
                Name = "emptyStateLabel",
                Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular),
                ForeColor = Color.FromArgb(160, 160, 160),
                TextAlign = ContentAlignment.MiddleCenter,
                Height = 60,
                Margin = new Padding(0, 120, 0, 0),
                Cursor = Cursors.Default
            };
        }

        private void ShowEmptyState()
        {
            EnsureEmptyStateLabel();
            _emptyStateLabel.Text = _showCompleted
                ? "尚无已完成的转换任务"
                : "拖拽文件到此处，或点击「添加文件」/「添加文件夹」按钮开始转换";
            _emptyStateLabel.Width = taskListPanel.ClientSize.Width - 32;
            if (!taskListPanel.Controls.Contains(_emptyStateLabel))
                taskListPanel.Controls.Add(_emptyStateLabel);
            _emptyStateLabel.Visible = true;
        }

        private void HideEmptyState()
        {
            if (_emptyStateLabel != null && !_emptyStateLabel.IsDisposed)
                _emptyStateLabel.Visible = false;
        }

        private TaskCard BuildTaskCard(ConversionTask task)
        {
            int cardW = Math.Max(900, taskListPanel.ClientSize.Width - 40);
            int cardH = 158;

            var cardPanel = new RoundedPanel
            {
                Width = cardW,
                Height = cardH,
                Margin = new Padding(0, 0, 0, 12),
                FillColor = Color.FromArgb(245, 242, 252),
                BorderColor = Color.FromArgb(215, 207, 235)
            };

            // Thumbnail.
            var thumb = new PictureBox
            {
                Location = new Point(12, 12),
                Size = new Size(150, 84),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = task.Thumbnail,
                BackColor = Color.FromArgb(235, 231, 247)
            };
            cardPanel.Controls.Add(thumb);

            // Play overlay — hidden until the thumbnail is hovered, then shown
            // centered over the preview. Clicking it (or the thumbnail) opens
            // the OpenGL preview window. #50
            var playOverlay = new Label
            {
                Location = new Point(thumb.Left + 55, thumb.Top + 22),
                Size = new Size(40, 40),
                BackColor = Color.FromArgb(124, 77, 255),
                Text = "▶",
                Font = new Font("Microsoft YaHei UI", 16F, FontStyle.Regular),
                ForeColor = Color.White,
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };
            cardPanel.Controls.Add(playOverlay);
            playOverlay.BringToFront();

            // Preview behaviour: show the play button on hover, open the OpenGL
            // preview window on click. #50
            void ShowOverlay(bool show)
            {
                if (!playOverlay.IsDisposed) playOverlay.Visible = show;
            }
            thumb.MouseEnter += (s, e) => ShowOverlay(true);
            thumb.MouseLeave += (s, e) => ShowOverlay(false);
            thumb.Click += (s, e) =>
            {
                SelectCard(cardPanel);
                OpenPlayer(task);
            };
            playOverlay.MouseEnter += (s, e) => ShowOverlay(true);
            playOverlay.MouseLeave += (s, e) => ShowOverlay(false);
            playOverlay.Click += (s, e) =>
            {
                SelectCard(cardPanel);
                OpenPlayer(task);
            };

            // ---- Input column ----
            int inputX = 174;
            int row1Y = 12;
            int row2Y = 36;
            int row3Y = 60;
            int row4Y = 88;

            // Input file name: width for ~15 Chinese chars.
            string inFileName = Path.GetFileName(task.InputPath);
            var lblInName = new Label
            {
                Location = new Point(inputX, row1Y),
                Size = new Size(250, 22),
                Text = inFileName,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left
            };
            toolTip.SetToolTip(lblInName, inFileName);
            cardPanel.Controls.Add(lblInName);

            var lblInFormat = AddInfoLabel(cardPanel, inputX, row2Y, "格式: " + task.SourceFormat, 110);
            var lblInResolution = AddInfoLabel(cardPanel, inputX + 110, row2Y, "分辨率: " + task.SourceResolution, 110);
            var lblInPixelFmt = AddInfoLabel(cardPanel, inputX + 220, row2Y, "像素格式: " + (task.SourcePixelFormat ?? "-"), 140);
            var lblInSize = AddInfoLabel(cardPanel, inputX, row3Y, "大小: " + task.SourceFileSize, 110);
            var lblInDuration = AddInfoLabel(cardPanel, inputX + 110, row3Y, "时长: " + task.SourceDuration, 110);
            var lblInFrameRate = AddInfoLabel(cardPanel, inputX + 220, row3Y, "帧率: " + (task.SourceFrameRate ?? "-"), 140);

            // Edit icon on input row 4 -> themed button (light fill, dark text,
            // dark border). #48
            var btnEditVideo = CreateThemeButton("✎", inputX, row4Y, "视频编辑");
            btnEditVideo.Click += (s, e) => OpenVideoEdit(task);
            cardPanel.Controls.Add(btnEditVideo);

            // ---- Output column ----
            int outputX = 460;
            int outputW = Math.Max(200, cardW - outputX - 120); // leave room for convert button

            // Output file name + edit icon + delete icon.
            var lblOutName = new Label
            {
                Location = new Point(outputX, row1Y),
                Size = new Size(outputW - 60, 22),
                Text = task.GetOutputFileName(),
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            toolTip.SetToolTip(lblOutName, task.OutputPath);
            cardPanel.Controls.Add(lblOutName);

            var txtOutName = new TextBox
            {
                Location = new Point(outputX, row1Y - 1),
                Size = new Size(outputW - 60, 23),
                Text = string.IsNullOrWhiteSpace(task.CustomOutputName)
                    ? Path.GetFileNameWithoutExtension(task.OutputPath)
                    : task.CustomOutputName,
                Visible = false,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Regular),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            cardPanel.Controls.Add(txtOutName);

            Button btnEditName = null;
            btnEditName = CreateThemeButton("✎", outputX + outputW - 54, row1Y - 2, "修改文件名");
            btnEditName.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnEditName.Click += (s, e) =>
            {
                if (txtOutName.Visible)
                {
                    // Already editing; explicit save via Enter/LostFocus.
                    cardPanel.Focus();
                }
                else
                {
                    ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
                }
            };
            cardPanel.Controls.Add(btnEditName);

            txtOutName.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
                else if (e.KeyCode == Keys.Escape)
                {
                    txtOutName.Text = string.IsNullOrWhiteSpace(task.CustomOutputName)
                        ? Path.GetFileNameWithoutExtension(task.OutputPath)
                        : task.CustomOutputName;
                    ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
                }
            };
            txtOutName.LostFocus += (s, e) =>
            {
                if (txtOutName.Visible) ToggleOutputNameEdit(task, lblOutName, txtOutName, btnEditName);
            };

            // Delete icon top-right — anchored to right edge.
            var btnDelete = CreateIconButton("🗑", cardW - 42, 8, "删除此文件");
            btnDelete.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnDelete.ForeColor = Color.FromArgb(180, 80, 80);
            btnDelete.Click += (s, e) =>
            {
                if (task.Status == TaskStatus.Converting && task.Cancellation != null)
                {
                    try { task.Cancellation.Cancel(); } catch { }
                }
                _tasks.Remove(task);
                if (task.Thumbnail != null) task.Thumbnail.Dispose();
                RefreshTaskList();
            };
            cardPanel.Controls.Add(btnDelete);

            var lblOutFormat = AddInfoLabel(cardPanel, outputX, row2Y, "格式: " + task.TargetFormat, 110);
            var lblOutResolution = AddInfoLabel(cardPanel, outputX + 110, row2Y, "分辨率: " + task.TargetResolution, 130);
            var lblOutSize = AddInfoLabel(cardPanel, outputX, row3Y, "预计大小: " + (task.EstimatedTargetSize ?? "-"), 130);
            var lblOutDuration = AddInfoLabel(cardPanel, outputX + 130, row3Y, "输出时长: " + task.TargetDuration, 130);

            // Row 4: preset selector (white bordered panel, styled like the
            // bottom "转换到" control) + subtitle + audio. #45
            int r4x = outputX;
            var presetPanel = new Panel
            {
                Location = new Point(r4x, row4Y - 1),
                Size = new Size(220, 26),
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var btnPreset = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                Text = string.Format("{0} / {1}", task.Preset.FormatName, task.Preset.Name),
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45),
                UseVisualStyleBackColor = false
            };
            btnPreset.FlatAppearance.BorderSize = 0;
            var btnPresetGear = new Button
            {
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                Text = "⚙",
                Size = new Size(26, 26),
                Font = new Font("Microsoft YaHei UI", 10F),
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45),
                UseVisualStyleBackColor = false
            };
            btnPresetGear.FlatAppearance.BorderSize = 0;
            btnPreset.Click += (s, e) => OpenPresetSelection(task, btnPreset, lblOutFormat, lblOutResolution, lblOutSize);
            btnPresetGear.Click += (s, e) => OpenPresetEdit(task, btnPreset, lblOutFormat, lblOutResolution, lblOutSize);
            presetPanel.Controls.Add(btnPreset);
            presetPanel.Controls.Add(btnPresetGear);
            cardPanel.Controls.Add(presetPanel);

            // 字幕按钮：点击弹出 PopupSubtitlePicker。
            // 比 ComboBox 更直观地展示三个模式 radio + 外挂字幕单选。
            var btnSubtitle = CreateSubtitleButton(task, r4x + 232, row4Y, 110);
            cardPanel.Controls.Add(btnSubtitle);

            var btnAudio = CreateAudioButton(task, r4x + 352, row4Y, 190);
            cardPanel.Controls.Add(btnAudio);

            // ---- Convert / Cancel button — right-aligned, height matches convertAllButton (40px) ----
            var btnConvert = new Button
            {
                Location = new Point(cardW - 102, 38),
                Size = new Size(90, 40),
                Text = "转换",
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                BackColor = Color.FromArgb(124, 77, 255),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            btnConvert.FlatAppearance.BorderSize = 0;
            cardPanel.Controls.Add(btnConvert);

            // ---- Progress bar at bottom ----
            var progress = new ProgressBar
            {
                Location = new Point(12, cardH - 18),
                Size = new Size(cardW - 24, 10),
                Maximum = 100,
                Value = 0,
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            cardPanel.Controls.Add(progress);

            var lblStatus = new Label
            {
                Location = new Point(12, cardH - 36),
                Size = new Size(cardW - 140, 16),
                Text = "",
                Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Regular),
                ForeColor = Color.FromArgb(120, 90, 200),
                BackColor = Color.Transparent,
                Visible = false,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            cardPanel.Controls.Add(lblStatus);

            var card = new TaskCard
            {
                Task = task,
                Panel = cardPanel,
                PresetButton = btnPreset,
                PresetGearButton = btnPresetGear,
                SubtitleCombo = null, // 改用 SubtitleButton + PopupSubtitlePicker
                SubtitleButton = btnSubtitle,
                AudioButton = btnAudio,
                ThumbnailBox = thumb,
                ConvertButton = btnConvert,
                ProgressBar = progress,
                StatusLabel = lblStatus,
                OutputNameLabel = lblOutName,
                OutputNameEdit = txtOutName,
                SourceFormatLabel = lblInFormat,
                SourceResolutionLabel = lblInResolution,
                SourceSizeLabel = lblInSize,
                SourceDurationLabel = lblInDuration,
                SourcePixelFormatLabel = lblInPixelFmt,
                SourceFrameRateLabel = lblInFrameRate,
                TargetFormatLabel = lblOutFormat,
                TargetResolutionLabel = lblOutResolution,
                TargetSizeLabel = lblOutSize,
                TargetDurationLabel = lblOutDuration
            };

            // 若任务正在转换中（例如切换页签触发列表重建），恢复进度条/状态/按钮的
            // 实时状态，否则新建卡片会丢失进度条显示、按钮仍显示"转换"而非"取消"。#95
            if (task.Status == TaskStatus.Converting)
            {
                card.IsConverting = true;
                card.ProgressBar.Visible = true;
                card.StatusLabel.Visible = true;
                card.ConvertButton.Text = "取消";
            }

            // Wire convert button after card is fully built.
            btnConvert.Click += (s, e) => ConvertSingleTask(task, card);

            // Hover / click visual feedback on the whole card.
            WireCardHover(cardPanel, task);

            return card;
        }

        /// <summary>
        /// Build a read-only card for an already-converted file. It shows only the
        /// output preview, output file name, format, resolution, size, duration and
        /// audio tracks — no edit controls, dropdowns or convert button. #93
        /// </summary>
        private TaskCard BuildCompletedCard(ConversionTask task)
        {
            int cardW = Math.Max(900, taskListPanel.ClientSize.Width - 40);
            int cardH = 158;

            var cardPanel = new RoundedPanel
            {
                Width = cardW,
                Height = cardH,
                Margin = new Padding(0, 0, 0, 12),
                FillColor = Color.FromArgb(240, 247, 240),
                BorderColor = Color.FromArgb(205, 225, 205)
            };

            // Output preview thumbnail (click to open the result file).
            var thumb = new PictureBox
            {
                Location = new Point(12, 12),
                Size = new Size(150, 84),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = task.Thumbnail,
                BackColor = Color.FromArgb(225, 235, 225)
            };
            cardPanel.Controls.Add(thumb);
            thumb.Click += (s, e) =>
            {
                SelectCard(cardPanel);
                OpenOutputFile(task);
            };

            // Output file name.
            string outName = Path.GetFileName(task.OutputPath);
            var lblName = new Label
            {
                Location = new Point(174, 12),
                Size = new Size(cardW - 200, 22),
                Text = outName,
                Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold),
                ForeColor = Color.FromArgb(45, 45, 45),
                BackColor = Color.Transparent,
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            toolTip.SetToolTip(lblName, task.OutputPath);
            lblName.Click += (s, e) =>
            {
                SelectCard(cardPanel);
                OpenOutputFile(task);
            };
            cardPanel.Controls.Add(lblName);

            // Format / resolution / size / duration (all read from the output).
            AddInfoLabel(cardPanel, 174, 36, "格式: " + (task.SourceFormat ?? "-"), 110);
            AddInfoLabel(cardPanel, 284, 36, "分辨率: " + (task.SourceResolution ?? "-"), 130);
            AddInfoLabel(cardPanel, 174, 60, "大小: " + (task.SourceFileSize ?? "-"), 130);
            AddInfoLabel(cardPanel, 304, 60, "时长: " + (task.SourceDuration ?? "-"), 130);

            // Audio tracks (combined into one line; each shows language + codec + sample rate + bitrate + channels).
            int rowAudioY = 88;
            AddInfoLabel(cardPanel, 174, rowAudioY, "音轨:", 44);
            if (task.AudioTracks != null && task.AudioTracks.Count > 0)
            {
                string combined = string.Join("    ",
                    task.AudioTracks.Select(a => a.DisplayName));
                AddInfoLabel(cardPanel, 218, rowAudioY, combined, cardW - 260);
            }
            else
            {
                AddInfoLabel(cardPanel, 218, rowAudioY, "无音轨", cardW - 260);
            }

            // A subtle "已完成" tag on the right side (informational label, not a button).
            var lblDone = new Label
            {
                Location = new Point(cardW - 110, 12),
                Size = new Size(72, 22),
                Text = "✓ 已完成",
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.FromArgb(60, 150, 90),
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            cardPanel.Controls.Add(lblDone);

            var card = new TaskCard
            {
                Task = task,
                Panel = cardPanel,
                ThumbnailBox = thumb
            };

            WireCardHover(cardPanel, task);
            return card;
        }

        private void OpenOutputFile(ConversionTask task)
        {
            try
            {
                string path = task.OutputPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    System.Diagnostics.Process.Start(path);
            }
            catch { }
        }

        private void WireCardHover(RoundedPanel cardPanel, ConversionTask task)
        {
            // 右键菜单：复制路径 / 打开位置 / 取消转换 / 删除。
            cardPanel.ContextMenuStrip = BuildCardContextMenu(task);

            cardPanel.MouseEnter += (s, e) =>
            {
                cardPanel.IsHovered = true;
                cardPanel.Invalidate();
            };
            cardPanel.MouseLeave += (s, e) =>
            {
                // Only turn off hover if the mouse really left the panel bounds.
                if (!cardPanel.ClientRectangle.Contains(cardPanel.PointToClient(Control.MousePosition)))
                {
                    cardPanel.IsHovered = false;
                    cardPanel.Invalidate();
                }
            };
            cardPanel.Click += (s, e) => SelectCard(cardPanel);

            // 拖拽手工排序：左键按下记录起点（命中交互控件不启动），
            // 移动超过 DragSize 阈值后进入 DoDragDrop。子控件鼠标事件会
            // 冒泡到 cardPanel，因此用 GetChildAtPoint 排除按钮等交互控件。
            cardPanel.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var hit = cardPanel.GetChildAtPoint(e.Location);
                if (hit != null && (hit is Button || hit is ComboBox || hit is TextBox ||
                    hit is ProgressBar || hit is NumericUpDown))
                    return; // 交互控件上按下不启动拖拽
                _dragCard = cardPanel;
                _dragStartPoint = e.Location;
            };
            cardPanel.MouseMove += (s, e) =>
            {
                if (_dragCard != cardPanel || e.Button != MouseButtons.Left) return;
                if (Math.Abs(e.X - _dragStartPoint.X) < SystemInformation.DragSize.Width &&
                    Math.Abs(e.Y - _dragStartPoint.Y) < SystemInformation.DragSize.Height)
                    return;
                // 搜索过滤激活时列表是子集，拖拽位置无法映射到完整顺序，禁用。
                if (GetSearchKeyword() != null) { _dragCard = null; return; }
                cardPanel.DoDragDrop(cardPanel, DragDropEffects.Move);
                _dragCard = null; // DoDragDrop 阻塞直到拖拽结束
            };

            foreach (Control c in cardPanel.Controls)
            {
                c.MouseEnter += (s, e) =>
                {
                    cardPanel.IsHovered = true;
                    cardPanel.Invalidate();
                };
                c.MouseLeave += (s, e) =>
                {
                    if (!cardPanel.ClientRectangle.Contains(cardPanel.PointToClient(Control.MousePosition)))
                    {
                        cardPanel.IsHovered = false;
                        cardPanel.Invalidate();
                    }
                };
                c.Click += (s, e) =>
                {
                    // Buttons/combos have their own click logic; do not change
                    // the selection state for them. Everything else selects the card.
                    if (c is Button || c is ComboBox || c is TextBox || c is ProgressBar)
                        return;
                    SelectCard(cardPanel);
                };
            }
        }

        /// <summary>拖拽悬停：计算插入位置、画指示线，并在上下边缘自动滚动。</summary>
        private void TaskListPanel_DragOver(object sender, DragEventArgs e)
        {
            // 文件拖放分流：taskListPanel.AllowDrop=true 后文件拖放不再穿透到 Form，
            // 这里直接放行为 Copy（与 Form 级 VideoConverter_DragOver 一致），
            // 仅当拖的是任务卡片时才进入重排逻辑。
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effect = DragDropEffects.Copy;
                return;
            }

            if (_dragCard == null || _dragCard.IsDisposed)
            {
                e.Effect = DragDropEffects.None;
                return;
            }
            e.Effect = DragDropEffects.Move;

            var pt = taskListPanel.PointToClient(new Point(e.X, e.Y));
            AutoScrollDuringDrag(pt.Y);
            DrawInsertionLine(ComputeInsertIndex(pt.Y));
        }

        /// <summary>拖拽放下：文件拖放交给原有添加逻辑，任务卡片则重排。</summary>
        private void TaskListPanel_DragDrop(object sender, DragEventArgs e)
        {
            ClearInsertionLine();

            // 文件拖放：复用 Form 级 VideoConverter_DragDrop 的添加文件逻辑。
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                VideoConverter_DragDrop(sender, e);
                return;
            }

            if (_dragCard == null || _dragCard.IsDisposed) return;
            var draggedTask = _cards.FirstOrDefault(c => c.Panel == _dragCard)?.Task;
            _dragCard = null;
            if (draggedTask == null) return;

            var pt = taskListPanel.PointToClient(new Point(e.X, e.Y));
            ReorderTask(draggedTask, ComputeInsertIndex(pt.Y));
        }

        /// <summary>拖拽时在面板上下边缘自动滚动列表。</summary>
        private void AutoScrollDuringDrag(int clientY)
        {
            const int edge = 40;
            const int step = 20;
            int cur = taskListPanel.AutoScrollPosition.Y; // 已滚动时 <= 0
            if (clientY > taskListPanel.ClientSize.Height - edge)
            {
                // 向下滚动：内容向上移动，滚动偏移增大。
                taskListPanel.AutoScrollPosition = new Point(0, -cur + step);
            }
            else if (clientY < edge)
            {
                taskListPanel.AutoScrollPosition = new Point(0, Math.Max(0, -cur - step));
            }
        }

        /// <summary>根据鼠标客户区 Y 计算插入索引（0.._cards.Count，末尾为 Count）。</summary>
        private int ComputeInsertIndex(int clientY)
        {
            int scrollY = taskListPanel.AutoScrollPosition.Y; // <= 0
            for (int i = 0; i < _cards.Count; i++)
            {
                var panel = _cards[i].Panel;
                if (panel == null || panel.IsDisposed) continue;
                var b = panel.Bounds;
                int top = b.Top + scrollY;      // 内容坐标 + 滚动偏移 = 客户区坐标
                if (clientY < top + b.Height / 2) return i;
            }
            return _cards.Count;
        }

        /// <summary>在目标插入位置绘制紫色指示线（覆盖旧线）。</summary>
        private void DrawInsertionLine(int insertIndex)
        {
            if (!taskListPanel.IsHandleCreated || _cards.Count == 0) return;
            ClearInsertionLine();

            int scrollY = taskListPanel.AutoScrollPosition.Y;
            int lineY;
            if (insertIndex >= _cards.Count)
            {
                var last = _cards[_cards.Count - 1].Panel;
                if (last == null || last.IsDisposed) return;
                lineY = last.Bounds.Top + scrollY + last.Bounds.Height + 6; // 列表末尾下方
            }
            else
            {
                var c = _cards[insertIndex].Panel;
                if (c == null || c.IsDisposed) return;
                lineY = c.Bounds.Top + scrollY - 6;                         // 目标卡片上方
            }

            using (var g = taskListPanel.CreateGraphics())
            using (var brush = new SolidBrush(Color.FromArgb(124, 77, 255)))
                g.FillRectangle(brush, 2, lineY, taskListPanel.ClientSize.Width - 4, 3);
            _insertionLineY = lineY;
        }

        /// <summary>用面板背景色擦除指示线。</summary>
        private void ClearInsertionLine()
        {
            if (_insertionLineY < 0) return;
            if (taskListPanel.IsHandleCreated)
            {
                using (var g = taskListPanel.CreateGraphics())
                using (var brush = new SolidBrush(taskListPanel.BackColor))
                    g.FillRectangle(brush, 2, _insertionLineY - 1, taskListPanel.ClientSize.Width - 4, 5);
            }
            _insertionLineY = -1;
        }

        /// <summary>把拖动任务移动到插入位置，并切换到「添加顺序」以新顺序为准。</summary>
        private void ReorderTask(ConversionTask draggedTask, int insertIndex)
        {
            int oldTaskIdx = _tasks.IndexOf(draggedTask);
            if (oldTaskIdx < 0) return;

            // 目标任务 = 当前可见顺序（_cards）中第 insertIndex 个；末尾为 null。
            ConversionTask targetTask = (insertIndex >= 0 && insertIndex < _cards.Count)
                ? _cards[insertIndex].Task
                : null;

            if (targetTask == draggedTask)
            {
                RefreshTaskList(); // 原地放下，仅重建保持一致性
                return;
            }

            _tasks.RemoveAt(oldTaskIdx);
            int tIdx = (targetTask == null) ? _tasks.Count : _tasks.IndexOf(targetTask);
            _tasks.Insert(tIdx, draggedTask);

            // 手工排序后切到「添加顺序」，列表以新手工顺序为准（触发 RefreshTaskList）。
            if (_sortMode != 0 && sortCombo != null)
                sortCombo.SelectedIndex = 0;
            else
                RefreshTaskList();
        }

        /// <summary>为任务卡片构建右键菜单：复制路径 / 打开位置 / 取消转换 / 删除。</summary>
        private ContextMenuStrip BuildCardContextMenu(ConversionTask task)
        {
            var ctx = new ContextMenuStrip();
            ctx.Items.Add("复制输入路径", null, (s, e) => SafeSetClipboard(task.InputPath));
            ctx.Items.Add("打开文件位置", null, (s, e) => ExplorerSelectFile(task.InputPath));

            var copyOut = ctx.Items.Add("复制输出路径", null, (s, e) => SafeSetClipboard(task.OutputPath));
            var openOut = ctx.Items.Add("打开输出位置", null, (s, e) => ExplorerSelectFile(task.OutputPath));

            ctx.Items.Add("-");

            ctx.Items.Add("媒体信息", null, (s, e) =>
            {
                using (var dlg = new MediaInfoForm(task))
                    dlg.ShowDialog(this.FindForm());
            });

            ctx.Items.Add("媒体信息编辑", null, (s, e) =>
            {
                using (var dlg = new MediaInfoEditorForm(task))
                {
                    if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
                    {
                        var card = _cards.FirstOrDefault(c => c.Task == task);
                        if (card != null) RefreshSingleCard(task);
                    }
                }
            });

            ctx.Items.Add("批量编辑", null, (s, e) =>
            {
                var pending = _tasks.Where(t => t.Status != TaskStatus.Completed).ToList();
                using (var dlg = new BatchEditForm(pending))
                {
                    if (dlg.ShowDialog(this.FindForm()) == DialogResult.OK)
                        RefreshTaskList();
                }
            });

            // P2: 暂停/恢复（仅转换中可用）
            var pauseItem = ctx.Items.Add("暂停", null, (s, e) => TogglePauseTask(task));

            var cancelItem = ctx.Items.Add("取消转换", null, (s, e) =>
            {
                try { task.Cancellation?.Cancel(); } catch { }
            });

            ctx.Items.Add("-");

            ctx.Items.Add("删除此任务", null, (s, e) => RemoveTask(task));

            ctx.Opening += (s, e) =>
            {
                bool outputExists = !string.IsNullOrEmpty(task.OutputPath) && File.Exists(task.OutputPath);
                copyOut.Enabled = !string.IsNullOrEmpty(task.OutputPath);
                openOut.Enabled = outputExists;
                cancelItem.Visible = task.Status == TaskStatus.Converting;
                pauseItem.Visible = task.Status == TaskStatus.Converting;
                pauseItem.Text = task.IsPaused ? "恢复" : "暂停";
            };
            return ctx;
        }

        /// <summary>从任务列表移除单个任务（含缩略图释放与取消）。</summary>
        private void RemoveTask(ConversionTask task)
        {
            if (task == null) return;
            if (task.Status == TaskStatus.Converting && task.Cancellation != null)
            {
                try { task.Cancellation.Cancel(); } catch { }
            }
            _tasks.Remove(task);
            if (task.Thumbnail != null) task.Thumbnail.Dispose();
            RefreshTaskList();
        }

        /// <summary>暂停/恢复转换任务（挂起/恢复 ffmpeg 进程）。</summary>
        private void TogglePauseTask(ConversionTask task)
        {
            if (task.Status != TaskStatus.Converting) return;
            if (task.CurrentProcessId <= 0) return;

            if (task.IsPaused)
            {
                // 恢复
                if (ProcessSuspender.Resume(task.CurrentProcessId))
                {
                    task.IsPaused = false;
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    if (card != null && !card.Panel.IsDisposed)
                        card.StatusLabel.Text = "转换中";
                }
            }
            else
            {
                // 暂停
                if (ProcessSuspender.Suspend(task.CurrentProcessId))
                {
                    task.IsPaused = true;
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    if (card != null && !card.Panel.IsDisposed)
                        card.StatusLabel.Text = "已暂停";
                }
            }
        }

        private static void SafeSetClipboard(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        /// <summary>在 Windows 资源管理器中选中指定文件。</summary>
        private static void ExplorerSelectFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + filePath + "\"");
            }
            catch { }
        }

        /// <summary>
        /// Select a single card. Only one card may be "active" at a time. #42
        /// </summary>
        private void SelectCard(RoundedPanel panel)
        {
            if (_selectedCard == panel) return;
            if (_selectedCard != null && !_selectedCard.IsDisposed)
            {
                _selectedCard.IsActive = false;
                _selectedCard.Invalidate();
            }
            _selectedCard = panel;
            panel.IsActive = true;
            panel.Invalidate();
        }

        private Label AddInfoLabel(Panel parent, int x, int y, string text, int width)
        {
            var lbl = new Label
            {
                Location = new Point(x, y),
                Size = new Size(width, 18),
                Text = text,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                ForeColor = Color.FromArgb(100, 100, 100),
                BackColor = Color.Transparent,
                AutoEllipsis = true
            };
            parent.Controls.Add(lbl);
            return lbl;
        }

        private Button CreateIconButton(string symbol, int x, int y, string tooltip)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(30, 26),
                Text = symbol,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.Transparent,
                ForeColor = Color.FromArgb(90, 90, 90),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderSize = 0;
            toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        /// <summary>
        /// Themed action button: light fill, dark text, dark (theme) border. #48
        /// </summary>
        private Button CreateThemeButton(string text, int x, int y, string tooltip)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(30, 26),
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(237, 232, 248),
                ForeColor = Color.FromArgb(45, 45, 45),
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(124, 77, 255);
            btn.FlatAppearance.BorderSize = 1;
            toolTip.SetToolTip(btn, tooltip);
            return btn;
        }

        private ComboBox CreateDropDown(int x, int y, int width)
        {
            return new ComboBox
            {
                Location = new Point(x, y),
                Size = new Size(width, 25),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular)
            };
        }

        /// <summary>
        /// 创建文件卡片上的字幕按钮。点击后弹出 PopupSubtitlePicker（自定义 ToolStripDropDown）。
        /// 按钮文字反映当前 SubMode 状态：
        ///   None         → "无字幕"
        ///   SoftKeepAll  → "保留字幕轨道"
        ///   BurnExternal → "烧录字幕" 或外挂字幕文件名截断
        /// </summary>
        private Button CreateSubtitleButton(ConversionTask task, int x, int y, int width)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(width, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45),
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "无字幕",
                UseVisualStyleBackColor = false,
                ImageAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 4, 0)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            // 加一个下拉箭头图标。
            try
            {
                btn.Image = new Bitmap(8, 8);
                using (var g = Graphics.FromImage(btn.Image))
                {
                    g.FillPolygon(Brushes.DimGray, new[] { new Point(1, 2), new Point(7, 2), new Point(4, 6) });
                }
            }
            catch { }

            RefreshSubtitleButtonText(btn, task);
            btn.Click += (s, e) => ShowSubtitlePopup(btn, task);
            return btn;
        }

        private void RefreshSubtitleButtonText(Button btn, ConversionTask task)
        {
            if (btn == null || btn.IsDisposed) return;
            string txt;
            switch (task.SubMode)
            {
                case SubtitleMode.SoftKeepAll:
                    txt = "保留字幕轨道";
                    break;
                case SubtitleMode.BurnExternal:
                    if (task.SelectedSubtitleTrack != null && task.SelectedSubtitleTrack.IsExternal)
                    {
                        string name = Path.GetFileName(task.SelectedSubtitleTrack.FilePath);
                        if (name.Length > 16) name = name.Substring(0, 14) + "…";
                        txt = name;
                    }
                    else txt = "烧录字幕";
                    break;
                case SubtitleMode.None:
                default:
                    txt = "无字幕";
                    break;
            }
            btn.Text = "  " + txt;
        }

        /// <summary>
        /// 创建文件卡片上的音轨按钮。点击后弹出 PopupAudioPicker（自定义 ToolStripDropDown）。
        /// 按钮文字反映当前音轨选择状态。
        /// </summary>
        private Button CreateAudioButton(ConversionTask task, int x, int y, int width)
        {
            var btn = new Button
            {
                Location = new Point(x, y),
                Size = new Size(width, 25),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(45, 45, 45),
                Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "音轨",
                UseVisualStyleBackColor = false,
                ImageAlign = ContentAlignment.MiddleRight,
                Padding = new Padding(0, 0, 4, 0)
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
            try
            {
                btn.Image = new Bitmap(8, 8);
                using (var g = Graphics.FromImage(btn.Image))
                {
                    g.FillPolygon(Brushes.DimGray, new[] { new Point(1, 2), new Point(7, 2), new Point(4, 6) });
                }
            }
            catch { }

            RefreshAudioButtonText(btn, task);
            btn.Click += (s, e) => ShowAudioPopup(btn, task);
            return btn;
        }

        private void RefreshAudioButtonText(Button btn, ConversionTask task)
        {
            if (btn == null || btn.IsDisposed) return;
            string txt;
            if (task.SelectedAudioTrackIndices != null && task.SelectedAudioTrackIndices.Contains(-1))
            {
                txt = "全部音轨";
            }
            else if (task.SelectedAudioTrackIndices != null && task.SelectedAudioTrackIndices.Count > 0)
            {
                if (task.SelectedAudioTrackIndices.Count == 1 && task.AudioTracks.Count > task.SelectedAudioTrackIndices[0])
                {
                    string name = task.AudioTracks[task.SelectedAudioTrackIndices[0]].DisplayName;
                    txt = name.Length > 14 ? name.Substring(0, 12) + "…" : name;
                }
                else
                {
                    txt = task.SelectedAudioTrackIndices.Count + " 条音轨";
                }
            }
            else if (task.SelectedAudioTrack != null)
            {
                string name = task.SelectedAudioTrack.DisplayName;
                txt = name.Length > 14 ? name.Substring(0, 12) + "…" : name;
            }
            else if (task.AudioTracks != null && task.AudioTracks.Count > 0)
            {
                txt = "无音频";
            }
            else
            {
                txt = "无音频";
            }
            btn.Text = "  " + txt;
        }

        /// <summary>
        /// 弹出 PopupAudioPicker。完全自绘的 ToolStripDropDown。
        ///   标题: "音轨" + "+" 图标
        ///   ○ 无音频
        ///   ▣ 保留所有音轨（三态）
        ///   音轨: 多选 checkbox 列表 + 删除按钮（外挂）
        ///   □ 应用到全部
        /// </summary>
        private void ShowAudioPopup(Button anchor, ConversionTask task)
        {
            var popup = new PopupAudioPicker(task);
            popup.Closed += (s, e) =>
            {
                var indices = popup.SelectedIndices;
                task.SelectedAudioTrackIndices = indices != null
                    ? new List<int>(indices) : new List<int>();
                // 同步 SelectedAudioTrack（保留原有写回逻辑）。
                if (indices != null && indices.Contains(-1))
                    task.SelectedAudioTrack = task.AudioTracks.Count > 0 ? task.AudioTracks[0] : null;
                else if (indices != null && indices.Count > 0)
                {
                    int first = indices[0];
                    task.SelectedAudioTrack = (first >= 0 && first < task.AudioTracks.Count)
                        ? task.AudioTracks[first] : null;
                }
                else
                    task.SelectedAudioTrack = null;

                RefreshAudioButtonText(anchor, task);
                if (popup.ApplyToAll) ApplyAudioToAll(task);
            };
            popup.Show(anchor, new System.Drawing.Point(0, anchor.Height));
        }

        /// <summary>把当前任务的音轨选择复制到所有其他待处理任务（“应用到全部”）。</summary>
        private void ApplyAudioToAll(ConversionTask source)
        {
            foreach (var card in _cards)
            {
                var t = card.Task;
                if (t == null || ReferenceEquals(t, source)) continue;
                if (t.Status != TaskStatus.Pending) continue;
                var src = source.SelectedAudioTrackIndices;
                t.SelectedAudioTrackIndices = src != null ? new List<int>(src) : new List<int>();
                t.SelectedAudioTrack = source.SelectedAudioTrack;
                if (card.AudioButton != null) RefreshAudioButtonText(card.AudioButton, t);
            }
        }

        /// <summary>
        /// 弹出 PopupSubtitlePicker。完全自绘的 ToolStripDropDown，行为贴近截图：
        ///   标题: "Subtitle" + "+" 图标
        ///   ○ 无字幕
        ///   ○ 保留所有字幕轨道 ⓘ
        ///   烧录字幕: ⓘ [✎]                  <- 修改图标 → 打开 VideoEditForm 字幕页签
        ///     ● 外挂字幕文件名列表（单选）
        ///   □ 导出字幕（暂未启用，灰显）
        /// </summary>
        private void ShowSubtitlePopup(Button anchor, ConversionTask task)
        {
            var popup = new PopupSubtitlePicker(task);
            popup.OuterEditClicked += () =>
            {
                popup.Close();
                OpenVideoEditSubtitle(task, anchor);
            };
            popup.Closed += (s, e) =>
            {
                task.ExportSubtitle = popup.ExportSubtitle;
                RefreshSubtitleButtonText(anchor, task);
                if (popup.ApplyToAll) ApplySubtitleToAll(task);
            };
            // ToolStripDropDown.Show(owner, Point) 的 Point 是相对 owner 的坐标，
            // 框架会内部换算为屏幕坐标。这里直接传 owner 相对坐标(按钮正下方)，避免双重换算导致弹窗跑到屏幕右下角。
            popup.Show(anchor, new System.Drawing.Point(0, anchor.Height));
        }

        /// <summary>把当前任务的字幕设置复制到所有其他待处理任务（“应用到全部”）。</summary>
        private void ApplySubtitleToAll(ConversionTask source)
        {
            foreach (var card in _cards)
            {
                var t = card.Task;
                if (t == null || ReferenceEquals(t, source)) continue;
                if (t.Status != TaskStatus.Pending) continue;
                t.SubMode = source.SubMode;
                t.BurnSubtitle = source.BurnSubtitle;
                t.ExportSubtitle = source.ExportSubtitle;
                t.SelectedSubtitleTrackIndices = source.SelectedSubtitleTrackIndices != null
                    ? new List<int>(source.SelectedSubtitleTrackIndices)
                    : new List<int>();
                t.SubtitleSettings = source.SubtitleSettings;
                t.SelectedSubtitleTrack = source.SelectedSubtitleTrack;
                if (card.SubtitleButton != null) RefreshSubtitleButtonText(card.SubtitleButton, t);
            }
        }

        /// <summary>打开 VideoEditForm 并直接切到字幕页签（文件卡片“修改”图标调用）。</summary>
        private void OpenVideoEditSubtitle(ConversionTask task, Control anchorForPosition)
        {
            using (var dlg = new VideoEditForm())
            {
                dlg.InputPath = task.InputPath;
                dlg.SourceDurationSeconds = task.SourceDurationSeconds;
                dlg.SourceWidth = 0; dlg.SourceHeight = 0;
                dlg.FrameRate = 0;
                dlg.Segments = task.Segments;
                dlg.Crop = task.Crop;
                dlg.Rotation = task.Rotation;
                dlg.MergeSegments = task.MergeSegments;
                // 字幕设置优先级：此视频个性设置 > 默认设置文件 > 系统默认
                dlg.SubSettings = VideoEditForm.LoadSubtitleOrDefault(task);
                dlg.SubTracks = task.SubtitleTracks;
                dlg.DefaultExternalSubPath = task.SelectedSubtitleTrack != null && task.SelectedSubtitleTrack.IsExternal
                    ? task.SelectedSubtitleTrack.FilePath
                    : null;
                dlg.StartTabIndex = 3; // 字幕页签
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // 同步样式 + 把 SubSettings 注入到 task（不切换 SubMode，由 popup 控制）。
                    task.SubtitleSettings = dlg.SubSettings;
                    if (task.SubtitleSettings.ExternalSubPath != null)
                    {
                        // 若用户没有切换 SubMode，但改了外挂字幕路径，更新 SelectedSubtitleTrack。
                        if (task.SubMode == SubtitleMode.BurnExternal &&
                            task.SelectedSubtitleTrack != null &&
                            !task.SelectedSubtitleTrack.IsExternal)
                        {
                            task.SelectedSubtitleTrack = new SubtitleTrackInfo
                            {
                                IsExternal = true,
                                FilePath = dlg.SubSettings.ExternalSubPath,
                                Codec = "srt",
                                Index = 0
                            };
                        }
                    }
                    // 立即更新文件卡片上字幕按钮的标题。
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    if (card?.SubtitleButton != null) RefreshSubtitleButtonText(card.SubtitleButton, task);
                }
            }
        }

        /// <summary>
        /// 字幕模式选择弹窗。自定义 ToolStripDropDown，包含三个 radio（无字幕/保留所有字幕轨/烧录外挂字幕）
        /// + 一个外挂字幕列表（仅显示 IsExternal=true 的字幕轨）+ 一个修改图标（打开 VideoEditForm 字幕页签）。
        /// 选择会直接写回 task.SubMode + task.SelectedSubtitleTrack。
        /// </summary>
        private class PopupSubtitlePicker : ToolStripDropDown
        {
            private readonly ConversionTask _task;
            private RadioButton _rbNone;
            private RadioButton _rbKeepAll;
            private RadioButton _rbBurnExternal;
            private Panel _externalListPanel;
            private CheckBox _chkExportSubtitle;
            private CheckBox _chkApplyToAll;
            private bool _cannotClose;
            private readonly List<RadioButton> _externalRadios = new List<RadioButton>();

            public event Action OuterEditClicked;

            /// <summary>是否把字幕设置应用到所有待处理任务。</summary>
            public bool ApplyToAll => _chkApplyToAll != null && _chkApplyToAll.Checked;

            /// <summary>是否导出字幕流为独立文件。</summary>
            public bool ExportSubtitle => _chkExportSubtitle != null && _chkExportSubtitle.Checked;

            public PopupSubtitlePicker(ConversionTask task)
            {
                _task = task;
                AutoSize = false;
                DropShadowEnabled = true;
                MinimumSize = new Size(280, 248);

                var host = new ToolStripControlHost(BuildContent());
                host.AutoSize = false;
                host.Size = new Size(280, 248);
                Padding = new Padding(0);
                Margin = new Padding(0);
                Items.Add(host);

                // OpenFileDialog 期间阻止失焦关闭。
                Closing += (s, e) => { if (_cannotClose) e.Cancel = true; };
            }

            private Control BuildContent()
            {
                var root = new Panel
                {
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    Size = new Size(280, 248),
                    Padding = new Padding(8)
                };

                // ---- Title bar: "Subtitle" + "+" icon ----
                var lblTitle = new Label
                {
                    Text = "Subtitle",
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                    Location = new Point(8, 8),
                    Size = new Size(180, 20),
                    ForeColor = Color.FromArgb(45, 45, 45)
                };
                root.Controls.Add(lblTitle);

                var btnAdd = new Button
                {
                    Text = "+",
                    Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                    Location = new Point(248, 4),
                    Size = new Size(26, 26),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(124, 77, 255)
                };
                btnAdd.FlatAppearance.BorderSize = 0;
                btnAdd.Click += (s, e) => AddExternalSubtitle();
                root.Controls.Add(btnAdd);

                // ---- ○ 无字幕 ----
                _rbNone = new RadioButton
                {
                    Text = "无字幕",
                    Location = new Point(8, 36),
                    Size = new Size(220, 22),
                    Checked = _task.SubMode == SubtitleMode.None
                };
                _rbNone.CheckedChanged += (s, e) => { if (_rbNone.Checked) ApplyMode(SubtitleMode.None); };
                root.Controls.Add(_rbNone);

                // ---- ○ 保留所有字幕轨道 ⓘ ----
                _rbKeepAll = new RadioButton
                {
                    Text = "保留所有字幕轨道",
                    Location = new Point(8, 60),
                    Size = new Size(180, 22),
                    Checked = _task.SubMode == SubtitleMode.SoftKeepAll
                };
                _rbKeepAll.CheckedChanged += (s, e) => { if (_rbKeepAll.Checked) ApplyMode(SubtitleMode.SoftKeepAll); };
                root.Controls.Add(_rbKeepAll);
                var infoKeep = new Label
                {
                    Text = "ⓘ",
                    Location = new Point(190, 60),
                    Size = new Size(20, 22),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    ForeColor = Color.Gray,
                    Cursor = Cursors.Help
                };
                toolTip1.SetToolTip(infoKeep, "将字幕以数据流保存至目标文件中\n（必须是 mp4/mov/mkv 等支持内嵌字幕的格式）");
                root.Controls.Add(infoKeep);

                // ---- 烧录字幕 ⓘ [✎] ----
                var lblBurn = new Label
                {
                    Text = "烧录字幕:",
                    Location = new Point(8, 88),
                    Size = new Size(70, 22),
                    ForeColor = Color.FromArgb(45, 45, 45)
                };
                root.Controls.Add(lblBurn);

                var infoBurn = new Label
                {
                    Text = "ⓘ",
                    Location = new Point(82, 88),
                    Size = new Size(16, 22),
                    Font = new Font("Microsoft YaHei UI", 9F),
                    ForeColor = Color.Gray,
                    Cursor = Cursors.Help
                };
                toolTip1.SetToolTip(infoBurn, "将外挂字幕文件烧录到视频画面（硬字幕）\n只有外挂字幕文件才会显示");
                root.Controls.Add(infoBurn);

                var btnEdit = new Button
                {
                    Text = "✎",
                    Location = new Point(248, 84),
                    Size = new Size(26, 26),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(124, 77, 255),
                    Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold)
                };
                btnEdit.FlatAppearance.BorderSize = 1;
                btnEdit.FlatAppearance.BorderColor = Color.FromArgb(220, 220, 220);
                btnEdit.Click += (s, e) => OuterEditClicked?.Invoke();
                root.Controls.Add(btnEdit);

                // ---- External subtitle file list (radio) ----
                _externalListPanel = new Panel
                {
                    Location = new Point(20, 116),
                    Size = new Size(244, 56),
                    AutoScroll = true,
                    BackColor = Color.White
                };
                root.Controls.Add(_externalListPanel);

                PopulateExternalList();

                // ---- □ 导出字幕 ----
                _chkExportSubtitle = new CheckBox
                {
                    Text = "导出字幕",
                    Location = new Point(8, 184),
                    Size = new Size(180, 22),
                    Checked = _task.ExportSubtitle
                };
                root.Controls.Add(_chkExportSubtitle);

                // ---- □ 应用到全部 ----
                _chkApplyToAll = new CheckBox
                {
                    Text = "应用到全部",
                    Location = new Point(8, 212),
                    Size = new Size(180, 22)
                };
                root.Controls.Add(_chkApplyToAll);

                return root;
            }

            private void PopulateExternalList()
            {
                _externalListPanel.Controls.Clear();
                _externalRadios.Clear();
                _rbBurnExternal = null;

                // 构建显示列表：(字幕, 是否可删除)。SubtitleTracks 中的可删除；
                // SubtitleSettings.ExternalSubPath 手动指定的仅显示不可删除。
                var externalSubs = (_task.SubtitleTracks ?? new List<SubtitleTrackInfo>())
                    .Where(s => s.IsExternal).ToList();
                var items = externalSubs.Select(s => new { Sub = s, Removable = true }).ToList();
                var manual = _task.SubtitleSettings != null
                    ? _task.SubtitleSettings.ExternalSubPath
                    : null;
                if (!string.IsNullOrEmpty(manual))
                {
                    bool already = items.Any(it =>
                        string.Equals(it.Sub.FilePath, manual, StringComparison.OrdinalIgnoreCase));
                    if (!already)
                        items.Insert(0, new
                        {
                            Sub = new SubtitleTrackInfo
                            {
                                IsExternal = true,
                                FilePath = manual,
                                Codec = "srt",
                                Index = 0
                            },
                            Removable = false
                        });
                }

                if (items.Count == 0)
                {
                    var lbl = new Label
                    {
                        Text = "（未检测到外挂字幕文件）",
                        Location = new Point(8, 6),
                        Size = new Size(228, 20),
                        ForeColor = Color.Gray,
                        Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Italic)
                    };
                    _externalListPanel.Controls.Add(lbl);
                    return;
                }

                int panelWidth = _externalListPanel.ClientSize.Width;
                if (panelWidth <= 0) panelWidth = 244;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    int y = i * 24;

                    // 每行一个 Panel 容器：RadioButton（左）+ 删除按钮（右，仅可删除项）。
                    var rowPanel = new Panel
                    {
                        Location = new Point(0, y),
                        Size = new Size(panelWidth, 24),
                        BackColor = Color.White
                    };

                    var sub = item.Sub;
                    bool selected = _task.SubMode == SubtitleMode.BurnExternal &&
                                    _task.SelectedSubtitleTrack != null &&
                                    string.Equals(_task.SelectedSubtitleTrack.FilePath, sub.FilePath,
                                        StringComparison.OrdinalIgnoreCase);

                    var rb = new RadioButton
                    {
                        Text = sub.FilePath != null
                            ? Path.GetFileName(sub.FilePath) + " …"
                            : "(无)",
                        Location = new Point(2, 1),
                        Size = new Size(panelWidth - 30, 22),
                        Checked = selected
                    };
                    rb.CheckedChanged += (s, e) =>
                    {
                        if (!rb.Checked) return;
                        // 手动维持跨 Panel 的单选互斥。
                        foreach (var other in _externalRadios)
                            if (other != rb) other.Checked = false;
                        ApplyExternal(SubtitleMode.BurnExternal, sub);
                    };
                    rowPanel.Controls.Add(rb);
                    _externalRadios.Add(rb);
                    if (i == 0) _rbBurnExternal = rb;

                    if (item.Removable)
                    {
                        var btnDel = new Button
                        {
                            Text = "×",
                            Location = new Point(panelWidth - 22, 3),
                            Size = new Size(16, 16),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = Color.White,
                            ForeColor = Color.FromArgb(180, 60, 60),
                            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
                            TextAlign = ContentAlignment.MiddleCenter
                        };
                        btnDel.FlatAppearance.BorderSize = 0;
                        var subToRemove = item.Sub;
                        btnDel.Click += (s, e) =>
                        {
                            _task.SubtitleTracks.Remove(subToRemove);
                            PopulateExternalList();
                        };
                        rowPanel.Controls.Add(btnDel);
                    }

                    _externalListPanel.Controls.Add(rowPanel);
                }
            }

            /// <summary>弹出 OpenFileDialog 添加外挂字幕文件到 _task.SubtitleTracks 并自动选中。</summary>
            private void AddExternalSubtitle()
            {
                using (var ofd = new OpenFileDialog
                {
                    Filter = "字幕文件|*.srt;*.ass;*.ssa;*.sub;*.vtt|所有文件|*.*",
                    Title = "选择外挂字幕文件"
                })
                {
                    _cannotClose = true;
                    try
                    {
                        if (ofd.ShowDialog() != DialogResult.OK) return;
                        string path = ofd.FileName;
                        bool exists = (_task.SubtitleTracks ?? new List<SubtitleTrackInfo>())
                            .Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
                        if (exists)
                        {
                            MessageBox.Show("该字幕文件已在列表中。", "提示",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        var sub = new SubtitleTrackInfo
                        {
                            Index = -1,
                            IsExternal = true,
                            FilePath = path,
                            Title = Path.GetFileName(path),
                            Codec = Path.GetExtension(path).TrimStart('.')
                        };
                        if (_task.SubtitleTracks == null)
                            _task.SubtitleTracks = new List<SubtitleTrackInfo>();
                        _task.SubtitleTracks.Add(sub);
                        PopulateExternalList();
                        // 自动选中新添加的项（BurnExternal 模式）。
                        ApplyExternal(SubtitleMode.BurnExternal, sub);
                    }
                    finally
                    {
                        _cannotClose = false;
                    }
                }
            }

            private void ApplyMode(SubtitleMode mode)
            {
                _task.SubMode = mode;
                if (mode == SubtitleMode.None)
                {
                    _task.SelectedSubtitleTrack = null;
                    _task.SelectedSubtitleTrackIndices = new List<int>();
                    _task.BurnSubtitle = false;
                }
                else if (mode == SubtitleMode.SoftKeepAll)
                {
                    _task.SelectedSubtitleTrackIndices = new List<int> { -1 };
                    _task.SelectedSubtitleTrack = _task.SubtitleTracks.Count > 0
                        ? _task.SubtitleTracks[0]
                        : null;
                    _task.BurnSubtitle = false;
                }
            }

            private void ApplyExternal(SubtitleMode mode, SubtitleTrackInfo sub)
            {
                _task.SubMode = mode;
                _task.SelectedSubtitleTrack = sub;
                _task.SelectedSubtitleTrackIndices = new List<int>();
                _task.BurnSubtitle = true;
                if (_task.SubtitleSettings != null)
                    _task.SubtitleSettings.ExternalSubPath = sub.FilePath;
            }

            // Local tool-tip shared by info glyphs.
            private readonly ToolTip toolTip1 = new ToolTip { InitialDelay = 0, ReshowDelay = 0, ShowAlways = true };
        }

        /// <summary>
        /// 音轨选择弹窗。自定义 ToolStripDropDown，结构与 PopupSubtitlePicker 一致。
        /// 支持：无音频（radio）/ 保留所有音轨（三态 checkbox）/ 多选指定音轨（checkbox 列表）。
        /// 选择会写回 task.SelectedAudioTrackIndices。
        /// </summary>
        private class PopupAudioPicker : ToolStripDropDown
        {
            private readonly ConversionTask _task;
            private RadioButton _rbNone;
            private CheckBox _cbKeepAll;
            private Panel _trackListPanel;
            private CheckBox _chkApplyToAll;
            private bool _cannotClose;
            private bool _suppressUpdate;
            private CheckState _prevKeepAllState = CheckState.Unchecked;
            // key = 音轨在 _task.AudioTracks 中的列表位置（与 SelectedAudioTrackIndices 语义一致）。
            private readonly Dictionary<int, CheckBox> _trackChecks = new Dictionary<int, CheckBox>();

            /// <summary>是否把音轨设置应用到所有待处理任务。</summary>
            public bool ApplyToAll => _chkApplyToAll != null && _chkApplyToAll.Checked;

            /// <summary>选中的音轨索引列表（-1=全部，空=无音频，其他=指定索引）。</summary>
            public List<int> SelectedIndices
            {
                get
                {
                    if (_rbNone != null && _rbNone.Checked) return new List<int>();
                    int checkedCount = _trackChecks.Values.Count(cb => cb != null && cb.Checked);
                    // 只有用户明确勾选"保留所有音轨"（三态 Checked 且所有独立勾选框也选中）
                    // 且音轨数 > 1 时，才视为"全部"；单音轨时返回该条音轨索引，避免误判。
                    if (_cbKeepAll != null && _cbKeepAll.CheckState == CheckState.Checked
                        && checkedCount == _trackChecks.Count
                        && _task.AudioTracks.Count > 1)
                    {
                        return new List<int> { -1 };
                    }
                    var indices = new List<int>();
                    foreach (var kvp in _trackChecks)
                        if (kvp.Value.Checked) indices.Add(kvp.Key);
                    return indices;
                }
            }

            public PopupAudioPicker(ConversionTask task)
            {
                _task = task;
                AutoSize = false;
                DropShadowEnabled = true;
                MinimumSize = new Size(280, 248);

                var host = new ToolStripControlHost(BuildContent());
                host.AutoSize = false;
                host.Size = new Size(280, 248);
                Padding = new Padding(0);
                Margin = new Padding(0);
                Items.Add(host);

                // OpenFileDialog 期间阻止失焦关闭。
                Closing += (s, e) => { if (_cannotClose) e.Cancel = true; };
            }

            private Control BuildContent()
            {
                var root = new Panel
                {
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    Size = new Size(280, 248),
                    Padding = new Padding(8)
                };

                // ---- Title bar: "音轨" + "+" icon ----
                var lblTitle = new Label
                {
                    Text = "音轨",
                    Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                    Location = new Point(8, 8),
                    Size = new Size(180, 20),
                    ForeColor = Color.FromArgb(45, 45, 45)
                };
                root.Controls.Add(lblTitle);

                var btnAdd = new Button
                {
                    Text = "+",
                    Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold),
                    Location = new Point(248, 4),
                    Size = new Size(26, 26),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(124, 77, 255)
                };
                btnAdd.FlatAppearance.BorderSize = 0;
                btnAdd.Click += (s, e) => AddExternalAudio();
                root.Controls.Add(btnAdd);

                // ---- ○ 无音频 ----
                _rbNone = new RadioButton
                {
                    Text = "无音频",
                    Location = new Point(8, 36),
                    Size = new Size(220, 22)
                };
                _rbNone.CheckedChanged += (s, e) =>
                {
                    if (_suppressUpdate || !_rbNone.Checked) return;
                    _suppressUpdate = true;
                    try
                    {
                        _cbKeepAll.CheckState = CheckState.Unchecked;
                        _prevKeepAllState = CheckState.Unchecked;
                        foreach (var kvp in _trackChecks) kvp.Value.Checked = false;
                    }
                    finally { _suppressUpdate = false; }
                };
                root.Controls.Add(_rbNone);

                // ---- ▣ 保留所有音轨（三态：全选=Checked、部分=Indeterminate、全不选=Unchecked）----
                _cbKeepAll = new CheckBox
                {
                    Text = "保留所有音轨",
                    Location = new Point(8, 60),
                    Size = new Size(220, 22),
                    ThreeState = true
                };
                _cbKeepAll.CheckStateChanged += (s, e) =>
                {
                    if (_suppressUpdate) return;
                    _suppressUpdate = true;
                    try
                    {
                        var cur = _cbKeepAll.CheckState;
                        bool wantAll;
                        // ThreeState 点击循环：Unchecked→Checked→Indeterminate→Unchecked
                        if (_prevKeepAllState == CheckState.Checked && cur == CheckState.Indeterminate)
                            wantAll = false; // 全选→点击→取消全选
                        else if (_prevKeepAllState == CheckState.Indeterminate && cur == CheckState.Unchecked)
                            wantAll = true; // 部分→点击→全选
                        else
                            wantAll = cur == CheckState.Checked;

                        if (wantAll)
                        {
                            _cbKeepAll.CheckState = CheckState.Checked;
                            _rbNone.Checked = false;
                            foreach (var kvp in _trackChecks) kvp.Value.Checked = true;
                        }
                        else
                        {
                            _cbKeepAll.CheckState = CheckState.Unchecked;
                            foreach (var kvp in _trackChecks) kvp.Value.Checked = false;
                        }
                        _prevKeepAllState = _cbKeepAll.CheckState;
                    }
                    finally { _suppressUpdate = false; }
                };
                root.Controls.Add(_cbKeepAll);

                // ---- 音轨列表标签 ----
                var lblTracks = new Label
                {
                    Text = "音轨:",
                    Location = new Point(8, 88),
                    Size = new Size(70, 22),
                    ForeColor = Color.FromArgb(45, 45, 45)
                };
                root.Controls.Add(lblTracks);

                _trackListPanel = new Panel
                {
                    Location = new Point(20, 116),
                    Size = new Size(244, 84),
                    AutoScroll = true,
                    BackColor = Color.White
                };
                root.Controls.Add(_trackListPanel);

                PopulateTrackList();
                RestoreSelection();

                // ---- □ 应用到全部 ----
                _chkApplyToAll = new CheckBox
                {
                    Text = "应用到全部",
                    Location = new Point(8, 208),
                    Size = new Size(180, 22)
                };
                root.Controls.Add(_chkApplyToAll);

                return root;
            }

            private void PopulateTrackList()
            {
                _trackListPanel.Controls.Clear();
                _trackChecks.Clear();

                var tracks = _task.AudioTracks ?? new List<AudioTrackInfo>();
                if (tracks.Count == 0)
                {
                    var lbl = new Label
                    {
                        Text = "（未检测到音轨）",
                        Location = new Point(8, 6),
                        Size = new Size(228, 20),
                        ForeColor = Color.Gray,
                        Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Italic)
                    };
                    _trackListPanel.Controls.Add(lbl);
                    return;
                }

                int panelWidth = _trackListPanel.ClientSize.Width;
                if (panelWidth <= 0) panelWidth = 244;
                for (int i = 0; i < tracks.Count; i++)
                {
                    var at = tracks[i];
                    int y = i * 24;

                    var rowPanel = new Panel
                    {
                        Location = new Point(0, y),
                        Size = new Size(panelWidth, 24),
                        BackColor = Color.White
                    };

                    var cb = new CheckBox
                    {
                        Text = at.DisplayName,
                        Location = new Point(2, 1),
                        Size = new Size(panelWidth - 30, 22)
                    };
                    int listIndex = i;
                    cb.CheckedChanged += (s, e) =>
                    {
                        if (_suppressUpdate) return;
                        _suppressUpdate = true;
                        try
                        {
                            if (cb.Checked) _rbNone.Checked = false;
                            UpdateKeepAllState();
                        }
                        finally { _suppressUpdate = false; }
                    };
                    rowPanel.Controls.Add(cb);
                    _trackChecks[listIndex] = cb;

                    if (at.IsExternal)
                    {
                        var btnDel = new Button
                        {
                            Text = "×",
                            Location = new Point(panelWidth - 22, 3),
                            Size = new Size(16, 16),
                            FlatStyle = FlatStyle.Flat,
                            BackColor = Color.White,
                            ForeColor = Color.FromArgb(180, 60, 60),
                            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
                            TextAlign = ContentAlignment.MiddleCenter
                        };
                        btnDel.FlatAppearance.BorderSize = 0;
                        var toRemove = at;
                        btnDel.Click += (s, e) =>
                        {
                            _task.AudioTracks.Remove(toRemove);
                            PopulateTrackList();
                            // 删除后索引可能已变，重置为"保留所有音轨"。
                            _suppressUpdate = true;
                            try
                            {
                                _rbNone.Checked = false;
                                _cbKeepAll.CheckState = CheckState.Checked;
                                _prevKeepAllState = CheckState.Checked;
                                foreach (var kvp in _trackChecks) kvp.Value.Checked = true;
                            }
                            finally { _suppressUpdate = false; }
                        };
                        rowPanel.Controls.Add(btnDel);
                    }

                    _trackListPanel.Controls.Add(rowPanel);
                }
            }

            private void UpdateKeepAllState()
            {
                if (_trackChecks.Count == 0)
                {
                    _cbKeepAll.CheckState = CheckState.Unchecked;
                }
                else
                {
                    int cnt = 0;
                    foreach (var kvp in _trackChecks)
                        if (kvp.Value.Checked) cnt++;
                    if (cnt == 0) _cbKeepAll.CheckState = CheckState.Unchecked;
                    else if (cnt == _trackChecks.Count) _cbKeepAll.CheckState = CheckState.Checked;
                    else _cbKeepAll.CheckState = CheckState.Indeterminate;
                }
                _prevKeepAllState = _cbKeepAll.CheckState;
            }

            /// <summary>从 _task.SelectedAudioTrackIndices / SelectedAudioTrack 恢复 UI 选择状态。</summary>
            private void RestoreSelection()
            {
                _suppressUpdate = true;
                try
                {
                    var sel = _task.SelectedAudioTrackIndices;
                    if (sel != null && sel.Contains(-1))
                    {
                        _rbNone.Checked = false;
                        _cbKeepAll.CheckState = CheckState.Checked;
                        foreach (var kvp in _trackChecks) kvp.Value.Checked = true;
                    }
                    else if (sel != null && sel.Count > 0)
                    {
                        _rbNone.Checked = false;
                        foreach (var kvp in _trackChecks)
                            kvp.Value.Checked = sel.Contains(kvp.Key);
                        UpdateKeepAllState();
                    }
                    else if (_task.SelectedAudioTrack != null)
                    {
                        _rbNone.Checked = false;
                        int idx = _task.AudioTracks.IndexOf(_task.SelectedAudioTrack);
                        foreach (var kvp in _trackChecks)
                            kvp.Value.Checked = (kvp.Key == idx);
                        UpdateKeepAllState();
                    }
                    else if (_trackChecks.Count > 0)
                    {
                        // 默认选中第一条音轨（与原 cmbAudio 行为一致）。
                        _rbNone.Checked = false;
                        CheckBox firstCb = null;
                        int minKey = int.MaxValue;
                        foreach (var kvp in _trackChecks)
                        {
                            if (kvp.Key < minKey) { minKey = kvp.Key; firstCb = kvp.Value; }
                        }
                        if (firstCb != null) firstCb.Checked = true;
                        UpdateKeepAllState();
                    }
                    else
                    {
                        _rbNone.Checked = true;
                        _cbKeepAll.CheckState = CheckState.Unchecked;
                    }
                    _prevKeepAllState = _cbKeepAll.CheckState;
                }
                finally { _suppressUpdate = false; }
            }

            /// <summary>弹出 OpenFileDialog 添加外挂音频文件到 _task.AudioTracks。</summary>
            private void AddExternalAudio()
            {
                using (var ofd = new OpenFileDialog
                {
                    Filter = "音频文件|*.mp3;*.aac;*.wav;*.flac;*.ogg;*.m4a|所有文件|*.*",
                    Title = "选择外挂音频文件"
                })
                {
                    _cannotClose = true;
                    try
                    {
                        if (ofd.ShowDialog() != DialogResult.OK) return;
                        string path = ofd.FileName;
                        bool exists = (_task.AudioTracks ?? new List<AudioTrackInfo>())
                            .Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
                        if (exists)
                        {
                            MessageBox.Show("该音频文件已在列表中。", "提示",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                            return;
                        }
                        var at = new AudioTrackInfo
                        {
                            Index = -1,
                            IsExternal = true,
                            FilePath = path,
                            Title = Path.GetFileName(path),
                            Codec = Path.GetExtension(path).TrimStart('.')
                        };
                        if (_task.AudioTracks == null)
                            _task.AudioTracks = new List<AudioTrackInfo>();
                        _task.AudioTracks.Add(at);
                        PopulateTrackList();
                        RestoreSelection();
                    }
                    finally
                    {
                        _cannotClose = false;
                    }
                }
            }
        }

        private string EstimateTargetSizeFromTask(ConversionTask task)
        {
            try
            {
                double seconds = task.GetEditedDurationSeconds();
                if (seconds <= 0) return "-";

                long videoBps = ResolveVideoBitRate(task);
                long audioBps = ResolveAudioBitRate(task);
                if (videoBps <= 0 && audioBps <= 0) return "-";
                long totalBytes = (long)((videoBps + audioBps) * seconds / 8);
                return FFmpegHelper.FormatFileSize(totalBytes);
            }
            catch { return "-"; }
        }

        private long ResolveVideoBitRate(ConversionTask task)
        {
            long br = ParseBitRate(task.Preset.VideoBitrate);
            if (br > 0) return br;

            int w = 0, h = 0;
            if (!string.IsNullOrEmpty(task.Preset.ResolutionValue) &&
                task.Preset.ResolutionValue.Contains("x"))
            {
                var parts = task.Preset.ResolutionValue.Split('x');
                int.TryParse(parts[0], out w);
                int.TryParse(parts[1], out h);
            }
            if (w <= 0 || h <= 0)
            {
                var res = task.SourceResolution ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(res, @"(\d+)\s*x\s*(\d+)");
                if (m.Success)
                {
                    int.TryParse(m.Groups[1].Value, out w);
                    int.TryParse(m.Groups[2].Value, out h);
                }
            }
            return EstimateVideoBitRateByResolution(w, h);
        }

        private long ResolveAudioBitRate(ConversionTask task)
        {
            long br = ParseBitRate(task.Preset.AudioBitrate);
            if (br > 0) return br;

            if (string.Equals(task.Preset.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                var src = task.AudioTracks.FirstOrDefault();
                if (src != null) br = ParseBitRate(src.BitRate);
            }
            else
            {
                string def = DefaultCodecSettings.GetAudioDefaultBitrate(task.Preset.AudioCodec);
                br = ParseBitRate(def);
            }
            return br > 0 ? br : 192_000;
        }

        private void ToggleOutputNameEdit(ConversionTask task, Label lbl, TextBox txt, Button btn)
        {
            if (txt.Visible)
            {
                // Save.
                string newName = txt.Text.Trim();
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    task.CustomOutputName = newName;
                    lbl.Text = task.GetOutputFileName();
                    toolTip.SetToolTip(lbl, task.OutputPath);
                }
                txt.Visible = false;
                lbl.Visible = true;
                btn.Text = "✎";
                toolTip.SetToolTip(btn, "修改文件名");
            }
            else
            {
                txt.Text = string.IsNullOrWhiteSpace(task.CustomOutputName)
                    ? Path.GetFileNameWithoutExtension(task.OutputPath)
                    : task.CustomOutputName;
                txt.Visible = true;
                lbl.Visible = false;
                txt.Focus();
                txt.SelectAll();
                btn.Text = "✓";
                toolTip.SetToolTip(btn, "保存文件名");
            }
        }

        private void OpenPlayer(ConversionTask task)
        {
            try
            {
                var ext = Path.GetExtension(task.InputPath).ToLowerInvariant();
                bool isImage = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" }.Contains(ext);
                var dlg = new VideoPlayerForm(task.InputPath, isImage);
                dlg.Show(this);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法打开预览：\n" + ex.Message, "预览",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async void OpenVideoEdit(ConversionTask task)
        {
            var info = await FFmpegHelper.ProbeDetailedAsync(task.InputPath);
            using (var dlg = new VideoEditForm())
            {
                dlg.InputPath = task.InputPath;
                dlg.SourceDurationSeconds = info.DurationSeconds > 0 ? info.DurationSeconds : task.SourceDurationSeconds;
                dlg.SourceWidth = info.Width;
                dlg.SourceHeight = info.Height;
                dlg.FrameRate = info.FrameRate;
                dlg.Segments = task.Segments;
                dlg.Crop = task.Crop;
                dlg.Rotation = task.Rotation;
                dlg.MergeSegments = task.MergeSegments;
                // D 区效果参数传入
                dlg.Speed = task.Speed;
                dlg.Brightness = task.Brightness;
                dlg.Contrast = task.Contrast;
                dlg.Saturation = task.Saturation;
                dlg.WatermarkPath = task.WatermarkPath;
                dlg.WatermarkPosition = task.WatermarkPosition;
                dlg.WatermarkOpacity = task.WatermarkOpacity;
                dlg.WatermarkScalePercent = task.WatermarkScalePercent;
                dlg.SubSettings = VideoEditForm.LoadSubtitleOrDefault(task);
                dlg.SubTracks = task.SubtitleTracks;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    task.Segments = dlg.Segments;
                    task.Crop = dlg.Crop;
                    task.Rotation = dlg.Rotation;
                    task.MergeSegments = dlg.MergeSegments;
                    // D 区效果参数写回
                    task.Speed = dlg.Speed;
                    task.Brightness = dlg.Brightness;
                    task.Contrast = dlg.Contrast;
                    task.Saturation = dlg.Saturation;
                    task.WatermarkPath = dlg.WatermarkPath;
                    task.WatermarkPosition = dlg.WatermarkPosition;
                    task.WatermarkOpacity = dlg.WatermarkOpacity;
                    task.WatermarkScalePercent = dlg.WatermarkScalePercent;
                    task.TrimStartSeconds = dlg.TrimStartSeconds;
                    task.TrimEndSeconds = dlg.TrimEndSeconds;
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    if (card != null)
                    {
                        card.TargetDurationLabel.Text = "输出时长: " + task.TargetDuration;
                        card.TargetSizeLabel.Text = "预计大小: " + EstimateTargetSizeFromTask(task);
                    }

                    // "应用到全部"：将剪切/裁剪/效果/字幕设置复制到所有待处理任务。
                    if (dlg.ApplyToAll)
                    {
                        foreach (var t in _tasks)
                        {
                            if (t == task || t.Status == TaskStatus.Completed) continue;
                            t.Crop = dlg.Crop?.Clone();
                            t.Rotation = dlg.Rotation;
                            t.Speed = dlg.Speed;
                            t.Brightness = dlg.Brightness;
                            t.Contrast = dlg.Contrast;
                            t.Saturation = dlg.Saturation;
                            t.WatermarkPath = dlg.WatermarkPath;
                            t.WatermarkPosition = dlg.WatermarkPosition;
                            t.WatermarkOpacity = dlg.WatermarkOpacity;
                            t.WatermarkScalePercent = dlg.WatermarkScalePercent;
                            var ac = _cards.FirstOrDefault(c => c.Task == t);
                            if (ac != null)
                            {
                                ac.TargetSizeLabel.Text = "预计大小: " + EstimateTargetSizeFromTask(t);
                            }
                        }
                    }
                }
            }
        }

        private void OpenPresetSelection(ConversionTask task, Button presetButton, Label lblFormat, Label lblResolution, Label lblSize)
        {
            using (var dlg = new PresetSelectionForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPreset != null)
                {
                    task.Preset = dlg.SelectedPreset;
                    RefreshPresetButton(presetButton, task, lblFormat, lblResolution, lblSize);
                }
            }
        }

        private void OpenPresetEdit(ConversionTask task, Button presetButton, Label lblFormat, Label lblResolution, Label lblSize)
        {
            using (var dlg = new PresetEditForm())
            {
                dlg.Preset = task.Preset;
                dlg.UseHardwareEncoding = hardwareCheck.Checked;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    task.Preset = dlg.Preset;
                    RegisterCustomIfNeeded(dlg.Preset);
                    RefreshPresetButton(presetButton, task, lblFormat, lblResolution, lblSize);
                }
            }
        }

        /// <summary>若编辑结果是自定义预设，则登记到 PresetDataStore 以便在“自定义”页签显示。</summary>
        private void RegisterCustomIfNeeded(PresetOption preset)
        {
            if (preset != null && !preset.IsBuiltIn)
                PresetDataStore.AddCustom(preset);
        }

        private void RefreshPresetButton(Button presetButton, ConversionTask task, Label lblFormat, Label lblResolution, Label lblSize)
        {
            if (presetButton != null && !presetButton.IsDisposed)
                presetButton.Text = string.Format("{0} / {1}", task.Preset.FormatName, task.Preset.Name);
            lblFormat.Text = "格式: " + task.TargetFormat;
            lblResolution.Text = "分辨率: " + task.TargetResolution;
            lblSize.Text = "预计大小: " + EstimateTargetSizeFromTask(task);
            var card = _cards.FirstOrDefault(c => c.Task == task);
            if (card != null && card.TargetDurationLabel != null && !card.TargetDurationLabel.IsDisposed)
                card.TargetDurationLabel.Text = "输出时长: " + task.TargetDuration;
        }

        #endregion

        #region Drag & drop, options

        private void VideoConverter_DragEnter(object sender, DragEventArgs e)
        {
            // 统一处理：无论事件来自 Form 还是 taskListPanel，都设置 Copy 效果，
            // 避免某些情况下 Effect 未设置导致拖放被丢弃。
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void VideoConverter_DragOver(object sender, DragEventArgs e)
        {
            // DragOver 在拖动过程中持续触发；某些控件会吞掉 DragEnter 但不会吞 DragOver。
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private async void VideoConverter_DragDrop(object sender, DragEventArgs e)
        {
            // 多文件拖入时统一提取文件列表，在 try-catch 中处理，
            // 确保即使个别文件异常也不影响整体添加流程。
            string[] files = null;
            try
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                files = e.Data.GetData(DataFormats.FileDrop) as string[];
            }
            catch { return; }
            if (files == null || files.Length == 0) return;

            // 拖放文件时强制切换到"正在转换"页签，让用户立即看到新添加的文件。
            if (_showCompleted)
            {
                _showCompleted = false;
                UpdateTabStyles();
            }
            try
            {
                await AddFiles(files);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("DragDrop AddFiles failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Checked = light-red background; unchecked = grey background.
        /// </summary>
        private void ApplyCheckStyle(CheckBox cb)
        {
            if (cb.Checked)
            {
                cb.BackColor = Color.FromArgb(255, 224, 224);  // 淡红
                cb.ForeColor = Color.FromArgb(200, 40, 40);
            }
            else
            {
                cb.BackColor = Color.FromArgb(225, 225, 225);  // 灰
                cb.ForeColor = Color.FromArgb(90, 90, 90);
            }
        }

        private void HighSpeedCheck_CheckedChanged(object sender, EventArgs e)
        {
            ApplyCheckStyle(highSpeedCheck);
            AppSettings.HighSpeed = highSpeedCheck.Checked;
            AppSettings.Save();
        }

        private void HardwareCheck_CheckedChanged(object sender, EventArgs e)
        {
            ApplyCheckStyle(hardwareCheck);
            AppSettings.Hardware = hardwareCheck.Checked;
            AppSettings.Save();
        }

        /// <summary>
        /// Background-detect which hardware encoders ffmpeg was built with and
        /// enable the "硬件编码" checkbox only when at least one is present.
        /// </summary>
        private async void DetectHardwareOnLoad()
        {
            HardwareSupportResult result;
            try
            {
                var sup = await FFmpegHelper.DetectHardwareEncodersAsync();
                result = new HardwareSupportResult { Support = sup, Ok = true };
            }
            catch
            {
                result = new HardwareSupportResult { Support = new FFmpegHelper.HardwareSupport(), Ok = false };
            }

            this.BeginInvoke((Action)(() =>
            {
                _hwSupport = result.Support;
                if (result.Ok && result.Support.Any)
                {
                    hardwareCheck.Enabled = true;
                    hardwareCheck.Text = "硬件编码 (" + result.Support.DisplayName + ")";
                }
                else
                {
                    hardwareCheck.Enabled = false;
                    hardwareCheck.Checked = false;
                    hardwareCheck.Text = result.Ok ? "硬件编码 (不支持)" : "硬件编码 (检测失败)";
                }
                // Apply persisted hardware-encode preference when supported. #47
                if (hardwareCheck.Enabled) hardwareCheck.Checked = _pendingHardware;
                ApplyCheckStyle(hardwareCheck);
            }));
        }

        private class HardwareSupportResult
        {
            public FFmpegHelper.HardwareSupport Support;
            public bool Ok;
        }

        /// <summary>
        /// <summary>
        /// High-speed mode: smart per-stream copy/transcode is enabled whenever the
        /// 高速转换 checkbox is on. Video/audio streams are copied only when the
        /// input codec equals the target codec, otherwise re-encoded with the
        /// target codec + auto defaults (see FFmpegHelper.AppendSmartCopyStreams).
        /// </summary>
        private bool AppliesStreamCopy(ConversionTask task)
        {
            return highSpeedCheck.Checked;
        }

        /// <summary>
        /// Resolve the hardware encoder for a task's output codec, or null when
        /// the codec has no hardware equivalent (e.g. AVI/Xvid) — caller falls
        /// back to the software encoder.
        /// </summary>
        private string GetHardwareEncoderFor(ConversionTask task)
        {
            // 当“硬件编码”勾选且检测到支持时，解析为对应厂商的 GPU 编码器；
            // 否则（未勾选 / 不支持 / 该编码无硬件实现）回退为 CPU 编码器。#65
            var hw = (hardwareCheck.Checked && _hwSupport != null && _hwSupport.Any) ? _hwSupport : null;
            string resolved = FFmpegHelper.ResolveVideoEncoder(task.Preset?.VideoCodec, hw, task.Preset?.VideoCodecLabel);
            if (string.Equals(resolved, "copy", StringComparison.OrdinalIgnoreCase)) return null;
            // 仅当确实解析为 GPU 编码器时才视为“正在使用硬件编码”（用于状态展示与失败降级）。
            return FFmpegHelper.IsHardwareEncoder(resolved) ? resolved : null;
        }

        #endregion

        #region Tabs and global actions

        private void TabConvertingLabel_Click(object sender, EventArgs e)
        {
            _showCompleted = false;
            UpdateTabStyles();
            RefreshTaskList();
        }

        private void TabCompletedLabel_Click(object sender, EventArgs e)
        {
            _showCompleted = true;
            UpdateTabStyles();
            RefreshTaskList();
        }

        private void UpdateTabStyles()
        {
            if (_showCompleted)
            {
                tabConvertingLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
                tabConvertingLabel.ForeColor = Color.Gray;
                tabCompletedLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
                tabCompletedLabel.ForeColor = Color.Black;
            }
            else
            {
                tabConvertingLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
                tabConvertingLabel.ForeColor = Color.Black;
                tabCompletedLabel.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Regular);
                tabCompletedLabel.ForeColor = Color.Gray;
            }
        }

        private void UpdateCount()
        {
            int count = _tasks.Count(t => t.Status != TaskStatus.Completed);
            convertingCountLabel.Text = string.Format("({0})", count);
        }

        private async void ConvertToButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new PresetSelectionForm())
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedPreset != null)
                {
                    _globalPreset = dlg.SelectedPreset;
                    PersistConvertToPreset();
                    UpdateConvertToDisplay();
                    await ApplyGlobalPresetToAll();
                }
            }
        }

        private async void ConvertToGearButton_Click(object sender, EventArgs e)
        {
            using (var dlg = new PresetEditForm())
            {
                dlg.Preset = _globalPreset.Clone();
                dlg.UseHardwareEncoding = hardwareCheck.Checked;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _globalPreset = dlg.Preset;
                    RegisterCustomIfNeeded(dlg.Preset);
                    PersistConvertToPreset();
                    UpdateConvertToDisplay();
                    await ApplyGlobalPresetToAll();
                }
            }
        }

        #region 合并所有文件 (Merge All)

        // 视频 / 音频扩展名白名单，用于合并前的类型一致性校验。
        private static readonly HashSet<string> MergeVideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v",
            ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".vob", ".ogv", ".3gp", ".rm", ".rmvb"
        };
        private static readonly HashSet<string> MergeAudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".aac", ".wav", ".flac", ".ogg", ".wma", ".m4a", ".ac3", ".opus", ".aiff"
        };

        private void MergeCheck_CheckedChanged(object sender, EventArgs e)
        {
            if (mergeCheck.Checked)
            {
                if (_tasks.Count < 2)
                {
                    MessageBox.Show(this, "合并所有文件需要至少 2 个文件。", "提示",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    mergeCheck.Checked = false;
                    return;
                }
                string error;
                if (!ValidateMergeAllFileTypes(out error))
                {
                    MessageBox.Show(this, error, "无法合并",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    mergeCheck.Checked = false;
                    return;
                }
                _mergeAllMode = true;
            }
            else
            {
                _mergeAllMode = false;
            }
            ApplyMergeAllModeToCards();
        }

        /// <summary>合并模式下禁用所有卡片的预设选择与编辑按钮，非合并模式恢复。</summary>
        private void ApplyMergeAllModeToCards()
        {
            foreach (var card in _cards)
            {
                if (card.PresetButton != null && !card.PresetButton.IsDisposed)
                    card.PresetButton.Enabled = !_mergeAllMode;
                if (card.PresetGearButton != null && !card.PresetGearButton.IsDisposed)
                    card.PresetGearButton.Enabled = !_mergeAllMode;
                // 合并模式下隐藏每个卡片的"转换"按钮：合并只能整体触发，不能单独转换。
                if (card.ConvertButton != null && !card.ConvertButton.IsDisposed)
                    card.ConvertButton.Visible = !_mergeAllMode;
            }
        }

        /// <summary>
        /// 校验列表中的文件是否同为视频或同为音频。
        /// 不兼容时 error 含具体不兼容文件名，返回 false。
        /// </summary>
        private bool ValidateMergeAllFileTypes(out string error)
        {
            error = null;
            if (_tasks.Count == 0) return true;

            bool? allVideo = null;
            foreach (var task in _tasks)
            {
                string ext = Path.GetExtension(task.InputPath);
                bool isVideo = MergeVideoExtensions.Contains(ext);
                bool isAudio = MergeAudioExtensions.Contains(ext);
                if (!isVideo && !isAudio)
                {
                    error = "不支持的文件类型，无法合并:\n" + Path.GetFileName(task.InputPath);
                    return false;
                }
                if (allVideo == null)
                    allVideo = isVideo;
                else if (allVideo.Value != isVideo)
                {
                    error = "列表中同时存在视频和音频文件，无法合并。\n请确保所有文件同为视频或同为音频。";
                    return false;
                }
            }
            return true;
        }

        #endregion

        private async Task ApplyGlobalPresetToAll()
        {
            if (_globalPreset == null) return;

            // 快照 _tasks，避免循环中 await 导致集合被修改引发枚举异常。
            var snapshot = _tasks.ToList();
            foreach (var task in snapshot)
            {
                // 直接用全局预设覆盖所有任务的预设。
                task.Preset = _globalPreset.Clone();
                var card = _cards.FirstOrDefault(c => c.Task == task);
                if (card != null)
                {
                    if (card.PresetButton != null && !card.PresetButton.IsDisposed)
                        card.PresetButton.Text = string.Format("{0} / {1}", task.Preset.FormatName, task.Preset.Name);
                    card.OutputNameLabel.Text = task.GetOutputFileName();
                    card.TargetFormatLabel.Text = "格式: " + task.TargetFormat;
                    card.TargetResolutionLabel.Text = "分辨率: " + task.TargetResolution;
                }

                // Re-probe source metadata via ffprobe and recompute the output
                // duration/size. #44 / #46
                await RefreshCardMetadata(task);
            }
        }

        /// <summary>
        /// Re-read a task's source resolution/duration with ffprobe and refresh
        /// the relevant card labels (source + output duration/size). #44 / #46
        /// </summary>
        private async Task RefreshCardMetadata(ConversionTask task)
        {
            var card = _cards.FirstOrDefault(c => c.Task == task);
            try
            {
                var info = await FFmpegHelper.ProbeDetailedAsync(task.InputPath);
                if (info.Width > 0 && info.Height > 0)
                    task.SourceResolution = string.Format("{0} x {1}", info.Width, info.Height);
                if (info.DurationSeconds > 0)
                {
                    task.SourceDurationSeconds = info.DurationSeconds;
                    task.SourceDuration = FFmpegHelper.FormatDuration(info.DurationSeconds);
                }
                if (card != null)
                {
                    if (card.SourceResolutionLabel != null && !card.SourceResolutionLabel.IsDisposed)
                        card.SourceResolutionLabel.Text = "分辨率: " + task.SourceResolution;
                    if (card.SourceDurationLabel != null && !card.SourceDurationLabel.IsDisposed)
                        card.SourceDurationLabel.Text = "时长: " + task.SourceDuration;
                }
            }
            catch { /* keep existing metadata on probe failure */ }

            if (card != null)
            {
                if (card.TargetDurationLabel != null && !card.TargetDurationLabel.IsDisposed)
                    card.TargetDurationLabel.Text = "输出时长: " + task.TargetDuration;
                if (card.TargetSizeLabel != null && !card.TargetSizeLabel.IsDisposed)
                    card.TargetSizeLabel.Text = "预计大小: " + EstimateTargetSizeFromTask(task);
            }
        }

        private async void ConvertAllButton_Click(object sender, EventArgs e)
        {
            // 已经在转换中 → 取消全部
            if (_batchConverting)
            {
                _batchConverting = false;
                // 取消所有正在转换/待转换任务的 CancellationToken。
                foreach (var t in _tasks)
                {
                    try { t.Cancellation?.Cancel(); } catch { }
                }
                return;
            }

            if (_tasks.Count == 0) return;

            addFilesButton.Enabled = false;
            convertAllButton.Text = "取消转换";
            _batchConverting = true;

            try
            {
                if (_mergeAllMode)
                {
                    await RunMergeAllConversion();
                }
                else
                {
                    var pending = _tasks.Where(t => t.Status != TaskStatus.Completed).ToList();
                    foreach (var task in pending)
                    {
                        if (!_batchConverting) break;
                        var card = _cards.FirstOrDefault(c => c.Task == task);
                        await RunTaskConversion(task, card);
                    }
                }
            }
            finally
            {
                _batchConverting = false;
                convertAllButton.Enabled = true;
                convertAllButton.Text = "全部转换";
                addFilesButton.Enabled = true;
                UpdateCount();
            }
        }

        /// <summary>
        /// 合并所有文件模式：逐文件预处理到临时目录（可流复制则流复制，否则转码），
        /// 全部完成后用 concat demuxer + -c copy 合并为最终输出。
        /// </summary>
        /// <summary>
        /// 根据目标 ffmpeg 编码器名推断对应的 ffprobe 源编码器名（小写）。
        /// 用于合并模式判断是否可流复制：只有源编码器与目标属同一家族时才允许 copy。
        /// 例如目标 libx264/h264_nvenc → 源须为 h264；wmv3 等不兼容编码器会强制重编码。
        /// </summary>
        private static string GetExpectedSourceCodec(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return null;
            string e = encoder.ToLowerInvariant();
            if (e.Contains("264")) return "h264";
            if (e.Contains("265") || e.Contains("hevc")) return "hevc";
            if (e.Contains("vp9")) return "vp9";
            if (e.Contains("av1")) return "av1";
            if (e.Contains("vp8")) return "vp8";
            if (e.Contains("mpeg2")) return "mpeg2video";
            if (e.Contains("mpeg4")) return "mpeg4";
            if (e.Contains("mjpeg")) return "mjpeg";
            if (e.Contains("prores")) return "prores";
            return null;
        }

        /// <summary>解析帧率字符串（如 "30"、"30000/1001"）为 double。</summary>
        private static double? ParseFrameRate(string fps)
        {
            if (string.IsNullOrEmpty(fps)) return null;
            fps = fps.Trim();
            int slash = fps.IndexOf('/');
            if (slash > 0)
            {
                if (double.TryParse(fps.Substring(0, slash), NumberStyles.Any, CultureInfo.InvariantCulture, out double num)
                    && double.TryParse(fps.Substring(slash + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double den)
                    && den != 0)
                    return num / den;
                return null;
            }
            if (double.TryParse(fps, NumberStyles.Any, CultureInfo.InvariantCulture, out double val))
                return val;
            return null;
        }

        private async Task RunMergeAllConversion()
        {
            var preset = _globalPreset ?? PresetOption.MP4_1080;
            var pending = _tasks.Where(t => t.Status != TaskStatus.Completed).ToList();
            if (pending.Count < 2)
            {
                MessageBox.Show(this, "合并所有文件需要至少 2 个文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // ---- 1. 探测所有文件的完整媒体信息（含 pix_fmt / 标称帧率） ----
            var infos = new List<MediaInfo>();
            foreach (var task in pending)
            {
                var card = _cards.FirstOrDefault(c => c.Task == task);
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.StatusLabel.Visible = true;
                    card.StatusLabel.Text = "探测媒体信息...";
                    card.StatusLabel.ForeColor = Color.FromArgb(120, 90, 200);
                }
                try
                {
                    var info = await FFmpegHelper.ProbeDetailedAsync(task.InputPath);
                    infos.Add(info);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "无法探测文件:\n" + task.InputPath + "\n" + ex.Message,
                        "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    foreach (var c in _cards.Where(c2 => pending.Contains(c2.Task)))
                    {
                        if (c != null && !c.Panel.IsDisposed) c.StatusLabel.Visible = false;
                    }
                    return;
                }
            }

            // ---- 确定输出路径 ----
            string outResolution;
            string ext = preset.GetExtension();
            string saveFolder = GetSelectedSaveToFolder();
            if (string.IsNullOrEmpty(saveFolder))
                saveFolder = Path.GetDirectoryName(pending[0].InputPath);
            string baseName = Path.GetFileNameWithoutExtension(pending[0].InputPath) + "_join";
            string finalOutput = GetUniqueFilePath(Path.Combine(saveFolder, baseName + ext));

            // ---- 诊断日志初始化（exe 同目录，加时间戳）----
            string mergeLogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                baseName + "_merge_log_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".txt");
            var mergeLog = new StringBuilder();
            Action flushMergeLog = () =>
            {
                try { System.IO.File.WriteAllText(mergeLogPath, mergeLog.ToString(), Encoding.UTF8); }
                catch (Exception e2) { System.Diagnostics.Debug.WriteLine("合并日志写入失败: " + e2.Message); }
            };
            mergeLog.AppendLine("==== 合并所有文件 诊断日志 ====");
            mergeLog.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            mergeLog.AppendLine("最终输出: " + finalOutput);
            mergeLog.AppendLine("输出扩展名: " + ext);

            // ---- 创建临时目录（exe 同目录）----
            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "vc_merge_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Directory.CreateDirectory(tempDir);
            mergeLog.AppendLine("临时目录: " + tempDir + "  （合并成功/失败后删除）");

            // ---- 解析目标编码器（预设 auto/copy/空 → 容器默认值）----
            string containerKey = FFmpegHelper.GetContainerKey(preset);
            string presetVCodec = preset.VideoCodec;
            if (string.IsNullOrEmpty(presetVCodec) ||
                string.Equals(presetVCodec, "copy", StringComparison.OrdinalIgnoreCase))
                presetVCodec = DefaultCodecSettings.GetContainerVideoEncoder(containerKey) ?? "H264";
            string presetACodec = preset.AudioCodec;
            if (string.IsNullOrEmpty(presetACodec) ||
                string.Equals(presetACodec, "copy", StringComparison.OrdinalIgnoreCase))
                presetACodec = DefaultCodecSettings.GetContainerAudioEncoder(containerKey) ?? "aac";

            string vEncoder = FFmpegHelper.ResolveVideoEncoder(
                presetVCodec.ToUpperInvariant(),
                hardwareCheck.Checked ? _hwSupport : null,
                preset.VideoCodecLabel);
            string aEncoder = string.IsNullOrEmpty(presetACodec)
                ? null
                : FFmpegHelper.ResolveAudioEncoder(presetACodec);

            string expectedSrcCodec = GetExpectedSourceCodec(vEncoder);
            string expectedSrcACodec = aEncoder != null
                ? FFmpegHelper.NormalizeAudioCodec(aEncoder)
                : null;

            // ---- 2. 确定统一输出参数 ----
            // 像素格式：总是取第 1 个文件的像素格式。
            // 分辨率/帧率：预设指定值，自动 → 第 1 个文件。
            // 采样率：预设指定值，自动 → max(预设默认, 第1个文件)。
            // 声道：预设指定值，自动 → 预设默认。
            // 视频/音频比特率：预设显式指定才使用，auto 时不加 -b:v/-b:a（仅用于 copy 判定）。

            var firstInfo = infos[0];
            var firstAudio = firstInfo.AudioTracks.FirstOrDefault();

            string outPixFmt = !string.IsNullOrEmpty(firstInfo.PixelFormat)
                ? firstInfo.PixelFormat : "yuv420p";

            if (!string.IsNullOrEmpty(preset.ResolutionValue))
                outResolution = preset.ResolutionValue;
            else
                outResolution = firstInfo.Width + "x" + firstInfo.Height;

            bool presetFpsIsAuto = string.IsNullOrEmpty(preset.FrameRate)
                || preset.FrameRate == "0" || preset.FrameRate == "-1";
            string outFrameRate;
            if (!presetFpsIsAuto)
                outFrameRate = preset.FrameRate;
            else
                outFrameRate = firstInfo.NominalFrameRate > 0
                    ? firstInfo.NominalFrameRate.ToString("0.###", CultureInfo.InvariantCulture)
                    : null;

            int defaultSR = DefaultCodecSettings.GetDefaultSampleRate();
            bool presetSrIsAuto = string.IsNullOrEmpty(preset.SampleRate)
                || preset.SampleRate == "-1" || preset.SampleRate == "0";
            int outSampleRate;
            if (!presetSrIsAuto)
                outSampleRate = int.Parse(preset.SampleRate, CultureInfo.InvariantCulture);
            else
            {
                int firstSR = firstAudio != null ? firstAudio.SampleRate : 0;
                outSampleRate = Math.Max(defaultSR, firstSR);
            }

            bool presetChIsAuto = preset.Channels <= 0;
            int outChannels = presetChIsAuto
                ? DefaultCodecSettings.GetDefaultChannels()
                : preset.Channels;

            bool videoBitrateExplicit = !string.IsNullOrEmpty(preset.VideoBitrate)
                && preset.VideoBitrate != "0" && preset.VideoBitrate != "-1"
                && !string.Equals(preset.VideoBitrate, "auto", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(preset.VideoBitrate, "copy", StringComparison.OrdinalIgnoreCase);
            string outVideoBitrate = videoBitrateExplicit ? preset.VideoBitrate : null;

            bool audioBitrateExplicit = !string.IsNullOrEmpty(preset.AudioBitrate)
                && preset.AudioBitrate != "0" && preset.AudioBitrate != "-1"
                && !string.Equals(preset.AudioBitrate, "auto", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(preset.AudioBitrate, "copy", StringComparison.OrdinalIgnoreCase);
            string outAudioBitrate = audioBitrateExplicit ? preset.AudioBitrate : null;

            // faststart：只要输出是 MP4/MOV/M4V 等 ISO 封装，每文件都加。
            bool perFileFastStart = FFmpegHelper.NeedsFastStart("x" + ext);

            // ---- 2.1 合并输出统一参数打印到日志 ----
            mergeLog.AppendLine("---- 合并输出统一参数（各文件统一）----");
            mergeLog.AppendLine("  视频编码器: " + vEncoder);
            mergeLog.AppendLine("  像素格式: " + outPixFmt);
            mergeLog.AppendLine("  分辨率: " + outResolution);
            mergeLog.AppendLine("  帧率: " + (outFrameRate ?? "(源无帧率)") + (presetFpsIsAuto ? " [取自第1个文件]" : ""));
            mergeLog.AppendLine("  视频比特率: " + (videoBitrateExplicit ? outVideoBitrate : "auto（不强制）"));
            mergeLog.AppendLine("  音频编码器: " + (aEncoder ?? "(无音频)"));
            mergeLog.AppendLine("  采样率: " + outSampleRate + " Hz" + (presetSrIsAuto ? " [max(默认" + defaultSR + ", 源" + (firstAudio != null ? firstAudio.SampleRate.ToString() : "0") + ")]" : ""));
            mergeLog.AppendLine("  声道: " + outChannels);
            mergeLog.AppendLine("  音频比特率: " + (audioBitrateExplicit ? outAudioBitrate : "auto（不强制）"));
            mergeLog.AppendLine("  每文件 faststart: " + (perFileFastStart ? "已启用" : "不适用"));
            mergeLog.AppendLine("  高速转换模式: " + (highSpeedCheck.Checked ? "已启用（符合条件可流复制）" : "未启用（全部强制重编码）"));

            // ---- 3. 逐文件预处理到临时目录 ----
            var tempFiles = new List<string>();
            double totalDuration = infos.Sum(i => i.DurationSeconds);
            double elapsed = 0;

            for (int i = 0; i < pending.Count; i++)
            {
                if (!_batchConverting) break;

                var task = pending[i];
                var info = infos[i];
                var card = _cards.FirstOrDefault(c => c.Task == task);
                string tempFile = Path.Combine(tempDir, (i + 1).ToString("D4") + ext);
                tempFiles.Add(tempFile);

                // 视频流复制判定：
                //   bitrate=auto && 编码器同族 && 像素格式一致 && 分辨率一致 && 帧率一致
                string srcPixelFmt = info.PixelFormat ?? "";
                double srcFps = info.NominalFrameRate;
                double? outFpsVal = outFrameRate != null
                    ? ParseFrameRate(outFrameRate) : (double?)null;
                bool fpsMatch = !outFpsVal.HasValue
                    || Math.Abs(srcFps - outFpsVal.Value) < 0.05;

                bool vCopy = !videoBitrateExplicit
                    && !string.IsNullOrEmpty(expectedSrcCodec)
                    && string.Equals(info.VideoCodec, expectedSrcCodec, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(srcPixelFmt, outPixFmt, StringComparison.OrdinalIgnoreCase)
                    && (info.Width + "x" + info.Height) == outResolution
                    && fpsMatch;

                // 音频流复制判定：
                //   bitrate=auto && 编码器同族 && 采样率一致 && 声道一致
                var at = info.AudioTracks.FirstOrDefault();
                bool hasAudio = at != null && !string.IsNullOrEmpty(expectedSrcACodec);
                bool aCopy = hasAudio
                    && !audioBitrateExplicit
                    && string.Equals(FFmpegHelper.NormalizeAudioCodec(at.Codec), expectedSrcACodec, StringComparison.OrdinalIgnoreCase)
                    && at.SampleRate == outSampleRate
                    && at.Channels == outChannels;

                // "高速转换"未勾选 → 强制全部重编码，不判断流复制条件。
                if (!highSpeedCheck.Checked)
                {
                    vCopy = false;
                    aCopy = false;
                }

                if (card != null && !card.Panel.IsDisposed)
                {
                    card.IsConverting = true;
                    card.ProgressBar.Visible = true;
                    card.ProgressBar.Value = 0;
                    card.StatusLabel.Visible = true;
                    card.StatusLabel.ForeColor = Color.FromArgb(120, 90, 200);
                    string mode = vCopy && aCopy ? "流复制" : "转码";
                    card.StatusLabel.Text = string.Format("合并预处理 {0}/{1} ({2})...", i + 1, pending.Count, mode);
                }

                // 重编码时用统一参数；流复制时对应侧参数不生效。
                string args = FFmpegHelper.BuildMergeAllFileArguments(
                    task.InputPath, tempFile,
                    vEncoder, outPixFmt, vCopy ? null : outResolution,
                    vCopy ? null : outFrameRate,
                    vCopy, vCopy ? null : outVideoBitrate,
                    aEncoder, outSampleRate, outChannels,
                    aCopy, aCopy ? null : outAudioBitrate,
                    hasAudio, perFileFastStart);

                // 合并预处理统一限制视频编码线程数为 4，避免多实例并发时
                // libx264 自动检测 threads=34 导致 Access Violation / Stack Smashing。
                if (!vCopy)
                    args += " -threads:v 4";

                mergeLog.AppendLine(string.Format("---- 文件 #{0}: {1} ----", i + 1, Path.GetFileName(task.InputPath)));
                mergeLog.AppendLine(string.Format("  源视频: {0}, {1}, {2}x{3}, 标称帧率 {4:0.###} fps",
                    info.VideoCodec, info.PixelFormat, info.Width, info.Height, info.NominalFrameRate));
                if (at != null)
                    mergeLog.AppendLine(string.Format("  源音频: {0}, 采样率 {1} Hz, 声道 {2}", at.Codec, at.SampleRate, at.Channels));
                else
                    mergeLog.AppendLine("  源音频: 无");
                mergeLog.AppendLine(string.Format("  处理模式: 视频 {0}, 音频 {1}{2}",
                    vCopy ? "流复制(-c:v copy)" : "转码",
                    aCopy ? "流复制(-c:a copy)" : (hasAudio ? "转码" : "无音频(-an)"),
                    highSpeedCheck.Checked ? "" : " [高速转换未勾选，强制重编码]"));
                mergeLog.AppendLine("  ffmpeg 参数: " + args);
                if (perFileFastStart)
                    mergeLog.AppendLine("  快速启动: -movflags +faststart 已启用");

                double fileDuration = info.DurationSeconds > 0 ? info.DurationSeconds : 1;
                double fileBase = elapsed;

                // 先让 UI 刷新进度条初始状态（0%），避免快速操作时进度条一闪而过。
                await Task.Yield();

                // 进度回调每次重新查找卡片，避免闭包捕获的 card 引用在
                // RefreshTaskList 等操作后变为已释放而丢失进度更新。
                var progress = new Progress<double>(p =>
                {
                    var curCard = _cards.FirstOrDefault(c => c.Task == task);
                    if (curCard != null && !curCard.Panel.IsDisposed && curCard.ProgressBar != null)
                    {
                        double overall = (fileBase + p * fileDuration) / System.Math.Max(1, totalDuration);
                        int v = (int)(overall * 100);
                        if (v < 0) v = 0;
                        if (v > 100) v = 100;
                        try { curCard.ProgressBar.Value = v; } catch { }
                    }
                });

                bool fileOk = false;
                try
                {
                    await FFmpegHelper.RunCommandAsync(args, fileDuration, progress, CancellationToken.None);
                    fileOk = true;
                }
                catch (Exception ex)
                {
                    // ffmpeg 自身崩溃（Access Violation / Stack Smashing 等 0xC0000xxx）：
                    // Windows 崩溃码映射为 -107374xxxx，降级用 -threads:v 4 重试一次。
                    bool isCrash = ex.Message.IndexOf("-107374", StringComparison.Ordinal) >= 0;
                    if (isCrash)
                    {
                        mergeLog.AppendLine("  [crash 降级重试] " + args + " -threads:v 4 -err_detect ignore_err");
                        // 重置进度条到文件起点，让用户看到重试进度。
                        if (card != null && !card.Panel.IsDisposed)
                        {
                            card.ProgressBar.Value = (int)(elapsed / System.Math.Max(1, totalDuration) * 100);
                            card.StatusLabel.Text = string.Format("合并预处理 {0}/{1} (降级重试)...", i + 1, pending.Count);
                        }
                        try
                        {
                            await FFmpegHelper.RunCommandAsync(
                                args + " -threads:v 4 -err_detect ignore_err",
                                fileDuration, progress, CancellationToken.None);
                            fileOk = true;
                            mergeLog.AppendLine("  降级重试成功");
                        }
                        catch (Exception ex2)
                        {
                            mergeLog.AppendLine("  降级重试也失败: " + ex2.Message);
                            ex = ex2;
                        }
                    }

                    if (!fileOk)
                    {
                        // 单个文件失败：标记该任务为 Failed，中止整个合并。
                        task.Status = TaskStatus.Failed;
                        if (card != null && !card.Panel.IsDisposed)
                        {
                            card.StatusLabel.Text = "失败: " + ex.Message;
                            card.StatusLabel.ForeColor = Color.Red;
                            card.IsConverting = false;
                        }
                        for (int j = i + 1; j < pending.Count; j++)
                        {
                            if (pending[j].Status != TaskStatus.Completed)
                                pending[j].Status = TaskStatus.Pending;
                            var jc = _cards.FirstOrDefault(c => c.Task == pending[j]);
                            if (jc != null && !jc.Panel.IsDisposed)
                            {
                                jc.IsConverting = false;
                                jc.StatusLabel.Visible = false;
                                jc.ProgressBar.Visible = false;
                            }
                        }
                        try { Directory.Delete(tempDir, true); } catch { }
                        mergeLog.AppendLine();
                        mergeLog.AppendLine("======== 预处理失败 ========");
                        mergeLog.AppendLine("文件: " + task.InputPath);
                        mergeLog.AppendLine("错误详情: " + ex.Message);
                        flushMergeLog();
                        MessageBox.Show(this,
                            "合并预处理失败，已中止:\n" + Path.GetFileName(task.InputPath) + "\n\n" + ex.Message + "\n\n诊断日志:\n" + mergeLogPath,
                            "合并错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        if (_showCompleted) RefreshTaskList();
                        UpdateCount();
                        return;
                    }
                }

                // 重新探测临时输出，记录实际输出媒体信息到诊断日志。
                try
                {
                    var outInfo = await FFmpegHelper.ProbeDetailedAsync(tempFile);
                    mergeLog.AppendLine(string.Format("  [输出#{0}] {1}", i + 1, Path.GetFileName(tempFile)));
                    mergeLog.AppendLine(string.Format("    视频: {0}, {1}, {2}x{3}, 标称帧率 {4:0.###} fps",
                        outInfo.VideoCodec, outInfo.PixelFormat, outInfo.Width, outInfo.Height, outInfo.NominalFrameRate));
                    var oat = outInfo.AudioTracks.FirstOrDefault();
                    if (oat != null)
                        mergeLog.AppendLine(string.Format("    音频: {0}, 采样率 {1} Hz, 声道 {2}", oat.Codec, oat.SampleRate, oat.Channels));
                    else
                        mergeLog.AppendLine("    音频: 无");
                }
                catch (Exception pex)
                {
                    mergeLog.AppendLine(string.Format("  [输出#{0}] 重新探测失败: {1}", i + 1, pex.Message));
                }

                elapsed += fileDuration;
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.StatusLabel.Text = "预处理完成";
                    card.ProgressBar.Value = 100;
                }
                // 每完成一个文件立即刷盘，确保处理过程中随时可查看日志。
                flushMergeLog();
            }

            if (!_batchConverting)
            {
                flushMergeLog();
                try { Directory.Delete(tempDir, true); } catch { }
                return;
            }

            // ---- 9. 写入 concat 列表（严格按列表顺序） ----
            string concatList = Path.Combine(tempDir, "concat_list.txt");
            FFmpegHelper.WriteSimpleConcatList(tempFiles, concatList);
            mergeLog.AppendLine("---- concat 列表 ----");
            mergeLog.AppendLine("列表文件: " + concatList);
            for (int k = 0; k < tempFiles.Count; k++)
                mergeLog.AppendLine("  " + (k + 1) + ". " + tempFiles[k]);

            // ---- 9.1 生成章节元数据（合并模式：以文件名为章节名，文件时长为章节时长） ----
            // 仅当用户开启"保留章节"且输出格式支持章节时才生成。
            string metadataPath = null;
            string outExt = Path.GetExtension(finalOutput).ToLowerInvariant();
            bool formatSupportsChapter = outExt == ".mp4" || outExt == ".mkv" || outExt == ".mov" ||
                                         outExt == ".m4v" || outExt == ".m4b" || outExt == ".m4a";
            if (AppSettings.KeepChapterMarkers && formatSupportsChapter)
            {
                var chapters = new List<ChapterInfo>();
                double accSeconds = 0;
                for (int i = 0; i < pending.Count; i++)
                {
                    double dur = infos[i].DurationSeconds > 0 ? infos[i].DurationSeconds : 0;
                    long startMs = (long)(accSeconds * 1000);
                    long endMs = (long)((accSeconds + dur) * 1000);
                    // 章节标题优先用文件名（不含扩展名），为空时回退 "Chapter N"
                    string title = Path.GetFileNameWithoutExtension(pending[i].InputPath);
                    if (string.IsNullOrWhiteSpace(title)) title = "Chapter " + (i + 1);
                    chapters.Add(new ChapterInfo
                    {
                        Index = i,
                        StartMs = startMs,
                        EndMs = endMs,
                        Title = title
                    });
                    accSeconds += dur;
                }
                string ffmeta = FFmpegHelper.GenerateFfmetadata(chapters);
                if (!string.IsNullOrEmpty(ffmeta))
                {
                    metadataPath = Path.Combine(tempDir, "chapters_metadata.txt");
                    File.WriteAllText(metadataPath, ffmeta, new UTF8Encoding(false));
                    mergeLog.AppendLine("章节元数据: " + metadataPath + " (KeepChapterMarkers=" + AppSettings.KeepChapterMarkers + ")");
                }
            }

            // ---- 10. 最终合并 (concat -c copy) ----
            foreach (var task in pending)
            {
                var card = _cards.FirstOrDefault(c => c.Task == task);
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.StatusLabel.Text = "最终合并中...";
                    card.StatusLabel.ForeColor = Color.FromArgb(120, 90, 200);
                }
            }

            string concatArgs = FFmpegHelper.BuildConcatCopyWithChaptersArguments(concatList, metadataPath, finalOutput);
            mergeLog.AppendLine("---- 最终合并参数 ----");
            mergeLog.AppendLine("命令: ffmpeg " + concatArgs);
            mergeLog.AppendLine("快速启动(-movflags +faststart): " +
                (concatArgs.Contains("+faststart") ? "已启用（moov 原子前置，修复拖动进度条卡死）"
                                                    : "未启用（非 MP4/MOV/M4V 等 ISO 封装或该方法未加）"));
            try
            {
                await FFmpegHelper.RunCommandAsync(concatArgs, 0, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                mergeLog.AppendLine();
                mergeLog.AppendLine("======== 最终合并失败 ========");
                mergeLog.AppendLine("错误详情: " + ex.Message);
                flushMergeLog();
                MessageBox.Show(this, "最终合并失败:\n" + ex.Message + "\n\n诊断日志:\n" + mergeLogPath, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                try { Directory.Delete(tempDir, true); } catch { }
                foreach (var task in pending)
                {
                    var card = _cards.FirstOrDefault(c => c.Task == task);
                    if (card != null && !card.Panel.IsDisposed)
                    {
                        card.StatusLabel.Text = "合并失败";
                        card.StatusLabel.ForeColor = Color.Red;
                        card.IsConverting = false;
                    }
                }
                return;
            }

            // ---- 11. 标记完成 + 清理临时目录 ----
            foreach (var task in pending)
            {
                task.Status = TaskStatus.Completed;
                var card = _cards.FirstOrDefault(c => c.Task == task);
                if (card != null && !card.Panel.IsDisposed)
                {
                    card.IsConverting = false;
                    card.StatusLabel.Text = "已合并";
                    card.StatusLabel.ForeColor = Color.Green;
                    card.ProgressBar.Value = 100;
                    card.ConvertButton.Text = "转换";
                }
            }

            try { Directory.Delete(tempDir, true); } catch { }

            flushMergeLog();
            MessageBox.Show(this, "所有文件已合并到:\n" + finalOutput +
                "\n\n诊断日志已保存:\n" + mergeLogPath, "合并完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);

            RefreshTaskList();  // 无论哪个页签都刷新，避免卡片残留旧状态
            UpdateCount();
        }

        private async void ConvertSingleTask(ConversionTask task, TaskCard card)
        {
            if (card == null) return;

            if (card.IsConverting)
            {
                // Cancel.
                try { task.Cancellation?.Cancel(); } catch { }
                return;
            }

            await RunTaskConversion(task, card);
        }

        private async Task RunTaskConversion(ConversionTask task, TaskCard card)
        {
            if (card == null) return;
            if (task.Status == TaskStatus.Converting) return;

            // 转换前一次 ffprobe 探测：源视频/音频编码（智能 copy 判定）+ VC-1 容错。#74
            MediaInfo srcInfo = null;
            try { srcInfo = await FFmpegHelper.ProbeDetailedAsync(task.InputPath); }
            catch { }
            task.SourceVideoCodec = srcInfo?.VideoCodec;
            task.SourceAudioCodec = task.SelectedAudioTrack?.Codec
                ?? srcInfo?.AudioTracks?.FirstOrDefault()?.Codec;
            task.IsVC1Input = FFmpegHelper.IsVC1Codec(srcInfo?.VideoCodec);
            // 章节保留：把 ffprobe 探测到的章节列表复制到任务上，供 BuildSegmentArguments 判定注入。
            task.Chapters = srcInfo?.Chapters ?? new List<ChapterInfo>();
            task.PreserveChapters = AppSettings.KeepChapterMarkers;

            // Decide per-task conversion mode.
            task.UseStreamCopy = AppliesStreamCopy(task);
            task.HardwareEncoder = GetHardwareEncoderFor(task);
            task.TargetVideoEncoder = FFmpegHelper.ResolveTargetVideoEncoder(task, _hwSupport, hardwareCheck.Checked);
            task.TargetAudioEncoder = FFmpegHelper.ResolveTargetAudioEncoder(task);
            task.Cancellation = new CancellationTokenSource();
            var token = task.Cancellation.Token;

            card.IsConverting = true;
            card.ConvertButton.Text = "取消";
            card.ProgressBar.Visible = true;
            card.StatusLabel.Visible = true;

            string statusText;
            if (task.UseStreamCopy)
            {
                // 高速转换模式下，视频/音频流各自判定是否实际走 copy。
                // 只有视频流和音频流都 copy 才显示"流复制"；否则显示实际编码模式。
                bool vCopy, aCopy;
                FFmpegHelper.EvaluateSmartCopy(task, out vCopy, out aCopy);
                if (vCopy && aCopy)
                    statusText = "流复制模式 (高速转换)...";
                else if (vCopy)
                    statusText = "视频流复制 / 音频转码中...";
                else if (aCopy)
                    statusText = "视频转码 / 音频流复制中...";
                else if (!string.IsNullOrEmpty(task.HardwareEncoder))
                    statusText = "硬件编码中 (" + task.HardwareEncoder + ")...";
                else
                    statusText = "转码中 (" + (task.TargetVideoEncoder ?? "?") + ")...";
            }
            else if (hardwareCheck.Checked && !string.IsNullOrEmpty(task.HardwareEncoder))
                statusText = "硬件编码中 (" + task.HardwareEncoder + ")...";
            else if (hardwareCheck.Checked)
                statusText = "硬件编码不支持，使用软件...";
            else
                statusText = "转码中 (" + (task.TargetVideoEncoder ?? "?") + ")...";
            card.StatusLabel.Text = statusText;

            task.Status = TaskStatus.Converting;
            // 进度回调按任务查找"当前"卡片：转换中列表可能被重建（切换页签），
            // 闭包捕获的旧 card 已释放，必须实时取最新卡片，否则进度条在重建后失效。#95
            var progress = new Progress<double>(p =>
            {
                var live = GetCard(task);
                if (live != null && !live.Panel.IsDisposed && live.ProgressBar != null)
                {
                    int v = (int)(p * 100);
                    if (v < 0) v = 0;
                    if (v > 100) v = 100;
                    try { live.ProgressBar.Value = v; } catch { }
                }
            });

            try
            {
                EnsureUniqueOutputPath(task);
                EnsureOutputDirectory(task);

                // 转换主流程：最多尝试 2 次。首次使用硬件编码失败时，自动降级为
                // CPU 编码器（hard_codec_settings.json 中对应 label 的 cpuEncoders）重试一次。#82
                bool completed = false;
                Exception lastError = null;
                for (int attempt = 0; attempt < 2 && !completed; attempt++)
                {
                    try
                    {
                        await FFmpegHelper.RunAsync(task, progress, token);
                        task.Status = TaskStatus.Completed;
                        completed = true;
                        var sc = GetCard(task);
                        if (sc != null && !sc.Panel.IsDisposed)
                        {
                            sc.StatusLabel.Text = attempt == 0 ? "✓ 转换成功" : "✓ 转换成功（已降级软件编码）";
                            sc.StatusLabel.ForeColor = Color.FromArgb(60, 150, 90);
                            sc.ProgressBar.Value = 100;
                        }
                        // Refresh the completed card's metadata from the real output file. #93
                        await UpdateTaskFromOutputAsync(task);
                        if (_showCompleted)
                            RefreshTaskList();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastError = ex;
                        // 首次且确为硬件编码失败时，集中式判定并就地降级为 CPU 编码器重试一次。
                        // 边界判断（流复制/非硬件/已取消不重试）已收口到 FFmpegHelper.ApplyHardwareFallback。
                        bool canFallback = attempt == 0
                            && !token.IsCancellationRequested
                            && FFmpegHelper.ApplyHardwareFallback(task, _hwSupport, hardwareCheck.Checked);
                        if (canFallback)
                        {
                            var fc = GetCard(task);
                            if (fc != null && !fc.Panel.IsDisposed)
                                fc.StatusLabel.Text = "硬件编码失败，降级为软件 (" + task.TargetVideoEncoder + ")...";
                            EnsureOutputDirectory(task);
                            continue;
                        }
                        break;
                    }
                }

                if (!completed)
                {
                    task.Status = TaskStatus.Failed;
                    var fc = GetCard(task);
                    if (fc != null && !fc.Panel.IsDisposed)
                    {
                        fc.StatusLabel.Text = "失败: " + (lastError != null ? lastError.Message : "未知错误");
                        fc.StatusLabel.ForeColor = Color.Red;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                task.Status = TaskStatus.Pending;
                var cc = GetCard(task);
                if (cc != null && !cc.Panel.IsDisposed)
                {
                    cc.StatusLabel.Text = "已取消";
                    cc.StatusLabel.ForeColor = Color.Gray;
                }
            }
            catch (Exception ex)
            {
                task.Status = TaskStatus.Failed;
                var fc = GetCard(task);
                if (fc != null && !fc.Panel.IsDisposed)
                {
                    fc.StatusLabel.Text = "失败: " + ex.Message;
                    fc.StatusLabel.ForeColor = Color.Red;
                }
            }
            finally
            {
                var fc = GetCard(task);
                if (fc != null && !fc.Panel.IsDisposed)
                {
                    fc.IsConverting = false;
                    fc.ConvertButton.Text = (task.Status == TaskStatus.Converting) ? "取消" : "转换";
                    if (task.Status == TaskStatus.Completed || task.Status == TaskStatus.Failed)
                    {
                        // 完成后保留状态标签（"✓ 转换成功"/"失败"）可见，仅延迟隐藏进度条，
                        // 让用户明确看到转换结果。#95
                        _ = Task.Delay(1200).ContinueWith(_ =>
                        {
                            if (fc.Panel.IsHandleCreated && !fc.Panel.IsDisposed)
                                fc.Panel.Invoke((Action)(() =>
                                {
                                    if (!fc.IsConverting) fc.ProgressBar.Visible = false;
                                }));
                        });
                    }
                    else
                    {
                        // 取消 / 回到待处理：隐藏进度条与状态。
                        fc.ProgressBar.Visible = false;
                        fc.StatusLabel.Visible = false;
                    }
                }
                task.Cancellation?.Dispose();
                task.Cancellation = null;
            }

            // 全部任务都已成功完成 → 自动切到"转换完成"页签，让"正在转换"页签清空、
            // 用户直接看到结果（避免转换完列表里还残留卡片）。#95
            if (!_showCompleted && _tasks.Count > 0 &&
                _tasks.All(t => t.Status == TaskStatus.Completed))
            {
                _showCompleted = true;
                UpdateTabStyles();
                RefreshTaskList();
            }
        }

        private void EnsureOutputDirectory(ConversionTask task)
        {
            foreach (var path in task.GetOutputPaths())
            {
                string folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder))
                    Directory.CreateDirectory(folder);
            }
        }

        /// <summary>
        /// 输出文件名冲突处理：当目标路径已存在时，自动追加序号 (1)、(2)… 避免覆盖。
        /// 仅对单输出（合并模式或单段）做唯一化；多段输出已带 _N 序号，罕见冲突。
        /// </summary>
        private void EnsureUniqueOutputPath(ConversionTask task)
        {
            var paths = task.GetOutputPaths();
            if (paths.Count == 0) return;

            // 单输出：唯一化后写回 task.OutputPath。
            if (paths.Count == 1)
            {
                string unique = GetUniqueFilePath(paths[0]);
                if (!string.Equals(unique, paths[0], StringComparison.OrdinalIgnoreCase))
                    task.OutputPath = unique;
                return;
            }

            // 多段输出：基于 base path 派生，若任一冲突则给 base name 加序号。
            bool anyConflict = false;
            foreach (var p in paths)
            {
                if (File.Exists(p)) { anyConflict = true; break; }
            }
            if (!anyConflict) return;

            string dir = Path.GetDirectoryName(task.OutputPath);
            string nameNoExt = Path.GetFileNameWithoutExtension(task.OutputPath);
            string ext = Path.GetExtension(task.OutputPath);
            int suffix = 1;
            while (true)
            {
                string candidateBase = Path.Combine(dir, nameNoExt + "(" + suffix + ")" + ext);
                var candidatePaths = new List<string>();
                for (int i = 0; i < paths.Count; i++)
                    candidatePaths.Add(Path.Combine(dir, nameNoExt + "(" + suffix + ")_" + (i + 1) + ext));
                bool ok = true;
                foreach (var cp in candidatePaths)
                {
                    if (File.Exists(cp)) { ok = false; break; }
                }
                if (ok)
                {
                    task.OutputPath = candidateBase;
                    return;
                }
                suffix++;
            }
        }

        /// <summary>返回不冲突的文件路径：已存在时追加 (1)、(2)… 序号。</summary>
        private static string GetUniqueFilePath(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string nameNoExt = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int suffix = 1;
            while (true)
            {
                string candidate = Path.Combine(dir, nameNoExt + "(" + suffix + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
                suffix++;
            }
        }

        #endregion

        #region Persistence (file-list store)

        /// <summary>
        /// Build a serializable DTO for one task. Converting/Failed are persisted as
        /// "Pending" so they re-queue on the next launch; only truly finished files
        /// are remembered as "Completed". #94
        /// </summary>
        private TaskListStore.TaskDto BuildTaskDto(ConversionTask task)
        {
            var dto = new TaskListStore.TaskDto
            {
                InputPath = task.InputPath,
                OutputPath = task.OutputPath,
                CustomOutputName = task.CustomOutputName,
                SaveToFolder = task.SaveToFolder,
                Status = task.Status == TaskStatus.Completed ? "Completed" : "Pending",
                HardwareEncoder = task.HardwareEncoder,
                UseStreamCopy = task.UseStreamCopy,
                Rotation = task.Rotation,
                MergeSegments = task.MergeSegments,
                AudioIndex = task.SelectedAudioTrack != null ? task.AudioTracks.IndexOf(task.SelectedAudioTrack) : -1,
                SubIndex = task.SelectedSubtitleTrack != null ? task.SubtitleTracks.IndexOf(task.SelectedSubtitleTrack) : -1,
                Preset = BuildPresetDto(task.Preset)
            };
            if (task.Crop != null)
                dto.Crop = new TaskListStore.CropDto { X = task.Crop.X, Y = task.Crop.Y, Width = task.Crop.Width, Height = task.Crop.Height };
            if (task.Segments != null && task.Segments.Count > 0)
                dto.Segments = task.Segments.Select(s => new TaskListStore.SegDto { StartMs = s.StartMs, EndMs = s.EndMs }).ToList();

            // P2 高级编码与元数据：持久化，重启后还原。
            dto.TwoPass = task.TwoPass;
            dto.Lossless = task.Lossless;
            dto.Deinterlace = task.Deinterlace;
            dto.H264Profile = task.H264Profile;
            dto.H264Level = task.H264Level;
            dto.SubtitleStyle = task.SubtitleStyle;
            dto.BurnSubtitle = task.BurnSubtitle;
            dto.SubMode = task.SubMode.ToString();
            dto.SubSettings = BuildSubtitleSettingsDto(task.SubtitleSettings);
            dto.MetaTitle = task.MetaTitle;
            dto.MetaAuthor = task.MetaAuthor;
            dto.MetaYear = task.MetaYear;
            dto.MetaComment = task.MetaComment;
            return dto;
        }

        private TaskListStore.SubtitleSettingsDto BuildSubtitleSettingsDto(SubtitleSettings s)
        {
            if (s == null) return null;
            return new TaskListStore.SubtitleSettingsDto
            {
                FontName = s.FontName,
                FontSize = s.FontSize,
                FontColorArgb = s.FontColorArgb,
                Bold = s.Bold,
                Italic = s.Italic,
                Underline = s.Underline,
                OutlineWidth = s.OutlineWidth,
                OutlineColorArgb = s.OutlineColorArgb,
                Transparency = s.Transparency,
                BackEnabled = s.BackEnabled,
                BackColorArgb = s.BackColorArgb,
                BackAlpha = s.BackAlpha,
                Alignment = s.Alignment,
                MarginV = s.MarginV,
                ExternalSubPath = s.ExternalSubPath
            };
        }

        private TaskListStore.PresetDto BuildPresetDto(PresetOption p)
        {
            if (p == null) return null;
            return new TaskListStore.PresetDto
            {
                Name = p.Name,
                FormatName = p.FormatName,
                Extension = p.Extension,
                VideoCodec = p.VideoCodec,
                VideoCodecLabel = p.VideoCodecLabel,
                AudioCodec = p.AudioCodec,
                CustomArgs = p.CustomArgs,
                ResolutionLabel = p.ResolutionLabel,
                ResolutionValue = p.ResolutionValue,
                VideoBitrate = p.VideoBitrate,
                BitrateMode = p.BitrateMode,
                QualityValue = p.QualityValue,
                QualityMaxRate = p.QualityMaxRate,
                AudioBitrate = p.AudioBitrate,
                FrameRate = p.FrameRate,
                Category = p.Category,
                PresetId = p.PresetId,
                FormatId = p.FormatId,
                FourCC = p.FourCC,
                KeepSource = p.KeepSource,
                SampleRate = p.SampleRate,
                Channels = p.Channels,
                IsBuiltIn = p.IsBuiltIn
            };
        }

        private void ApplyDtoToTask(ConversionTask task, TaskListStore.TaskDto dto)
        {
            // 与 AddFiles 一致，加载持久化任务时也规范化路径分隔符为反斜杠，
            // 兼容旧版本可能保存的正斜杠路径。
            task.InputPath = NormalizeBackslash(dto.InputPath);
            task.CustomOutputName = dto.CustomOutputName;
            task.SaveToFolder = NormalizeBackslash(dto.SaveToFolder);
            // Pin the exact output path so a reload reproduces the same result file.
            if (!string.IsNullOrEmpty(dto.OutputPath))
                task.OutputPath = NormalizeBackslash(dto.OutputPath);
            task.HardwareEncoder = dto.HardwareEncoder;
            task.UseStreamCopy = dto.UseStreamCopy;
            task.Rotation = dto.Rotation;
            task.MergeSegments = dto.MergeSegments;
            task.Status = string.Equals(dto.Status, "Completed", StringComparison.OrdinalIgnoreCase)
                ? TaskStatus.Completed
                : TaskStatus.Pending;
            if (dto.Crop != null)
                task.Crop = new CropRegion { X = dto.Crop.X, Y = dto.Crop.Y, Width = dto.Crop.Width, Height = dto.Crop.Height };
            if (dto.Segments != null)
                task.Segments = dto.Segments.Select(s => new VideoSegment { StartMs = s.StartMs, EndMs = s.EndMs }).ToList();

            // P2 还原（旧 v1/v2 文件缺这些字段 → 取默认值，行为与之前一致）。
            task.TwoPass = dto.TwoPass;
            task.Lossless = dto.Lossless;
            task.Deinterlace = dto.Deinterlace;
            task.H264Profile = dto.H264Profile;
            task.H264Level = dto.H264Level;
            task.SubtitleStyle = dto.SubtitleStyle;
            task.BurnSubtitle = dto.BurnSubtitle;

            // 字幕模式：v3 直接读 subMode；v1/v2 文件按旧字段推断（向后兼容）。
            SubtitleMode parsed;
            if (!string.IsNullOrEmpty(dto.SubMode) && Enum.TryParse(dto.SubMode, out parsed))
                task.SubMode = parsed;
            else if (dto.BurnSubtitle && !string.IsNullOrEmpty(dto.SubtitleStyle))
                task.SubMode = SubtitleMode.BurnExternal;
            else
                task.SubMode = SubtitleMode.None;

            // 字幕样式：v3 完整还原；旧文件 fallback 到默认。
            if (dto.SubSettings != null)
                task.SubtitleSettings = new SubtitleSettings
                {
                    FontName = dto.SubSettings.FontName,
                    FontSize = dto.SubSettings.FontSize,
                    FontColorArgb = dto.SubSettings.FontColorArgb,
                    Bold = dto.SubSettings.Bold,
                    Italic = dto.SubSettings.Italic,
                    Underline = dto.SubSettings.Underline,
                    OutlineWidth = dto.SubSettings.OutlineWidth,
                    OutlineColorArgb = dto.SubSettings.OutlineColorArgb,
                    Transparency = dto.SubSettings.Transparency,
                    BackEnabled = dto.SubSettings.BackEnabled,
                    BackColorArgb = dto.SubSettings.BackColorArgb,
                    BackAlpha = dto.SubSettings.BackAlpha,
                    Alignment = dto.SubSettings.Alignment,
                    MarginV = dto.SubSettings.MarginV,
                    ExternalSubPath = dto.SubSettings.ExternalSubPath
                };

            task.MetaTitle = dto.MetaTitle;
            task.MetaAuthor = dto.MetaAuthor;
            task.MetaYear = dto.MetaYear;
            task.MetaComment = dto.MetaComment;
            task.Preset = ApplyPresetDto(dto.Preset) ?? PresetOption.MP4_1080;
        }

        private PresetOption ApplyPresetDto(TaskListStore.PresetDto d)
        {
            if (d == null) return null;
            return new PresetOption
            {
                Name = d.Name,
                FormatName = d.FormatName,
                Extension = d.Extension,
                VideoCodec = d.VideoCodec,
                VideoCodecLabel = d.VideoCodecLabel,
                AudioCodec = d.AudioCodec,
                CustomArgs = d.CustomArgs,
                ResolutionLabel = d.ResolutionLabel,
                ResolutionValue = d.ResolutionValue,
                VideoBitrate = d.VideoBitrate,
                BitrateMode = d.BitrateMode,
                QualityValue = d.QualityValue,
                QualityMaxRate = d.QualityMaxRate,
                AudioBitrate = d.AudioBitrate,
                FrameRate = d.FrameRate,
                Category = d.Category,
                PresetId = d.PresetId,
                FormatId = d.FormatId,
                FourCC = d.FourCC,
                KeepSource = d.KeepSource,
                SampleRate = d.SampleRate,
                Channels = d.Channels,
                IsBuiltIn = d.IsBuiltIn
            };
        }

        private void ApplySelectedIndices(ConversionTask task, TaskListStore.TaskDto dto)
        {
            if (dto.AudioIndex >= 0 && dto.AudioIndex < task.AudioTracks.Count)
                task.SelectedAudioTrack = task.AudioTracks[dto.AudioIndex];
            else
                task.SelectedAudioTrack = task.AudioTracks.Count > 0 ? task.AudioTracks[0] : null;

            if (dto.SubIndex >= 0 && dto.SubIndex < task.SubtitleTracks.Count)
                task.SelectedSubtitleTrack = task.SubtitleTracks[dto.SubIndex];
            else
                task.SelectedSubtitleTrack = task.SubtitleTracks.Count > 0 ? task.SubtitleTracks[0] : null;
        }

        /// <summary>
        /// Re-probe the output file of a finished job and refresh the task's display
        /// metadata (format / resolution / size / audio tracks / thumbnail) so the
        /// completed card always reflects the real result. #93
        /// </summary>
        private async Task UpdateTaskFromOutputAsync(ConversionTask task)
        {
            string outPath = task.OutputPath;
            if (string.IsNullOrEmpty(outPath) || !File.Exists(outPath)) return;
            try
            {
                var info = await FFmpegHelper.ProbeDetailedAsync(outPath);
                if (info == null) return;
                task.SourceFormat = string.IsNullOrEmpty(info.VideoCodec)
                    ? Path.GetExtension(outPath).TrimStart('.').ToUpperInvariant()
                    : info.VideoCodec.ToUpperInvariant();
                if (info.Width > 0 && info.Height > 0)
                    task.SourceResolution = string.Format("{0} x {1}", info.Width, info.Height);
                if (info.SizeBytes > 0)
                    task.SourceFileSize = FFmpegHelper.FormatFileSize(info.SizeBytes);
                if (info.DurationSeconds > 0)
                {
                    task.SourceDurationSeconds = info.DurationSeconds;
                    task.SourceDuration = FFmpegHelper.FormatDuration(info.DurationSeconds);
                }
                if (info.AudioTracks != null && info.AudioTracks.Count > 0)
                    task.AudioTracks = info.AudioTracks;
                if (info.SubtitleTracks != null && info.SubtitleTracks.Count > 0)
                    task.SubtitleTracks = info.SubtitleTracks;
                if (task.SelectedAudioTrack == null && task.AudioTracks.Count > 0)
                    task.SelectedAudioTrack = task.AudioTracks[0];
                try { task.Thumbnail = await FFmpegHelper.GetThumbnailAsync(outPath, 160, 90); }
                catch { }
            }
            catch { }
        }

        /// <summary>
        /// Load the persisted file list and re-probe each entry (input for pending,
        /// output for completed) to restore metadata, tracks and thumbnail. #94
        /// 先立即创建任务并增量显示卡片（占位信息），再并行探测逐卡原地更新，
        /// 避免启动时长时间空白 / "几秒才刷新 1 个"。
        /// </summary>
        private async Task LoadTaskListAsync()
        {
            var dtos = TaskListStore.Load();
            if (dtos == null || dtos.Count == 0) return;

            // ---- Phase 1：先用占位信息创建任务并立即显示卡片 ----
            // 旧实现串行 ffprobe + 缩略图，且每任务全量 RefreshTaskList（O(N²) 重建），
            // 导致启动加载缓慢。改为：先建卡（占位元数据）→ 并行探测 → 逐卡原地更新。
            var pending = new List<KeyValuePair<ConversionTask, TaskListStore.TaskDto>>();
            foreach (var dto in dtos)
            {
                if (string.IsNullOrWhiteSpace(dto.InputPath) || !File.Exists(dto.InputPath))
                    continue;

                var task = new ConversionTask();
                ApplyDtoToTask(task, dto);

                string probeFile = (task.Status == TaskStatus.Completed) ? task.OutputPath : task.InputPath;
                if (task.Status == TaskStatus.Completed && string.IsNullOrEmpty(probeFile))
                    probeFile = task.InputPath;
                if (task.Status == TaskStatus.Completed && !string.IsNullOrEmpty(probeFile) && !File.Exists(probeFile))
                    probeFile = task.InputPath; // output missing (deleted) -> fall back to input

                // 占位元数据：探测完成后由 UpdateTaskCardContent 原地更新。
                task.SourceFormat = Path.GetExtension(probeFile).TrimStart('.').ToUpperInvariant();
                task.SourceResolution = "-";
                task.SourceFileSize = FFmpegHelper.FormatFileSize(new FileInfo(probeFile).Length);
                task.SourceDurationSeconds = 0;
                task.SourceDuration = "00:00:00";
                task.Thumbnail = CreatePlaceholderThumbnail();
                task.AudioTracks = new System.Collections.Generic.List<AudioTrackInfo>();
                task.SubtitleTracks = new System.Collections.Generic.List<SubtitleTrackInfo>();

                _tasks.Add(task);
                pending.Add(new KeyValuePair<ConversionTask, TaskListStore.TaskDto>(task, dto));
                AppendTaskCard(task);
            }

            if (pending.Count == 0) return;

            // ---- Phase 2：并行探测 + 缩略图，逐卡原地更新 ----
            // 并发上限由 FFmpegHelper 全局信号量统一约束（ProbeDetailedAsync / GetThumbnailAsync
            // 内部已接入 FFmpegHelper.MaxParallelFfmpeg），此处不再单独加锁以免重复占用槽位死锁。
            var probes = pending.Select(pair => ProbeAndPopulateRestoredTaskAsync(pair.Key, pair.Value)).ToArray();
            await Task.WhenAll(probes);

            // 自定义排序 / 搜索过滤激活时，增量追加可能破坏顺序或过滤，做一次全量重建。
            if (_sortMode != 0 || GetSearchKeyword() != null)
                RefreshTaskList();
        }

        /// <summary>
        /// 对单个恢复的任务执行 ffprobe 探测 + 外挂字幕检测 + 缩略图，
        /// 恢复真实元数据与选中轨道索引，完成后回到 UI 线程原地刷新卡片。
        /// 在启动加载 Phase 2 中并行调用。
        /// </summary>
        private async Task ProbeAndPopulateRestoredTaskAsync(ConversionTask task, TaskListStore.TaskDto dto)
        {
            string probeFile = (task.Status == TaskStatus.Completed) ? task.OutputPath : task.InputPath;
            if (task.Status == TaskStatus.Completed && string.IsNullOrEmpty(probeFile))
                probeFile = task.InputPath;
            if (task.Status == TaskStatus.Completed && !string.IsNullOrEmpty(probeFile) && !File.Exists(probeFile))
                probeFile = task.InputPath; // output missing (deleted) -> fall back to input

            MediaInfo info = null;
            try { info = await FFmpegHelper.ProbeDetailedAsync(probeFile).ConfigureAwait(false); }
            catch { }

            if (info != null)
            {
                task.SourceFormat = string.IsNullOrEmpty(info.VideoCodec)
                    ? Path.GetExtension(probeFile).TrimStart('.').ToUpperInvariant()
                    : info.VideoCodec.ToUpperInvariant();
                task.SourceResolution = info.Width > 0 && info.Height > 0
                    ? string.Format("{0} x {1}", info.Width, info.Height)
                    : "-";
                task.SourceFileSize = info.SizeBytes > 0
                    ? FFmpegHelper.FormatFileSize(info.SizeBytes)
                    : FFmpegHelper.FormatFileSize(new FileInfo(probeFile).Length);
                task.SourceDurationSeconds = info.DurationSeconds;
                task.SourceDuration = FFmpegHelper.FormatDuration(info.DurationSeconds);
                task.SourcePixelFormat = !string.IsNullOrEmpty(info.PixelFormat) ? info.PixelFormat : "-";
                task.SourceFrameRate = info.NominalFrameRate > 0
                    ? info.NominalFrameRate.ToString("0.###", CultureInfo.InvariantCulture) + " fps"
                    : "-";
                task.AudioTracks = info.AudioTracks ?? new System.Collections.Generic.List<AudioTrackInfo>();
                task.SubtitleTracks = info.SubtitleTracks ?? new System.Collections.Generic.List<SubtitleTrackInfo>();
            }
            else
            {
                // 保持占位元数据（扩展名/文件大小），其余为 "-"/"00:00:00"。
                task.SourceFormat = Path.GetExtension(probeFile).TrimStart('.').ToUpperInvariant();
                task.SourceResolution = "-";
                task.SourceFileSize = FFmpegHelper.FormatFileSize(new FileInfo(probeFile).Length);
                task.SourceDurationSeconds = 0;
                task.SourceDuration = "00:00:00";
                task.SourcePixelFormat = "-";
                task.SourceFrameRate = "-";
            }

            // External subtitle files are only relevant for pending (input) entries.
            if (task.Status != TaskStatus.Completed)
            {
                var externalSubs = FFmpegHelper.FindExternalSubtitles(task.InputPath);
                if (externalSubs.Count > 0)
                {
                    task.SubtitleTracks.AddRange(externalSubs);
                    if (task.SelectedSubtitleTrack == null)
                        task.SelectedSubtitleTrack = externalSubs[0];
                }
            }

            // 还原选中的音轨/字幕轨（依赖探测出的轨道列表）。
            ApplySelectedIndices(task, dto);

            // 仅对"正在转换"任务估算目标大小（已完成任务无意义）。
            if (task.Status != TaskStatus.Completed)
                task.EstimatedTargetSize = EstimateTargetSize(info, task.Preset);

            // Thumbnail at 1s.
            try
            {
                task.Thumbnail = await FFmpegHelper.GetThumbnailAsync(probeFile, 160, 90).ConfigureAwait(false)
                    ?? CreatePlaceholderThumbnail();
            }
            catch
            {
                // 保留占位缩略图。
            }

            // 回到 UI 线程原地刷新卡片内容。
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(new Action(() => UpdateTaskCardContent(task)));
        }

        #endregion

        #region Nested types

        private class TaskCard
        {
            public ConversionTask Task { get; set; }
            public RoundedPanel Panel { get; set; }
            public Button PresetButton { get; set; }
            public Button PresetGearButton { get; set; }
            public ComboBox SubtitleCombo { get; set; }
        public Button SubtitleButton { get; set; }
            public Button AudioButton { get; set; }
            public PictureBox ThumbnailBox { get; set; }
            public Button ConvertButton { get; set; }
            public ProgressBar ProgressBar { get; set; }
            public Label StatusLabel { get; set; }
            public Label OutputNameLabel { get; set; }
            public TextBox OutputNameEdit { get; set; }
            public Label SourceFormatLabel { get; set; }
            public Label SourceResolutionLabel { get; set; }
            public Label SourceSizeLabel { get; set; }
            public Label SourceDurationLabel { get; set; }
            public Label SourcePixelFormatLabel { get; set; }
            public Label SourceFrameRateLabel { get; set; }
            public Label TargetFormatLabel { get; set; }
            public Label TargetResolutionLabel { get; set; }
            public Label TargetSizeLabel { get; set; }
            public Label TargetDurationLabel { get; set; }
            public bool IsConverting { get; set; }
        }

        #endregion
    }
}
