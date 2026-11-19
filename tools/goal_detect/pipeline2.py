"""门框检测 v2。

v1 的致命假设是「必须有两根完整发达的门柱」，而实战里：
- 门柱会被门将/球员遮挡
- 焦段长时机位远离，门柱在画面里很细
- 球门常被裁掉一部分，只剩三个角

v2 改用三条互补的假设生成路径，并对不可靠的角明确标注：

  A. 两柱配对      —— 两根柱都清晰（v1 的主路径，参数放宽）
  B. 横梁 + 单柱   —— 横梁一端接柱，另一端推断（f023 的典型形态）
  C. 横梁 + 对称   —— 只有横梁，按真实门宽高比 3:1 推两根柱

另外加入了「边的白色覆盖率」判定，四角里哪些是实测、哪些是推出来的，分开标。
"""
from __future__ import annotations

import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from pipeline import horizontal_strips, runs, tophat_horizontal, tophat_vertical  # noqa: E402


# ---------- 更宽容的条检测 ----------

def vertical_strips_multi(gray, thresholds=(28, 40, 55), kw=33, min_ratio=0.09):
    """多个 top-hat 阈值取并集，减少漏检；同一条竖线只保留最强的表示。"""
    h = gray.shape[0]
    min_len = int(h * min_ratio)
    merged = {}

    for th in thresholds:
        mask = tophat_vertical(gray, kw) > th
        w = gray.shape[1]
        per_col = {}
        for x in range(w):
            rs = runs(mask[:, x], min_len)
            if rs:
                per_col[x] = max(rs, key=lambda t: t[1] - t[0])

        cur = None
        strips = []
        for x in sorted(per_col):
            y0, y1 = per_col[x]
            if cur and x - cur["x1"] <= 4 and min(cur["y1"], y1) - max(cur["y0"], y0) > 0:
                cur["x1"] = x
                cur["y0"] = min(cur["y0"], y0)
                cur["y1"] = max(cur["y1"], y1)
            else:
                if cur:
                    strips.append(cur)
                cur = {"x0": x, "x1": x, "y0": y0, "y1": y1}
        if cur:
            strips.append(cur)

        for s in strips:
            key = None
            for k in merged:
                if abs(merged[k]["x"] - (s["x0"] + s["x1"]) / 2.0) < 10 and \
                        min(merged[k]["y_bot"], s["y1"]) - max(merged[k]["y_top"], s["y0"]) > 0:
                    key = k
                    break
            cand = {
                "x": (s["x0"] + s["x1"]) / 2.0,
                "y_top": float(s["y0"]),
                "y_bot": float(s["y1"]),
                "len": float(s["y1"] - s["y0"]),
                "width": float(s["x1"] - s["x0"] + 1),
            }
            if key is None:
                merged[len(merged)] = cand
            elif cand["len"] > merged[key]["len"]:
                merged[key] = cand

    out = list(merged.values())
    for v in out:
        v["score_len"] = v["len"] / h
    out.sort(key=lambda s: -s["len"])
    return out


def horizontal_strips_multi(gray, thresholds=(28, 40, 55), kh=33, min_ratio=0.10):
    h, w = gray.shape
    min_len = int(w * min_ratio)
    merged = {}

    for th in thresholds:
        mask = tophat_horizontal(gray, kh) > th
        per_row = {}
        for y in range(h):
            rs = runs(mask[y, :], min_len)
            if rs:
                per_row[y] = max(rs, key=lambda t: t[1] - t[0])

        cur = None
        strips = []
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

        for s in strips:
            key = None
            for k in merged:
                if abs(merged[k]["y"] - (s["y0"] + s["y1"]) / 2.0) < 10 and \
                        min(merged[k]["x_right"], s["x1"]) - max(merged[k]["x_left"], s["x0"]) > 0:
                    key = k
                    break
            cand = {
                "y": (s["y0"] + s["y1"]) / 2.0,
                "x_left": float(s["x0"]),
                "x_right": float(s["x1"]),
                "len": float(s["x1"] - s["x0"]),
            }
            if key is None:
                merged[len(merged)] = cand
            elif cand["len"] > merged[key]["len"]:
                merged[key] = cand

    out = list(merged.values())
    out.sort(key=lambda s: -s["len"])
    return out


# ---------- 边的可靠性 ----------

def edge_white_ratio(hsv, p0, p1, samples: int = 20) -> float:
    """沿线段采样，看有多大概率落在「亮且不太彩」的像素上（门柱/横梁是浅色）。

    hsv 由调用方预先算好传入 —— 之前这里每次都对整幅图做一次 cvtColor，
    四条边就是四次全图转换，是主要的性能瓶颈。
    """
    h, w = hsv.shape[:2]
    hits = total = 0
    for t in np.linspace(0.05, 0.95, samples):
        x = int(round(p0[0] + (p1[0] - p0[0]) * t))
        y = int(round(p0[1] + (p1[1] - p0[1]) * t))
        total += 1
        if not (0 <= y < h and 0 <= x < w):
            continue
        patch = hsv[max(0, y - 2):y + 3, max(0, x - 2):x + 3]
        if not patch.size:
            continue
        v = patch[..., 2].astype(np.float32).mean()
        s = patch[..., 1].astype(np.float32).mean()
        if v > 120 and s < 90:
            hits += 1
    return hits / max(1, total)


# ---------- 候选生成 ----------

def _mk_quad(a, b):
    """a,b 是左右两根柱，返回 TL,TR,BR,BL。"""
    if a["x"] > b["x"]:
        a, b = b, a
    return ([(a["x"], a["y_top"]), (b["x"], b["y_top"]),
             (b["x"], b["y_bot"]), (a["x"], a["y_bot"])],
            {"left": a, "right": b})


def _plausible(quad, h, w, min_w_ratio=0.09, max_w_ratio=0.95):
    tl, tr, br, bl = quad
    span = abs(tr[0] - tl[0])
    if not (w * min_w_ratio <= span <= w * max_w_ratio):
        return False, f"宽{span:.0f}越界"
    lh = abs(bl[1] - tl[1])
    rh = abs(br[1] - tr[1])
    if lh < h * 0.09 or rh < h * 0.09:
        return False, "柱太短"
    if not (tl[1] < bl[1] and tr[1] < br[1]):
        return False, "上下颠倒"
    ratio = span / max(1.0, (lh + rh) / 2.0)
    if not (1.2 <= ratio <= 8.0):
        return False, f"宽高比{ratio:.1f}"
    return True, ""


def candidates_from_two_posts(vs, h, w):
    out = []
    cand = [v for v in vs if v["len"] >= h * 0.09][:14]
    for i in range(len(cand)):
        for j in range(i + 1, len(cand)):
            a, b = cand[i], cand[j]
            if a["x"] > b["x"]:
                a, b = b, a
            span = b["x"] - a["x"]
            if span < w * 0.09:
                continue
            la, lb = a["len"], b["len"]
            if min(la, lb) / max(la, lb) < 0.40:
                continue
            if abs(a["y_bot"] - b["y_bot"]) > h * 0.24:
                continue
            if abs(a["y_top"] - b["y_top"]) > h * 0.26:
                continue
            quad, meta = _mk_quad(a, b)
            ok, why = _plausible(quad, h, w)
            if not ok:
                continue
            out.append({"quad": quad, "meta": meta, "mode": "two_posts"})
    return out


def _post_near(x, y, vs, tol_x=14, tol_y=26):
    best = None
    for v in vs:
        if abs(v["x"] - x) <= tol_x and abs(v["y_top"] - y) <= tol_y:
            if best is None or v["len"] > best["len"]:
                best = v
    return best


def candidates_from_beam(vs, hs, h, w):
    """横梁 + 至少一根柱：另一端用已知柱长度推。"""
    out = []
    for beam in hs:
        if beam["len"] < w * 0.14:
            continue
        y = beam["y"]
        left = _post_near(beam["x_left"], y, vs)
        right = _post_near(beam["x_right"], y, vs)

        if left and right:
            quad, meta = _mk_quad(left, right)
        elif left and not right:
            # 推断右侧柱：与左侧同顶部、同长度（透视差异留 15% 余量，只作候选）
            inferred = {"x": beam["x_right"], "y_top": y,
                        "y_bot": y + left["len"], "len": left["len"], "width": 3.0}
            quad, meta = _mk_quad(left, inferred)
            meta["inferred_right"] = True
        elif right and not left:
            inferred = {"x": beam["x_left"], "y_top": y,
                        "y_bot": y + right["len"], "len": right["len"], "width": 3.0}
            quad, meta = _mk_quad(inferred, right)
            meta["inferred_left"] = True
        else:
            # 两根柱都没对上：只用横梁，按真实 3:1 推
            span = beam["len"]
            post_h = span / 3.0
            if post_h < h * 0.08 or post_h > h * 0.95:
                continue
            inferred_l = {"x": beam["x_left"], "y_top": y, "y_bot": y + post_h,
                          "len": post_h, "width": 3.0}
            inferred_r = {"x": beam["x_right"], "y_top": y, "y_bot": y + post_h,
                          "len": post_h, "width": 3.0}
            quad = [(inferred_l["x"], y), (inferred_r["x"], y),
                    (inferred_r["x"], y + post_h), (inferred_l["x"], y + post_h)]
            meta = {"inferred_left": True, "inferred_right": True,
                    "left": inferred_l, "right": inferred_r}
            out.append({"quad": quad, "meta": meta, "mode": "beam_sym",
                        "beam": beam, "penalty": 0.18})
            continue

        ok, _ = _plausible(quad, h, w)
        if ok:
            out.append({"quad": quad, "meta": meta, "mode": "beam_post",
                        "beam": beam, "penalty": 0.06})
    return out


# ---------- 打分 ----------

def score_candidate(cand, bgr, hsv, hs, h, w):
    quad = cand["quad"]
    tl, tr, br, bl = quad
    span = abs(tr[0] - tl[0])
    lh = abs(bl[1] - tl[1])
    rh = abs(br[1] - tr[1])
    avg_h = (lh + rh) / 2.0

    ratios = [
        edge_white_ratio(hsv, tl, tr),
        edge_white_ratio(hsv, tr, br),
        edge_white_ratio(hsv, br, bl),
        edge_white_ratio(hsv, bl, tl),
    ]
    white_score = float(np.mean(ratios))

    # 左右柱长度应接近；差得越多越可疑（透视会带来差，但不会差一倍）
    sym = 1.0 - min(1.0, abs(lh - rh) / max(lh, rh))
    # 门的真实宽高比 3.0，考虑透视放宽
    ratio = span / max(1.0, avg_h)
    ratio_pen = 1.0 - min(1.0, abs(ratio - 3.0) / 2.8)
    # 横梁贴合度
    beam_ok = 0.0
    top_y = (tl[1] + tr[1]) / 2.0
    for b in hs:
        if b["len"] < span * 0.55:
            continue
        if abs(b["y"] - top_y) <= h * 0.07 and \
                min(b["x_right"], max(tl[0], tr[0])) - max(b["x_left"], min(tl[0], tr[0])) > span * 0.4:
            beam_ok = 1.0
            break

    s = (0.30 * white_score
         + 0.20 * sym
         + 0.15 * ratio_pen
         + 0.15 * min(1.0, avg_h / (h * 0.45))
         + 0.20 * beam_ok)
    s -= cand.get("penalty", 0.0)
    return max(0.0, min(1.0, s)), ratios


def is_clipped(quad, w, h, margin=0.02) -> bool:
    """门框触到画面边界，或被撑到几乎整幅宽 —— 说明球门没被完整拍进来。"""
    for x, y in quad:
        if x < w * margin or x > w * (1 - margin) or y < h * margin or y > h * (1 - margin):
            return True
    span = abs(quad[1][0] - quad[0][0])
    return span > w * 0.80


def dedupe(cands, w, h, topk=3):
    cands.sort(key=lambda c: -c["score"])
    picked = []
    for c in cands:
        q = c["quad"]
        cx = sum(p[0] for p in q) / 4.0
        cy = sum(p[1] for p in q) / 4.0
        dup = False
        for p in picked:
            pq = p["quad"]
            px = sum(t[0] for t in pq) / 4.0
            py = sum(t[1] for t in pq) / 4.0
            if math.hypot(cx - px, cy - py) < w * 0.06:
                dup = True
                break
        if not dup:
            picked.append(c)
        if len(picked) >= topk:
            break
    return picked


def green_ratio(bgr) -> float:
    """草地占比。点球全景画面里草地必然占很大一块；
    门将特写、球网特写、广告牌特写这类镜头草地几乎没有 —— 它们里面不可能有完整球门。"""
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    g = cv2.inRange(hsv, np.array([33, 35, 35]), np.array([90, 255, 255]))
    return float(g.mean()) / 255.0


_person_model = None


def person_max_height_ratio(bgr) -> float:
    """画面里最大那个人的高度占比。

    门将特写/球员特写里人几乎占满画面（>0.45），而这类镜头里根本不存在完整球门，
    之前给它们拼出来的框全是误报。全景镜头里人最多占 1/4，不会误伤。
    """
    global _person_model
    if _person_model is None:
        from ultralytics import YOLO
        _person_model = YOLO(str(Path(__file__).resolve().parents[2] / "models" / "yolo11m.pt"))
    res = _person_model(bgr, classes=[0], conf=0.35, imgsz=640, verbose=False)[0]
    if res.boxes is None or len(res.boxes) == 0:
        return 0.0
    h = bgr.shape[0]
    best = 0.0
    for b in res.boxes:
        _, y1, _, y2 = [float(v) for v in b.xyxy[0]]
        best = max(best, (y2 - y1) / h)
    return best


def detect(bgr, topk=3):
    h, w = bgr.shape[:2]
    gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)
    vs = vertical_strips_multi(gray)
    hs = horizontal_strips_multi(gray)
    grass = green_ratio(bgr)
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)

    raw = candidates_from_two_posts(vs, h, w)
    raw += candidates_from_beam(vs, hs, h, w)

    for c in raw:
        sc, ratios = score_candidate(c, bgr, hsv, hs, h, w)
        # 被画面裁掉的门框没法用来做落点标定（你看到的「只有三个角」就是这种），
        # 不能直接丢掉（否则漏检），但必须排到完整门框后面并显式标记。
        c["clipped"] = is_clipped(c["quad"], w, h)
        if c["clipped"]:
            sc *= 0.55

        # 草地占比只记录、不作门槛：门后视角的球门画面里草地可能几乎为零
        # （你确认存在的 f021 就是 grass=0.01），拿它压制会误杀真门框。
        c["grass"] = round(grass, 3)

        # 尺寸过小的框在标定里没有意义
        span = abs(c["quad"][1][0] - c["quad"][0][0])
        if span < w * 0.10:
            sc *= 0.55

        c["score"] = round(float(sc), 4)
        c["edge_white"] = [round(float(r), 2) for r in ratios]
        # 任一条边白色覆盖低于 0.35，就认为该边是推出来的、不可靠
        c["unreliable_edges"] = [i for i, r in enumerate(ratios) if r < 0.35]
        c["partial"] = bool(c["unreliable_edges"] or
                            c["meta"].get("inferred_left") or
                            c["meta"].get("inferred_right"))

    return dedupe(raw, w, h, topk), vs, hs


# ---------- 输出 ----------

def draw(bgr, verticals, horizontals, best, out_path: Path, title: str):
    img = bgr.copy()
    for v in verticals[:14]:
        cv2.line(img, (int(v["x"]), int(v["y_top"])), (int(v["x"]), int(v["y_bot"])),
                 (0, 200, 255), 1, cv2.LINE_AA)
    for hs in horizontals[:8]:
        cv2.line(img, (int(hs["x_left"]), int(hs["y"])), (int(hs["x_right"]), int(hs["y"])),
                 (255, 120, 0), 1, cv2.LINE_AA)

    if best:
        q = best["quad"]
        for i in range(4):
            p0, p1 = q[i], q[(i + 1) % 4]
            color = (0, 255, 0) if i not in best["unreliable_edges"] else (0, 140, 255)
            thick = 3 if i not in best["unreliable_edges"] else 2
            cv2.line(img, (int(p0[0]), int(p0[1])), (int(p1[0]), int(p1[1])),
                     color, thick, cv2.LINE_AA)
        for k, p in enumerate(q):
            inferred = (k == 3 and best["meta"].get("inferred_left")) or \
                       (k == 2 and best["meta"].get("inferred_right")) or \
                       (k == 0 and best["meta"].get("inferred_left")) or \
                       (k == 1 and best["meta"].get("inferred_right"))
            cv2.circle(img, (int(p[0]), int(p[1])), 8,
                       (255, 0, 255) if inferred else (0, 0, 255), -1)
            label = "TL TR BR BL".split()[k] + ("*" if inferred else "")
            cv2.putText(img, label, (int(p[0]) + 10, int(p[1]) - 10),
                        cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 255), 2, cv2.LINE_AA)
        tag = f"{best['score']:.3f} {best['mode']}"
        if best["partial"]:
            tag += " PARTIAL"
        if best.get("clipped"):
            tag += " CLIPPED"
        cv2.putText(img, tag, (16, 44), cv2.FONT_HERSHEY_SIMPLEX, 1.0,
                    (0, 255, 0) if not best["partial"] else (0, 160, 255), 3, cv2.LINE_AA)
        cv2.putText(img, "edge " + " ".join(f"{r:.2f}" for r in best["edge_white"]),
                    (16, 84), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (200, 255, 200), 2, cv2.LINE_AA)
    else:
        cv2.putText(img, "NO GOAL", (16, 48), cv2.FONT_HERSHEY_SIMPLEX, 1.2,
                    (0, 0, 255), 3, cv2.LINE_AA)

    cv2.putText(img, title, (16, img.shape[0] - 16), cv2.FONT_HERSHEY_SIMPLEX,
                0.8, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.imwrite(str(out_path), img)


def main(argv):
    frames_dir = Path(argv[1] if len(argv) > 1 else "data/goal_detect/frames")
    out_dir = Path(argv[2] if len(argv) > 2 else "data/goal_detect/out3")
    out_dir.mkdir(parents=True, exist_ok=True)

    labels_path = frames_dir.parent / "labels.json"
    labels = {}
    if labels_path.exists():
        labels = json.loads(labels_path.read_text(encoding="utf-8"))

    results = []
    correct = missing = wrong = unseen_ok = 0
    for fp in sorted(frames_dir.glob("*.png")):
        bgr = cv2.imread(str(fp))
        if bgr is None:
            continue
        cands, vs, hs = detect(bgr)
        best = cands[0] if cands else None
        draw(bgr, vs, hs, best, out_dir / f"{fp.stem}_goal.png", fp.stem)

        stem = fp.stem
        truth = labels.get(stem, "")
        if truth == "ok":
            correct += 1 if best else 0
        elif truth == "miss":
            missing += 0 if best else 1
        elif truth == "bad":
            wrong += 1 if best else 0
        elif truth == "unseen":
            unseen_ok += 0 if best else 1

        results.append({
            "frame": fp.name, "label": truth,
            "verticals": len(vs), "horizontals": len(hs), "best": best,
        })
        tag = (f"{best['score']:.3f} {best['mode']} grass={best.get('grass', 0):.2f}"
               f"{' PARTIAL' if best['partial'] else ''}"
               f"{' CLIPPED' if best.get('clipped') else ''} {best['quad']}") if best else "无"
        print(f"{fp.name}: [{truth:6s}] 竖{len(vs):2d} 横{len(hs):2d} -> {tag}")

    (out_dir / "results.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

    n_ok = sum(1 for r in results if r["label"] == "ok")
    print(f"\n评测：应检出 {n_ok}，实际正确 {correct}；"
          f"该漏未漏 {missing}/4；误报 {wrong}/2；不可见时正确沉默 {unseen_ok}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
