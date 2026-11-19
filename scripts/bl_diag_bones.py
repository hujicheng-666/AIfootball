"""逐帧对照：CMU 骨方向 vs 重定向后模型骨方向。

输出每根映射骨在指定帧的「世界单位向量」，直接看偏差角。
股骨/胫骨的方向是判断抬腿是否丢失的关键。
"""
import sys
import math
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
BLEND = argv[0]
ASF = Path(argv[1])
AMC = Path(argv[2])
FRAMES = [int(v) for v in argv[3].split(",")]

sys.path.insert(0, r"D:\AIfootball\scripts")
from cmu_amc import Asf, forward_kinematics, read_amc  # noqa: E402

bpy.ops.wm.open_mainfile(filepath=BLEND)
arm_obj = bpy.data.objects["ShooterRig"]
scn = bpy.context.scene

asf = Asf.parse(ASF)
motion = read_amc(AMC)
keys = sorted(motion)

# CMU 骨段：关节 -> 子关节（用来算骨方向）
CMU_CHILD = {
    "lowerback": "upperback", "upperback": "thorax", "thorax": "lowerneck",
    "lowerneck": "upperneck", "upperneck": "head",
    "lclavicle": "lhumerus", "lhumerus": "lradius", "lradius": "lwrist", "lwrist": "lhand",
    "rclavicle": "rhumerus", "rhumerus": "rradius", "rradius": "rwrist", "rwrist": "rhand",
    "lfemur": "ltibia", "ltibia": "lfoot", "lfoot": "ltoes",
    "rfemur": "rtibia", "rtibia": "rfoot", "rfoot": "rtoes",
}
MAP = {
    "Hips": "lowerback", "Spine": "upperback", "Chest": "thorax", "Head": "head",
    "LeftUpperArm": "lhumerus", "LeftLowerArm": "lradius",
    "RightUpperArm": "rhumerus", "RightLowerArm": "rradius",
    "LeftUpperLeg": "lfemur", "LeftLowerLeg": "ltibia",
    "RightUpperLeg": "rfemur", "RightLowerLeg": "rtibia",
    "LeftFoot": "lfoot", "RightFoot": "rfoot",
}
A = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]])

# 复用保存时的 G
zero = {"root": [0.0] * 6}
pos0, rot0 = forward_kinematics(asf, zero)
best, kick = -1e9, 0
for i, k in enumerate(keys):
    fk = forward_kinematics(asf, motion[k])
    if fk is None:
        continue
    if fk[0]["rtoes"][2] > best:
        best = fk[0]["rtoes"][2]
        kick = i
fk = forward_kinematics(asf, motion[keys[kick]])
p_k = fk[0]
lv = np.array(p_k["lhumerus"], float) - np.array(p_k["rhumerus"], float)
lv[1] = 0
lv /= np.linalg.norm(lv)
fb = A @ np.cross(lv, np.array([0.0, 1.0, 0.0]))
ang = math.atan2(fb[1], fb[0])
tw = -(math.pi / 2.0) - ang
ct, st = math.cos(tw), math.sin(tw)
G = np.array([[ct, -st, 0.0], [st, ct, 0.0], [0.0, 0.0, 1.0]])
print(f"[diag] kick={kick+1} twist={math.degrees(tw):+.1f}deg")

for f in FRAMES:
    idx = f - 1
    if not (0 <= idx < len(keys)):
        continue
    pos, _ = forward_kinematics(asf, motion[keys[idx]])
    scn.frame_set(f)
    bpy.context.view_layer.update()
    print("-" * 78)
    print(f"frame {f} (CMU #{keys[idx]})")
    print(f"{'bone':14s} {'CMU dir(blender)':>28s} {'model dir':>28s} {'err':>7s}")
    for tgt, cmu in MAP.items():
        child = CMU_CHILD.get(cmu)
        if child is None or child not in pos:
            continue
        d_cmu = np.array(pos[child], float) - np.array(pos[cmu], float)
        d_cmu_b = G @ (A @ d_cmu)
        n = np.linalg.norm(d_cmu_b)
        if n < 1e-9:
            continue
        d_cmu_b /= n

        pb = arm_obj.pose.bones[tgt]
        v = (arm_obj.matrix_world @ pb.tail) - (arm_obj.matrix_world @ pb.head)
        if v.length < 1e-9:
            continue
        v = v.normalized()
        d_model = np.array([v.x, v.y, v.z])
        dot = max(-1.0, min(1.0, float(np.dot(d_cmu_b, d_model))))
        err = math.degrees(math.acos(dot))
        s1 = "(" + ",".join(f"{x:+.2f}" for x in d_cmu_b) + ")"
        s2 = "(" + ",".join(f"{x:+.2f}" for x in d_model) + ")"
        print(f"{tgt:14s} {s1:>28s} {s2:>28s} {err:6.1f}°")

# CMU 抬腿高度
pos, _ = forward_kinematics(asf, motion[keys[FRAMES[0] - 1]])
print("-" * 78)
print("[CMU] 该帧 lfoot/rfoot 的 y(高度) 与 z(前后):")
for j in ("lfemur", "ltibia", "lfoot", "ltoes", "rfemur", "rtibia", "rfoot", "rtoes"):
    p = pos[j]
    print(f"  {j:8s} x={p[0]:+7.2f} y(up)={p[1]:+7.2f} z(fwd)={p[2]:+7.2f}")
print("DONE")
