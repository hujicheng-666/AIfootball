"""渲染 .blend 里的指定帧，用于验证关节是否扭曲。

相机固定看向角色，两个机位：正面（-Y）与 3/4 侧。-Y 是角色面朝方向，
所以正面图等价于"从球门看向射手"。
"""
import sys
import math
import bpy
from mathutils import Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
BLEND = argv[0]
OUT = argv[1]
frames = [int(v) for v in argv[2].split(",")]
tag = argv[3] if len(argv) > 3 else "f"

bpy.ops.wm.open_mainfile(filepath=BLEND)
scn = bpy.context.scene
arm = bpy.data.objects["ShooterRig"]

scn.render.engine = "BLENDER_WORKBENCH"
scn.render.resolution_x = 760
scn.render.resolution_y = 900
scn.render.film_transparent = False
sh = scn.display.shading
sh.light = "STUDIO"
sh.studio_light = "Default"
sh.color_type = "SINGLE"
sh.single_color = (0.60, 0.62, 0.68)
sh.show_cavity = True
sh.cavity_type = "BOTH"
sh.show_object_outline = True
sh.show_shadows = True
sh.shadow_intensity = 0.35

cam_data = bpy.data.cameras.new("Cam")
cam_data.lens = 55
cam = bpy.data.objects.new("Cam", cam_data)
scn.collection.objects.link(cam)
scn.camera = cam

TARGET = Vector((0.0, 0.0, 0.62))
DIST = 2.5
VIEWS = {
    "front": Vector((0.0, -DIST, 0.95)),
    "side":  Vector((DIST * 0.96, -DIST * 0.24, 0.92)),
    "q34":   Vector((DIST * 0.64, -DIST * 0.74, 1.00)),
}


def aim(cam_obj, loc, target):
    """用 Blender 自带的 track_quat 求朝向，避免手推欧拉角出错。"""
    cam_obj.location = loc
    cam_obj.rotation_mode = "QUATERNION"
    cam_obj.rotation_quaternion = (target - loc).to_track_quat("-Z", "Y")


for f in frames:
    scn.frame_set(f)
    bpy.context.view_layer.update()
    for vname, loc in VIEWS.items():
        aim(cam, loc, TARGET)
        scn.render.filepath = f"{OUT}/{tag}_f{f:04d}_{vname}.png"
        bpy.ops.render.render(write_still=True)
    print("RENDERED", f)
print("DONE")
