"""分析逐帧扫描结果：用镜头内的时间一致性找出检测错误帧。"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np


def main(argv):
    rows = json.loads(Path(argv[1]).read_text(encoding="utf-8"))
    if not rows:
        print("空")
        return 1

    # 按镜头切换分段
    segs, cur = [], []
    for r in rows:
        if r["cut"] and cur:
            segs.append(cur)
            cur = []
        cur.append(r)
    if cur:
        segs.append(cur)

    n = len(rows)
    hit = sum(1 for r in rows if r["found"])
    print(f"共 {n} 帧，检出 {hit} ({hit/n:.1%})，切成 {len(segs)} 段\n")

    # 段内相邻帧突变
    jumps = []
    for seg in segs:
        for a, b in zip(seg, seg[1:]):
            if not (a["found"] and b["found"]):
                continue
            dw = abs(b["w"] - a["w"]) / max(1.0, a["w"])
            dh = abs(b["h"] - a["h"]) / max(1.0, a["h"])
            dcx = abs(b["cx"] - a["cx"])
            dcy = abs(b["cy"] - a["cy"])
            if dw > 0.12 or dh > 0.12 or dcx > 35 or dcy > 35:
                jumps.append({
                    "from": a["i"], "to": b["i"],
                    "dw": round(dw, 2), "dh": round(dh, 2),
                    "dcx": round(dcx), "dcy": round(dcy),
                    "t": a["t"],
                })

    print(f"段内突变 {len(jumps)} 处（正常镜头不该跳）:")
    for j in jumps[:25]:
        print(f"  {j['from']}->{j['to']} t={j['t']:.1f}s  "
              f"Δw={j['dw']:.0%} Δh={j['dh']:.0%} Δcx={j['dcx']} Δcy={j['dcy']}")

    # 每段概况
    print("\n分段概况:")
    for i, seg in enumerate(segs):
        found = [r for r in seg if r["found"]]
        clipped = sum(1 for r in found if r.get("clipped"))
        ws = [r["w"] for r in found]
        hs = [r["h"] for r in found]
        cov = f"w {min(ws):.0f}~{max(ws):.0f}  h {min(hs):.0f}~{max(hs):.0f}" if found else "-"
        print(f"  段{i:2d} t={seg[0]['t']:6.1f}~{seg[-1]['t']:6.1f}s "
              f"{len(seg):3d}帧 检出{len(found):3d} ({len(found)/len(seg):3.0%}) "
              f"clipped={clipped:2d}  {cov}")

    # 检出帧里，尺寸异常（对 720p 而言）
    if argv[2:] and argv[2] == "outliers":
        print("\n尺寸异常帧（宽<80 或 高<40 或 宽>1000）:")
        for r in rows:
            if not r["found"]:
                continue
            if r["w"] < 80 or r["h"] < 40 or r["w"] > 1000:
                print(f"  t={r['t']:.1f}s w={r['w']:.0f} h={r['h']:.0f} "
                      f"score={r['score']:.2f} mode={r['mode']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
