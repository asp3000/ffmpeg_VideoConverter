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

                string output = await Task.Run(() => proc.StandardOutput.ReadToEnd());
                await tcs.Task;

                sup.Nvidia = output.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0;
                sup.Intel = output.IndexOf("_qsv", StringComparison.OrdinalIgnoreCase) >= 0;
                sup.Amd = output.IndexOf("_amf", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return sup;
        }

        #endregion

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
                output = await Task.Run(() => proc.StandardOutput.ReadToEnd());
                await tcs.Task;
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
                await proc.StandardOutput.BaseStream.CopyToAsync(ms);
                await tcs.Task;

                if (ms.Length == 0) return null;
                ms.Position = 0;

                // Copy into a Bitmap so the underlying stream can be disposed.
                using (var temp = Image.FromStream(ms))
                    return new Bitmap(temp);
            }
        }

        /// <summary>
        /// Build ffmpeg argument string for a task.
        /// Honors two optional modes set on the task before a run:
        ///  - UseStreamCopy : remux with "-c copy" (high-speed mode, same container)
        ///  - HardwareEncoder : swap the software video encoder for a HW one
        /// </summary>
        public static string BuildArguments(ConversionTask task)
        {
            var sb = new StringBuilder();
            sb.Append(" -y"); // overwrite output

            // Trim: apply -ss before input for fast seek, -to after input.
            if (task.TrimStartSeconds > 0)
                sb.AppendFormat(" -ss {0:0.000}", task.TrimStartSeconds);

            sb.AppendFormat(" -i \"{0}\"", task.InputPath);

            if (task.TrimEndSeconds > task.TrimStartSeconds && task.TrimEndSeconds > 0)
                sb.AppendFormat(" -to {0:0.000}", task.TrimEndSeconds);

            // Stream mapping for selected tracks.
            sb.Append(" -map 0:v:0");
            if (task.SelectedAudioTrack != null)
                sb.AppendFormat(" -map 0:a:{0}", task.SelectedAudioTrack.Index);
            else
                sb.Append(" -an");

            // High-speed mode: stream copy (only valid for matching containers).
            if (task.UseStreamCopy)
            {
                sb.Append(" -c copy");
                sb.AppendFormat(" \"{0}\"", task.OutputPath);
                return sb.ToString();
            }

            var p = task.Preset;
            if (p != null)
            {
                // Video
                string vcodec = !string.IsNullOrEmpty(task.HardwareEncoder) ? task.HardwareEncoder : p.VideoCodec;
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

            sb.AppendFormat(" \"{0}\"", task.OutputPath);
            return sb.ToString();
        }

        /// <summary>
        /// Run ffmpeg with progress callbacks.
        /// </summary>
        public static async Task RunAsync(ConversionTask task,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(FFmpegPath))
                throw new FileNotFoundException("ffmpeg.exe not found.", FFmpegPath);

            string args = BuildArguments(task);
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

            double duration = await GetDurationAsync(task.InputPath);
            var durationRegex = new Regex(@"Duration:\s+(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);
            var timeRegex = new Regex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)", RegexOptions.Compiled);

            using (var proc = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var tcs = new TaskCompletionSource<object>();
                proc.Exited += (s, e) => tcs.TrySetResult(null);
                proc.Start();
                ProcessGuard.Register(proc, tempFile);

                // Read stderr line by line to parse progress.
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

                try { await tcs.Task; }
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
