// ============================================================================
//  PresetDataStore.cs — loads the three-layer UniConverter preset spec.
//
//  Layer 1  options_spec/presets.json         预设：类型 / 名称 / 默认参数值
//  Layer 2  options_spec/common_options.json  公共下拉项（分辨率、帧率、码率、
//                                             采样率、声道…… 全局去重后只存一份）
//  Layer 3  options_spec/format_options.json  特定类型下拉项（每个封装格式自己
//                                             的视频/音频编码器，例如 AVI 只有
//                                             Xvid / DivX / MS MPEG-4 v3 /
//                                             MJPEG / H.264 / FFV1）
//
//  预设只保存默认值；界面下拉在运行期由「公共项 + 该格式的特定项」拼装。
//
//  IMPORTANT — DataContractJsonSerializer 陷阱：
//    * 反序列化 JSON object 到 Dictionary<string,T> 会「静默」返回空字典且不抛
//      异常，所以三个文件全部使用纯数组（array）结构。
//    * 成员按字母序匹配，JSON 必须 sort_keys 输出；类型不符的成员会被静默丢弃，
//      因此 frameRate 用 double、channel 用 int，不可错配。
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VideoConverter
{
    /// <summary>
    /// In-memory store built from the extracted UniConverter preset database.
    /// </summary>
    public static class PresetDataStore
    {
        private static string SpecPath(string fileName)
        {
            string baseDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(baseDir)) baseDir = AppDomain.CurrentDomain.BaseDirectory;
            return Path.Combine(baseDir, "options_spec", fileName);
        }

        /// <summary>All category names in UI order.</summary>
        public static List<string> Categories { get; private set; } = new List<string>();

        /// <summary>Each category maps to a list of formats (id/title/icon/formatId/presets).</summary>
        public static Dictionary<string, List<FormatEntry>> FormatsByCategory { get; private set; }
            = new Dictionary<string, List<FormatEntry>>();

        /// <summary>Layer 3 — format-specific codec lists, keyed by format ID.</summary>
        public static Dictionary<string, FormatOptionJson> FormatSpecs { get; private set; }
            = new Dictionary<string, FormatOptionJson>();

        /// <summary>Layer 2 — shared dropdown pools, stored once for the whole app.</summary>
        public static CommonOptionsJson Common { get; private set; } = new CommonOptionsJson();

        /// <summary>FourCC -> ffmpeg encoder, aggregated from every format entry.</summary>
        private static readonly Dictionary<string, CodecJson> VideoCodecIndex =
            new Dictionary<string, CodecJson>(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, CodecJson> AudioCodecIndex =
            new Dictionary<string, CodecJson>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Last load exception, if any. Useful for diagnostics.</summary>
        public static Exception LoadException { get; private set; }

        /// <summary>True when at least one category was loaded.</summary>
        public static bool IsLoaded => Categories.Count > 0;

        /// <summary>
        /// Idempotent wrapper around <see cref="Load"/>. Safe to call from
        /// anywhere; the preset database is parsed once and then held in this
        /// static object for the lifetime of the process. #43
        /// </summary>
        public static void EnsureLoaded()
        {
            if (IsLoaded) return;
            Load();
        }

        /// <summary>Recently selected presets (max 20).</summary>
        public static List<PresetOption> RecentPresets { get; private set; } = new List<PresetOption>();

        public static void AddRecent(PresetOption preset)
        {
            if (preset == null) return;
            RecentPresets.RemoveAll(p => p.PresetId == preset.PresetId && p.Name == preset.Name && p.FormatId == preset.FormatId);
            RecentPresets.Insert(0, preset.Clone());
            if (RecentPresets.Count > 20)
                RecentPresets.RemoveAt(RecentPresets.Count - 1);
        }

        // ------------------------------------------------------------------ //
        //  Loading
        // ------------------------------------------------------------------ //

        public static void Load()
        {
            LoadException = null;
            Categories = new List<string>();
            FormatsByCategory = new Dictionary<string, List<FormatEntry>>();
            FormatSpecs = new Dictionary<string, FormatOptionJson>();
            Common = new CommonOptionsJson();
            VideoCodecIndex.Clear();
            AudioCodecIndex.Clear();

            PresetsRoot presetsRoot = null;

            try
            {
                Common = ReadJson<CommonOptionsJson>(SpecPath("common_options.json")) ?? new CommonOptionsJson();

                var formatRoot = ReadJson<FormatOptionsRoot>(SpecPath("format_options.json"));
                if (formatRoot?.formats != null)
                {
                    foreach (var f in formatRoot.formats)
                    {
                        if (f == null || string.IsNullOrEmpty(f.id)) continue;
                        FormatSpecs[f.id] = f;
                        IndexCodecs(f);
                    }
                }

                presetsRoot = ReadJson<PresetsRoot>(SpecPath("presets.json"));
            }
            catch (Exception ex)
            {
                LoadException = ex;
            }

            BuildIndex(presetsRoot);

            // Array-shaped JSON never throws on shape mismatch, so treat "nothing
            // loaded" as a failure too — the caller falls back to built-in presets.
            if (!IsLoaded && LoadException == null)
                LoadException = new InvalidDataException(
                    "options_spec 下未读取到任何预设（presets.json 缺失或结构不符）。");
        }

        private static T ReadJson<T>(string path) where T : class
        {
            if (!File.Exists(path)) return null;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                return ser.ReadObject(fs) as T;
            }
        }

        private static void IndexCodecs(FormatOptionJson f)
        {
            if (f.videoCodecs != null)
                foreach (var c in f.videoCodecs)
                    if (c != null && !string.IsNullOrEmpty(c.fourCC) && !VideoCodecIndex.ContainsKey(c.fourCC))
                        VideoCodecIndex[c.fourCC] = c;

            if (f.audioCodecs != null)
                foreach (var c in f.audioCodecs)
                    if (c != null && !string.IsNullOrEmpty(c.fourCC) && !AudioCodecIndex.ContainsKey(c.fourCC))
                        AudioCodecIndex[c.fourCC] = c;
        }

        private static void BuildIndex(PresetsRoot root)
        {
            if (root?.categories == null) return;

            foreach (var cat in root.categories)
            {
                if (cat == null || string.IsNullOrEmpty(cat.name) || cat.formats == null) continue;

                Categories.Add(cat.name);
                var list = new List<FormatEntry>();
                foreach (var fmt in cat.formats)
                {
                    if (fmt == null) continue;
                    var entry = new FormatEntry
                    {
                        Id = fmt.id ?? string.Empty,
                        Title = fmt.title ?? string.Empty,
                        Icon = fmt.icon ?? string.Empty,
                        FormatId = fmt.formatId ?? string.Empty,
                    };

                    if (fmt.presets != null)
                        foreach (var p in fmt.presets)
                            if (p != null)
                                entry.Presets.Add(ToPresetOption(p, entry));

                    list.Add(entry);
                }
                FormatsByCategory[cat.name] = list;
            }
        }

        private static PresetOption ToPresetOption(PresetJson p, FormatEntry format)
        {
            string fmtId = string.IsNullOrEmpty(p.formatId) ? format.FormatId : p.formatId;
            FormatOptionJson fmtOpt;
            FormatSpecs.TryGetValue(fmtId ?? string.Empty, out fmtOpt);

            string ext = ".mp4";
            if (fmtOpt != null && !string.IsNullOrEmpty(fmtOpt.extension))
                ext = "." + fmtOpt.extension;
            else if (!string.IsNullOrEmpty(format.Title))
                ext = "." + format.Title.ToLowerInvariant();

            if (p.keepSource)
            {
                return new PresetOption
                {
                    Name = p.name ?? string.Empty,
                    FormatName = format.Title,
                    Extension = ext,
                    VideoCodec = "copy",
                    AudioCodec = "copy",
                    ResolutionLabel = "与源文件相同",
                    ResolutionValue = null,
                    VideoBitrate = null,
                    AudioBitrate = null,
                    FrameRate = null,
                    PresetId = p.id,
                    FormatId = fmtId,
                    FourCC = p.fourCC ?? string.Empty,
                    KeepSource = true,
                    IsBuiltIn = true,
                };
            }

            int w = p.resolution != null ? p.resolution.width : 0;
            int h = p.resolution != null ? p.resolution.height : 0;

            return new PresetOption
            {
                Name = p.name ?? string.Empty,
                FormatName = format.Title,
                Extension = ext,
                VideoCodec = ResolveVideoEncoder(p.videoCodec, fmtOpt),
                AudioCodec = ResolveAudioEncoder(p.audioCodec, fmtOpt),
                ResolutionLabel = w > 0 && h > 0 ? string.Format("{0} x {1}", w, h) : "与源文件相同",
                ResolutionValue = w > 0 && h > 0 ? string.Format("{0}x{1}", w, h) : null,
                VideoBitrate = p.videoBitrate > 0 ? p.videoBitrate + "k" : null,
                AudioBitrate = p.audioBitrate > 0 ? p.audioBitrate + "k" : null,
                FrameRate = p.frameRate > 0 ? FormatFps(p.frameRate) : null,
                SampleRate = p.sampleRate > 0 ? p.sampleRate.ToString(CultureInfo.InvariantCulture) : null,
                Channels = p.channel > 0 ? p.channel : 0,
                PresetId = p.id,
                FormatId = fmtId,
                FourCC = p.fourCC ?? string.Empty,
                KeepSource = false,
                IsBuiltIn = true,
            };
        }

        // ------------------------------------------------------------------ //
        //  Codec resolution — FourCC is looked up in the format's own list
        //  first (layer 3), then in the global index. No hard-coded table.
        // ------------------------------------------------------------------ //

        private static string ResolveVideoEncoder(string fourCC, FormatOptionJson fmt)
        {
            var c = FindCodec(fourCC, fmt?.videoCodecs, VideoCodecIndex);
            if (c != null) return c.encoder;
            return string.IsNullOrEmpty(fourCC) ? "copy" : fourCC.ToLowerInvariant();
        }

        private static string ResolveAudioEncoder(string fourCC, FormatOptionJson fmt)
        {
            if (string.IsNullOrEmpty(fourCC)) return string.Empty;   // 无音频轨（如图片）
            var c = FindCodec(fourCC, fmt?.audioCodecs, AudioCodecIndex);
            return c != null ? c.encoder : fourCC.ToLowerInvariant();
        }

        private static CodecJson FindCodec(string fourCC, List<CodecJson> local, Dictionary<string, CodecJson> global)
        {
            if (string.IsNullOrEmpty(fourCC)) return null;
            if (local != null)
            {
                var hit = local.FirstOrDefault(c => c != null &&
                    string.Equals(c.fourCC, fourCC, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            CodecJson g;
            return global.TryGetValue(fourCC, out g) ? g : null;
        }

        public static FormatEntry FindFormat(string formatId)
        {
            if (string.IsNullOrEmpty(formatId)) return null;
            foreach (var list in FormatsByCategory.Values)
            {
                var found = list.FirstOrDefault(f => f.FormatId == formatId);
                if (found != null) return found;
            }
            return null;
        }

        // ------------------------------------------------------------------ //
        //  Dropdown assembly: common pool + format-specific codecs
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Build every dropdown for a format: the codec lists come from
        /// format_options.json (layer 3), everything else from
        /// common_options.json (layer 2).
        /// </summary>
        public static FormatOptions GetFormatOptions(string formatId)
        {
            var result = new FormatOptions();

            FormatOptionJson fmt = null;
            if (!string.IsNullOrEmpty(formatId))
                FormatSpecs.TryGetValue(formatId, out fmt);

            // ---- layer 3: codecs specific to this container ----
            if (fmt?.videoCodecs != null)
                foreach (var c in fmt.videoCodecs)
                    if (c != null && !string.IsNullOrEmpty(c.encoder))
                        result.VideoCodecs.Add(new OptionItem(c.encoder, BuildCodecLabel(c)));

            if (fmt?.audioCodecs != null)
                foreach (var c in fmt.audioCodecs)
                    if (c != null && !string.IsNullOrEmpty(c.encoder))
                        result.AudioCodecs.Add(new OptionItem(c.encoder, BuildCodecLabel(c)));

            // ---- layer 2: shared pools ----
            if (Common.resolutions != null)
                foreach (var r in Common.resolutions)
                    if (r != null && r.width > 0 && r.height > 0)
                        result.Resolutions.Add(new OptionItem(
                            string.Format("{0}x{1}", r.width, r.height),
                            string.IsNullOrEmpty(r.label) ? string.Format("{0} x {1}", r.width, r.height) : r.label));

            if (Common.frameRates != null)
                foreach (var f in Common.frameRates)
                    if (f > 0)
                        result.FrameRates.Add(new OptionItem(FormatFps(f), FormatFps(f) + " fps"));

            if (Common.videoBitrates != null)
                foreach (var b in Common.videoBitrates)
                    if (b > 0)
                        result.VideoBitrates.Add(new OptionItem(b + "k", b + " kbps"));

            if (Common.audioBitrates != null)
                foreach (var b in Common.audioBitrates)
                    if (b > 0)
                        result.AudioBitrates.Add(new OptionItem(b + "k", b + " kbps"));

            if (Common.sampleRates != null)
                foreach (var s in Common.sampleRates)
                    if (s > 0)
                        result.SampleRates.Add(new OptionItem(s.ToString(CultureInfo.InvariantCulture), s + " Hz"));

            if (Common.channels != null)
                foreach (var c in Common.channels)
                    if (c != null && c.value > 0)
                        result.Channels.Add(new OptionItem(
                            c.value.ToString(CultureInfo.InvariantCulture),
                            string.IsNullOrEmpty(c.label) ? c.value + " 声道" : c.label));

            return result;
        }

        private static string BuildCodecLabel(CodecJson c)
        {
            if (string.IsNullOrEmpty(c.label)) return c.encoder;
            return string.Equals(c.label, c.encoder, StringComparison.OrdinalIgnoreCase)
                ? c.label
                : c.label + " (" + c.encoder + ")";
        }

        /// <summary>23.97 -> "23.97", 30.0 -> "30" (invariant, ffmpeg-safe).</summary>
        internal static string FormatFps(double fps)
        {
            return fps == Math.Floor(fps)
                ? ((int)fps).ToString(CultureInfo.InvariantCulture)
                : fps.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    #region JSON data contracts — all containers are ARRAYS on purpose

    [DataContract]
    public class PresetsRoot
    {
        [DataMember] public List<CategoryJson> categories { get; set; }
    }

    [DataContract]
    public class CategoryJson
    {
        [DataMember] public string name { get; set; }
        [DataMember] public List<FormatJson> formats { get; set; }
    }

    [DataContract]
    public class FormatJson
    {
        [DataMember] public string id { get; set; }
        [DataMember] public string title { get; set; }
        [DataMember] public string icon { get; set; }
        [DataMember] public string formatId { get; set; }
        [DataMember] public List<PresetJson> presets { get; set; }
    }

    [DataContract]
    public class PresetJson
    {
        [DataMember] public string id { get; set; }
        [DataMember] public string name { get; set; }
        [DataMember] public string formatId { get; set; }
        [DataMember] public string fourCC { get; set; }
        [DataMember] public bool keepSource { get; set; }
        [DataMember] public string videoCodec { get; set; }
        [DataMember] public ResolutionJson resolution { get; set; }
        [DataMember] public int videoBitrate { get; set; }
        [DataMember] public double frameRate { get; set; }
        [DataMember] public string audioCodec { get; set; }
        [DataMember] public int channel { get; set; }
        [DataMember] public int sampleRate { get; set; }
        [DataMember] public int audioBitrate { get; set; }
    }

    [DataContract]
    public class ResolutionJson
    {
        [DataMember] public int width { get; set; }
        [DataMember] public int height { get; set; }
    }

    // ---- common_options.json ------------------------------------------------

    [DataContract]
    public class CommonOptionsJson
    {
        [DataMember] public List<ResolutionOptionJson> resolutions { get; set; }
        [DataMember] public List<double> frameRates { get; set; }
        [DataMember] public List<int> videoBitrates { get; set; }
        [DataMember] public List<int> audioBitrates { get; set; }
        [DataMember] public List<int> sampleRates { get; set; }
        [DataMember] public List<ChannelOptionJson> channels { get; set; }
    }

    [DataContract]
    public class ResolutionOptionJson
    {
        [DataMember] public int id { get; set; }
        [DataMember] public int width { get; set; }
        [DataMember] public int height { get; set; }
        [DataMember] public string label { get; set; }
    }

    [DataContract]
    public class ChannelOptionJson
    {
        [DataMember] public int value { get; set; }
        [DataMember] public string label { get; set; }
    }

    // ---- format_options.json ------------------------------------------------

    [DataContract]
    public class FormatOptionsRoot
    {
        [DataMember] public List<FormatOptionJson> formats { get; set; }
    }

    [DataContract]
    public class FormatOptionJson
    {
        [DataMember] public string id { get; set; }
        [DataMember] public string name { get; set; }
        [DataMember] public string fourCC { get; set; }
        [DataMember] public string extension { get; set; }
        [DataMember] public List<CodecJson> videoCodecs { get; set; }
        [DataMember] public List<CodecJson> audioCodecs { get; set; }
    }

    [DataContract]
    public class CodecJson
    {
        [DataMember] public string fourCC { get; set; }
        [DataMember] public string label { get; set; }
        [DataMember] public string encoder { get; set; }
    }

    #endregion

    public class FormatEntry
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Icon { get; set; }
        public string FormatId { get; set; }
        public List<PresetOption> Presets { get; set; } = new List<PresetOption>();
    }

    /// <summary>A dropdown entry: friendly label for the UI, raw value for ffmpeg.</summary>
    public class OptionItem
    {
        public string Value { get; set; }
        public string Label { get; set; }

        public OptionItem() { }

        public OptionItem(string value, string label)
        {
            Value = value;
            Label = label;
        }

        public override string ToString() => Label ?? Value ?? string.Empty;
    }

    /// <summary>All dropdown lists for one output format.</summary>
    public class FormatOptions
    {
        public List<OptionItem> VideoCodecs { get; set; } = new List<OptionItem>();
        public List<OptionItem> Resolutions { get; set; } = new List<OptionItem>();
        public List<OptionItem> VideoBitrates { get; set; } = new List<OptionItem>();
        public List<OptionItem> FrameRates { get; set; } = new List<OptionItem>();
        public List<OptionItem> AudioCodecs { get; set; } = new List<OptionItem>();
        public List<OptionItem> SampleRates { get; set; } = new List<OptionItem>();
        public List<OptionItem> AudioBitrates { get; set; } = new List<OptionItem>();
        public List<OptionItem> Channels { get; set; } = new List<OptionItem>();
    }
}
