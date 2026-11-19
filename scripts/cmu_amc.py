"""CMU Motion Capture ASF/AMC 解析与正向运动学。

为什么要自己写解析器
------------------
Unity 导入不了 ASF/AMC，市面上也没有可靠的 Python 包（PyMO 年久失修、依赖 py3 前的语法）。
而这份数据恰好是**真人在足球场上做出来的动作**——射门、助跑、踢球，正是射手模型缺的东西。

模型要点
--------
1. **偏移方向**：ASF 里 bone 的 ``direction`` + ``length`` 描述的是「从**父关节**出发走到该骨骼
   本体的向量」。所以 ``p[j] = p[parent] + R[parent] @ (dir[parent] * len[parent])``，
   即位置由父骨的方向和自己父的旋转累积决定。用 ``dir[j]*len[j]`` 会多叠一段、腿凭空长 2.4 单位。
2. **旋转累积**：``R[j] = R[parent] @ M_axis[j] @ R_dof[j]``。
   ``M_axis`` 是骨骼局部坐标系相对父系的固定旋转（ASF 的 ``axis`` 字段，如腿骨绕 Z 转 20°），
   AMC 的 DOF 角度是在这个局部坐标系里量的。忽略 ``axis`` 会让屈膝绕着错误的轴转，
   表现就是「膝盖往侧面掰」。
3. **欧拉顺序**：ASF 的 ``axis ... XYZ`` 与 AMC 的 ``rx ry rz`` 都按 ``R = Rx @ Ry @ Rz`` 组合。

坐标系
------
CMU：右手系，X = 角色**左**，Y = 上，Z = 前（由 ``lhumerus direction = (1,0,0)`` 与
``lfoot direction z = +0.95`` 直接读出）。
Unity：左手系，x = 右，y = 上，z = 前。
所以 ``ux = -X, uy = Y, uz = Z``（取反一个轴即可翻转手性）。

单位
----
ASF 的 length 约等于 inch 的数倍，绝对值对重定向无意义（重定向只用骨向量方向），
这里统一乘 ``INCH_TO_M`` 得到米，便于调试时看懂尺寸。
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

# ASF 的 length 单位换算：完整数据里「脚趾 y ≈ 0、头关节 y ≈ 29.7」，
# 对应真人 1.75~1.78m，故 1 单位 ≈ 0.057m。
# 姿态重定向只用方向，这个系数只影响根位移的米制长度。
UNIT_TO_M = 0.057

# CMU 骨架的父子关系（ASF 1.10 文件里没有 parent 字段，只能按标准体例硬编码）
PARENT = {
    "root": None,
    "lowerback": "root",
    "upperback": "lowerback",
    "thorax": "upperback",
    "lowerneck": "thorax",
    "upperneck": "lowerneck",
    "head": "upperneck",
    "lclavicle": "thorax",
    "lhumerus": "lclavicle",
    "lradius": "lhumerus",
    "lwrist": "lradius",
    "lhand": "lwrist",
    "lfingers": "lhand",
    "lthumb": "lhand",
    "rclavicle": "thorax",
    "rhumerus": "rclavicle",
    "rradius": "rhumerus",
    "rwrist": "rradius",
    "rhand": "rwrist",
    "rfingers": "rhand",
    "rthumb": "rhand",
    "lhipjoint": "root",
    "lfemur": "lhipjoint",
    "ltibia": "lfemur",
    "lfoot": "ltibia",
    "ltoes": "lfoot",
    "rhipjoint": "root",
    "rfemur": "rhipjoint",
    "rtibia": "rfemur",
    "rfoot": "rtibia",
    "rtoes": "rfoot",
}

# 从根到叶的求解顺序
ORDER = [
    "root",
    "lowerback", "upperback", "thorax", "lowerneck", "upperneck", "head",
    "lclavicle", "lhumerus", "lradius", "lwrist", "lhand", "lfingers", "lthumb",
    "rclavicle", "rhumerus", "rradius", "rwrist", "rhand", "rfingers", "rthumb",
    "lhipjoint", "lfemur", "ltibia", "lfoot", "ltoes",
    "rhipjoint", "rfemur", "rtibia", "rfoot", "rtoes",
]


def rot_x(deg: float) -> np.ndarray:
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return np.array([[1, 0, 0], [0, c, -s], [0, s, c]], dtype=np.float64)


def rot_y(deg: float) -> np.ndarray:
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]], dtype=np.float64)


def rot_z(deg: float) -> np.ndarray:
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return np.array([[c, -s, 0], [s, c, 0], [0, 0, 1]], dtype=np.float64)


def euler_xyz(ax: float, ay: float, az: float) -> np.ndarray:
    """ASF 约定的 XYZ 欧拉角：先绕 Z，再绕 Y，最后绕 X。"""
    return rot_x(ax) @ rot_y(ay) @ rot_z(az)


@dataclass
class Joint:
    name: str
    ident: int
    direction: np.ndarray
    length: float
    axis: tuple  # (ax, ay, az)
    dof: list = field(default_factory=list)

    @property
    def offset(self) -> np.ndarray:
        """从父关节指向本关节的偏移（父坐标系中）。"""
        return self.direction * self.length

    @property
    def axis_matrix(self) -> np.ndarray:
        return euler_xyz(*self.axis)


@dataclass
class Asf:
    joints: dict
    root_dof: list

    @classmethod
    def parse(cls, path: Path) -> "Asf":
        joints: dict[str, Joint] = {}
        root_dof: list[str] = []
        in_bone = False
        cur: dict = {}

        for raw in path.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = raw.strip()
            if not line or line.startswith("#"):
                continue
            if line.startswith(":root"):
                in_root = True
                continue
            if line.startswith(":bonedata"):
                in_bone = True
                continue
            if line.startswith(":hierarchy"):
                break

            if in_bone:
                if line == "begin":
                    cur = {"dof": []}
                elif line == "end":
                    if "name" in cur:
                        joints[cur["name"]] = Joint(
                            name=cur["name"],
                            ident=int(cur["id"]),
                            direction=np.array(cur["direction"], dtype=np.float64),
                            length=float(cur["length"]),
                            axis=tuple(cur.get("axis", (0.0, 0.0, 0.0))),
                            dof=cur["dof"],
                        )
                    cur = {}
                elif line.startswith("name "):
                    cur["name"] = line.split()[1]
                elif line.startswith("id "):
                    cur["id"] = line.split()[1]
                elif line.startswith("direction "):
                    cur["direction"] = [float(v) for v in line.split()[1:4]]
                elif line.startswith("length "):
                    cur["length"] = line.split()[1]
                elif line.startswith("axis "):
                    p = line.split()
                    cur["axis"] = (float(p[1]), float(p[2]), float(p[3]))
                elif line.startswith("dof "):
                    cur["dof"] = line.split()[1:]
            elif line.startswith("order "):
                # :root 段的 order TX TY TZ RX RY RZ
                root_dof = line.split()[1:]

        return cls(joints=joints, root_dof=root_dof)


def read_amc(path: Path) -> dict[int, dict[str, list[float]]]:
    """读 AMC：返回 {帧号: {关节名: 角度列表}}。末尾截断的帧会被丢弃。"""
    frames: dict[int, dict[str, list[float]]] = {}
    cur_no: int | None = None
    cur: dict[str, list[float]] = {}

    text = path.read_text(encoding="utf-8", errors="ignore")
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith("#") or line.startswith(":"):
            continue
        parts = line.split()
        if parts[0].isdigit():
            if cur_no is not None and cur:
                frames[cur_no] = cur
            cur_no = int(parts[0])
            cur = {}
        elif parts[0] == "root":
            cur["root"] = [float(v) for v in parts[1:]]
        else:
            try:
                cur[parts[0]] = [float(v) for v in parts[1:]]
            except ValueError:
                pass  # 行被截断
    if cur_no is not None and cur:
        frames[cur_no] = cur
    return frames


# 手臂链：这些骨的 AMC 欧拉需要取负（见 forward_kinematics 内注释）
ARM_NEG_TRANSPOSE = {
    "lradius", "rradius",   # 肘（单轴 rx）：取负后肘 1338/1393 帧正常前弯
    "lwrist", "rwrist",     # 腕
}
# 注意：humerus 绝不能取负——取负后三轴全反，双臂上举过头（1452/1602 帧朝上）；
# 不取负时肘弯 2/1393 正常是因为肘弯方向由前臂(radius)决定，humerus 角度本身
# 与官方 avi 渲染一致。


def forward_kinematics(asf: Asf, frame: dict[str, list[float]]):
    """单帧正向运动学：返回 {关节名: (世界旋转 3x3, 世界位置 3)}。"""
    pos: dict[str, np.ndarray] = {}
    rot: dict[str, np.ndarray] = {}

    r = frame.get("root")
    if r is None:
        return None
    # root: TX TY TZ RX RY RZ（order 里先平移后旋转）
    if len(r) < 6:
        return None
    pos["root"] = np.array(r[0:3], dtype=np.float64)
    rot["root"] = euler_xyz(r[3], r[4], r[5])

    for name in ORDER[1:]:
        parent = PARENT[name]
        j = asf.joints.get(name)
        if j is None:
            return None
        # 位置：从父关节沿父骨方向走父骨长度
        if parent == "root":
            # root 不是真正的骨骼，没有 direction/length；
            # lhipjoint / lowerback 直接用自己的偏移挂在 root 上
            pos[name] = pos["root"] + rot["root"] @ j.offset
        else:
            pj = asf.joints[parent]
            pos[name] = pos[parent] + rot[parent] @ pj.offset

        # 旋转：父 × (局部朝向 M · DOF · M⁻¹)
        # 共轭形式是关键：M_axis 只负责「把 DOF 的旋转轴搬到解剖轴上」，
        # 不能让 M_axis 本身改变骨骼朝向。否则腿骨会在 dir 已有的 20° 外张之外
        # 再被 axis 的 20° 转一次，膝盖一路甩到体侧去（踝比髋还外 7 个单位）。
        ang = frame.get(name, [])
        vals = [0.0, 0.0, 0.0]
        # dof 定义是轴名序列（如 "rx rz"），AMC 数值按此顺序给出，
        # 必须按轴名落到对应槽位，不能按序号填（否则 rz 会进 Y 槽）。
        _axis_slot = {"rx": 0, "ry": 1, "rz": 2}
        for i, dof in enumerate(j.dof):
            if i < len(ang):
                vals[_axis_slot[dof]] = ang[i]
        m = j.axis_matrix
        R = euler_xyz(*vals)
        # VICON BodyBuilder 生成的骨架里，手臂骨 axis 含 180° 翻转
        # （humerus/radius: 180 ±30 ∓90；wrist: 0 90 90），AMC 给出的
        # 手臂欧拉角符号约定与腿相反：直接用 R 会让肘 1338/1393 帧朝身后
        # 反折（真人不可能）。对手臂整链取 R.T（= 角度取负）后肘
        # 1346/1393 帧正常前弯，且腿不受影响。此规则对 subject 10/11 均成立。
        if name in ARM_NEG_TRANSPOSE:
            R = R.T
        rot[name] = rot[parent] @ m @ R @ m.T

    return pos, rot


def cmu_to_unity(p: np.ndarray) -> np.ndarray:
    """CMU 右手系（X 左 / Y 上 / Z 前）→ Unity 左手系（x 右 / y 上 / z 前）。"""
    out = np.empty_like(p)
    out[..., 0] = -p[..., 0]
    out[..., 1] = p[..., 1]
    out[..., 2] = p[..., 2]
    return out * UNIT_TO_M


def load_sequence(asf_path: Path, amc_path: Path):
    """返回 (帧号列表, 每帧 {关节: 位置}, 每帧 {关节: 旋转})，坐标为 CMU 原始单位。"""
    asf = Asf.parse(asf_path)
    motion = read_amc(amc_path)
    keys = sorted(motion)
    frames = []
    for k in keys:
        fk = forward_kinematics(asf, motion[k])
        if fk is None:
            continue
        frames.append(fk)
    return keys, frames, asf


if __name__ == "__main__":
    import sys

    root = Path(__file__).resolve().parents[1] / "data" / "external" / "cmu"
    for amc in sorted(root.glob("*.amc")):
        subj = amc.stem.split("_")[0]
        keys, frames, asf = load_sequence(root / f"{subj}.asf", amc)
        if not frames:
            print(f"{amc.name}: 解析失败")
            continue
        pos, _ = frames[0]
        head = pos["head"]
        toe = pos["ltoes"]
        print(f"{amc.name}: {len(frames)} 帧  头高 {head[1]:.1f}  脚趾 y {toe[1]:.1f} "
              f"root {pos['root'][1]:.1f}  时长 {len(frames)/120:.2f}s")
