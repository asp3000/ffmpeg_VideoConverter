// ============================================================================
//  AppSettings.cs — Simple JSON-backed settings for VideoConverter.
//  Persists the "高速转换" / "硬件编码" check-box state across runs.
// ============================================================================

using System;
using System.IO;
using System.Text;

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

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                var m = System.Text.RegularExpressions.Regex.Match(json, "\"HighSpeed\"\\s*:\\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) HighSpeed = string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
                m = System.Text.RegularExpressions.Regex.Match(json, "\"Hardware\"\\s*:\\s*(true|false)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) Hardware = string.Equals(m.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                // Corrupt or unreadable settings: fall back to defaults.
                HighSpeed = false;
                Hardware = false;
            }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"HighSpeed\": ").Append(HighSpeed ? "true" : "false").Append(",\n");
                sb.Append("  \"Hardware\": ").Append(Hardware ? "true" : "false").Append("\n");
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
    }
}
