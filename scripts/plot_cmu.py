"""把 CMU 动捕的骨架画出来，肉眼核对正向运动学是否正确。

判据（不用看视频也能判断 FK 错没错）：
  · 站立帧膝盖应朝前、脚掌贴地；
  · 侧视图里躯干朝向 +Z（CMU 的「前」）；
  · 踢腿帧抬起的脚应在前上方，膝盖不反折。
"""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib
matplotlib.rcParams['font.sans-serif'] = ['Microsoft YaHei', 'SimHei', 'DejaVu Sans']
matplotlib.rcParams['axes.unicode_minus'] = False
import matplotlib.pyplot as plt
import numpy as np

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "scripts"))

from cmu_amc import load_sequence  # noqa: E402

BONES = [
    ("root", "lowerback"), ("lowerback", "upperback"), ("upperback", "thorax"),
    ("thorax", "lowerneck"), ("lowerneck", "upperneck"), ("upperneck", "head"),
    ("thorax", "lclavicle"), ("lclavicle", "lhumerus"), ("lhumerus", "lradius"),
    ("lradius", "lwrist"), ("lwrist", "lhand"),
    ("thorax", "rclavicle"), ("rclavicle", "rhumerus"), ("rhumerus", "rradius"),
    ("rradius", "rwrist"), ("rwrist", "rhand"),
    ("root", "lhipjoint"), ("lhipjoint", "lfemur"), ("lfemur", "ltibia"),
    ("ltibia", "lfoot"), ("lfoot", "ltoes"),
    ("root", "rhipjoint"), ("rhipjoint", "rfemur"), ("rfemur", "rtibia"),
    ("rtibia", "rfoot"), ("rfoot", "rtoes"),
]

# 侧视：横轴 Z(前)、纵轴 Y(上)；正视：横轴 X、纵轴 Y
PROJ = {"side": (2, 1, "Z 前 →"), "front": (0, 1, "X 左 →")}


def draw(ax, pos, view, color, lw=2.2, alpha=1.0):
    a, b, _ = PROJ[view]
    for p, c in BONES:
        if p not in pos or c not in pos:
            continue
        ax.plot([pos[p][a], pos[c][a]], [pos[p][b], pos[c][b]],
                color=color, lw=lw, alpha=alpha, solid_capstyle="round")
    pts = np.array([pos[k] for k in pos])
    ax.plot(pts[:, a], pts[:, b], ".", color=color, ms=3, alpha=alpha)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clip", default="10_01")
    ap.add_argument("--frames", type=int, default=12)
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    root = REPO / "data" / "external" / "cmu"
    subj = args.clip.split("_")[0]
    keys, frames, _ = load_sequence(root / f"{subj}.asf", root / f"{args.clip}.amc")

    n = args.frames
    idx = np.linspace(0, len(frames) - 1, n).astype(int)
    out = Path(args.out) if args.out else REPO / "output" / "shooter-cmu" / f"{args.clip}_rig.png"
    out.parent.mkdir(parents=True, exist_ok=True)

    fig, axes = plt.subplots(2, n, figsize=(2.5 * n, 9.0))
    for col, i in enumerate(idx):
        raw = frames[i][0]
        # 以髋为水平原点，否则人物会贴在画框边上看不清
        c = raw["root"]
        pos = {k: v - np.array([c[0], 0.0, c[2]]) for k, v in raw.items()}
        for row, view in enumerate(("side", "front")):
            ax = axes[row, col]
            draw(ax, pos, view, "#c8a21a")
            # 地面
            ax.axhline(0, color="#888", lw=0.6, ls="--", alpha=0.6)
            ax.axvline(0, color="#ccc", lw=0.5, ls=":", alpha=0.8)
            ax.set_xlim(-22, 22)
            ax.set_ylim(-2, 32)
            ax.set_aspect("equal")
            ax.set_xticks([]); ax.set_yticks([])
            if col == 0:
                ax.set_ylabel("侧视 Z→" if row == 0 else "正视 X 左→", fontsize=8)
            if row == 0:
                ax.set_title(f"{args.clip} 帧{i} ({i/120:.2f}s)", fontsize=8)

    fig.suptitle(f"CMU {args.clip}  FK 校验（上：侧视 / 下：正视）", fontsize=11)
    fig.tight_layout(rect=(0, 0, 1, 0.95))
    fig.savefig(out, dpi=110, facecolor="white")
    print("saved", out)

    # 数值自检：膝盖朝向与地面接触
    pos = frames[len(frames) // 4][0]
    hip, knee, ankle = pos["lfemur"], pos["ltibia"], pos["lfoot"]
    print(f"静止相：髋{np.round(hip,2)} 膝{np.round(knee,2)} 踝{np.round(ankle,2)}")
    print(f"  膝在髋下方 {hip[1]-knee[1]:.2f}，踝在膝下方 {knee[1]-ankle[1]:.2f}")
    print(f"  躯干朝向：thorax→lowerneck 的 Z 分量 {(pos['lowerneck']-pos['thorax'])[2]:+.2f}（应接近 0）")
    print(f"  最低点 y = {min(p[1] for p in pos.values()):.2f}（应接近 0）")


if __name__ == "__main__":
    main()
