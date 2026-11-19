# -*- coding: utf-8 -*-
"""解析 CMU 官方 BVH 转换版，做肘/膝/肩解剖学判定，与自研 FK 交叉验证。

BVH 标准：欧拉按 CHANNELS 声明顺序内旋（绕移动轴），Y-up，单位任意（scale 无关判定）。
"""
import os
import sys
from pathlib import Path

import numpy as np


def rot(axis, deg):
    a = np.radians(deg)
    c, s = np.cos(a), np.sin(a)
    if axis == "X":
        return np.array([[1,0,0],[0,c,-s],[0,s,c]])
    if axis == "Y":
        return np.array([[c,0,s],[0,1,0],[-s,0,c]])
    return np.array([[c,-s,0],[s,c,0],[0,0,1]])


def parse_bvh(path):
    lines = Path(path).read_text(errors="ignore").splitlines()
    joints = {}          # name -> dict(parent, offsets, channels)
    stack = []
    root = None
    chan_ofs = 0         # 该关节 channel 在整帧行中的起始偏移（DFS 顺序）
    i = 0
    while i < len(lines):
        ln = lines[i].strip()
        if ln.startswith("ROOT") or ln.startswith("JOINT"):
            name = ln.split()[1]
            parent = stack[-1] if stack else None
            joints[name] = {"parent": parent, "offset": None, "channels": None}
            if root is None:
                root = name
            stack.append(name)
        elif ln.startswith("End Site"):
            stack.append("__end__")
        elif ln.startswith("OFFSET"):
            vals = [float(x) for x in ln.split()[1:4]]
            if stack[-1] != "__end__":
                joints[stack[-1]]["offset"] = np.array(vals)
        elif ln.startswith("CHANNELS"):
            n = int(ln.split()[1])
            chans = ln.split()[2:2+n]
            if stack[-1] != "__end__":
                joints[stack[-1]]["channels"] = chans
                joints[stack[-1]]["chan_ofs"] = chan_ofs
                chan_ofs += n
        elif ln == "}":
            if stack:
                stack.pop()
        elif ln.startswith("MOTION"):
            break
        i += 1
    while i < len(lines) and not lines[i].strip().startswith("Frames:"):
        i += 1
    nframes = int(lines[i].split(":")[1].strip())
    data = []
    for ln in lines[i+3:]:
        parts = ln.split()
        if len(parts) < 3:
            continue
        data.append([float(x) for x in parts])
        if len(data) >= nframes:
            break
    return joints, data


def fk_bvh(joints, row):
    """返回 {joint: world_pos}。逐关节累积 offset 与欧拉。"""
    pos = {}
    rot_acc = {}
    # 按 parent 链计算（递归）
    def compute(name):
        jd = joints[name]
        base = jd.get("chan_ofs", 0)
        if jd["parent"] is None:
            # ROOT: 前几个 channel 是平移
            chans = jd["channels"]
            t = np.zeros(3)
            R = np.eye(3)
            for ci, c in enumerate(chans):
                v = row[base + ci]
                if c == "Xposition": t[0] = v
                elif c == "Yposition": t[1] = v
                elif c == "Zposition": t[2] = v
                elif c == "Xrotation": R = R @ rot("X", v)
                elif c == "Yrotation": R = R @ rot("Y", v)
                elif c == "Zrotation": R = R @ rot("Z", v)
            pos[name] = t
            rot_acc[name] = R
            return
        compute(jd["parent"])
        chans = jd["channels"]
        R = rot_acc[jd["parent"]]
        R2 = R.copy()
        for ci, c in enumerate(chans):
            v = row[base + ci]
            if c == "Xrotation": R2 = R2 @ rot("X", v)
            elif c == "Yrotation": R2 = R2 @ rot("Y", v)
            elif c == "Zrotation": R2 = R2 @ rot("Z", v)
        pos[name] = pos[jd["parent"]] + R @ jd["offset"]
        rot_acc[name] = R2
    for name in joints:
        compute(name)
    return pos


def scan(joints, data, label):
    kb = kt = eb = et = up = tot = 0
    for row in data:
        p = fk_bvh(joints, row)
        try:
            S = p["LeftArm"] - p["RightArm"]
        except KeyError:
            S = p["lShldrBend"] - p["rShldrBend"]
        F = np.cross(S, [0.0,1.0,0.0]); F /= np.linalg.norm(F)
        F = -F if os.environ.get("FLIP_F") else F
        pairs = None
        # Motionbuilder-friendly 命名
        cand = [
            ("LeftArm","LeftForeArm","LeftHand","LeftUpLeg","LeftLeg","LeftFoot"),
            ("lShldrBend","lForeArmBend","lHand","lThighBend","lShin","lFoot"),
        ]
        for c in cand:
            if all(x in p for x in c):
                pairs = c
                break
        if pairs is None:
            print("未识别骨架命名:", sorted(p.keys())[:20]); return
        la, lf, lh, lu, ll, lft = pairs
        for s_ in ((0,1) if pairs[0][0]=="L" else (0,1)):
            armA, armB, hand = (pairs[0], pairs[1], pairs[2])
            legA, legB, foot = (pairs[3], pairs[4], pairs[5])
            if s_ == 1 and pairs[0][0] == "L":
                armA, armB, hand = ("RightArm","RightForeArm","RightHand")
                legA, legB, foot = ("RightUpLeg","RightLeg","RightFoot")
            elif s_ == 1:
                armA, armB, hand = ("rShldrBend","rForeArmBend","rHand")
                legA, legB, foot = ("rThighBend","rShin","rFoot")
            hum = p[armB] - p[armA]
            rad = p[hand] - p[armB]
            nh = np.linalg.norm(hum)
            if nh < 1e-9: continue
            humn = hum/nh
            tot += 1
            if humn[1] > 0.3: up += 1
            d = rad - np.dot(rad, humn)*humn
            nd = np.linalg.norm(d)
            if nd < 1e-9: continue
            d /= nd
            ang = np.degrees(np.arccos(np.clip(np.dot(rad/np.linalg.norm(rad), humn), -1, 1)))
            if ang > 25:
                et += 1
                if float(np.dot(d, F)) > 0.2: eb += 1
            # 膝
            th = p[legB] - p[legA]
            shin = p[foot] - p[legB]
            nth = np.linalg.norm(th)
            if nth < 1e-9: continue
            thn = th/nth
            dk = shin - np.dot(shin, thn)*thn
            nk = np.linalg.norm(dk)
            if nk < 1e-9: continue
            dk /= nk
            angk = np.degrees(np.arccos(np.clip(np.dot(shin/np.linalg.norm(shin), thn), -1, 1)))
            if angk > 15:
                kt += 1
                if float(np.dot(dk, -F)) > 0.2: kb += 1
    print(f"{label}: 肘正常 {eb}/{et} ({100*eb/max(1,et):.0f}%)  膝正常 {kb}/{kt} ({100*kb/max(1,kt):.0f}%)  上臂上举 {up}/{tot}")


def main():
    for path, label in ((Path(sys.argv[1]), sys.argv[2]),):
        joints, data = parse_bvh(path)
        print(f"{path.name}: {len(data)} 帧, 关节 {len(joints)}")
        scan(joints, data, label)


if __name__ == "__main__":
    main()
