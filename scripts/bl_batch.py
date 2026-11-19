"""批量把六段 CMU 射门动捕重定向进一个 .blend，每段一个 Action。

复用 bl_retarget 的算法（骨方向对齐 + 骨盆绝对高度），产出用户可直接
打开播放的文件：内置地面、已设好活动动作，打开按空格即播。
"""
import sys
import math
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
RIGGED = argv[0]
CMU_DIR = Path(argv[1])
OUT = argv[2]

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cmu_amc import Asf, forward_kinematics, read_amc  # noqa: E402

MAP = {
    "Hips": "lowerback", "Spine": "upperback", "Chest": "thorax",
    "Neck": "lowerneck", "Head": "head",
    "LeftShoulder": "lclavicle", "LeftUpperArm": "lhumerus",
    "LeftLowerArm": "lradius", "LeftHand": "lwrist",
    "RightShoulder": "rclavicle", "RightUpperArm": "rhumerus",
    "RightLowerArm": "rradius", "RightHand": "rwrist",
    "LeftUpperLeg": "lfemur", "LeftLowerLeg": "ltibia",
    "LeftFoot": "lfoot", "LeftToes": "ltoes",
    "RightUpperLeg": "rfemur", "RightLowerLeg": "rtibia",
    "RightFoot": "rfoot", "RightToes": "rtoes",
}
PARENT_TGT = {
    "Hips": None, "Spine": "Hips", "Chest": "Spine", "Neck": "Chest", "Head": "Neck",
    "LeftShoulder": "Chest", "LeftUpperArm": "LeftShoulder",
    "LeftLowerArm": "LeftUpperArm", "LeftHand": "LeftLowerArm",
    "RightShoulder": "Chest", "RightUpperArm": "RightShoulder",
    "RightLowerArm": "RightUpperArm", "RightHand": "RightLowerArm",
    "LeftUpperLeg": "Hips", "LeftLowerLeg": "LeftUpperLeg",
    "LeftFoot": "LeftLowerLeg", "LeftToes": "LeftFoot",
    "RightUpperLeg": "Hips", "RightLowerLeg": "RightUpperLeg",
    "RightFoot": "RightLowerLeg", "RightToes": "RightFoot",
}
CMU_CHILD = {
    "lowerback": "upperback", "upperback": "thorax", "thorax": "lowerneck",
    "lowerneck": "upperneck", "upperneck": "head",
    "lclavicle": "lhumerus", "lhumerus": "lradius", "lradius": "lwrist", "lwrist": "lhand",
    "rclavicle": "rhumerus", "rhumerus": "rradius", "rradius": "rwrist", "rwrist": "rhand",
    "lfemur": "ltibia", "ltibia": "lfoot", "lfoot": "ltoes",
    "rfemur": "rtibia", "rtibia": "rfoot", "rfoot": "rtoes",
    "ltoes": "ltoes", "rtoes": "rtoes",
}
SWING_BONES = {
    "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
    "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
    "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
    "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
}
A = np.array([[1.0, 0.0, 0.0], [0.0, 0.0, -1.0], [0.0, 1.0, 0.0]])


def np2m(r):
    return Matrix([[float(r[0][0]), float(r[0][1]), float(r[0][2])],
                   [float(r[1][0]), float(r[1][1]), float(r[1][2])],
                   [float(r[2][0]), float(r[2][1]), float(r[2][2])]])


def rot_between(a, b):
    a = a / np.linalg.norm(a)
    b = b / np.linalg.norm(b)
    v = np.cross(a, b)
    c = float(np.dot(a, b))
    if c < -0.999999:
        axis = np.cross(a, [1.0, 0.0, 0.0])
        if np.linalg.norm(axis) < 1e-6:
            axis = np.cross(a, [0.0, 1.0, 0.0])
        axis /= np.linalg.norm(axis)
        return 2.0 * np.outer(axis, axis) - np.eye(3)
    vx = np.array([[0.0, -v[2], v[1]], [v[2], 0.0, -v[0]], [-v[1], v[0], 0.0]])
    return np.eye(3) + vx + vx @ vx / (1.0 + c)


def moving_avg(arr, win):
    """居中窗口移动平均，边界复制首末帧（无相位滞后）。arr: T×N"""
    T = len(arr)
    if T < 3 or win < 3:
        return np.asarray(arr, dtype=float)
    half = win // 2
    pad = np.concatenate([np.repeat(arr[:1], half, axis=0), arr,
                          np.repeat(arr[-1:], half, axis=0)], axis=0)
    ker = np.ones(win, dtype=float) / win
    out = np.empty_like(arr, dtype=float)
    for c in range(arr.shape[1]):
        out[:, c] = np.convolve(pad[:, c], ker, mode="valid")
    return out


def smooth_rot_list(mats, win):
    """旋转矩阵序列的居中窗口平滑：窗口内取均值后 SVD 投影回 SO(3)。"""
    A3 = np.asarray(mats, dtype=float)
    T = len(A3)
    if T < 3:
        return A3
    half = win // 2
    out = np.empty_like(A3)
    for t in range(T):
        lo, hi = max(0, t - half), min(T, t + half + 1)
        M = A3[lo:hi].mean(axis=0)
        U, S, Vt = np.linalg.svd(M)
        if np.linalg.det(U @ Vt) < 0:
            U = U.copy()
            U[:, -1] *= -1
        out[t] = U @ Vt
    return out


def build_action(arm_obj, asf, motion, keys, name):
    arm = arm_obj.data
    zero = {"root": [0.0] * 6}
    pos0, rot0 = forward_kinematics(asf, zero)
    rest_cmu = {k: np.asarray(v, float) for k, v in rot0.items()}
    rest_tgt = {b.name: b.matrix_local.to_3x3() for b in arm.bones}
    rest_dir_tgt = {n: np.array(m @ Vector((0.0, 1.0, 0.0))) for n, m in rest_tgt.items()}

    cmu_h = pos0["head"][1] - min(pos0["ltoes"][1], pos0["rtoes"][1])
    tgt_h = arm.bones["Head"].tail_local.z
    scale = tgt_h / cmu_h

    # ── 第 1 步：整段 FK，缺失帧用前值填充 ──
    raw = [forward_kinematics(asf, motion[k]) for k in keys]
    T = len(keys)
    names = list(raw[next(i for i, f in enumerate(raw) if f is not None)][0].keys())
    last_valid = None
    for t in range(T):
        if raw[t] is not None:
            last_valid = raw[t]
        else:
            raw[t] = last_valid

    # ── 第 2 步：时序平滑（去 CMU 抖动，减僵硬感）──
    # CMU 120fps 原始数据高频噪声 + 逐帧刚性重定向 = 机械感。
    # 居中移动平均（窗口 9 帧 ≈ 75ms）去掉抖动、保留动作包络；
    # 用居中而非因果窗口，避免相位滞后造成滑步。
    WIN = 9
    P = {n: moving_avg(np.array([raw[t][0][n] for t in range(T)], dtype=float), WIN) for n in names}
    Rm = {n: smooth_rot_list([raw[t][1][n] for t in range(T)], WIN) for n in names}

    # ── 第 3 步：kick 帧与全局朝向（用平滑后数据）──
    lo, hi = max(1, T // 10), T * 9 // 10
    best, kick = -1e9, lo
    for t in range(lo, min(hi, T)):
        if P["rtoes"][t][2] > best:
            best = P["rtoes"][t][2]
            kick = t
    lv = P["lhumerus"][kick] - P["rhumerus"][kick]
    lv[1] = 0.0
    lv /= np.linalg.norm(lv)
    fb = A @ np.cross(lv, [0.0, 1.0, 0.0])
    twist = -(math.pi / 2.0) - math.atan2(fb[1], fb[0])
    G_np = np.array([[math.cos(twist), -math.sin(twist), 0.0],
                     [math.sin(twist), math.cos(twist), 0.0],
                     [0.0, 0.0, 1.0]])
    root_ref = np.array(P["root"][kick], float)

    action = bpy.data.actions.new(name)
    action.use_fake_user = True   # 没有使用者的 Action 不会被存进 .blend
    if not arm_obj.animation_data:
        arm_obj.animation_data_create()
    arm_obj.animation_data.action = action
    for pb in arm_obj.pose.bones:
        pb.rotation_mode = "QUATERNION"
        pb.location = (0.0, 0.0, 0.0)
        pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)

    Rw = {}
    n_done = 0
    for t in range(T):
        for tgt_name, cmu in MAP.items():
            if tgt_name in SWING_BONES:
                child = CMU_CHILD[cmu]
                base = {"ltoes": "lfoot", "rtoes": "rfoot"}.get(cmu, cmu)
                if child in P and base in P:
                    d = P[child][t] - P[base][t]
                    v1 = G_np @ (A @ d)
                    if np.linalg.norm(v1) > 1e-9:
                        Rw[tgt_name] = np2m(rot_between(rest_dir_tgt[tgt_name], v1)) @ rest_tgt[tgt_name]
                        continue
            dl = Rm[cmu][t] @ rest_cmu[cmu].T
            Rw[tgt_name] = np2m(G_np @ (A @ dl @ A.T)) @ rest_tgt[tgt_name]

        # Hips：水平 = root 相对 kick 帧位移；高度 = CMU 骨盆绝对高度
        pelvis = (P["lfemur"][t] + P["rfemur"][t]) * 0.5
        dp_b = np.array([P["root"][t][0] - root_ref[0],
                         -(P["root"][t][2] - root_ref[2])], dtype=float) * scale
        tgt_pos = arm.bones["Hips"].head_local.copy()
        tgt_pos.x += dp_b[0]
        tgt_pos.y += dp_b[1]
        tgt_pos.z = pelvis[1] * scale
        tgt_m = Rw["Hips"].to_4x4()
        tgt_m.translation = tgt_pos
        basis_m = arm.bones["Hips"].matrix_local.inverted() @ tgt_m
        pb = arm_obj.pose.bones["Hips"]
        pb.rotation_quaternion = basis_m.to_quaternion()
        pb.location = basis_m.to_translation()
        pb.keyframe_insert("rotation_quaternion", frame=t + 1)
        pb.keyframe_insert("location", frame=t + 1)

        for tgt_name in MAP:
            if tgt_name == "Hips":
                continue
            par = PARENT_TGT[tgt_name]
            rp = Rw[par] if par else Matrix.Identity(3)
            rl_pose = rp.transposed() @ Rw[tgt_name]
            rl_rest = rest_tgt[par].transposed() @ rest_tgt[tgt_name]
            q = (rl_rest.transposed() @ rl_pose).to_quaternion()
            q.normalize()
            pb = arm_obj.pose.bones[tgt_name]
            pb.rotation_quaternion = q
            pb.keyframe_insert("rotation_quaternion", frame=t + 1)
        n_done += 1
    print(f"[batch] {name}: {n_done} 帧  kick=第{kick+1}帧  scale={scale:.5f}  "
          f"朝向修正 {math.degrees(twist):+.1f}°  平滑窗口={WIN}帧")
    return action, n_done


def main():
    bpy.ops.wm.open_mainfile(filepath=RIGGED)
    arm_obj = bpy.data.objects["ShooterRig"]
    scn = bpy.context.scene
    scn.render.fps = 120

    # 地面
    ground_data = bpy.data.meshes.new("Ground")
    s = 12.0
    ground_data.vertices.add(4)
    ground_data.vertices.foreach_set("co", [-s, -s, 0, s, -s, 0, s, s, 0, -s, s, 0])
    ground_data.loops.add(4)
    ground_data.polygons.add(1)
    ground_data.polygons[0].vertices = [0, 1, 2, 3]
    ground_obj = bpy.data.objects.new("Ground", ground_data)
    scn.collection.objects.link(ground_obj)
    mat = bpy.data.materials.new("GroundMat")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (0.12, 0.34, 0.12, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.95
    ground_data.materials.append(mat)

    amcs = ["10_01", "10_02", "10_03", "10_05", "10_06", "11_01"]
    first = None
    for stem in amcs:
        asf_path = CMU_DIR / f"{stem.split('_')[0]}.asf"
        asf = Asf.parse(asf_path)
        motion = read_amc(CMU_DIR / f"{stem}.amc")
        keys = sorted(motion)
        act, n = build_action(arm_obj, asf, motion, keys, f"CMU_{stem}")
        if first is None:
            first = (act, n)
    arm_obj.animation_data.action = first[0]
    scn.frame_start = 1
    scn.frame_end = first[1]
    bpy.ops.wm.save_as_mainfile(filepath=OUT)
    print("[batch] saved", OUT)


main()
print("DONE")
