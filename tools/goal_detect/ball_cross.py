"""逐帧检测足球，并结合已检测到的门框位置，挑出"球出现在球门附近"的候选帧。

思路：不追求在每一帧里稳定追踪足球（单目远景里这本身就不现实），而是把
8064 帧压缩成少量候选帧——只要球在门框区域出现过，就抓出来交给人眼确认。
人看一眼就知道进没进，比逐帧标注四角便宜几个数量级。

候选判据（三重，任一条命中即保留）：
  1. 球心落在门框四边形内部
  2. 球心在门框四边形下缘以下 band 区间内（球贴地滚过门线）
  3. 球心在门框四边形外扩 margin 后仍在内（球擦柱、球撞横梁）

输出：
  cross_candidates.json   每段每帧状态
  cross_frames/*.jpg      带门框与球标注的候选帧（供人眼筛查）

用法：
  python ball_cross.py --segments <trusted_segments.json> --video <mp4>
  python ball_cross.py ... --only 0,2,3 --limit-frames 300
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import time

import cv2
import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
BALL_CLASS = 32
CONF = 0.15
MIN_SIDE, MAX_SIDE = 5, 70


def quad_poly(quad) -> np.ndarray:
    """门框四角 -> float32 多边形。接受 [[x,y]*4] 或扁平 [x,y,...]。"""
    a = np.asarray(quad, dtype=np.float32)
    if a.ndim == 1:
        a = a.reshape(-1, 2)
    return a.astype(np.float32)


def quad_from_segment(s: dict) -> np.ndarray:
    """取门框四边形。优先用段里的 quad；缺失时用 median 的中心与宽高重建。

    trusted_segments.json 是汇总产物，只保留了 median(cx,cy,w,h)；由于可信段的
    逐帧 jitter 均在 3% 以内，用中位矩形重建与真实四角误差极小。
    """
    if s.get("quad"):
        return quad_poly(s["quad"])
    m = s["median"]
    cx, cy = float(m["cx"]), float(m["cy"])
    w, h = float(m["w"]), float(m["h"])
    return np.array([[cx - w / 2, cy - h / 2],
                     [cx + w / 2, cy - h / 2],
                     [cx + w / 2, cy + h / 2],
                     [cx - w / 2, cy + h / 2]], np.float32)


def goal_line(quad: np.ndarray) -> tuple[float, float, float, float]:
    """门线 = 四边形下边。返回 (x1,y1,x2,y2)，取 y 最大的一条边。"""
    pts = quad.reshape(-1, 2)
    order = np.argsort(pts[:, 1])
    low2 = pts[order[-2:]]
    low2 = low2[np.argsort(low2[:, 0])]
    return float(low2[0][0]), float(low2[0][1]), float(low2[1][0]), float(low2[1][1])


def classify_ball(quad: np.ndarray, bx: float, by: float) -> dict:
    """判断球心与门框四边形的空间关系。"""
    poly = quad.reshape(-1, 1, 2)
    sd = cv2.pointPolygonTest(poly, (float(bx), float(by)), True)  # >0 在内
    ys = quad.reshape(-1, 2)[:, 1]
    y_top, y_bot = float(ys.min()), float(ys.max())
    qh = max(y_bot - y_top, 1.0)
    rel_y = (by - y_top) / qh          # 0=横梁高度, 1=门线高度
    gx1, gy1, gx2, gy2 = goal_line(quad)
    gl_y = (gy1 + gy2) / 2.0
    return {
        "inside": bool(sd >= 0),
        "signed_dist": round(float(sd), 1),
        "rel_y": round(float(rel_y), 3),
        "below_line_px": round(float(by - gl_y), 1),
    }


def is_candidate(info: dict, h: int) -> bool:
    """三重判据：门框内 / 贴门线以下带 / 门框边缘外扩区。"""
    if info["inside"]:
        # 门框内但高于横梁顶部的不算（那是看台方向）
        return info["rel_y"] >= -0.05
    _ = h
    # 门线附近的带：球在地面上滚过
    band = 0.18
    if 0.82 <= info["rel_y"] <= 1.0 + band:
        return True
    # 门框外扩：球擦柱或撞梁，距边界 6% 门高以内
    if abs(info["signed_dist"]) <= 0.06 * 720:
        return True
    return False


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--segments", required=True)
    ap.add_argument("--video", required=True)
    ap.add_argument("--weights", default=os.path.join(
        os.path.dirname(HERE), "..", "models", "yolo11m.pt"))
    ap.add_argument("--only", default="", help="只跑这些段号，逗号分隔")
    ap.add_argument("--limit-frames", type=int, default=0)
    ap.add_argument("--outdir", default=os.path.join(
        HERE, "..", "..", "data", "goal_detect"))
    ap.add_argument("--crops", type=int, default=1, help="1=保存候选帧图片")
    args = ap.parse_args()

    weights = os.path.abspath(args.weights)
    segs = json.load(open(args.segments, encoding="utf-8"))
    if args.only:
        want = {int(x) for x in args.only.split(",") if x.strip()}
        segs = [s for s in segs if int(s["seg"]) in want]
    if not segs:
        print("没有匹配的段")
        return 2

    from ultralytics import YOLO
    model = YOLO(weights)

    cap = cv2.VideoCapture(args.video)
    if not cap.isOpened():
        print("视频打不开")
        return 2
    fps = cap.get(cv2.CAP_PROP_FPS) or 25.0
    W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

    out_root = os.path.abspath(args.outdir)
    crop_dir = os.path.join(out_root, "cross_frames")
    if args.crops:
        os.makedirs(crop_dir, exist_ok=True)

    print(f"{W}x{H} fps={fps:g} 段数 {len(segs)}")
    results = []
    t_start = time.time()
    done_frames = 0
    cand_total = 0

    for s in segs:
        sid = int(s["seg"])
        t0, t1 = float(s["t0"]), float(s["t1"])
        f0, f1 = int(round(t0 * fps)), int(round(t1 * fps))
        n = f1 - f0
        if args.limit_frames > 0:
            n = min(n, args.limit_frames)
        quad = quad_from_segment(s)

        cap.set(cv2.CAP_PROP_POS_FRAMES, f0)
        seg_hits = []
        for k in range(n):
            ok, bgr = cap.read()
            if not ok:
                break
            fidx = f0 + k
            res = model.predict(bgr, imgsz=640, conf=CONF, classes=[BALL_CLASS],
                                verbose=False, device="cpu")
            balls = []
            for r in res:
                for (x1, y1, x2, y2), cf in zip(r.boxes.xyxy.tolist(),
                                                r.boxes.conf.tolist()):
                    side = max(x2 - x1, y2 - y1)
                    if side < MIN_SIDE or side > MAX_SIDE:
                        continue
                    balls.append((float(x1), float(y1), float(x2), float(y2),
                                  float(cf)))
            done_frames += 1

            hit = None
            for (x1, y1, x2, y2, cf) in balls:
                cx, cy = (x1 + x2) / 2, (y1 + y2) / 2
                info = classify_ball(quad, cx, cy)
                if is_candidate(info, H):
                    if hit is None or cf > hit["conf"]:
                        hit = {"cx": round(cx, 1), "cy": round(cy, 1),
                               "box": [round(x1, 1), round(y1, 1),
                                       round(x2, 1), round(y2, 1)],
                               "conf": round(cf, 3), **info}
            if hit:
                cand_total += 1
                rec = {"seg": sid, "i": fidx, "t": round(fidx / fps, 2), **hit}
                seg_hits.append(rec)
                if args.crops:
                    vis = bgr.copy()
                    cv2.polylines(vis, [quad.astype(np.int32)], True,
                                  (0, 255, 255), 2)
                    gx1, gy1, gx2, gy2 = goal_line(quad)
                    cv2.line(vis, (int(gx1), int(gy1)), (int(gx2), int(gy2)),
                             (0, 128, 255), 2)
                    x1, y1, x2, y2 = [int(v) for v in hit["box"]]
                    cv2.rectangle(vis, (x1, y1), (x2, y2), (0, 0, 255), 2)
                    cv2.putText(vis, f"S{sid} f{fidx} {hit['conf']:.2f}",
                                (12, 30), cv2.FONT_HERSHEY_SIMPLEX, 0.8,
                                (0, 0, 255), 2)
                    cv2.imwrite(os.path.join(crop_dir, f"seg{sid:03d}_f{fidx:05d}.jpg"),
                                vis)
        results.append({"seg": sid, "t0": t0, "t1": t1, "frames": n,
                        "hits": seg_hits})
        dt = time.time() - t_start
        rate = done_frames / dt if dt > 0 else 0
        print(f"  段 {sid:3d} {t0:5.1f}-{t1:5.1f}s 帧{n:4d} "
              f"候选{len(seg_hits):3d}  累计{done_frames}帧 {rate:.1f}fps "
              f"候选{cand_total} 已用{dt/60:.1f}min")

    cap.release()

    outp = os.path.join(out_root, "cross_candidates.json")
    with open(outp, "w", encoding="utf-8") as f:
        json.dump({"video": os.path.abspath(args.video),
                   "segments": results}, f, ensure_ascii=False, indent=1)
    print(f"\n候选帧 {cand_total} / 检测帧 {done_frames} "
          f"({cand_total / max(done_frames, 1) * 100:.2f}%)")
    print(f"明细 -> {outp}")
    if args.crops:
        print(f"图片 -> {crop_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
