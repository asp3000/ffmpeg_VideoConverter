﻿// ============================================================================
//  ArgumentBuilderTests.cs — Unit tests for FFmpegHelper argument building:
//  cutting (trim), cropping, conversion, and multi-segment merge.
// ============================================================================

using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VideoConverter;

namespace VideoConverter.Tests
{
    [TestClass]
    public class ArgumentBuilderTests
    {
        private const string InputFile = @"C:\test\input.mp4";
        private const string OutputFile = @"C:\test\output.mp4";

        private static ConversionTask CreateBaseTask()
        {
            var task = new ConversionTask
            {
                InputPath = InputFile,
                OutputPath = OutputFile,
                SourceDurationSeconds = 60.0,
                SourceVideoCodec = "h264",
                SourceAudioCodec = "aac",
                TargetVideoEncoder = "libx264",
                TargetAudioEncoder = "aac",
                SelectedAudioTrack = new AudioTrackInfo { Index = 0, Codec = "aac" }
            };
            task.Preset = PresetOption.MP4_1080.Clone();
            return task;
        }

        // ---- Cutting (trim) ----

        [TestMethod]
        public void BuildArguments_SingleSegment_IncludesSeekAndDuration()
        {
            var task = CreateBaseTask();
            task.Segments = new System.Collections.Generic.List<VideoSegment>
            {
                new VideoSegment { StartMs = 5000, EndMs = 15000 }
            };

            string args = FFmpegHelper.BuildArguments(task);

            // -ss before -i for fast seek.
            Assert.IsTrue(args.Contains("-ss "), "Expected -ss for seek start.");
            // -t for duration (10 seconds).
            Assert.IsTrue(args.Contains("-t "), "Expected -t for duration.");
            // Output path included.
            Assert.IsTrue(args.Contains(OutputFile), "Expected output path in args.");
        }

        [TestMethod]
        public void BuildArguments_NoSegments_NoSeek()
        {
            var task = CreateBaseTask();
            // No segments → no -ss / -t.

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsFalse(args.Contains("-ss "), "Should not have -ss without segments.");
        }

        // ---- Cropping ----

        [TestMethod]
        public void BuildArguments_WithCrop_ContainsCropFilter()
        {
            var task = CreateBaseTask();
            task.Crop = new CropRegion { X = 10, Y = 20, Width = 640, Height = 360 };

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("crop=640:360:10:20"),
                "Expected crop filter in argument string.");
        }

        [TestMethod]
        public void BuildArguments_WithRotation_ContainsTranspose()
        {
            var task = CreateBaseTask();
            task.Rotation = 1; // 90° clockwise

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("transpose=1"),
                "Expected transpose=1 for 90° clockwise rotation.");
        }

        // ---- Conversion (basic) ----

        [TestMethod]
        public void BuildArguments_BasicConversion_ContainsInputAndOutput()
        {
            var task = CreateBaseTask();

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("-i \"" + InputFile + "\""),
                "Expected input file in -i argument.");
            Assert.IsTrue(args.Contains("\"" + OutputFile + "\""),
                "Expected output file at end of args.");
            Assert.IsTrue(args.Contains("-y"),
                "Expected -y for overwrite.");
        }

        [TestMethod]
        public void BuildArguments_ResolutionScaling_IncludesScaleFilter()
        {
            var task = CreateBaseTask();
            task.Preset.ResolutionValue = "1280x720";

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("scale="),
                "Expected scale filter when resolution is set.");
        }

        // ---- Smart stream copy ----

        [TestMethod]
        public void BuildArguments_HighSpeedMode_UsesStreamCopy()
        {
            var task = CreateBaseTask();
            task.UseStreamCopy = true;
            // Source and target codec match (both h264).
            task.SourceVideoCodec = "h264";
            task.TargetVideoEncoder = "libx264";
            task.Preset.VideoBitrate = null;
            task.Preset.BitrateMode = null;

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("-c:v copy"),
                "Expected -c:v copy in high-speed mode with matching codecs.");
        }

        [TestMethod]
        public void BuildArguments_HighSpeedMode_WithCrop_ForciblyReencodes()
        {
            var task = CreateBaseTask();
            task.UseStreamCopy = true;
            task.Crop = new CropRegion { X = 0, Y = 0, Width = 100, Height = 100 };

            string args = FFmpegHelper.BuildArguments(task);

            // Stream copy is disabled when video filters (crop) are present.
            Assert.IsFalse(args.Contains("-c:v copy"),
                "Should NOT use -c:v copy when crop is active.");
        }

        // ---- Multi-segment merge (concat demuxer) ----

        [TestMethod]
        public void BuildMergedArguments_UsesConcatDemuxer()
        {
            var task = CreateBaseTask();
            task.Segments = new System.Collections.Generic.List<VideoSegment>
            {
                new VideoSegment { StartMs = 0, EndMs = 10000 },
                new VideoSegment { StartMs = 20000, EndMs = 30000 }
            };
            task.MergeSegments = true;
            task.UseStreamCopy = false;

            string concatList = Path.Combine(Path.GetTempPath(), "test_concat_list.txt");
            string args = FFmpegHelper.BuildMergedArguments(task, concatList, OutputFile);

            Assert.IsTrue(args.Contains("-f concat"),
                "Expected -f concat for merge.");
            Assert.IsTrue(args.Contains("-safe 0"),
                "Expected -safe 0 for concat demuxer.");
            Assert.IsTrue(args.Contains(concatList),
                "Expected concat list path in args.");
        }

        [TestMethod]
        public void WriteConcatList_CreatesValidConcatList()
        {
            var task = CreateBaseTask();
            task.Segments = new System.Collections.Generic.List<VideoSegment>
            {
                new VideoSegment { StartMs = 1000, EndMs = 5000 },
                new VideoSegment { StartMs = 10000, EndMs = 20000 }
            };

            string tempList = Path.Combine(Path.GetTempPath(),
                "vc_test_concat_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                FFmpegHelper.WriteConcatList(task, tempList);
                string content = File.ReadAllText(tempList);

                // Should reference the input file twice (one per segment).
                int fileCount = Regex.Matches(content, @"^file '", RegexOptions.Multiline).Count;
                Assert.AreEqual(2, fileCount, "Expected 2 file entries for 2 segments.");

                // Should have inpoint/outpoint for each.
                Assert.IsTrue(content.Contains("inpoint 1.000"), "Expected inpoint 1.000 for first segment.");
                Assert.IsTrue(content.Contains("outpoint 5.000"), "Expected outpoint 5.000 for first segment.");
                Assert.IsTrue(content.Contains("inpoint 10.000"), "Expected inpoint 10.000 for second segment.");
                Assert.IsTrue(content.Contains("outpoint 20.000"), "Expected outpoint 20.000 for second segment.");
            }
            finally
            {
                if (File.Exists(tempList)) File.Delete(tempList);
            }
        }

        [TestMethod]
        public void BuildMergedArguments_HighSpeedMode_UsesSmartCopy()
        {
            var task = CreateBaseTask();
            task.Segments = new System.Collections.Generic.List<VideoSegment>
            {
                new VideoSegment { StartMs = 0, EndMs = 5000 },
                new VideoSegment { StartMs = 10000, EndMs = 15000 }
            };
            task.MergeSegments = true;
            task.UseStreamCopy = true;
            task.Crop = null;
            task.Rotation = 0;
            // Matching codecs for copy.
            task.SourceVideoCodec = "h264";
            task.TargetVideoEncoder = "libx264";
            task.Preset.VideoBitrate = null;
            task.Preset.BitrateMode = null;

            string concatList = Path.Combine(Path.GetTempPath(), "test_concat_list2.txt");
            string args = FFmpegHelper.BuildMergedArguments(task, concatList, OutputFile);

            Assert.IsTrue(args.Contains("-c:v copy"),
                "Expected -c:v copy in high-speed merge mode with matching codecs.");
        }

        // ---- P2: Deinterlace ----

        [TestMethod]
        public void BuildArguments_Deinterlace_ContainsYadifFilter()
        {
            var task = CreateBaseTask();
            task.Deinterlace = true;

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("yadif="),
                "Expected yadif filter when Deinterlace is enabled.");
            Assert.IsFalse(task.UseStreamCopy || !FFmpegHelper.HasVideoFilters(task),
                "Deinterlace must force transcode (HasVideoFilters=true).");
        }

        // ---- P2: H264 Profile / Level ----

        [TestMethod]
        public void BuildArguments_H264Profile_ContainsProfileAndLevel()
        {
            var task = CreateBaseTask();
            task.H264Profile = "high";
            task.H264Level = "4.1";

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("-profile:v high"),
                "Expected -profile:v high in args.");
            Assert.IsTrue(args.Contains("-level 4.1"),
                "Expected -level 4.1 in args.");
        }

        // ---- P2: Lossless ----

        [TestMethod]
        public void BuildArguments_Lossless_SoftwareEncoder_UsesCrfZero()
        {
            var task = CreateBaseTask();
            task.TargetVideoEncoder = "libx264";
            task.Lossless = true;

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("-crf 0"),
                "Expected -crf 0 for lossless software (x264) encoding.");
        }

        [TestMethod]
        public void BuildArguments_Lossless_HardwareEncoder_UsesLosslessFlag()
        {
            var task = CreateBaseTask();
            task.TargetVideoEncoder = "h264_nvenc";
            task.HardwareEncoder = "h264_nvenc";
            task.Lossless = true;

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("-lossless 1"),
                "Expected -lossless 1 for NVENC lossless encoding.");
        }

        // ---- P2: Metadata ----

        [TestMethod]
        public void BuildArguments_Metadata_ContainsMetadataFlags()
        {
            var task = CreateBaseTask();
            task.MetaTitle = "My Title";
            task.MetaAuthor = "Tester";
            task.MetaYear = "2026";
            task.MetaComment = "Hello";

            string args = FFmpegHelper.BuildArguments(task);

            Assert.IsTrue(args.Contains("-metadata title="),
                "Expected -metadata title= in args.");
            Assert.IsTrue(args.Contains("-metadata artist="),
                "Expected -metadata artist= in args.");
            Assert.IsTrue(args.Contains("-metadata date="),
                "Expected -metadata date= in args.");
            Assert.IsTrue(args.Contains("-metadata comment="),
                "Expected -metadata comment= in args.");
        }

        // ---- P2: Subtitle burn filter ----

        [TestMethod]
        public void BuildSubtitleBurnFilter_Disabled_ReturnsNull()
        {
            var task = CreateBaseTask();
            task.BurnSubtitle = false;

            string filter = FFmpegHelper.BuildSubtitleBurnFilter(task);

            Assert.IsNull(filter, "Expected null when BurnSubtitle is false.");
        }

        [TestMethod]
        public void BuildSubtitleBurnFilter_InternalSubtitle_ContainsSubtitlesFilter()
        {
            var task = CreateBaseTask();
            task.BurnSubtitle = true;
            task.SelectedSubtitleTrack = new SubtitleTrackInfo { Index = 2, IsExternal = false };

            string filter = FFmpegHelper.BuildSubtitleBurnFilter(task);

            Assert.IsNotNull(filter, "Expected non-null filter for burn mode.");
            Assert.IsTrue(filter.StartsWith("subtitles="),
                "Expected subtitles= filter prefix.");
            Assert.IsTrue(filter.Contains(":si=2"),
                "Expected :si=2 stream index for internal subtitle.");
        }

        [TestMethod]
        public void BuildSubtitleBurnFilter_ExternalSubtitle_ContainsFilePath()
        {
            var task = CreateBaseTask();
            task.BurnSubtitle = true;
            task.SelectedSubtitleTrack = new SubtitleTrackInfo
            {
                Index = 0,
                IsExternal = true,
                FilePath = @"C:\subs\demo.srt"
            };

            string filter = FFmpegHelper.BuildSubtitleBurnFilter(task);

            Assert.IsNotNull(filter, "Expected non-null filter for external subtitle.");
            Assert.IsTrue(filter.Contains("demo.srt"),
                "Expected external subtitle file path in filter.");
        }

        [TestMethod]
        public void BuildSubtitleBurnFilter_WithStyle_ContainsForceStyle()
        {
            var task = CreateBaseTask();
            task.BurnSubtitle = true;
            task.SubtitleStyle = "FontSize=24,PrimaryColour=&H00FFFFFF";
            task.SelectedSubtitleTrack = new SubtitleTrackInfo { Index = 0, IsExternal = false };

            string filter = FFmpegHelper.BuildSubtitleBurnFilter(task);

            Assert.IsNotNull(filter, "Expected non-null filter with style.");
            Assert.IsTrue(filter.Contains("force_style="),
                "Expected force_style= in filter when SubtitleStyle set.");
        }

        // ---- P2: Two-pass encoding ----

        [TestMethod]
        public void BuildTwoPassArguments_Disabled_ReturnsNull()
        {
            var task = CreateBaseTask();
            task.TwoPass = false;

            var result = FFmpegHelper.BuildTwoPassArguments(task, null, OutputFile);

            Assert.IsNull(result, "Expected null when TwoPass is false.");
        }

        [TestMethod]
        public void BuildTwoPassArguments_StreamCopy_ReturnsNull()
        {
            var task = CreateBaseTask();
            task.TwoPass = true;
            task.UseStreamCopy = true;

            var result = FFmpegHelper.BuildTwoPassArguments(task, null, OutputFile);

            Assert.IsNull(result, "Expected null when UseStreamCopy is true (two-pass unsupported).");
        }

        [TestMethod]
        public void BuildTwoPassArguments_Enabled_ReturnsTwoPassesAndLogFile()
        {
            var task = CreateBaseTask();
            task.TwoPass = true;
            task.UseStreamCopy = false;
            var seg = new VideoSegment { StartMs = 0, EndMs = 10000 };

            var result = FFmpegHelper.BuildTwoPassArguments(task, seg, OutputFile);

            Assert.IsNotNull(result, "Expected non-null result for two-pass.");
            Assert.AreEqual(3, result.Count, "Expected [args1, args2, passlogfile].");
            Assert.IsTrue(result[0].Contains("-pass 1"),
                "First pass must contain -pass 1.");
            Assert.IsTrue(result[0].Contains("-an"),
                "First pass must contain -an (no audio).");
            Assert.IsTrue(result[0].Contains("NUL"),
                "First pass must output to NUL.");
            Assert.IsTrue(result[1].Contains("-pass 2"),
                "Second pass must contain -pass 2.");
            Assert.IsTrue(result[2].Contains("vc_2pass_"),
                "Third element must be the passlogfile path.");
        }

        // ---- P2 持久化往返（TaskListStore） ----

        /// <summary>
        /// 通过反射把 TaskListStore.FilePath 重定向到临时文件，避免污染真实配置。
        /// 返回原值，调用方用 finally 恢复。
        /// </summary>
        private static string RedirectTaskStorePath(string tempPath)
        {
            var field = typeof(TaskListStore).GetField("FilePath",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            string original = (string)field.GetValue(null);
            field.SetValue(null, tempPath);
            return original;
        }

        [TestMethod]
        public void TaskListStore_SaveLoad_PreservesP2Fields()
        {
            var dto = new TaskListStore.TaskDto
            {
                InputPath = @"C:\in.mp4",
                OutputPath = @"C:\out.mp4",
                TwoPass = true,
                Lossless = true,
                Deinterlace = true,
                H264Profile = "high",
                H264Level = "4.1",
                SubtitleStyle = "FontSize=24",
                BurnSubtitle = true,
                MetaTitle = "Title",
                MetaAuthor = "Author",
                MetaYear = "2026",
                MetaComment = "Comment"
            };

            string tempFile = Path.Combine(Path.GetTempPath(),
                "vc_test_store_" + Guid.NewGuid().ToString("N") + ".json");
            string original = RedirectTaskStorePath(tempFile);
            try
            {
                TaskListStore.Save(new System.Collections.Generic.List<TaskListStore.TaskDto> { dto });
                var loaded = TaskListStore.Load();

                Assert.AreEqual(1, loaded.Count, "Expected 1 task after round-trip.");
                var r = loaded[0];
                Assert.IsTrue(r.TwoPass, "TwoPass should survive round-trip.");
                Assert.IsTrue(r.Lossless, "Lossless should survive round-trip.");
                Assert.IsTrue(r.Deinterlace, "Deinterlace should survive round-trip.");
                Assert.AreEqual("high", r.H264Profile, "H264Profile mismatch.");
                Assert.AreEqual("4.1", r.H264Level, "H264Level mismatch.");
                Assert.AreEqual("FontSize=24", r.SubtitleStyle, "SubtitleStyle mismatch.");
                Assert.IsTrue(r.BurnSubtitle, "BurnSubtitle should survive round-trip.");
                Assert.AreEqual("Title", r.MetaTitle, "MetaTitle mismatch.");
                Assert.AreEqual("Author", r.MetaAuthor, "MetaAuthor mismatch.");
                Assert.AreEqual("2026", r.MetaYear, "MetaYear mismatch.");
                Assert.AreEqual("Comment", r.MetaComment, "MetaComment mismatch.");
            }
            finally
            {
                RedirectTaskStorePath(original);
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        [TestMethod]
        public void TaskListStore_Load_V1FileMissingP2Fields_DefaultsSafely()
        {
            // 模拟旧 v1 文件：不含任何 P2 字段。反序列化后 P2 应取类型默认值（false/null）。
            string v1Json =
                "{\"version\":1,\"tasks\":[{\"input\":\"C\\\\old.mp4\"," +
                "\"output\":\"C\\\\old_out.mp4\"," +
                "\"useCopy\":true,\"merge\":true}]}";

            string tempFile = Path.Combine(Path.GetTempPath(),
                "vc_test_v1_" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(tempFile, v1Json, System.Text.Encoding.UTF8);

            string original = RedirectTaskStorePath(tempFile);
            try
            {
                var loaded = TaskListStore.Load();
                Assert.AreEqual(1, loaded.Count, "Expected 1 task from v1 file.");
                var r = loaded[0];
                // 旧文件无 P2 字段 → 默认值，不抛异常。
                Assert.IsFalse(r.TwoPass, "TwoPass default should be false.");
                Assert.IsFalse(r.Lossless, "Lossless default should be false.");
                Assert.IsFalse(r.Deinterlace, "Deinterlace default should be false.");
                Assert.IsFalse(r.BurnSubtitle, "BurnSubtitle default should be false.");
                Assert.IsNull(r.H264Profile, "H264Profile default should be null.");
                Assert.IsNull(r.MetaTitle, "MetaTitle default should be null.");
                Assert.IsTrue(r.UseStreamCopy, "UseStreamCopy should be loaded from v1 file.");
            }
            finally
            {
                RedirectTaskStorePath(original);
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        // ---- concat demuxer 单引号转义 ----

        [TestMethod]
        public void WriteConcatList_FilenameWithSingleQuote_IsEscaped()
        {
            // 文件名包含单引号（如 World's）时，concat 列表必须转义为 '\''，否则 ffmpeg 解析失败。
            var task = CreateBaseTask();
            task.InputPath = @"D:\media\World's video.mp4";
            task.Segments = new System.Collections.Generic.List<VideoSegment>
            {
                new VideoSegment { StartMs = 0, EndMs = 5000 }
            };

            string tempFile = Path.Combine(Path.GetTempPath(),
                "vc_concat_test_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                FFmpegHelper.WriteConcatList(task, tempFile);
                string content = File.ReadAllText(tempFile);

                // 转义后应为 file 'D:/media/World'\''s video.mp4'
                Assert.IsTrue(content.Contains("World'\''s video.mp4"),
                    "Single quote in filename must be escaped as '\'': " + content);
                Assert.IsFalse(content.Contains("World's video.mp4"),
                    "Unescaped single quote must not appear in concat list.");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

        [TestMethod]
        public void WriteConcatList_FilenameWithoutSingleQuote_NoEscaping()
        {
            // 无单引号的普通文件名不应被修改。
            var task = CreateBaseTask();
            task.InputPath = @"D:\media\normal_file.mp4";
            task.Segments = new System.Collections.Generic.List<VideoSegment>
            {
                new VideoSegment { StartMs = 0, EndMs = 5000 }
            };

            string tempFile = Path.Combine(Path.GetTempPath(),
                "vc_concat_test_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                FFmpegHelper.WriteConcatList(task, tempFile);
                string content = File.ReadAllText(tempFile);

                Assert.IsTrue(content.Contains("file 'D:/media/normal_file.mp4'"),
                    "Normal filename should appear unchanged in concat list.");
                Assert.IsFalse(content.Contains("\\'"),
                    "No escape sequences should appear for normal filename.");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            }
        }

            }
}