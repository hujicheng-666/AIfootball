"""下载 CMU 的 asf/amc 并校验完整性。

为什么要校验：mocap.cs.cmu.edu 的传输经常被中途掐断，curl 静默退出后留下的是
「帧号连续但每帧缺后半截关节」的文件——解析时缺失的关节会被当作 0°，动作就
凭空少掉一块，而且从帧数上看不出来。这里以「每帧 31 行（root + 30 关节）」为准。
"""
from __future__ import annotations

import sys
import time
import urllib.request
from pathlib import Path

CLIPS = ["10_01", "10_02", "10_03", "10_05", "10_06", "11_01"]
BASE = "http://mocap.cs.cmu.edu/subjects/{s}/{name}"
DST = Path(__file__).resolve().parents[1] / "data" / "external" / "cmu"

EXPECT_JOINTS = 29  # root + 28（amc 里不含 lhipjoint/rhipjoint 这两个虚拟关节）


def check_amc(path: Path) -> tuple[bool, str]:
    if not path.exists() or path.stat().st_size == 0:
        return False, "空文件"
    frames = 0
    cur = 0
    bad = 0
    with path.open("r", encoding="utf-8", errors="ignore") as f:
        for line in f:
            s = line.strip()
            if not s or s.startswith("#") or s.startswith(":"):
                continue
            head = s.split()[0]
            if head.isdigit():
                if frames and cur != EXPECT_JOINTS:
                    bad += 1
                frames += 1
                cur = 0
            elif head in ("root",) or head.isalpha():
                cur += 1
    if frames and cur != EXPECT_JOINTS:
        bad += 1
    if frames < 20:
        return False, f"仅 {frames} 帧"
    if bad > 0:
        return False, f"{frames} 帧中有 {bad} 帧关节不全"
    return True, f"{frames} 帧完整"


def fetch(url: str, dst: Path) -> bool:
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0"})
        with urllib.request.urlopen(req, timeout=180) as r:
            data = r.read()
        dst.write_bytes(data)
        return True
    except Exception as e:                                     # noqa: BLE001
        print(f"    下载异常 {type(e).__name__}: {e}")
        return False


def main():
    DST.mkdir(parents=True, exist_ok=True)
    targets = []
    for c in CLIPS:
        targets.append((f"{c.split('_')[0]}.asf", None))
        targets.append((f"{c}.amc", c))
    seen = set()
    for name, clip in targets:
        if name in seen:
            continue
        seen.add(name)
        dst = DST / name
        subj = name.split(".")[0].split("_")[0]
        url = BASE.format(s=subj, name=name)
        for attempt in range(4):
            ok, msg = (check_amc(dst) if name.endswith(".amc") else (dst.exists() and dst.stat().st_size > 3000, ""))
            if ok:
                print(f"{name:12s} 已有完整 {msg}")
                break
            print(f"{name:12s} 第 {attempt + 1} 次下载 …")
            if fetch(url, dst) and name.endswith(".amc"):
                ok, msg = check_amc(dst)
                print(f"  → {dst.stat().st_size}B  {msg}")
                if ok:
                    break
            time.sleep(1)
    print("\n=== 最终状态 ===")
    for p in sorted(DST.glob("*.amc")):
        ok, msg = check_amc(p)
        print(f"  {p.name:12s} {'OK ' if ok else 'BAD'} {msg}")


if __name__ == "__main__":
    main()
