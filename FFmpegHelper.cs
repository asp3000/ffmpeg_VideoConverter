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
        public static string ResolveVideoEncoder(string fourCC, HardwareSupport hw)
        {
            if (string.IsNullOrEmpty(fourCC) || string.Equals(fourCC, "copy", StringComparison.OrdinalIgnoreCase))
                return fourCC;

            string cpu = PresetDataStore.GetCpuEncoder(fourCC);
            if (hw != null && hw.Any && HardwareEncoderMap.TryGetValue(cpu ?? string.Empty, out var vendors))
            {
                if (hw.Nvidia && vendors.TryGetValue(HardwareVendor.Nvidia, out var n)) return n;
                if (hw.Intel && vendors.TryGetValue(HardwareVendor.Intel, out var i)) return i;
                if (hw.Amd && vendors.TryGetValue(HardwareVendor.Amd, out var a)) return a;
            }
            return cpu;
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

            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = "-hide_banner -encoders " + tag,
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
        }

        #endregion

        /// <summary>
        /// Run "ffmpeg -version" and return the version token after "ffmpeg version",
        /// e.g. "7.1.1" or "N-116000-gabc123". Returns null when ffmpeg is missing
        /// or cannot be run.
        /// </summary>
        public static async Task<string> GetInstalledVersionAsync()
        {
            try
            {
                if (!File.Exists(FFmpegPath)) return null;
                var psi = new ProcessStartInfo
                {
                    FileName = FFmpegPath,
                    Arguments = "-version",
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

            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFprobePath,
                Arguments = string.Format(
                    "-v error -show_streams -show_entries format=size,duration " +
                    "-of json \"{0}\" {1}",
                    filePath, tag),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            string output = null;
            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var tcs = new TaskCompletionSource<object>();
                proc.Exited += (s, e) => tcs.TrySetResult(null);
                proc.Start();
                ProcessGuard.Register(proc, tempFile);
                output = await Task.Run(() => proc.StandardOutput.ReadToEnd()).ConfigureAwait(false);
                await tcs.Task.ConfigureAwait(false);
            }

            return ParseFfprobeJson(output, filePath);
        }

        /// <summary>
        /// Minimal JSON parser for ffprobe -of json output. Avoids adding an
        /// external JSON dependency to the net472 project.
        /// </summary>
        private static MediaInfo ParseFfprobeJson(string json, string filePath)
        {
            var info = new MediaInfo { FilePath = filePath };
            if (string.IsNullOrWhiteSpace(json)) return info;

            // Format block.
            var formatMatch = Regex.Match(json, @"""format""\s*:\s*\{([\s\S]*?)\}\s*(?:,|\])");
            if (formatMatch.Success)
            {
                string fmt = formatMatch.Groups[1].Value;
                string dur = ExtractJsonValue(fmt, "duration");
                if (!string.IsNullOrEmpty(dur) && double.TryParse(dur, NumberStyles.Any, CultureInfo.InvariantCulture, out double fd))
                    info.DurationSeconds = fd;
                string size = ExtractJsonValue(fmt, "size");
                if (!string.IsNullOrEmpty(size) && long.TryParse(size, out long sz))
                    info.SizeBytes = sz;
            }

            // Extract each stream object.
            var streamMatches = Regex.Matches(json, @"\{\s*""index""[\s\S]*?""codec_type""\s*:\s*""([^""]*)""[\s\S]*?\}");
            if (streamMatches.Count == 0)
            {
                // Fallback: looser match for objects inside "streams": [...]
                var streamsSection = Regex.Match(json, @"""streams""\s*:\s*(\[[\s\S]*?\])");
                if (streamsSection.Success)
                    streamMatches = Regex.Matches(streamsSection.Groups[1].Value, @"\{([\s\S]*?)\}");
            }

            foreach (Match m in streamMatches)
            {
                string body = m.Groups[m.Groups.Count - 1].Value;
                string codecType = ExtractJsonValue(body, "codec_type");
                if (codecType == null) codecType = ExtractJsonValue(body, "codec_type");

                if (codecType == "video")
                {
                    info.VideoCodec = ExtractJsonValue(body, "codec_name");
                    if (int.TryParse(ExtractJsonValue(body, "width"), out int w)) info.Width = w;
                    if (int.TryParse(ExtractJsonValue(body, "height"), out int h)) info.Height = h;
                    string fr = ExtractJsonValue(body, "r_frame_rate");
                    if (!string.IsNullOrEmpty(fr)) info.FrameRate = ParseRational(fr);
                    if (info.DurationSeconds <= 0)
                    {
                        if (double.TryParse(ExtractJsonValue(body, "duration"), NumberStyles.Any, CultureInfo.InvariantCulture, out double vd))
                            info.DurationSeconds = vd;
                    }
                }
                else if (codecType == "audio")
                {
                    var at = new AudioTrackInfo
                    {
                        Index = info.AudioTracks.Count,
                        Codec = ExtractJsonValue(body, "codec_name"),
                        Language = ExtractTag(body, "language"),
                        Title = ExtractTag(body, "title"),
                    };
                    if (int.TryParse(ExtractJsonValue(body, "sample_rate"), out int sr)) at.SampleRate = sr;
                    if (int.TryParse(ExtractJsonValue(body, "channels"), out int ch)) at.Channels = ch;
                    at.BitRate = FormatBitRate(ExtractJsonValue(body, "bit_rate"));
                    info.AudioTracks.Add(at);
                }
                else if (codecType == "subtitle")
                {
                    var st = new SubtitleTrackInfo
                    {
                        Index = info.SubtitleTracks.Count,
                        Codec = ExtractJsonValue(body, "codec_name"),
                        Language = ExtractTag(body, "language"),
                        Title = ExtractTag(body, "title"),
                    };
                    info.SubtitleTracks.Add(st);
                }
            }

            return info;
        }

        private static string ExtractJsonValue(string json, string key)
        {
            var m = Regex.Match(json, @"""" + Regex.Escape(key) + @"""\s*:\s*""([^""\n\r]*)""");
            if (m.Success) return m.Groups[1].Value;
            var mn = Regex.Match(json, @"""" + Regex.Escape(key) + @"""\s*:\s*([\d.]+)(?:,|\s|\}|\n)");
            return mn.Success ? mn.Groups[1].Value : null;
        }

        private static string ExtractTag(string json, string tagName)
        {
            var tagsMatch = Regex.Match(json, @"""tags""\s*:\s*\{([\s\S]*?)\}");
            if (!tagsMatch.Success) return null;
            return ExtractJsonValue(tagsMatch.Groups[1].Value, tagName);
        }

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

            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    "-ss {0:0.000} -i \"{1}\" -vframes 1 -s {2}x{3} -f image2pipe -vcodec png - {4}",
                    ms / 1000.0, filePath, width, height, tag),
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
                await proc.StandardOutput.BaseStream.CopyToAsync(msStream).ConfigureAwait(false);
                await tcs.Task.ConfigureAwait(false);

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

            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFprobePath,
                Arguments = string.Format(
                    "-v error -select_streams v:0 -skip_frame nokey -show_entries frame=pts_time -of csv=p=0 \"{0}\" {1}",
                    filePath, tag),
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

                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string t = line.Trim();
                    if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out double sec))
                        list.Add((long)(sec * 1000));
                }
            }
            list.Sort();
            return list;
        }

        /// <summary>
        /// Extract a PNG thumbnail at the 1-second mark.
        /// </summary>
        public static async Task<Image> GetThumbnailAsync(string filePath, int width, int height)
        {
            if (!File.Exists(FFmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", FFmpegPath);

            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = string.Format(
                    "-ss 00:00:01 -i \"{0}\" -vframes 1 -s {1}x{2} -f image2pipe -vcodec png - {3}",
                    filePath, width, height, tag),
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

                var ms = new MemoryStream();
                await proc.StandardOutput.BaseStream.CopyToAsync(ms).ConfigureAwait(false);
                await tcs.Task.ConfigureAwait(false);

                if (ms.Length == 0) return null;
                ms.Position = 0;

                // Copy into a Bitmap so the underlying stream can be disposed.
                using (var temp = Image.FromStream(ms))
                    return new Bitmap(temp);
            }
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

        /// <summary>质量控制参数规范：参数名 + 取值范围 + 推荐值。</summary>
        public class QualitySpec
        {
            public string Param;
            public int Min;
            public int Max;
            public int Recommended;

            public override string ToString()
            {
                return string.Format("{0}~{1}，推荐 {2}", Min, Max, Recommended);
            }
        }

        /// <summary>
        /// 按实际 ffmpeg 编码器名返回质量控制参数规范（-crf / -qp / -cq / -global_quality / -q:v）。
        /// 前缀匹配保证 CPU/GPU 解析后的编码器（如 libx264、h264_nvenc、hevc_qsv）都能命中；
        /// 不支持的编码器返回 null（界面不提供质量控制选项）。
        /// </summary>
        public static QualitySpec GetQualitySpec(string encoder)
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
        ///   VBR     → -b:v X
        ///   Quality → -(crf|qp|cq|global_quality|q:v) V，可选 -maxrate M -bufsize 2M（受限质量控制）
        ///   Auto    → 兼容旧行为：有码率就 -b:v X
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
                        sb.AppendFormat(" -b:v {0}", bitrate);
                    break;
                case BitrateMode.Quality:
                    var spec = GetQualitySpec(encoder);
                    if (spec != null && p.QualityValue > 0)
                    {
                        sb.AppendFormat(" {0} {1}", spec.Param, p.QualityValue);
                        if (!string.IsNullOrEmpty(p.QualityMaxRate))
                        {
                            sb.AppendFormat(" -maxrate {0}", p.QualityMaxRate);
                            sb.AppendFormat(" -bufsize {0}", DoubleBitrate(p.QualityMaxRate));
                        }
                    }
                    break;
                default: // Auto
                    if (!string.IsNullOrEmpty(bitrate))
                        sb.AppendFormat(" -b:v {0}", bitrate);
                    break;
            }
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

            sb.AppendFormat(" -i \"{0}\"", task.InputPath);

            if (durationSec > 0 && durationSec < task.SourceDurationSeconds - 0.05)
                sb.AppendFormat(CultureInfo.InvariantCulture, " -t {0:0.000}", durationSec);

            // Stream mapping for selected tracks.
            sb.Append(" -map 0:v:0");
            if (task.SelectedAudioTrack != null)
                sb.AppendFormat(" -map 0:a:{0}", task.SelectedAudioTrack.Index);
            else
                sb.Append(" -an");

            // High-speed mode: stream copy (only valid for matching containers).
            // Cannot stream-copy when crop/rotation is requested.
            bool hasVideoFilter = task.Crop != null || task.Rotation != 0;
            if (task.UseStreamCopy && !hasVideoFilter)
            {
                sb.Append(" -c copy");
                sb.AppendFormat(" \"{0}\"", outputPath);
                return sb.ToString();
            }

            var p = task.Preset;
            if (p != null)
            {
                // Video filter chain (crop / rotate / scale).
                var vfParts = new List<string>();
                if (task.Crop != null)
                {
                    var c = task.Crop;
                    vfParts.Add(string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}:{2}:{3}",
                        c.Width, c.Height, c.X, c.Y));
                }
                switch (task.Rotation)
                {
                    case 1: vfParts.Add("transpose=1"); break; // 90° clockwise
                    case 2: vfParts.Add("transpose=2"); break; // 90° counter-clockwise
                    case 3: vfParts.Add("transpose=1,transpose=1"); break; // 180°
                    case 4: vfParts.Add("hflip"); break;
                    case 5: vfParts.Add("vflip"); break;
                }
                if (!string.IsNullOrEmpty(p.ResolutionValue))
                    vfParts.Add(string.Format("scale={0}", p.ResolutionValue.Replace("x", ":")));

                // Video
                string vcodec = !string.IsNullOrEmpty(task.HardwareEncoder)
                    ? task.HardwareEncoder
                    : PresetDataStore.GetCpuEncoder(p.VideoCodec);
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
                    if (!string.IsNullOrEmpty(p.AudioBitrate))
                        sb.AppendFormat(" -b:a {0}", p.AudioBitrate);
                }

                // Subtitle: simplified to drop subtitles unless one is selected.
                if (task.SelectedSubtitleTrack != null)
                {
                    sb.AppendFormat(" -map 0:s:{0} -c:s copy", task.SelectedSubtitleTrack.Index);
                }
                else
                {
                    sb.Append(" -sn");
                }
            }

            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        /// <summary>
        /// Build ffmpeg arguments that merge multiple segments into one output file using concat demuxer.
        /// This creates a temporary concat list file; caller is responsible for deleting it after the run.
        /// </summary>
        public static string BuildMergedArguments(ConversionTask task, string concatListPath, string outputPath)
        {
            var sb = new StringBuilder();
            sb.Append(" -y -f concat -safe 0 -i \"");
            sb.Append(concatListPath);
            sb.Append("\"");

            var p = task.Preset;
            if (p != null)
            {
                string vcodec = !string.IsNullOrEmpty(task.HardwareEncoder)
                    ? task.HardwareEncoder
                    : PresetDataStore.GetCpuEncoder(p.VideoCodec);
                if (string.Equals(p.VideoCodec, "copy", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase))
                {
                    sb.Append(" -c:v copy");
                }
                else
                {
                    sb.AppendFormat(" -c:v {0}", vcodec);
                    if (!string.IsNullOrEmpty(p.ResolutionValue))
                        sb.AppendFormat(" -s {0}", p.ResolutionValue);
                    if (!string.IsNullOrEmpty(p.VideoBitrate))
                        sb.AppendFormat(" -b:v {0}", p.VideoBitrate);
                    if (!string.IsNullOrEmpty(p.FrameRate))
                        sb.AppendFormat(" -r {0}", p.FrameRate);
                }

                if (task.SelectedAudioTrack == null)
                    sb.Append(" -an");
                else if (string.Equals(p.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" -c:a copy");
                else
                {
                    sb.AppendFormat(" -c:a {0}", p.AudioCodec);
                    if (!string.IsNullOrEmpty(p.AudioBitrate))
                        sb.AppendFormat(" -b:a {0}", p.AudioBitrate);
                }

                if (task.SelectedSubtitleTrack != null)
                    sb.AppendFormat(" -map 0:s:{0} -c:s copy", task.SelectedSubtitleTrack.Index);
                else
                    sb.Append(" -sn");
            }

            // 自定义参数（高级）：直接附加到命令行末尾。#65
            if (p != null && !string.IsNullOrWhiteSpace(p.CustomArgs))
                sb.Append(" " + p.CustomArgs.Trim());

            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
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
                if (hasSegments && task.Segments.Count == 1)
                    duration = (task.Segments[0].EndMs - task.Segments[0].StartMs) / 1000.0;
                await RunSingleAsync(task, BuildArguments(task), duration, progress, cancellationToken);
                return;
            }

            if (task.MergeSegments)
            {
                // One ffmpeg run with select filters covering all retained segments.
                string args = BuildMergeFilterArguments(task, task.OutputPath);
                double totalDuration = task.GetEditedDurationSeconds();
                await RunSingleAsync(task, args, totalDuration, progress, cancellationToken);
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
        /// Build a single-run ffmpeg argument that selects and concatenates all retained segments in memory.
        /// </summary>
        private static string BuildMergeFilterArguments(ConversionTask task, string outputPath)
        {
            var sb = new StringBuilder();
            sb.Append(" -y");
            sb.AppendFormat(" -i \"{0}\"", task.InputPath);

            // Stream mapping.
            sb.Append(" -map 0:v:0");
            if (task.SelectedAudioTrack != null)
                sb.AppendFormat(" -map 0:a:{0}", task.SelectedAudioTrack.Index);
            else
                sb.Append(" -an");

            // Video filter: crop/rotate/scale + select segments + reset timestamps.
            var vfParts = new List<string>();
            if (task.Crop != null)
            {
                var c = task.Crop;
                vfParts.Add(string.Format(CultureInfo.InvariantCulture, "crop={0}:{1}:{2}:{3}",
                    c.Width, c.Height, c.X, c.Y));
            }
            switch (task.Rotation)
            {
                case 1: vfParts.Add("transpose=1"); break;
                case 2: vfParts.Add("transpose=2"); break;
                case 3: vfParts.Add("transpose=1,transpose=1"); break;
                case 4: vfParts.Add("hflip"); break;
                case 5: vfParts.Add("vflip"); break;
            }

            var selectExpr = new StringBuilder();
            for (int i = 0; i < task.Segments.Count; i++)
            {
                var seg = task.Segments[i];
                if (i > 0) selectExpr.Append("+");
                selectExpr.AppendFormat(CultureInfo.InvariantCulture, "between(t,{0:0.000},{1:0.000})",
                    seg.StartMs / 1000.0, seg.EndMs / 1000.0);
            }
            vfParts.Add(string.Format("select='{0}'", selectExpr));
            vfParts.Add("setpts=N/FRAME_RATE/TB");

            var p = task.Preset;
            if (p != null)
            {
                string vcodec = !string.IsNullOrEmpty(task.HardwareEncoder)
                    ? task.HardwareEncoder
                    : PresetDataStore.GetCpuEncoder(p.VideoCodec);
                // Merging segments requires re-encoding; ignore "copy" requests.
                bool forceVideoEncode = task.Segments != null && task.Segments.Count > 0;
                if (!forceVideoEncode &&
                    (string.Equals(p.VideoCodec, "copy", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase)))
                {
                    sb.Append(" -c:v copy");
                }
                else
                {
                    if (string.Equals(vcodec, "copy", StringComparison.OrdinalIgnoreCase))
                        vcodec = "libx264"; // safe fallback for segmented merge
                    sb.AppendFormat(" -c:v {0}", vcodec);
                    if (!string.IsNullOrEmpty(p.ResolutionValue))
                        vfParts.Add(string.Format("scale={0}", p.ResolutionValue.Replace("x", ":")));
                    sb.AppendFormat(" -vf \"{0}\"", string.Join(",", vfParts));
                    if (!string.IsNullOrEmpty(p.VideoBitrate))
                        sb.AppendFormat(" -b:v {0}", p.VideoBitrate);
                    if (!string.IsNullOrEmpty(p.FrameRate))
                        sb.AppendFormat(" -r {0}", p.FrameRate);
                }

                bool forceAudioEncode = task.Segments != null && task.Segments.Count > 0;
                if (task.SelectedAudioTrack == null)
                    sb.Append(" -an");
                else if (!forceAudioEncode && string.Equals(p.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase))
                    sb.Append(" -c:a copy");
                else
                {
                    string acodec = forceAudioEncode && string.Equals(p.AudioCodec, "copy", StringComparison.OrdinalIgnoreCase)
                        ? "aac" : p.AudioCodec;
                    sb.AppendFormat(" -c:a {0}", acodec);
                    // Audio select + reset timestamps must match video segments.
                    sb.AppendFormat(" -af \"aselect='{0}',asetpts=N/SR/TB\"", selectExpr);
                    if (!string.IsNullOrEmpty(p.AudioBitrate))
                        sb.AppendFormat(" -b:a {0}", p.AudioBitrate);
                }

                if (task.SelectedSubtitleTrack != null)
                    sb.AppendFormat(" -map 0:s:{0} -c:s copy", task.SelectedSubtitleTrack.Index);
                else
                    sb.Append(" -sn");
            }

            // 自定义参数（高级）：直接附加到命令行末尾。#65
            if (p != null && !string.IsNullOrWhiteSpace(p.CustomArgs))
                sb.Append(" " + p.CustomArgs.Trim());

            sb.AppendFormat(" \"{0}\"", outputPath);
            return sb.ToString();
        }

        private static async Task RunSingleAsync(ConversionTask task, string args,
            double duration, IProgress<double> progress, CancellationToken cancellationToken)
        {
            string tag = ProcessGuard.MakeTag(out string tempFile);
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = args + " " + tag,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8
            };

            var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var tcs = new TaskCompletionSource<object>();
                proc.Exited += (s, e) => tcs.TrySetResult(null);
                proc.Start();
                ProcessGuard.Register(proc, tempFile);

                var stderrReader = Task.Run(async () =>
                {
                    string line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            try { proc.Kill(); } catch { }
                            break;
                        }

                        var m = timeRegex.Match(line);
                        if (m.Success && duration > 0)
                        {
                            double t = ParseTime(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                            progress?.Report(Math.Min(1.0, t / duration));
                        }
                    }
                }, cancellationToken);

                try { await tcs.Task.ConfigureAwait(false); }
                finally
                {
                    try { await Task.WhenAny(stderrReader, Task.Delay(1000)); } catch { }
                }

                if (proc.ExitCode != 0 && !cancellationToken.IsCancellationRequested)
                    throw new InvalidOperationException("ffmpeg exited with code " + proc.ExitCode);
            }
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
    }

    public class MediaInfo
    {
        public string FilePath { get; set; }
        public string VideoCodec { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public double DurationSeconds { get; set; }
        public double FrameRate { get; set; }
        public long SizeBytes { get; set; }
        public List<AudioTrackInfo> AudioTracks { get; set; } = new List<AudioTrackInfo>();
        public List<SubtitleTrackInfo> SubtitleTracks { get; set; } = new List<SubtitleTrackInfo>();
    }
}
