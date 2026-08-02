// ============================================================================
//  AppSettings.cs — Simple JSON-backed settings for VideoConverter.
//  Persists the "高速转换" / "硬件编码" check-box state, the last chosen
//  "转换到" preset and the "保存到" target (folder / same-as-source) across runs.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoConverter
{
    /// <summary>
    /// Tiny settings store saved next to the executable as a JSON file.
    /// Only primitive values are kept so (de)serialization stays dependency-free.
    /// </summary>
    public static class AppSettings
    {
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppDomain.CurrentDomain.BaseDirectory,
            "videoconverter.settings.json");

        public static bool HighSpeed { get; set; }
        public static bool Hardware { get; set; }

        /// <summary>上次选择的「转换到」预设标识（FormatId + Name 联合定位）。</summary>
        public static string ConvertToFormatId { get; set; }
        public static string ConvertToPresetName { get; set; }

        /// <summary>上次选择的「保存到」值："与源文件夹相同" 或具体目录路径。</summary>
        public static string SaveToValue { get; set; }

        /// <summary>曾经选择过的输出目录历史（追加到下拉项中）。</summary>
        public static List<string> SaveToFolders { get; set; } = new List<string>();

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);

                var m = Regex.Match(json, "\"HighSpeed\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (m.Success) HighSpeed = string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                m = Regex.Match(json, "\"Hardware\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (m.Success) Hardware = string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);

                ConvertToFormatId = ReadString(json, "ConvertToFormatId");
                ConvertToPresetName = ReadString(json, "ConvertToPresetName");
                SaveToValue = ReadString(json, "SaveToValue");

                var fm = Regex.Match(json, "\"SaveToFolders\"\\s*:\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                SaveToFolders = fm.Success
                    ? fm.Groups[1].Value
                        .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Replace("\\\"", "\"").Replace("\\\\", "\\"))
                        .ToList()
                    : new List<string>();
            }
            catch
            {
                // Corrupt or unreadable settings: fall back to defaults.
                HighSpeed = false;
                Hardware = false;
                ConvertToFormatId = null;
                ConvertToPresetName = null;
                SaveToValue = null;
                SaveToFolders = new List<string>();
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"HighSpeed\": ").Append(HighSpeed ? "true" : "false").Append(",\n");
                sb.Append("  \"Hardware\": ").Append(Hardware ? "true" : "false").Append(",\n");
                sb.Append("  \"ConvertToFormatId\": \"").Append(Escape(ConvertToFormatId)).Append("\",\n");
                sb.Append("  \"ConvertToPresetName\": \"").Append(Escape(ConvertToPresetName)).Append("\",\n");
                sb.Append("  \"SaveToValue\": \"").Append(Escape(SaveToValue)).Append("\",\n");
                sb.Append("  \"SaveToFolders\": \"").Append(string.Join("\n", SaveToFolders.Select(Escape))).Append("\"\n");
                sb.Append("}");
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Best-effort persistence; ignore write failures.
            }
        }

        private static string ReadString(string json, string key)
        {
            var m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
