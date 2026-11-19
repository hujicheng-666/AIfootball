"""给标注页的剪映式时间轴铺一排缩略图。

为什么要单独一个脚本：WPF 的 MediaElement 只能播、不能抽帧，
而"看着小图找那一脚"是剪辑软件最核心的手感——只有刻度尺的话，
用户仍然得靠 ±1 秒按钮盲摸。这里用和 shot_list.py 一样的路子：
Python 读帧、落盘到 data/goal_detect/thumbs/<stem>/，C# 只负责按序加载。

采样是"等间隔、取每段中点"：每张图代表 duration/count 秒，
C# 侧按这个步长把图贴到时间轴对应的位置上，缩放时不会和图错位。

用法：
    python timeline_thumbs.py --video <mp4> [--workspace <dir>] [--out <dir>]
                              [--count 96] [--height 44]

输出（stdout 单行 JSON）：
    {"ok": true, "dir": "...", "count": 96, "step": 3.36,
     "duration": 322.56, "height": 44}
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import cv2


def _thumb_dir(args, stem: str) -> str:
    if args.out:
        return args.out
    root = args.workspace or os.getcwd()
    return os.path.join(root, "data", "goal_detect", "thumbs", stem)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--video", required=True)
    parser.add_argument("--workspace", default=None, help="工作区根目录（缓存落在其下）")
    parser.add_argument("--out", default=None, help="缩略图目录；默认 data/goal_detect/thumbs/<stem>")
    parser.add_argument("--count", type=int, default=96, help="采样张数")
    parser.add_argument("--height", type=int, default=44, help="缩略图高度（像素）")
    args = parser.parse_args()

    video = os.path.abspath(args.video)
    if not os.path.isfile(video):
        print(json.dumps({"ok": False, "error": f"视频不存在：{video}"}, ensure_ascii=False))
        return 1

    stem = os.path.splitext(os.path.basename(video))[0]
    out_dir = _thumb_dir(args, stem)
    os.makedirs(out_dir, exist_ok=True)

    # 换片或长度变化时旧图会错位，先清干净
    for name in os.listdir(out_dir):
        if name.startswith("t") and name.endswith(".jpg"):
            try:
                os.remove(os.path.join(out_dir, name))
            except OSError:
                pass

    capture = cv2.VideoCapture(video)
    if not capture.isOpened():
        print(json.dumps({"ok": False, "error": f"打不开视频：{video}"}, ensure_ascii=False))
        return 1

    try:
        fps = float(capture.get(cv2.CAP_PROP_FPS) or 0.0) or 25.0
        frames = int(capture.get(cv2.CAP_PROP_FRAME_COUNT) or 0)
        duration = frames / fps if frames > 0 else 0.0
        if duration <= 0:
            print(json.dumps({"ok": False, "error": "解不出帧数"}, ensure_ascii=False))
            return 1

        count = max(8, min(args.count, 240))
        step = duration / count
        height = max(24, args.height)
        written = 0

        for index in range(count):
            # 取每段的中点：第一张不会正好是黑场，最后一张也不会越过片尾
            moment = min(duration - 1e-3, (index + 0.5) * step)
            capture.set(cv2.CAP_PROP_POS_MSEC, moment * 1000.0)
            ok, frame = capture.read()
            if not ok:
                continue
            source_h, source_w = frame.shape[:2]
            if source_h <= 0 or source_w <= 0:
                continue
            scale = height / float(source_h)
            resized = cv2.resize(
                frame,
                (max(1, int(round(source_w * scale))), height),
                interpolation=cv2.INTER_AREA,
            )
            target = os.path.join(out_dir, f"t{index:05d}.jpg")
            if cv2.imwrite(target, resized, [int(cv2.IMWRITE_JPEG_QUALITY), 72]):
                written += 1
    finally:
        capture.release()

    payload = {
        "ok": written > 0,
        "dir": out_dir,
        "count": written,
        "step": round(duration / max(1, written), 4) if written else 0.0,
        "duration": round(duration, 3),
        "height": args.height,
    }
    print(json.dumps(payload, ensure_ascii=False))
    return 0 if written > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
