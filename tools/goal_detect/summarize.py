"""汇总可信段，输出可直接用于标定的结果。"""
from __future__ import annotations

import json
import statistics
import sys
from pathlib import Path


def main(argv):
    segs = json.loads(Path(argv[1]).read_text(encoding="utf-8"))
    with_med = [s for s in segs if s.get("median")]
    trusted = [s for s in with_med if s["trusted"]]

    print(f"可信段 {len(trusted)} / {len(with_med)} 个有门框的段")
    frames = sum(s["frames"] for s in trusted)
    print(f"覆盖 {frames} 帧 = {frames/25:.0f} 秒（视频共 8064 帧 / 322 秒）")
    print()

    print("前 24 个可信段：")
    for s in trusted[:24]:
        m = s["median"]
        print(f"  {s['t0']:6.1f}~{s['t1']:6.1f}s  {s['frames']:3d}帧  "
              f"w={m['w']:.0f} h={m['h']:.0f} 中心=({m['cx']:.0f},{m['cy']:.0f})")

    durs = [s["frames"] / 25 for s in trusted]
    if durs:
        print()
        print(f"段时长 中位={statistics.median(durs):.1f}s "
              f"最长={max(durs):.1f}s 最短={min(durs):.1f}s")
        print(f">=3 秒的段: {sum(1 for d in durs if d >= 3)} / {len(durs)}")

    # 按时间排序输出，便于直接喂给标定流程
    ordered = sorted(trusted, key=lambda s: s["t0"])
    out = Path(argv[2] if len(argv) > 2 else "data/goal_detect/trusted_segments.json")
    out.write_text(json.dumps(ordered, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"\n已写出 {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
