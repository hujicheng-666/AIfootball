# -*- coding: utf-8 -*-
"""给守门员网格贴 Hunyuan PBR 贴图并渲染验证。"""
import bpy, sys
from mathutils import Vector

argv = sys.argv[sys.argv.index("--")+1:]
BLEND, TEX, OUT = argv[0], argv[1], argv[2]
frames = [int(v) for v in argv[3].split(",")] if len(argv) > 3 else [1]

bpy.ops.wm.open_mainfile(filepath=BLEND)
scn = bpy.context.scene
body = bpy.data.objects["ShooterBody"]

mat = bpy.data.materials.new("GK_PBR")
mat.use_nodes = True
nt = mat.node_tree
bsdf = nt.nodes["Principled BSDF"]
tex_img = nt.nodes.new("ShaderNodeTexImage")
tex_img.image = bpy.data.images.load(TEX, check_existing=True)
nt.links.new(tex_img.outputs["Color"], bsdf.inputs["Base Color"])
# OBJ 体系无 roughness 贴图, 给个布料手感
bsdf.inputs["Roughness"].default_value = 0.85
body.data.materials.clear()
body.data.materials.append(mat)

scn.render.engine = "BLENDER_EEVEE"
scn.render.resolution_x = 760
scn.render.resolution_y = 900
arm = bpy.data.objects["ShooterRig"]

# 光照
if "Sun" not in bpy.data.objects:
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", 'SUN'))
    sun.data.energy = 3.0
    sun.rotation_euler = (0.9, 0.0, 0.6)
    scn.collection.objects.link(sun)
if "World" not in bpy.data.worlds or bpy.data.worlds[0] is None:
    w = bpy.data.worlds.new("World"); scn.world = w
scn.world.use_nodes = True
bg = scn.world.node_tree.nodes.get("Background")
if bg: bg.inputs["Strength"].default_value = 1.0

cam_data = bpy.data.cameras.new("Cam"); cam_data.lens = 55
cam = bpy.data.objects.new("Cam", cam_data)
scn.collection.objects.link(cam); scn.camera = cam

for f in frames:
    scn.frame_set(f); bpy.context.view_layer.update()
    hp = arm.pose.bones["Hips"].matrix.translation
    loc = Vector((hp.x + 1.6, hp.y - 1.9, hp.z + 0.7))
    cam.location = loc
    cam.rotation_mode = "QUATERNION"
    cam.rotation_quaternion = (hp - loc).to_track_quat("-Z", "Y")
    scn.render.filepath = f"{OUT}/mat_f{f:04d}.png"
    bpy.ops.render.render(write_still=True)
    print("RENDERED", f)
bpy.ops.wm.save_as_mainfile(filepath=BLEND)
print("SAVED", BLEND)
print("DONE")
