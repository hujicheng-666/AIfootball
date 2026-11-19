"""Blender 探针：读 Shooter.obj 的包围盒、顶点数、朝向，以及 CMU 骨架尺寸。

只读，输出到 stdout，供后续建骨与坐标换算用。
"""
import sys
import numpy as np

import bpy
from mathutils import Vector

argv = sys.argv
argv = argv[argv.index("--") + 1:] if "--" in argv else []
obj_path = argv[0]
sys.path.insert(0, r"D:\AIfootball\scripts")
from cmu_amc import Asf, load_sequence  # noqa: E402
from pathlib import Path

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.wm.obj_import(filepath=obj_path)

print("=" * 70)
for ob in bpy.data.objects:
    print("OBJ", ob.type, ob.name, "scale", tuple(round(v, 4) for v in ob.scale))
    if ob.type == "MESH":
        me = ob.data
        n = len(me.vertices)
        co = np.empty(n * 3, dtype=np.float32)
        me.vertices.foreach_get("co", co)
        v = co.reshape(-1, 3)
        print(f"  verts={n}  polys={len(me.polygons)}  materials={[m.name if m else None for m in me.materials]}")
        for i, ax in enumerate("XYZ"):
            print(f"  {ax}: {v[:, i].min():+.4f} .. {v[:, i].max():+.4f}  span={v[:, i].max()-v[:, i].min():.4f}")
        # 手臂判断：T-pose 手臂沿某轴水平伸展，取极端 8% 的顶点的中位高度，判断哪根轴是"上"
        for i, ax in enumerate("XYZ"):
            lo = v[:, i] < (v[:, i].min() + 0.04 * (v[:, i].max() - v[:, i].min()))
            hi = v[:, i] > (v[:, i].max() - 0.04 * (v[:, i].max() - v[:, i].min()))
            ext = v[lo | hi]
            if len(ext):
                other = [j for j in range(3) if j != i]
                print(f"  ext[{ax}] n={len(ext)} median {other[0]}={np.median(ext[:, other[0]]):+.3f} "
                      f"{other[1]}={np.median(ext[:, other[1]]):+.3f}")

print("-" * 70)
cmu = Path(r"D:\AIfootball\data\external\cmu")
keys, frames, asf = load_sequence(cmu / "10.asf", cmu / "10_02.amc")
pos, rot = frames[0]
print(f"CMU 10_02 帧数 {len(frames)}  首帧 {keys[0]}")
for name in ["root", "lowerback", "thorax", "upperneck", "head",
             "lhumerus", "lradius", "lwrist", "rhumerus", "rradius", "rwrist",
             "lfemur", "ltibia", "lfoot", "ltoes", "rfemur", "rtibia", "rfoot", "rtoes"]:
    p = pos[name]
    print(f"  {name:12s} ({p[0]:+8.3f},{p[1]:+8.3f},{p[2]:+8.3f})")
print("head_y - toe_y =", pos["head"][1] - pos["ltoes"][1])
print("DONE")
