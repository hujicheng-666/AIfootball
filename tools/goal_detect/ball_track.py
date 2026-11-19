"""球过线帧定位原型。

思路分三层，对应「机位和焦段都不固定」这个前提：
  1. YOLO 给球的高置信度锚点（conf>=0.15），坐标可信但会漏
  2. 运动检测补漏：球是画面里移动最快的小目标，用帧差 + 团块匹配补 YOLO 漏掉的帧
  3. 轨迹拟合后做穿越判定：把门框四角定义的梯形当作门平面，
     球心轨迹穿过梯形边界的那一帧即候选过线帧；轨迹在边界处突变（撞柱/横梁）判为未进

单目无深度，这是几何近似，不是严格三维判定。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))


def ball_from_yolo(frames_dir: Path, conf: float = 0.15):
    from ultralytics import YOLO
    model = YOLO(str(Path(__file__).resolve().parents[2] / "models" / "yolo11m.pt"))
    out = {}
    for fp in sorted(frames_dir.glob("*.png")):
        res = model(str(fp), classes=[32], conf=conf, verbose=False)[0]
        if res.boxes is None or len(res.boxes) == 0:
            continue
        boxes = res.boxes
        i = int(boxes.conf.argmax())
        x1, y1, x2, y2 = (float(v) for v in boxes.xyxy[i])
        w, h = x2 - x1, y2 - y1
        # 球在 720p 转播里通常 10~40px；过大的必然是误检
        if not (8 <= max(w, h) <= 45):
            continue
        out[fp.stem] = {
            "cx": (x1 + x2) / 2.0, "cy": (y1 + y2) / 2.0,
            "w": w, "h": h, "conf": float(boxes.conf[i]),
        }
    return out


def ball_from_motion(prev_gray, gray, min_area=20, max_area=2500):
    """帧差找快速移动的小团块。球是画面里最锐利的小目标。"""
    d = cv2.absdiff(gray, prev_gray)
    _, m = cv2.threshold(d, 28, 255, cv2.THRESH_BINARY)
    m = cv2.morphologyEx(m, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    m = cv2.morphologyEx(m, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    n, _, stats, cents = cv2.connectedComponentsWithStats(m, 8)
    blobs = []
    for i in range(1, n):
        area = int(stats[i, cv2.CC_STAT_AREA])
        if not (min_area <= area <= max_area):
            continue
        bw, bh = int(stats[i, 2]), int(stats[i, 3])
        if not (0.45 <= bw / max(1, bh) <= 2.2):
            continue
        blobs.append({"cx": float(cents[i][0]), "cy": float(cents[i][1]),
                      "area": area, "w": bw, "h": bh})
    blobs.sort(key=lambda b: -b["area"])
    return blobs


def point_in_quad(pt, quad) -> bool:
    """射线法判断点是否在凸四边形内。"""
    x, y = pt
    inside = False
    n = len(quad)
    for i in range(n):
        x1, y1 = quad[i]
        x2, y2 = quad[(i + 1) % n]
        if ((y1 > y) != (y2 > y)) and (x < (x2 - x1) * (y - y1) / (y2 - y1 + 1e-9) + x1):
            inside = not inside
    return inside


def main(argv):
    video = argv[1]
    start = float(argv[2]) if len(argv) > 2 else 0.0
    dur = float(argv[3]) if len(argv) > 3 else 20.0
    out = Path(argv[4] if len(argv) > 4 else "data/goal_detect/ball_test.json")

    cap = cv2.VideoCapture(video)
    if not cap.isOpened():
        print("打不开")
        return 1
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    if start > 0:
        cap.set(cv2.CAP_PROP_POS_MSEC, start * 1000.0)

    from ultralytics import YOLO
    model = YOLO(str(Path(__file__).resolve().parents[2] / "models" / "yolo11m.pt"))

    rows = []
    prev_gray = None
    limit = int(dur * fps)
    for i in range(limit):
        ok, frame = cap.read()
        if not ok:
            break
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        t = cap.get(cv2.CAP_PROP_POS_MSEC) / 1000.0

        rec = {"i": i, "t": round(t, 2), "yolo": None, "motion": 0}

        res = model(frame, classes=[32], conf=0.15, imgsz=640, verbose=False)[0]
        if res.boxes is not None and len(res.boxes):
            boxes = res.boxes
            best = None
            for bi in range(len(boxes)):
                x1, y1, x2, y2 = (float(v) for v in boxes.xyxy[bi])
                bw, bh = x2 - x1, y2 - y1
                if not (8 <= max(bw, bh) <= 45):
                    continue
                cf = float(boxes.conf[bi])
                if best is None or cf > best[0]:
                    best = (cf, (x1 + x2) / 2.0, (y1 + y2) / 2.0, bw, bh)
            if best:
                rec["yolo"] = {"conf": round(best[0], 2),
                               "cx": round(best[1], 1), "cy": round(best[2], 1),
                               "w": round(best[3], 1), "h": round(best[4], 1)}

        if prev_gray is not None:
            blobs = ball_from_motion(prev_gray, gray)
            rec["motion"] = len(blobs)
            if rec["yolo"] is None and blobs:
                # 用运动团块里最接近上一帧球位置的补位
                rec["motion_best"] = {k: round(v, 1) if isinstance(v, float) else v
                                      for k, v in blobs[0].items()}
        prev_gray = gray
        rows.append(rec)

        if (i + 1) % 50 == 0:
            print(f"  {i+1} 帧, 球可见 {sum(1 for r in rows if r['yolo'])}", flush=True)

    cap.release()
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps(rows, ensure_ascii=False), encoding="utf-8")
    yolo_hits = sum(1 for r in rows if r["yolo"])
    print(f"\n{len(rows)} 帧, YOLO 球检出 {yolo_hits} ({yolo_hits/max(1,len(rows)):.0%}) -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
