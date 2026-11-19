"""确认模型在 Blender 里的朝向：导入 OBJ，转成 Z-up，渲染四个方位。

输出到 .tmp_orient/。只为判断「哪根轴是前 / 哪边是左手」，不做任何资产改动。
"""
import sys
import math
import bpy
from mathutils import Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
obj_path = argv[0]
out_dir = argv[1]

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=obj_path)

mesh_obs = [o for o in bpy.data.objects if o.type == "MESH"]
root = mesh_obs[0]
# Y-up -> Z-up：绕 X 轴 +90°，原 +Y(身高) -> +Z，原 +Z -> -Y
root.rotation_mode = "XYZ"
root.rotation_euler = (math.radians(90.0), 0.0, 0.0)
bpy.context.view_layer.update()

# 世界包围盒
mn = Vector((1e9, 1e9, 1e9))
mx = Vector((-1e9, -1e9, -1e9))
for o in mesh_obs:
    for c in o.bound_box:
        w = o.matrix_world @ Vector(c)
        for i in range(3):
            mn[i] = min(mn[i], w[i])
            mx[i] = max(mx[i], w[i])
ctr = (mn + mx) * 0.5
size = max((mx - mn))
print("BBOX", tuple(round(v, 4) for v in mn), tuple(round(v, 4) for v in mx))
print("SIZE", tuple(round(v, 4) for v in (mx - mn)))

scn = bpy.context.scene
scn.render.engine = "BLENDER_WORKBENCH"
scn.render.resolution_x = 620
scn.render.resolution_y = 900
scn.render.film_transparent = False
sh = scn.display.shading
sh.light = "STUDIO"
sh.color_type = "SINGLE"
sh.single_color = (0.62, 0.62, 0.66)
sh.show_cavity = True
sh.show_object_outline = True

cam_data = bpy.data.cameras.new("Cam")
cam_data.type = "ORTHO"
cam_data.ortho_scale = size * 1.12
cam = bpy.data.objects.new("Cam", cam_data)
scn.collection.objects.link(cam)
scn.camera = cam

D = size * 3.0
views = {
    "front_negY": (ctr + Vector((0, -D, 0)), (math.radians(90), 0, 0)),
    "back_posY": (ctr + Vector((0, D, 0)), (math.radians(90), 0, math.radians(180))),
    "right_posX": (ctr + Vector((D, 0, 0)), (math.radians(90), 0, math.radians(90))),
    "left_negX": (ctr + Vector((-D, 0, 0)), (math.radians(90), 0, math.radians(-90))),
}
for name, (loc, rot) in views.items():
    cam.location = loc
    cam.rotation_euler = rot
    scn.render.filepath = f"{out_dir}/{name}.png"
    bpy.ops.render.render(write_still=True)
    print("RENDERED", name)
print("DONE")
