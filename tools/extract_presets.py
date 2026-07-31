#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Extract UniConverter Converter presets from XML .dat files into JSON specs.
Data source: D:\\tools\\UniConverterPortable\\App\\UniConverter\\
Outputs:
  options_spec/presets.json        -> preset selection UI (categories / formats / presets)
  options_spec/format_options.json -> preset edit UI dropdowns per format
"""

import json
import os
import re
import xml.etree.ElementTree as ET

SRC = r"D:\tools\UniConverterPortable\App\UniConverter"
OUT = r"D:\AI\ffmpeg_VideoConverter\options_spec"


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


def parse_bitrate(s):
    m = re.search(r"(\d+)", s.replace(",", ""))
    return int(m.group(1)) if m else 0


def parse_resolution(s):
    m = re.search(r"(\d+)\s*x\s*(\d+)", s)
    return {"width": int(m.group(1)), "height": int(m.group(2))} if m else None


def split_values(s):
    return [v.strip() for v in s.replace(";", ",").split(",") if v.strip()]


def bitrate_list(s):
    vals = split_values(s)
    return sorted(set(parse_bitrate(v) for v in vals if parse_bitrate(v) > 0))


def framerate_list(s):
    vals = split_values(s)
    out = []
    for v in vals:
        m = re.search(r"([\d.]+)", v)
        if m:
            try:
                out.append(float(m.group(1)))
            except ValueError:
                pass
    return sorted(set(out))


def sample_rate_list(s):
    vals = split_values(s)
    out = []
    for v in vals:
        m = re.search(r"(\d+)", v)
        if m:
            out.append(int(m.group(1)))
    return sorted(set(out))


def localize_name(name):
    """Map common English preset names to Chinese labels used by UniConverter UI."""
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
        "HD 1080P": "1080",
        "HD 1080p": "1080",
        "1080P": "1080",
        "1080p": "1080",
        "HD 720P": "720P",
        "HD 720p": "720P",
        "720P": "720P",
        "720p": "720P",
        "640P": "640P",
        "640p": "640P",
        "SD 576P": "576P",
        "SD 576p": "576P",
        "576P": "576P",
        "576p": "576P",
        "SD 480P": "480P",
        "SD 480p": "480P",
        "480P": "480P",
        "480p": "480P",
        "360P": "360P",
        "240P": "240P",
        "High Quality": "高品质",
        "Medium Quality": "中品质",
        "Low Quality": "低品质",
        "Normal": "标准",
        "Small Size": "小体积",
    }
    return mapping.get(name.strip(), name.strip())


def extract():
    os.makedirs(OUT, exist_ok=True)

    display_root = load_xml("DisplayCategory.dat")
    enc_root = load_xml("EncodeParamInfos.dat")
    fmt_root = load_xml("Format.dat")

    # --- build format options map ---
    format_options = {}
    for fi in fmt_root.findall("FormatInfo"):
        fid = fi.get("ID")
        fourcc = fi.get("FourCC", "")
        ext = fi.get("Format", "").lstrip(".")
        name = text(fi, "Name") or ext.upper()

        video_options = []
        audio_options = []

        ve = fi.find("VideoEnc")
        if ve is not None:
            for ep in ve.findall("EncParam"):
                resolution = parse_resolution(ep.get("Resolution", ""))
                if resolution is None:
                    continue
                vbrs = bitrate_list(text(ep, "VideoBitrate"))
                fps = framerate_list(text(ep, "FrameRate"))
                video_options.append({
                    "codec": ep.get("VideoFourCC", ""),
                    "resolution": resolution,
                    "defaultBitrate": parse_bitrate(ep.get("defVideoBitrate", "")),
                    "defaultFrameRate": parse_bitrate(ep.get("defFrameRate", "")),
                    "bitrates": vbrs,
                    "frameRates": fps,
                })

        ae = fi.find("AudioEnc")
        if ae is not None:
            for ep in ae.findall("EncParam"):
                codec = ep.get("AudioFourCC", "")
                sr = sample_rate_list(text(ep, "SampleRate"))
                abrs = bitrate_list(text(ep, "AudioBitrate"))
                chs = ["Mono", "Stereo", "5.1"]  # UniConverter 常见
                audio_options.append({
                    "codec": codec,
                    "sampleRates": sr,
                    "channels": chs,
                    "bitrates": abrs,
                })

        format_options[fid] = {
            "id": fid,
            "name": name,
            "fourcc": fourcc,
            "extension": ext,
            "videoOptions": video_options,
            "audioOptions": audio_options,
        }

    # --- build presets grouped by category type ---
    # EncParamInfo keyed by ID
    enc_by_id = {e.get("ID"): e for e in enc_root.findall("EncParamInfo")}

    type_names = {
        "0": "视频",
        "1": "音频",
        "2": "设备",
        "3": "Web Video",
        "6": "Image",
    }

    categories = {}
    for dc in display_root.findall("displaycategory"):
        cid = dc.get("id")
        title = dc.get("title")
        ctype = dc.get("type", "0")
        icon = dc.get("icon", "")
        enc_id = dc.get("EncParamInfoID")

        cat_key = type_names.get(ctype, "Custom")
        if cat_key not in categories:
            categories[cat_key] = []

        # find all EncParamInfo for this display category
        presets = []
        for eid, e in enc_by_id.items():
            if e.get("DisPlayCategoryid") == cid:
                fmt_id = e.get("FormatID")
                name = text(e, "Name")
                keep = e.get("KeepSrcParam", "False").lower() == "true"
                ve = e.find("VEncParam")
                ae = e.find("AEncParam")
                resolution = None
                default_vcodec = ""
                default_vbitrate = -1
                default_fps = -1
                default_acodec = ""
                default_channel = -1
                default_sample_rate = -1
                default_abitrate = -1

                if ve is not None:
                    default_vcodec = ve.get("VideoFourCC", "")
                    res_text = ve.get("Resolution", "")
                    resolution = parse_resolution(res_text) if res_text != "-1" else None
                    default_vbitrate = parse_bitrate(ve.get("defVideoBitrate", "-1")) if ve.get("defVideoBitrate") != "-1" else -1
                    default_fps = parse_bitrate(ve.get("defFrameRate", "-1")) if ve.get("defFrameRate") != "-1" else -1

                if ae is not None:
                    default_acodec = ae.get("AudioFourCC", "")
                    default_channel = int(ae.get("Channel", "-1")) if ae.get("Channel") != "-1" else -1
                    default_sample_rate = int(ae.get("defSampleRate", "-1").replace(" Hz", "")) if ae.get("defSampleRate") != "-1" else -1
                    default_abitrate = parse_bitrate(ae.get("defAudioBitrate", "-1")) if ae.get("defAudioBitrate") != "-1" else -1

                presets.append({
                    "id": eid,
                    "name": localize_name(name),
                    "formatId": fmt_id,
                    "fourCC": e.get("FourCC", ""),
                    "keepSource": keep,
                    "defaultVideoCodec": default_vcodec,
                    "resolution": resolution,
                    "defaultBitrate": default_vbitrate,
                    "defaultFrameRate": default_fps,
                    "defaultAudioCodec": default_acodec,
                    "defaultChannel": default_channel,
                    "defaultSampleRate": default_sample_rate,
                    "defaultAudioBitrate": default_abitrate,
                })

        if presets:
            categories[cat_key].append({
                "id": cid,
                "title": title,
                "icon": icon,
                "formatId": presets[0].get("formatId"),
                "presets": presets,
            })

    # reorder to match UI tabs: 最近, 视频, 音频, Image, 设备, Web Video, Custom
    ordered = {}
    for key in ["最近", "视频", "音频", "Image", "设备", "Web Video"]:
        if key in categories:
            ordered[key] = categories[key]
    if "Custom" in categories:
        ordered["Custom"] = categories["Custom"]

    # collect recent = first preset of each video format for the "最近" tab
    recent = []
    for fmt in ordered.get("视频", []):
        if fmt["presets"]:
            p = fmt["presets"][0].copy()
            p["title"] = fmt["title"]
            p["icon"] = fmt["icon"]
            recent.append({
                "id": fmt["id"],
                "title": fmt["title"],
                "icon": fmt["icon"],
                "formatId": fmt["formatId"],
                "presets": [p],
            })
    ordered["最近"] = recent

    presets_doc = {"categories": ordered}

    with open(os.path.join(OUT, "presets.json"), "w", encoding="utf-8") as f:
        json.dump(presets_doc, f, ensure_ascii=False, indent=2)

    with open(os.path.join(OUT, "format_options.json"), "w", encoding="utf-8") as f:
        json.dump(format_options, f, ensure_ascii=False, indent=2)

    # quick stats
    total_presets = sum(len(fmt["presets"]) for cat in ordered.values() for fmt in cat)
    print(f"Wrote {OUT}/presets.json  ({len(ordered)} categories, {total_presets} presets)")
    print(f"Wrote {OUT}/format_options.json  ({len(format_options)} formats)")


if __name__ == "__main__":
    extract()
