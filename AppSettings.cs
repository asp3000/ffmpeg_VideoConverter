// ============================================================================
//  AppSettings.cs — Simple JSON-backed settings for VideoConverter.
//  Persists the "高速转换" / "硬件编码" check-box state, the last chosen
//  "转换到" preset and the "保存到" target (folder / same-as-source) across runs.
//  使用 DataContractJsonSerializer 进行 (de)serialization，替代旧版 Regex 解析。
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace VideoConverter
{
    /// <summary>
    /// 设置持久化存储，保存在 EXE 同目录的 videoconverter.settings.json。
    /// 仅保存基本类型值，(de)serialization 不依赖第三方库。
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

        /// <summary>是否保留章节标记（单文件保留源章节，合并时以文件名生成章节）。默认 true。</summary>
        public static bool KeepChapterMarkers { get; set; } = true;

        /// <summary>可序列化的设置数据容器。</summary>
        [DataContract]
        private class SettingsData
        {
            [DataMember] public bool HighSpeed { get; set; }
            [DataMember] public bool Hardware { get; set; }
            [DataMember] public string ConvertToFormatId { get; set; }
            [DataMember] public string ConvertToPresetName { get; set; }
            [DataMember] public string SaveToValue { get; set; }
            [DataMember(Name = "SaveToFolders")]
            public List<string> SaveToFolders { get; set; }
            [DataMember] public bool KeepChapterMarkers { get; set; } = true;
        }

        private static readonly DataContractJsonSerializer Serializer =
            new DataContractJsonSerializer(typeof(SettingsData));

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                SettingsData data = null;
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    data = Serializer.ReadObject(ms) as SettingsData;
                }
                if (data != null)
                {
                    HighSpeed = data.HighSpeed;
                    Hardware = data.Hardware;
                    ConvertToFormatId = data.ConvertToFormatId;
                    ConvertToPresetName = data.ConvertToPresetName;
                    SaveToValue = data.SaveToValue;
                    SaveToFolders = data.SaveToFolders ?? new List<string>();
                    KeepChapterMarkers = data.KeepChapterMarkers;
                }
            }
            catch
            {
                // 损坏或不可读的设置文件：回退到默认值。
                HighSpeed = false;
                Hardware = false;
                ConvertToFormatId = null;
                ConvertToPresetName = null;
                SaveToValue = null;
                SaveToFolders = new List<string>();
                KeepChapterMarkers = true;
            }
        }

        public static void Save()
        {
            try
            {
                var data = new SettingsData
                {
                    HighSpeed = HighSpeed,
                    Hardware = Hardware,
                    ConvertToFormatId = ConvertToFormatId,
                    ConvertToPresetName = ConvertToPresetName,
                    SaveToValue = SaveToValue,
                    SaveToFolders = SaveToFolders,
                    KeepChapterMarkers = KeepChapterMarkers
                };
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                using (var ms = new MemoryStream())
                {
                    Serializer.WriteObject(ms, data);
                    File.WriteAllText(FilePath, Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8);
                }
            }
            catch
            {
                // 尽力持久化；写入失败时忽略。
            }
        }
    }
}
