// ============================================================================
//  ConversionTask.cs — data model for one item in the VideoConverter list.
//  Part of the VideoConverter module for FFBatch.
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Threading;

namespace VideoConverter
{
    /// <summary>
    /// One file queued for conversion.
    /// </summary>
    public class ConversionTask : INotifyPropertyChanged
    {
        public string InputPath { get; set; }

        public string OutputPath
        {
            get
            {
                if (!string.IsNullOrEmpty(_outputPath)) return _outputPath;
                string folder = SaveToFolder;
                if (string.IsNullOrEmpty(folder))
                    folder = Path.GetDirectoryName(InputPath);
                string name = GetOutputFileName();
                return Path.Combine(folder, name);
            }
            set { _outputPath = value; }
        }
        private string _outputPath;

        /// <summary>
        /// 清除缓存的输出路径，使下次访问 OutputPath 时按当前 SaveToFolder 重新计算。
        /// 用于"保存到"目录变更后，让已排队任务改用新目录。#95
        /// </summary>
        public void ResetCachedOutputPath()
        {
            _outputPath = null;
        }

        /// <summary>
        /// 用户自定义输出文件名（不含扩展名）。为空时默认使用源文件名。
        /// </summary>
        public string CustomOutputName { get; set; }

        public string GetOutputFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(CustomOutputName)
                ? Path.GetFileNameWithoutExtension(InputPath)
                : CustomOutputName;
            return baseName + Preset.GetExtension();
        }

        public string SaveToFolder { get; set; }

        /// <summary>
        /// 输入视频是否为 VC-1（WMV：vc1/wmv3/wvc1）。为 true 时 ffmpeg 命令注入
        /// 容错参数（-fflags +discardcorrupt -err_detect ignore_err -threads 1）。#74
        /// </summary>
        public bool IsVC1Input { get; set; }

        /// <summary>输入视频流 codec_name（ffprobe，小写，如 h264/wmv3）。高速智能 copy 判定用。</summary>
        public string SourceVideoCodec { get; set; }

        /// <summary>输入音频流 codec_name（ffprobe，如 aac/ac3）。高速智能 copy 判定用。</summary>
        public string SourceAudioCodec { get; set; }

        /// <summary>目标视频编码器（实际 ffmpeg 编码器名，含硬件解析/容器默认）。高速智能 copy 判定用。</summary>
        public string TargetVideoEncoder { get; set; }

        /// <summary>目标音频编码器（实际 ffmpeg 编码器名，含容器默认）。</summary>
        public string TargetAudioEncoder { get; set; }

        // ---- source metadata ------------------------------------------------
        public string SourceFormat { get; set; }
        public string SourceResolution { get; set; }
        public string SourceFileSize { get; set; }
        public string SourceDuration { get; set; }
        public string SourcePixelFormat { get; set; }
        public string SourceFrameRate { get; set; }

        // ---- target metadata ------------------------------------------------
        public string TargetFormat { get { return Preset.FormatName; } }
        public string TargetResolution { get { return Preset.ResolutionLabel; } }
        public string TargetFileSize { get; set; }
        public string TargetDuration
        {
            get
            {
                // 如果做了剪切（Segments），优先用保留段总时长；否则跟随源。
                double edited = GetEditedDurationSeconds();
                if (edited > 0 && Math.Abs(edited - EstimateSourceDurationSeconds()) > 0.05)
                    return FFmpegHelper.FormatDuration(edited);
                return SourceDuration;
            }
        }

        /// <summary>
        /// 目标文件预计大小（转换前根据码率估算，转换后更新为实际大小）。
        /// </summary>
        public string EstimatedTargetSize { get; set; }

        public double EstimateSourceDurationSeconds()
        {
            if (SourceDurationSeconds > 0) return SourceDurationSeconds;
            return 0;
        }

        public double SourceDurationSeconds { get; set; }

        /// <summary>
        /// 计算保留段的总时长（秒）。无保留段时返回源时长。
        /// </summary>
        public double GetEditedDurationSeconds()
        {
            double src = EstimateSourceDurationSeconds();
            if (Segments == null || Segments.Count == 0) return src;
            double total = 0;
            foreach (var seg in Segments)
                total += Math.Max(0, (seg.EndMs - seg.StartMs) / 1000.0);
            return total;
        }

        /// <summary>
        /// 返回最终输出的文件路径列表（合并模式 1 个，非合并模式 N 个）。
        /// </summary>
        public List<string> GetOutputPaths()
        {
            var list = new List<string>();
            string basePath = OutputPath;
            if (MergeSegments || Segments == null || Segments.Count <= 1)
            {
                list.Add(basePath);
                return list;
            }
            string dir = Path.GetDirectoryName(basePath);
            string nameNoExt = Path.GetFileNameWithoutExtension(basePath);
            string ext = Path.GetExtension(basePath);
            for (int i = 0; i < Segments.Count; i++)
                list.Add(Path.Combine(dir, $"{nameNoExt}_{i + 1}{ext}"));
            return list;
        }

        // ---- tracks ---------------------------------------------------------
        public List<AudioTrackInfo> AudioTracks { get; set; } = new List<AudioTrackInfo>();
        public List<SubtitleTrackInfo> SubtitleTracks { get; set; } = new List<SubtitleTrackInfo>();

        // ---- chapters ------------------------------------------------------
        /// <summary>源文件探测到的章节列表（ffprobe -show_chapters）。</summary>
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();

        /// <summary>是否保留章节标记。单文件转换时默认 true；合并时由合并逻辑生成。</summary>
        public bool PreserveChapters { get; set; } = true;

        public AudioTrackInfo SelectedAudioTrack { get; set; }
        public SubtitleTrackInfo SelectedSubtitleTrack { get; set; }

        /// <summary>多选音轨索引列表（空列表=使用 SelectedAudioTrack 单选；含 -1=全部音轨）。</summary>
        public List<int> SelectedAudioTrackIndices { get; set; } = new List<int>();

        /// <summary>多选字幕轨索引列表（空列表=使用 SelectedSubtitleTrack 单选；含 -1=全部字幕）。</summary>
        public List<int> SelectedSubtitleTrackIndices { get; set; } = new List<int>();

        // ---- visuals --------------------------------------------------------
        public Image Thumbnail { get; set; }

        // ---- settings -------------------------------------------------------
        public PresetOption Preset { get; set; }
        public string SubtitleTrack { get; set; }
        public string AudioTrack { get; set; }

        // ---- edit / trim settings -------------------------------------------
        // Backward-compatible single trim range (kept for legacy callers).
        // When Segments is populated, it takes precedence.
        public double TrimStartSeconds { get; set; }
        public double TrimEndSeconds { get; set; }

        /// <summary>
        /// 剪切后保留的视频段（毫秒精度）。为空表示不剪切（保留整段）。
        /// </summary>
        public List<VideoSegment> Segments { get; set; } = new List<VideoSegment>();

        /// <summary>
        /// 多段剪切时是否合并为一个文件；false 则输出多个带序号的文件。
        /// </summary>
        public bool MergeSegments { get; set; } = true;

        /// <summary>
        /// 画面裁剪区域（null 表示不裁剪）。
        /// </summary>
        public CropRegion Crop { get; set; }

        /// <summary>
        /// 画面旋转/翻转。0=不旋转；1=顺时针90°；2=逆时针90°；3=180°；4=水平翻转；5=垂直翻转。
        /// </summary>
        public int Rotation { get; set; }

        /// <summary>播放速度倍率。1.0=原速；0.5=慢放；2.0=快放。范围 [0.25, 4.0]。</summary>
        public double Speed { get; set; } = 1.0;

        /// <summary>亮度调整。范围 [-1.0, 1.0]，0=不变。</summary>
        public double Brightness { get; set; } = 0.0;

        /// <summary>对比度调整。范围 [-1000, 1000]，1=不变。</summary>
        public double Contrast { get; set; } = 1.0;

        /// <summary>饱和度调整。范围 [0, 3]，1=不变。</summary>
        public double Saturation { get; set; } = 1.0;

        /// <summary>水印图片路径；null 或空表示不加水印。</summary>
        public string WatermarkPath { get; set; }

        /// <summary>水印位置：1=左上 2=右上 3=右下 4=左下 5=居中。默认 3（右下）。</summary>
        public int WatermarkPosition { get; set; } = 3;

        /// <summary>水印不透明度 [0.0, 1.0]，默认 0.8。</summary>
        public double WatermarkOpacity { get; set; } = 0.8;

        /// <summary>水印缩放比例（相对于视频宽度的百分比），0=不缩放保持原尺寸。默认 0。</summary>
        public double WatermarkScalePercent { get; set; } = 0.0;

        // ---- P2 高级编码参数 ----
        /// <summary>双通道编码（两遍编码，提升质量但耗时翻倍）。</summary>
        public bool TwoPass { get; set; }

        /// <summary>无损转换：视频用 -lossless 1（NVENC）或 -crf 0（x264）；音频 copy。</summary>
        public bool Lossless { get; set; }

        /// <summary>去隔行：true 时在滤镜链加入 yadif。</summary>
        public bool Deinterlace { get; set; }

        /// <summary>H.264/H.265 Profile（baseline/main/high/high444）。null=不指定。</summary>
        public string H264Profile { get; set; }

        /// <summary>H.264/H.265 Level（如 "4.0"、"5.1"）。null=不指定。</summary>
        public string H264Level { get; set; }

        /// <summary>字幕烧录样式：ForceStyle 字符串（如 "FontSize=24,PrimaryColour=&H00FFFFFF"）。null=默认。</summary>
        public string SubtitleStyle { get; set; }

        /// <summary>是否将字幕烧录到画面（硬字幕）。false=软字幕（流映射）。</summary>
        public bool BurnSubtitle { get; set; }

        /// <summary>
        /// 是否把字幕流导出为独立文件（-map 0:s? 输出到单独 .srt/.ass）。
        /// 由文件卡片字幕弹窗的“导出字幕”复选框控制。FFmpegHelper 侧的支持另行实现。
        /// </summary>
        public bool ExportSubtitle { get; set; }

        /// <summary>
        /// 字幕输出模式：决定文件卡片字幕弹窗的三个 radio 选项如何映射到 ffmpeg 命令。
        /// None        = 丢掉所有字幕（不映射字幕轨）
        /// SoftKeepAll = 以原始流形式保存到容器中（要求 mp4/mov/mkv 等支持内嵌的容器）
        /// BurnExternal= 烧录外挂字幕（subtitles 滤镜 + force_style）
        /// </summary>
        public SubtitleMode SubMode { get; set; } = SubtitleMode.None;

        /// <summary>烧录字幕的完整样式（字体/大小/颜色/B/I/U/边框/背景/位置等）。</summary>
        public SubtitleSettings SubtitleSettings { get; set; } = new SubtitleSettings();

        /// <summary>元数据：标题。null=不写入。</summary>
        public string MetaTitle { get; set; }

        /// <summary>元数据：作者/艺术家。null=不写入。</summary>
        public string MetaAuthor { get; set; }

        /// <summary>元数据：年份。null=不写入。</summary>
        public string MetaYear { get; set; }

        /// <summary>元数据：备注/描述。null=不写入。</summary>
        public string MetaComment { get; set; }

        // ---- P2 任务暂停/恢复 ----
        /// <summary>任务是否已暂停（ffmpeg 进程被挂起）。</summary>
        public bool IsPaused { get; set; }

        /// <summary>当前运行的 ffmpeg 进程 ID（转换进行中才有值）。</summary>
        public int CurrentProcessId { get; set; } = -1;

        // ---- conversion mode (set right before each run) --------------------
        /// <summary>
        /// When true the file is remuxed with "-c copy" (only valid when the
        /// input and output containers are the same). Driven by the "高速转换"
        /// checkbox combined with a per-file format check.
        /// </summary>
        public bool UseStreamCopy { get; set; }

        /// <summary>
        /// Hardware encoder to use for the video stream (e.g. "h264_nvenc"),
        /// or null to fall back to the preset's software encoder. Driven by the
        /// "硬件编码" checkbox and the detected GPU vendor.
        /// </summary>
        public string HardwareEncoder { get; set; }

        // ---- state ----------------------------------------------------------
        public TaskStatus Status { get; set; }
        public double Progress { get; set; }
        public string StatusMessage { get; set; }

        /// <summary>
        /// 用于单独取消该任务的 CancellationTokenSource。
        /// </summary>
        [System.Xml.Serialization.XmlIgnore]
        public CancellationTokenSource Cancellation { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public ConversionTask()
        {
            Preset = PresetOption.MP4_1080;
            Status = TaskStatus.Pending;
        }
    }

    public enum TaskStatus { Pending, Converting, Completed, Failed }

    /// <summary>
    /// 视频剪切保留段（毫秒精度）。
    /// </summary>
    public class VideoSegment
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }

        [System.Xml.Serialization.XmlIgnore]
        public bool IsSelected { get; set; }

        public VideoSegment Clone()
        {
            return new VideoSegment { StartMs = StartMs, EndMs = EndMs, IsSelected = IsSelected };
        }
    }

    /// <summary>
    /// 画面裁剪区域。
    /// </summary>
    public class CropRegion
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public CropRegion Clone()
        {
            return new CropRegion { X = X, Y = Y, Width = Width, Height = Height };
        }
    }

    /// <summary>
    /// 一条 ffprobe 探测到的音轨信息。
    /// </summary>
    public class AudioTrackInfo
    {
        public int Index { get; set; }
        public string Codec { get; set; }
        public int SampleRate { get; set; }
        public int Channels { get; set; }
        public string BitRate { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }

        /// <summary>是否为外挂音频文件（非容器内流）。由文件卡片音频弹窗的“+”按钮添加。</summary>
        public bool IsExternal { get; set; }

        /// <summary>外挂音频文件的完整路径（仅当 IsExternal 为 true 时有效）。</summary>
        public string FilePath { get; set; }

        public string DisplayName
        {
            get
            {
                if (IsExternal && !string.IsNullOrWhiteSpace(FilePath))
                    return "外挂 " + Path.GetFileName(FilePath);
                var sb = new System.Text.StringBuilder();
                if (!string.IsNullOrWhiteSpace(Language) && Language != "und")
                    sb.Append("[").Append(Language).Append("] ");
                sb.Append(string.IsNullOrWhiteSpace(Codec) ? "Audio" : Codec.ToUpperInvariant());
                if (SampleRate > 0) sb.Append(" ").Append((SampleRate / 1000.0).ToString("0.0")).Append("kHz");
                if (!string.IsNullOrWhiteSpace(BitRate)) sb.Append(" ").Append(BitRate);
                if (Channels > 0) sb.Append(" ").Append(FormatChannels(Channels));
                return sb.ToString();
            }
        }

        private static string FormatChannels(int ch)
        {
            switch (ch)
            {
                case 1: return "Mono";
                case 2: return "Stereo";
                case 6: return "5.1";
                case 8: return "7.1";
                default: return ch + "ch";
            }
        }
    }

    /// <summary>
    /// 章节信息（对应 FFMETADATA 中的一个 [CHAPTER] 块）。
    /// </summary>
    public class ChapterInfo
    {
        /// <summary>章节索引（从 0 开始）。</summary>
        public int Index { get; set; }

        /// <summary>开始时间（毫秒）。</summary>
        public long StartMs { get; set; }

        /// <summary>结束时间（毫秒）。</summary>
        public long EndMs { get; set; }

        /// <summary>章节标题。</summary>
        public string Title { get; set; }

        public ChapterInfo Clone()
        {
            return new ChapterInfo { Index = Index, StartMs = StartMs, EndMs = EndMs, Title = Title };
        }
    }

    /// <summary>
    /// 一条字幕轨信息（内部流或外挂字幕文件）。
    /// </summary>
    public class SubtitleTrackInfo
    {
        public int Index { get; set; }
        public string Codec { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }
        /// <summary>是否为外挂字幕文件（非容器内流）。</summary>
        public bool IsExternal { get; set; }
        /// <summary>外挂字幕文件的完整路径（仅当 IsExternal 为 true 时有效）。</summary>
        public string FilePath { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                var sb = new System.Text.StringBuilder();
                if (IsExternal)
                    sb.Append("外挂 ");
                sb.Append(string.IsNullOrWhiteSpace(Codec) ? "Subtitle" : Codec.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(Language) && Language != "und")
                    sb.Append(" [").Append(Language).Append("]");
                return sb.ToString();
            }
        }
    }

    /// <summary>字幕输出模式（与文件卡片字幕弹窗的三个 radio 选项一一对应）。</summary>
    public enum SubtitleMode
    {
        /// <summary>无字幕：不映射任何字幕轨（默认）。</summary>
        None = 0,
        /// <summary>保留字幕轨道：以原始流形式嵌入目标容器（mp4/mov/mkv/m4v 等）。</summary>
        SoftKeepAll = 1,
        /// <summary>烧录字幕：将外挂字幕文件烧录到视频画面（硬字幕）。</summary>
        BurnExternal = 2,
    }

    /// <summary>
    /// 字幕烧录样式参数。会序列化为 ASS 的 force_style 字符串。
    /// 字段名采用 ASS/FFmpeg 常见命名，序列化兼容 DataContractJsonSerializer。
    /// </summary>
    public class SubtitleSettings
    {
        /// <summary>字体名称（如 "Arial"、"微软雅黑"）。null=不指定。</summary>
        public string FontName { get; set; } = "Arial";
        /// <summary>字体大小（pt）。常用 16-48，默认 24。</summary>
        public int FontSize { get; set; } = 24;
        /// <summary>字体颜色（ARGB）。</summary>
        public int FontColorArgb { get; set; } = unchecked((int)0xFFFFFFFF);

        public bool Bold { get; set; }
        public bool Italic { get; set; }
        public bool Underline { get; set; }

        /// <summary>边框/描边宽度（0=关闭）。</summary>
        public int OutlineWidth { get; set; } = 1;
        /// <summary>边框颜色（ARGB）。</summary>
        public int OutlineColorArgb { get; set; } = unchecked((int)0xFF000000);

        /// <summary>整体透明度 0-100（100=完全不透明）。对应 ASS PrimaryColour Alpha。</summary>
        public int Transparency { get; set; } = 100;

        /// <summary>是否启用背景色框（ASS BackColour）。</summary>
        public bool BackEnabled { get; set; }
        /// <summary>背景色（ARGB，alpha 通道由 BackAlpha 决定）。</summary>
        public int BackColorArgb { get; set; } = unchecked((int)0xFF000000);
        /// <summary>背景透明度 0-100（100=完全透明）。</summary>
        public int BackAlpha { get; set; } = 80;

        /// <summary>
        /// ASS 对齐方式（numpad）：1=左下 2=中下 3=右下 4=中左 5=中中 6=中右 7=左上 8=中上 9=右上。
        /// 截图样例使用的是底部居中 → 默认 2。
        /// </summary>
        public int Alignment { get; set; } = 2;

        /// <summary>字幕到画面底部的纵向偏移像素（正值向下）。0=贴底。</summary>
        public int MarginV { get; set; } = 20;

        /// <summary>外挂字幕文件绝对路径（仅当 SubMode=BurnExternal 时有效）。null=自动检测同基名 .srt/.ass 等。</summary>
        public string ExternalSubPath { get; set; }

        /// <summary>
        /// 把当前设置序列化为 FFmpeg subtitles 滤镜 force_style 字符串。
        /// ASS 颜色采用 &amp;HBBGGRR&amp;（十六进制），alpha 独立控制。
        /// </summary>
        public string ToForceStyle()
        {
            var sb = new System.Text.StringBuilder();

            if (!string.IsNullOrEmpty(FontName))
                sb.Append("FontName=").Append(EscapeForceStyleValue(FontName)).Append(',');
            if (FontSize > 0)
                sb.Append("FontSize=").Append(FontSize).Append(',');
            // PrimaryColour = font color (alpha = 100 - transparency)
            int primaryAlpha = Math.Max(0, Math.Min(255, 255 - Transparency * 255 / 100));
            sb.Append("PrimaryColour=").Append(ArgbToAssColor(FontColorArgb, primaryAlpha)).Append(',');
            if (Bold) sb.Append("Bold=1,");
            if (Italic) sb.Append("Italic=1,");
            if (Underline) sb.Append("Underline=1,");

            // Outline
            if (OutlineWidth > 0)
            {
                sb.Append("Outline=").Append(OutlineWidth).Append(',');
                sb.Append("OutlineColour=").Append(ArgbToAssColor(OutlineColorArgb, 0)).Append(',');
            }

            // BackColour
            if (BackEnabled)
            {
                int backAlpha = Math.Max(0, Math.Min(255, 255 - BackAlpha * 255 / 100));
                sb.Append("BackColour=").Append(ArgbToAssColor(BackColorArgb, backAlpha)).Append(',');
            }

            // Alignment
            sb.Append("Alignment=").Append(Alignment).Append(',');

            // MarginV
            sb.Append("MarginV=").Append(MarginV);

            return sb.ToString();
        }

        /// <summary>
        /// 把 .NET ARGB（0xAARRGGBB）转为 ASS &amp;HBBGGRR&amp; 字符串，alpha 单独注入。
        /// FFmpeg force_style 期望颜色按 BGR 顺序。
        /// </summary>
        private static string ArgbToAssColor(int argb, int alpha)
        {
            int a = alpha & 0xFF;
            int r = (argb >> 16) & 0xFF;
            int g = (argb >> 8) & 0xFF;
            int b = argb & 0xFF;
            // ASS 颜色字符串：&HAABBGGRR&
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "&H{0:X2}{1:X2}{2:X2}{3:X2}&", a, b, g, r);
        }

        /// <summary>
        /// 转义 force_style 值中的特殊字符。FFmpeg 用单引号包裹整段，内部不能含 ','
        /// （subtitles 滤镜解析器对逗号敏感）。这里用反斜杠转义。
        /// </summary>
        private static string EscapeForceStyleValue(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace(",", "\\,").Replace("=", "\\=");
        }

        /// <summary>将一组 Color 控件回填到当前设置（便于 UI 控件双向同步）。</summary>
        public Color FontColor => Color.FromArgb(FontColorArgb);
        public Color OutlineColor => Color.FromArgb(OutlineColorArgb);
        public Color BackColor => Color.FromArgb(BackColorArgb);

        public void SetFontColor(Color c) => FontColorArgb = unchecked((int)c.ToArgb());
        public void SetOutlineColor(Color c) => OutlineColorArgb = unchecked((int)c.ToArgb());
        public void SetBackColor(Color c) => BackColorArgb = unchecked((int)c.ToArgb());
    }

    /// <summary>
    /// A simplified preset option used by the converter UI.
    /// </summary>
    public class PresetOption
    {
        public string Name { get; set; }
        public string FormatName { get; set; }
        /// <summary>顶层类别（视频/音频/图像/设备/网络视频），用于自定义与最近列表分组显示。</summary>
        public string Category { get; set; }
        public string Extension { get; set; }
        public string VideoCodec { get; set; }
        /// <summary>
        /// Logical codec family (fourCC, e.g. "H264"). The preset editor shows
        /// <see cref="VideoCodecLabel"/>; the concrete CPU/GPU encoder is resolved
        /// at conversion time via FFmpegHelper.ResolveVideoEncoder based on the
        /// hardware-encoding setting. #65
        /// </summary>
        public string VideoCodecLabel { get; set; }
        public string AudioCodec { get; set; }
        /// <summary>Extra raw ffmpeg parameters appended to the command (advanced). #65</summary>
        public string CustomArgs { get; set; }
        public string ResolutionLabel { get; set; }
        public string ResolutionValue { get; set; }   // e.g. 1920x1080
        public string VideoBitrate { get; set; }
        /// <summary>视频码率模式：auto / cbr / vbr / quality（null 视为 auto）。#73</summary>
        public string BitrateMode { get; set; }
        /// <summary>质量控制值（CRF/QP/CQ），仅 BitrateMode=quality 时有效。</summary>
        public int QualityValue { get; set; }
        /// <summary>
        /// VBV 峰值码率限制（可选，如 "8000k"），输出 -maxrate/-bufsize。
        /// quality 模式 → Capped CRF；vbr 模式 → 受限 VBR（constrained VBR）。
        /// 字段名沿用 QualityMaxRate 以保证已存预设 JSON 兼容，语义已扩展为通用最大码率。
        /// </summary>
        public string QualityMaxRate { get; set; }
        public string AudioBitrate { get; set; }
        public string FrameRate { get; set; }

        // ---- fields populated from UniConverter preset database ----
        public string PresetId { get; set; }
        public string FormatId { get; set; }
        public string FourCC { get; set; }
        public bool KeepSource { get; set; }
        public string SampleRate { get; set; }
        public int Channels { get; set; }

        /// <summary>
        /// 是否为内置预设。内置预设不可被直接修改；修改保存时会自动另存为“名称（自定义）”。
        /// </summary>
        public bool IsBuiltIn { get; set; }

        public string GetExtension()
        {
            return string.IsNullOrEmpty(Extension) ? ".mp4" : Extension;
        }

        /// <summary>
        /// Return a shallow copy so per-task edits do not affect the shared store entry.
        /// </summary>
        public PresetOption Clone()
        {
            return (PresetOption)this.MemberwiseClone();
        }

        // ---- built-in common presets ---------------------------------------
        public static readonly PresetOption MP4_SameAsSource = new PresetOption
        {
            Name = "MP4 与源文件相同",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "copy",
            AudioCodec = "copy",
            ResolutionLabel = "与源文件相同",
            ResolutionValue = null,
            VideoBitrate = null,
            AudioBitrate = null,
            FrameRate = null,
            IsBuiltIn = true
        };

        public static readonly PresetOption MP4_4K = new PresetOption
        {
            Name = "MP4 4K",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "H264",
            VideoCodecLabel = "H.264",
            AudioCodec = "aac",
            ResolutionLabel = "3840 x 2160",
            ResolutionValue = "3840x2160",
            VideoBitrate = "15000k",
            AudioBitrate = "256k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        public static readonly PresetOption MP4_1080 = new PresetOption
        {
            Name = "MP4 1080",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "H264",
            VideoCodecLabel = "H.264",
            AudioCodec = "aac",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "8000k",
            AudioBitrate = "256k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        public static readonly PresetOption MP4_720 = new PresetOption
        {
            Name = "MP4 720P",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "H264",
            VideoCodecLabel = "H.264",
            AudioCodec = "aac",
            ResolutionLabel = "1280 x 720",
            ResolutionValue = "1280x720",
            VideoBitrate = "4000k",
            AudioBitrate = "192k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        public static readonly PresetOption MP4_480 = new PresetOption
        {
            Name = "MP4 480P",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "H264",
            VideoCodecLabel = "H.264",
            AudioCodec = "aac",
            ResolutionLabel = "854 x 480",
            ResolutionValue = "854x480",
            VideoBitrate = "1500k",
            AudioBitrate = "128k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        public static readonly PresetOption AVI_XVID = new PresetOption
        {
            Name = "AVI Xvid",
            FormatName = "AVI",
            Extension = ".avi",
            VideoCodec = "XVID",
            VideoCodecLabel = "Xvid (MPEG-4)",
            AudioCodec = "mp3",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "6000k",
            AudioBitrate = "192k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        public static readonly PresetOption MKV_H264 = new PresetOption
        {
            Name = "MKV H.264",
            FormatName = "MKV",
            Extension = ".mkv",
            VideoCodec = "H264",
            VideoCodecLabel = "H.264",
            AudioCodec = "aac",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "8000k",
            AudioBitrate = "256k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        public static readonly PresetOption MOV_H264 = new PresetOption
        {
            Name = "MOV H.264",
            FormatName = "MOV",
            Extension = ".mov",
            VideoCodec = "H264",
            VideoCodecLabel = "H.264",
            AudioCodec = "aac",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "8000k",
            AudioBitrate = "256k",
            FrameRate = "30",
            IsBuiltIn = true
        };

        /// <summary>
        /// Defaults used when the external preset database is not available.
        /// </summary>
        public static PresetOption[] BuiltInAll => new[]
        {
            MP4_SameAsSource, MP4_4K, MP4_1080, MP4_720, MP4_480,
            AVI_XVID, MKV_H264, MOV_H264
        };

        /// <summary>
        /// All presets loaded from options_spec/presets.json, falling back to built-ins.
        /// </summary>
        public static PresetOption[] All
        {
            get
            {
                try
                {
                    var list = new System.Collections.Generic.List<PresetOption>();
                    foreach (var cat in PresetDataStore.Categories)
                    {
                        if (!PresetDataStore.FormatsByCategory.ContainsKey(cat)) continue;
                        foreach (var fmt in PresetDataStore.FormatsByCategory[cat])
                            list.AddRange(fmt.Presets);
                    }
                    if (list.Count > 0) return list.ToArray();
                }
                catch { }
                return BuiltInAll;
            }
        }
    }
}
