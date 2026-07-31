// ============================================================================
//  PresetDataStore.cs — loads UniConverter-style preset specs from JSON.
//  Data source: options_spec/presets.json + options_spec/format_options.json
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

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

        private static Dictionary<string, object> _presetsRoot;
        private static Dictionary<string, object> _formatOptionsRoot;

        /// <summary>All category names in UI order.</summary>
        public static List<string> Categories { get; private set; } = new List<string>();

        /// <summary>
        /// Each category maps to a list of formats. Each format has id/title/icon/formatId/presets.
        /// </summary>
        public static Dictionary<string, List<FormatEntry>> FormatsByCategory { get; private set; }
            = new Dictionary<string, List<FormatEntry>>();

        public static void Load()
        {
            try
            {
                var serializer = new JavaScriptSerializer();
                if (File.Exists(PresetsPath))
                    _presetsRoot = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(PresetsPath));
                if (File.Exists(FormatOptionsPath))
                    _formatOptionsRoot = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(FormatOptionsPath));
            }
            catch
            {
                _presetsRoot = null;
                _formatOptionsRoot = null;
            }

            BuildIndex();
        }

        private static void BuildIndex()
        {
            FormatsByCategory = new Dictionary<string, List<FormatEntry>>();
            Categories = new List<string>();

            if (_presetsRoot == null || !_presetsRoot.ContainsKey("categories"))
                return;

            var cats = _presetsRoot["categories"] as Dictionary<string, object>;
            if (cats == null) return;

            foreach (var kv in cats)
            {
                string catName = kv.Key;
                var formats = kv.Value as List<object>;
                if (formats == null) continue;

                Categories.Add(catName);
                var list = new List<FormatEntry>();
                foreach (var fobj in formats)
                {
                    var dict = fobj as Dictionary<string, object>;
                    if (dict == null) continue;

                    var entry = new FormatEntry
                    {
                        Id = DictStr(dict, "id"),
                        Title = DictStr(dict, "title"),
                        Icon = DictStr(dict, "icon"),
                        FormatId = DictStr(dict, "formatId"),
                    };

                    var presets = dict.ContainsKey("presets") ? dict["presets"] as List<object> : null;
                    if (presets != null)
                    {
                        foreach (var pobj in presets)
                        {
                            var pdict = pobj as Dictionary<string, object>;
                            if (pdict == null) continue;
                            entry.Presets.Add(ToPresetOption(pdict, entry));
                        }
                    }

                    list.Add(entry);
                }
                FormatsByCategory[catName] = list;
            }
        }

        /// <summary>
        /// Convert a JSON preset node into a PresetOption usable by the converter engine.
        /// </summary>
        private static PresetOption ToPresetOption(Dictionary<string, object> dict, FormatEntry format)
        {
            string name = DictStr(dict, "name");
            string fmtId = DictStr(dict, "formatId");
            bool keep = false;
            if (dict.ContainsKey("keepSource"))
                bool.TryParse(dict["keepSource"].ToString(), out keep);

            string vcodec = DictStr(dict, "defaultVideoCodec");
            string acodec = DictStr(dict, "defaultAudioCodec");
            string fourCC = DictStr(dict, "fourCC");

            // Look up format-specific options to get a nice extension and fallback dropdowns.
            string ext = ".mp4";
            if (_formatOptionsRoot != null && _formatOptionsRoot.ContainsKey(fmtId))
            {
                var fmt = _formatOptionsRoot[fmtId] as Dictionary<string, object>;
                if (fmt != null)
                {
                    string e = DictStr(fmt, "extension");
                    if (!string.IsNullOrEmpty(e)) ext = "." + e;
                }
            }
            else if (!string.IsNullOrEmpty(format.Title))
            {
                ext = "." + format.Title.ToLowerInvariant();
            }

            // Resolution: may be null for keep-source presets.
            int w = 0, h = 0;
            var resObj = dict.ContainsKey("resolution") ? dict["resolution"] : null;
            if (resObj is Dictionary<string, object> res)
            {
                int.TryParse(res["width"].ToString(), out w);
                int.TryParse(res["height"].ToString(), out h);
            }

            int vbr = 0, fps = 0, abr = 0, sr = 0, ch = 0;
            if (dict.ContainsKey("defaultBitrate")) int.TryParse(dict["defaultBitrate"].ToString(), out vbr);
            if (dict.ContainsKey("defaultFrameRate")) int.TryParse(dict["defaultFrameRate"].ToString(), out fps);
            if (dict.ContainsKey("defaultAudioBitrate")) int.TryParse(dict["defaultAudioBitrate"].ToString(), out abr);
            if (dict.ContainsKey("defaultSampleRate")) int.TryParse(dict["defaultSampleRate"].ToString(), out sr);
            if (dict.ContainsKey("defaultChannel")) int.TryParse(dict["defaultChannel"].ToString(), out ch);

            // Keep-source means copy video/audio streams; no explicit parameters.
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
                    PresetId = DictStr(dict, "id"),
                    FormatId = fmtId,
                    FourCC = fourCC,
                    KeepSource = true,
                };
            }

            // Translate common FourCC values to ffmpeg encoder names.
            string ffmpegVideoCodec = FourCCToVideoCodec(vcodec, fourCC);
            string ffmpegAudioCodec = FourCCToAudioCodec(acodec);

            return new PresetOption
            {
                Name = name,
                FormatName = format.Title,
                Extension = ext,
                VideoCodec = ffmpegVideoCodec,
                AudioCodec = ffmpegAudioCodec,
                ResolutionLabel = w > 0 && h > 0 ? string.Format("{0} x {1}", w, h) : "与源文件相同",
                ResolutionValue = w > 0 && h > 0 ? string.Format("{0}x{1}", w, h) : null,
                VideoBitrate = vbr > 0 ? vbr + "k" : null,
                AudioBitrate = abr > 0 ? abr + "k" : null,
                FrameRate = fps > 0 ? fps.ToString() : null,
                SampleRate = sr > 0 ? sr.ToString() : null,
                Channels = ch,
                PresetId = DictStr(dict, "id"),
                FormatId = fmtId,
                FourCC = fourCC,
                KeepSource = false,
            };
        }

        public static FormatEntry FindFormat(string formatId)
        {
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
            if (_formatOptionsRoot == null || !_formatOptionsRoot.ContainsKey(formatId))
                return result;

            var fmt = _formatOptionsRoot[formatId] as Dictionary<string, object>;
            if (fmt == null) return result;

            var videoOptions = fmt.ContainsKey("videoOptions") ? fmt["videoOptions"] as List<object> : null;
            if (videoOptions != null)
            {
                foreach (var vo in videoOptions)
                {
                    var d = vo as Dictionary<string, object>;
                    if (d == null) continue;
                    result.VideoCodecs.Add(FourCCToVideoCodec(DictStr(d, "codec"), DictStr(fmt, "fourcc")));

                    int w = 0, h = 0;
                    var res = d.ContainsKey("resolution") ? d["resolution"] as Dictionary<string, object> : null;
                    if (res != null)
                    {
                        int.TryParse(res["width"].ToString(), out w);
                        int.TryParse(res["height"].ToString(), out h);
                    }
                    if (w > 0 && h > 0)
                        result.Resolutions.Add(string.Format("{0}x{1}", w, h));

                    var brs = d.ContainsKey("bitrates") ? d["bitrates"] as List<object> : null;
                    if (brs != null)
                        foreach (var b in brs) result.VideoBitrates.Add(b.ToString() + "k");

                    var fps = d.ContainsKey("frameRates") ? d["frameRates"] as List<object> : null;
                    if (fps != null)
                        foreach (var f in fps) result.FrameRates.Add(f.ToString());
                }
            }

            var audioOptions = fmt.ContainsKey("audioOptions") ? fmt["audioOptions"] as List<object> : null;
            if (audioOptions != null)
            {
                foreach (var ao in audioOptions)
                {
                    var d = ao as Dictionary<string, object>;
                    if (d == null) continue;
                    result.AudioCodecs.Add(FourCCToAudioCodec(DictStr(d, "codec")));

                    var srs = d.ContainsKey("sampleRates") ? d["sampleRates"] as List<object> : null;
                    if (srs != null)
                        foreach (var s in srs) result.SampleRates.Add(s.ToString());

                    var abrs = d.ContainsKey("bitrates") ? d["bitrates"] as List<object> : null;
                    if (abrs != null)
                        foreach (var b in abrs) result.AudioBitrates.Add(b.ToString() + "k");

                    var chs = d.ContainsKey("channels") ? d["channels"] as List<object> : null;
                    if (chs != null)
                        foreach (var c in chs) result.Channels.Add(c.ToString());
                }
            }

            // De-duplicate while preserving order.
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

        private static string DictStr(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return string.Empty;
            return dict[key].ToString();
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
                case "0": return string.Empty; // no audio
                case "": return "aac";
                default: return f.ToLowerInvariant();
            }
        }
    }

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
