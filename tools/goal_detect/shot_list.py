"""把一段比赛合集切成"一脚一个镜头"，给标注面板做脚次导航。

为什么要单独一个脚本：合集视频里有几十脚点球，标注时靠 ±1s 按钮一帧帧摸过去
不现实，而"下一脚"按钮原本只在**已标注**的时间点之间跳——第一次通标时毫无用处。
先扫一遍镜头切换，得到候选脚次，标注就变成"标完自动进下一脚"。

只做灰度帧差，不做门框检测，所以比 video_scan.py 快一个量级：
8064 帧 1280x720 约 30~50 秒（CPU），结果按视频缓存，第二次打开直接读。

用法：
    python shot_list.py --video <mp4> [--out <json>] [--threshold 26]

输出（stdout 也是同样的 JSON，方便 C# 直接读）：
    {
      "ok": true, "video": "...", "fps": 25.0, "frames": 8064,
      "duration": 322.56, "threshold": 26.0, "shots": [
        {"index": 1, "start": 0.0, "end": 3.44, "key": 2.24, "frames": 86}
      ]
    }
"""

from __future__ import annotations

import argparse
import json
import math
import os
import sys

import cv2
import numpy as np

# 缩到 160x90 做帧差：切镜是全画面变化，小图上一样明显，解码也快
PROBE_W, PROBE_H = 160, 90

# 阈值下限/上限。切镜帧差通常在 20~40，普通运动 3~10；
# 夹在 20~32 之间可以躲开"整段静止"和"整段手持晃动"两种极端素材。
THRESHOLD_MIN = 20.0
THRESHOLD_MAX = 32.0

# 同一脚里可能有连续两帧都过阈值（闪白/淡入），0.4 秒内的合并成一个切点
MERGE_SECONDS = 0.4

# 短于这个长度的"镜头"是闪白、转场特效，不是一脚点球，并进前一段
MIN_SHOT_SECONDS = 0.9

# 跳到某一脚时落在切点前多久：切点前 ~1.2s 一般是助跑结束、球刚被踢出
KEY_LEAD_SECONDS = 1.2


def _decode_diffs(path: str) -> tuple[list[float], float, int]:
    """返回逐帧灰度帧差（第 0 帧为 0）以及 fps / 帧数。"""
    capture = cv2.VideoCapture(path)
    if not capture.isOpened():
        raise RuntimeError(f"打不开视频：{path}")
    try:
        fps = float(capture.get(cv2.CAP_PROP_FPS) or 0.0)
        if fps <= 0:
            fps = 25.0
        diffs: list[float] = []
        previous = None
        while True:
            ok, frame = capture.read()
            if not ok:
                break
            small = cv2.resize(frame, (PROBE_W, PROBE_H), interpolation=cv2.INTER_AREA)
            gray = cv2.cvtColor(small, cv2.COLOR_BGR2GRAY)
            if previous is None:
                diffs.append(0.0)
            else:
                diffs.append(float(cv2.absdiff(gray, previous).mean()))
            previous = gray
        return diffs, fps, len(diffs)
    finally:
        capture.release()


def _pick_threshold(diffs: np.ndarray, override: float | None) -> float:
    if override and override > 0:
        return float(override)
    # 80 脚 / 8000 帧 ≈ 1%，切点大致落在 p99 附近；再夹到合理区间兜底
    p99 = float(np.percentile(diffs, 99)) if diffs.size else THRESHOLD_MIN
    return max(THRESHOLD_MIN, min(THRESHOLD_MAX, p99))


def build_shots(diffs: list[float], fps: float, threshold: float) -> list[dict]:
    values = np.asarray(diffs, dtype=float)
    if values.size == 0:
        return []

    merge_frames = max(1, int(round(fps * MERGE_SECONDS)))
    cuts: list[int] = []
    last = -10**9
    for index in np.flatnonzero(values > threshold):
        index = int(index)
        if index - last > merge_frames:
            cuts.append(index)
        last = index

    # 切点 → 区间。第 0 帧永远是新一段的开头
    boundaries = [0] + [c for c in cuts if 0 < c < len(values)]
    spans = []
    for position, start in enumerate(boundaries):
        end = boundaries[position + 1] if position + 1 < len(boundaries) else len(values)
        if end <= start:
            continue
        spans.append((start, end))

    # 太短的并进前一段（闪白、0.2 秒的转场）
    min_frames = max(1, int(round(fps * MIN_SHOT_SECONDS)))
    merged: list[list[int]] = []
    for start, end in spans:
        if merged and end - start < min_frames:
            merged[-1][1] = end
        else:
            merged.append([start, end])

    shots = []
    for start, end in merged:
        start_seconds = start / fps
        end_seconds = end / fps
        key = max(start_seconds + 0.3, end_seconds - KEY_LEAD_SECONDS)
        key = min(key, max(start_seconds, end_seconds - 0.05))
        shots.append(
            {
                "index": len(shots) + 1,
                "start": round(start_seconds, 2),
                "end": round(end_seconds, 2),
                "key": round(key, 2),
                "frames": end - start,
            }
        )
    return shots


def default_cache_path(workspace: str, video: str) -> str:
    stem = os.path.splitext(os.path.basename(video))[0]
    return os.path.join(workspace, "data", "goal_detect", "shots", stem + ".json")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--video", required=True)
    parser.add_argument("--out", default=None, help="缓存路径；默认 data/goal_detect/shots/<stem>.json")
    parser.add_argument("--workspace", default=None)
    parser.add_argument("--threshold", type=float, default=0.0, help="0 = 自动（p99 夹在 20~32）")
    args = parser.parse_args()

    workspace = args.workspace or os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    out_path = args.out or default_cache_path(workspace, args.video)

    try:
        diffs, fps, frames = _decode_diffs(args.video)
    except Exception as error:  # noqa: BLE001 - 失败要回给 C# 一段可读的 JSON
        print(json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False))
        return 1

    if frames == 0:
        print(json.dumps({"ok": False, "error": "解不出任何帧"}, ensure_ascii=False))
        return 1

    threshold = _pick_threshold(np.asarray(diffs, dtype=float), args.threshold)
    shots = build_shots(diffs, fps, threshold)
    payload = {
        "ok": True,
        "video": os.path.basename(args.video),
        "fps": round(fps, 3),
        "frames": frames,
        "duration": round(frames / fps, 2),
        "threshold": round(threshold, 1),
        "shots": shots,
    }

    try:
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        with open(out_path, "w", encoding="utf-8") as handle:
            json.dump(payload, handle, ensure_ascii=False, indent=1)
    except OSError as error:
        payload["cacheError"] = str(error)

    print(json.dumps(payload, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
