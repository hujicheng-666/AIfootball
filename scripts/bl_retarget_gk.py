# -*- coding: utf-8 -*-
"""把 Mixamo 守门员动作(FBX) 重定向到 goalkeeper_rigged.blend 的骨架。

与 bl_retarget.py(CMU 版) 同一套数学：
  - 四肢 = swing(骨方向对齐) + twist(绕骨轴扭转, 从源世界增量提取)
  - 躯干 = 完整世界增量(保留转体/侧弯)
差异：
  - 源是 Blender 场景内导入的 Mixamo FBX(object 带 0.01 缩放+Y-up旋转, 全部换算到世界空间)
  - 骨盆高度 = 目标 rest 髋高 + (源当前髋高 - 源 T-pose 髋高) × 身高比
    (扑救动作大量跳跃/倒地, 不能像 CMU 版那样用整段最高点当站立基准)
  - 30fps 原速, 每段一个 Action + NLA strip 连播
"""
import sys
import math
import os
import numpy as np
from mathutils import Matrix, Quaternion, Vector

import bpy

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
BLEND_IN = argv[0]
FBX_DIR = argv[1]
BLEND_OUT = argv[2]
ONLY = argv[3] if len(argv) > 3 else ""   # 逗号分隔的索引, 空=全部

# ── Mixamo 骨 → 目标骨映射 ──
MAP = {
    "Hips": "mixamorig:Hips",
    "Spine": "mixamorig:Spine",
    "Chest": "mixamorig:Spine2",     # 世界增量天然含 Spine1+Spine2 累计
    "Neck": "mixamorig:Neck",
    "Head": "mixamorig:Head",
    "LeftShoulder": "mixamorig:LeftShoulder",
    "LeftUpperArm": "mixamorig:LeftArm",
    "LeftLowerArm": "mixamorig:LeftForeArm",
    "LeftHand": "mixamorig:LeftHand",
    "RightShoulder": "mixamorig:RightShoulder",
    "RightUpperArm": "mixamorig:RightArm",
    "RightLowerArm": "mixamorig:RightForeArm",
    "RightHand": "mixamorig:RightHand",
    "LeftUpperLeg": "mixamorig:LeftUpLeg",
    "LeftLowerLeg": "mixamorig:LeftLeg",
    "LeftFoot": "mixamorig:LeftFoot",
    "LeftToes": "mixamorig:LeftToeBase",
    "RightUpperLeg": "mixamorig:RightUpLeg",
    "RightLowerLeg": "mixamorig:RightLeg",
    "RightFoot": "mixamorig:RightFoot",
    "RightToes": "mixamorig:RightToeBase",
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
# 源骨 → 子骨(取骨段方向/twist 轴)
CHILD_SRC = {
    "mixamorig:LeftShoulder": "mixamorig:LeftArm",
    "mixamorig:LeftArm": "mixamorig:LeftForeArm",
    "mixamorig:LeftForeArm": "mixamorig:LeftHand",
    "mixamorig:LeftHand": "mixamorig:LeftHandMiddle1",
    "mixamorig:RightShoulder": "mixamorig:RightArm",
    "mixamorig:RightArm": "mixamorig:RightForeArm",
    "mixamorig:RightForeArm": "mixamorig:RightHand",
    "mixamorig:RightHand": "mixamorig:RightHandMiddle1",
    "mixamorig:LeftUpLeg": "mixamorig:LeftLeg",
    "mixamorig:LeftLeg": "mixamorig:LeftFoot",
    "mixamorig:LeftFoot": "mixamorig:LeftToeBase",
    "mixamorig:LeftToeBase": "mixamorig:LeftToe_End",
    "mixamorig:RightUpLeg": "mixamorig:RightLeg",
    "mixamorig:RightLeg": "mixamorig:RightFoot",
    "mixamorig:RightFoot": "mixamorig:RightToeBase",
    "mixamorig:RightToeBase": "mixamorig:RightToe_End",
}
SWING_BONES = {
    "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
    "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
    "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
    "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
}

NAMES = [
    "GK_00_PlacingBallShort", "GK_01_PlacingBallLong", "GK_02_SidestepRight",
    "GK_03_Miss", "GK_04_BodyBlockLeft", "GK_05_PassRolling",
    "GK_06_DirectingDefense", "GK_07_BodyBlockRight", "GK_08_SidestepLeft",
    "GK_09_IdleHoldBall", "GK_10_IdleNoBall", "GK_11_CatchMedium",
    "GK_12_CatchLow", "GK_13_CatchJumpHigh", "GK_14_DropKick",
    "GK_15_DivingSaveRight", "GK_16_BodyBlockLeft2", "GK_17_OverhandThrow",
    "GK_18_DivingSaveLeft", "GK_19_Scoop", "GK_20_CatchRunJumpHigh",
    "GK_21_DirectingOffense",
]
FILES = [f"gk_{i:02d}_anim.fbx" for i in range(len(NAMES))]


# ── 数学工具(与 bl_retarget.py 相同) ──
def twist_angle(dW: np.ndarray, axis: np.ndarray) -> float:
    t = np.trace(dW)
    if t > 0.0:
        r_ = np.sqrt(t + 1.0)
        w = 0.5 * r_
        v = np.array([dW[2, 1] - dW[1, 2], dW[0, 2] - dW[2, 0], dW[1, 0] - dW[0, 1]]) / (2.0 * r_)
    else:
        i = int(np.argmax(np.diag(dW)))
        j, k = (i + 1) % 3, (i + 2) % 3
        r_ = np.sqrt(dW[i, i] - dW[j, j] - dW[k, k] + 1.0)
        vv = np.zeros(3)
        vv[i] = 0.5 * r_
        w = (dW[k, j] - dW[j, k]) / (2.0 * r_)
        vv[j] = (dW[j, i] + dW[i, j]) / (2.0 * r_)
        vv[k] = (dW[k, i] + dW[i, k]) / (2.0 * r_)
        v = vv
    a = axis / np.linalg.norm(axis)
    return 2.0 * math.atan2(float(np.dot(v, a)), w)


def rot_axis_angle(axis: np.ndarray, ang: float) -> np.ndarray:
    a = axis / np.linalg.norm(axis)
    K = np.array([[0.0, -a[2], a[1]], [a[2], 0.0, -a[0]], [-a[1], a[0], 0.0]])
    return np.eye(3) + math.sin(ang) * K + (1.0 - math.cos(ang)) * (K @ K)


def rot_between(a: np.ndarray, b: np.ndarray) -> np.ndarray:
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


def np2m(r) -> Matrix:
    return Matrix([[float(r[0][0]), float(r[0][1]), float(r[0][2])],
                   [float(r[1][0]), float(r[1][1]), float(r[1][2])],
                   [float(r[2][0]), float(r[2][1]), float(r[2][2])]])


def ortho3x3(m: Matrix) -> Matrix:
    """去掉 object 缩放, 把矩阵变成纯旋转(列归一化)。"""
    r = m.copy()
    for i in range(3):
        col = np.array([r[j][i] for j in range(3)], dtype=float)
        l = np.linalg.norm(col)
        if l > 1e-12:
            for j in range(3):
                r[j][i] = col[j] / l
    return r


def npv(v) -> np.ndarray:
    return np.array([v[0], v[1], v[2]], dtype=float)


def build_action(arm_obj, fbx_path: str, name: str):
    arm = arm_obj.data
    scn = bpy.context.scene

    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=fbx_path)
    src = [o for o in set(bpy.data.objects) - before if o.type == "ARMATURE"][0]
    act_src = src.animation_data.action
    f1, f2 = [int(round(v)) for v in act_src.frame_range]
    T = f2 - f1 + 1

    # ── 源 rest(T-pose) 世界参考 ──
    # 注意: pb.matrix 是 pose 空间(= armature object 局部, FBX 原始 cm/Y-up),
    # 必须乘 matrix_world(含 0.01 缩放+Y-up->Z-up) 才是世界坐标
    Mw = src.matrix_world
    pos0, R0 = {}, {}
    for b in src.data.bones:
        m = Mw @ b.matrix_local
        pos0[b.name] = m.translation.copy()
        R0[b.name] = ortho3x3(m.to_3x3())

    # ── 全局朝向 G: 让角色面朝 -Y ──
    lv = pos0["mixamorig:LeftShoulder"] - pos0["mixamorig:RightShoulder"]
    lv.z = 0.0
    lv.normalize()
    front = lv.cross(Vector((0.0, 0.0, 1.0)))
    ang = math.atan2(front.y, front.x)
    twist_g = -(math.pi / 2.0) - ang
    G = Matrix.Rotation(twist_g, 3, "Z")
    G_np = np.array(G)

    # ── 身高比例 & 髋高基准 ──
    src_h = (pos0["mixamorig:HeadTop_End"].z
             - min(pos0["mixamorig:LeftToe_End"].z, pos0["mixamorig:RightToe_End"].z))
    dst_h = arm.bones["Head"].tail_local.z
    scale = dst_h / src_h
    hips_rest_z_dst = arm.bones["Hips"].head_local.z
    hips0 = pos0["mixamorig:Hips"].copy()
    print(f"[gk] {name}: T-pose髋高 {hips0.z:.3f} 源身高 {src_h:.3f} "
          f"目标身高 {dst_h:.3f} scale={scale:.4f} 朝向修正 {math.degrees(twist_g):+.1f}° "
          f"帧数 {T}")

    # ── 目标 rest ──
    rest_tgt = {b.name: b.matrix_local.to_3x3() for b in arm.bones}
    rest_dir_tgt = {n: npv(m @ Vector((0.0, 1.0, 0.0))) for n, m in rest_tgt.items()}

    for pb in arm_obj.pose.bones:
        pb.rotation_mode = "QUATERNION"
        pb.location = (0.0, 0.0, 0.0)
        pb.rotation_quaternion = (1.0, 0.0, 0.0, 0.0)

    action = bpy.data.actions.new(name)
    action.use_fake_user = True
    if not arm_obj.animation_data:
        arm_obj.animation_data_create()
    arm_obj.animation_data.action = action

    Rw = {}
    hips_ref = None
    for t, f in enumerate(range(f1, f2 + 1)):
        scn.frame_set(f)
        bpy.context.view_layer.update()
        pos_w, R_w = {}, {}
        for b in src.data.bones:
            pm = Mw @ src.pose.bones[b.name].matrix
            pos_w[b.name] = pm.translation.copy()
            R_w[b.name] = ortho3x3(pm.to_3x3())

        for tgt, srcb in MAP.items():
            dR = R_w[srcb] @ R0[srcb].transposed()
            if tgt in SWING_BONES:
                ch = CHILD_SRC[srcb]
                d = pos_w[ch] - pos_w[srcb]
                v1 = G @ d
                if v1.length > 1e-9:
                    v1n = v1.normalized()
                    sw = rot_between(rest_dir_tgt[tgt], npv(v1n))
                    d0 = npv(pos0[ch] - pos0[srcb])           # rest 骨段方向 = twist 轴
                    th = twist_angle(np.array(dR), d0)
                    twm = rot_axis_angle(npv(v1n), th)
                    Rw[tgt] = np2m(twm @ sw) @ rest_tgt[tgt]
                    continue
            dRb = G @ np2m(dR) @ G.transposed()
            Rw[tgt] = dRb @ rest_tgt[tgt]

        # ── Hips: 水平 = 相对动作首帧位移(经 G); 高度 = rest 髋高 + 相对变化 ──
        hp = pos_w["mixamorig:Hips"]
        if hips_ref is None:
            hips_ref = hp.copy()
        dp = (hp - hips_ref)
        dp.z = 0.0
        dp_b = G @ (dp * scale)
        tgt_pos = arm.bones["Hips"].head_local.copy()
        tgt_pos.x += dp_b.x
        tgt_pos.y += dp_b.y
        tgt_pos.z = hips_rest_z_dst + (hp.z - hips0.z) * scale
        # 骨盆物理下限：倒地/贴地动作里源人偶骨盆可降到极低,
        # 但守门员腿相对短(髋高/身高 0.53 vs 源 0.57), 等比缩放会穿地。
        # 骨盆贴地极限 ≈ 臀部半径, 取模型空间 0.10m。
        tgt_pos.z = max(tgt_pos.z, 0.10)
        tgt_m = Rw["Hips"].to_4x4()
        tgt_m.translation = tgt_pos
        basis_m = arm.bones["Hips"].matrix_local.inverted() @ tgt_m
        pb = arm_obj.pose.bones["Hips"]
        pb.rotation_quaternion = basis_m.to_quaternion()
        pb.location = basis_m.to_translation()
        pb.keyframe_insert("rotation_quaternion", frame=t + 1)
        pb.keyframe_insert("location", frame=t + 1)

        for tgt in MAP:
            if tgt == "Hips":
                continue
            par = PARENT_TGT[tgt]
            rp = Rw[par] if par else Matrix.Identity(3)
            rl_pose = rp.transposed() @ Rw[tgt]
            rl_rest = rest_tgt[par].transposed() @ rest_tgt[tgt]
            q = (rl_rest.transposed() @ rl_pose).to_quaternion()
            q.normalize()
            pb = arm_obj.pose.bones[tgt]
            pb.rotation_quaternion = q
            pb.keyframe_insert("rotation_quaternion", frame=t + 1)

    # ── 清理源对象 ──
    for o in set(bpy.data.objects) - before:
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.outliner.orphans_purge(do_local_ids=True, do_linked_ids=True,
                                   do_recursive=True)
    return action, T


def main():
    bpy.ops.wm.open_mainfile(filepath=BLEND_IN)
    arm_obj = bpy.data.objects["ShooterRig"]
    scn = bpy.context.scene
    scn.render.fps = 30

    only = set(int(x) for x in ONLY.split(",") if x.strip() != "")
    idxs = sorted(only) if only else range(len(NAMES))

    # 地面
    if "Ground" not in bpy.data.objects:
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

    tracks = {}
    cursor = 1
    n_all = []
    for i in idxs:
        fbx_path = os.path.join(FBX_DIR, FILES[i])
        act, n = build_action(arm_obj, fbx_path, NAMES[i])
        n_all.append((NAMES[i], n))
        if not only:   # 全量时排 NLA 连播
            if "GK_Motions" not in tracks:
                tr = arm_obj.animation_data.nla_tracks.new()
                tr.name = "GK_Motions"
                tracks["GK_Motions"] = tr
            st = tracks["GK_Motions"].strips.new(act.name, cursor, act)
            st.frame_start = cursor
            st.frame_end = cursor + n - 1
            st.influence = 1.0
            cursor += n

    if only:
        first = idxs[0]
        arm_obj.animation_data.action = bpy.data.actions.get(NAMES[first])
        scn.frame_start = 1
        scn.frame_end = n_all[0][1]
    else:
        # NLA 连播模式: 清掉 active action, 否则与 strip 叠加双重驱动
        arm_obj.animation_data.action = None
        scn.frame_start = 1
        scn.frame_end = cursor - 1

    scn.frame_set(1)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT)
    print("[gk] saved", BLEND_OUT, " 总帧数", cursor - 1 if not only else n_all[0][1])
    for nm, n in n_all:
        print("   ", nm, n, "帧")


main()
print("DONE")
