"""Blender 端探针：确认版本、FBX 导入算子、模型骨架结构。

只读，不改动任何项目文件。用于确定后续重定向脚本的骨骼命名与 rest 姿态。
"""
import sys
import bpy

print("=" * 70)
print("BLENDER", bpy.app.version_string)

# FBX 导入算子在 4.2 之后从 import_scene.fbx 迁到 wm.fbx_import
fbx_op = None
for name in ("wm.fbx_import", "import_scene.fbx"):
    mod, fn = name.split(".")
    if hasattr(getattr(bpy.ops, mod, None), fn):
        fbx_op = name
        break
print("FBX_OP:", fbx_op)

argv = sys.argv
if "--" in argv:
    argv = argv[argv.index("--") + 1:]
else:
    argv = []

path = argv[0] if argv else ""
if not path:
    print("NO_PATH")
    sys.exit(0)

mod, fn = fbx_op.split(".")
kwargs = dict(filepath=path, use_anim=False)
getattr(getattr(bpy.ops, mod), fn)(**kwargs)

print("-" * 70)
for ob in bpy.data.objects:
    print("OBJ", ob.type, "|", ob.name, "| scale", tuple(round(v, 4) for v in ob.scale))

for ob in bpy.data.objects:
    if ob.type != "ARMATURE":
        continue
    arm = ob.data
    print("-" * 70)
    print("ARMATURE", ob.name, "bones", len(arm.bones))
    for b in arm.bones:
        h = b.head_local
        t = b.tail_local
        print(f"BONE\t{b.name}\tparent={b.parent.name if b.parent else '-'}\t"
              f"head=({h.x:.3f},{h.y:.3f},{h.z:.3f})\t"
              f"tail=({t.x:.3f},{t.y:.3f},{t.z:.3f})\tlen={b.length:.3f}")
    for ob2 in bpy.data.objects:
        if ob2.type == "MESH" and any(m.type == "ARMATURE" and m.object == ob for m in ob2.modifiers):
            print("MESH", ob2.name, "verts", len(ob2.data.vertices),
                  "mats", [m.name if m else None for m in ob2.data.materials],
                  "scale", tuple(round(v, 3) for v in ob2.scale))
print("DONE")
