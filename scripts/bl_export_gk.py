# -*- coding: utf-8 -*-
"""守门员 FBX 导出：减面网格 + 骨架 + 22 个 Mixamo 动作, 供 Unity 使用。"""
import bpy, sys

argv = sys.argv[sys.argv.index("--")+1:]
BLEND, OUT_FBX = argv[0], argv[1]
DECIMATE = float(argv[2]) if len(argv) > 2 else 0.1   # 减面比率

bpy.ops.wm.open_mainfile(filepath=BLEND)
body = bpy.data.objects["ShooterBody"]
arm = bpy.data.objects["ShooterRig"]

# ── 减面（运行时蒙皮 75 万顶点太重）──
if DECIMATE < 1.0:
    dec = body.modifiers.get("Decimate")
    if dec is None:
        dec = body.modifiers.new("Decimate", 'DECIMATE')
    dec.ratio = DECIMATE
    bpy.context.view_layer.objects.active = body
    bpy.ops.object.modifier_apply(modifier="Decimate")
    print("after decimate: verts", len(body.data.vertices), "polys", len(body.data.polygons))

# ── 选中骨架+网格导出 ──
bpy.ops.object.select_all(action='DESELECT')
arm.select_set(True); body.select_set(True)
bpy.context.view_layer.objects.active = arm

bpy.ops.export_scene.fbx(
    filepath=OUT_FBX,
    use_selection=True,
    object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=False,
    add_leaf_bones=False,
    bake_anim=True,
    bake_anim_use_nla_strips=False,
    bake_anim_use_all_actions=True,
    bake_anim_simplify_factor=0.0,
    apply_unit_scale=True,
    apply_scale_options='FBX_SCALE_NONE',
    axis_forward='-Z', axis_up='Y',
    global_scale=1.0,
)
print("EXPORTED", OUT_FBX)
print("DONE")
