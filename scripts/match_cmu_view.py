"""用 CMU 原始动捕数据渲染 8 个方位，与官方预览视频比对，确定拍摄机位。

为什么需要这一步
----------------
CMU 的预览 avi 是固定机位拍的。只知道「画面里人物朝右」并不能判断左右 ——
必须知道相机站在受试者的哪一侧。把骨架从 8 个方位各渲一张，和视频里同一时刻
的姿势对上（手臂的高度、两腿的前后关系），就能反推出机位，从而确定
数据里的 l/r 与画面左右是怎么对应的。

用法::

    python scripts/match_cmu_view.py --clip 11_01 --t 3.0 --out .tmp_3d
"""
from __future__ import annotations

import argparse
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "scripts"))

from cmu_amc import Asf, forward_kinematics, read_amc  # noqa: E402

SRC = REPO / "data" / "external" / "cmu"

# 画骨架用的连接：CMU 关节名
SEGMENTS = [
    ("root", "upperback"), ("upperback", "thorax"), ("thorax", "lowerneck"),
    ("lowerneck", "upperneck"), ("upperneck", "head"),
    ("thorax", "lhumerus"), ("thorax", "rhumerus"),
    ("lhumerus", "lradius"), ("lradius", "lwrist"), ("lwrist", "lhand"),
    ("rhumerus", "rradius"), ("rradius", "rwrist"), ("rwrist", "rhand"),
    ("root", "lfemur"), ("root", "rfemur"),
    ("lfemur", "ltibia"), ("ltibia", "lfoot"), ("lfoot", "ltoes"),
    ("rfemur", "rtibia"), ("rtibia", "rfoot"), ("rfoot", "rtoes"),
]

# 左侧暖色、右侧冷色，方便判断哪条腿在上、哪只手在前
CLR_L = (236, 150, 60)
CLR_R = (70, 160, 210)
CLR_C = (240, 206, 80)


def project(pts: np.ndarray, az_deg: float, el_deg: float, center: np.ndarray,
            scale: float, w: int, h: int, dist_cm: float = 60.0):
    """方位角 az（0 = 相机在 +Z 正前方）、仰角 el 的透视投影。

    距离用 CMU 单位：全身约 30 单位高，站太近画面里就只剩局部。
    """
    az, el = math.radians(az_deg), math.radians(el_deg)
    view = np.array([math.sin(az) * math.cos(el), math.sin(el), math.cos(az) * math.cos(el)])
    eye = center + view * dist_cm
    fwd = -view
    up0 = np.array([0.0, 1.0, 0.0])
    right = np.cross(up0, fwd)
    right /= np.linalg.norm(right)
    up = np.cross(fwd, right)

    rel = pts - eye
    x = rel @ right
    y = rel @ up
    z = rel @ fwd
    z = np.maximum(z, 1e-3)
    sx = w * 0.5 + x * scale / z
    sy = h * 0.5 - y * scale / z
    return np.stack([sx, sy], axis=-1)


def draw_pose(draw: ImageDraw.ImageDraw, pos: dict, az: float, el: float,
              center: np.ndarray, scale: float, w: int, h: int, title: str):
    for a, b in SEGMENTS:
        if a not in pos or b not in pos:
            continue
        col = CLR_C
        if a.startswith("l") or b.startswith("l"):
            col = CLR_L
        if a.startswith("r") or b.startswith("r"):
            col = CLR_R
        if a == "root" or b == "root":
            col = CLR_C
        p = project(np.stack([pos[a], pos[b]]), az, el, center, scale, w, h)
        draw.line([tuple(p[0]), tuple(p[1])], fill=col, width=5)
    for name in ("head", "lhand", "rhand", "ltoes", "rtoes"):
        if name not in pos:
            continue
        p = project(pos[name][None, :], az, el, center, scale, w, h)[0]
        col = CLR_C if name == "head" else (CLR_L if name.startswith("l") else CLR_R)
        draw.ellipse([p[0] - 5, p[1] - 5, p[0] + 5, p[1] + 5], fill=col)
    draw.text((8, 6), title, fill=(240, 240, 240))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clip", default="11_01")
    ap.add_argument("--t", type=float, default=3.0)
    ap.add_argument("--out", default=str(REPO / ".tmp_3d"))
    args = ap.parse_args()

    subj = args.clip.split("_")[0]
    asf = Asf.parse(SRC / f"{subj}.asf")
    motion = read_amc(SRC / f"{args.clip}.amc")
    keys = sorted(motion)
    frame = int(round(args.t * 120))
    frame = min(max(frame, keys[0]), keys[-1])
    pos = forward_kinematics(asf, motion[frame])[0]

    center = (pos["root"] + pos["head"]) * 0.5
    W, H = 300, 380
    dist_scale = H * 1.9

    cols, rows = 4, 2
    sheet = Image.new("RGB", (W * cols, H * rows), (16, 16, 18))
    d = ImageDraw.Draw(sheet)

    az_list = [0, 45, 90, 135, 180, 225, 270, 315]
    for i, az in enumerate(az_list):
        im = Image.new("RGB", (W, H), (16, 16, 18))
        dd = ImageDraw.Draw(im)
        # 相机在 az 方向：az=0 表示相机位于 +Z（受试者前往方向）
        draw_pose(dd, pos, az, 8.0, center, dist_scale, W, H, f"az={az}  t={args.t}s")
        sheet.paste(im, ((i % cols) * W, (i // cols) * H))

    dst = Path(args.out) / f"views_{args.clip}_t{args.t}.png"
    sheet.save(dst)
    print(f"saved {dst}  帧 {frame}  尺寸 {sheet.size}")
    print("橙=受试者左(CMU l*)  蓝=受试者右(CMU r*)  黄=躯干")


if __name__ == "__main__":
    main()
