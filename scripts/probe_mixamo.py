# -*- coding: utf-8 -*-
"""探测守门员 blend 与 Mixamo FBX 导入后的结构。"""
import bpy, sys

fbx = sys.argv[sys.argv.index("--") + 1]
bpy.ops.wm.open_mainfile(filepath=r"D:/AIfootball/output/blender/goalkeeper_rigged.blend")
print("== scene objects ==")
for o in bpy.data.objects:
    print(" ", o.name, o.type, tuple(round(v, 3) for v in o.matrix_world.to_3x3()[0]),
          tuple(round(v, 3) for v in o.matrix_world.to_3x3()[2]))
    if o.type == "ARMATURE":
        print("   bones:", [b.name for b in o.data.bones])
        print("   Hips head:", tuple(round(v, 4) for v in o.data.bones["Hips"].head_local),
              " Head tail_z:", round(o.data.bones["Head"].tail_local.z, 4))

before = set(bpy.data.objects)
bpy.ops.import_scene.fbx(filepath=fbx)
new = set(bpy.data.objects) - before
print("== imported ==")
for o in new:
    m = o.matrix_world
    print(" NEW:", o.name, o.type, "MW=")
    print("   ", tuple(round(v, 4) for v in m[0]), "|", tuple(round(v, 4) for v in m[1]),
          "|", tuple(round(v, 4) for v in m[2]), "|", tuple(round(v, 4) for v in m[3]))
    if o.type == "ARMATURE":
        print("   bones:", [b.name for b in o.data.bones])
        ad = o.animation_data
        act = ad.action if ad else None
        print("   action:", act.name if act else None,
              "range:", tuple(round(v, 2) for v in act.frame_range) if act else None)
        for bn in ("mixamorig:Hips", "mixamorig:LeftArm", "mixamorig:LeftToeBase"):
            pb = o.pose.bones.get(bn)
            if pb:
                print(f"   {bn}: head_world_rest=",
                      tuple(round(v, 4) for v in (o.matrix_world @ pb.bone.matrix_local).translation),
                      " children:", [c.name for c in pb.bone.children])
