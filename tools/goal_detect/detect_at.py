"""按秒数抽帧跑门框检测，只处理指定帧。

标注页把整段合集开放成时间轴，用户拖到某一帧点球之后，才需要知道这一帧的门线
在哪。全程 8000 帧扫描（70 分钟）没必要，这里只解用户真正标记的那几帧。

优先顺序在 C# 侧：先用预扫描的 trusted_segments.json，没命中才落到这个脚本。

用法：
    python detect_at.py --video <mp4> --seconds 124.2 [--seconds 260.9] [--out out.json]

输出：{"video": ..., "results": [{"seconds", "found", "score", "mode", "quad", "rect", ...}]}
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import cv2

sys.path.insert(0, str(Path(__file__).resolve().parent))
from pipeline3 import detect  # noqa: E402


def rect_from_quad(quad):
    """门框矩形 + 原始四点。

    四点要一起带回去：机位带倾斜时四个角并不正交，用包围盒会把门线压成水平线，
    左右两端能差出几十像素，球在门线哪一侧就会判错。
    """
    xs = [float(p[0]) for p in quad]
    ys = [float(p[1]) for p in quad]
    return {
        "cx": (min(xs) + max(xs)) / 2.0,
        "cy": (min(ys) + max(ys)) / 2.0,
        "w": max(xs) - min(xs),
        "h": max(ys) - min(ys),
        "quad": [[float(p[0]), float(p[1])] for p in quad],
    }


def grab(cap, seconds):
    """定位到指定秒。CAP_PROP_POS_MSEC 在部分容器上不可靠，失败就退回按帧号。"""
    cap.set(cv2.CAP_PROP_POS_MSEC, seconds * 1000.0)
    ok, frame = cap.read()
    if ok and frame is not None:
        return frame
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    cap.set(cv2.CAP_PROP_POS_FRAMES, max(0, int(round(seconds * fps))))
    ok, frame = cap.read()
    return frame if ok else None


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--video", required=True)
    ap.add_argument("--seconds", type=float, action="append", required=True)
    ap.add_argument("--out")
    args = ap.parse_args()

    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        print(json.dumps({"error": f"打不开视频：{args.video}"}, ensure_ascii=False))
        return 1

    results = []
    for sec in args.seconds:
        frame = grab(cap, sec)
        if frame is None:
            results.append({"seconds": sec, "found": False, "reason": "取不到这一帧"})
            continue
        cands, vs, hs = detect(frame)
        best = cands[0] if cands else None
        item = {
            "seconds": sec,
            "found": bool(best),
            "verticalCount": len(vs),
            "horizontalCount": len(hs),
        }
        if best:
            item["score"] = round(float(best["score"]), 4)
            item["mode"] = best["mode"]
            item["quad"] = best["quad"]
            item["rect"] = rect_from_quad(best["quad"])
            item["partial"] = bool(best.get("partial"))
            item["clipped"] = bool(best.get("clipped"))
        results.append(item)

    cap.release()
    payload = {"video": args.video, "results": results}
    text = json.dumps(payload, ensure_ascii=False)
    if args.out:
        Path(args.out).write_text(text, encoding="utf-8")
    print(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
