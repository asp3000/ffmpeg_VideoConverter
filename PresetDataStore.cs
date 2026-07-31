// ============================================================================
//  PresetDataStore.cs — loads UniConverter-style preset specs from JSON.
//  Data source: options_spec/presets.json + options_spec/format_options.json
//  Uses DataContractJsonSerializer (System.Runtime.Serialization) so we do
//  not depend on System.Web.Extensions at runtime.
// ============================================================================

using System;
using System.Collections.Generic;
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
        private static readonly string PresetsPath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "options_spec", "presets.json");

        private static readonly string FormatOptionsPath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "options_spec", "format_options.json");

        /// <summary>All category names in UI order.</summary>
        public static List<string> Categories { get; private set; } = new List<string>();

        /// <summary>
        /// Each category maps to a list of formats. Each format has id/title/icon/formatId/presets.
        /// </summary>
        public static Dictionary<string, List<FormatEntry>> FormatsByCategory { get; private set; }
            = new Dictionary<string, List<FormatEntry>>();

        /// <summary>Raw format options keyed by format ID.</summary>
        public static Dictionary<string, FormatOptionJson> FormatOptions { get; private set; }
            = new Dictionary<string, FormatOptionJson>();

        /// <summary>Last load exception, if any. Useful for diagnostics.</summary>
        public static Exception LoadException { get; private set; }

        /// <summary>True when at least one category was loaded.</summary>
        public static bool IsLoaded => Categories.Count > 0;

        /// <summary>Recently selected presets (max 20).</summary>
        public static List<PresetOption> RecentPresets { get; private set; } = new List<PresetOption>();

        public static void AddRecent(PresetOption preset)
        {
            if (preset == null) return;
            // Remove existing identical entry.
            RecentPresets.RemoveAll(p => p.PresetId == preset.PresetId && p.Name == preset.Name && p.FormatId == preset.FormatId);
            RecentPresets.Insert(0, preset.Clone());
            if (RecentPresets.Count > 20)
                RecentPresets.RemoveAt(RecentPresets.Count - 1);
        }

        public static void Load()
        {
            LoadException = null;
            Categories = new List<string>();
            FormatsByCategory = new Dictionary<string, List<FormatEntry>>();
            FormatOptions = new Dictionary<string, FormatOptionJson>();

            PresetsRoot presetsRoot = null;
            Dictionary<string, FormatOptionJson> formatRoot = null;

            try
            {
                if (File.Exists(PresetsPath))
                {
                    using (var fs = new FileStream(PresetsPath, FileMode.Open, FileAccess.Read))
                    {
                        var ser = new DataContractJsonSerializer(typeof(PresetsRoot));
                        presetsRoot = ser.ReadObject(fs) as PresetsRoot;
                    }
                }

                if (File.Exists(FormatOptionsPath))
                {
                    using (var fs = new FileStream(FormatOptionsPath, FileMode.Open, FileAccess.Read))
                    {
                        var ser = new DataContractJsonSerializer(typeof(Dictionary<string, FormatOptionJson>));
                        formatRoot = ser.ReadObject(fs) as Dictionary<string, FormatOptionJson>;
                    }
                }
            }
            catch (Exception ex)
            {
                LoadException = ex;
            }

            if (formatRoot != null)
                FormatOptions = formatRoot;

            BuildIndex(presetsRoot);
        }

        private static void BuildIndex(PresetsRoot root)
        {
            if (root?.categories == null) return;

            foreach (var kv in root.categories)
            {
                string catName = kv.Key;
                var formats = kv.Value;
                if (formats == null) continue;

                Categories.Add(catName);
                var list = new List<FormatEntry>();
                foreach (var fmt in formats)
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
                    {
                        foreach (var p in fmt.presets)
                        {
                            if (p == null) continue;
                            entry.Presets.Add(ToPresetOption(p, entry));
                        }
                    }

                    list.Add(entry);
                }
                FormatsByCategory[catName] = list;
            }
        }

        private static PresetOption ToPresetOption(PresetJson p, FormatEntry format)
        {
            string name = p.name ?? string.Empty;
            string fmtId = p.formatId ?? format.FormatId;
            string fourCC = p.fourCC ?? string.Empty;
            bool keep = p.keepSource;

            string ext = ".mp4";
            FormatOptionJson fmtOpt = null;
            if (FormatOptions.ContainsKey(fmtId))
                fmtOpt = FormatOptions[fmtId];
            if (fmtOpt != null && !string.IsNullOrEmpty(fmtOpt.extension))
                ext = "." + fmtOpt.extension;
            else if (!string.IsNullOrEmpty(format.Title))
                ext = "." + format.Title.ToLowerInvariant();

            int w = 0, h = 0;
            if (p.resolution != null)
            {
                w = p.resolution.width;
                h = p.resolution.height;
            }

            if (keep)
            {
                return new PresetOption
                {
                    Name = name,
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
                    FourCC = fourCC,
                    KeepSource = true,
                    IsBuiltIn = true,
                };
            }

            string ffmpegVideoCodec = FourCCToVideoCodec(p.defaultVideoCodec, fourCC);
            string ffmpegAudioCodec = FourCCToAudioCodec(p.defaultAudioCodec);

            return new PresetOption
            {
                Name = name,
                FormatName = format.Title,
                Extension = ext,
                VideoCodec = ffmpegVideoCodec,
                AudioCodec = ffmpegAudioCodec,
                ResolutionLabel = w > 0 && h > 0 ? string.Format("{0} x {1}", w, h) : "与源文件相同",
                ResolutionValue = w > 0 && h > 0 ? string.Format("{0}x{1}", w, h) : null,
                VideoBitrate = p.defaultBitrate > 0 ? p.defaultBitrate + "k" : null,
                AudioBitrate = p.defaultAudioBitrate > 0 ? p.defaultAudioBitrate + "k" : null,
                FrameRate = p.defaultFrameRate > 0 ? p.defaultFrameRate.ToString() : null,
                SampleRate = p.defaultSampleRate > 0 ? p.defaultSampleRate.ToString() : null,
                Channels = p.defaultChannel > 0 ? p.defaultChannel : 0,
                PresetId = p.id,
                FormatId = fmtId,
                FourCC = fourCC,
                KeepSource = false,
                IsBuiltIn = true,
            };
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

        /// <summary>
        /// Return video/audio dropdown options for a given format ID.
        /// </summary>
        public static FormatOptions GetFormatOptions(string formatId)
        {
            var result = new FormatOptions();
            if (string.IsNullOrEmpty(formatId) || !FormatOptions.ContainsKey(formatId))
                return result;

            var fmt = FormatOptions[formatId];
            if (fmt?.videoOptions != null)
            {
                foreach (var vo in fmt.videoOptions)
                {
                    if (vo == null) continue;
                    result.VideoCodecs.Add(FourCCToVideoCodec(vo.codec, fmt.fourcc));

                    if (vo.resolution != null && vo.resolution.width > 0 && vo.resolution.height > 0)
                        result.Resolutions.Add(string.Format("{0}x{1}", vo.resolution.width, vo.resolution.height));

                    if (vo.bitrates != null)
                        foreach (var b in vo.bitrates) result.VideoBitrates.Add(b + "k");

                    if (vo.frameRates != null)
                        foreach (var f in vo.frameRates) result.FrameRates.Add(f.ToString());
                }
            }

            if (fmt?.audioOptions != null)
            {
                foreach (var ao in fmt.audioOptions)
                {
                    if (ao == null) continue;
                    result.AudioCodecs.Add(FourCCToAudioCodec(ao.codec));

                    if (ao.sampleRates != null)
                        foreach (var s in ao.sampleRates) result.SampleRates.Add(s.ToString());

                    if (ao.bitrates != null)
                        foreach (var b in ao.bitrates) result.AudioBitrates.Add(b + "k");

                    if (ao.channels != null)
                        foreach (var c in ao.channels) result.Channels.Add(c.ToString());
                }
            }

            result.VideoCodecs = result.VideoCodecs.Distinct().ToList();
            result.Resolutions = result.Resolutions.Distinct().ToList();
            result.VideoBitrates = result.VideoBitrates.Distinct().ToList();
            result.FrameRates = result.FrameRates.Distinct().ToList();
            result.AudioCodecs = result.AudioCodecs.Distinct().ToList();
            result.SampleRates = result.SampleRates.Distinct().ToList();
            result.AudioBitrates = result.AudioBitrates.Distinct().ToList();
            result.Channels = result.Channels.Distinct().ToList();

            return result;
        }

        private static string FourCCToVideoCodec(string fourCC, string containerFourCC)
        {
            string f = (fourCC ?? string.Empty).Trim().ToUpperInvariant();
            switch (f)
            {
                case "H264":
                case "B264": return "libx264";
                case "HEVC":
                case "X265": return "libx265";
                case "MP4V": return "mpeg4";
                case "XVID":
                case "DIVX": return "libxvid";
                case "MJPG": return "mjpeg";
                case "AV1": return "libsvtav1";
                case "CFHD": return "cfhd";
                case "FFV1": return "ffv1";
                case "MP43": return "msmpeg4v3";
                case "HAVC": return "libx264";
                case "": return string.IsNullOrEmpty(containerFourCC) ? "libx264" : "copy";
                default: return f.ToLowerInvariant();
            }
        }

        private static string FourCCToAudioCodec(string fourCC)
        {
            string f = (fourCC ?? string.Empty).Trim().ToUpperInvariant();
            switch (f)
            {
                case "AAC":
                case "MAAC": return "aac";
                case "MP3": return "libmp3lame";
                case "AC3": return "ac3";
                case "EAC3": return "eac3";
                case "FLAC": return "flac";
                case "OPUS": return "libopus";
                case "VORBIS": return "libvorbis";
                case "0": return string.Empty;
                case "": return "aac";
                default: return f.ToLowerInvariant();
            }
        }
    }

    #region JSON data contracts

    [DataContract]
    public class PresetsRoot
    {
        [DataMember]
        public Dictionary<string, List<FormatJson>> categories { get; set; }
    }

    [DataContract]
    public class FormatJson
    {
        [DataMember]
        public string id { get; set; }
        [DataMember]
        public string title { get; set; }
        [DataMember]
        public string icon { get; set; }
        [DataMember]
        public string formatId { get; set; }
        [DataMember]
        public List<PresetJson> presets { get; set; }
    }

    [DataContract]
    public class PresetJson
    {
        [DataMember]
        public string id { get; set; }
        [DataMember]
        public string name { get; set; }
        [DataMember]
        public string formatId { get; set; }
        [DataMember]
        public string fourCC { get; set; }
        [DataMember]
        public bool keepSource { get; set; }
        [DataMember]
        public string defaultVideoCodec { get; set; }
        [DataMember]
        public ResolutionJson resolution { get; set; }
        [DataMember]
        public int defaultBitrate { get; set; }
        [DataMember]
        public int defaultFrameRate { get; set; }
        [DataMember]
        public string defaultAudioCodec { get; set; }
        [DataMember]
        public int defaultChannel { get; set; }
        [DataMember]
        public int defaultSampleRate { get; set; }
        [DataMember]
        public int defaultAudioBitrate { get; set; }
    }

    [DataContract]
    public class ResolutionJson
    {
        [DataMember]
        public int width { get; set; }
        [DataMember]
        public int height { get; set; }
    }

    [DataContract]
    public class FormatOptionJson
    {
        [DataMember]
        public string formatId { get; set; }
        [DataMember]
        public string extension { get; set; }
        [DataMember]
        public string fourcc { get; set; }
        [DataMember]
        public List<VideoOptionJson> videoOptions { get; set; }
        [DataMember]
        public List<AudioOptionJson> audioOptions { get; set; }
    }

    [DataContract]
    public class VideoOptionJson
    {
        [DataMember]
        public string codec { get; set; }
        [DataMember]
        public ResolutionJson resolution { get; set; }
        [DataMember]
        public List<int> bitrates { get; set; }
        [DataMember]
        public List<int> frameRates { get; set; }
    }

    [DataContract]
    public class AudioOptionJson
    {
        [DataMember]
        public string codec { get; set; }
        [DataMember]
        public List<int> sampleRates { get; set; }
        [DataMember]
        public List<int> bitrates { get; set; }
        [DataMember]
        public List<int> channels { get; set; }
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

    public class FormatOptions
    {
        public List<string> VideoCodecs { get; set; } = new List<string>();
        public List<string> Resolutions { get; set; } = new List<string>();
        public List<string> VideoBitrates { get; set; } = new List<string>();
        public List<string> FrameRates { get; set; } = new List<string>();
        public List<string> AudioCodecs { get; set; } = new List<string>();
        public List<string> SampleRates { get; set; } = new List<string>();
        public List<string> AudioBitrates { get; set; } = new List<string>();
        public List<string> Channels { get; set; } = new List<string>();
    }
}
