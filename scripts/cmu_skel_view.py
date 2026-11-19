# -*- coding: utf-8 -*-
"""渲染 CMU AMC 原始骨架指定帧（matplotlib），用于肘弯方向对比。

用法:
    python cmu_skel_view.py asf amc frames 输出目录 [azim]
azim: mplot3d 方位角。CMU 世界 Y-up。azim=-90 从 -Y 看（正面），0 从 +X 看。
左肢橙红 / 右肢青蓝，与 render3d.py 约定一致。
"""
import sys
from pathlib import Path

import matplotlib

matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cmu_amc import PARENT, Asf, forward_kinematics, read_amc

LEFT = {"lfemur", "ltibia", "lfoot", "ltoes", "lclavicle", "lhumerus",
        "lradius", "lwrist", "lhand"}
RIGHT = {"rfemur", "rtibia", "rfoot", "rtoes", "rclavicle", "rhumerus",
         "rradius", "rwrist", "rhand"}


def main():
    asf = Asf.parse(Path(sys.argv[1]))
    motion = read_amc(Path(sys.argv[2]))
    frames = [int(x) for x in sys.argv[3].split(",")]
    outdir = Path(sys.argv[4])
    outdir.mkdir(parents=True, exist_ok=True)
    azim = float(sys.argv[5]) if len(sys.argv) > 5 else 0.0

    keys = sorted(motion.keys())
    for fi in frames:
        fk = forward_kinematics(asf, motion[keys[fi]])
        if fk is None:
            print(f"frame {fi}: fk None")
            continue
        pos, _ = fk
        fig = plt.figure(figsize=(7, 9))
        ax = fig.add_subplot(111, projection="3d")
        for jname, pj in pos.items():
            parent = PARENT.get(jname)
            if parent is None or parent == "root":
                continue
            pj2 = pos.get(parent)
            if pj2 is None:
                continue
            # mplot3d 竖轴是 Z，CMU up 是 Y -> (x, z, y) 换轴
            xs = [pj2[0], pj[0]]
            ys = [pj2[2], pj[2]]
            zs = [pj2[1], pj[1]]
            if jname in LEFT:
                c, lw = "#e07030", 2.5
            elif jname in RIGHT:
                c, lw = "#30a0c0", 2.5
            else:
                c, lw = "#404040", 3.5
            ax.plot(xs, ys, zs, c=c, lw=lw)
        for jname, pj in pos.items():
            ax.text(pj[0], pj[2], pj[1], jname[:6], fontsize=5)
        allp = np.array(list(pos.values()))
        mid = allp.mean(axis=0)
        span = max(allp.max(axis=0) - allp.min(axis=0))
        ax.set_xlim(mid[0] - span, mid[0] + span)
        ax.set_ylim(mid[2] - span, mid[2] + span)
        ax.set_zlim(mid[1] - span, mid[1] + span)
        ax.view_init(elev=8, azim=azim)
        ax.set_xlabel("X"); ax.set_ylabel("Z(front)"); ax.set_zlabel("Y(up)")
        out = outdir / f"cmu_f{fi:04d}_az{int(azim)}.png"
        fig.savefig(out, dpi=110, bbox_inches="tight")
        plt.close(fig)
        print("saved", out)


if __name__ == "__main__":
    main()
