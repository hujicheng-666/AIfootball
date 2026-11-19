"""把逐帧候选聚成"过线事件"：一簇连续候选 = 一次射门，簇内找过线瞬间。

单帧候选没有意义：门框附近的任何误检都会产生候选。真正的进球在中弹时表现
为一条连续、单调的轨迹——球从门框外侧（below_line > 0）移动到门框内部
（below_line < 0）。本脚本据此把候选帧压缩成少量事件，交给人眼终审。

判据：
  1. 簇：相邻候选帧间隔 <= max_gap 帧
  2. 过线：簇内 below_line_px 由正变负的那一对帧
  3. 位移方向：簇内 signed_dist 必须递增（球在向门内运动）
  4. 持续：簇至少 min_frames 帧，抑制单帧误检

用法：
  python cross_events.py --in cross_candidates.json
"""
from __future__ import annotations

import argparse
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def cluster(hits: list[dict], max_gap: int = 5) -> list[list[dict]]:
    """按帧号间隔把候选帧聚成簇。"""
    if not hits:
        return []
    hits = sorted(hits, key=lambda h: h["i"])
    out, cur = [], [hits[0]]
    for h in hits[1:]:
        if h["i"] - cur[-1]["i"] <= max_gap:
            cur.append(h)
        else:
            out.append(cur)
            cur = [h]
    out.append(cur)
    return out


def event_from_cluster(cl: list[dict], min_frames: int = 2) -> dict | None:
    """从一簇候选里判定是否构成过线事件。"""
    if len(cl) < min_frames:
        return None
    f0, f1 = cl[0], cl[-1]
    below = [h["below_line_px"] for h in cl]
    dists = [h["signed_dist"] for h in cl]

    # 过线瞬间：below_line 由正转负
    cross_at = None
    for a, b in zip(cl, cl[1:]):
        if a["below_line_px"] > 0 >= b["below_line_px"]:
            cross_at = b
            break

    # 入射：signed_dist 递增（球在靠近/进入门框）
    approach = dists[-1] - dists[0]

    return {
        "f_start": f0["i"], "f_end": f1["i"],
        "t_start": f0["t"], "t_end": f1["t"],
        "n_frames": len(cl),
        "f_cross": cross_at["i"] if cross_at else None,
        "t_cross": cross_at["t"] if cross_at else None,
        "below_start": below[0], "below_end": below[-1],
        "dist_start": dists[0], "dist_end": dists[-1],
        "approach": round(approach, 1),
        "max_conf": round(max(h["conf"] for h in cl), 2),
        "inside_any": any(h["inside"] for h in cl),
        "crossed": cross_at is not None,
        "frames": [{"i": h["i"], "t": h["t"], "conf": h["conf"],
                    "below": h["below_line_px"], "dist": h["signed_dist"]}
                   for h in cl],
    }


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", default=os.path.join(
        HERE, "..", "..", "data", "goal_detect", "cross_candidates.json"))
    ap.add_argument("--out", default=os.path.join(
        HERE, "..", "..", "data", "goal_detect", "cross_events.json"))
    ap.add_argument("--max-gap", type=int, default=5)
    ap.add_argument("--min-frames", type=int, default=2)
    args = ap.parse_args()

    j = json.load(open(args.inp, encoding="utf-8"))
    video = j.get("video")
    events = []
    n_hits = 0
    for s in j["segments"]:
        n_hits += len(s["hits"])
        for cl in cluster(s["hits"], args.max_gap):
            ev = event_from_cluster(cl, args.min_frames)
            if ev is None:
                continue
            ev["seg"] = s["seg"]
            ev["seg_t0"] = s["t0"]
            ev["seg_t1"] = s["t1"]
            events.append(ev)

    crossed = [e for e in events if e["crossed"]]
    inside_only = [e for e in events if e["inside_any"] and not e["crossed"]]

    print(f"候选帧 {n_hits} -> 事件 {len(events)}")
    print(f"  确认过线 {len(crossed)}")
    print(f"  门框内但未见跨线 {len(inside_only)}")
    print(f"  其他（擦柱/门线附近）{len(events) - len(crossed) - len(inside_only)}")
    print()
    for e in sorted(events, key=lambda x: (x["crossed"], x["max_conf"]), reverse=True):
        flag = "过线" if e["crossed"] else ("框内" if e["inside_any"] else "邻近")
        ct = f"{e['t_cross']:6.2f}s" if e["t_cross"] is not None else "      "
        print(f"  seg{e['seg']:3d} {flag} {e['t_start']:6.2f}-{e['t_end']:6.2f}s "
              f"过线{ct} 帧{e['n_frames']:2d} conf{e['max_conf']:.2f} "
              f"approach{e['approach']:+7.1f}")

    os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump({"video": video, "total_hits": n_hits, "events": events},
                  f, ensure_ascii=False, indent=1)
    print(f"\n-> {os.path.abspath(args.out)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
