"""在原视频上逐帧跑门框检测，并用时间一致性自检 —— 不依赖人眼。

自检依据：真实镜头的机位在一个连续镜头内是平滑的（推拉摇移都是渐变），
所以门框的宽、高、中心点每帧变化都应当很小。检测结果如果出现突变，
要么是镜头切换（用帧差单独识别），要么就是这一帧检测错了。
"""
from __future__ import annotations

import json
import sys
import time
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from pipeline3 import detect  # noqa: E402


def quad_stats(quad):
    tl, tr, br, bl = quad
    left_h = abs(bl[1] - tl[1])
    right_h = abs(br[1] - tr[1])
    top_w = abs(tr[0] - tl[0])
    bot_w = abs(br[0] - bl[0])
    cx = sum(p[0] for p in quad) / 4.0
    cy = sum(p[1] for p in quad) / 4.0
    return {
        "cx": cx, "cy": cy,
        "w": (top_w + bot_w) / 2.0,
        "h": (left_h + right_h) / 2.0,
        "skew": abs(br[1] - bl[1]),          # 底边倾斜
        "ratio": (top_w + bot_w) / max(1.0, left_h + right_h),
    }


def main(argv):
    video = argv[1]
    start = float(argv[2]) if len(argv) > 2 else 0.0
    dur = float(argv[3]) if len(argv) > 3 else 60.0
    out = Path(argv[4] if len(argv) > 4 else "data/goal_detect/scan.json")

    cap = cv2.VideoCapture(video)
    if not cap.isOpened():
        print("打不开视频")
        return 1
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    if start > 0:
        cap.set(cv2.CAP_PROP_POS_MSEC, start * 1000.0)

    rows = []
    prev_small = None
    t0 = time.time()
    limit = int(dur * fps)

    for i in range(limit):
        ok, frame = cap.read()
        if not ok:
            break
        t = cap.get(cv2.CAP_PROP_POS_MSEC) / 1000.0

        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        small = cv2.resize(gray, (320, 180), interpolation=cv2.INTER_AREA)
        cut = False
        if prev_small is not None:
            cut = float(cv2.absdiff(small, prev_small).mean()) > 18.0
        prev_small = small

        cands, _, _ = detect(frame, topk=1)
        best = cands[0] if cands else None

        row = {
            "i": i,
            "t": round(t, 2),
            "cut": cut,
            "found": best is not None,
        }
        if best:
            row.update({
                "score": best["score"],
                "mode": best["mode"],
                "clipped": best.get("clipped", False),
                "partial": best["partial"],
                "quad": best["quad"],
            })
            row.update({k: round(v, 1) for k, v in quad_stats(best["quad"]).items()})
        rows.append(row)

        if (i + 1) % 100 == 0:
            el = time.time() - t0
            print(f"  {i+1} 帧  {el:.1f}s  {el/(i+1)*1000:.0f}ms/帧", flush=True)

    cap.release()
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")

    n = len(rows)
    hit = sum(1 for r in rows if r["found"])
    cuts = sum(1 for r in rows if r["cut"])
    print(f"\n{n} 帧，检出 {hit} ({hit/max(1,n):.1%})，镜头切换 {cuts}，"
          f"耗时 {time.time()-t0:.0f}s -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
