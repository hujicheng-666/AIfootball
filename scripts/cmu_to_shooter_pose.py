"""把 CMU 动捕（ASF/AMC）转成射手模型使用的 17 关节姿态片段。

产物格式与 ``scripts/export_shooter_pose.py`` 完全一致，因此 Unity 侧
``ShooterPoseClip`` / ``ShooterRetargeter`` 不需要任何改动就能直接播。

三处必须做的处理
----------------
1. **朝向归正**：CMU 的受试者不是正对 Z 轴站的（11_01 的骨盆常年偏 -40°），
   而 Unity 侧把角色 transform.forward 当成射门方向。不归正的话，射手会
   斜着往边线踢。这里用「双肩连线」定左右、叉乘出前向，取全程平均再整体旋转。
2. **以髋为原点**：每帧减去当帧 root，根位移交给 ShooterDriver，脚不会滑。
3. **触球帧**：取踝关节水平速度峰值。Unity 侧用它决定何时出球，
   用标注值会因为 CMU 没有标注而失效。
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "scripts"))

from cmu_amc import UNIT_TO_M, Asf, forward_kinematics, read_amc  # noqa: E402

SRC = REPO / "data" / "external" / "cmu"

# 裁剪窗口（秒）：触球前留 1.25s 给助跑与摆腿，后留 0.62s 给随挥
TRIM_PRE = 1.25
TRIM_POST = 0.62

# 射手的腿比真人短：模型大腿+小腿 = 0.681m，CMU 数据 0.871m（见 ShooterPoseDiag 的 angles.txt）。
# 髋位移是按真人的步幅记录的，若原样施加，模型每跨一步都会比身体实际迈出的距离更远，
# 表现为脚底持续打滑、触球帧脚够不到球。位移与站位距离都按腿长比例缩回来。
LEG_SCALE = 0.78
OUT = REPO / "Myproject" / "Assets" / "PenaltyKick" / "Resources" / "ShooterPoses"

# CMU 骨架 → H3WB 17 关节（顺序必须与 ShooterRetargeter 里的名字一致）
JOINTS = [
    ("Center of Hips", "root"),
    ("Left Hip", "lfemur"),
    ("Left Knee", "ltibia"),
    ("Left Ankle", "lfoot"),
    ("Right Hip", "rfemur"),
    ("Right Knee", "rtibia"),
    ("Right Ankle", "rfoot"),
    ("Center of Body", "upperback"),
    ("Center of Shoulder", "thorax"),
    ("Neck", "upperneck"),
    ("Head", "head"),
    ("Right Shoulder", "rhumerus"),
    ("Right Elbow", "rradius"),
    ("Right Wrist", "rwrist"),
    ("Left Shoulder", "lhumerus"),
    ("Left Elbow", "lradius"),
    ("Left Wrist", "lwrist"),
]


def rot_y(deg: float) -> np.ndarray:
    r = np.radians(deg)
    c, s = np.cos(r), np.sin(r)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]], dtype=np.float64)


def average_facing(frames: list[dict]) -> float:
    """全身平均朝向相对 +Z 的偏角（度，CMU 坐标系）。"""
    acc = np.zeros(3)
    for pos in frames:
        right = pos["rsh"] - pos["lsh"]          # CMU 里 X 指向左，故这是 +右
        right[1] = 0.0
        if np.linalg.norm(right) < 1e-6:
            continue
        right /= np.linalg.norm(right)
        fwd = np.cross(np.array([0.0, 1.0, 0.0]), right)   # 上 × 右 = 前
        acc += fwd
    if np.linalg.norm(acc) < 1e-6:
        return 0.0
    acc /= np.linalg.norm(acc)
    return float(np.degrees(np.arctan2(acc[0], acc[2])))


def to_unity(p: np.ndarray, facing_deg: float) -> np.ndarray:
    """先绕 Y 转正朝向，再翻手性进 Unity（x 取反），最后换算成米。"""
    p = p @ rot_y(-facing_deg).T
    out = np.empty_like(p)
    out[..., 0] = -p[..., 0]
    out[..., 1] = p[..., 1]
    out[..., 2] = p[..., 2]
    return out * UNIT_TO_M


def build(clip: str, fps_out: float, scale: float) -> dict:
    subj = clip.split("_")[0]
    asf = Asf.parse(SRC / f"{subj}.asf")
    motion = read_amc(SRC / f"{clip}.amc")
    keys = sorted(motion)

    raw: list[dict] = []
    for k in keys:
        fk = forward_kinematics(asf, motion[k])
        if fk is None:
            continue
        pos = fk[0]
        raw.append({
            "root": pos["root"],
            "lsh": pos["lhumerus"], "rsh": pos["rhumerus"],
            "pts": {name: pos[cmu] for name, cmu in JOINTS},
        })
    if not raw:
        raise SystemExit(f"{clip}: 解析不出任何帧")

    facing = average_facing(raw)
    step = max(1, int(round(120.0 / fps_out)))
    used = raw[::step]
    fps = 120.0 / step

    seq = []
    for i, f in enumerate(used):
        p = np.array([f["pts"][name] for name, _ in JOINTS])   # (17,3)
        seq.append(to_unity(p, facing))
    all_p = np.array(seq)                                       # (T,17,3)

    # 触球帧：踢球脚（右脚踝，17 关节下标 6）向前最远的时刻。
    # 早先按踝速度峰值判，实测会提前 4~16 帧 —— 脚在触球前的加速段速度同样很高，
    # 峰值容易落在摆腿中段。脚最前伸的那一刻才是真正碰到球的时刻。
    ankle_z = all_p[:, 6, 2]
    lo, hi = int(len(ankle_z) * 0.10), max(int(len(ankle_z) * 0.95), 1)
    kick_all = int(np.argmax(ankle_z[lo:hi])) + lo

    # 裁剪：原始 trial 是 3~6.7 秒，里面还包含走位、调整、踢完走开。
    # 游戏里一次射门只需要「助跑 → 触球 → 随挥」，取触球前 1.25s 到后 0.62s。
    pre = int(round(TRIM_PRE * fps))
    post = int(round(TRIM_POST * fps))
    start = max(0, kick_all - pre)
    end = min(len(all_p), kick_all + post + 1)
    all_p = all_p[start:end]
    kick = kick_all - start

    # 根位移：动捕里的助跑是真人跑出来的，位移真实。但 ShooterDriver 另有一套
    # 「程序化跑步 + 插值位移」，两套同时跑就会脚底打滑。这里把髋关节的水平轨迹
    # 单独存出来，让驱动跟着真实轨迹走；姿态本身仍以髋为原点（重定向只用方向）。
    hip = all_p[:, 0, :].copy()
    hip_off = (hip - hip[kick]) * LEG_SCALE                     # 触球帧对齐原点，并按模型腿长缩放步幅
    p_rel = all_p - hip[:, None, :]

    frames = []
    for i in range(len(p_rel)):
        frames.append({
            "t": round(i / fps, 6),
            "p": [round(float(x), 5) for x in p_rel[i].reshape(-1)],
            "hipX": round(float(hip_off[i, 0]), 4),
            "hipZ": round(float(hip_off[i, 2]), 4),
        })

    return {
        "shotId": f"CMU_{clip}",
        "fps": float(fps),
        "kickFrame": int(kick),
        "kickFrameKinematic": int(kick),
        "jointNames": [n for n, _ in JOINTS],
        "frames": frames,
        "facingDeg": round(facing, 1),
        "sourceFrames": len(raw),
        "trimStart": int(start),
        "hasRootMotion": True,
        # 触球帧踝关节在髋前方的距离 + 脚踝到击球点（脚内侧/脚背）约 0.10m，
        # 直接作为「射手站位到球的距离」，各段落在 0.73~0.93m，量级一致
        "standOffset": round(float(p_rel[kick, 6, 2] * LEG_SCALE + 0.10 * LEG_SCALE), 3),
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clips", nargs="+", default=None)
    ap.add_argument("--fps", type=float, default=60.0)
    ap.add_argument("--out", default=str(OUT))
    args = ap.parse_args()

    clips = args.clips or sorted(p.stem for p in SRC.glob("*.amc"))
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    for clip in clips:
        data = build(clip, args.fps, 1.0)
        path = out / f"CMU_{clip}.json"
        path.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
        print(f"{clip}: 原 {data['sourceFrames']} 帧 → 取 {len(data['frames'])} 帧 @{data['fps']:.0f}fps "
              f"({len(data['frames']) / data['fps']:.2f}s)  触球帧 {data['kickFrame']}  "
              f"朝向 {data['facingDeg']:+.1f}°  {path.stat().st_size // 1024}KB")


if __name__ == "__main__":
    main()
