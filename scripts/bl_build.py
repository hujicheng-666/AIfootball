"""Blender 端为混元射手网格重建人形骨架 + 自动权重。

为什么不在 Unity 里用 HunyuanRigger
---------------------------------
Unity 那套是「水平切片猜关节 + 到骨段距离四次方反比算权重」，是程序化近似。
问题不在动画数据，而在这层绑定：膝关节没有真正的权重过渡，抬腿时大腿与小腿
交界会塌成一片；腋下同理。Blender 的 bone-heat 是解拉普拉斯方程的扩散算法，
质量高一个量级，这是重建绑定的唯一理由。

坐标约定（重要）
-------------
原 OBJ 是 Y-up：X=左右、Y=身高、Z=前后（脚趾朝 +Z）。
绕 X 轴 +90° 后成为 Blender 的 Z-up：
    原 +Y(上)  -> +Z(上)
    原 +Z(前)  -> -Y(前)     => 角色**面朝 -Y**
    原 +X      -> +X
Blender/Rigify 约定：面朝 -Y 的角色，**左侧在 +X，右侧在 -X**。
本脚本一律按这个约定建骨，CMU 的 l* 骨映到 +X、r* 骨映到 -X。
"""
import sys
import math
import bpy
from mathutils import Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
OBJ_PATH = argv[0]
OUT_BLEND = argv[1]

# ── 比例常量（相对身高 H，来自人体测量中位数，也与 HunyuanRigger 一致）──
HIPS_F = 0.530
KNEE_F = 0.285
ANKLE_F = 0.045
ARM_F = 0.856          # 实测手臂中位高度（占 H 比例），T-pose 手臂裸露可靠
NECK_LIFT = 0.030
HEAD_LIFT = 0.055
LEG_X = 0.062
SHOULDER_X = 0.098
WRIST_INSET = 0.062
TOE_FWD = 0.075


def build():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.wm.obj_import(filepath=OBJ_PATH)

    meshes = [o for o in bpy.data.objects if o.type == "MESH"]
    src = meshes[0]
    src.name = "ShooterBody"
    # Y-up -> Z-up
    src.rotation_mode = "XYZ"
    src.rotation_euler = (math.radians(90.0), 0.0, 0.0)
    bpy.context.view_layer.objects.active = src
    src.select_set(True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.context.view_layer.update()

    # ── 量尺寸 ──
    bb = [src.matrix_world @ Vector(c) for c in src.bound_box]
    zmin = min(v.z for v in bb)
    zmax = max(v.z for v in bb)
    xmax = max(abs(v.x) for v in bb)
    ymin = min(v.y for v in bb)
    ymax = max(v.y for v in bb)
    H = zmax - zmin
    cY = (ymin + ymax) * 0.5
    print(f"[build] H={H:.4f} X±{xmax:.4f} Y[{ymin:.4f},{ymax:.4f}] cY={cY:.4f}")

    z0 = zmin
    hipsZ = z0 + HIPS_F * H
    kneeZ = z0 + KNEE_F * H
    ankleZ = z0 + ANKLE_F * H
    armZ = z0 + ARM_F * H
    neckZ = min(armZ + NECK_LIFT * H, z0 + 0.905 * H)
    headZ = neckZ + HEAD_LIFT * H
    legX = LEG_X * H
    shoX = SHOULDER_X * H
    wristX = xmax - WRIST_INSET * H
    toeZ = z0 + 0.028 * H
    toeY = cY - TOE_FWD * H

    def lerp(a, b, t):
        return a + (b - a) * t

    # 关节位置（Blender 世界坐标，Z-up，面朝 -Y，左在 +X）
    J = {
        "Hips":        Vector((0.0, cY, hipsZ)),
        "Spine":       Vector((0.0, cY - 0.002 * H, lerp(hipsZ, neckZ, 0.28))),
        "Chest":       Vector((0.0, cY - 0.004 * H, lerp(hipsZ, neckZ, 0.62))),
        "Neck":        Vector((0.0, cY - 0.006 * H, neckZ)),
        "Head":        Vector((0.0, cY - 0.004 * H, headZ)),
        "HeadTop":     Vector((0.0, cY, z0 + H)),
    }
    for side, sx in (("Left", 1.0), ("Right", -1.0)):
        J[f"{side}Shoulder"] = Vector((sx * shoX * 0.45, cY - 0.004 * H, armZ + 0.018 * H))
        J[f"{side}UpperArm"] = Vector((sx * shoX, cY - 0.004 * H, armZ - 0.008 * H))
        J[f"{side}LowerArm"] = Vector((sx * lerp(shoX, wristX, 0.52), cY - 0.004 * H + 0.004 * H,
                                       armZ - 0.012 * H))
        J[f"{side}Hand"] = Vector((sx * wristX, cY - 0.004 * H + 0.002 * H, armZ - 0.012 * H))
        J[f"{side}HandTip"] = Vector((sx * (wristX + 0.055 * H), cY - 0.004 * H + 0.002 * H,
                                      armZ - 0.012 * H))
        J[f"{side}UpperLeg"] = Vector((sx * legX, cY, hipsZ - 0.012 * H))
        J[f"{side}LowerLeg"] = Vector((sx * legX, cY + 0.004 * H, kneeZ))
        J[f"{side}Foot"] = Vector((sx * legX, cY - 0.004 * H, ankleZ))
        J[f"{side}Toes"] = Vector((sx * legX, toeY, toeZ))
        J[f"{side}ToesTip"] = Vector((sx * legX, toeY - 0.045 * H, toeZ))

    # ── 建骨架 ──
    arm_data = bpy.data.armatures.new("ShooterArmature")
    arm_obj = bpy.data.objects.new("ShooterRig", arm_data)
    bpy.context.scene.collection.objects.link(arm_obj)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.mode_set(mode="EDIT")

    eb = arm_data.edit_bones

    def mk(name, head, tail, parent=None, connect=False):
        b = eb.new(name)
        b.head = head
        b.tail = tail
        if parent:
            b.parent = eb[parent]
            b.use_connect = connect
        return b

    mk("Hips", J["Hips"], J["Spine"])
    mk("Spine", J["Spine"], J["Chest"], "Hips", True)
    mk("Chest", J["Chest"], J["Neck"], "Spine", True)
    mk("Neck", J["Neck"], J["Head"], "Chest", True)
    mk("Head", J["Head"], J["HeadTop"], "Neck", True)
    for side in ("Left", "Right"):
        mk(f"{side}Shoulder", J[f"{side}Shoulder"], J[f"{side}UpperArm"], "Chest")
        mk(f"{side}UpperArm", J[f"{side}UpperArm"], J[f"{side}LowerArm"], f"{side}Shoulder", True)
        mk(f"{side}LowerArm", J[f"{side}LowerArm"], J[f"{side}Hand"], f"{side}UpperArm", True)
        mk(f"{side}Hand", J[f"{side}Hand"], J[f"{side}HandTip"], f"{side}LowerArm", True)
        mk(f"{side}UpperLeg", J[f"{side}UpperLeg"], J[f"{side}LowerLeg"], "Hips")
        mk(f"{side}LowerLeg", J[f"{side}LowerLeg"], J[f"{side}Foot"], f"{side}UpperLeg", True)
        mk(f"{side}Foot", J[f"{side}Foot"], J[f"{side}Toes"], f"{side}LowerLeg", True)
        mk(f"{side}Toes", J[f"{side}Toes"], J[f"{side}ToesTip"], f"{side}Foot", True)

    bpy.ops.object.mode_set(mode="OBJECT")

    # ── 权重：bone heat 对某些大网格会静默分配全 0（守门员实测失败），
    # 改用自写距离场权重：顶点到骨段投影距离的 4 次方反比 + top4 截断，
    # 再做顶点邻接扩散平滑拉宽过渡带。质量不依赖 heat 的数值求解。
    bpy.ops.object.select_all(action="DESELECT")
    src.select_set(True)
    arm_obj.select_set(True)
    bpy.context.view_layer.objects.active = arm_obj
    bpy.ops.object.parent_set(type="ARMATURE_NAME")   # 仅建父子关系，不分配权重

    import numpy as np
    mesh = src.data
    nvp = len(mesh.vertices)
    V = np.empty((nvp, 3), dtype=np.float64)
    mesh.vertices.foreach_get("co", V.ravel())

    # 骨段（armature 无变换，本地坐标即世界坐标）
    bone_names = [b.name for b in arm_data.bones]
    n_bones = len(bone_names)
    segs = [(np.array(b.head_local), np.array(b.tail_local)) for b in arm_data.bones]
    dists = np.empty((nvp, n_bones), dtype=np.float64)
    for i, (h, t) in enumerate(segs):
        ab = t - h
        L2 = float(ab @ ab)
        tt = np.clip(((V - h) @ ab) / max(L2, 1e-9), 0.0, 1.0)
        proj = h + tt[:, None] * ab
        dists[:, i] = np.linalg.norm(V - proj, axis=1)
    W = 1.0 / (dists + 5e-3) ** 4
    # top4 截断
    idx = np.argpartition(W, -4, axis=1)[:, -4:]
    mask = np.zeros_like(W, dtype=bool)
    np.put_along_axis(mask, idx, True, axis=1)
    W[~mask] = 0.0
    print(f"[build] 距离权重完成：top4 截断，每顶点候选 {(mask.sum(axis=1)).mean():.1f} 骨")
    W = W.T   # (nvp, n_bones) -> (n_bones, nvp)，后续平滑按骨行处理

    # ── 权重后处理：邻接平滑 + 小权重清理 ──
    # 距离场权重逐点独立计算，骨交界处过渡窄而尖，弯曲时呈硬折痕。
    # 顶点邻接扩散（λ 迭代平滑）把权重过渡拉宽。
    edges = np.array([e.vertices for e in mesh.edges], dtype=np.int64)
    e0, e1 = edges[:, 0], edges[:, 1]
    cnt = (np.bincount(e0, minlength=nvp) + np.bincount(e1, minlength=nvp)).astype(np.float64)
    cnt[cnt < 1] = 1.0
    lam = 0.45
    for bi in range(n_bones):
        w = W[bi]
        for _ in range(3):
            s = (np.bincount(e0, weights=w[e1], minlength=nvp)
                 + np.bincount(e1, weights=w[e0], minlength=nvp))
            w = (1.0 - lam) * w + lam * (s / cnt)
        W[bi] = w

    # 小权重清理 + 归一化
    W[W < 0.008] = 0.0
    ssum = W.sum(axis=0)
    safe = ssum > 1e-6
    W[:, safe] /= ssum[safe]
    # 残余未加权（孤立顶点）给 Hips
    hips_i = bone_names.index("Hips") if "Hips" in bone_names else 0
    W[hips_i, ~safe] = 1.0

    # 写入 vertex groups（清空 ARMATURE_NAME 生成的空组后重建）
    for g in list(src.vertex_groups):
        src.vertex_groups.remove(g)
    gmap = [src.vertex_groups.new(name=bn) for bn in bone_names]
    nz = np.nonzero(W)
    for bi, vi in zip(nz[0], nz[1]):
        gmap[int(bi)].add([int(vi)], float(W[bi, vi]), "REPLACE")
    print(f"[build] 权重完成：距离场 top4 + λ=0.45×3 平滑，阈值 0.008，"
          f"非零项 {len(nz[0])} 个，平均每顶点 {len(nz[0])/nvp:.1f} 骨")

    bpy.ops.wm.save_as_mainfile(filepath=OUT_BLEND)
    print("[build] saved", OUT_BLEND)


build()
print("DONE")
