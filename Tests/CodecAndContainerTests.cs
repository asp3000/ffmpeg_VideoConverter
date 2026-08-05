// ============================================================================
//  CodecAndContainerTests.cs — Unit tests for codec normalization and
//  container compatibility checks in FFmpegHelper.
// ============================================================================

using Microsoft.VisualStudio.TestTools.UnitTesting;
using VideoConverter;

namespace VideoConverter.Tests
{
    [TestClass]
    public class CodecAndContainerTests
    {
        // ---- NormalizeVideoCodec ----

        [TestMethod]
        public void NormalizeVideoCodec_H264Variants_AllMapToH264()
        {
            Assert.AreEqual("h264", FFmpegHelper.NormalizeVideoCodec("h264"));
            Assert.AreEqual("h264", FFmpegHelper.NormalizeVideoCodec("H264"));
            Assert.AreEqual("h264", FFmpegHelper.NormalizeVideoCodec("x264"));
            Assert.AreEqual("h264", FFmpegHelper.NormalizeVideoCodec("libx264"));
            Assert.AreEqual("h264", FFmpegHelper.NormalizeVideoCodec("h264_nvenc"));
            Assert.AreEqual("h264", FFmpegHelper.NormalizeVideoCodec("avc1"));
        }

        [TestMethod]
        public void NormalizeVideoCodec_HevcVariants_AllMapToHevc()
        {
            Assert.AreEqual("hevc", FFmpegHelper.NormalizeVideoCodec("hevc"));
            Assert.AreEqual("hevc", FFmpegHelper.NormalizeVideoCodec("x265"));
            Assert.AreEqual("hevc", FFmpegHelper.NormalizeVideoCodec("h265"));
            Assert.AreEqual("hevc", FFmpegHelper.NormalizeVideoCodec("hevc_nvenc"));
            Assert.AreEqual("hevc", FFmpegHelper.NormalizeVideoCodec("hvc1"));
        }

        [TestMethod]
        public void NormalizeVideoCodec_Vp9AndAv1()
        {
            Assert.AreEqual("vp9", FFmpegHelper.NormalizeVideoCodec("vp9"));
            Assert.AreEqual("av1", FFmpegHelper.NormalizeVideoCodec("av1"));
            Assert.AreEqual("av1", FFmpegHelper.NormalizeVideoCodec("libaom-av1"));
            Assert.AreEqual("av1", FFmpegHelper.NormalizeVideoCodec("svt-av1"));
        }

        [TestMethod]
        public void NormalizeVideoCodec_Copy_ReturnsNull()
        {
            Assert.IsNull(FFmpegHelper.NormalizeVideoCodec("copy"));
            Assert.IsNull(FFmpegHelper.NormalizeVideoCodec(""));
            Assert.IsNull(FFmpegHelper.NormalizeVideoCodec(null));
        }

        // ---- NormalizeAudioCodec ----

        [TestMethod]
        public void NormalizeAudioCodec_CommonCodecs()
        {
            Assert.AreEqual("aac", FFmpegHelper.NormalizeAudioCodec("aac"));
            Assert.AreEqual("mp3", FFmpegHelper.NormalizeAudioCodec("mp3"));
            Assert.AreEqual("mp3", FFmpegHelper.NormalizeAudioCodec("libmp3lame"));
            Assert.AreEqual("opus", FFmpegHelper.NormalizeAudioCodec("opus"));
            Assert.AreEqual("vorbis", FFmpegHelper.NormalizeAudioCodec("vorbis"));
            Assert.AreEqual("flac", FFmpegHelper.NormalizeAudioCodec("flac"));
            Assert.AreEqual("ac3", FFmpegHelper.NormalizeAudioCodec("eac3"));
        }

        [TestMethod]
        public void NormalizeAudioCodec_Copy_ReturnsNull()
        {
            Assert.IsNull(FFmpegHelper.NormalizeAudioCodec("copy"));
            Assert.IsNull(FFmpegHelper.NormalizeAudioCodec(null));
        }

        // ---- IsCodecSupportedByContainer (video) ----

        [TestMethod]
        public void ContainerCompat_H264InMp4_Allowed()
        {
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("h264", "MP4", false));
        }

        [TestMethod]
        public void ContainerCompat_Vp9InWebm_Allowed()
        {
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("vp9", "WEBM", false));
        }

        [TestMethod]
        public void ContainerCompat_Vp9InMp4_Allowed()
        {
            // VP9 is listed in the MP4 whitelist (newer MP4 spec supports it).
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("vp9", "MP4", false));
        }

        [TestMethod]
        public void ContainerCompat_H264InWebm_Blocked()
        {
            // H.264 is NOT in the WebM whitelist → must re-encode.
            Assert.IsFalse(FFmpegHelper.IsCodecSupportedByContainer("h264", "WEBM", false));
        }

        [TestMethod]
        public void ContainerCompat_WmvInWmv_Allowed()
        {
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("wmv2", "WMV", false));
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("wmv3", "WMV", false));
        }

        [TestMethod]
        public void ContainerCompat_Mkv_AllowsEverything()
        {
            // MKV whitelist is null → everything allowed.
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("h264", "MKV", false));
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("vp9", "MKV", false));
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("theora", "MKV", false));
        }

        [TestMethod]
        public void ContainerCompat_UnknownContainer_Allowed()
        {
            // Unknown containers default to allowed.
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("h264", "UNKNOWN", false));
        }

        // ---- IsCodecSupportedByContainer (audio) ----

        [TestMethod]
        public void ContainerCompat_AacInMp4_Allowed()
        {
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("aac", "MP4", true));
        }

        [TestMethod]
        public void ContainerCompat_VorbisInMp4_Blocked()
        {
            Assert.IsFalse(FFmpegHelper.IsCodecSupportedByContainer("vorbis", "MP4", true));
        }

        [TestMethod]
        public void ContainerCompat_OpusInWebm_Allowed()
        {
            Assert.IsTrue(FFmpegHelper.IsCodecSupportedByContainer("opus", "WEBM", true));
        }

        [TestMethod]
        public void ContainerCompat_AacInWebm_Blocked()
        {
            Assert.IsFalse(FFmpegHelper.IsCodecSupportedByContainer("aac", "WEBM", true));
        }
    }
}
