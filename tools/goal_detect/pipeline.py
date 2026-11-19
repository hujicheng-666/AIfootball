"""点球视频自动分析原型：门柱/横梁检测 + 过线帧定位。

设计原则（针对「机位和焦段都在变」这一点）：
- 不做全局标定，**每一帧独立检测**，检测不到就明确报告失败，绝不拿上一帧糊弄
- 门柱物理特征：比左右邻域亮的**竖直细条** → 水平 top-hat
- 横梁物理特征：比上下邻域亮的**水平细条** → 竖直 top-hat
- 由「两柱 + 一梁」直接得到门框四角，天然适配任意焦段
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np


# ---------- 特征提取 ----------

def tophat_vertical(gray: np.ndarray, kw: int = 33) -> np.ndarray:
    """亮竖直细条（门柱）。水平结构元会抹掉宽亮区、留下窄竖线。"""
    k = cv2.getStructuringElement(cv2.MORPH_RECT, (kw, 1))
    return cv2.morphologyEx(gray, cv2.MORPH_TOPHAT, k)


def tophat_horizontal(gray: np.ndarray, kh: int = 33) -> np.ndarray:
    """亮水平细条（横梁）。竖直结构元会抹掉高亮区、留下扁横线。"""
    k = cv2.getStructuringElement(cv2.MORPH_RECT, (1, kh))
    return cv2.morphologyEx(gray, cv2.MORPH_TOPHAT, k)


def runs(mask1d: np.ndarray, min_len: int):
    out, s = [], None
    for y, v in enumerate(mask1d):
        if v and s is None:
            s = y
        elif not v and s is not None:
            if y - s >= min_len:
                out.append((s, y))
            s = None
    if s is not None and len(mask1d) - s >= min_len:
        out.append((s, len(mask1d)))
    return out


# ---------- 条（strip）检测 ----------

def vertical_strips(gray: np.ndarray, th: int = 40, kw: int = 33,
                    min_ratio: float = 0.10):
    h, w = gray.shape
    mask = tophat_vertical(gray, kw) > th
    min_len = int(h * min_ratio)

    # 每列取最长连续段
    per_col = {}
    for x in range(w):
        rs = runs(mask[:, x], min_len)
        if rs:
            per_col[x] = max(rs, key=lambda t: t[1] - t[0])

    # 相邻列聚成条
    strips = []
    cur = None
    for x in sorted(per_col):
        y0, y1 = per_col[x]
        if cur and x - cur["x1"] <= 4 and min(cur["y1"], y1) - max(cur["y0"], y0) > 0:
            cur["x1"] = x
            cur["y0"] = min(cur["y0"], y0)
            cur["y1"] = max(cur["y1"], y1)
            cur["n"] += 1
        else:
            if cur:
                strips.append(cur)
            cur = {"x0": x, "x1": x, "y0": y0, "y1": y1, "n": 1}
    if cur:
        strips.append(cur)

    out = []
    for s in strips:
        length = s["y1"] - s["y0"]
        out.append({
            "x": (s["x0"] + s["x1"]) / 2.0,
            "y_top": float(s["y0"]),
            "y_bot": float(s["y1"]),
            "len": float(length),
            "width": float(s["x1"] - s["x0"] + 1),
            "score_len": length / h,
        })
    out.sort(key=lambda s: -s["len"])
    return out


def horizontal_strips(gray: np.ndarray, th: int = 40, kh: int = 33,
                      min_ratio: float = 0.12):
    h, w = gray.shape
    mask = tophat_horizontal(gray, kh) > th
    min_len = int(w * min_ratio)

    per_row = {}
    for y in range(h):
        rs = runs(mask[y, :], min_len)
        if rs:
            per_row[y] = max(rs, key=lambda t: t[1] - t[0])

    strips, cur = [], None
    for y in sorted(per_row):
        x0, x1 = per_row[y]
        if cur and y - cur["y1"] <= 4 and min(cur["x1"], x1) - max(cur["x0"], x0) > 0:
            cur["y1"] = y
            cur["x0"] = min(cur["x0"], x0)
            cur["x1"] = max(cur["x1"], x1)
        else:
            if cur:
                strips.append(cur)
            cur = {"y0": y, "y1": y, "x0": x0, "x1": x1}
    if cur:
        strips.append(cur)

    out = []
    for s in strips:
        out.append({
            "y": (s["y0"] + s["y1"]) / 2.0,
            "x_left": float(s["x0"]),
            "x_right": float(s["x1"]),
            "len": float(s["x1"] - s["x0"]),
        })
    out.sort(key=lambda s: -s["len"])
    return out


# ---------- 门框假设 ----------

def build_goal(verticals, horizontals, h, w):
    """两柱 + 横梁 → 门框四角，返回带分数候选列表。"""
    cands = []
    vs = [v for v in verticals if v["len"] >= h * 0.12][:10]
    for i in range(len(vs)):
        for j in range(i + 1, len(vs)):
            a, b = vs[i], vs[j]
            if a["x"] > b["x"]:
                a, b = b, a
            span = b["x"] - a["x"]
            if span < w * 0.15:
                continue
            la, lb = a["len"], b["len"]
            if min(la, lb) / max(la, lb) < 0.55:
                continue
            if abs(a["y_bot"] - b["y_bot"]) > h * 0.16:
                continue
            if abs(a["y_top"] - b["y_top"]) > h * 0.20:
                continue

            tl = (a["x"], a["y_top"])
            tr = (b["x"], b["y_top"])
            br = (b["x"], b["y_bot"])
            bl = (a["x"], a["y_bot"])

            # 横梁：找一条与上边贴合的水平条
            top_y = (a["y_top"] + b["y_top"]) / 2.0
            beam_bonus = 0.0
            for hs in horizontals:
                if hs["len"] < span * 0.6:
                    continue
                if abs(hs["y"] - top_y) > h * 0.09:
                    continue
                ov = min(hs["x_right"], max(tl[0], tr[0])) - max(hs["x_left"], min(tl[0], tr[0]))
                if ov > span * 0.5:
                    beam_bonus = 0.22
                    break

            goal_w = span
            goal_h = (abs(bl[1] - tl[1]) + abs(br[1] - tr[1])) / 2.0
            ratio = goal_w / max(1.0, goal_h)
            if not (1.5 <= ratio <= 6.5):
                continue

            vert_err = (abs(bl[0] - tl[0]) / max(1.0, goal_h)
                        + abs(br[0] - tr[0]) / max(1.0, goal_h)) / 2.0
            v_score = max(0.0, 1.0 - vert_err / 0.30)
            len_score = min(1.0, min(la, lb) / (h * 0.45))
            sym_score = 1.0 - min(1.0, abs(la - lb) / max(la, lb))
            base_y = max(bl[1], br[1]) / h

            score = (0.30 * v_score + 0.25 * len_score + 0.20 * sym_score
                     + 0.15 * min(1.0, span / (w * 0.5)) + beam_bonus)
            cands.append({
                "score": round(float(score), 4),
                "quad": [[round(float(p[0]), 1), round(float(p[1]), 1)]
                         for p in (tl, tr, br, bl)],
                "beam": beam_bonus > 0,
                "base_y": round(float(base_y), 3),
            })

    cands.sort(key=lambda c: -c["score"])
    return cands


# ---------- 可视化 ----------

def draw(bgr, verticals, horizontals, best, out_path: Path, title: str):
    img = bgr.copy()
    for v in verticals[:12]:
        cv2.line(img, (int(v["x"]), int(v["y_top"])), (int(v["x"]), int(v["y_bot"])),
                 (0, 200, 255), 1, cv2.LINE_AA)
    for hs in horizontals[:8]:
        cv2.line(img, (int(hs["x_left"]), int(hs["y"])), (int(hs["x_right"]), int(hs["y"])),
                 (255, 120, 0), 1, cv2.LINE_AA)
    if best:
        pts = np.array(best["quad"], dtype=np.int32).reshape(-1, 1, 2)
        cv2.polylines(img, [pts], True, (0, 255, 0), 3)
        for k, p in enumerate(best["quad"]):
            cv2.circle(img, (int(p[0]), int(p[1])), 8, (0, 0, 255), -1)
            cv2.putText(img, "TL TR BR BL".split()[k], (int(p[0]) + 10, int(p[1]) - 10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2, cv2.LINE_AA)
        cv2.putText(img, f"{best['score']:.3f}", (16, 44),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.2, (0, 255, 0), 3, cv2.LINE_AA)
    else:
        cv2.putText(img, "NO GOAL", (16, 44),
                    cv2.FONT_HERSHEY_SIMPLEX, 1.2, (0, 0, 255), 3, cv2.LINE_AA)
    cv2.putText(img, title, (16, img.shape[0] - 18),
                cv2.FONT_HERSHEY_SIMPLEX, 0.8, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.imwrite(str(out_path), img)


def main(argv):
    frames_dir = Path(argv[1] if len(argv) > 1 else "data/goal_detect/frames")
    out_dir = Path(argv[2] if len(argv) > 2 else "data/goal_detect/out2")
    out_dir.mkdir(parents=True, exist_ok=True)

    results = []
    for fp in sorted(frames_dir.glob("*.png")):
        bgr = cv2.imread(str(fp))
        if bgr is None:
            continue
        h, w = bgr.shape[:2]
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)

        vs = vertical_strips(gray)
        hs = horizontal_strips(gray)
        cands = build_goal(vs, hs, h, w)
        best = cands[0] if cands else None

        draw(bgr, vs, hs, best, out_dir / f"{fp.stem}_goal.png", fp.stem)
        results.append({
            "frame": fp.name,
            "verticals": len(vs),
            "horizontals": len(hs),
            "best": best,
            "alts": cands[1:3],
        })
        tag = f"{best['score']:.3f} {best['quad']}" if best else "无"
        print(f"{fp.name}: 竖条{len(vs):2d} 横条{len(hs):2d} -> {tag}")

    (out_dir / "results.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")
    ok = sum(1 for r in results if r["best"])
    print(f"\n命中 {ok}/{len(results)} 帧 -> {out_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
