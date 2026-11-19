"""把 CMU 动捕重定向到 Blender 里的射手骨架，并保存成可直接播放的 .blend。

重定向原理
----------
不用欧拉角、不用 swing/twist 分解，只用一个恒等式：

    目标骨世界旋转 = G · (A · ΔR_cmu · Aᵀ) · R_rest_target

其中
    ΔR_cmu       = R_cmu(t) · R_rest_cmuᵀ      相对 T-pose 的旋转增量
    A            = CMU 坐标基 -> Blender 坐标基（正交，det=+1，只换基不镜像）
    G            = 全局朝向修正（让射门瞬间角色面朝 -Y）
    R_rest_target= 模型骨在 T-pose 的 armature 空间旋转

因 ΔR 是在骨骼自己的父坐标系里量的相对量，所以它天然与骨长、骨方向无关：
大腿短 22% 不会让旋转出错，只会让脚的位置偏低。这是纯旋转映射的定义性质。

坐标基
------
CMU：X=左, Y=上, Z=前（右手系）
Blender：X=右, Y=后, Z=上；角色面朝 -Y，故左侧在 +X
A 满足 (bx,by,bz) = (cx, -cz, cy)
"""
import sys
import math
from pathlib import Path

import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
BLEND_IN = argv[0]
ASF = Path(argv[1])
AMC = Path(argv[2])
BLEND_OUT = argv[3]
osd = sys.stdout

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cmu_amc import Asf, forward_kinematics, read_amc  # noqa: E402

# CMU 关节 -> 模型骨名
MAP = {
    "Hips":       "lowerback",
    "Spine":      "upperback",
    "Chest":      "thorax",
    "Neck":       "lowerneck",
    "Head":       "head",
    "LeftShoulder":  "lclavicle",
    "LeftUpperArm":  "lhumerus",
    "LeftLowerArm":  "lradius",
    "LeftHand":      "lwrist",
    "RightShoulder": "rclavicle",
    "RightUpperArm": "rhumerus",
    "RightLowerArm": "rradius",
    "RightHand":     "rwrist",
    "LeftUpperLeg":  "lfemur",
    "LeftLowerLeg":  "ltibia",
    "LeftFoot":      "lfoot",
    "LeftToes":      "ltoes",
    "RightUpperLeg": "rfemur",
    "RightLowerLeg": "rtibia",
    "RightFoot":     "rfoot",
    "RightToes":     "rtoes",
}
# 期望的父子关系（用于算局部 basis）
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

# CMU 关节 -> 子关节，用来取骨段方向
CMU_CHILD = {
    "lowerback": "upperback", "upperback": "thorax", "thorax": "lowerneck",
    "lowerneck": "upperneck", "upperneck": "head",
    "lclavicle": "lhumerus", "lhumerus": "lradius", "lradius": "lwrist", "lwrist": "lhand",
    "rclavicle": "rhumerus", "rhumerus": "rradius", "rradius": "rwrist", "rwrist": "rhand",
    "lfemur": "ltibia", "ltibia": "lfoot", "lfoot": "ltoes",
    "rfemur": "rtibia", "rtibia": "rfoot", "rfoot": "rtoes",
    # 脚趾骨用「脚掌方向」：CMU 只有到趾尖的关节，没有再往前的骨段
    "ltoes": "ltoes", "rtoes": "rtoes",
}
# 四肢只对齐「骨方向」，扭转沿用模型 rest。
# 躯干保留完整世界增量，否则无法表达转体。
SWING_BONES = {
    "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
    "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
    "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
    "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
}


def twist_angle(dW: np.ndarray, axis: np.ndarray) -> float:
    """把世界旋转增量 dW 沿骨轴 axis 做 swing-twist 分解，返回扭转角（带符号）。

    四元数 q=(w,v) 绕轴的扭转分量 = normalize((w, v·axis))，
    角度 = 2*atan2(dot(v,axis), w)。符号与 axis 的指向一致。
    """
    m = np.asarray(dW, dtype=float)
    # 矩阵 -> 四元数（Shepperd 法）
    t = np.trace(m)
    if t > 0.0:
        r_ = np.sqrt(t + 1.0)
        w = 0.5 * r_
        v = np.array([m[2, 1] - m[1, 2], m[0, 2] - m[2, 0], m[1, 0] - m[0, 1]]) / (2.0 * r_)
    else:
        i = int(np.argmax(np.diag(m)))
        j, k = (i + 1) % 3, (i + 2) % 3
        r_ = np.sqrt(m[i, i] - m[j, j] - m[k, k] + 1.0)
        vv = np.zeros(3)
        vv[i] = 0.5 * r_
        w = (m[k, j] - m[j, k]) / (2.0 * r_)
        vv[j] = (m[j, i] + m[i, j]) / (2.0 * r_)
        vv[k] = (m[k, i] + m[i, k]) / (2.0 * r_)
        v = vv
    a = axis / np.linalg.norm(axis)
    sv = float(np.dot(v, a))
    return 2.0 * math.atan2(sv, w)


def rot_axis_angle(axis: np.ndarray, ang: float) -> np.ndarray:
    """绕单位轴 axis 旋转 ang 的矩阵（Rodrigues）。"""
    a = axis / np.linalg.norm(axis)
    K = np.array([[0.0, -a[2], a[1]], [a[2], 0.0, -a[0]], [-a[1], a[0], 0.0]])
    return np.eye(3) + math.sin(ang) * K + (1.0 - math.cos(ang)) * (K @ K)


def rot_between(a: np.ndarray, b: np.ndarray) -> np.ndarray:
    """把单位向量 a 转到 b 的最小弧旋转。"""
    a = a / np.linalg.norm(a)
    b = b / np.linalg.norm(b)
    v = np.cross(a, b)
    c = float(np.dot(a, b))
    if c < -0.999999:
        # 反向：任一垂直轴转 180°
        axis = np.cross(a, [1.0, 0.0, 0.0])
        if np.linalg.norm(axis) < 1e-6:
            axis = np.cross(a, [0.0, 1.0, 0.0])
        axis /= np.linalg.norm(axis)
        return 2.0 * np.outer(axis, axis) - np.eye(3)
    vx = np.array([[0.0, -v[2], v[1]], [v[2], 0.0, -v[0]], [-v[1], v[0], 0.0]])
    return np.eye(3) + vx + vx @ vx / (1.0 + c)


# (cx,cy,cz) -> (cx, -cz, cy)
A = np.array([[1.0, 0.0, 0.0],
              [0.0, 0.0, -1.0],
              [0.0, 1.0, 0.0]])


def np2m(r: np.ndarray) -> Matrix:
    return Matrix([[float(r[0][0]), float(r[0][1]), float(r[0][2])],
                   [float(r[1][0]), float(r[1][1]), float(r[1][2])],
                   [float(r[2][0]), float(r[2][1]), float(r[2][2])]])


def main():
    bpy.ops.wm.open_mainfile(filepath=BLEND_IN)
    arm_obj = bpy.data.objects["ShooterRig"]
    arm = arm_obj.data

    asf = Asf.parse(ASF)
    motion = read_amc(AMC)
    keys = sorted(motion)

    # CMU T-pose（全零）作为 rest 参照
    zero = {"root": [0.0] * 6}
    pos0, rot0 = forward_kinematics(asf, zero)
    rest_cmu = {k: np.asarray(v, dtype=float) for k, v in rot0.items()}

    # 模型 rest（armature 空间）
    rest_tgt = {b.name: b.matrix_local.to_3x3() for b in arm.bones}
    idx_of = {b.name: i for i, b in enumerate(arm.bones)}

    # CMU 身高做尺度基准
    cmu_head = pos0["head"][1]
    cmu_toe = min(pos0["ltoes"][1], pos0["rtoes"][1])
    cmu_h = cmu_head - cmu_toe
    tgt_h = arm.bones["Head"].tail_local.z   # HeadTop = 头顶，即身高
    scale = tgt_h / cmu_h
    print(f"[retarget] CMU 身高 {cmu_h:.2f}u 模型 {tgt_h:.4f}  scale={scale:.6f}")

    # ── 全局朝向 G：让 kick 帧面朝 -Y ──
    kick = None
    best = -1e9
    for i, k in enumerate(keys):
        fk = forward_kinematics(asf, motion[k])
        if fk is None:
            continue
        p, _ = fk
        z = p["rtoes"][2]
        if z > best:
            best = z
            kick = i
    fk = forward_kinematics(asf, motion[keys[kick]])
    p_k = fk[0]
    left_v = np.array(p_k["lhumerus"], dtype=float) - np.array(p_k["rhumerus"], dtype=float)
    left_v[1] = 0.0
    left_v /= np.linalg.norm(left_v)
    front_cmu = np.cross(left_v, np.array([0.0, 1.0, 0.0]))  # 左×上=前
    front_b = A @ front_cmu
    ang = math.atan2(front_b[1], front_b[0])
    twist = -(math.pi / 2.0) - ang
    G = Matrix.Rotation(twist, 3, "Z")
    print(f"[retarget] kick 帧序号 {kick}/{len(keys)} 帧号 {keys[kick]}  "
          f"朝向修正 {math.degrees(twist):+.1f}°  front_blender={tuple(round(v,3) for v in front_b)}")

    root_ref = np.array(p_k["root"], dtype=float)
    # 站立基准：全零帧的 root 落在 y=0（地面），不能当基准；
    # 取整段序列里骨盆最高的那一帧 = 人最直立时的高度。
    pelvis_max = -1e9
    for k in keys:
        fk = forward_kinematics(asf, motion[k])
        if fk is None:
            continue
        pp = fk[0]
        py = (pp["lfemur"][1] + pp["rfemur"][1]) * 0.5
        if py > pelvis_max:
            pelvis_max = py
    pelvis0 = np.array([0.0, pelvis_max, 0.0])
    print(f"[retarget] 站立骨盆高 {pelvis_max:.2f}u  kick 帧骨盆高 "
          f"{(p_k['lfemur'][1] + p_k['rfemur'][1]) * 0.5:.2f}u  "
          f"=> 下蹲 {((p_k['lfemur'][1]+p_k['rfemur'][1])*0.5 - pelvis_max) * scale:+.3f}u")

    # ── 逐帧写关键帧 ──
    for pb in arm_obj.pose.bones:
        pb.rotation_mode = "QUATERNION"

    Rw = {}  # 当前帧 目标骨 armature 空间旋转

    # 模型每根骨的 rest 骨方向（世界） = R_rest @ Y
    rest_dir_tgt = {n: np.array(m @ Vector((0.0, 1.0, 0.0))) for n, m in rest_tgt.items()}
    # CMU 零姿态骨段方向（扭转分解轴）：关节 -> 子关节位置差
    pos0_child = {}
    for cmu_j in MAP.values():
        cj = CMU_CHILD.get(cmu_j)
        if cj and cj in pos0:
            pos0_child[cmu_j] = np.asarray(pos0[cj], float)
    G_np = np.array(G)
    A_np = A

    def frame_world(p, r, shift, use_shift):
        """写出一帧的目标世界旋转。

        四肢用「骨方向对齐」：
            v0 = 模型该骨 rest 方向
            v1 = CMU 对应骨段方向（换基 + 全局朝向 G）
            W  = rot_between(v0, v1) · R_rest
        因为膝盖/肘的弯曲平面是由相邻两段骨方向共同决定的，直接把方向对齐，
        关节角就必然与动捕一致；扭转分量留给 rest，避免模型腿骨 roll 与 CMU 的
        ASF axis 不一致时把膝盖掰向体侧（实测世界增量法在每条腿骨累积 20°，
        大腿+小腿叠成 40°，脚被顶高 18cm，人看着像站在地上没抬腿）。

        躯干仍用完整世界增量 ΔR = W·W_restᵀ，否则丢掉转体。
        """
        for name, cmu in MAP.items():
            if name in SWING_BONES:
                child = CMU_CHILD[cmu]
                base = {"ltoes": "lfoot", "rtoes": "rfoot"}.get(cmu, cmu)
                if child in p and base in p:
                    d = np.asarray(p[child], float) - np.asarray(p[base], float)
                    v1 = G_np @ (A_np @ d)
                    if np.linalg.norm(v1) > 1e-9:
                        # swing：对齐骨方向（保证膝/肘弯曲角度与动捕一致）
                        sw = rot_between(rest_dir_tgt[name], v1)
                        # twist：从动捕世界增量里提取绕骨轴的扭转角，补回模型。
                        # 没有它，手臂内外旋全部丢失 -> 肘弯平面翻转、前臂拧麻花；
                        # twist 只绕骨轴转，不改变骨方向，所以不会把 CMU 腿骨
                        # 20° 外张带回膝盖（那 20° 在 swing 里被对齐吃掉了）。
                        a_cmu = pos0_child.get(cmu)
                        if a_cmu is not None:
                            d0 = a_cmu - np.asarray(pos0[cmu], float)
                            dl0 = np.asarray(r[cmu], float) @ rest_cmu[cmu].T
                            th = twist_angle(dl0, d0)
                            tw_m = rot_axis_angle(v1 / np.linalg.norm(v1), th)
                            Rw[name] = np2m(tw_m @ sw) @ rest_tgt[name]
                        else:
                            Rw[name] = np2m(sw) @ rest_tgt[name]
                        continue
            dl = np.asarray(r[cmu], float) @ rest_cmu[cmu].T
            Rw[name] = G @ np2m(A @ dl @ A.T) @ rest_tgt[name]
        return Rw

    scn = bpy.context.scene
    scn.render.fps = 120
    action = bpy.data.actions.new(AMC.stem)
    if not arm_obj.animation_data:
        arm_obj.animation_data_create()
    arm_obj.animation_data.action = action

    n_done = 0
    for i, k in enumerate(keys):
        fk = forward_kinematics(asf, motion[k])
        if fk is None:
            continue
        p, r = fk
        frame_world(p, r, None, False)

        # Hips 平移：水平用 root 位移（助跑前进），垂直用**骨盆高度**。
        # 只用 root 位移是错的：root 在 AMC 里是身体基准点，踢球时骨盆会下沉
        # 6~8cm、上身前压，root 却几乎不降。结果人一直站得笔直、两脚悬空，
        # 看着就不像射门。
        pelvis = (np.asarray(p["lfemur"], float) + np.asarray(p["rfemur"], float)) * 0.5
        # 高度与 CMU 绝对对齐：Hips 的目标 z = CMU 骨盆高 × scale。
        # 之前用「模型 rest 髋高(0.571) + 相对变化」，而 CMU 髋高只有 0.41×身高，
        # 差值把人整体抬高了 7cm，两脚跟着悬空——看着像站在地上没动作。
        hips_z_abs = pelvis[1] * scale
        dp_b = np.array([
            p["root"][0] - root_ref[0],
            -(p["root"][2] - root_ref[2]),
            0.0,
        ], dtype=float) * scale
        dp_v = G @ Vector((float(dp_b[0]), float(dp_b[1]), float(dp_b[2])))
        # 目标 armature 矩阵：位置 = (水平位移, CMU 骨盆绝对高度)
        tgt_pos = arm.bones["Hips"].head_local + dp_v
        tgt_pos.z = hips_z_abs
        tgt_m = Rw["Hips"].to_4x4()
        tgt_m.translation = tgt_pos
        basis_m = arm.bones["Hips"].matrix_local.inverted() @ tgt_m
        pb = arm_obj.pose.bones["Hips"]
        pb.rotation_quaternion = basis_m.to_quaternion()
        pb.location = basis_m.to_translation()
        pb.keyframe_insert("rotation_quaternion", frame=i + 1)
        pb.keyframe_insert("location", frame=i + 1)

        for name in MAP:
            if name == "Hips":
                continue
            par = PARENT_TGT[name]
            rp = Rw[par] if par else Matrix.Identity(3)
            rl_pose = rp.transposed() @ Rw[name]
            rl_rest = rest_tgt[par].transposed() @ rest_tgt[name]
            basis = rl_rest.transposed() @ rl_pose
            pb = arm_obj.pose.bones[name]
            q = basis.to_quaternion()
            q.normalize()
            pb.rotation_quaternion = q
            pb.keyframe_insert("rotation_quaternion", frame=i + 1)
        n_done += 1

    scn.frame_start = 1
    scn.frame_end = n_done
    scn.frame_set(1)
    print(f"[retarget] 写入 {n_done} 帧  action={action.name}")

    # 让 .blend 一打开就是这段动作
    arm_obj.animation_data.action = action
    bpy.ops.wm.save_as_mainfile(filepath=BLEND_OUT)
    print("[retarget] saved", BLEND_OUT)


main()
print("DONE")
