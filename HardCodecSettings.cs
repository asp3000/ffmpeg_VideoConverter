// ============================================================================
//  HardCodecSettings.cs — 硬件编码配置（hard_codec_settings.json）。
//  按界面上显示的编码 label（如 "H.264"）索引，列出该编码对应的 CPU 编码器
//  与各厂商（NVIDIA/Intel/AMD）的硬件编码器。用途：
//   ① 勾选「硬件编码」时，按此配置把 label 解析为对应 GPU 编码器（否则用 CPU）；
//   ② 硬件编码运行时失败（如 -c:v h264_nvenc 报错），自动降级为 CPU 编码器重试。
//  配置文件与程序同目录，可手工修改；缺失时用内置默认并自动生成文件。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace VideoConverter
{
    public static class HardCodecSettings
    {
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "hard_codec_settings.json");

        private sealed class Entry
        {
            public string Label;
            public List<string> CpuEncoders = new List<string>();
            public List<HwEncoderJson> HardwareEncoders = new List<HwEncoderJson>();
        }

        // label（界面显示名，如 "H.264"）→ 配置项
        private static readonly Dictionary<string, Entry> ByLabel =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // CPU 编码器名（如 "libx264"）→ 配置项（反向索引，便于按预设 fourCC 命中）
        private static readonly Dictionary<string, Entry> ByCpu =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static bool _loaded;
        public static bool Loaded { get { return _loaded; } }

        public static void EnsureLoaded()
        {
            if (_loaded) return;
            Load();
        }

        public static void Load()
        {
            List<HardCodecEntryJson> entries = null;
            try
            {
                if (File.Exists(FilePath))
                {
                    using (var fs = File.OpenRead(FilePath))
                    {
                        var ser = new DataContractJsonSerializer(typeof(HardCodecSettingsJson));
                        var root = ser.ReadObject(fs) as HardCodecSettingsJson;
                        entries = root?.hardwareCodecs;
                    }
                }
            }
            catch { entries = null; }

            if (entries == null)
            {
                entries = new List<HardCodecEntryJson>(BuildBuiltIn());
                try { WriteJson(new HardCodecSettingsJson { hardwareCodecs = entries }); }
                catch { }
            }

            ByLabel.Clear();
            ByCpu.Clear();
            foreach (var e in entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.label)) continue;
                var entry = new Entry { Label = e.label };
                if (e.cpuEncoders != null)
                    foreach (var c in e.cpuEncoders)
                        if (!string.IsNullOrWhiteSpace(c) && !entry.CpuEncoders.Contains(c))
                            entry.CpuEncoders.Add(c);
                if (e.hardwareEncoders != null)
                    foreach (var h in e.hardwareEncoders)
                        if (h != null && !string.IsNullOrWhiteSpace(h.encoder))
                            entry.HardwareEncoders.Add(h);
                ByLabel[e.label] = entry;
                foreach (var c in entry.CpuEncoders)
                    if (!ByCpu.ContainsKey(c)) ByCpu[c] = entry;
            }
            _loaded = true;
        }

        // ---- queries -------------------------------------------------------

        /// <summary>该 label 是否支持硬件编码（配置中存在且至少列出一个硬件编码器）。</summary>
        public static bool IsHardwareCapable(string label)
        {
            Entry e;
            return !string.IsNullOrWhiteSpace(label)
                && ByLabel.TryGetValue(label, out e)
                && e.HardwareEncoders.Count > 0;
        }

        /// <summary>该 label 的主 CPU 编码器（用于降级重试），未配置返回 null。</summary>
        public static string GetCpuEncoder(string label)
        {
            Entry e;
            if (string.IsNullOrWhiteSpace(label) || !ByLabel.TryGetValue(label, out e)) return null;
            return e.CpuEncoders.Count > 0 ? e.CpuEncoders[0] : null;
        }

        /// <summary>
        /// 按 label 与检测到的硬件支持，返回应使用的编码器：
        /// 硬件勾选且当前机器支持 → 对应厂商的 GPU 编码器；否则 → CPU 编码器。
        /// 同时输出 cpuEncoder（降级用）。label 未知时两者均为 null。
        /// </summary>
        public static void Resolve(string label, FFmpegHelper.HardwareSupport hw,
            out string resolved, out string cpuEncoder)
        {
            resolved = null;
            cpuEncoder = null;
            Entry e;
            if (string.IsNullOrWhiteSpace(label) || !ByLabel.TryGetValue(label, out e)) return;

            cpuEncoder = e.CpuEncoders.Count > 0 ? e.CpuEncoders[0] : null;

            if (hw != null && hw.Any)
            {
                foreach (var h in e.HardwareEncoders)
                {
                    if (string.Equals(h.vendor, "NVIDIA", StringComparison.OrdinalIgnoreCase) && hw.Nvidia) { resolved = h.encoder; return; }
                    if (string.Equals(h.vendor, "Intel", StringComparison.OrdinalIgnoreCase) && hw.Intel) { resolved = h.encoder; return; }
                    if (string.Equals(h.vendor, "AMD", StringComparison.OrdinalIgnoreCase) && hw.Amd) { resolved = h.encoder; return; }
                }
            }
            resolved = cpuEncoder;
        }

        /// <summary>判断一个实际 ffmpeg 编码器名是否为硬件（GPU）编码器。</summary>
        public static bool IsHardwareEncoder(string encoder)
        {
            if (string.IsNullOrEmpty(encoder)) return false;
            string e = encoder.ToLowerInvariant();
            return e.Contains("_nvenc") || e.Contains("_qsv") || e.Contains("_amf");
        }

        // ---- built-in fallback (mirrors hard_codec_settings.json) ---------

        private static HardCodecEntryJson[] BuildBuiltIn()
        {
            return new[]
            {
                Hw("H.264", new[] { "libx264" },
                    new[,] { { "NVIDIA", "h264_nvenc" }, { "Intel", "h264_qsv" }, { "AMD", "h264_amf" } }),
                Hw("H.265 (HEVC)", new[] { "libx265" },
                    new[,] { { "NVIDIA", "hevc_nvenc" }, { "Intel", "hevc_qsv" }, { "AMD", "hevc_amf" } }),
                Hw("AV1", new[] { "libsvtav1", "libaom-av1" },
                    new[,] { { "NVIDIA", "av1_nvenc" }, { "Intel", "av1_qsv" }, { "AMD", "av1_amf" } }),
                Hw("VP9", new[] { "libvpx-vp9" },
                    new[,] { { "NVIDIA", "vp9_nvenc" }, { "Intel", "vp9_qsv" } }),
                Hw("MJPEG", new[] { "mjpeg" },
                    new[,] { { "NVIDIA", "mjpeg_nvenc" } }),
                Hw("MPEG-2", new[] { "mpeg2video" },
                    new[,] { { "Intel", "mpeg2_qsv" } })
            };
        }

        private static HardCodecEntryJson Hw(string label, string[] cpus, string[,] hw)
        {
            var e = new HardCodecEntryJson { label = label };
            e.cpuEncoders = new List<string>(cpus);
            int rows = hw.GetLength(0);
            e.hardwareEncoders = new List<HwEncoderJson>();
            for (int i = 0; i < rows; i++)
                e.hardwareEncoders.Add(new HwEncoderJson { vendor = hw[i, 0], encoder = hw[i, 1] });
            return e;
        }

        // ---- JSON IO -------------------------------------------------------

        private static void WriteJson(HardCodecSettingsJson obj)
        {
            using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
            {
                var ser = new DataContractJsonSerializer(typeof(HardCodecSettingsJson));
                ser.WriteObject(fs, obj);
            }
        }
    }

    // ---- JSON contracts ----------------------------------------------------

    [DataContract]
    public class HwEncoderJson
    {
        [DataMember] public string vendor;
        [DataMember] public string encoder;
    }

    [DataContract]
    public class HardCodecEntryJson
    {
        [DataMember] public string label;
        [DataMember] public List<string> cpuEncoders;
        [DataMember] public List<HwEncoderJson> hardwareEncoders;
    }

    [DataContract]
    public class HardCodecSettingsJson
    {
        [DataMember] public List<HardCodecEntryJson> hardwareCodecs;
    }
}
