// ============================================================================
//  FFmpegHelper.cs — locate ffmpeg/ffprobe and build/run commands.
//  All actual conversion work is delegated to ffmpeg.exe.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoConverter
{
    public static class FFmpegHelper
    {
        // Hard-coded fallback provided by the user.
        private static readonly string DefaultFFmpegFolder = @"D:\AI\ffmpeg-8.1.2-full_build\bin";

        #region Hardware encoding support

        public enum HardwareVendor { Nvidia, Intel, Amd }

        /// <summary>
        /// Which hardware-accelerated encoders ffmpeg was built with. Detection at
        /// startup tells us which GPU vendors are available on this machine.
        /// </summary>
        public class HardwareSupport
        {
            public bool Nvidia;
            public bool Intel;
            public bool Amd;

            public bool Any { get { return Nvidia || Intel || Amd; } }

            public string DisplayName
            {
                get
                {
                    var parts = new List<string>();
                    if (Nvidia) parts.Add("NVIDIA");
                    if (Intel) parts.Add("Intel");
                    if (Amd) parts.Add("AMD");
                    return parts.Count == 0 ? "不支持" : string.Join(", ", parts);
                }
            }
        }

        /// <summary>
        /// Maps a software ffmpeg video encoder to its hardware equivalents, per
        /// vendor. This is also the canonical reference list of codecs that have
        /// BOTH a software and a hardware implementation.
        /// </summary>
        private static readonly Dictionary<string, Dictionary<HardwareVendor, string>> HardwareEncoderMap =
            new Dictionary<string, Dictionary<HardwareVendor, string>>
        {
            { "libx264",    new Dictionary<HardwareVendor, string> { { HardwareVendor.Nvidia, "h264_nvenc" }, { HardwareVendor.Intel, "h264_qsv" }, { HardwareVendor.Amd, "h264_amf" } } },
            { "libx265",    new Dictionary<HardwareVendor, string> { { HardwareVendor.Nvidia, "hevc_nvenc" }, { HardwareVendor.Intel, "hevc_qsv" }, { HardwareVendor.Amd, "hevc_amf" } } },
            { "libvpx-vp9", new Dictionary<HardwareVendor, string> { { HardwareVendor.Nvidia, "vp9_nvenc" },  { HardwareVendor.Intel, "vp9_qsv" } } },
            { "libsvtav1",  new Dictionary<HardwareVendor, string> { { HardwareVendor.Nvidia, "av1_nvenc" },  { HardwareVendor.Intel, "av1_qsv" }, { HardwareVendor.Amd, "av1_amf" } } },
            { "libaom-av1", new Dictionary<HardwareVendor, string> { { HardwareVendor.Nvidia, "av1_nvenc" },  { HardwareVendor.Intel, "av1_qsv" }, { HardwareVendor.Amd, "av1_amf" } } },
            { "mjpeg",      new Dictionary<HardwareVendor, string> { { HardwareVendor.Nvidia, "mjpeg_nvenc" } } },
            { "mpeg2video", new Dictionary<HardwareVendor, string> { { HardwareVendor.Intel, "mpeg2_qsv" } } }
        };

        /// <summary>
        /// Returns the best hardware encoder for a software codec given the
        /// detected support, or null when the codec has no hardware equivalent.
        /// Priority: NVIDIA > Intel > AMD.
        /// </summary>
        public static bool TryGetHardwareEncoder(string softwareCodec, HardwareSupport support, out string hardwareEncoder)
        {
            hardwareEncoder = null;
            if (support == null || !support.Any) return false;
            if (!HardwareEncoderMap.TryGetValue(softwareCodec ?? string.Empty, out var vendors))
                return false;

            if (support.Nvidia && vendors.TryGetValue(HardwareVendor.Nvidia, out var n)) { hardwareEncoder = n; return true; }
            if (support.Intel && vendors.TryGetValue(HardwareVendor.Intel, out var i)) { hardwareEncoder = i; return true; }
            if (support.Amd && vendors.TryGetValue(HardwareVendor.Amd, out var a)) { hardwareEncoder = a; return true; }
            return false;
        }

        /// <summary>
        /// Resolve a logical codec family (fourCC, e.g. "H264") to the concrete
        /// ffmpeg encoder to use. When <paramref name="hw"/> indicates hardware
        /// encoding is enabled and a GPU encoder exists for the detected vendor,
        /// the GPU encoder is returned (e.g. "h264_nvenc"); otherwise the CPU
        /// encoder is returned (e.g. "libx264"). This is the single place where
        /// the "H264 → CPU/GPU" decision is made. #65
        /// </summary>
        public static string ResolveVideoEncoder(string fourCC, HardwareSupport hw, string label = null)
        {
            if (string.IsNullOrEmpty(fourCC) || string.Equals(fourCC, "copy", StringComparison.OrdinalIgnoreCase))
                return fourCC;

            // 优先按界面 label 走硬件编码配置（hard_codec_settings.json）：
            // 勾选硬件且当前机器支持 → 对应 GPU 编码器；否则 → CPU 编码器。
            if (!string.IsNullOrEmpty(label) && HardCodecSettings.Loaded && HardCodecSettings.IsHardwareCapable(label))
            {
                string resolved, cpu;
                HardCodecSettings.Resolve(label, hw, out resolved, out cpu);
                if (!string.IsNullOrEmpty(resolved)) return resolved;
                if (!string.IsNullOrEmpty(cpu)) return cpu;
            }

            // 配置缺失或 label 未知时回退到内置映射（保持旧行为）。
            string cpuEnc = PresetDataStore.GetCpuEncoder(fourCC);
            if (hw != null && hw.Any && HardwareEncoderMap.TryGetValue(cpuEnc ?? string.Empty, out var vendors))
            {
                if (hw.Nvidia && vendors.TryGetValue(HardwareVendor.Nvidia, out var n)) return n;
                if (hw.Intel && vendors.TryGetValue(HardwareVendor.Intel, out var i)) return i;
                if (hw.Amd && vendors.TryGetValue(HardwareVendor.Amd, out var a)) return a;
            }
            return cpuEnc;
        }

        /// <summary>判断一个实际 ffmpeg 编码器名是否为硬件（GPU）编码器（*_nvenc / *_qsv / *_amf）。</summary>
        public static bool IsHardwareEncoder(string encoder)
        {
            return HardCodecSettings.IsHardwareEncoder(encoder);
        }

        /// <summary>
        /// Probe ffmpeg for the hardware encoders it was compiled with. A codec
        /// name such as h264_nvenc only appears when that vendor's encoder is
        /// present in the build.
        /// </summary>
        public static async Task<HardwareSupport> DetectHardwareEncodersAsync()
        {
            var sup = new HardwareSupport();
            if (!File.Exists(FFmpegPath)) return sup;

            return await RunUnderGlobalConcurrencyAsync(async () =>
            {
                string tag = ProcessGuard.MakeTag(out string tempFile);
                var psi = new ProcessStartInfo
                {
                    FileName = FFmpegPath,
                    Arguments = "-nostdin -hide_banner -encoders " + tag,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    var tcs = new TaskCompletionSource<object>();
                    proc.Exited += (s, e) => tcs.TrySetResult(null);
                    proc.Start();
                    ProcessGuard.Register(proc, tempFile);

                    string output = await Task.Run(() => proc.StandardOutput.ReadToEnd()).ConfigureAwait(false);
                    await tcs.Task.ConfigureAwait(false);

                    sup.Nvidia = output.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0;
                    sup.Intel = output.IndexOf("_qsv", StringComparison.OrdinalIgnoreCase) >= 0;
                    sup.Amd = output.IndexOf("_amf", StringComparison.OrdinalIgnoreCase) >= 0;
                }
                return sup;
            });
        }

        #endregion

        /// <summary>
        /// Run "ffmpeg -version" and return the version token after "ffmpeg version",
        /// e.g. "7.1.1" or "N-116000-gabc123". Returns null when ffmpeg is missing
        /// or cannot be run.
        /// </summary>
        public static async Task<string> GetInstalledVersionAsync()
        {
            if (!File.Exists(FFmpegPath)) return null;
            try
            {
                return await RunUnderGlobalConcurrencyAsync(async () =>
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = FFmpegPath,
                        Arguments = "-nostdin -version",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    using (var proc = Process.Start(psi))
                    {
                        string first = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(first)) return null;
                        var m = Regex.Match(first, @"ffmpeg\s+version\s+([^\s]+)", RegexOptions.IgnoreCase);
                        if (m.Success) return m.Groups[1].Value.Trim();
                        return first.Trim();
                    }
                });
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Returns the folder that contains ffmpeg.exe. Tries the project setting first,
        /// then the user-supplied path.
        /// </summary>
        public static string GetFFmpegFolder()
        {
            // 1) 与可执行文件同目录（独立发布时 ffmpeg 就放在这里）
            string exeDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(exeDir) &&
                File.Exists(Path.Combine(exeDir, "ffmpeg.exe")))
                return exeDir;

            // 2) 同目录下的 ffmpeg 子文件夹
            string sub = Path.Combine(exeDir, "ffmpeg");
            if (Directory.Exists(sub) && File.Exists(Path.Combine(sub, "ffmpeg.exe")))
                return sub;

            // 3) 用户提供的固定路径（最后回退）
            if (Directory.Exists(DefaultFFmpegFolder) &&
                File.Exists(Path.Combine(DefaultFFmpegFolder, "ffmpeg.exe")))
                return DefaultFFmpegFolder;

            return exeDir ?? AppDomain.CurrentDomain.BaseDirectory;
        }

        public static string FFmpegPath { get { return Path.Combine(GetFFmpegFolder(), "ffmpeg.exe"); } }
        public static string FFprobePath { get { return Path.Combine(GetFFmpegFolder(), "ffprobe.exe"); } }

        /// <summary>
        /// Run ffprobe and return basic metadata.
        /// </summary>
        public static async Task<MediaInfo> ProbeAsync(string filePath)
        {
            return await ProbeDetailedAsync(filePath);
        }

        /// <summary>
        /// Run ffprobe and return full metadata including audio/subtitle tracks.
        /// </summary>
        public static async Task<MediaInfo> ProbeDetailedAsync(string filePath)
        {
            if (!File.Exists(FFprobePath))
                throw new FileNotFoundException("ffprobe.exe not found.", FFprobePath);

            // ffprobe does not support -progress; passing it makes ffprobe emit an
            // error and return no JSON. Register the process without a temp marker.
            string output = await RunUnderGlobalConcurrencyAsync(async () =>
            {
                var psi = new ProcessStartInfo
                {
                    FileName = FFprobePath,
                    Arguments = string.Format(
                        "-nostdin -v error -show_streams -show_chapters -show_entries format=size,duration " +
                        "-of json \"{0}\"",
                        filePath),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                string captured = null;
                using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    var tcs = new TaskCompletionSource<object>();
                    proc.Exited += (s, e) => tcs.TrySetResult(null);
                    proc.Start();
                    ProcessGuard.Register(proc);
                    // Read stdout (and drain stderr) so ffprobe can never block on a
                    // full stderr pipe. #76
                    var outTask = Task.Run(() => proc.StandardOutput.ReadToEnd());
                    var errTask = Task.Run(() => proc.StandardError.ReadToEnd());

                    // 15 秒超时：某些格式（如 rm/rmvb）可能让 ffprobe 卡死，
                    // 超时后杀进程并抛异常，避免添加文件流程永久挂起。
                    var winner = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                    if (winner != tcs.Task)
                    {
                        try { proc.Kill(); } catch { }
                        throw new TimeoutException("ffprobe 探测超时: " + filePath);
                    }

                    captured = await outTask.ConfigureAwait(false);
                    await errTask.ConfigureAwait(false);
                }
                return captured;
            });

            return ParseFfprobeJson(output, filePath);
        }

        /// <summary>
        /// 判断 ffprobe 的 codec_name 是否为 VC-1 家族（WMV 常见：vc1/wmv3/wvc1）。
        /// </summary>
        public static bool IsVC1Codec(string codecName)
        {
            if (string.IsNullOrWhiteSpace(codecName)) return false;
            string c = codecName.Trim().ToLowerInvariant();
            return c == "vc1" || c == "wmv3" || c == "wvc1";
        }

        /// <summary>
        /// 用 ffprobe 检测输入文件的视频流是否为 VC-1。探测失败返回 false。
        /// </summary>
        public static async Task<bool> DetectVC1InputAsync(string filePath)
        {
            try
            {
                var info = await ProbeDetailedAsync(filePath).ConfigureAwait(false);
                return IsVC1Codec(info?.VideoCodec);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Parse ffprobe -of json output into a MediaInfo using a proper JSON
        /// (de)serializer so nested objects (e.g. a stream's "disposition"/"tags")
        /// are handled correctly. The previous hand-rolled regex parser grabbed the
        /// wrong capture group and returned empty metadata for real-world files. #76
        /// </summary>
        private static MediaInfo ParseFfprobeJson(string json, string filePath)
        {
            var info = new MediaInfo { FilePath = filePath };
            if (string.IsNullOrWhiteSpace(json)) return info;
            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var ser = new DataContractJsonSerializer(typeof(FfprobeRoot));
                    var root = (FfprobeRoot)ser.ReadObject(ms);
                    if (root?.format != null)
                    {
                        if (double.TryParse(root.format.duration, NumberStyles.Any, CultureInfo.InvariantCulture, out double fd))
                            info.DurationSeconds = fd;
                        if (long.TryParse(root.format.size, out long sz))
                            info.SizeBytes = sz;
                    }
                    if (root?.streams != null)
                    {
                        foreach (var s in root.streams)
                        {
                            string ct = s.codec_type ?? "";
                            if (ct == "video")
                            {
                                info.VideoCodec = s.codec_name;
                                info.Width = s.width;
                                info.Height = s.height;
                                info.PixelFormat = s.pix_fmt;
                                info.NominalFrameRate = ParseRational(s.avg_frame_rate);
                                if (info.NominalFrameRate <= 0)
                                    info.NominalFrameRate = ParseRational(s.r_frame_rate);
                                if (info.DurationSeconds <= 0 &&
                                    double.TryParse(s.duration, NumberStyles.Any, CultureInfo.InvariantCulture, out double vd))
                                    info.DurationSeconds = vd;
                            }
                            else if (ct == "audio")
                            {
                                var at = new AudioTrackInfo
                                {
                                    Index = info.AudioTracks.Count,
                                    Codec = s.codec_name,
                                    Language = TagValue(s.tags, "language"),
                                    Title = TagValue(s.tags, "title")
                                };
                                if (int.TryParse(s.sample_rate, out int sr)) at.SampleRate = sr;
                                at.Channels = s.channels;
                                at.BitRate = FormatBitRate(s.bit_rate);
                                info.AudioTracks.Add(at);
                            }
                            else if (ct == "subtitle")
                            {
                                var st = new SubtitleTrackInfo
                                {
                                    Index = info.SubtitleTracks.Count,
                                    Codec = s.codec_name,
                                    Language = TagValue(s.tags, "language"),
                                    Title = TagValue(s.tags, "title")
                                };
                                info.SubtitleTracks.Add(st);
                            }
                        }
                    }
                    if (root?.chapters != null)
                    {
                        foreach (var ch in root.chapters)
                        {
                            string title = TagValue(ch.tags, "title");
                            if (string.IsNullOrWhiteSpace(title)) title = "Chapter " + (info.Chapters.Count + 1);
                            info.Chapters.Add(new ChapterInfo
                            {
                                Index = info.Chapters.Count,
                                StartMs = ChapterTimeToMs(ch.start, ch.time_base),
                                EndMs = ChapterTimeToMs(ch.end, ch.time_base),
                                Title = title
                            });
                        }
                    }
                }
            }
            catch
            {
                // Corrupt or unexpected JSON: return whatever we have.
            }
            return info;
        }

        private static string TagValue(Dictionary<string, string> tags, string name)
        {
            if (tags != null && tags.TryGetValue(name, out string v)) return v;
            return null;
        }

        /// <summary>
        /// 将 ffprobe 章节的 start/end 值按 time_base 转换为毫秒。
        /// time_base 形如 "1/1000" 或 "1/1000000"；无法解析时原样返回（假定为毫秒）。
        /// </summary>
        private static long ChapterTimeToMs(long value, string timeBase)
        {
            if (string.IsNullOrEmpty(timeBase)) return value;
            string[] parts = timeBase.Split('/');
            if (parts.Length == 2 &&
                long.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out long num) &&
                long.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out long den) &&
                den > 0)
            {
                return (long)(value * num * 1000.0 / den);
            }
            return value;
        }

        /// <summary>
        /// 根据章节列表生成 FFMETADATA1 格式的字符串（用于 ffmpeg -i meta -map_metadata 1 注入章节）。
        /// chapters 为 null 或空时返回 null。
        /// </summary>
        public static string GenerateFfmetadata(List<ChapterInfo> chapters)
        {
            if (chapters == null || chapters.Count == 0) return null;
            var sb = new StringBuilder();
            sb.AppendLine(";FFMETADATA1");
            sb.AppendLine();
            foreach (var ch in chapters)
            {
                sb.AppendLine("[CHAPTER]");
                sb.AppendLine("TIMEBASE=1/1000");
                sb.AppendLine("START=" + ch.StartMs);
                sb.AppendLine("END=" + ch.EndMs);
                string title = string.IsNullOrEmpty(ch.Title)
                    ? ("Chapter " + (ch.Index + 1))
                    : ch.Title;
                title = title.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
                sb.AppendLine("title=" + title);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// 判断当前任务是否满足章节写入条件：
        /// 1) 用户开启 PreserveChapters
        /// 2) 速度倍率为 1（变速会改变时间轴）
        /// 3) 无视频编辑参数变化（crop/rotate/eq/watermark/speed/burn/lossless/twoPass 都没有）
        /// 4) 有章节数据
        /// 5) 输出格式支持章节（.mp4/.mkv/.mov/.m4v/.m4b/.m4a）
        /// </summary>
        public static bool SupportChapterWrite(ConversionTask task)
        {
            if (task == null || !task.PreserveChapters) return false;
            if (task.Chapters == null || task.Chapters.Count == 0) return false;
            if (task.Speed > 0 && Math.Abs(task.Speed - 1.0) > 0.001) return false;
            if (HasVideoFilters(task)) return false;
            if (task.TwoPass) return false;
            string ext = (task.Preset?.GetExtension() ?? "").ToLowerInvariant();
            if (ext != ".mp4" && ext != ".mkv" && ext != ".mov" &&
                ext != ".m4v" && ext != ".m4b" && ext != ".m4a") return false;
            return true;
        }

#pragma warning disable CS0649 // DTO fields are populated by DataContractJsonSerializer via reflection.
        [DataContract]
        private class FfprobeRoot
        {
            [DataMember(Name = "streams")] public List<FfprobeStream> streams;
            [DataMember(Name = "format")] public FfprobeFormat format;
            [DataMember(Name = "chapters")] public List<FfprobeChapter> chapters;
        }

        [DataContract]
        private class FfprobeStream
        {
            [DataMember(Name = "index")] public int index;
            [DataMember(Name = "codec_type")] public string codec_type;
            [DataMember(Name = "codec_name")] public string codec_name;
            [DataMember(Name = "width")] public int width;
            [DataMember(Name = "height")] public int height;
            [DataMember(Name = "sample_rate")] public string sample_rate;
            [DataMember(Name = "channels")] public int channels;
            [DataMember(Name = "bit_rate")] public string bit_rate;
            [DataMember(Name = "duration")] public string duration;
            [DataMember(Name = "pix_fmt")] public string pix_fmt;
            [DataMember(Name = "avg_frame_rate")] public string avg_frame_rate;
            [DataMember(Name = "r_frame_rate")] public string r_frame_rate;
            [DataMember(Name = "tags")] public Dictionary<string, string> tags;
        }

        [DataContract]
        private class FfprobeFormat
        {
            [DataMember(Name = "duration")] public string duration;
            [DataMember(Name = "pix_fmt")] public string pix_fmt;
            [DataMember(Name = "avg_frame_rate")] public string avg_frame_rate;
            [DataMember(Name = "r_frame_rate")] public string r_frame_rate;
            [DataMember(Name = "size")] public string size;
        }

        [DataContract]
        private class FfprobeChapter
        {
            [DataMember(Name = "id")] public int id;
            [DataMember(Name = "time_base")] public string time_base;
            [DataMember(Name = "start")] public long start;
            [DataMember(Name = "end")] public long end;
            [DataMember(Name = "tags")] public Dictionary<string, string> tags;
        }
#pragma warning restore CS0649

        private static string FormatBitRate(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            if (long.TryParse(raw, out long bps))
            {
                if (bps >= 1000000) return (bps / 1000000.0).ToString("0.0") + " Mbps";
                if (bps >= 1000) return (bps / 1000.0).ToString("0.0") + " kbps";
            }
            return raw;
        }

        /// <summary>
        /// Extract a single video frame at a specific time (milliseconds) as PNG.
        /// </summary>
        public static async Task<Image> GetFrameAtTimeAsync(string filePath, long ms, int width, int height)
        {
            if (!File.Exists(FFmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", FFmpegPath);

            return await RunUnderGlobalConcurrencyAsync(
                () => ExtractPngFrameAsync(filePath, ms / 1000.0, width, height, 0));
        }

        /// <summary>
        /// 单帧 PNG 管道提取的公共核心（GetFrameAtTimeAsync 与 GetThumbnailAsync 共用）。
        /// timeoutMs &gt; 0 时设置上限，避免个别封装（rm/rmvb）让 ffmpeg 卡死。
        /// </summary>
        private static async Task<Image> ExtractPngFrameAsync(string filePath, double seconds, int width, int height, int timeoutMs)
        {
            // width/height &lt;= 0 时不指定 -s，保持原始分辨率，由调用方用 SizeMode=Zoom 缩放显示。
            string sizeArg = (width > 0 && height > 0)
                ? string.Format(CultureInfo.InvariantCulture, " -s {0}x{1}", width, height)
                : "";
            string ss = seconds.ToString("0.000", CultureInfo.InvariantCulture);
            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    "-nostdin -ss {0} -i \"{1}\" -vframes 1{2} -f image2pipe -vcodec png - {3}",
                    ss, filePath, sizeArg, tag),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var tcs = new TaskCompletionSource<object>();
                proc.Exited += (s, e) => tcs.TrySetResult(null);
                proc.Start();
                ProcessGuard.Register(proc, tempFile);

                var msStream = new MemoryStream();
                var copyTask = proc.StandardOutput.BaseStream.CopyToAsync(msStream);

                Task winner = timeoutMs > 0
                    ? await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false)
                    : tcs.Task;
                if (timeoutMs > 0 && winner != tcs.Task)
                {
                    try { proc.Kill(); } catch { }
                    return null;
                }

                await copyTask.ConfigureAwait(false);
                if (msStream.Length == 0) return null;
                msStream.Position = 0;
                using (var temp = Image.FromStream(msStream))
                    return new Bitmap(temp);
            }
        }

        /// <summary>
        /// Extract keyframe timestamps (milliseconds) using ffprobe.
        /// </summary>
        public static async Task<List<long>> GetKeyframesAsync(string filePath)
        {
            var list = new List<long>();
            if (!File.Exists(FFprobePath)) return list;

            return await RunUnderGlobalConcurrencyAsync(async () =>
            {
                // ffprobe does not support -progress.
                var psi = new ProcessStartInfo
                {
                    FileName = FFprobePath,
                    Arguments = string.Format(
                        "-nostdin -v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 \"{0}\"",
                        filePath),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    var tcs = new TaskCompletionSource<object>();
                    proc.Exited += (s, e) => tcs.TrySetResult(null);
                    proc.Start();
                    ProcessGuard.Register(proc);

                    string output = await Task.Run(() => proc.StandardOutput.ReadToEnd()).ConfigureAwait(false);
                    await tcs.Task.ConfigureAwait(false);

                    foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double sec))
                            list.Add((long)(sec * 1000));
                    }
                }
                list.Sort();
                return list;
            });
        }

        /// <summary>
        /// Extract a PNG thumbnail at the 1-second mark.
        /// </summary>
        public static async Task<Image> GetThumbnailAsync(string filePath, int width, int height)
        {
            if (!File.Exists(FFmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", FFmpegPath);

            // 固定 1 秒处取帧；10 秒超时避免个别封装卡死 ffmpeg。
            return await RunUnderGlobalConcurrencyAsync(
                () => ExtractPngFrameAsync(filePath, 1.0, width, height, 10000));
        }

        #region Bitrate control (CBR / VBR / quality)

        /// <summary>视频码率模式：自动 / 固定码率(CBR) / 可变码率(VBR) / 质量控制(CRF/QP)。</summary>
        public enum BitrateMode { Auto, CBR, VBR, Quality }

        public static string BitrateModeLabel(BitrateMode m)
        {
            switch (m)
            {
                case BitrateMode.CBR: return "固定码率";
                case BitrateMode.VBR: return "可变码率";
                case BitrateMode.Quality: return "质量控制";
                default: return "自动";
            }
        }

        public static BitrateMode ParseBitrateMode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return BitrateMode.Auto;
            if (string.Equals(s, "cbr", StringComparison.OrdinalIgnoreCase)) return BitrateMode.CBR;
            if (string.Equals(s, "vbr", StringComparison.OrdinalIgnoreCase)) return BitrateMode.VBR;
            if (string.Equals(s, "quality", StringComparison.OrdinalIgnoreCase)) return BitrateMode.Quality;
            return BitrateMode.Auto;
        }

        /// <summary>质量控制参数规范：参数名 + 取值范围 + 推荐值 + 是否支持 VBV 峰值约束。</summary>
        public class QualitySpec
        {
            public string Param;
            public int Min;
            public int Max;
            public int Recommended;
            /// <summary>该编码器是否响应 -maxrate/-bufsize（intra-only 编码器为 false）。</summary>
            public bool SupportsVbv = true;

            public override string ToString()
            {
                return string.Format("{0}~{1}，推荐 {2}", Min, Max, Recommended);
            }
        }

        /// <summary>
        /// 该编码器是否支持 VBV（-maxrate/-bufsize 峰值缓冲约束）。
        /// VBV 是独立于码率控制模式之外的一层，CBR / VBR / 质量控制 三种模式都能叠加。
        /// 实测：mjpeg 加 -maxrate 输出字节数完全不变（intra-only 无 VBV 模型），
        ///       而 mpeg4 会被有效限制（8369→1941 kbps），故不能按 "-q:v 系" 一刀切。
        /// </summary>
        public static bool SupportsVbv(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return false;
            string e = encoder.ToLowerInvariant();
            if (e == "copy") return false;
            // intra-only / 无码率控制模型的编码器：maxrate 被静默忽略。
            if (e.Contains("mjpeg") || e.Contains("prores") || e.Contains("ffv1") ||
                e.Contains("dnxhd") || e.Contains("rawvideo") || e.Contains("huffyuv") ||
                e.Contains("png") || e.Contains("bmp") || e.Contains("tiff") ||
                e.Contains("gif") || e.Contains("webp") || e.Contains("qtrle"))
                return false;
            return true;
        }

        /// <summary>
        /// 按实际 ffmpeg 编码器名返回质量控制参数规范（-crf / -qp / -cq / -global_quality / -q:v）。
        /// 前缀匹配保证 CPU/GPU 解析后的编码器（如 libx264、h264_nvenc、hevc_qsv）都能命中；
        /// 不支持的编码器返回 null（界面不提供质量控制选项）。
        /// </summary>
        public static QualitySpec GetQualitySpec(string encoder)
        {
            var spec = GetQualitySpecCore(encoder);
            if (spec != null) spec.SupportsVbv = SupportsVbv(encoder);
            return spec;
        }

        private static QualitySpec GetQualitySpecCore(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return null;
            string e = encoder.ToLowerInvariant();
            if (e.Contains("x264")) return new QualitySpec { Param = "-crf", Min = 0, Max = 51, Recommended = 23 };
            if (e.Contains("x265")) return new QualitySpec { Param = "-crf", Min = 0, Max = 51, Recommended = 28 };
            if (e.Contains("nvenc"))
            {
                if (e.Contains("av1")) return new QualitySpec { Param = "-cq", Min = 0, Max = 63, Recommended = 32 };
                if (e.Contains("hevc")) return new QualitySpec { Param = "-cq", Min = 0, Max = 51, Recommended = 28 };
                return new QualitySpec { Param = "-cq", Min = 0, Max = 51, Recommended = 23 };
            }
            if (e.Contains("qsv"))
            {
                if (e.Contains("hevc")) return new QualitySpec { Param = "-global_quality", Min = 1, Max = 51, Recommended = 28 };
                return new QualitySpec { Param = "-global_quality", Min = 1, Max = 51, Recommended = 23 };
            }
            if (e.Contains("amf"))
            {
                if (e.Contains("hevc")) return new QualitySpec { Param = "-qp", Min = 0, Max = 51, Recommended = 28 };
                return new QualitySpec { Param = "-qp", Min = 0, Max = 51, Recommended = 23 };
            }
            if (e.Contains("vpx")) return new QualitySpec { Param = "-crf", Min = 0, Max = 63, Recommended = 31 };
            if (e.Contains("aom") || e.Contains("svtav1")) return new QualitySpec { Param = "-crf", Min = 0, Max = 63, Recommended = 32 };
            if (e.Contains("xvid") || e.Contains("mpeg4") || e.Contains("mpeg2") || e.Contains("h263") ||
                e.Contains("mjpeg") || e.Contains("wmv") || e.Contains("msmpeg"))
                return new QualitySpec { Param = "-q:v", Min = 1, Max = 31, Recommended = 5 };
            if (e.Contains("prores")) return new QualitySpec { Param = "-q:v", Min = 1, Max = 63, Recommended = 10 };
            if (e.Contains("ffv1")) return new QualitySpec { Param = "-q:v", Min = 1, Max = 31, Recommended = 5 };
            return null;
        }

        /// <summary>是否支持目标码率（固定/可变）控制。copy 流复制不支持。</summary>
        public static bool SupportsTargetBitrate(string encoder)
        {
            return !string.IsNullOrEmpty(encoder) &&
                   !string.Equals(encoder, "copy", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 按码率模式把码率/质量控制参数追加到命令行：
        ///   CBR     → -b:v X -maxrate X -bufsize 2X
        ///   VBR     → -b:v X，可选 -maxrate M -bufsize 2M（受限 VBR / constrained VBR）
        ///   Quality → -(crf|qp|cq|global_quality|q:v) V，可选 -maxrate M -bufsize 2M（Capped CRF）
        ///   Auto    → 兼容旧行为：有码率就 -b:v X
        /// 说明：-maxrate/-bufsize 是 VBV 峰值约束，独立于码率控制模式，三种模式都可叠加；
        ///       x264 实测缺少 -bufsize 时 -maxrate 会被静默丢弃，故始终成对输出。
        /// </summary>
        public static void AppendVideoBitrate(StringBuilder sb, PresetOption p, string encoder)
        {
            if (p == null) return;
            BitrateMode mode = ParseBitrateMode(p.BitrateMode);
            string bitrate = p.VideoBitrate;
            switch (mode)
            {
                case BitrateMode.CBR:
                    if (!string.IsNullOrEmpty(bitrate) && SupportsTargetBitrate(encoder))
                    {
                        sb.AppendFormat(" -b:v {0}", bitrate);
                        sb.AppendFormat(" -maxrate {0}", bitrate);
                        sb.AppendFormat(" -bufsize {0}", DoubleBitrate(bitrate));
                    }
                    break;
                case BitrateMode.VBR:
                    if (!string.IsNullOrEmpty(bitrate) && SupportsTargetBitrate(encoder))
                    {
                        sb.AppendFormat(" -b:v {0}", bitrate);
                        // 受限 VBR：平均码率 = -b:v，瞬时峰值不超过 -maxrate。
                        // 若用户选的最大码率低于目标码率，x264 会以 maxrate 为准而退化成 CBR，
                        // 这里自动提升到目标码率，避免与用户设置的平均码率自相矛盾。
                        string mr = ClampMaxRate(p.QualityMaxRate, bitrate);
                        if (!string.IsNullOrEmpty(mr) && SupportsVbv(encoder))
                        {
                            sb.AppendFormat(" -maxrate {0}", mr);
                            sb.AppendFormat(" -bufsize {0}", DoubleBitrate(mr));
                        }
                    }
                    break;
                case BitrateMode.Quality:
                    var spec = GetQualitySpec(encoder);
                    if (spec != null && p.QualityValue > 0)
                    {
                        sb.AppendFormat(" {0} {1}", spec.Param, p.QualityValue);
                        if (!string.IsNullOrEmpty(p.QualityMaxRate) && SupportsVbv(encoder))
                        {
                            sb.AppendFormat(" -maxrate {0}", p.QualityMaxRate);
                            sb.AppendFormat(" -bufsize {0}", DoubleBitrate(p.QualityMaxRate));
                        }
                    }
                    break;
                default: // Auto
                    if (!string.IsNullOrEmpty(bitrate) && SupportsTargetBitrate(encoder))
                    {
                        sb.AppendFormat(" -b:v {0}", bitrate);
                    }
                    else if (string.IsNullOrEmpty(bitrate))
                    {
                        // 码率=自动且预设无固定码率 → 输出程序默认质量参数（如 -crf 23 / -cq 26）。
                        var defSpec = DefaultCodecSettings.GetVideoDefault(encoder);
                        if (defSpec != null)
                            sb.AppendFormat(" {0} {1}", defSpec.Param, defSpec.Recommended);
                    }
                    break;
            }
        }

        /// <summary>"5000k" / "5M" / "5000000" → 归一化为 kbps 数值；无法解析返回 -1。</summary>
        public static long ParseBitrateKbps(string bitrate)
        {
            if (string.IsNullOrWhiteSpace(bitrate)) return -1;
            var m = Regex.Match(bitrate.Trim(), @"^(\d+)\s*([kKmM]?)$");
            if (!m.Success) return -1;
            long n;
            if (!long.TryParse(m.Groups[1].Value, out n)) return -1;
            string unit = m.Groups[2].Value.ToLowerInvariant();
            if (unit == "m") return n * 1000;
            if (unit == "k") return n;
            return n / 1000;   // 无单位视为 bps
        }

        /// <summary>
        /// VBR 用：最大码率不得低于目标平均码率，否则实际会退化为 CBR。
        /// 低于时返回目标码率；maxRate 为空返回 null（表示不加 VBV 约束）。
        /// </summary>
        public static string ClampMaxRate(string maxRate, string targetBitrate)
        {
            if (string.IsNullOrWhiteSpace(maxRate)) return null;
            long mr = ParseBitrateKbps(maxRate);
            long tb = ParseBitrateKbps(targetBitrate);
            if (mr < 0 || tb < 0) return maxRate;
            return mr < tb ? targetBitrate : maxRate;
        }

        /// <summary>"5000k" → "10000k"（bufsize 一般取码率的 2 倍）。</summary>
        private static string DoubleBitrate(string bitrate)
        {
            if (string.IsNullOrWhiteSpace(bitrate)) return bitrate;
            var m = Regex.Match(bitrate.Trim(), @"^(\d+)([km]?)$", RegexOptions.IgnoreCase);
            if (!m.Success) return bitrate;
            long n;
            if (!long.TryParse(m.Groups[1].Value, out n)) return bitrate;
            return (n * 2).ToString() + m.Groups[2].Value.ToLowerInvariant();
        }

        #endregion

        #region Smart stream copy (high-speed mode)

        /// <summary>
        /// 把编码器名或 ffprobe 的 codec_name 归一到家族名（h264/hevc/mpeg4/vp9/av1…），
        /// 用于「输入编码 == 目标编码」的流级 copy 判定。返回 null 表示 copy/未知。
        /// </summary>
        public static string NormalizeVideoCodec(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string e = s.Trim().ToLowerInvariant();
            if (e == "copy") return null;
            if (e.Contains("x264") || e == "h264" || e.Contains("avc1") || e == "h264_nvenc" || e == "h264_qsv" || e == "h264_amf")
                return "h264";
            if (e.Contains("x265") || e == "hevc" || e.Contains("hvc1") || e == "h265" || e.Contains("hevc_nvenc") || e.Contains("hevc_qsv") || e.Contains("hevc_amf"))
                return "hevc";
            if (e.Contains("xvid") || e.Contains("mpeg4") || e == "mp4v" || e.Contains("divx"))
                return "mpeg4";
            if (e.Contains("vp9")) return "vp9";
            if (e.Contains("vp8") || e == "libvpx") return "vp8";
            if (e.Contains("av1") || e.Contains("aom") || e.Contains("svt")) return "av1";
            if (e.Contains("mpeg2") || e == "mpg2") return "mpeg2video";
            if (e == "wmv2") return "wmv2";
            if (e == "wmv3") return "wmv3";
            if (e == "vc1" || e == "wvc1") return "vc1";
            if (e.Contains("mjpeg") || e == "jpg" || e == "jpeg") return "mjpeg";
            if (e.Contains("theora")) return "theora";
            if (e == "gif" || e == "gifv") return "gif";
            if (e.Contains("h263")) return "h263";
            if (e.Contains("prores")) return "prores";
            if (e.Contains("ffv1")) return "ffv1";
            if (e == "png") return "png";
            if (e == "bmp") return "bmp";
            if (e == "tiff") return "tiff";
            if (e.Contains("webp")) return "webp";
            return e;
        }

        /// <summary>音频编码器/编解码名 → 家族名（aac/mp3/opus/vorbis/ac3/wma/flac/alac/pcm…）。</summary>
        public static string NormalizeAudioCodec(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string e = s.Trim().ToLowerInvariant();
            if (e == "copy") return null;
            if (e.Contains("aac")) return "aac";
            if (e.Contains("mp3") || e.Contains("lame")) return "mp3";
            if (e.Contains("opus")) return "opus";
            if (e.Contains("vorbis")) return "vorbis";
            if (e == "ac3" || e == "eac3") return "ac3";
            if (e.Contains("wma") || e.Contains("wmav")) return "wma";
            if (e.Contains("flac")) return "flac";
            if (e.Contains("alac")) return "alac";
            if (e.Contains("pcm")) return "pcm";
            return e;
        }

        /// <summary>
        /// 解析任务的目标视频编码器（实际 ffmpeg 编码器名）。
        /// 预设指定了编码 → 按硬件勾选解析（libx264/h264_nvenc…）；
        /// 预设为 copy（与源文件相同）→ 取目标容器的默认视频编码器。
        /// </summary>
        public static string ResolveTargetVideoEncoder(ConversionTask task, HardwareSupport hw, bool useHardware)
        {
            string target = task.Preset?.VideoCodec;
            if (string.IsNullOrEmpty(target) || string.Equals(target, "copy", StringComparison.OrdinalIgnoreCase))
            {
                string container = GetContainerKey(task.Preset);
                target = DefaultCodecSettings.GetContainerVideoEncoder(container);
                if (string.IsNullOrEmpty(target)) return null;
            }
            string resolved = ResolveVideoEncoder(target, useHardware ? hw : null, task.Preset?.VideoCodecLabel);
            return string.Equals(resolved, "copy", StringComparison.OrdinalIgnoreCase) ? null : resolved;
        }

        /// <summary>解析任务的目标音频编码器（实际 ffmpeg 编码器名），copy/空 → 容器默认。</summary>
        public static string ResolveTargetAudioEncoder(ConversionTask task)
        {
            string target = task.Preset?.AudioCodec;
            if (string.IsNullOrEmpty(target) || string.Equals(target, "copy", StringComparison.OrdinalIgnoreCase))
            {
                string container = GetContainerKey(task.Preset);
                target = DefaultCodecSettings.GetContainerAudioEncoder(container);
            }
            return (string.IsNullOrEmpty(target) || string.Equals(target, "copy", StringComparison.OrdinalIgnoreCase))
                ? null
                : target;
        }

        public static string GetContainerKey(PresetOption p)
        {
            string ext = p != null ? p.GetExtension() : ".mp4";
            string key = ext.TrimStart('.').ToUpperInvariant();
            return key.Length > 0 ? key : "MP4";
        }

        /// <summary>容器 → 支持的视频编码家族白名单；null 表示几乎全支持（MKV）。</summary>
        private static readonly Dictionary<string, HashSet<string>> ContainerVideoCodecs =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "MP4",  new HashSet<string> { "h264", "hevc", "mpeg4", "av1", "vp9", "h263", "mjpeg" } },
                { "M4V",  new HashSet<string> { "h264", "hevc", "mpeg4", "av1", "h263", "mjpeg" } },
                { "MKV",  null },
                { "MOV",  new HashSet<string> { "h264", "hevc", "mpeg4", "mpeg2video", "av1", "prores", "mjpeg" } },
                { "WEBM", new HashSet<string> { "vp9", "vp8", "av1" } },
                { "AVI",  new HashSet<string> { "mpeg4", "mpeg2video", "wmv2", "wmv3", "h263", "mjpeg", "h264" } },
                { "FLV",  new HashSet<string> { "h264", "mpeg4" } },
                { "WMV",  new HashSet<string> { "wmv2", "wmv3", "vc1" } },
                { "TS",   new HashSet<string> { "h264", "hevc", "mpeg2video", "mpeg4", "av1" } },
                { "MTS",  new HashSet<string> { "h264", "hevc", "mpeg2video", "mpeg4", "av1" } },
                { "M2TS", new HashSet<string> { "h264", "hevc", "mpeg2video", "mpeg4", "av1" } },
                { "3GP",  new HashSet<string> { "h264", "mpeg4", "h263" } },
                { "OGV",  new HashSet<string> { "vp9", "vp8", "theora" } },
            };

        /// <summary>容器 → 支持的音频编码家族白名单；null 表示几乎全支持（MKV）。</summary>
        private static readonly Dictionary<string, HashSet<string>> ContainerAudioCodecs =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            {
                { "MP4",  new HashSet<string> { "aac", "mp3", "ac3", "alac", "opus" } },
                { "M4V",  new HashSet<string> { "aac", "mp3", "ac3", "alac" } },
                { "MKV",  null },
                { "MOV",  new HashSet<string> { "aac", "mp3", "ac3", "alac", "pcm" } },
                { "WEBM", new HashSet<string> { "opus", "vorbis" } },
                { "AVI",  new HashSet<string> { "mp3", "ac3", "pcm", "wma" } },
                { "FLV",  new HashSet<string> { "aac", "mp3" } },
                { "WMV",  new HashSet<string> { "wma" } },
                { "TS",   new HashSet<string> { "aac", "mp3", "ac3" } },
                { "MTS",  new HashSet<string> { "aac", "mp3", "ac3" } },
                { "M2TS", new HashSet<string> { "aac", "mp3", "ac3" } },
                { "3GP",  new HashSet<string> { "aac" } },
                { "OGV",  new HashSet<string> { "opus", "vorbis" } },
            };

        /// <summary>
        /// 判断给定编码家族是否可被直接复制进目标容器（流复制容器兼容性校验）。
        /// 未知容器视为兼容，避免过度阻止 copy。
        /// </summary>
        public static bool IsCodecSupportedByContainer(string codecFamily, string containerKey, bool isAudio)
        {
            if (string.IsNullOrEmpty(codecFamily) || string.IsNullOrEmpty(containerKey)) return true;
            var map = isAudio ? ContainerAudioCodecs : ContainerVideoCodecs;
            if (!map.TryGetValue(containerKey, out var supported)) return true;
            if (supported == null) return true;
            return supported.Contains(codecFamily);
        }

        /// <summary>
        /// 评估高速转换模式下视频/音频流是否实际走 copy。
        /// 与 AppendSmartCopyStreams 内部判定逻辑一致，供 UI 显示实际转换模式。
        /// </summary>
        public static void EvaluateSmartCopy(ConversionTask task, out bool videoCopy, out bool audioCopy)
        {
            videoCopy = false;
            audioCopy = false;
            var p = task.Preset;
            string vEnc = task.TargetVideoEncoder;
            string aEnc = task.TargetAudioEncoder;
            string inV = NormalizeVideoCodec(task.SourceVideoCodec);
            string inA = NormalizeAudioCodec(task.SourceAudioCodec);
            string containerKey = GetContainerKey(p);

            bool vAuto = p == null ||
                         (string.IsNullOrEmpty(p.BitrateMode) ||
                          string.Equals(p.BitrateMode, "auto", StringComparison.OrdinalIgnoreCase)) &&
                         string.IsNullOrEmpty(p.VideoBitrate);
            // 预设指定了分辨率时，源分辨率必须匹配才能 copy（否则需重编码缩放）。
            // 源分辨率未知(null)时不阻止 copy（尚未探测的场景）。
            bool resMatch = p == null || string.IsNullOrEmpty(p.ResolutionValue)
                            || string.IsNullOrEmpty(task.SourceResolution)
                            || string.Equals(task.SourceResolution, p.ResolutionValue, StringComparison.OrdinalIgnoreCase)
                            || string.Equals((task.SourceResolution ?? "").Replace(" ", ""), p.ResolutionValue, StringComparison.OrdinalIgnoreCase);
            videoCopy = vAuto && resMatch && !string.IsNullOrEmpty(inV) && NormalizeVideoCodec(vEnc) == inV
                        && IsCodecSupportedByContainer(inV, containerKey, false);

            if (task.SelectedAudioTrack != null)
            {
                bool aAuto = p == null || string.IsNullOrEmpty(p.AudioBitrate);
                audioCopy = aAuto && !string.IsNullOrEmpty(inA) && NormalizeAudioCodec(aEnc) == inA
                            && IsCodecSupportedByContainer(inA, containerKey, true);
            }
        }

        /// <summary>
        /// 高速转换的智能流判定：视频/音频各自判定——
        /// 输入编码 == 目标编码（规范化后）且该流码率模式为自动 → copy；
        /// 否则用目标编码器转码并带默认参数（自动码率）或预设码率参数。
        /// </summary>
        private static void AppendSmartCopyStreams(StringBuilder sb, ConversionTask task)
        {
            var p = task.Preset;
            string vEnc = task.TargetVideoEncoder;
            string aEnc = task.TargetAudioEncoder;
            string inV = NormalizeVideoCodec(task.SourceVideoCodec);
            string inA = NormalizeAudioCodec(task.SourceAudioCodec);
            string containerKey = GetContainerKey(p);

            // 视频流：编码一致 且 码率模式为自动 → copy（否则按预设/默认参数转码）
            bool vAuto = p == null ||
                         (string.IsNullOrEmpty(p.BitrateMode) ||
                          string.Equals(p.BitrateMode, "auto", StringComparison.OrdinalIgnoreCase)) &&
                         string.IsNullOrEmpty(p.VideoBitrate);
            bool resMatch = p == null || string.IsNullOrEmpty(p.ResolutionValue)
                            || string.IsNullOrEmpty(task.SourceResolution)
                            || string.Equals(task.SourceResolution, p.ResolutionValue, StringComparison.OrdinalIgnoreCase)
                            || string.Equals((task.SourceResolution ?? "").Replace(" ", ""), p.ResolutionValue, StringComparison.OrdinalIgnoreCase);
            bool vCopy = vAuto && resMatch && !string.IsNullOrEmpty(inV) && NormalizeVideoCodec(vEnc) == inV
                         && IsCodecSupportedByContainer(inV, containerKey, false);
            if (vCopy)
            {
                sb.Append(" -c:v copy");
            }
            else if (!string.IsNullOrEmpty(vEnc))
            {
                sb.AppendFormat(" -c:v {0}", vEnc);
                if (p != null && !string.IsNullOrEmpty(p.ResolutionValue))
                    sb.AppendFormat(" -s {0}", p.ResolutionValue);
                AppendVideoBitrate(sb, p, vEnc);
                if (p != null && !string.IsNullOrEmpty(p.FrameRate))
                    sb.AppendFormat(" -r {0}", p.FrameRate);
            }
            else
            {
                sb.Append(" -c:v copy");
            }

            // 音频流：编码一致 且 音频码率自动 → copy
            if (task.SelectedAudioTrack == null)
            {
                // -an 已由 stream mapping 部分输出，这里直接返回。
                return;
            }
            bool aAuto = p == null || string.IsNullOrEmpty(p.AudioBitrate);
            bool aCopy = aAuto && !string.IsNullOrEmpty(inA) && NormalizeAudioCodec(aEnc) == inA
                         && IsCodecSupportedByContainer(inA, containerKey, true);
            if (aCopy)
            {
                sb.Append(" -c:a copy");
            }
            else if (!string.IsNullOrEmpty(aEnc))
            {
                sb.AppendFormat(" -c:a {0}", aEnc);
                string br = DefaultCodecSettings.GetAudioDefaultBitrate(aEnc);
                if (!string.IsNullOrEmpty(br)) sb.AppendFormat(" -b:a {0}", br);
                // 预设采样率为 auto(-1/0/空) → 默认 44100；否则用预设值。
                int sr = ParseSampleRate(p);
                if (sr > 0) sb.AppendFormat(" -ar {0}", sr);
                // 预设声道为 auto(-1/0) → 默认 2；否则用预设值。
                int ch = (p != null && p.Channels > 0) ? p.Channels : DefaultCodecSettings.GetDefaultChannels();
                if (ch > 0) sb.AppendFormat(" -ac {0}", ch);
            }
            else
            {
                sb.Append(" -c:a copy");
            }
        }

        /// <summary>解析预设采样率：auto(-1/0/空) → 默认 44100；否则返回预设值。</summary>
        private static int ParseSampleRate(PresetOption p)
        {
            if (p == null || string.IsNullOrEmpty(p.SampleRate)) return DefaultCodecSettings.GetDefaultSampleRate();
            string s = p.SampleRate.Trim();
            if (s == "-1" || s == "0") return DefaultCodecSettings.GetDefaultSampleRate();
            int v;
            return int.TryParse(s, out v) && v > 0 ? v : DefaultCodecSettings.GetDefaultSampleRate();
        }

        #endregion

        /// <summary>
        /// Build ffmpeg argument string for a task.
        /// Honors two optional modes set on the task before a run:
        ///  - UseStreamCopy : remux with "-c copy" (high-speed mode, same container)
        ///  - HardwareEncoder : swap the software video encoder for a HW one
        ///  - Segments/Crop : multi-segment trim and video cropping
        /// </summary>
        public static string BuildArguments(ConversionTask task)
        {            var segment = (task.Segments != null && task.Segments.Count > 0)
                ? task.Segments[0]
                : new VideoSegment { StartMs = 0, EndMs = (long)(task.SourceDurationSeconds * 1000) };
            return BuildSegmentArguments(task, segment, task.OutputPath);
        }

        /// <summary>
        /// Build ffmpeg arguments for one segment. outputPath may differ from task.OutputPath.
        /// </summary>
        /// <summary>
        /// 判断任务是否包含需要转码的视频滤镜（crop/rotate/speed/eq/watermark）。
        /// 高速流复制模式下若有任何视频滤镜，必须降级为转码。
        /// </summary>
        public static bool HasVideoFilters(ConversionTask task)
        {
            return task.Crop != null
                || task.Rotation != 0
                || (task.Speed > 0 && Math.Abs(task.Speed - 1.0) > 0.001)
                || (Math.Abs(task.Brightness) > 0.001)
                || (Math.Abs(task.Contrast - 1.0) > 0.001)
                || (Math.Abs(task.Saturation - 1.0) > 0.001)
                || !string.IsNullOrEmpty(task.WatermarkPath)
                || task.Deinterlace
                || task.BurnSubtitle
                || task.Lossless
                || task.TwoPass;
        }

        /// <summary>
        /// 构建视频滤镜链（crop → eq → transpose → scale → 水印 overlay）。
        /// 返回滤镜字符串列表；调用方用逗号串联或独立 -vf/-filter_complex。
        /// </summary>
        public static List<string> BuildVideoFilters(ConversionTask task, PresetOption preset)
        {
            var vf = new List<string>();

            // 去隔行（在裁剪前应用）
            if (task.Deinterlace)
                vf.Add("yadif=0:-1:0");

            // 裁剪
            if (task.Crop != null)
            {
                var c = task.Crop;
                vf.Add(string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}:{2}:{3}",
                    c.Width, c.Height, c.X, c.Y));
            }

            // 亮度/对比度/饱和度（eq 滤镜）
            bool hasEq = Math.Abs(task.Brightness) > 0.001
                      || Math.Abs(task.Contrast - 1.0) > 0.001
                      || Math.Abs(task.Saturation - 1.0) > 0.001;
            if (hasEq)
            {
                vf.Add(string.Format(CultureInfo.InvariantCulture,
                    "eq=brightness={0:F3}:contrast={1:F3}:saturation={2:F3}",
                    task.Brightness, task.Contrast, task.Saturation));
            }

            // 旋转
            switch (task.Rotation)
            {
                case 1: vf.Add("transpose=1"); break;
                case 2: vf.Add("transpose=2"); break;
                case 3: vf.Add("transpose=1,transpose=1"); break;
                case 4: vf.Add("hflip"); break;
                case 5: vf.Add("vflip"); break;
            }

            // 缩放
            if (preset != null && !string.IsNullOrEmpty(preset.ResolutionValue))
                vf.Add(string.Format("scale={0}", preset.ResolutionValue.Replace("x", ":")));

            return vf;
        }

        /// <summary>
        /// 构建调速相关的 PTS/atempo 滤镜参数。
        /// 视频用 setpts=PTS/N，音频用 atempo=N（atempo 单次范围 [0.5,2.0]，超出则链式）。
        /// 返回 (videoPtsFilter, audioAtempoFilter)；速度为 1.0 时返回 null。
        /// </summary>
        public static void BuildSpeedFilters(double speed, out string videoPts, out string audioAtempo)
        {
            if (speed <= 0 || Math.Abs(speed - 1.0) < 0.001)
            {
                videoPts = null;
                audioAtempo = null;
                return;
            }
            videoPts = string.Format(CultureInfo.InvariantCulture, "setpts={0:F6}*PTS", 1.0 / speed);

            // atempo 单次范围 [0.5, 2.0]；超出需链式分解。
            var parts = new List<string>();
            double remaining = speed;
            while (remaining > 2.0)
            {
                parts.Add("atempo=2.0");
                remaining /= 2.0;
            }
            while (remaining < 0.5)
            {
                parts.Add("atempo=0.5");
                remaining /= 0.5;
            }
            if (Math.Abs(remaining - 1.0) > 0.001)
                parts.Add(string.Format(CultureInfo.InvariantCulture, "atempo={0:F6}", remaining));
            audioAtempo = parts.Count > 0 ? string.Join(",", parts) : null;
        }

        /// <summary>
        /// 构建水印 overlay 滤镜参数。返回 overlay 滤镜字符串；无水印返回 null。
        /// 注意：调用方需用 -filter_complex（不能用 -vf），且 -i 水印图片需作为额外输入。
        /// </summary>
        public static string BuildWatermarkOverlay(ConversionTask task, int videoWidth, int videoHeight)
        {
            if (string.IsNullOrEmpty(task.WatermarkPath)) return null;

            // 水印缩放（相对于视频宽度）
            string scaleFilter = "";
            if (task.WatermarkScalePercent > 0)
            {
                int wmWidth = (int)(videoWidth * task.WatermarkScalePercent / 100.0);
                scaleFilter = string.Format(CultureInfo.InvariantCulture, "scale={0}:-1,", wmWidth);
            }

            // 不透明度
            string opacityFilter = "";
            if (task.WatermarkOpacity < 0.999)
                opacityFilter = string.Format(CultureInfo.InvariantCulture, "format=rgba,colorchannelmixer=aa={0:F3},", task.WatermarkOpacity);

            // 位置（overlay=x:y）
            string pos;
            int margin = 20;
            switch (task.WatermarkPosition)
            {
                case 1: pos = string.Format("{0}:{1}", margin, margin); break;
                case 2: pos = string.Format("W-w-{0}:{1}", margin, margin); break;
                case 4: pos = string.Format("{0}:H-h-{1}", margin, margin); break;
                case 5: pos = "(W-w)/2:(H-h)/2"; break;
                case 3:
                default: pos = string.Format("W-w-{0}:H-h-{1}", margin, margin); break;
            }
            return string.Format("[1:v]{2}{3}[wm];[0:v][wm]overlay={0}", pos, "", scaleFilter, opacityFilter);
        }

        public static string BuildSegmentArguments(ConversionTask task, VideoSegment segment, string outputPath)
        {
            var sb = new StringBuilder();
            sb.Append(" -y"); // overwrite output

            double startSec = segment.StartMs / 1000.0;
            double endSec = segment.EndMs / 1000.0;
            double durationSec = Math.Max(0, endSec - startSec);

            // Trim: apply -ss before input for fast seek, -t after input for accurate duration.
            if (startSec > 0)
                sb.AppendFormat(CultureInfo.InvariantCulture, " -ss {0:0.000}", startSec);

            // VC-1 (WMV) 容错：vc1 native 解码器多线程不稳且易遇损坏包，注入输入侧容错参数。
            if (task.IsVC1Input)
                sb.Append(" -fflags +discardcorrupt -err_detect ignore_err -threads 1");

            sb.AppendFormat(" -i \"{0}\"", task.InputPath);

            // External subtitle file is added as a second input so it can be
            // mapped into the output container (SoftKeepAll) or referenced by the
            // subtitles filter (BurnExternal).
            bool externalSubtitle = task.SelectedSubtitleTrack != null && task.SelectedSubtitleTrack.IsExternal;
            if (externalSubtitle)
                sb.AppendFormat(" -i \"{0}\"", task.SelectedSubtitleTrack.FilePath);

            if (durationSec > 0 && durationSec < task.SourceDurationSeconds - 0.05)
                sb.AppendFormat(CultureInfo.InvariantCulture, " -t {0:0.000}", durationSec);

            // Stream mapping for selected tracks.
            sb.Append(" -map 0:v:0");
            if (HasMultiAudioTracks(task))
            {
                AppendMultiAudioMaps(sb, task);
                if (task.SelectedAudioTrackIndices.Contains(-1))
                    sb.Append(" -c:a copy");
                else
                {
                    // 多音轨转码：所有音轨用同一编码器
                    var p0 = task.Preset;
                    if (p0 != null && !string.IsNullOrEmpty(p0.AudioCodec) &&
                        !string.Equals(p0.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
                        sb.AppendFormat(" -c:a {0}", p0.AudioCodec);
                    else
                        sb.Append(" -c:a copy");
                }
            }
            else if (task.SelectedAudioTrack != null)
                sb.AppendFormat(" -map 0:a:{0}", task.SelectedAudioTrack.Index);
            else
                sb.Append(" -an");
            // 外挂字幕仅在 SoftKeepAll 时才作为第二个 -i 输入流被映射；
            // BurnExternal 由 BuildSubtitleBurnFilter 处理，None 则丢弃。
            if (externalSubtitle && task.SubMode == SubtitleMode.SoftKeepAll)
                sb.Append(" -map 1:s:0");

            // High-speed mode: smart per-stream copy — video/audio streams are
            // copied only when input codec == target codec, otherwise re-encoded
            // with the target codec + auto defaults. Cannot copy when crop/rotate.
            bool hasVideoFilter = HasVideoFilters(task);
            if (task.UseStreamCopy && !hasVideoFilter)
            {
                AppendSmartCopyStreams(sb, task);
                AppendSubtitleCodec(sb, task, outputPath);
                if (SupportChapterWrite(task))
                    sb.Append(" -map_metadata 0");
                sb.AppendFormat(" \"{0}\"", outputPath);
                return sb.ToString();
            }

            var p = task.Preset;
            if (p != null)
            {
                // Video filter chain (crop / eq / rotate / scale).
                var vfParts = BuildVideoFilters(task, p);

                // 调速：视频 setpts 滤镜。
                string speedPts, speedAtempo;
                BuildSpeedFilters(task.Speed, out speedPts, out speedAtempo);
                if (!string.IsNullOrEmpty(speedPts))
                    vfParts.Add(speedPts);

                // P2: 字幕烧录（硬字幕）。subtitles 滤镜附加到 -vf 链末尾。
                string burnSub = BuildSubtitleBurnFilter(task);
                if (!string.IsNullOrEmpty(burnSub))
                    vfParts.Add(burnSub);

                // Video — 优先使用 UI 流程已解析的 TargetVideoEncoder（含硬件/CPU 降级结果），
                // 其次 HardwareEncoder，最后回退到预设的 CPU 编码器。
                string vcodec = !string.IsNullOrEmpty(task.TargetVideoEncoder)
                    ? task.TargetVideoEncoder
                    : (!string.IsNullOrEmpty(task.HardwareEncoder)
                        ? task.HardwareEncoder
                        : PresetDataStore.GetCpuEncoder(p.VideoCodec));
                if (string.Equals(p.VideoCodec, "copy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(" -c:v copy");
                }
                else
                {
                    sb.AppendFormat(" -c:v {0}", vcodec);
                    if (vfParts.Count > 0)
                        sb.AppendFormat(" -vf \"{0}\"", string.Join(",", vfParts));
                    if (!string.IsNullOrEmpty(p.ResolutionValue) && vfParts.Count == 0)
                        sb.AppendFormat(" -s {0}", p.ResolutionValue);
                    FFmpegHelper.AppendVideoBitrate(sb, p, vcodec);
                    // P2: H264 Profile/Level
                    if (!string.IsNullOrEmpty(task.H264Profile))
                        sb.AppendFormat(" -profile:v {0}", task.H264Profile);
                    if (!string.IsNullOrEmpty(task.H264Level))
                        sb.AppendFormat(" -level {0}", task.H264Level);
                    // P2: 无损转换
                    if (task.Lossless)
                    {
                        if (vcodec.Contains("nvenc") || vcodec.Contains("qsv") || vcodec.Contains("amf"))
                            sb.Append(" -lossless 1");
                        else if (vcodec.Contains("x264") || vcodec.Contains("x265"))
                            sb.Append(" -crf 0");
                    }
                    if (!string.IsNullOrEmpty(p.FrameRate))
                        sb.AppendFormat(" -r {0}", p.FrameRate);
                }

                // Audio
                if (task.SelectedAudioTrack == null)
                {
                    sb.Append(" -an");
                }
                else if (string.Equals(p.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(" -c:a copy");
                }
                else
                {
                    sb.AppendFormat(" -c:a {0}", p.AudioCodec);
                    // 调速：音频 atempo 滤镜。
                    if (!string.IsNullOrEmpty(speedAtempo))
                        sb.AppendFormat(" -af {0}", speedAtempo);
                    if (!string.IsNullOrEmpty(p.AudioBitrate))
                        sb.AppendFormat(" -b:a {0}", p.AudioBitrate);
                    else
                    {
                        // 音频码率=自动 → 程序默认码率（如 aac → -b:a 192k）
                        string br = DefaultCodecSettings.GetAudioDefaultBitrate(p.AudioCodec);
                        if (!string.IsNullOrEmpty(br)) sb.AppendFormat(" -b:a {0}", br);
                    }
                }

                // Subtitle: drop unless one is selected.
                AppendSubtitleCodec(sb, task, outputPath);
            }

            // P2: 元数据写入
            AppendMetadata(sb, task);

            // 章节保留：源文件已有章节时原样映射到输出（-map_metadata 0）。
            // 注意：-ss 在 -i 之前时章节时间戳会自动偏移，无需额外处理。
            if (SupportChapterWrite(task) && !task.UseStreamCopy)
            {
                sb.Append(" -map_metadata 0");
            }

            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        /// <summary>
        /// Append subtitle codec/map options. Handles internal streams and external files.
        /// </summary>
        /// <summary>
        /// 判断是否选择了多音轨模式（SelectedAudioTrackIndices 非空）。</summary>
        private static bool HasMultiAudioTracks(ConversionTask task)
        {
            return task.SelectedAudioTrackIndices != null && task.SelectedAudioTrackIndices.Count > 0;
        }

        /// <summary>
        /// 判断是否选择了多字幕轨模式。</summary>
        private static bool HasMultiSubtitleTracks(ConversionTask task)
        {
            return task.SelectedSubtitleTrackIndices != null && task.SelectedSubtitleTrackIndices.Count > 0;
        }

        /// <summary>
        /// 追加多音轨 -map 参数。含 -1 时映射全部音轨；否则映射指定索引。</summary>
        private static void AppendMultiAudioMaps(StringBuilder sb, ConversionTask task)
        {
            if (!HasMultiAudioTracks(task)) return;
            if (task.SelectedAudioTrackIndices.Contains(-1))
            {
                sb.Append(" -map 0:a?"); // 全部音轨，? 容忍无音轨
            }
            else
            {
                foreach (var idx in task.SelectedAudioTrackIndices)
                    sb.AppendFormat(" -map 0:a:{0}", idx);
            }
        }

        /// <summary>
        /// 追加多字幕轨 -map 参数。含 -1 时映射全部字幕；否则映射指定索引。</summary>
        private static void AppendMultiSubtitleMaps(StringBuilder sb, ConversionTask task)
        {
            if (!HasMultiSubtitleTracks(task)) return;
            if (task.SelectedSubtitleTrackIndices.Contains(-1))
            {
                sb.Append(" -map 0:s?"); // 全部字幕
            }
            else
            {
                foreach (var idx in task.SelectedSubtitleTrackIndices)
                    sb.AppendFormat(" -map 0:s:{0}", idx);
            }
            sb.Append(" -c:s copy");
        }

        private static void AppendSubtitleCodec(StringBuilder sb, ConversionTask task, string outputPath)
        {
            // 模式路由：
            //   None         → -sn（丢弃所有字幕轨）
            //   SoftKeepAll  → -map 0:s? -c:s copy（保留全部原始字幕轨）
            //   BurnExternal → 不在这里追加字幕 -map/-c（已被 BuildSubtitleBurnFilter 通过 subtitles 滤镜烧录）
            switch (task.SubMode)
            {
                case SubtitleMode.SoftKeepAll:
                    sb.Append(" -map 0:s? -c:s copy");
                    return;
                case SubtitleMode.BurnExternal:
                    // 烧录字幕已通过 -vf subtitles= 处理，无需额外 -map。
                    return;
                case SubtitleMode.None:
                default:
                    sb.Append(" -sn");
                    return;
            }
        }

        /// <summary>
        /// Build ffmpeg arguments that merge multiple segments into one output file
        /// using the concat demuxer (inpoint/outpoint per segment). This preserves
        /// audio-video sync far better than the select-filter approach.
        /// Caller must create the concat list file (see <see cref="WriteConcatList"/>)
        /// and delete it after the run.
        /// </summary>
        public static string BuildMergedArguments(ConversionTask task, string concatListPath, string outputPath)
        {
            var sb = new StringBuilder();
            sb.Append(" -y -f concat -safe 0 -i \"");
            sb.Append(concatListPath);
            sb.Append("\"");

            // External subtitle as second input (if any).
            bool externalSubtitle = task.SelectedSubtitleTrack != null && task.SelectedSubtitleTrack.IsExternal;
            if (externalSubtitle)
                sb.AppendFormat(" -i \"{0}\"", task.SelectedSubtitleTrack.FilePath);

            // Stream mapping.
            sb.Append(" -map 0:v:0");
            if (task.SelectedAudioTrack != null)
                sb.AppendFormat(" -map 0:a:{0}", task.SelectedAudioTrack.Index);
            else
                sb.Append(" -an");
            if (externalSubtitle)
                sb.Append(" -map 1:s:0");

            var p = task.Preset;
            bool hasVideoFilter = HasVideoFilters(task);

            // High-speed mode: smart per-stream copy (only when no video filters).
            if (task.UseStreamCopy && !hasVideoFilter)
            {
                AppendSmartCopyStreams(sb, task);
                AppendSubtitleCodec(sb, task, outputPath);
                if (p != null && !string.IsNullOrWhiteSpace(p.CustomArgs))
                    sb.Append(" " + p.CustomArgs.Trim());
                sb.AppendFormat(" \"{0}\"", outputPath);
                return sb.ToString();
            }

            // Re-encode path: build video filter chain (crop/eq/rotate/scale).
            var vfParts = BuildVideoFilters(task, p);

            // 调速：视频 setpts 滤镜。
            string speedPts, speedAtempo;
            BuildSpeedFilters(task.Speed, out speedPts, out speedAtempo);
            if (!string.IsNullOrEmpty(speedPts))
                vfParts.Add(speedPts);

            if (p != null)
            {
                // Video: merging segments requires re-encoding; resolve encoder.
                string vcodec = !string.IsNullOrEmpty(task.HardwareEncoder)
                    ? task.HardwareEncoder
                    : task.TargetVideoEncoder;
                if (string.IsNullOrEmpty(vcodec) ||
                    string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase))
                {
                    vcodec = PresetDataStore.GetCpuEncoder(p.VideoCodec);
                }
                if (string.IsNullOrEmpty(vcodec) ||
                    string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase))
                {
                    vcodec = "libx264"; // safe fallback for segment merge
                }
                sb.AppendFormat(" -c:v {0}", vcodec);
                if (vfParts.Count > 0)
                    sb.AppendFormat(" -vf \"{0}\"", string.Join(",", vfParts));

                AppendVideoBitrate(sb, p, vcodec);
                if (!string.IsNullOrEmpty(p.FrameRate))
                    sb.AppendFormat(" -r {0}", p.FrameRate);

                // Audio: segment merge requires re-encoding; resolve encoder.
                if (task.SelectedAudioTrack == null)
                {
                    sb.Append(" -an");
                }
                else
                {
                    string acodec = task.TargetAudioEncoder;
                    if (string.IsNullOrEmpty(acodec) ||
                        string.Equals(acodec, "copy", StringComparison.OrdinalIgnoreCase))
                    {
                        acodec = !string.IsNullOrEmpty(p.AudioCodec) &&
                                 !string.Equals(p.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase)
                            ? p.AudioCodec
                            : "aac"; // safe fallback for segment merge
                    }
                    sb.AppendFormat(" -c:a {0}", acodec);
                    // 调速：音频 atempo 滤镜。
                    if (!string.IsNullOrEmpty(speedAtempo))
                        sb.AppendFormat(" -af {0}", speedAtempo);
                    if (!string.IsNullOrEmpty(p.AudioBitrate))
                        sb.AppendFormat(" -b:a {0}", p.AudioBitrate);
                    else
                    {
                        string br = DefaultCodecSettings.GetAudioDefaultBitrate(acodec);
                        if (!string.IsNullOrEmpty(br)) sb.AppendFormat(" -b:a {0}", br);
                    }
                }
            }

            AppendSubtitleCodec(sb, task, outputPath);

            // 自定义参数（高级）：直接附加到命令行末尾。#65
            if (p != null && !string.IsNullOrWhiteSpace(p.CustomArgs))
                sb.Append(" " + p.CustomArgs.Trim());

            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        /// <summary>
        /// Write a concat demuxer list file with inpoint/outpoint for each segment.
        /// Each entry references the original input file with a time range.
        /// </summary>
        public static void WriteConcatList(ConversionTask task, string listPath)
        {
            var sb = new StringBuilder();
            // ffmpeg concat demuxer requires forward slashes.
            string inputFile = task.InputPath.Replace('\\', '/');
            // concat demuxer 用单引号包裹路径，文件名中的单引号必须转义为 '''：
            //   原文 file 'D:/path/World's.mp4'  → 解析错误
            //   转义 file 'D:/path/World'\''s.mp4' → 正确解析为 World's.mp4
            string escapedFile = inputFile.Replace("'", "'\''");
            foreach (var seg in task.Segments)
            {
                sb.AppendLine("file '" + escapedFile + "'");
                sb.AppendFormat(CultureInfo.InvariantCulture, "inpoint {0:0.000}", seg.StartMs / 1000.0);
                sb.AppendLine();
                sb.AppendFormat(CultureInfo.InvariantCulture, "outpoint {0:0.000}", seg.EndMs / 1000.0);
                sb.AppendLine();
            }
            File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// Build the fully-resolved ffmpeg parameter string for a standalone
        /// preset (no trim / crop / segment specifics), used by the preset
        /// editor's live preview. Mirrors the codec/bitrate/resolution/audio
        /// resolution of <see cref="BuildSegmentArguments"/>.
        /// The video encoder is resolved through <see cref="ResolveVideoEncoder"/>:
        /// GPU encoders (e.g. h264_nvenc) appear only when <paramref name="useHardware"/>
        /// is set AND a matching GPU encoder is available; otherwise the CPU
        /// encoder (e.g. libx264) is shown, matching real conversion behavior.
        /// </summary>
        public static string BuildPresetPreviewArguments(PresetOption p, HardwareSupport hw, bool useHardware)
        {
            if (p == null) return string.Empty;
            var sb = new StringBuilder();
            sb.Append("ffmpeg -i \"<输入文件>\"");

            string vcodec = ResolveVideoEncoder(p.VideoCodec, useHardware ? hw : null);
            if (string.Equals(p.VideoCodec, "copy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" -c:v copy");
            }
            else if (!string.IsNullOrEmpty(vcodec))
            {
                sb.AppendFormat(" -c:v {0}", vcodec);
                if (!string.IsNullOrEmpty(p.ResolutionValue))
                    sb.AppendFormat(" -s {0}", p.ResolutionValue);
                AppendVideoBitrate(sb, p, vcodec);
                if (!string.IsNullOrEmpty(p.FrameRate))
                    sb.AppendFormat(" -r {0}", p.FrameRate);
            }

            if (string.IsNullOrEmpty(p.AudioCodec))
            {
                sb.Append(" -an");
            }
            else if (string.Equals(p.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(" -c:a copy");
            }
            else
            {
                sb.AppendFormat(" -c:a {0}", p.AudioCodec);
                if (!string.IsNullOrEmpty(p.AudioBitrate))
                    sb.AppendFormat(" -b:a {0}", p.AudioBitrate);
                else
                {
                    string br = DefaultCodecSettings.GetAudioDefaultBitrate(p.AudioCodec);
                    if (!string.IsNullOrEmpty(br)) sb.AppendFormat(" -b:a {0}", br);
                }
                if (!string.IsNullOrEmpty(p.SampleRate))
                    sb.AppendFormat(" -ar {0}", p.SampleRate);
                if (p.Channels > 0)
                    sb.AppendFormat(" -ac {0}", p.Channels);
            }

            sb.Append(" -sn");

            if (!string.IsNullOrWhiteSpace(p.CustomArgs))
                sb.Append(" " + p.CustomArgs.Trim());

            sb.Append(" \"<输出文件>\"");
            return sb.ToString();
        }

        /// <summary>
        /// Run ffmpeg with progress callbacks.
        /// Handles single segment, multi-segment merge, and multi-segment split output.
        /// </summary>
        /// <summary>
        /// 构建字幕烧录滤镜（subtitles 滤镜）。返回滤镜字符串；不烧录返回 null。
        /// 仅支持内嵌字幕轨的烧录；外挂字幕需先转为 ass/srt 临时文件。
        ///
        /// <summary>
        /// 烧录字幕滤镜。为 SRT 字幕生成带样式的临时 ASS 文件，使用 ass= 滤镜
        /// （比 subtitles= + force_style 更可靠，因为 libass 对 SRT 的 force_style 支持不稳定）。
        /// ASS 文件则直接使用 ass= 滤镜保留原样式。
        /// </summary>
        public static string BuildSubtitleBurnFilter(ConversionTask task)
        {
            if (task.SubMode != SubtitleMode.BurnExternal) return null;
            if (task.SelectedSubtitleTrack == null || !task.SelectedSubtitleTrack.IsExternal) return null;

            var st = task.SelectedSubtitleTrack;
            string subPath = !string.IsNullOrEmpty(task.SubtitleSettings?.ExternalSubPath)
                ? task.SubtitleSettings.ExternalSubPath
                : st.FilePath;
            if (string.IsNullOrEmpty(subPath) || !File.Exists(subPath)) return null;

            string ext = Path.GetExtension(subPath).ToLowerInvariant();

            // ASS/SSA 文件直接用 ass= 滤镜，保留原文件中已有的样式定义。
            if (ext == ".ass" || ext == ".ssa")
            {
                string escaped = subPath.Replace("\\", "/").Replace(":", "\\:");
                return "ass='" + escaped + "'";
            }

            // SRT 等文本字幕：生成带样式的临时 ASS 文件，确保字体/大小/位置等设置生效。
            if (task.SubtitleSettings == null) return null;
            try
            {
                string assPath = GenerateStyledAssFromSrt(subPath, task.SubtitleSettings);
                if (string.IsNullOrEmpty(assPath)) return null;
                string escaped = assPath.Replace("\\", "/").Replace(":", "\\:");
                return "ass='" + escaped + "'";
            }
            catch
            {
                // 降级：生成失败时回退到 subtitles + force_style（可能部分生效）。
                string escaped = subPath.Replace("\\", "/").Replace(":", "\\:");
                string forceStyle = task.SubtitleSettings.ToForceStyle();
                if (!string.IsNullOrEmpty(forceStyle))
                    return "subtitles='" + escaped + "':force_style='" + forceStyle + "'";
                return "subtitles='" + escaped + "'";
            }
        }

        /// <summary>
        /// 将 SRT 字幕转为带样式的临时 ASS 文件。样式从 SubtitleSettings 读取。
        /// 返回绝对路径；失败返回 null。
        /// </summary>
        private static string GenerateStyledAssFromSrt(string srtPath, SubtitleSettings settings)
        {
            string srtContent = File.ReadAllText(srtPath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(srtContent)) return null;

            // 解析 SRT 条目
            var entries = ParseSimpleSrt(srtContent);
            if (entries.Count == 0) return null;

            // ASS 头部
            var ass = new StringBuilder();
            ass.AppendLine("[Script Info]");
            ass.AppendLine("ScriptType: v4.00+");
            ass.AppendLine("WrapStyle: 0");
            ass.AppendLine("ScaledBorderAndShadow: yes");
            ass.AppendLine("YCbCr Matrix: TV.709");
            ass.AppendLine("PlayResX: 1920");
            ass.AppendLine("PlayResY: 1080");
            ass.AppendLine();
            ass.AppendLine("[V4+ Styles]");
            ass.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
            ass.Append("Style: Default,");
            ass.Append(string.IsNullOrEmpty(settings.FontName) ? "Arial" : settings.FontName); ass.Append(',');
            ass.Append(settings.FontSize > 0 ? settings.FontSize : 24); ass.Append(',');
            int primaryAlpha = Math.Max(0, Math.Min(255, 255 - settings.Transparency * 255 / 100));
            ass.Append(ArgbToAssStyleColor(settings.FontColorArgb, primaryAlpha)); ass.Append(',');
            ass.Append("&H00000000,");  // SecondaryColour (unused)
            ass.Append(ArgbToAssStyleColor(settings.OutlineColorArgb, 0)); ass.Append(',');
            int backAlpha = settings.BackEnabled ? Math.Max(0, Math.Min(255, 255 - settings.BackAlpha * 255 / 100)) : 0;
            ass.Append(ArgbToAssStyleColor(settings.BackColorArgb, backAlpha)); ass.Append(',');
            ass.Append(settings.Bold ? "-1" : "0"); ass.Append(',');
            ass.Append(settings.Italic ? "-1" : "0"); ass.Append(',');
            ass.Append(settings.Underline ? "-1" : "0"); ass.Append(',');
            ass.Append("0,");   // StrikeOut
            ass.Append("100,100,"); // ScaleX, ScaleY
            ass.Append("0,");   // Spacing
            ass.Append("0,");   // Angle
            ass.Append("1,");   // BorderStyle = 1 (outline + drop shadow)
            ass.Append(settings.OutlineWidth > 0 ? settings.OutlineWidth : 1); ass.Append(',');
            ass.Append("0,");   // Shadow
            ass.Append(settings.Alignment > 0 ? settings.Alignment : 2); ass.Append(',');
            ass.Append("10,10,"); // MarginL, MarginR
            ass.Append(settings.MarginV); ass.Append(',');
            ass.AppendLine("1");// Encoding
            ass.AppendLine();
            ass.AppendLine("[Events]");
            ass.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

            // 事件：每条 SRT 条目转一行 Dialogue
            foreach (var e in entries)
            {
                string text = e.Text.Replace("\r\n", "\\N").Replace("\n", "\\N").Replace("\r", "");
                ass.Append("Dialogue: 0,");
                ass.Append(SecondsToAssTime(e.StartSeconds)); ass.Append(',');
                ass.Append(SecondsToAssTime(e.EndSeconds)); ass.Append(',');
                ass.AppendLine("Default,,0,0,0,," + text);
            }

            // 写入临时文件（与 srt 同目录，同基名 + _styled + 时间戳 + .ass）
            string outDir = Path.GetDirectoryName(srtPath);
            string baseName = Path.GetFileNameWithoutExtension(srtPath);
            string assPath = Path.Combine(
                string.IsNullOrEmpty(outDir) ? Path.GetTempPath() : outDir,
                baseName + "_styled_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".ass");
            File.WriteAllText(assPath, ass.ToString(), Encoding.UTF8);
            return assPath;
        }

        /// <summary>ARGB → ASS 样式颜色 &amp;HAABBGGRR&amp;</summary>
        private static string ArgbToAssStyleColor(int argb, int alpha)
        {
            int a = Math.Max(0, Math.Min(255, alpha));
            int r = (argb >> 16) & 0xFF;
            int g = (argb >> 8) & 0xFF;
            int b = argb & 0xFF;
            return string.Format(CultureInfo.InvariantCulture, "&H{0:X2}{1:X2}{2:X2}{3:X2}&", a, b, g, r);
        }

        private struct SimpleSrtEntry { public double StartSeconds; public double EndSeconds; public string Text; }

        private static List<SimpleSrtEntry> ParseSimpleSrt(string content)
        {
            var entries = new List<SimpleSrtEntry>();
            var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int i = 0;
            while (i < lines.Length)
            {
                // 跳过空行和索引号
                while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
                if (i >= lines.Length) break;
                if (!int.TryParse(lines[i], out _)) { i++; continue; }
                i++;
                // 时间戳行 "00:00:01,000 --> 00:00:04,000"
                if (i >= lines.Length) break;
                string tsLine = lines[i]; i++;
                var parts = tsLine.Split(new[] { "-->" }, StringSplitOptions.None);
                if (parts.Length < 2) continue;
                double start = ParseSrtTimestamp(parts[0]);
                double end = ParseSrtTimestamp(parts[1]);
                // 文本行（直到空行）
                var text = new StringBuilder();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    if (text.Length > 0) text.Append("\\N");
                    text.Append(lines[i]);
                    i++;
                }
                if (start >= 0 && end >= 0)
                    entries.Add(new SimpleSrtEntry { StartSeconds = start, EndSeconds = end, Text = text.ToString() });
            }
            return entries;
        }

        private static double ParseSrtTimestamp(string s)
        {
            // "00:00:01,000" or "00:00:01.000"
            s = (s ?? "").Trim().Replace(',', '.');
            var parts = s.Split(':');
            if (parts.Length != 3) return -1;
            if (!double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double h)) return -1;
            if (!double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double m)) return -1;
            if (!double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double sec)) return -1;
            return h * 3600 + m * 60 + sec;
        }

        private static string SecondsToAssTime(double seconds)
        {
            int h = (int)(seconds / 3600);
            int m = (int)((seconds % 3600) / 60);
            double s = seconds % 60;
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}:{2:00.00}", h, m, s);
        }

        /// <summary>追加 -metadata 参数（标题/作者/年份/备注）。</summary>
        public static void AppendMetadata(StringBuilder sb, ConversionTask task)
        {
            if (!string.IsNullOrEmpty(task.MetaTitle))
                sb.AppendFormat(" -metadata title=\"{0}\"", EscapeMetadata(task.MetaTitle));
            if (!string.IsNullOrEmpty(task.MetaAuthor))
                sb.AppendFormat(" -metadata artist=\"{0}\"", EscapeMetadata(task.MetaAuthor));
            if (!string.IsNullOrEmpty(task.MetaYear))
                sb.AppendFormat(" -metadata date=\"{0}\"", EscapeMetadata(task.MetaYear));
            if (!string.IsNullOrEmpty(task.MetaComment))
                sb.AppendFormat(" -metadata comment=\"{0}\"", EscapeMetadata(task.MetaComment));
        }

        /// <summary>元数据转义：去除双引号和换行。</summary>
        private static string EscapeMetadata(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
        }

        /// <summary>
        /// 构建双通道编码参数。第一遍分析，第二遍正式编码。
        /// 返回两遍的参数列表 + passlogfile 路径；非双通道返回 null。
        /// </summary>
        public static List<string> BuildTwoPassArguments(ConversionTask task, VideoSegment segment, string outputPath)
        {
            if (!task.TwoPass || task.UseStreamCopy) return null;
            string passlogfile = Path.Combine(Path.GetTempPath(), "vc_2pass_" + Guid.NewGuid().ToString("N"));
            var args1 = BuildSegmentArguments(task, segment, outputPath);
            // 第一遍：-pass 1 -an 输出到 null（不编码音频）
            args1 += string.Format(" -pass 1 -passlogfile \"{0}\" -an -f null NUL", passlogfile);
            // 第二遍：-pass 2 正式输出
            var args2 = BuildSegmentArguments(task, segment, outputPath);
            args2 += string.Format(" -pass 2 -passlogfile \"{0}\"", passlogfile);
            return new List<string> { args1, args2, passlogfile };
        }

        public static async Task RunAsync(ConversionTask task,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(FFmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", FFmpegPath);

            bool hasSegments = task.Segments != null && task.Segments.Count > 0;
            bool hasCrop = task.Crop != null || task.Rotation != 0;

            // Simple path: no segments or single segment without crop -> existing single-run behavior.
            if (!hasSegments || (task.Segments.Count == 1 && !hasCrop))
            {
                double duration = task.SourceDurationSeconds;
                VideoSegment seg = null;
                if (hasSegments && task.Segments.Count == 1)
                {
                    seg = task.Segments[0];
                    duration = (seg.EndMs - seg.StartMs) / 1000.0;
                }
                else if (duration > 0)
                {
                    // 无片段的整文件转换：合成全范围片段供双通道参数构建使用。
                    seg = new VideoSegment { StartMs = 0, EndMs = (long)(duration * 1000) };
                }
                // P2: 双通道编码（两遍）。第一遍分析（无进度回传），第二遍正式编码（带进度）。
                // 仅当 task.TwoPass 且非流复制时启用；passlogfile 在临时目录，结束后删除。
                if (task.TwoPass && !task.UseStreamCopy)
                {
                    var twoPass = BuildTwoPassArguments(task, seg, task.OutputPath);
                    if (twoPass != null && twoPass.Count >= 3)
                    {
                        string passlog = twoPass[2];
                        try
                        {
                            await RunSingleAsync(task, twoPass[0], 0, null, cancellationToken);
                            await RunSingleAsync(task, twoPass[1], duration, progress, cancellationToken);
                        }
                        finally
                        {
                            try { if (File.Exists(passlog)) File.Delete(passlog); } catch { }
                            try { if (File.Exists(passlog + "-0.log")) File.Delete(passlog + "-0.log"); } catch { }
                        }
                        return;
                    }
                }
                await RunSingleAsync(task, BuildArguments(task), duration, progress, cancellationToken);
                return;
            }

            if (task.MergeSegments)
            {
                // Use concat demuxer for proper audio-video sync (replaces select filter).
                string concatListPath = Path.Combine(Path.GetTempPath(),
                    "vc_concat_" + Guid.NewGuid().ToString("N") + ".txt");
                try
                {
                    WriteConcatList(task, concatListPath);
                    string args = BuildMergedArguments(task, concatListPath, task.OutputPath);
                    double totalDuration = task.GetEditedDurationSeconds();
                    await RunSingleAsync(task, args, totalDuration, progress, cancellationToken);
                }
                finally
                {
                    try { File.Delete(concatListPath); } catch { }
                }
            }
            else
            {
                // Run ffmpeg once per segment; output files get numeric suffixes.
                var outputs = task.GetOutputPaths();
                int total = outputs.Count;
                for (int i = 0; i < total; i++)
                {
                    var segment = task.Segments[i];
                    string args = BuildSegmentArguments(task, segment, outputs[i]);
                    double segDuration = (segment.EndMs - segment.StartMs) / 1000.0;
                    var segProgress = new Progress<double>(v => progress?.Report((i + v) / total));
                    await RunSingleAsync(task, args, segDuration, segProgress, cancellationToken);
                    if (cancellationToken.IsCancellationRequested) break;
                }
            }
        }

        /// <summary>
        /// 全局并发信号量：限制同时运行的 ffmpeg 进程数，
        /// 避免 I/O 抢占与瞬时资源耗尽（对应 VideoConverterUltimate 的 ParallelTaskSemaphore）。
        /// 探测、转换、合并统一使用它，不再各自 new SemaphoreSlim。
        /// </summary>
        private static readonly SemaphoreSlim _globalConcurrency =
            new SemaphoreSlim(MaxParallelFfmpeg, MaxParallelFfmpeg);

        /// <summary>最大并行 ffmpeg 进程数（默认 4，可在运行时调整）。</summary>
        public static int MaxParallelFfmpeg { get; set; } = 4;

        /// <summary>App 级共享并发信号量，供探测/转换/合并统一调用。</summary>
        public static SemaphoreSlim GlobalConcurrency => _globalConcurrency;

        /// <summary>
        /// 在全局并发信号量约束下运行一个 ffmpeg/ffprobe 子任务。
        /// 所有直接启动 ffmpeg/ffprobe 进程的方法（探测、取帧、缩略图、关键帧、版本检测等）
        /// 都应经此包装，确保全局并发数统一受 MaxParallelFfmpeg 限制，
        /// 而非仅限转换/合并。避免各自 new 信号量导致的实际并发失控。
        /// </summary>
        private static async Task<T> RunUnderGlobalConcurrencyAsync<T>(Func<Task<T>> action)
        {
            await _globalConcurrency.WaitAsync().ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _globalConcurrency.Release();
            }
        }

        /// <summary>
        /// 运行一条 ffmpeg 转换/合并命令的核心实现（受全局并发信号量约束）。
        /// 稳定性设计（对齐参考引擎 dei.cs）：
        /// 1) -nostdin：进程启动即关闭 stdin 读取，避免 ffmpeg 等待 stdin 而卡死（一类"进度 0%/CPU 0%"的根因）。
        /// 2) 事件驱动 stderr：BeginErrorReadLine + ErrorDataReceived，解析异常立即 Kill，绝不后台挂死。
        /// 3) 受全局并发信号量约束，避免同时拉起过多进程抢占 I/O。
        /// </summary>
        private static async Task RunFfmpegCoreAsync(string args, double duration,
            IProgress<double> progress, CancellationToken cancellationToken,
            Action<int> onProcessId = null)
        {
            await _globalConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string tag = ProcessGuard.MakeTag(out string tempFile);
                var psi = new ProcessStartInfo
                {
                    FileName = FFmpegPath,
                    Arguments = "-nostdin " + args + " " + tag,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardErrorEncoding = Encoding.UTF8
                };

                var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);
                var stderrCapture = new StringBuilder();

                using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
                {
                    var tcs = new TaskCompletionSource<object>();
                    proc.Exited += (s, e) => tcs.TrySetResult(null);
                    proc.ErrorDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return; // 流结束标记
                        try
                        {
                            stderrCapture.AppendLine(e.Data);
                            if (cancellationToken.IsCancellationRequested)
                            {
                                try { proc.Kill(); } catch { }
                                return;
                            }
                            var m = timeRegex.Match(e.Data);
                            if (m.Success && duration > 0)
                            {
                                double t = ParseTime(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                                progress?.Report(Math.Min(1.0, t / duration));
                            }
                        }
                        catch
                        {
                            // 参考 dei.cs：解析异常立即杀进程，避免后台挂死
                            try { proc.Kill(); } catch { }
                        }
                    };

                    proc.Start();
                    ProcessGuard.Register(proc, tempFile);
                    onProcessId?.Invoke(proc.Id);

                    proc.BeginErrorReadLine(); // 事件驱动读取 stderr（替代手动 ReadLineAsync 循环）

                    try { await tcs.Task.ConfigureAwait(false); }
                    finally
                    {
                        // 给 ErrorDataReceived 事件一点时间 flush 最后几行
                        await Task.Delay(150).ConfigureAwait(false);
                        try { proc.CancelErrorRead(); } catch { }
                        onProcessId?.Invoke(-1); // 进程已退出：清空 PID
                    }

                    if (proc.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
                    {
                        string stderr = stderrCapture.Length > 0
                            ? stderrCapture.ToString().TrimEnd('\r', '\n')
                            : "(无 ffmpeg 诊断输出)";
                        throw new InvalidOperationException(
                            "ffmpeg exited with code " + proc.ExitCode + "\n--- ffmpeg stderr ---\n" + stderr);
                    }
                }
            }
            finally
            {
                _globalConcurrency.Release();
            }
        }

        private static async Task RunSingleAsync(ConversionTask task, string args,
            double duration, IProgress<double> progress, CancellationToken cancellationToken)
        {
            await RunFfmpegCoreAsync(args, duration, progress, cancellationToken,
                id => task.CurrentProcessId = id);
        }

        /// <summary>
        /// 判断首次硬件编码失败后是否降级为 CPU 编码器，并就地修改 task 使其下次构建使用 CPU 编码器。
        /// 返回 true 表示已应用降级。集中实现「查表 + 边界判断」，避免在调用处散落 if-else。
        /// </summary>
        public static bool ApplyHardwareFallback(ConversionTask task, HardwareSupport hw, bool hwChecked)
        {
            if (task.UseStreamCopy) return false;
            if (string.IsNullOrEmpty(task.TargetVideoEncoder)) return false;
            if (!IsHardwareEncoder(task.TargetVideoEncoder)) return false;
            if (!hwChecked) return false;
            string cpuEnc = HardCodecSettings.GetCpuEncoder(task.Preset?.VideoCodecLabel)
                ?? ResolveTargetVideoEncoder(task, hw, false);
            if (string.IsNullOrEmpty(cpuEnc) || IsHardwareEncoder(cpuEnc)) return false;
            task.HardwareEncoder = null;     // 关键：清空后重新构建将使用 CPU 编码器
            task.TargetVideoEncoder = cpuEnc;
            return true;
        }

        private static async Task<double> GetDurationAsync(string filePath)
        {
            try
            {
                var info = await ProbeAsync(filePath);
                if (info.DurationSeconds > 0) return info.DurationSeconds;
            }
            catch { }
            return 0;
        }

        private static double ParseTime(string hh, string mm, string ss)
        {
            if (double.TryParse(hh, out double h) &&
                double.TryParse(mm, out double m) &&
                double.TryParse(ss, NumberStyles.Any, CultureInfo.InvariantCulture, out double s))
                return h * 3600 + m * 60 + s;
            return 0;
        }

        /// <summary>
        /// Parse an ffprobe rational such as "30000/1001" or "25" into a double.
        /// </summary>
        private static double ParseRational(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            value = value.Trim();
            int slash = value.IndexOf('/');
            if (slash < 0)
            {
                if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)) return v;
                return 0;
            }
            string a = value.Substring(0, slash);
            string b = value.Substring(slash + 1);
            if (double.TryParse(a, NumberStyles.Any, CultureInfo.InvariantCulture, out double num) &&
                double.TryParse(b, NumberStyles.Any, CultureInfo.InvariantCulture, out double den) && den != 0)
                return num / den;
            return 0;
        }

        /// <summary>
        /// 查找与视频文件同目录、同基名的常见外挂字幕文件。
        /// </summary>
        public static List<SubtitleTrackInfo> FindExternalSubtitles(string videoPath)
        {
            var list = new List<SubtitleTrackInfo>();
            if (string.IsNullOrWhiteSpace(videoPath)) return list;
            string dir = Path.GetDirectoryName(videoPath);
            string baseName = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return list;

            // 按常见程度排序；.sub 通常需 .idx 但这里仍列出，交给 ffmpeg 处理。
            string[] exts = { ".srt", ".ass", ".ssa", ".vtt", ".smi", ".sub", ".idx" };
            foreach (string ext in exts)
            {
                string path = Path.Combine(dir, baseName + ext);
                if (!File.Exists(path)) continue;
                string codec = ext.TrimStart('.').ToLowerInvariant();
                if (codec == "sub") codec = "dvd_subtitle";
                if (codec == "idx") codec = "dvd_subtitle";
                if (codec == "smi") codec = "sami";
                list.Add(new SubtitleTrackInfo
                {
                    Index = 0,
                    Codec = codec,
                    Language = GuessSubtitleLanguage(path),
                    Title = Path.GetFileName(path),
                    IsExternal = true,
                    FilePath = path
                });
            }
            return list;
        }

        private static string GuessSubtitleLanguage(string path)
        {
            // 常见命名：video.en.srt、video.zh-cn.srt
            string name = Path.GetFileNameWithoutExtension(path);
            string videoBase = Path.GetFileNameWithoutExtension(name);
            string suffix = name.Substring(Math.Min(videoBase.Length, name.Length)).Trim('.');
            if (!string.IsNullOrEmpty(suffix) && suffix.Length <= 5)
                return suffix.ToLowerInvariant();
            return null;
        }

        public static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return string.Format("{0:0.00} KB", bytes / 1024.0);
            if (bytes < 1024L * 1024 * 1024) return string.Format("{0:0.00} MB", bytes / (1024.0 * 1024));
            return string.Format("{0:0.00} GB", bytes / (1024.0 * 1024 * 1024));
        }

        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0 || double.IsNaN(seconds)) return "00:00:00";
            var ts = TimeSpan.FromSeconds(seconds);
            return string.Format("{0:D2}:{1:D2}:{2:D2}", ts.Hours, ts.Minutes, ts.Seconds);
        }

        #region 合并所有文件 (Merge All)

        /// <summary>将音频 codec_name 映射为 ffmpeg 编码器名（如 mp3→libmp3lame）。</summary>
        public static string ResolveAudioEncoder(string codecName)
        {
            if (string.IsNullOrEmpty(codecName)) return null;
            string c = codecName.ToLowerInvariant().Trim();
            switch (c)
            {
                case "mp3":    return "libmp3lame";
                case "aac":    return "aac";
                case "ac3":    return "ac3";
                case "eac3":   return "eac3";
                case "vorbis": return "libvorbis";
                case "opus":   return "libopus";
                case "flac":   return "flac";
                case "alac":   return "alac";
                case "wmav1":
                case "wmav2":  return c;
                case "pcm_s16le":
                case "pcm_s24le":
                case "pcm_s32le": return c;
                default:       return c;
            }
        }

        /// <summary>
        /// 运行一条 ffmpeg 命令（不绑定 ConversionTask），用于合并所有文件的
        /// 逐文件预处理与最终 concat。进度按 duration 归一化回传。
        /// </summary>
        public static async Task RunCommandAsync(string args, double duration,
            IProgress<double> progress, CancellationToken cancellationToken)
        {
            if (!File.Exists(FFmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", FFmpegPath);

            await RunFfmpegCoreAsync(args, duration, progress, cancellationToken);
        }

        /// <summary>
        /// 为合并所有文件构建单文件预处理参数。
        /// videoCopy=true 时视频流 -c:v copy，否则按统一参数重编码。
        /// audioCopy=true 时音频流 -c:a copy，否则按统一参数重编码。
        /// hasAudio=false 时输出无音频流 (-an)。
        /// needsFastStart=true 时追加 -movflags +faststart（MP4/MOV 等 ISO 封装）。
        /// </summary>
        public static string BuildMergeAllFileArguments(
            string inputPath, string outputPath,
            string videoEncoder, string pixelFormat, string resolution, string frameRate,
            bool videoCopy, string videoBitrate,
            string audioEncoder, int sampleRate, int channels, bool audioCopy, string audioBitrate,
            bool hasAudio, bool needsFastStart)
        {
            var sb = new StringBuilder();
            sb.Append("-y -hide_banner -i \"");
            sb.Append(inputPath);
            sb.Append("\" -map 0:v:0");

            if (hasAudio)
                sb.Append(" -map 0:a:0");

            // Video stream
            if (videoCopy)
            {
                sb.Append(" -c:v copy");
            }
            else
            {
                if (!string.IsNullOrEmpty(videoEncoder))
                    sb.AppendFormat(" -c:v {0}", videoEncoder);
                if (!string.IsNullOrEmpty(resolution))
                    sb.AppendFormat(" -s {0}", resolution);
                if (!string.IsNullOrEmpty(frameRate))
                    sb.AppendFormat(" -r {0}", frameRate);
                if (!string.IsNullOrEmpty(pixelFormat))
                    sb.AppendFormat(" -pix_fmt {0}", pixelFormat);
                if (!string.IsNullOrEmpty(videoBitrate))
                    sb.AppendFormat(" -b:v {0}", videoBitrate);
            }

            // Audio stream
            if (!hasAudio)
            {
                sb.Append(" -an");
            }
            else if (audioCopy)
            {
                sb.Append(" -c:a copy");
            }
            else
            {
                if (!string.IsNullOrEmpty(audioEncoder))
                    sb.AppendFormat(" -c:a {0}", audioEncoder);
                if (sampleRate > 0)
                    sb.AppendFormat(" -ar {0}", sampleRate);
                if (channels > 0)
                    sb.AppendFormat(" -ac {0}", channels);
                if (!string.IsNullOrEmpty(audioBitrate))
                    sb.AppendFormat(" -b:a {0}", audioBitrate);
            }

            if (needsFastStart)
                sb.Append(" -movflags +faststart");

            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        /// <summary>
        /// 写入简单的 concat 列表（整文件合并，无 inpoint/outpoint）。
        /// 路径按 ffmpeg concat demuxer 要求转为正斜杠，单引号转义。
        /// files 顺序即为合并顺序。
        /// </summary>
        public static void WriteSimpleConcatList(List<string> files, string listPath)
        {
            var sb = new StringBuilder();
            foreach (string f in files)
            {
                string path = f.Replace('\\', '/');
                string escaped = path.Replace("'", "'\''");
                sb.AppendLine("file '" + escaped + "'");
            }
            File.WriteAllText(listPath, sb.ToString(), new UTF8Encoding(false));
        }

        /// <summary>
        /// 判断输出容器是否需要 -movflags +faststart：把 moov 原子移到文件头部，
        /// 让播放器无需读完整个文件即可建立 seek 索引，修复合并后拖动进度条卡死的问题。
        /// 适用于 MP4/MOV/M4V/M4A/M4B/3GP/ISMV/F4V 等 ISO 媒体封装；MKV/FLV/AVI/TS 等不适用。
        /// </summary>
        public static bool NeedsFastStart(string outputPath)
        {
            if (string.IsNullOrEmpty(outputPath)) return false;
            string ext = Path.GetExtension(outputPath).ToLowerInvariant();
            return ext == ".mp4" || ext == ".mov" || ext == ".m4v" ||
                   ext == ".m4a" || ext == ".m4b" || ext == ".3gp" ||
                   ext == ".ismv" || ext == ".f4v";
        }

        /// <summary>构建 concat demuxer + -c copy 的最终合并参数。</summary>
        public static string BuildConcatCopyArguments(string concatListPath, string outputPath)
        {
            var sb = new StringBuilder("-y -hide_banner -f concat -safe 0 -i \"");
            sb.Append(concatListPath);
            sb.Append("\" -c copy");
            if (NeedsFastStart(outputPath))
                sb.Append(" -movflags +faststart");
            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        /// <summary>
        /// 构建 concat demuxer + -c copy + 注入章节元数据的最终合并参数。
        /// metadataPath 指向 FFMETADATA1 格式的临时文件；为 null 时退化为普通 -c copy。
        /// 对 MP4/MOV/M4V 等 ISO 封装自动追加 -movflags +faststart 以便拖动进度条。
        /// </summary>
        public static string BuildConcatCopyWithChaptersArguments(string concatListPath, string metadataPath, string outputPath)
        {
            if (string.IsNullOrEmpty(metadataPath))
                return BuildConcatCopyArguments(concatListPath, outputPath);
            var sb = new StringBuilder("-y -hide_banner -f concat -safe 0 -i \"");
            sb.Append(concatListPath);
            sb.Append("\" -i \"");
            sb.Append(metadataPath);
            sb.Append("\" -map 0 -map_metadata 1 -codec copy");
            if (NeedsFastStart(outputPath))
                sb.Append(" -movflags +faststart");
            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        #endregion
    }

    public class MediaInfo
    {
        public string FilePath { get; set; }
        public string VideoCodec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double DurationSeconds { get; set; }
        public double FrameRate { get; set; }
        public string PixelFormat { get; set; }
        public double NominalFrameRate { get; set; }
        public long SizeBytes { get; set; }
        public List<AudioTrackInfo> AudioTracks { get; set; } = new List<AudioTrackInfo>();
        public List<SubtitleTrackInfo> SubtitleTracks { get; set; } = new List<SubtitleTrackInfo>();
        public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
    }
}
