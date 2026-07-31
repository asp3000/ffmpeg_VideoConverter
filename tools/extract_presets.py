#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Extract UniConverter Converter presets from XML .dat files into a 3-layer JSON spec.

Data source: D:\\tools\\UniConverterPortable\\App\\UniConverter\\

Layer design
------------
1) options_spec/presets.json         preset = category + name + DEFAULT VALUES only
2) options_spec/common_options.json  shared dropdown pools (resolution / frame rate /
                                     video bitrate / audio bitrate / sample rate / channel)
3) options_spec/format_options.json  format-SPECIFIC dropdowns only (codecs), e.g.
                                     AVI -> Xvid / DivX / MS MPEG-4 v3 / MJPEG / FFV1

All containers are JSON ARRAYS (never JSON objects used as maps), because the C# side
uses DataContractJsonSerializer, which silently yields an EMPTY dictionary when asked to
map a JSON object onto Dictionary<string, T>.
"""

import json
import os
import re
import xml.etree.ElementTree as ET

SRC = r"D:\tools\UniConverterPortable\App\UniConverter"
OUT = r"D:\AI\ffmpeg_VideoConverter\options_spec"


# --------------------------------------------------------------------------- #
# FourCC -> (ffmpeg encoder, display label)
# Keeping the mapping in the data layer means the C# side needs no hard-coded table.
# --------------------------------------------------------------------------- #
VIDEO_CODEC_MAP = {
    "H264": ("libx264", "H.264"),
    "B264": ("libx264", "H.264"),
    "HAVC": ("libx264", "H.264"),
    "AVC1": ("libx264", "H.264"),
    "HEVC": ("libx265", "H.265 (HEVC)"),
    "X265": ("libx265", "H.265 (HEVC)"),
    "HEV1": ("libx265", "H.265 (HEVC)"),
    "MP4V": ("mpeg4", "MPEG-4"),
    "XVID": ("libxvid", "Xvid"),
    "DIVX": ("libxvid", "DivX"),
    "DX50": ("libxvid", "DivX 5"),
    "MP43": ("msmpeg4v3", "MS MPEG-4 v3"),
    "MP42": ("msmpeg4v2", "MS MPEG-4 v2"),
    "MJPG": ("mjpeg", "MJPEG"),
    "FFV1": ("ffv1", "FFV1"),
    "HFYU": ("huffyuv", "HuffYUV"),
    "AV1": ("libsvtav1", "AV1"),
    "AV01": ("libsvtav1", "AV1"),
    "VP80": ("libvpx", "VP8"),
    "VP8": ("libvpx", "VP8"),
    "VP90": ("libvpx-vp9", "VP9"),
    "VP9": ("libvpx-vp9", "VP9"),
    "THEO": ("libtheora", "Theora"),
    "MPG1": ("mpeg1video", "MPEG-1"),
    "MPEG": ("mpeg1video", "MPEG-1"),
    "MPG2": ("mpeg2video", "MPEG-2"),
    "MP2V": ("mpeg2video", "MPEG-2"),
    "BDAV": ("mpeg2video", "MPEG-2"),
    "WMV1": ("wmv1", "WMV 7"),
    "WMV2": ("wmv2", "WMV 8"),
    "WMV3": ("wmv2", "WMV 9"),
    "WVC1": ("wmv2", "VC-1"),
    "PRRS": ("prores_ks", "Apple ProRes"),
    "APCN": ("prores_ks", "Apple ProRes"),
    "DNXH": ("dnxhd", "DNxHD"),
    "CFHD": ("cfhd", "CineForm"),
    "DVSD": ("dvvideo", "DV"),
    "FLV1": ("flv", "Sorenson H.263"),
    "GIF": ("gif", "GIF"),
    "PNG": ("png", "PNG"),
    "JPEG": ("mjpeg", "JPEG"),
    "JPG": ("mjpeg", "JPEG"),
    "JPE": ("mjpeg", "JPEG"),
    "JP2": ("jpeg2000", "JPEG 2000"),
    "BMP": ("bmp", "BMP"),
    "TIFF": ("tiff", "TIFF"),
    "TIF": ("tiff", "TIFF"),
    "WEBP": ("libwebp", "WebP"),
    "TGA": ("targa", "TGA"),
    "DPX": ("dpx", "DPX"),
    "ICO": ("bmp", "ICO"),
    "SVG": ("png", "SVG"),
    "HEIC": ("libx265", "HEIC"),
    "AVIF": ("libaom-av1", "AVIF"),
}

AUDIO_CODEC_MAP = {
    "AAC": ("aac", "AAC"),
    "MAAC": ("aac", "AAC"),
    "MP4A": ("aac", "AAC"),
    "MP3": ("libmp3lame", "MP3"),
    "MP2": ("mp2", "MP2"),
    "AC3": ("ac3", "AC3"),
    "EAC3": ("eac3", "E-AC3"),
    "DTS": ("dca", "DTS"),
    "FLAC": ("flac", "FLAC"),
    "ALAC": ("alac", "Apple Lossless"),
    "OPUS": ("libopus", "Opus"),
    "VORB": ("libvorbis", "Vorbis"),
    "OGG": ("libvorbis", "Vorbis"),
    "WMA2": ("wmav2", "WMA"),
    "WMA": ("wmav2", "WMA"),
    "PCM": ("pcm_s16le", "PCM"),
    "WAV": ("pcm_s16le", "PCM"),
    "AIFF": ("pcm_s16be", "AIFF PCM"),
    "AMR": ("libopencore_amrnb", "AMR-NB"),
    "SPX": ("libspeex", "Speex"),
}

CHANNEL_LABELS = [
    {"value": 1, "label": "单声道"},
    {"value": 2, "label": "立体声"},
    {"value": 6, "label": "5.1 环绕声"},
]

# UniConverter stores channels as these display strings; map back to ffmpeg -ac values.
CHANNEL_NAME_TO_VALUE = {"mono": 1, "stereo": 2, "5.1": 6}


def load_xml(name):
    path = os.path.join(SRC, name)
    with open(path, "rb") as f:
        data = f.read()
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return ET.fromstring(data.decode("utf-8"))


def text(el, tag, default=""):
    child = el.find(tag)
    return (child.text or default).strip() if child is not None else default


def parse_int(s):
    m = re.search(r"(\d+)", (s or "").replace(",", ""))
    return int(m.group(1)) if m else 0


def parse_float(s):
    m = re.search(r"([\d.]+)", (s or "").replace(",", ""))
    if not m:
        return 0.0
    try:
        return round(float(m.group(1)), 3)
    except ValueError:
        return 0.0


def snap_frame_rate(value, pool):
    """Snap a preset default fps onto the shared frame-rate pool (23 -> 23.97)."""
    if value <= 0 or not pool:
        return value
    best = min(pool, key=lambda x: abs(x - value))
    return best if abs(best - value) <= 0.999 else value


def norm_fourcc(s):
    """Preset FourCC codes must match format_options entries exactly (upper-case)."""
    v = (s or "").strip().upper()
    return "" if v == "0" else v


def parse_resolution(s):
    m = re.search(r"(\d+)\s*x\s*(\d+)", s or "")
    return (int(m.group(1)), int(m.group(2))) if m else None


def split_values(s):
    return [v.strip() for v in (s or "").replace(";", ",").split(",") if v.strip()]


def int_list(s):
    return sorted({parse_int(v) for v in split_values(s) if parse_int(v) > 0})


def float_list(s):
    out = set()
    for v in split_values(s):
        m = re.search(r"([\d.]+)", v)
        if m:
            try:
                out.add(round(float(m.group(1)), 3))
            except ValueError:
                pass
    return sorted(out)


def map_video_codec(fourcc):
    key = (fourcc or "").strip().upper()
    if not key:
        return None
    enc, label = VIDEO_CODEC_MAP.get(key, (key.lower(), key))
    return {"fourCC": key, "label": label, "encoder": enc}


def map_audio_codec(fourcc):
    key = (fourcc or "").strip().upper()
    if not key or key == "0":
        return None
    enc, label = AUDIO_CODEC_MAP.get(key, (key.lower(), key))
    return {"fourCC": key, "label": label, "encoder": enc}


def localize_name(name):
    """Map English preset names to the Chinese labels used by the UniConverter UI."""
    mapping = {
        "Same as source": "与源文件相同",
        "Same As Source": "与源文件相同",
        "4K Video": "4K",
        "8K Video": "8K",
        "Alpha": "透明通道",
        "3D Red-Blue": "3D 红蓝",
        "3D RedBlue": "3D 红蓝",
        "3D Left-Right": "3D 左右",
        "3D LeftRight": "3D 左右",
        "HD 1080P": "1080", "HD 1080p": "1080", "1080P": "1080", "1080p": "1080",
        "HD 720P": "720P", "HD 720p": "720P", "720P": "720P", "720p": "720P",
        "640P": "640P", "640p": "640P",
        "SD 576P": "576P", "SD 576p": "576P", "576P": "576P", "576p": "576P",
        "SD 480P": "480P", "SD 480p": "480P", "480P": "480P", "480p": "480P",
        "360P": "360P", "240P": "240P",
        "High Quality": "高品质",
        "Medium Quality": "中品质",
        "Low Quality": "低品质",
        "Normal": "标准",
        "Small Size": "小体积",
    }
    return mapping.get((name or "").strip(), (name or "").strip())


# --------------------------------------------------------------------------- #

def extract():
    os.makedirs(OUT, exist_ok=True)

    display_root = load_xml("DisplayCategory.dat")
    enc_root = load_xml("EncodeParamInfos.dat")
    fmt_root = load_xml("Format.dat")

    # ---- shared pools (deduplicated across every format) ----
    all_resolutions = set()
    all_frame_rates = set()
    all_video_bitrates = set()
    all_audio_bitrates = set()
    all_sample_rates = set()
    all_channels = set()

    formats = []

    for fi in fmt_root.findall("FormatInfo"):
        fid = fi.get("ID")
        fourcc = fi.get("FourCC", "")
        ext = fi.get("Format", "").lstrip(".")
        name = text(fi, "Name") or ext.upper()

        video_codecs = {}
        audio_codecs = {}

        ve = fi.find("VideoEnc")
        if ve is not None:
            for ep in ve.findall("EncParam"):
                res = parse_resolution(ep.get("Resolution", ""))
                if res:
                    all_resolutions.add(res)
                all_video_bitrates.update(int_list(text(ep, "VideoBitrate")))
                all_frame_rates.update(float_list(text(ep, "FrameRate")))
                vc = map_video_codec(ep.get("VideoFourCC", ""))
                if vc:
                    video_codecs[vc["fourCC"]] = vc

        ae = fi.find("AudioEnc")
        if ae is not None:
            for ep in ae.findall("EncParam"):
                all_sample_rates.update(int_list(text(ep, "SampleRate")))
                all_audio_bitrates.update(int_list(text(ep, "AudioBitrate")))
                for cname in ("Mono", "Stereo", "5.1"):
                    all_channels.add(CHANNEL_NAME_TO_VALUE[cname.lower()])
                ac = map_audio_codec(ep.get("AudioFourCC", ""))
                if ac:
                    audio_codecs[ac["fourCC"]] = ac

        formats.append({
            "id": fid,
            "name": name,
            "fourCC": fourcc,
            "extension": ext,
            "videoCodecs": sorted(video_codecs.values(), key=lambda c: c["label"]),
            "audioCodecs": sorted(audio_codecs.values(), key=lambda c: c["label"]),
        })

    # Frame-rate snapshot used to snap preset defaults (23 -> 23.97).
    fps_pool = sorted(all_frame_rates)

    # ---- presets.json (defaults only, array-shaped) ----
    enc_by_id = {e.get("ID"): e for e in enc_root.findall("EncParamInfo")}

    type_names = {"0": "视频", "1": "音频", "2": "设备", "3": "Web Video", "6": "Image"}
    category_buckets = {}

    for dc in display_root.findall("displaycategory"):
        cid = dc.get("id")
        title = dc.get("title")
        ctype = dc.get("type", "0")
        icon = dc.get("icon", "")

        cat_key = type_names.get(ctype, "Custom")
        category_buckets.setdefault(cat_key, [])

        presets = []
        for eid, e in enc_by_id.items():
            if e.get("DisPlayCategoryid") != cid:
                continue

            ve = e.find("VEncParam")
            ae = e.find("AEncParam")

            resolution = None
            d_vcodec = ""
            d_vbitrate = -1
            d_fps = -1
            d_acodec = ""
            d_channel = -1
            d_srate = -1
            d_abitrate = -1

            if ve is not None:
                d_vcodec = ve.get("VideoFourCC", "")
                res_text = ve.get("Resolution", "")
                res = parse_resolution(res_text) if res_text != "-1" else None
                if res:
                    resolution = {"width": res[0], "height": res[1]}
                if ve.get("defVideoBitrate", "-1") != "-1":
                    d_vbitrate = parse_int(ve.get("defVideoBitrate", ""))
                if ve.get("defFrameRate", "-1") != "-1":
                    d_fps = snap_frame_rate(parse_float(ve.get("defFrameRate", "")), fps_pool)

            if ae is not None:
                d_acodec = ae.get("AudioFourCC", "")
                if ae.get("Channel", "-1") != "-1":
                    d_channel = parse_int(ae.get("Channel", ""))
                if ae.get("defSampleRate", "-1") != "-1":
                    d_srate = parse_int(ae.get("defSampleRate", ""))
                if ae.get("defAudioBitrate", "-1") != "-1":
                    d_abitrate = parse_int(ae.get("defAudioBitrate", ""))

            presets.append({
                "id": eid,
                "name": localize_name(text(e, "Name")),
                "formatId": e.get("FormatID"),
                "fourCC": norm_fourcc(e.get("FourCC", "")),
                "keepSource": e.get("KeepSrcParam", "False").lower() == "true",
                "videoCodec": norm_fourcc(d_vcodec),
                "resolution": resolution,
                "videoBitrate": d_vbitrate,
                "frameRate": d_fps,
                "audioCodec": norm_fourcc(d_acodec),
                "channel": d_channel,
                "sampleRate": d_srate,
                "audioBitrate": d_abitrate,
            })

        if not presets:
            continue

        card_format_id = presets[0].get("formatId")

        # Image categories (JPG / PNG / SVG ...) have no FormatInfo entry, so their
        # presets carry formatId="0". Synthesise a format so every preset resolves.
        if card_format_id in (None, "", "0"):
            synth_id = "img" + str(cid)
            ext = (title or "").strip().lower()
            codecs = {}
            for pr in presets:
                vc = map_video_codec(pr.get("videoCodec"))
                if vc:
                    codecs[vc["fourCC"]] = vc
            if not codecs:
                vc = map_video_codec(ext.upper())
                if vc:
                    codecs[vc["fourCC"]] = vc
            formats.append({
                "id": synth_id,
                "name": title,
                "fourCC": "",
                "extension": ext,
                "videoCodecs": sorted(codecs.values(), key=lambda c: c["label"]),
                "audioCodecs": [],
            })
            for pr in presets:
                pr["formatId"] = synth_id
            card_format_id = synth_id

        category_buckets[cat_key].append({
            "id": cid,
            "title": title,
            "icon": icon,
            "formatId": card_format_id,
            "presets": presets,
        })

    # A handful of preset defaults (odd resolutions / sample rates) never appear in
    # any format's EncParam list. Fold them back into the shared pools so every
    # preset default is selectable in the UI dropdowns.
    for bucket in category_buckets.values():
        for card in bucket:
            for pr in card["presets"]:
                if pr["resolution"]:
                    all_resolutions.add((pr["resolution"]["width"], pr["resolution"]["height"]))
                if pr["frameRate"] > 0:
                    all_frame_rates.add(pr["frameRate"])
                if pr["videoBitrate"] > 0:
                    all_video_bitrates.add(pr["videoBitrate"])
                if pr["audioBitrate"] > 0:
                    all_audio_bitrates.add(pr["audioBitrate"])
                if pr["sampleRate"] > 0:
                    all_sample_rates.add(pr["sampleRate"])
                if pr["channel"] > 0:
                    all_channels.add(pr["channel"])

    # ---- common_options.json ----
    resolutions = []
    for idx, (w, h) in enumerate(sorted(all_resolutions, key=lambda r: (-r[0] * r[1], -r[0]))):
        resolutions.append({"id": idx, "width": w, "height": h, "label": "{0} x {1}".format(w, h)})

    common = {
        "resolutions": resolutions,
        "frameRates": sorted(all_frame_rates),
        "videoBitrates": sorted(all_video_bitrates),
        "audioBitrates": sorted(all_audio_bitrates),
        "sampleRates": sorted(all_sample_rates),
        "channels": [c for c in CHANNEL_LABELS if c["value"] in all_channels] or CHANNEL_LABELS,
    }

    # UI tab order
    ordered_names = ["视频", "音频", "Image", "设备", "Web Video", "Custom"]
    categories = []
    for key in ordered_names:
        if category_buckets.get(key):
            categories.append({"name": key, "formats": category_buckets[key]})

    # UniConverter ships a few duplicate FormatInfo IDs (e.g. 3007 is used by both
    # "NOOK Tablet" and "Kindle Fire"). Merge them so the ID stays a unique key.
    merged = {}
    for f in formats:
        prev = merged.get(f["id"])
        if prev is None:
            merged[f["id"]] = f
            continue
        for kind in ("videoCodecs", "audioCodecs"):
            known = {c["fourCC"] for c in prev[kind]}
            prev[kind].extend(c for c in f[kind] if c["fourCC"] not in known)
            prev[kind].sort(key=lambda c: c["label"])
    formats = sorted(merged.values(), key=lambda f: f["id"])

    presets_doc = {"categories": categories}
    format_doc = {"formats": formats}

    def write(fname, obj):
        # sort_keys is REQUIRED: DataContractJsonSerializer matches members in
        # alphabetical order and silently drops fields that arrive out of order.
        path = os.path.join(OUT, fname)
        with open(path, "w", encoding="utf-8") as f:
            json.dump(obj, f, ensure_ascii=False, indent=1, sort_keys=True)
        return os.path.getsize(path)

    s1 = write("presets.json", presets_doc)
    s2 = write("common_options.json", common)
    s3 = write("format_options.json", format_doc)

    total_presets = sum(len(fmt["presets"]) for cat in categories for fmt in cat["formats"])
    total_vcodecs = sum(len(f["videoCodecs"]) for f in formats)
    total_acodecs = sum(len(f["audioCodecs"]) for f in formats)

    print("presets.json         {0:>9,} bytes   {1} categories / {2} presets".format(
        s1, len(categories), total_presets))
    print("common_options.json  {0:>9,} bytes   {1} res / {2} fps / {3} vbr / {4} abr / {5} sr / {6} ch".format(
        s2, len(common["resolutions"]), len(common["frameRates"]), len(common["videoBitrates"]),
        len(common["audioBitrates"]), len(common["sampleRates"]), len(common["channels"])))
    print("format_options.json  {0:>9,} bytes   {1} formats / {2} video codecs / {3} audio codecs".format(
        s3, len(formats), total_vcodecs, total_acodecs))
    print("total                {0:>9,} bytes".format(s1 + s2 + s3))


if __name__ == "__main__":
    extract()
