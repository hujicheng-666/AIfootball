"""把逐帧扫描结果压成「镜头段」，并判定每段的门框是否可信。

原理：同一镜头内机位是连续的（推拉摇移都是渐变），所以门框的宽高和中心
在相邻帧之间不该突变。用段内标准差判定：稳定的段可信，抖动的段不可信。
这样就不需要人眼看图，机器自己就能说出「哪些帧的门框是可靠的」。
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np


def main(argv):
    rows = json.loads(Path(argv[1]).read_text(encoding="utf-8"))
    out = Path(argv[2] if len(argv) > 2 else "data/goal_detect/segments.json")

    segs, cur = [], []
    for r in rows:
        if r["cut"] and cur:
            segs.append(cur)
            cur = []
        cur.append(r)
    if cur:
        segs.append(cur)

    result = []
    for si, seg in enumerate(segs):
        found = [r for r in seg if r["found"]]
        if len(found) < 8:
            result.append({
                "seg": si, "t0": seg[0]["t"], "t1": seg[-1]["t"],
                "frames": len(seg), "found": len(found),
                "trusted": False, "reason": "检出帧不足8帧",
            })
            continue

        ww = np.array([r["w"] for r in found], dtype=float)
        hh = np.array([r["h"] for r in found], dtype=float)
        cx = np.array([r["cx"] for r in found], dtype=float)
        cy = np.array([r["cy"] for r in found], dtype=float)

        mw, mh = float(np.median(ww)), float(np.median(hh))
        cv_w = float(np.std(ww) / max(1.0, mw))
        cv_h = float(np.std(hh) / max(1.0, mh))
        cv_cx = float(np.std(cx) / max(1.0, mw))
        cv_cy = float(np.std(cy) / max(1.0, mh))

        trusted = False
        dw = dh = dcx = dcy = 1.0
        if len(found) > 1:
            # 关键判据用「相邻帧差」而不是「段内标准差」：
            # 镜头摇移时门框会平滑移动，段内 std 大但逐帧变化很小 —— 那是正确检测；
            # 真正错的是逐帧乱跳，此时相邻帧差才会大。
            dw = float(np.median(np.abs(np.diff(ww)))) / max(1.0, mw)
            dh = float(np.median(np.abs(np.diff(hh)))) / max(1.0, mh)
            dcx = float(np.median(np.abs(np.diff(cx)))) / max(1.0, mw)
            dcy = float(np.median(np.abs(np.diff(cy)))) / max(1.0, mh)
            trusted = (dw < 0.03 and dh < 0.03 and dcx < 0.03 and dcy < 0.03
                       and len(found) >= len(seg) * 0.5)

        # 逐角取中位数保留四点。只存 cx/cy/w/h 会退化成轴对齐包围盒，
        # 倾斜机位下门线被压平（实测底边左右能差 40px 以上 ≈ 一整行落点），
        # C# 侧 GoalRect.Corners() 有四点就一定优先用四点。
        quads = [np.array(r["quad"], dtype=float) for r in found if r.get("quad")]
        median = {"cx": round(float(np.median(cx)), 1),
                  "cy": round(float(np.median(cy)), 1),
                  "w": round(mw, 1), "h": round(mh, 1)}
        if len(quads) >= 4:
            mq = np.median(np.stack(quads), axis=0)
            median["quad"] = [[round(float(p[0]), 1), round(float(p[1]), 1)] for p in mq]

        result.append({
            "seg": si, "t0": seg[0]["t"], "t1": seg[-1]["t"],
            "frames": len(seg), "found": len(found),
            "median": median,
            "cv": {"w": round(cv_w, 3), "h": round(cv_h, 3),
                   "cx": round(cv_cx, 3), "cy": round(cv_cy, 3)},
            "jitter": {"w": round(dw, 3), "h": round(dh, 3),
                       "cx": round(dcx, 3), "cy": round(dcy, 3)},
            "trusted": bool(trusted),
        })

    out.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")

    trusted = [r for r in result if r["trusted"]]
    print(f"{len(segs)} 段，可信 {len(trusted)} 段\n")
    for r in result:
        mark = "OK " if r["trusted"] else "---"
        m = r.get("median")
        if m:
            cv = r["cv"]
            jt = r["jitter"]
            info = (f"w={m['w']:.0f} h={m['h']:.0f} "
                    f"jitter w{jt['w']:.2f} h{jt['h']:.2f} x{jt['cx']:.2f} y{jt['cy']:.2f} | "
                    f"std w{cv['w']:.2f} x{cv['cx']:.2f}")
        else:
            info = r.get("reason", "")
        print(f"  {mark} 段{r['seg']:3d} {r['t0']:6.1f}~{r['t1']:6.1f}s  "
              f"{r['found']:3d}/{r['frames']:3d}  {info}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
