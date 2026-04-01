#!/usr/bin/env python3
# -*- coding: utf-8 -*-

import os
import sys
import shutil
import subprocess
import threading
import tempfile
from concurrent.futures import ThreadPoolExecutor, as_completed

# =========================
# 可调参数
# =========================

# 字幕样式（默认尽量兼顾清晰和省码率）
FONT_NAME = "思源黑体 CN Heavy"
FONT_SIZE = 22
PRIMARY_COLOUR = "&H00FFFFFF"
OUTLINE_COLOUR = "&H00000000"
OUTLINE = 1
SHADOW = 1
BACK_COLOUR = "&H66000000"

TOP_MARGIN = 10
BOTTOM_MARGIN = 10

DEFAULT_WORKERS = max(1, min(4, (os.cpu_count() or 2) // 2))
FFMPEG_THREADS = "2"
DEFAULT_BUF_SIZE = "1835k"
KEEP_TEMP = False

# 编码模式：
# strict   -> 严格 CBR，兼容优先
# balanced -> 轻度 ABR，更利于大小/画质平衡
RATE_MODE = "balanced"

# 目标体积预留比例
# 例如 0.97 表示给视频留到“原文件扣掉音频后预算”的 97%
RESERVE_RATIO = 0.97

# 码率上下限，避免探测异常
MIN_VIDEO_KBPS = 1200
MAX_VIDEO_KBPS = 9000

# balanced 模式下允许的峰值倍率
BALANCED_MAXRATE_RATIO = 1.20

print_lock = threading.Lock()


def safe_print(*args, **kwargs):
    with print_lock:
        print(*args, **kwargs)


def escape_subtitle_path(path):
    path = os.path.abspath(path)
    path = path.replace("\\", "/")
    path = path.replace(":", "\\:")
    path = path.replace("'", r"\'")
    return path


def make_force_style(alignment, margin_v):
    return ",".join([
        f"FontName={FONT_NAME}",
        f"FontSize={FONT_SIZE}",
        f"PrimaryColour={PRIMARY_COLOUR}",
        f"OutlineColour={OUTLINE_COLOUR}",
        f"Outline={OUTLINE}",
        f"Shadow={SHADOW}",
        f"BackColour={BACK_COLOUR}",
        f"Alignment={alignment}",
        f"MarginV={margin_v}"
    ])


def build_subtitle_filter(subtitle_file, alignment, margin_v):
    subtitle_file_escaped = escape_subtitle_path(subtitle_file)
    force_style = make_force_style(alignment, margin_v)
    return f"subtitles='{subtitle_file_escaped}':force_style='{force_style}'"


def build_filters(bottom_subtitle_file, top_subtitle_file=None):
    filters = []

    if top_subtitle_file and os.path.exists(top_subtitle_file):
        filters.append(build_subtitle_filter(
            subtitle_file=top_subtitle_file,
            alignment=6,   # 顶部居中
            margin_v=TOP_MARGIN
        ))

    filters.append(build_subtitle_filter(
        subtitle_file=bottom_subtitle_file,
        alignment=2,     # 底部居中
        margin_v=BOTTOM_MARGIN
    ))

    return ",".join(filters)


def run_cmd(cmd, cwd=None):
    try:
        process = subprocess.Popen(
            cmd,
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            universal_newlines=True,
            encoding="utf-8",
            errors="ignore"
        )
    except FileNotFoundError:
        raise RuntimeError(f"命令未找到，请确认已加入 PATH: {cmd[0]}")

    lines = []
    for line in process.stdout:
        line = line.rstrip()
        lines.append(line)

    process.wait()
    return process.returncode, "\n".join(lines)


def probe_video_info(video_file):
    cmd = [
        "ffprobe",
        "-v", "error",
        "-select_streams", "v:0",
        "-show_entries", "stream=width,height,r_frame_rate,bit_rate",
        "-show_entries", "format=duration",
        "-of", "default=noprint_wrappers=1:nokey=0",
        video_file
    ]

    code, out = run_cmd(cmd)
    if code != 0:
        return {
            "width": 640,
            "height": 480,
            "fps": 24.0,
            "bit_rate": 6000000,
            "duration": 0.0
        }

    info = {}
    for line in out.splitlines():
        if "=" in line:
            k, v = line.strip().split("=", 1)
            info[k] = v

    width = int(info.get("width", 640))
    height = int(info.get("height", 480))

    fps_text = info.get("r_frame_rate", "24000/1000")
    try:
        if "/" in fps_text:
            a, b = fps_text.split("/")
            fps = float(a) / float(b) if float(b) != 0 else 24.0
        else:
            fps = float(fps_text)
    except Exception:
        fps = 24.0

    try:
        bit_rate = int(info.get("bit_rate", "6000000"))
    except Exception:
        bit_rate = 6000000

    try:
        duration = float(info.get("duration", "0"))
    except Exception:
        duration = 0.0

    return {
        "width": width,
        "height": height,
        "fps": fps,
        "bit_rate": bit_rate,
        "duration": duration
    }


def calc_gop(fps):
    return max(1, round(fps / 2))


def inject_meta(m2v_path):
    with open(m2v_path, "rb") as f:
        content = f.read()

    gop_start = b"\x00\x00\x01\xb8"
    user_start = b"\x00\x00\x01\xb2"
    meta = b"==== Created with subtitle batch tool. Powered by FFMPEG and PS2STR. ===="

    pos = content.find(gop_start)
    if pos == -1:
        raise RuntimeError("GOP start code not found in m2v")

    payload = user_start + meta
    new_content = content[:pos] + payload + content[pos:]

    with open(m2v_path, "wb") as f:
        f.write(new_content)


def append_end_code(m2v_path):
    end_code = b"\x00\x00\x01\xb7"

    with open(m2v_path, "rb") as f:
        content = f.read()

    if len(content) >= 4 and content[-4:] == end_code:
        return

    with open(m2v_path, "ab") as f:
        f.write(end_code)


def demux_pss_audio(input_pss, temp_dir, base_name):
    cmd = [
        "ps2str",
        "d",
        "-o",
        "-v",
        "-d", temp_dir,
        input_pss
    ]

    code, out = run_cmd(cmd)
    if code != 0:
        raise RuntimeError(f"ps2str demux failed:\n{out}")

    ads_file = os.path.join(temp_dir, f"{base_name}_pcm_0.ads")
    if not os.path.exists(ads_file):
        raise RuntimeError(f"未找到原始音频文件: {ads_file}\n\ndemux输出:\n{out}")

    return ads_file, out


def calc_target_bitrate_from_size(input_pss, ads_file, duration,
                                  reserve_ratio=0.97,
                                  min_kbps=1200,
                                  max_kbps=9000):
    input_size = os.path.getsize(input_pss)
    ads_size = os.path.getsize(ads_file)

    if duration <= 0:
        # 退回一个保守默认值
        return 6000

    # 扣除音频后的视频预算，再留一点封装余量
    target_video_bytes = max(1, int((input_size - ads_size) * reserve_ratio))
    target_bitrate_kbps = int(target_video_bytes * 8 / duration / 1000)

    target_bitrate_kbps = max(min_kbps, min(target_bitrate_kbps, max_kbps))
    return target_bitrate_kbps


def encode_subtitled_video_to_m2v(input_pss, bottom_srt, output_m2v, ads_file, top_srt=None):
    subtitle_filter = build_filters(bottom_srt, top_srt)
    meta = probe_video_info(input_pss)

    width = meta["width"]
    height = meta["height"]
    fps = meta["fps"]
    duration = meta["duration"]

    gop = calc_gop(fps)
    res = f"{width}x{height}"

    bitrate_k = calc_target_bitrate_from_size(
        input_pss=input_pss,
        ads_file=ads_file,
        duration=duration,
        reserve_ratio=RESERVE_RATIO,
        min_kbps=MIN_VIDEO_KBPS,
        max_kbps=MAX_VIDEO_KBPS
    )

    cmd = [
        "ffmpeg",
        "-threads", FFMPEG_THREADS,
        "-i", input_pss,
        "-vf", subtitle_filter,
        "-c:v", "mpeg2video",
        "-profile:v", "4",
        "-level:v", "8",
        "-b:v", f"{bitrate_k}k",
        "-bufsize", DEFAULT_BUF_SIZE,
        "-color_range", "tv",
        "-colorspace", "smpte170m",
        "-color_trc", "smpte170m",
        "-color_primaries", "smpte170m",
        "-field_order", "progressive",
        "-g", str(gop),
        "-r", f"{fps:.6f}",
        "-s", res,
        "-an",
        "-y",
        output_m2v
    ]

    if RATE_MODE == "strict":
        # 严格 CBR，兼容优先
        cmd[cmd.index("-bufsize"):cmd.index("-bufsize")] = [
            "-maxrate", f"{bitrate_k}k",
            "-minrate", f"{bitrate_k}k",
        ]
    elif RATE_MODE == "balanced":
        # 轻度 ABR，更利于大小/画质平衡
        maxrate_k = max(bitrate_k + 1, int(bitrate_k * BALANCED_MAXRATE_RATIO))
        cmd[cmd.index("-bufsize"):cmd.index("-bufsize")] = [
            "-maxrate", f"{maxrate_k}k",
        ]
    else:
        raise RuntimeError(f"未知 RATE_MODE: {RATE_MODE}")

    code, out = run_cmd(cmd)
    if code != 0:
        raise RuntimeError(f"ffmpeg 编码视频失败:\n{out}")

    out_size = os.path.getsize(output_m2v) if os.path.exists(output_m2v) else 0

    return {
        "width": width,
        "height": height,
        "fps": fps,
        "duration": duration,
        "bitrate_k": bitrate_k,
        "gop": gop,
        "mode": RATE_MODE,
        "m2v_size": out_size,
        "log": out
    }


def write_mux_file(mux_path, m2v_path, ads_path):
    m2v_path = m2v_path.replace("\\", "/")
    ads_path = ads_path.replace("\\", "/")

    content = f'''pss

    stream video:0
        input "{m2v_path}"
    end

    stream pcm:0
        input "{ads_path}"
    end
end
'''
    with open(mux_path, "w", encoding="utf-8", newline="\n") as f:
        f.write(content)


def mux_pss(mux_file, output_file, output_dir):
    output_name = os.path.basename(output_file)

    cmd = [
        "ps2str",
        "m",
        "-o",
        "-v",
        "-d", output_dir,
        mux_file,
        output_name
    ]

    code, out = run_cmd(cmd)
    if code != 0:
        raise RuntimeError(f"ps2str mux 失败:\n{out}")

    if not os.path.exists(output_file):
        raise RuntimeError(f"ps2str 未生成输出文件: {output_file}\n{out}")

    return out


def sizeof_mb(path):
    try:
        return os.path.getsize(path) / 1024 / 1024
    except Exception:
        return 0.0


def process_one(task):
    pss_name = task["pss_name"]
    input_pss = task["input_pss"]
    bottom_srt = task["bottom_srt"]
    top_srt = task["top_srt"]
    output_pss = task["output_pss"]
    has_top = task["has_top"]

    base_name = os.path.splitext(pss_name)[0]
    output_dir = os.path.dirname(output_pss)
    temp_dir = tempfile.mkdtemp(prefix=f"pss_{base_name}_")

    try:
        safe_print("\n" + "=" * 60)
        safe_print(f"开始处理: {pss_name}")
        safe_print(f"输入PSS: {input_pss}")
        if has_top:
            safe_print(f"上行字幕: {top_srt}")
        safe_print(f"下行字幕: {bottom_srt}")
        safe_print(f"输出PSS: {output_pss}")
        safe_print(f"临时目录: {temp_dir}")
        safe_print(f"编码模式: {RATE_MODE}")
        safe_print(f"体积预留比例: {RESERVE_RATIO}")

        input_size_mb = sizeof_mb(input_pss)
        safe_print(f"原始PSS大小: {input_size_mb:.2f} MB")

        safe_print("[1/4] 拆出原始音频...")
        ads_file, _ = demux_pss_audio(input_pss, temp_dir, base_name)
        ads_size_mb = sizeof_mb(ads_file)
        safe_print(f"原始音频: {ads_file}")
        safe_print(f"原始音频大小: {ads_size_mb:.2f} MB")

        new_m2v = os.path.join(temp_dir, f"{base_name}_video_0.m2v")

        safe_print("[2/4] 烧字幕并编码新视频...")
        enc_info = encode_subtitled_video_to_m2v(
            input_pss=input_pss,
            bottom_srt=bottom_srt,
            output_m2v=new_m2v,
            ads_file=ads_file,
            top_srt=top_srt if has_top else None
        )

        safe_print(
            f"分辨率: {enc_info['width']}x{enc_info['height']} | "
            f"帧率: {enc_info['fps']:.6f} | "
            f"时长: {enc_info['duration']:.3f}s | "
            f"目标码率: {enc_info['bitrate_k']}k | "
            f"GOP: {enc_info['gop']} | "
            f"模式: {enc_info['mode']}"
        )
        safe_print(f"新视频大小: {enc_info['m2v_size'] / 1024 / 1024:.2f} MB")

        safe_print("[3/4] 修正 m2v 元数据...")
        inject_meta(new_m2v)
        append_end_code(new_m2v)

        mux_file = os.path.join(temp_dir, f"{base_name}.mux")
        write_mux_file(mux_file, new_m2v, ads_file)

        safe_print("[4/4] 重新封装 PSS...")
        mux_pss(mux_file, output_pss, output_dir)

        output_size_mb = sizeof_mb(output_pss)
        diff_mb = output_size_mb - input_size_mb
        diff_pct = (diff_mb / input_size_mb * 100.0) if input_size_mb > 0 else 0.0

        safe_print(f"输出PSS大小: {output_size_mb:.2f} MB")
        safe_print(f"大小变化: {diff_mb:+.2f} MB ({diff_pct:+.2f}%)")
        safe_print(f"成功: {pss_name}")
        return pss_name, True, ""

    except Exception as e:
        safe_print(f"失败: {pss_name}")
        safe_print(str(e))
        return pss_name, False, str(e)

    finally:
        if KEEP_TEMP:
            safe_print(f"保留临时目录: {temp_dir}")
        else:
            shutil.rmtree(temp_dir, ignore_errors=True)


def main():
    global RATE_MODE

    if len(sys.argv) < 4:
        print("用法:")
        print(f"  python {os.path.basename(sys.argv[0])} <pss文件夹> <字幕文件夹> <输出文件夹> [并行数] [模式]")
        print("")
        print("模式:")
        print("  strict   -> 严格 CBR，兼容优先（默认）")
        print("  balanced -> 轻度 ABR，体积/画质更平衡")
        sys.exit(1)

    pss_folder = os.path.abspath(sys.argv[1])
    sub_folder = os.path.abspath(sys.argv[2])
    out_folder = os.path.abspath(sys.argv[3])

    if len(sys.argv) >= 5:
        try:
            max_workers = max(1, int(sys.argv[4]))
            mode_arg_index = 5
        except ValueError:
            max_workers = DEFAULT_WORKERS
            mode_arg_index = 4
    else:
        max_workers = DEFAULT_WORKERS
        mode_arg_index = 4

    if len(sys.argv) > mode_arg_index:
        mode = sys.argv[mode_arg_index].strip().lower()
        if mode not in ("strict", "balanced"):
            print("错误：模式必须是 strict 或 balanced")
            sys.exit(1)
        RATE_MODE = mode

    if not os.path.isdir(pss_folder):
        print(f"错误：PSS 文件夹不存在 -> {pss_folder}")
        sys.exit(1)

    if not os.path.isdir(sub_folder):
        print(f"错误：字幕文件夹不存在 -> {sub_folder}")
        sys.exit(1)

    os.makedirs(out_folder, exist_ok=True)

    pss_files = [f for f in os.listdir(pss_folder) if f.lower().endswith(".pss")]
    if not pss_files:
        print("没有找到 .pss 文件")
        sys.exit(0)

    print(f"PSS目录: {pss_folder}")
    print(f"字幕目录: {sub_folder}")
    print(f"输出目录: {out_folder}")
    print(f"并行数: {max_workers}")
    print(f"编码模式: {RATE_MODE}")
    print(f"目标体积预留比例: {RESERVE_RATIO}")
    print(f"找到 PSS 数量: {len(pss_files)}")
    print("工具调用方式: ffmpeg / ffprobe / ps2str 均从 PATH 直接调用")

    tasks = []
    skip_count = 0

    for pss_name in pss_files:
        base_name = os.path.splitext(pss_name)[0]

        input_pss = os.path.join(pss_folder, pss_name)
        bottom_srt = os.path.join(sub_folder, base_name + ".srt")
        top_srt = os.path.join(sub_folder, base_name + ".srt1")
        output_pss = os.path.join(out_folder, pss_name)

        if not os.path.exists(bottom_srt):
            print(f"跳过: 未找到下行字幕 -> {base_name}.srt")
            skip_count += 1
            continue

        tasks.append({
            "pss_name": pss_name,
            "input_pss": input_pss,
            "bottom_srt": bottom_srt,
            "top_srt": top_srt,
            "output_pss": output_pss,
            "has_top": os.path.exists(top_srt)
        })

    if not tasks:
        print("没有可处理任务")
        print(f"跳过: {skip_count}")
        sys.exit(0)

    success_count = 0
    fail_count = 0

    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = [executor.submit(process_one, task) for task in tasks]

        for future in as_completed(futures):
            name, ok, _ = future.result()
            if ok:
                success_count += 1
            else:
                fail_count += 1

    print("\n" + "=" * 60)
    print("批量处理完成")
    print(f"成功: {success_count}")
    print(f"跳过: {skip_count}")
    print(f"失败: {fail_count}")
    print("=" * 60)


if __name__ == "__main__":
    main()