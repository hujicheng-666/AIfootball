#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""把姿态 JSON 画成骨架序列图（侧视 + 正前），用于校验动作本身是否合理。

只依赖 JSON 里的关节位置，不经过 Unity，因此可以做"数据层"的独立验证：
渲染管线出问题时，先确认数据对不对。
"""

import json
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

REPO = Path(__file__).resolve().parents[1]

# 骨骼连线（关节名 -> 关节名）
LINKS = [
    ("Center of Hips", "Center of Body"), ("Center of Body", "Center of Shoulder"),
    ("Center of Shoulder", "Neck"), ("Neck", "Head"),
    ("Center of Hips", "Left Hip"), ("Left Hip", "Left Knee"), ("Left Knee", "Left Ankle"),
    ("Center of Hips", "Right Hip"), ("Right Hip", "Right Knee"), ("Right Knee", "Right Ankle"),
    ("Center of Shoulder", "Left Shoulder"), ("Left Shoulder", "Left Elbow"), ("Left Elbow", "Left Wrist"),
    ("Center of Shoulder", "Right Shoulder"), ("Right Shoulder", "Right Elbow"), ("Right Elbow", "Right Wrist"),
]

LEFT_COLOR = "#2E86DE"
RIGHT_COLOR = "#E74C3C"
BODY_COLOR = "#27AE60"


def draw(ax, P, names, title, axis_h="z", axis_v="y"):
    idx = {n: i for i, n in enumerate(names)}
    ah, av = ("xyz".index(axis_h), "xyz".index(axis_v))
    for a, b in LINKS:
        if a not in idx or b not in idx:
            continue
        pa, pb = P[idx[a]], P[idx[b]]
        col = LEFT_COLOR if ("Left" in a or "Left" in b) else (
            RIGHT_COLOR if ("Right" in a or "Right" in b) else BODY_COLOR)
        ax.plot([pa[ah], pb[ah]], [pa[av], pb[av]], '-', color=col, lw=2.2, solid_capstyle='round')
    for n, i in idx.items():
        p = P[i]
        ax.plot(p[ah], p[av], 'o', ms=4, color='#333', zorder=5)
    ax.set_title(title, fontsize=9)
    ax.set_aspect('equal')
    ax.grid(alpha=0.25, lw=0.5)
    ax.axhline(0, color='#999', lw=0.5, ls='--')


def main(json_path, out_path, ncols=9):
    d = json.loads(Path(json_path).read_text(encoding="utf-8"))
    names = d["jointNames"]
    frames = d["frames"]
    n = len(frames)
    # 均匀覆盖整段，而不是只取前 ncols 个
    picks = np.unique(np.linspace(0, n - 1, ncols).astype(int))

    fig, axes = plt.subplots(2, len(picks), figsize=(len(picks) * 2.0, 7.0))
    if len(picks) == 1:
        axes = np.array([[axes[0]], [axes[1]]])
    for k, fi in enumerate(picks):
        P = np.array(frames[fi]["p"]).reshape(-1, 3)
        draw(axes[0][k], P, names, "t=%.2f SIDE" % frames[fi]["t"], "z", "y")
        draw(axes[1][k], P, names, "t=%.2f FRONT" % frames[fi]["t"], "x", "y")
    for row in axes:
        for a in row:
            a.set_xlim(-0.95, 0.95)
            a.set_ylim(-0.85, 0.65)
            a.tick_params(labelsize=6)
    fig.suptitle("%s   %d frames / %.2fs" % (Path(json_path).name, n, frames[-1]["t"]), fontsize=10)
    fig.tight_layout(rect=(0, 0, 1, 0.95))
    fig.savefig(out_path, dpi=120)
    print("写出", out_path)


if __name__ == "__main__":
    src = sys.argv[1] if len(sys.argv) > 1 else str(
        REPO / "Myproject/Assets/PenaltyKick/Resources/ShooterPoses/SYN17.json")
    dst = sys.argv[2] if len(sys.argv) > 2 else str(REPO / ".tmp_synth/skeleton.png")
    Path(dst).parent.mkdir(parents=True, exist_ok=True)
    main(src, dst)
