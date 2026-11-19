#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
按关节角度合成完整的射门动作（1.75s）。

为什么重写
----------
上一版直接给每根骨写"世界绝对方向"（球坐标 elev/azim），结果是：大腿向后摆 34°、
小腿也向后下 25°，两者几乎平行 -> 画面上腿是直的，完全不像屈膝后摆。
人是靠**关节相对角度**做动作的，所以这一版改成：

    hip_flex     髋屈（大腿相对躯干向前抬；负值 = 后伸）
    knee_flex    膝屈（小腿向后折，正值；0 = 伸直）
    ankle_flex   踝（相对小腿）
    shoulder_flex / shoulder_abduct / elbow_flex

逐层累积成世界方向，再由骨长累加成关节位置。这样"屈膝 95°"就一定画出
小腿向后折 95°，不会再退化。

坐标：+X 右、+Y 上、+Z 前（射手朝球门），位置相对 Center of Hips。
绕 +X 轴正旋转 = Y 轴转向 +Z（即向前）。

人类生理参考值（右脚射门）
--------------------------
  站立     髋 0°、膝 5°
  后摆顶点 髋后伸 35~40°、膝屈 90~100°、躯干前倾 18°
  触球     髋前屈 20~25°、膝近伸直 10~15°（鞭打末端的伸展）
  随挥     髋前屈 55~65°、膝屈 30°
"""

import json
import math
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
POSE_DIR = REPO / "Myproject" / "Assets" / "PenaltyKick" / "Resources" / "ShooterPoses"

BONE_LEN = {
    "Left Hip": 0.1081, "Left Knee": 0.3192, "Left Ankle": 0.3205,
    "Right Hip": 0.0934, "Right Knee": 0.2993, "Right Ankle": 0.2925,
    "Center of Body": 0.1764, "Center of Shoulder": 0.1850,
    "Neck": 0.0525, "Head": 0.0839,
    "Right Shoulder": 0.0758, "Right Elbow": 0.1890, "Right Wrist": 0.1705,
    "Left Shoulder": 0.0897, "Left Elbow": 0.1644, "Left Wrist": 0.1310,
}

JOINTS = [
    "Center of Hips", "Left Hip", "Left Knee", "Left Ankle",
    "Right Hip", "Right Knee", "Right Ankle",
    "Center of Body", "Center of Shoulder", "Neck", "Head",
    "Right Shoulder", "Right Elbow", "Right Wrist",
    "Left Shoulder", "Left Elbow", "Left Wrist",
]

X = np.array([1.0, 0.0, 0.0])
Y = np.array([0.0, 1.0, 0.0])
Z = np.array([0.0, 0.0, 1.0])


def rot(v, axis, deg):
    """罗德里格斯旋转：把向量 v 绕 axis 转 deg 度。"""
    a = np.asarray(axis, dtype=np.float64)
    a = a / np.linalg.norm(a)
    th = math.radians(deg)
    c, s = math.cos(th), math.sin(th)
    v = np.asarray(v, dtype=np.float64)
    return v * c + np.cross(a, v) * s + a * np.dot(a, v) * (1.0 - c)


def unit(v):
    n = np.linalg.norm(v)
    return v / n if n > 1e-9 else v


# ── 关键帧 ────────────────────────────────────────────────────────────────
# 字段（角度，单位：度）
#   trunk      躯干前倾（正=前倾）
#   hipL/hipR  髋屈（正=大腿前抬，负=后伸）
#   kneeL/kneeR 膝屈（正=小腿向后折）
#   ankL/ankR  踝相对小腿（正=脚尖下压）
#   shL/shR    肩前摆（正=手臂前抬，负=后摆）
#   abL/abR    肩外展（正=远离身体）
#   elL/elR    肘屈
#   twist      躯干相对骨盆的扭转（正=右肩向前）
KEYS = [
    (0.00, dict(trunk=4,  hipL=0,   kneeL=6,  ankL=0,  hipR=0,   kneeR=6,  ankR=0,
                shL=6,  abL=8,  elL=22, shR=6,  abR=8,  elR=22, twist=0)),
    (0.22, dict(trunk=15, hipL=16,  kneeL=26, ankL=-6, hipR=-28, kneeR=62, ankR=14,
                shL=34, abL=10, elL=58, shR=-34, abR=12, elR=66, twist=-6)),
    (0.45, dict(trunk=24, hipL=10,  kneeL=34, ankL=-10, hipR=-40, kneeR=100, ankR=20,
                shL=46, abL=12, elL=70, shR=-52, abR=14, elR=78, twist=-12)),
    (0.58, dict(trunk=20, hipL=6,   kneeL=22, ankL=-5, hipR=-14, kneeR=86, ankR=16,
                shL=34, abL=11, elL=62, shR=-32, abR=12, elR=68, twist=-7)),
    (0.70, dict(trunk=14, hipL=3,   kneeL=10, ankL=2,  hipR=34,  kneeR=8,  ankR=24,
                shL=6,  abL=9,  elL=48, shR=10, abR=10, elR=52, twist=-2)),
    (0.84, dict(trunk=7,  hipL=-2,  kneeL=16, ankL=10, hipR=56,  kneeR=20, ankR=16,
                shL=-20, abL=10, elL=44, shR=26, abR=11, elR=50, twist=3)),
    (1.00, dict(trunk=2,  hipL=-12, kneeL=34, ankL=18, hipR=72,  kneeR=34, ankR=8,
                shL=-34, abL=12, elL=40, shR=40, abR=13, elR=46, twist=6)),
    (1.28, dict(trunk=10, hipL=-2,  kneeL=18, ankL=4,  hipR=16,  kneeR=26, ankR=4,
                shL=-10, abL=9,  elL=28, shR=18, abR=10, elR=32, twist=1)),
    (1.75, dict(trunk=4,  hipL=0,   kneeL=6,  ankL=0,  hipR=0,   kneeR=6,  ankR=0,
                shL=6,  abL=8,  elL=22, shR=6,  abR=8,  elR=22, twist=0)),
]

FPS = 30.0
KICK_TIME = 0.70

FIELDS = ["trunk", "hipL", "kneeL", "ankL", "hipR", "kneeR", "ankR",
          "shL", "abL", "elL", "shR", "abR", "elR", "twist"]


def smoothstep(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * (3.0 - 2.0 * x)


def sample_params(t):
    """在关键帧之间对角度做 smoothstep 插值（角度空间线性 + 缓动即可）。"""
    if t <= KEYS[0][0]:
        return dict(KEYS[0][1])
    if t >= KEYS[-1][0]:
        return dict(KEYS[-1][1])
    i = 0
    while i < len(KEYS) - 1 and not (KEYS[i][0] <= t <= KEYS[i + 1][0]):
        i += 1
    t0, k0 = KEYS[i]
    t1, k1 = KEYS[i + 1]
    w = smoothstep((t - t0) / (t1 - t0)) if t1 > t0 else 0.0
    return {f: k0[f] * (1 - w) + k1[f] * w for f in FIELDS}


def build_frame(p):
    """由关节角度累积出 17 个关节的世界位置（相对髋）。"""
    pos = {"Center of Hips": np.zeros(3)}

    # 骨盆坐标系
    trunk_dir = unit(rot(Y, X, p["trunk"]))          # 前倾后的"向上"
    fwd = unit(rot(Z, X, p["trunk"]))                # 前倾后的"向前"
    right = X

    # 骨盆左右髋（沿骨盆横轴，略向下）
    hip_half = {"L": -1.0, "R": 1.0}
    for side, sgn in hip_half.items():
        key = "Left Hip" if side == "L" else "Right Hip"
        lateral = unit(right * sgn)
        pos[key] = pos["Center of Hips"] + lateral * BONE_LEN[key] \
            + trunk_dir * (-0.012)

    # 躯干链
    twist_dir_f = unit(rot(fwd, trunk_dir, p["twist"]))
    twist_dir_r = unit(np.cross(trunk_dir, twist_dir_f))
    pos["Center of Body"] = pos["Center of Hips"] + trunk_dir * BONE_LEN["Center of Body"]
    pos["Center of Shoulder"] = pos["Center of Body"] + trunk_dir * BONE_LEN["Center of Shoulder"]
    pos["Neck"] = pos["Center of Shoulder"] + trunk_dir * BONE_LEN["Neck"]
    pos["Head"] = pos["Neck"] + unit(trunk_dir * 0.92 + twist_dir_f * 0.38) * BONE_LEN["Head"]

    # 肩点
    for side, sgn in hip_half.items():
        key = "Left Shoulder" if side == "L" else "Right Shoulder"
        pos[key] = pos["Center of Shoulder"] + twist_dir_r * (sgn * BONE_LEN[key]) \
            + trunk_dir * (-0.028)

    # 腿：大腿 = 躯干反方向，绕 X 做髋屈；小腿绕 X 做膝屈
    for side, sgn in hip_half.items():
        hip_k = "Left Hip" if side == "L" else "Right Hip"
        knee_k = "Left Knee" if side == "L" else "Right Knee"
        ank_k = "Left Ankle" if side == "L" else "Right Ankle"
        hip_flex = p["hip" + side]
        knee_flex = p["knee" + side]
        ank_flex = p["ank" + side]

        thigh = unit(rot(-trunk_dir, X, -hip_flex))       # 正 hip_flex = 向前抬
        shin = unit(rot(thigh, X, knee_flex))             # 正 knee_flex = 小腿向后折
        foot = unit(rot(shin, X, -ank_flex))              # 正 ank_flex = 脚尖下压

        pos[knee_k] = pos[hip_k] + thigh * BONE_LEN[knee_k]
        pos[ank_k] = pos[knee_k] + shin * BONE_LEN[ank_k]
        # 踝末端（脚尖）不入库，仅用于自检

    # 手臂：上臂 = 下垂方向，绕 X 做前摆、绕 Z 做外展；前臂绕 X 做肘屈
    for side, sgn in hip_half.items():
        sh_k = "Left Shoulder" if side == "L" else "Right Shoulder"
        el_k = "Left Elbow" if side == "L" else "Right Elbow"
        wr_k = "Left Wrist" if side == "L" else "Right Wrist"
        sh_flex = p["sh" + side]
        abduct = p["ab" + side]
        el_flex = p["el" + side]

        upper = unit(-trunk_dir)
        upper = unit(rot(upper, X, -sh_flex))             # 正 = 向前抬
        upper = unit(rot(upper, Z, -sgn * abduct))        # 正 = 远离身体
        fore = unit(rot(upper, X, -el_flex))              # 正 = 屈肘（前臂朝前上）

        pos[el_k] = pos[sh_k] + upper * BONE_LEN[el_k]
        pos[wr_k] = pos[el_k] + fore * BONE_LEN[wr_k]

    return np.array([pos[j] for j in JOINTS])


def generate(mirror=False):
    dur = KEYS[-1][0]
    n = int(round(dur * FPS)) + 1
    frames = []
    for i in range(n):
        t = i / FPS
        p = sample_params(t)
        if mirror:
            # 真正的"左脚射门"：交换左右关节参数，而不是把网格沿 X 翻转。
            # 几何镜像会让球衣号码、鞋型都反掉；交换参数则两边动作都成立。
            p = dict(p)
            for a, b in (("hipL", "hipR"), ("kneeL", "kneeR"), ("ankL", "ankR"),
                         ("shL", "shR"), ("abL", "abR"), ("elL", "elR")):
                p[a], p[b] = p[b], p[a]
            p["twist"] = -p["twist"]
        P = build_frame(p)
        frames.append({"t": round(t, 4),
                       "p": [round(float(v), 5) for v in P.reshape(-1)]})
    kick = int(round(KICK_TIME * FPS))
    return {
        "shotId": "SYN17_m" if mirror else "SYN17",
        "fps": FPS,
        "kickFrame": kick,
        "kickFrameKinematic": kick,
        "jointNames": JOINTS,
        "frames": frames,
    }


def self_check(out):
    P = np.array([np.array(f["p"]).reshape(-1, 3) for f in out["frames"]])
    idx = {n: i for i, n in enumerate(JOINTS)}
    print("帧数 %d  时长 %.3fs  触球帧 %d (t=%.2fs)"
          % (len(out["frames"]), out["frames"][-1]["t"],
             out["kickFrame"], out["frames"][out["kickFrame"]]["t"]))

    # 膝角自检：小腷相对大腿的夹角，后摆顶点应接近 90~100°
    print("时刻    右膝角  左膝角  右髋(°相对垂直)")
    for tt in [0.0, 0.22, 0.45, 0.58, 0.70, 0.84, 1.00, 1.28, 1.75]:
        p = sample_params(tt)
        print("  %.2f    %5.1f   %5.1f   %6.1f"
              % (tt, p["kneeR"], p["kneeL"], p["hipR"]))

    # 踝部最低点：决定角色该放多高才贴地
    print("踝最低点：左 %.3f 右 %.3f（相对髋）"
          % (P[:, idx["Left Ankle"], 1].min(), P[:, idx["Right Ankle"], 1].min()))

    # 骨长一致性
    bad = 0
    for c, p in [("Left Knee", "Left Hip"), ("Left Ankle", "Left Knee"),
                 ("Right Knee", "Right Hip"), ("Right Ankle", "Right Knee"),
                 ("Center of Body", "Center of Hips"),
                 ("Center of Shoulder", "Center of Body")]:
        ln = np.linalg.norm(P[:, idx[c]] - P[:, idx[p]], axis=1)
        if ln.std() > 1e-4:
            print("  !! 骨长不一致 %s cv=%.4f" % (c, ln.std() / ln.mean()))
            bad += 1
    print("骨长校验：%s" % ("通过" if bad == 0 else "有 %d 处异常" % bad))


def main():
    for mirror, name in ((False, "SYN17"), (True, "SYN17_m")):
        out = generate(mirror)
        path = POSE_DIR / (name + ".json")
        path.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
        print("=== " + name + ("（左脚）" if mirror else "（右脚）") + " ===")
        self_check(out)
        print("->", path.name)


if __name__ == "__main__":
    main()
