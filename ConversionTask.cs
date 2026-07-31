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
        /// 用户自定义输出文件名（不含扩展名）。为空时默认使用 "源文件名_converted"。
        /// </summary>
        public string CustomOutputName { get; set; }

        public string GetOutputFileName()
        {
            string baseName = string.IsNullOrWhiteSpace(CustomOutputName)
                ? Path.GetFileNameWithoutExtension(InputPath) + "_converted"
                : CustomOutputName;
            return baseName + Preset.GetExtension();
        }

        public string SaveToFolder { get; set; }

        // ---- source metadata ------------------------------------------------
        public string SourceFormat { get; set; }
        public string SourceResolution { get; set; }
        public string SourceFileSize { get; set; }
        public string SourceDuration { get; set; }

        // ---- target metadata ------------------------------------------------
        public string TargetFormat { get { return Preset.FormatName; } }
        public string TargetResolution { get { return Preset.ResolutionLabel; } }
        public string TargetFileSize { get; set; }
        public string TargetDuration
        {
            get
            {
                // 如果做了剪切，优先用剪切的时长；否则跟随源。
                if (TrimEndSeconds > TrimStartSeconds && TrimEndSeconds > 0)
                    return FFmpegHelper.FormatDuration(TrimEndSeconds - TrimStartSeconds);
                if (TrimStartSeconds > 0)
                    return FFmpegHelper.FormatDuration(Math.Max(0, EstimateSourceDurationSeconds() - TrimStartSeconds));
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

        // ---- tracks ---------------------------------------------------------
        public List<AudioTrackInfo> AudioTracks { get; set; } = new List<AudioTrackInfo>();
        public List<SubtitleTrackInfo> SubtitleTracks { get; set; } = new List<SubtitleTrackInfo>();

        public AudioTrackInfo SelectedAudioTrack { get; set; }
        public SubtitleTrackInfo SelectedSubtitleTrack { get; set; }

        // ---- visuals --------------------------------------------------------
        public Image Thumbnail { get; set; }

        // ---- settings -------------------------------------------------------
        public PresetOption Preset { get; set; }
        public string SubtitleTrack { get; set; }
        public string AudioTrack { get; set; }

        // ---- edit / trim settings -------------------------------------------
        public double TrimStartSeconds { get; set; }
        public double TrimEndSeconds { get; set; }

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

        public string DisplayName
        {
            get
            {
                var sb = new System.Text.StringBuilder();
                sb.Append(string.IsNullOrWhiteSpace(Codec) ? "Audio" : Codec.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(BitRate)) sb.Append(" ").Append(BitRate);
                if (SampleRate > 0) sb.Append(" ").Append((SampleRate / 1000.0).ToString("0.0")).Append("kHz");
                if (Channels > 0) sb.Append(" ").Append(FormatChannels(Channels));
                if (!string.IsNullOrWhiteSpace(Language) && Language != "und")
                    sb.Append(" [").Append(Language).Append("]");
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
    /// 一条 ffprobe 探测到的字幕轨信息。
    /// </summary>
    public class SubtitleTrackInfo
    {
        public int Index { get; set; }
        public string Codec { get; set; }
        public string Language { get; set; }
        public string Title { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Title)) return Title;
                var sb = new System.Text.StringBuilder();
                sb.Append(string.IsNullOrWhiteSpace(Codec) ? "Subtitle" : Codec.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(Language) && Language != "und")
                    sb.Append(" [").Append(Language).Append("]");
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// A simplified preset option used by the converter UI.
    /// </summary>
    public class PresetOption
    {
        public string Name { get; set; }
        public string FormatName { get; set; }
        public string Extension { get; set; }
        public string VideoCodec { get; set; }
        public string AudioCodec { get; set; }
        public string ResolutionLabel { get; set; }
        public string ResolutionValue { get; set; }   // e.g. 1920x1080
        public string VideoBitrate { get; set; }
        public string AudioBitrate { get; set; }
        public string FrameRate { get; set; }

        public string GetExtension()
        {
            return string.IsNullOrEmpty(Extension) ? ".mp4" : Extension;
        }

        // ---- built-in common presets ---------------------------------------
        public static readonly PresetOption MP4_SameAsSource = new PresetOption
        {
            Name = "MP4 Same as source",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "copy",
            AudioCodec = "copy",
            ResolutionLabel = "Same as source",
            ResolutionValue = null,
            VideoBitrate = null,
            AudioBitrate = null,
            FrameRate = null
        };

        public static readonly PresetOption MP4_4K = new PresetOption
        {
            Name = "MP4 4K",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "libx264",
            AudioCodec = "aac",
            ResolutionLabel = "3840 x 2160",
            ResolutionValue = "3840x2160",
            VideoBitrate = "15000k",
            AudioBitrate = "256k",
            FrameRate = "30"
        };

        public static readonly PresetOption MP4_1080 = new PresetOption
        {
            Name = "MP4 1080",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "libx264",
            AudioCodec = "aac",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "8000k",
            AudioBitrate = "256k",
            FrameRate = "30"
        };

        public static readonly PresetOption MP4_720 = new PresetOption
        {
            Name = "MP4 720",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "libx264",
            AudioCodec = "aac",
            ResolutionLabel = "1280 x 720",
            ResolutionValue = "1280x720",
            VideoBitrate = "4000k",
            AudioBitrate = "192k",
            FrameRate = "30"
        };

        public static readonly PresetOption MP4_480 = new PresetOption
        {
            Name = "MP4 480",
            FormatName = "MP4",
            Extension = ".mp4",
            VideoCodec = "libx264",
            AudioCodec = "aac",
            ResolutionLabel = "854 x 480",
            ResolutionValue = "854x480",
            VideoBitrate = "1500k",
            AudioBitrate = "128k",
            FrameRate = "30"
        };

        public static readonly PresetOption AVI_XVID = new PresetOption
        {
            Name = "AVI Xvid",
            FormatName = "AVI",
            Extension = ".avi",
            VideoCodec = "libxvid",
            AudioCodec = "mp3",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "6000k",
            AudioBitrate = "192k",
            FrameRate = "30"
        };

        public static readonly PresetOption MKV_H264 = new PresetOption
        {
            Name = "MKV H.264",
            FormatName = "MKV",
            Extension = ".mkv",
            VideoCodec = "libx264",
            AudioCodec = "aac",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "8000k",
            AudioBitrate = "256k",
            FrameRate = "30"
        };

        public static readonly PresetOption MOV_H264 = new PresetOption
        {
            Name = "MOV H.264",
            FormatName = "MOV",
            Extension = ".mov",
            VideoCodec = "libx264",
            AudioCodec = "aac",
            ResolutionLabel = "1920 x 1080",
            ResolutionValue = "1920x1080",
            VideoBitrate = "8000k",
            AudioBitrate = "256k",
            FrameRate = "30"
        };

        public static readonly PresetOption[] All = new[]
        {
            MP4_SameAsSource, MP4_4K, MP4_1080, MP4_720, MP4_480,
            AVI_XVID, MKV_H264, MOV_H264
        };
    }
}
