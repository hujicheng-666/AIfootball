# -*- coding: utf-8 -*-
"""把 shooter_mixamo.blend 里的两个 Mixamo 动作导出为射手 17 关节姿态 JSON。

与 cmu_to_shooter_pose.py 产物格式一致（ShooterPoseClip / ShooterRetargeter 无需改动）：
  - 17 关节、Unity 坐标系(x右 y上 z前)、以髋为原点、面朝 +z
  - kickFrame = 踢球脚踝向前最伸的帧
  - hipX/hipZ = 髋相对触球帧的水平位移(米, 按腿长比缩放) → root motion
  - standOffset = 触球帧踝到髋前方距离 + 0.10m(按腿长比缩放)

坐标映射（Blender 世界 → Unity 局部）：ux=-bx, uy=bz, uz=-by
  （角色在 Blender 里面朝 -Y, 映射后正好面朝 +z；行列式 -1 = 右手系→左手系）

流程(与 cmu 版一致)：
  采样 → Unity 坐标 → 双肩平均朝向归正 → 触球帧检测(踝最前伸) →
  裁剪(触球前 1.25s/后 0.62s) → 髋原点化 + root motion → JSON
"""
import bpy
import json
import math
import os
import sys

import numpy as np

argv = sys.argv[sys.argv.index("--") + 1:]
BLEND = argv[0]
OUT_DIR = argv[1]
LEG_SCALE = 0.78          # 射手模型腿比真人短, 步幅按腿长比缩放(与 CMU 版一致)
TRIM_PRE = 1.25           # 触球前保留秒数(助跑)
TRIM_POST = 0.62          # 触球后保留秒数(随挥)

# 17 关节 → Blender 骨名(顺序必须与 ShooterRetargeter 一致)
JOINTS = [
    ("Center of Hips", "Hips"),
    ("Left Hip", "LeftUpperLeg"),
    ("Left Knee", "LeftLowerLeg"),
    ("Left Ankle", "LeftFoot"),
    ("Right Hip", "RightUpperLeg"),
    ("Right Knee", "RightLowerLeg"),
    ("Right Ankle", "RightFoot"),
    ("Center of Body", "Spine"),
    ("Center of Shoulder", None),   # 双肩中点, 特殊处理
    ("Neck", "Neck"),
    ("Head", "Head"),
    ("Right Shoulder", "RightShoulder"),
    ("Right Elbow", "RightLowerArm"),
    ("Right Wrist", "RightHand"),
    ("Left Shoulder", "LeftShoulder"),
    ("Left Elbow", "LeftLowerArm"),
    ("Left Wrist", "LeftHand"),
]
NAMES = [n for n, _ in JOINTS]
ANKLE_L, ANKLE_R = 3, 6   # 关节下标: Left Ankle / Right Ankle
IDX_HIP = 0
IDX_LSH, IDX_RSH = 14, 11


def sample_action(arm, act):
    """逐帧采样一个 action, 返回 ((T,17,3) Blender 世界米, fps)。"""
    fps = bpy.context.scene.render.fps
    f0, f1 = [int(v) for v in act.frame_range]
    arm.animation_data.action = act
    try:
        arm.animation_data.action_slot = act.slots[0] if act.slots else None
    except Exception:
        pass
    mw = arm.matrix_world
    frames = []
    for f in range(f0, f1 + 1):
        bpy.context.scene.frame_set(f)
        bpy.context.view_layer.update()
        pts = []
        for name, bone in JOINTS:
            if name == "Center of Shoulder":
                l = mw @ arm.pose.bones["LeftShoulder"].matrix.translation
                r = mw @ arm.pose.bones["RightShoulder"].matrix.translation
                pts.append((l + r) * 0.5)
            else:
                pts.append(mw @ arm.pose.bones[bone].matrix.translation)
        frames.append([tuple(p) for p in pts])
    return np.array(frames), fps


def to_unity(p):
    """Blender 世界(米, Z-up, 面-Y) → Unity 坐标(x右 y上 z前, 面+z)。"""
    out = np.empty_like(p)
    out[..., 0] = -p[..., 0]
    out[..., 1] = p[..., 2]
    out[..., 2] = -p[..., 1]
    return out


def rot_y(deg):
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


def average_facing_deg(p_unity):
    """双肩连线定左右, 平均前向相对 +Z 的偏角(度, Unity 空间)。"""
    acc = np.zeros(3)
    for i in range(len(p_unity)):
        right = p_unity[i, IDX_RSH] - p_unity[i, IDX_LSH]
        right[1] = 0.0
        n = np.linalg.norm(right)
        if n < 1e-6:
            continue
        right /= n
        # Unity 是左手系: 前向 = right × up (面朝 +z 时 right=+x, cross((1,0,0),(0,1,0))=(0,0,1))
        fwd = np.cross(right, np.array([0.0, 1.0, 0.0]))
        acc += fwd
    if np.linalg.norm(acc) < 1e-6:
        return 0.0
    acc /= np.linalg.norm(acc)
    return float(math.degrees(math.atan2(acc[0], acc[2])))


def export_action(arm, act, shot_id, trim_pre=TRIM_PRE, trim_post=TRIM_POST, root_motion=True):
    seq_bl, fps = sample_action(arm, act)
    p = to_unity(seq_bl)                                      # (T,17,3) Unity 米

    facing = average_facing_deg(p)
    p = p @ rot_y(-facing).T                                  # 转正朝向 +z

    # 先相对髋化(髋在助跑中大幅前移, 绝对坐标会让 kick 检测落到助跑末端)
    hip_abs = p[:, IDX_HIP, :].copy()
    p_rel = p - hip_abs[:, None, :]

    # 触球帧: 踢球脚踝相对髋向前(z)最伸, 取左右踝峰值更大者, 限制在 10%~95% 段
    lo, hi = int(len(p_rel) * 0.10), max(int(len(p_rel) * 0.95), 1)
    zl, zr = p_rel[lo:hi, ANKLE_L, 2], p_rel[lo:hi, ANKLE_R, 2]
    fl, fr = int(np.argmax(zl)) + lo, int(np.argmax(zr)) + lo
    kick = fr if zr.max() >= zl.max() else fl
    ankle_idx = ANKLE_R if kick == fr else ANKLE_L

    # 裁剪: 触球前 trim_pre 秒 / 后 TRIM_POST 秒
    pre = int(round(trim_pre * fps))
    post = int(round(trim_post * fps))
    start = max(0, kick - pre)
    end = min(len(p_rel), kick + post + 1)
    p_rel = p_rel[start:end]
    hip_abs = hip_abs[start:end]
    kick -= start

    # root motion: 髋水平轨迹(触球帧为原点, 步幅按腿长比缩放)
    if root_motion:
        hip_off = (hip_abs - hip_abs[kick]) * LEG_SCALE       # (T,3)
    else:
        hip_off = np.zeros((len(p_rel), 3))   # 位移交给程序化助跑

    frames = []
    for i in range(len(p_rel)):
        frames.append({
            "t": round(i / fps, 6),
            "p": [round(float(x), 5) for x in p_rel[i].reshape(-1)],
            "hipX": round(float(hip_off[i, 0]), 4),
            "hipZ": round(float(hip_off[i, 2]), 4),
        })

    return {
        "shotId": shot_id,
        "fps": float(fps),
        "kickFrame": int(kick),
        "kickFrameKinematic": int(kick),
        "jointNames": NAMES,
        "frames": frames,
        "facingDeg": round(facing, 1),
        "hasRootMotion": True,
        # 触球帧踝在髋前方距离 + 踝到击球点约 0.10m, 都按腿长比缩放
        "standOffset": round(float(p_rel[kick, ankle_idx, 2] * LEG_SCALE
                                   + 0.10 * LEG_SCALE), 3),
    }


def main():
    bpy.ops.wm.open_mainfile(filepath=BLEND)
    arm = bpy.data.objects["ShooterRig"]

    # SH_Jog: 跑步循环, 全量不裁剪, kick 帧仅参考, 无 root motion(位移由 ShooterDriver 程序化驱动)
    # SH_PenaltyKick: 射门, 只保留摆腿+触球+随挥(TRIM_PRE 缩短), 无 root motion(与程序化助跑衔接)
    jobs = [
        ("SH_00_StrikeForwardJog", "SH_Jog", 999.0, 999.0, False),
        ("SH_01_PenaltyKick", "SH_PenaltyKick", 0.45, TRIM_POST, False),
    ]
    os.makedirs(OUT_DIR, exist_ok=True)
    for act_name, shot_id, trim_pre, trim_post, rm in jobs:
        act = bpy.data.actions.get(act_name)
        if act is None:
            print("MISSING action:", act_name)
            continue
        data = export_action(arm, act, shot_id, trim_pre=trim_pre, trim_post=trim_post, root_motion=rm)
        path = os.path.join(OUT_DIR, shot_id + ".json")
        with open(path, "w", encoding="utf-8") as fp:
            json.dump(data, fp, ensure_ascii=False, indent=1)
        n = len(data["frames"])
        print(f"[pose] {shot_id}: {n} 帧 @{data['fps']:.0f}fps "
              f"({n / data['fps']:.2f}s)  触球帧 {data['kickFrame']}  "
              f"朝向 {data['facingDeg']:+.1f}°  standOffset {data['standOffset']}  "
              f"-> {path}")
    print("DONE")


main()
