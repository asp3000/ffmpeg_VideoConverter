// ============================================================================
//  DefaultCodecSettings.cs — 自动码率默认值与目标容器默认编码的可编辑配置。
//  配置文件 default_codec_settings.json 与程序同目录，可手工修改；
//  缺失时用内置默认值（用户确认版）并自动生成文件。
//  用于：①「码率=自动」时的默认参数（如 libx264 → -crf 23）；②「与源文件相同」
//  类预设/高速转换智能 copy 时的目标默认编码器与默认码率。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VideoConverter
{
    public static class DefaultCodecSettings
    {
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "default_codec_settings.json");

        // 视频编码器 → 质量参数默认（param/min/max/value=推荐值）
        private static readonly Dictionary<string, VideoDefaultJson> VideoDefaults =
            new Dictionary<string, VideoDefaultJson>(StringComparer.OrdinalIgnoreCase);

        // 音频编码器 → 默认码率
        private static readonly Dictionary<string, string> AudioDefaults =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 目标容器 → 默认视频/音频编码器（实际 ffmpeg 编码器名）
        private static readonly Dictionary<string, ContainerDefaultJson> ContainerDefaults =
            new Dictionary<string, ContainerDefaultJson>(StringComparer.OrdinalIgnoreCase);

        public static void EnsureLoaded()
        {
            if (Loaded) return;
            Load();
        }

        private static bool _loaded;
        public static bool Loaded { get { return _loaded; } }

        public static void Load()
        {
            try
            {
                var json = ReadJson();
                if (json != null)
                {
                    Apply(json);
                    return;
                }
            }
            catch { }

            // 文件缺失或损坏：用内置默认并生成文件，方便手工修改。
            var builtIn = BuildBuiltIn();
            Apply(builtIn);
            try { WriteJson(builtIn); } catch { }
        }

        private static void Apply(DefaultCodecSettingsJson j)
        {
            VideoDefaults.Clear();
            AudioDefaults.Clear();
            ContainerDefaults.Clear();
            if (j.videoDefaults != null)
                foreach (var v in j.videoDefaults)
                    if (v != null && !string.IsNullOrWhiteSpace(v.codec))
                        VideoDefaults[v.codec] = v;
            if (j.audioDefaults != null)
                foreach (var a in j.audioDefaults)
                    if (a != null && !string.IsNullOrWhiteSpace(a.codec))
                        AudioDefaults[a.codec] = a.bitrate;
            if (j.containerDefaults != null)
                foreach (var c in j.containerDefaults)
                    if (c != null && !string.IsNullOrWhiteSpace(c.container))
                        ContainerDefaults[c.container] = c;
            _loaded = true;
        }

        // ---- queries -------------------------------------------------------

        /// <summary>按实际编码器名返回自动码率默认（配置优先；未配置回退 FFmpegHelper 硬编码推荐）。</summary>
        public static FFmpegHelper.QualitySpec GetVideoDefault(string encoder)
        {
            if (!string.IsNullOrWhiteSpace(encoder) && VideoDefaults.TryGetValue(encoder, out var v))
                return new FFmpegHelper.QualitySpec { Param = v.param, Min = v.min, Max = v.max, Recommended = v.value };
            return FFmpegHelper.GetQualitySpec(encoder);
        }

        /// <summary>按实际音频编码器名返回默认码率（如 "192k"），未配置返回 null。</summary>
        public static string GetAudioDefaultBitrate(string encoder)
        {
            if (string.IsNullOrWhiteSpace(encoder)) return null;
            string br;
            return AudioDefaults.TryGetValue(encoder, out br) ? br : null;
        }

        /// <summary>按容器名（如 "MP4"）返回默认视频编码器（实际 ffmpeg 编码器名，如 libx264）。</summary>
        public static string GetContainerVideoEncoder(string container)
        {
            if (string.IsNullOrWhiteSpace(container)) return null;
            ContainerDefaultJson c;
            return ContainerDefaults.TryGetValue(container, out c) ? c.video : null;
        }

        /// <summary>按容器名返回默认音频编码器（实际编码器名，如 aac）。</summary>
        public static string GetContainerAudioEncoder(string container)
        {
            if (string.IsNullOrWhiteSpace(container)) return null;
            ContainerDefaultJson c;
            return ContainerDefaults.TryGetValue(container, out c) ? c.audio : null;
        }

        /// <summary>
        /// 「采样率=自动」时的全局默认值（Hz）。无法流复制时用此值兜底。
        /// </summary>
        public static int GetDefaultSampleRate() { return 44100; }

        /// <summary>
        /// 「声道=自动」时的全局默认值。无法流复制时用此值兜底。
        /// </summary>
        public static int GetDefaultChannels() { return 2; }

        /// <summary>
        /// 「帧率=自动」时的全局默认值（Hz）；0 表示保持源帧率（不指定 -r）。
        /// 无法流复制时不指定 -r，由编码器沿用源帧率，避免抽帧/补帧。
        /// </summary>
        public static double GetDefaultFrameRate() { return 0; }

        // ---- built-in defaults (user-confirmed) ----------------------------

        private static DefaultCodecSettingsJson BuildBuiltIn()
        {
            return new DefaultCodecSettingsJson
            {
                videoDefaults = new List<VideoDefaultJson>
                {
                    V("libx264", "-crf", 23, 0, 51),
                    V("libx265", "-crf", 28, 0, 51),
                    V("h264_nvenc", "-cq", 26, 0, 51),
                    V("hevc_nvenc", "-cq", 30, 0, 51),
                    V("h264_qsv", "-global_quality", 23, 1, 51),
                    V("hevc_qsv", "-global_quality", 23, 1, 51),
                    V("h264_amf", "-qp", 23, 0, 51),
                    V("hevc_amf", "-qp", 23, 0, 51),
                    V("libvpx-vp9", "-crf", 30, 0, 63),
                    V("libaom-av1", "-crf", 30, 0, 63),
                    V("libsvtav1", "-crf", 30, 0, 63),
                    V("mpeg4", "-q:v", 3, 1, 31),
                    V("libxvid", "-q:v", 3, 1, 31),
                    V("mpeg2video", "-q:v", 3, 1, 31),
                    V("mjpeg", "-q:v", 3, 1, 31),
                    V("wmv2", "-q:v", 5, 1, 31),
                    V("wmv3", "-q:v", 5, 1, 31),
                    V("cfhd", "-q:v", 5, 1, 31),
                    V("prores_ks", "-q:v", 3, 1, 5),
                    V("libwebp", "-q:v", 80, 0, 100),
                    V("libtheora", "-q:v", 5, 0, 10)
                },
                audioDefaults = new List<AudioDefaultJson>
                {
                    A("aac", "192k"),
                    A("libmp3lame", "192k"),
                    A("libopus", "160k"),
                    A("libvorbis", "160k"),
                    A("ac3", "192k"),
                    A("wmav2", "192k"),
                    A("mp2", "192k"),
                    A("pcm_s16le", null),
                    A("pcm_s24le", null)
                },
                containerDefaults = new List<ContainerDefaultJson>
                {
                    C("MP4", "libx264", "aac"),
                    C("MOV", "libx264", "aac"),
                    C("M4V", "libx264", "aac"),
                    C("F4V", "libx264", "aac"),
                    C("MKV", "libx265", "aac"),
                    C("AVI", "mpeg4", "libmp3lame"),
                    C("WMV", "wmv2", "wmav2"),
                    C("ASF", "wmv2", "wmav2"),
                    C("WEBM", "libvpx-vp9", "libopus"),
                    C("MPEG", "mpeg2video", "libmp3lame"),
                    C("MPG", "mpeg2video", "libmp3lame"),
                    C("VOB", "mpeg2video", "libmp3lame"),
                    C("TS", "libx264", "aac"),
                    C("M2TS", "libx264", "aac"),
                    C("FLV", "libx264", "aac"),
                    C("3GP", "libx264", "aac"),
                    C("OGV", "libtheora", "libvorbis"),
                    C("GIF", "gif", null),
                    // ---- 视频容器 ----
                    C("DV", "dvvideo", "pcm_s16le"),
                    C("3G2", "libx264", "aac"),
                    C("AMV", "mjpeg", "libmp3lame"),
                    C("MXF", "mpeg2video", "pcm_s16le"),
                    // ---- 音频容器（video=null，无视频流）----
                    C("MP3", null, "libmp3lame"),
                    C("WAV", null, "pcm_s16le"),
                    C("AAC", null, "aac"),
                    C("AC3", null, "ac3"),
                    C("FLAC", null, "flac"),
                    C("M4A", null, "aac"),
                    C("M4B", null, "aac"),
                    C("M4R", null, "aac"),
                    C("WMA", null, "wmav2"),
                    C("OGG", null, "libvorbis"),
                    C("MKA", null, "aac"),
                    C("AU", null, "pcm_s16le"),
                    C("APE", null, "ape"),
                    C("AIFF", null, "pcm_s16le"),
                    C("AMR", null, "amr_nb"),
                    C("MP2", null, "mp2"),
                    // ---- 图像容器（audio=null，无音频流）----
                    C("JPG", "mjpeg", null),
                    C("JPEG", "mjpeg", null),
                    C("PNG", "png", null),
                    C("BMP", "bmp", null),
                    C("TIFF", "tiff", null),
                    C("TIF", "tiff", null),
                    C("ICO", "png", null),
                    C("SVG", "png", null),
                    C("WEBP", "libwebp", null),
                    C("AVIF", "libaom-av1", null),
                    C("EPS", "png", null),
                    C("PSD", "png", null),
                    C("XBM", "xbm", null)
                }
            };
        }

        private static VideoDefaultJson V(string codec, string param, int value, int min, int max)
        {
            return new VideoDefaultJson { codec = codec, param = param, value = value, min = min, max = max };
        }

        private static AudioDefaultJson A(string codec, string bitrate)
        {
            return new AudioDefaultJson { codec = codec, bitrate = bitrate };
        }

        private static ContainerDefaultJson C(string container, string video, string audio)
        {
            return new ContainerDefaultJson { container = container, video = video, audio = audio };
        }

        // ---- JSON IO -------------------------------------------------------

        private static DefaultCodecSettingsJson ReadJson()
        {
            if (!File.Exists(FilePath)) return null;
            using (var fs = File.OpenRead(FilePath))
            {
                var ser = new DataContractJsonSerializer(typeof(DefaultCodecSettingsJson));
                return ser.ReadObject(fs) as DefaultCodecSettingsJson;
            }
        }

        private static void WriteJson(DefaultCodecSettingsJson obj)
        {
            using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
            {
                var ser = new DataContractJsonSerializer(typeof(DefaultCodecSettingsJson));
                ser.WriteObject(fs, obj);
            }
        }
    }

    // ---- JSON contracts ----------------------------------------------------

    [DataContract]
    public class VideoDefaultJson
    {
        [DataMember] public string codec;
        [DataMember] public string param;
        [DataMember] public int value;
        [DataMember] public int min;
        [DataMember] public int max;
    }

    [DataContract]
    public class AudioDefaultJson
    {
        [DataMember] public string codec;
        [DataMember] public string bitrate;
    }

    [DataContract]
    public class ContainerDefaultJson
    {
        [DataMember] public string container;
        [DataMember] public string video;
        [DataMember] public string audio;
    }

    [DataContract]
    public class DefaultCodecSettingsJson
    {
        [DataMember] public List<VideoDefaultJson> videoDefaults;
        [DataMember] public List<AudioDefaultJson> audioDefaults;
        [DataMember] public List<ContainerDefaultJson> containerDefaults;
    }
}
