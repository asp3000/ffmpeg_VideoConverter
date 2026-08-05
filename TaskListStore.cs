// ============================================================================
//  TaskListStore.cs — Persists the current file list across runs.
//
//  On exit the main window serializes every ConversionTask (its preset
//  selection, output settings, trim/crop, and selected tracks) to
//  "videoconverter.tasks.json" next to the executable. On the next launch
//  the list is reloaded and each entry is re-probed with ffprobe so that
//  media metadata, audio/subtitle tracks and the thumbnail are fresh.
//
//  Pure framework dependency (System.Runtime.Serialization.Json), no 3rd
//  party JSON library required.
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
    /// Serializes / deserializes the file list to a standalone JSON config file.
    /// </summary>
    public static class TaskListStore
    {
        private static readonly string FilePath = Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? AppDomain.CurrentDomain.BaseDirectory,
            "videoconverter.tasks.json");

        // ---- DTO contracts --------------------------------------------------

        [DataContract]
        public class TaskListDto
        {
            [DataMember(Name = "version")] public int Version = 3;
            [DataMember(Name = "tasks")] public List<TaskDto> Tasks = new List<TaskDto>();
        }

        [DataContract]
        public class TaskDto
        {
            [DataMember(Name = "input")] public string InputPath;
            [DataMember(Name = "output")] public string OutputPath;
            [DataMember(Name = "customName")] public string CustomOutputName;
            [DataMember(Name = "saveTo")] public string SaveToFolder;
            /// <summary>"Pending" or "Completed". Converting/Failed are saved as Pending.</summary>
            [DataMember(Name = "status")] public string Status;
            [DataMember(Name = "preset")] public PresetDto Preset;
            [DataMember(Name = "hw")] public string HardwareEncoder;
            [DataMember(Name = "useCopy")] public bool UseStreamCopy;
            [DataMember(Name = "audioIndex")] public int AudioIndex = -1;
            [DataMember(Name = "subIndex")] public int SubIndex = -1;
            [DataMember(Name = "crop")] public CropDto Crop;
            [DataMember(Name = "rotation")] public int Rotation;
            [DataMember(Name = "merge")] public bool MergeSegments = true;
            [DataMember(Name = "segments")] public List<SegDto> Segments;

            // ---- P2 高级编码与元数据（v2 新增；旧文件缺失时使用类型默认值，向后兼容） ----
            [DataMember(Name = "twoPass")] public bool TwoPass;
            [DataMember(Name = "lossless")] public bool Lossless;
            [DataMember(Name = "deinterlace")] public bool Deinterlace;
            [DataMember(Name = "h264Profile")] public string H264Profile;
            [DataMember(Name = "h264Level")] public string H264Level;
            [DataMember(Name = "subtitleStyle")] public string SubtitleStyle;
            [DataMember(Name = "burnSubtitle")] public bool BurnSubtitle;
            // v3 字幕模式（None/SoftKeepAll/BurnExternal）+ 完整样式
            [DataMember(Name = "subMode")] public string SubMode;
            [DataMember(Name = "subSettings")] public SubtitleSettingsDto SubSettings;
            [DataMember(Name = "metaTitle")] public string MetaTitle;
            [DataMember(Name = "metaAuthor")] public string MetaAuthor;
            [DataMember(Name = "metaYear")] public string MetaYear;
            [DataMember(Name = "metaComment")] public string MetaComment;
        }

        [DataContract]
        public class PresetDto
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "format")] public string FormatName;
            [DataMember(Name = "ext")] public string Extension;
            [DataMember(Name = "vcodec")] public string VideoCodec;
            [DataMember(Name = "vcodecLabel")] public string VideoCodecLabel;
            [DataMember(Name = "acodec")] public string AudioCodec;
            [DataMember(Name = "customArgs")] public string CustomArgs;
            [DataMember(Name = "resLabel")] public string ResolutionLabel;
            [DataMember(Name = "resValue")] public string ResolutionValue;
            [DataMember(Name = "vbr")] public string VideoBitrate;
            [DataMember(Name = "bitrateMode")] public string BitrateMode;
            [DataMember(Name = "quality")] public int QualityValue;
            [DataMember(Name = "maxRate")] public string QualityMaxRate;
            [DataMember(Name = "abr")] public string AudioBitrate;
            [DataMember(Name = "fps")] public string FrameRate;
            [DataMember(Name = "category")] public string Category;
            [DataMember(Name = "presetId")] public string PresetId;
            [DataMember(Name = "formatId")] public string FormatId;
            [DataMember(Name = "fourcc")] public string FourCC;
            [DataMember(Name = "keepSource")] public bool KeepSource;
            [DataMember(Name = "sampleRate")] public string SampleRate;
            [DataMember(Name = "channels")] public int Channels;
            [DataMember(Name = "isBuiltIn")] public bool IsBuiltIn;
        }

        [DataContract]
        public class CropDto
        {
            [DataMember(Name = "x")] public int X;
            [DataMember(Name = "y")] public int Y;
            [DataMember(Name = "w")] public int Width;
            [DataMember(Name = "h")] public int Height;
        }

        [DataContract]
        public class SegDto
        {
            [DataMember(Name = "start")] public long StartMs;
            [DataMember(Name = "end")] public long EndMs;
        }

        /// <summary>字幕样式 DTO。字段名与 SubtitleSettings 属性名一致，DataContractJsonSerializer 双向兼容。</summary>
        [DataContract]
        public class SubtitleSettingsDto
        {
            [DataMember(Name = "fontName")] public string FontName;
            [DataMember(Name = "fontSize")] public int FontSize;
            [DataMember(Name = "fontColorArgb")] public int FontColorArgb;
            [DataMember(Name = "bold")] public bool Bold;
            [DataMember(Name = "italic")] public bool Italic;
            [DataMember(Name = "underline")] public bool Underline;
            [DataMember(Name = "outlineWidth")] public int OutlineWidth;
            [DataMember(Name = "outlineColorArgb")] public int OutlineColorArgb;
            [DataMember(Name = "transparency")] public int Transparency;
            [DataMember(Name = "backEnabled")] public bool BackEnabled;
            [DataMember(Name = "backColorArgb")] public int BackColorArgb;
            [DataMember(Name = "backAlpha")] public int BackAlpha;
            [DataMember(Name = "alignment")] public int Alignment;
            [DataMember(Name = "marginV")] public int MarginV;
            [DataMember(Name = "externalSubPath")] public string ExternalSubPath;
        }

        // ---- public API ----------------------------------------------------

        public static void Save(List<TaskDto> dtos)
        {
            try
            {
                if (dtos == null) dtos = new List<TaskDto>();
                var list = new TaskListDto { Tasks = dtos };
                var ser = new DataContractJsonSerializer(typeof(TaskListDto));
                using (var ms = new MemoryStream())
                {
                    ser.WriteObject(ms, list);
                    string json = Encoding.UTF8.GetString(ms.ToArray());
                    string dir = Path.GetDirectoryName(FilePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(FilePath, json, Encoding.UTF8);
                }
            }
            catch
            {
                // Best-effort persistence; ignore write failures.
            }
        }

        public static List<TaskDto> Load()
        {
            var result = new List<TaskDto>();
            try
            {
                if (!File.Exists(FilePath)) return result;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var ser = new DataContractJsonSerializer(typeof(TaskListDto));
                    var dto = (TaskListDto)ser.ReadObject(ms);
                    if (dto?.Tasks != null) result = dto.Tasks;
                }
            }
            catch
            {
                // Corrupt or unreadable store: start empty.
            }
            return result;
        }
    }
}
