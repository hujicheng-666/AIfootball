"""门框检测 v3：形态学 + 连通域，比 v2 快一个数量级以上。

v2 的瓶颈在于「逐列/逐行用 Python 循环找连续段」，1280 列 × 3 个阈值 = 近 4000 次
Python 调用，实测 850ms/帧。其实 OpenCV 的**开运算**本身就等价于「只保留长度
超过结构元的部分」：
  - 竖核 (1, L) 开运算 → 只留下竖直方向不低于 L 像素的连通块
  - 横核 (L, 1) 开运算 → 只留下水平方向不低于 L 像素的连通块
再用 connectedComponentsWithStats 直接拿到每条线的包围盒，全部在 C 层完成。

配对、打分、部分可见标记沿用 v2 的策略（那部分逻辑是对的）。
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from pipeline2 import (  # noqa: E402
    candidates_from_beam,
    candidates_from_two_posts,
    dedupe,
    edge_white_ratio,
    green_ratio,
    is_clipped,
    score_candidate,
)
from pipeline import tophat_horizontal, tophat_vertical  # noqa: E402


def _components(mask: np.ndarray, min_side: int, vertical: bool, min_area: int):
    """在开运算后的掩码里取连通域，返回条状候选。"""
    k = cv2.getStructuringElement(
        cv2.MORPH_RECT, (1, min_side) if vertical else (min_side, 1))
    opened = cv2.morphologyEx(mask, cv2.MORPH_OPEN, k)
    n, _, stats, _ = cv2.connectedComponentsWithStats(opened, 8)
    out = []
    for i in range(1, n):
        x, y, w, h, area = (int(v) for v in stats[i])
        if area < min_area:
            continue
        if vertical:
            if h < min_side or w > h * 0.9:      # 太短或太粗，不像门柱
                continue
            out.append({"x": x + w / 2.0, "y_top": float(y), "y_bot": float(y + h),
                        "len": float(h), "width": float(w)})
        else:
            if w < min_side or h > w * 0.9:
                continue
            out.append({"y": y + h / 2.0, "x_left": float(x), "x_right": float(x + w),
                        "len": float(w), "width": float(h)})
    return out


def vertical_strips_fast(gray: np.ndarray, min_ratio: float = 0.08):
    h = gray.shape[0]
    min_side = max(12, int(h * min_ratio))
    # 只算一次 top-hat，三个阈值共用
    th_img = tophat_vertical(gray, 33)
    seen = {}
    for th in (26, 38, 52):
        mask = (th_img > th).astype(np.uint8) * 255
        for s in _components(mask, min_side, True, min_side * 2):
            key = None
            for k, v in seen.items():
                if abs(v["x"] - s["x"]) < 10 and \
                        min(v["y_bot"], s["y_bot"]) - max(v["y_top"], s["y_top"]) > 0:
                    key = k
                    break
            if key is None:
                seen[len(seen)] = s
            elif s["len"] > seen[key]["len"]:
                seen[key] = s
    out = list(seen.values())
    for v in out:
        v["score_len"] = v["len"] / h
    out.sort(key=lambda s: -s["len"])
    return out


def horizontal_strips_fast(gray: np.ndarray, min_ratio: float = 0.10):
    w = gray.shape[1]
    min_side = max(16, int(w * min_ratio))
    th_img = tophat_horizontal(gray, 33)
    seen = {}
    for th in (26, 38, 52):
        mask = (th_img > th).astype(np.uint8) * 255
        for s in _components(mask, min_side, False, min_side * 2):
            key = None
            for k, v in seen.items():
                if abs(v["y"] - s["y"]) < 10 and \
                        min(v["x_right"], s["x_right"]) - max(v["x_left"], s["x_left"]) > 0:
                    key = k
                    break
            if key is None:
                seen[len(seen)] = s
            elif s["len"] > seen[key]["len"]:
                seen[key] = s
    out = list(seen.values())
    out.sort(key=lambda s: -s["len"])
    return out


def detect(bgr, topk=3):
    h, w = bgr.shape[:2]
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    vs = vertical_strips_fast(gray)
    hs = horizontal_strips_fast(gray)
    grass = green_ratio(bgr)
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)

    raw = candidates_from_two_posts(vs, h, w)
    raw += candidates_from_beam(vs, hs, h, w)

    for c in raw:
        sc, ratios = score_candidate(c, bgr, hsv, hs, h, w)
        c["clipped"] = is_clipped(c["quad"], w, h)
        if c["clipped"]:
            sc *= 0.55
        c["grass"] = round(grass, 3)
        span = abs(c["quad"][1][0] - c["quad"][0][0])
        if span < w * 0.10:
            sc *= 0.55
        c["score"] = round(float(sc), 4)
        c["edge_white"] = [round(float(r), 2) for r in ratios]
        c["unreliable_edges"] = [i for i, r in enumerate(ratios) if r < 0.35]
        c["partial"] = bool(c["unreliable_edges"] or
                            c["meta"].get("inferred_left") or
                            c["meta"].get("inferred_right"))

    return dedupe(raw, w, h, topk), vs, hs


def draw(bgr, verticals, horizontals, best, out_path: Path, title: str):
    img = bgr.copy()
    for v in verticals[:16]:
        cv2.line(img, (int(v["x"]), int(v["y_top"])), (int(v["x"]), int(v["y_bot"])),
                 (0, 200, 255), 1, cv2.LINE_AA)
    for hs in horizontals[:10]:
        cv2.line(img, (int(hs["x_left"]), int(hs["y"])), (int(hs["x_right"]), int(hs["y"])),
                 (255, 120, 0), 1, cv2.LINE_AA)
    if best:
        q = best["quad"]
        for i in range(4):
            p0, p1 = q[i], q[(i + 1) % 4]
            color = (0, 140, 255) if i in best["unreliable_edges"] else (0, 255, 0)
            cv2.line(img, (int(p0[0]), int(p0[1])), (int(p1[0]), int(p1[1])),
                     color, 3, cv2.LINE_AA)
        for k, p in enumerate(q):
            cv2.circle(img, (int(p[0]), int(p[1])), 7, (0, 0, 255), -1)
            cv2.putText(img, "TL TR BR BL".split()[k], (int(p[0]) + 9, int(p[1]) - 9),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2, cv2.LINE_AA)
        tag = f"{best['score']:.3f} {best['mode']}"
        if best["partial"]:
            tag += " P"
        if best.get("clipped"):
            tag += " C"
        cv2.putText(img, tag, (16, 44), cv2.FONT_HERSHEY_SIMPLEX, 1.0,
                    (0, 255, 0), 3, cv2.LINE_AA)
    else:
        cv2.putText(img, "NO GOAL", (16, 48), cv2.FONT_HERSHEY_SIMPLEX, 1.2,
                    (0, 0, 255), 3, cv2.LINE_AA)
    cv2.putText(img, title, (16, img.shape[0] - 16), cv2.FONT_HERSHEY_SIMPLEX,
                0.8, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.imwrite(str(out_path), img)


def main(argv):
    frames_dir = Path(argv[1] if len(argv) > 1 else "data/goal_detect/frames")
    out_dir = Path(argv[2] if len(argv) > 2 else "data/goal_detect/out5")
    out_dir.mkdir(parents=True, exist_ok=True)

    labels = {}
    lp = frames_dir.parent / "labels.json"
    if lp.exists():
        labels = json.loads(lp.read_text(encoding="utf-8"))

    results = []
    for fp in sorted(frames_dir.glob("*.png")):
        bgr = cv2.imread(str(fp))
        if bgr is None:
            continue
        cands, vs, hs = detect(bgr)
        best = cands[0] if cands else None
        draw(bgr, vs, hs, best, out_dir / f"{fp.stem}_goal.png", fp.stem)
        results.append({"frame": fp.name, "label": labels.get(fp.stem, ""),
                        "verticals": len(vs), "horizontals": len(hs), "best": best})
        tag = (f"{best['score']:.3f} {best['mode']} {best['quad']}") if best else "无"
        print(f"{fp.name}: [{results[-1]['label']:7s}] 竖{len(vs):2d} 横{len(hs):2d} -> {tag}")

    (out_dir / "results.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    ok = sum(1 for r in results if r["best"])
    print(f"\n命中 {ok}/{len(results)} -> {out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
