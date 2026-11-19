"""从 3DSP 导出射手踢球姿态，供 Unity Humanoid 重定向使用。

坐标系转换
----------
3DSP（MotionAGFormer 输出）：右手系，X=后、Y=左、Z=上；水平以髋为原点，Z 为对地高度。
Unity：左手系，x=右、y=上、z=前。
映射： ux = -Y,  uy = Z,  uz = -X
经此映射，原本"面朝 -X"的球员在 Unity 中变为"面朝 +z"，
因此只要让射手 GameObject 的 forward 指向球门即可。

输出
----
Unity JsonUtility 无法解析 Dictionary，故关节名与坐标均扁平化为数组。

用法
----
    python scripts/export_shooter_pose.py --shot 00001
    python scripts/export_shooter_pose.py --shot 00001 00002 --out <dir>
"""
from __future__ import annotations

import argparse
import configparser
import json
import sys
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[1]
DEFAULT_SRC = REPO / "data" / "external" / "3dsp" / "3dsp" / "train"
DEFAULT_OUT = REPO / "Myproject" / "Assets" / "PenaltyKick" / "Resources" / "ShooterPoses"

# 3DSP 关节名（顺序按 index 0..16，与 H3WB 一致）
JOINT_ORDER = [
    "Center of Hips", "Left Hip", "Left Knee", "Left Ankle",
    "Right Hip", "Right Knee", "Right Ankle",
    "Center of Body", "Center of Shoulder", "Neck", "Head",
    "Right Shoulder", "Right Elbow", "Right Wrist",
    "Left Shoulder", "Left Elbow", "Left Wrist",
]

HIPS_JOINT = "Center of Hips"


def to_unity(p: np.ndarray) -> np.ndarray:
    """把 3DSP 坐标转成 Unity 坐标系。

    ⚠️ 必须作用在**最后一维**（坐标轴）上。输入形状是 (帧, 关节, 坐标)，
    若写成 p[:, 0]/p[:, 1]/p[:, 2] 切到的就是「关节维」，
    会把关节顺序整个打乱，重定向出来的姿态必然扭曲。
    """
    out = np.empty_like(p)
    out[..., 0] = -p[..., 1]     # ux = -Y   (3DSP 的 Y 指向左，Unity 的 x 指向右)
    out[..., 1] = p[..., 2]      # uy =  Z   (两者都以 Z / y 为竖直)
    out[..., 2] = -p[..., 0]     # uz = -X   (3DSP 的 X 指向后，Unity 的 z 指向前)
    return out


def load_shot(shot_dir: Path) -> tuple[np.ndarray, list[str]]:
    """返回 (T,17,3) 的 3DSP 原始坐标与关节名列表。"""
    frames = []
    names: list[str] = []
    for jf in sorted((shot_dir / "posture").glob("*.json")):
        data = json.loads(jf.read_text(encoding="utf-8"))
        kp = data["keypoint_3d"]
        if not names:
            names = [kp[str(i)]["name"] for i in range(len(kp))]
        frames.append([[kp[str(i)]["x"], kp[str(i)]["y"], kp[str(i)]["z"]]
                       for i in range(len(kp))])
    return np.asarray(frames, dtype=np.float64), names


def read_kick_offset_ms(shot_dir: Path) -> int:
    """从 info.ini 读取动作标注点相对首帧的偏移（毫秒）。"""
    cp = configparser.ConfigParser()
    cp.read(shot_dir / "info.ini", encoding="utf-8")
    info = cp["info"]
    return int(info["annotated_position"]) - int(info["start_position"])


def estimate_kick_frame(seq: np.ndarray, names: list[str]) -> int:
    """用踝关节帧间位移峰值估计触球帧（0-based）。"""
    idx = {n: i for i, n in enumerate(names)}
    ankles = [idx[n] for n in ("Left Ankle", "Right Ankle") if n in idx]
    best_frame, best_speed = 0, -1.0
    for a in ankles:
        d = np.linalg.norm(np.diff(seq[:, a, :], axis=0), axis=1)
        f = int(np.argmax(d)) + 1
        if d.max() > best_speed:
            best_speed, best_frame = float(d.max()), f
    return best_frame


def smooth(seq: np.ndarray, window: int = 3) -> np.ndarray:
    """轻度时间平滑，抑制提升模型的逐帧抖动（边界保持）。"""
    if window < 3 or len(seq) < window:
        return seq
    half = window // 2
    out = seq.copy()
    for i in range(half, len(seq) - half):
        out[i] = seq[i - half:i + half + 1].mean(axis=0)
    return out


# ---------------------------------------------------------------- 骨架刚性化

# 骨架拓扑（父 -> 子）。列表顺序即重建顺序：父关节必须先算出来。
SKELETON_TREE = [
    ("Center of Hips",     "Center of Body"),
    ("Center of Body",     "Center of Shoulder"),
    ("Center of Shoulder", "Neck"),
    ("Neck",               "Head"),
    ("Center of Shoulder", "Left Shoulder"),
    ("Left Shoulder",      "Left Elbow"),
    ("Left Elbow",         "Left Wrist"),
    ("Center of Shoulder", "Right Shoulder"),
    ("Right Shoulder",     "Right Elbow"),
    ("Right Elbow",        "Right Wrist"),
    ("Center of Hips",     "Left Hip"),
    ("Left Hip",           "Left Knee"),
    ("Left Knee",          "Left Ankle"),
    ("Center of Hips",     "Right Hip"),
    ("Right Hip",          "Right Knee"),
    ("Right Knee",         "Right Ankle"),
]

# 每条骨的方向变化上限（度/帧），作为平滑之后的硬约束。
#
# 取值依据是「实测噪声水平」与「该段在射门里的真实角速度」取较宽的一侧：
#   • 躯干不该超过 ~300°/s（31.7fps ≈ 9°/帧），实测出现 20°/帧，属噪声；
#   • 前臂助跑摆动约 500°/s（≈16°/帧），实测出现 46°/帧，明显是姿态估计末端抖动；
#   • 小腿在触球瞬间本来就快（实测 19°/帧），上限给得宽，不压真实动作。
MAX_DEG_PER_FRAME = {
    ("Center of Hips", "Center of Body"): 10.0,
    ("Center of Hips", "Left Hip"): 20.0,
    ("Center of Hips", "Right Hip"): 20.0,
    ("Center of Body", "Center of Shoulder"): 10.0,
    ("Center of Shoulder", "Neck"): 12.0,
    ("Neck", "Head"): 12.0,
    ("Center of Shoulder", "Left Shoulder"): 12.0,
    ("Center of Shoulder", "Right Shoulder"): 12.0,
    ("Left Shoulder", "Left Elbow"): 18.0,
    ("Right Shoulder", "Right Elbow"): 18.0,
    ("Left Elbow", "Left Wrist"): 24.0,
    ("Right Elbow", "Right Wrist"): 24.0,
    ("Left Hip", "Left Knee"): 20.0,
    ("Right Hip", "Right Knee"): 20.0,
    ("Left Knee", "Left Ankle"): 28.0,
    ("Right Knee", "Right Ankle"): 28.0,
}
DEFAULT_MAX_DEG_PER_FRAME = 20.0


def _smooth_directions(dirs: np.ndarray, weights=(1, 2, 3, 2, 1)) -> np.ndarray:
    """沿时间轴对单位方向做加权平滑，再重新归一化。

    边界用**复制填充**（把首/末帧重复当作窗口外的数据），而不是缩小窗口。
    原先的做法在首末帧只取到一半的采样，等于端点几乎没被平滑 —— 而实测最大
    跳变恰好出现在最后一帧（手腕 46°/帧），正是端点没压住的表现。
    """
    n_frames = len(dirs)
    half = len(weights) // 2
    w = np.asarray(weights, dtype=np.float64)
    w /= w.sum()

    out = dirs.copy()
    for i in range(n_frames):
        acc = np.zeros(3)
        for k in range(-half, half + 1):
            j = min(max(i + k, 0), n_frames - 1)
            acc += dirs[j] * w[k + half]
        n = np.linalg.norm(acc)
        out[i] = acc / n if n > 1e-8 else dirs[i]
    return out


def _rotate_about(axis: np.ndarray, vec: np.ndarray, theta: float) -> np.ndarray:
    """罗德里格斯公式：把 vec 绕单位轴 axis 旋转 theta 弧度。"""
    c, s = np.cos(theta), np.sin(theta)
    return vec * c + np.cross(axis, vec) * s + axis * np.dot(axis, vec) * (1.0 - c)


def _limit_angular_rate(dirs: np.ndarray, max_deg: float) -> np.ndarray:
    """限制相邻帧之间的方向变化角速度。

    平滑只能把噪声"摊薄"，压不住偶发的坏帧；这一步是硬约束：
    每帧相对上一帧的方向变化不超过 max_deg，保证肢体不会突然闪一下。
    上限按骨骼分组给（手臂末端远低于腿部，因为踢球时小腿本来就快）。
    """
    if len(dirs) < 2 or max_deg <= 0:
        return dirs

    out = dirs.copy()
    limit = np.radians(max_deg)
    for i in range(1, len(out)):
        prev, cur = out[i - 1], out[i]
        ang = np.arccos(float(np.clip(np.dot(prev, cur), -1.0, 1.0)))
        if ang <= limit:
            continue

        axis = np.cross(prev, cur)
        norm = np.linalg.norm(axis)
        if norm < 1e-8:
            # 正好反向：任取一个与 prev 垂直的轴
            axis = np.cross(prev, np.array([1.0, 0.0, 0.0]))
            if np.linalg.norm(axis) < 1e-6:
                axis = np.cross(prev, np.array([0.0, 0.0, 1.0]))
            norm = np.linalg.norm(axis)
        if norm < 1e-8:
            continue

        rotated = _rotate_about(axis / norm, prev, limit)
        out[i] = rotated / max(np.linalg.norm(rotated), 1e-8)
    return out


def rigidify(seq: np.ndarray, names: list[str], do_smooth: bool = True) -> np.ndarray:
    """把逐帧骨架锁成刚性结构。

    3D 姿态估计是**逐帧独立回归**的，同一根骨头在不同帧的长短并不一致
    （实测变异系数最高 0.27，即长度抖动近三成）。直接拿这套坐标去做重定向，
    等于让角色的腿一会儿长一会儿短、关节一会儿跳 40cm —— 这是"肢体乱"的主因。

    做法：
      1. 每段骨的方向先沿时间轴加权平滑，压掉逐帧抖动；
      2. 平滑后再做角速度限幅，挡住偶发坏帧造成的"闪一下"；
      3. 骨长锁定为全序列**中位数**（比均值更抗偶发的坏帧）；
      4. 从根关节出发沿骨架树重建，保证每根骨长度恒定、且父子首尾相接。

    只保留方向信息与固定的骨长，模型的实际比例仍由 Avatar 决定，不会互相打架。
    """
    idx = {n: i for i, n in enumerate(names)}
    n_frames = len(seq)
    out = np.zeros_like(seq)

    root = idx[HIPS_JOINT]
    out[:, root] = seq[:, root]

    missing = []
    for parent, child in SKELETON_TREE:
        if parent not in idx or child not in idx:
            missing.append(f"{parent}->{child}")
            continue

        pi, ci = idx[parent], idx[child]
        vec = seq[:, ci] - seq[:, pi]
        length = np.linalg.norm(vec, axis=1)

        valid = length > 1e-6
        if not valid.any():
            out[:, ci] = out[:, pi]
            continue

        dirs = np.zeros_like(vec)
        dirs[valid] = vec[valid] / length[valid, None]

        # 零长度帧用时间上最近的有效帧方向顶替，避免平滑后被抹成零向量
        if not valid.all():
            good = np.where(valid)[0]
            for t in range(n_frames):
                if not valid[t]:
                    dirs[t] = dirs[good[np.argmin(np.abs(good - t))]]

        if do_smooth:
            dirs = _smooth_directions(dirs)
        dirs = _limit_angular_rate(dirs, MAX_DEG_PER_FRAME.get(
            (parent, child), DEFAULT_MAX_DEG_PER_FRAME))

        bone_length = float(np.median(length[valid]))
        out[:, ci] = out[:, pi] + dirs * bone_length

    if missing:
        print(f"[警告] 骨架拓扑中有 {len(missing)} 条边找不到关节: {missing}",
              file=sys.stderr)

    return out


def align_facing(seq: np.ndarray, names: list[str]) -> np.ndarray:
    """绕竖轴旋转整段姿态，使**首帧**的髋轴（左髋 → 右髋）对齐到 +x。

    为什么必须做这一步
    ------------------
    3DSP 的姿态是摄像机视角下的绝对坐标：同一个球员，在这段里朝画面左前方，
    换个机位就变成朝右后方。而 Unity 里的射手模型是固定「+z 为正前」的。
    不做归一化，角色就会歪着身子踢球——从正面看过去，本该前后摆动的手臂会
    呈现「向两侧张开」的样子，这正是"肢体看起来乱"的主要来源。

    只归一化首帧、不逐帧归一化：播放过程中球员本来会转身助跑，逐帧抵消 yaw
    会把这段转身也一起抹掉，动作反而失真。
    """
    idx = {n: i for i, n in enumerate(names)}
    if "Left Hip" not in idx or "Right Hip" not in idx:
        return seq

    right = seq[0, idx["Right Hip"]] - seq[0, idx["Left Hip"]]
    if np.linalg.norm(right[[0, 2]]) < 1e-6:
        return seq

    theta = float(np.arctan2(right[2], right[0]))     # 右向轴在水平面内的偏角
    c, s = np.cos(theta), np.sin(theta)

    out = seq.copy()
    x = seq[..., 0].copy()
    z = seq[..., 2].copy()
    out[..., 0] = x * c + z * s                       # 与 Unity 绕 Y 轴旋转一致
    out[..., 2] = -x * s + z * c
    return out


def export(shot_dir: Path, out_dir: Path, do_smooth: bool) -> dict:
    seq_raw, names = load_shot(shot_dir)
    n_frames = len(seq_raw)

    seq = smooth(seq_raw, 3) if do_smooth else seq_raw
    seq = to_unity(seq)                       # -> Unity 坐标系

    # 转为相对髋的位置（重定向只用方向，故绝对高度无意义，且可消除尺度差）
    hips_i = names.index(HIPS_JOINT)
    seq = seq - seq[:, hips_i:hips_i + 1, :]

    # 朝向归一化：让首帧的正面朝 +z，角色才不会歪着身子踢球
    seq = align_facing(seq, names)

    # 锁骨长 + 平滑方向：不做这一步，逐帧独立回归出来的骨骼长短不一会直接变成
    # 「腿一会儿长一会儿短」的观感，仅靠 3 帧均值压不住（实测单帧跳变可达 0.4m）
    seq = rigidify(seq, names, do_smooth=do_smooth)

    # 触球帧：info.ini 的人工标注偏移优先，运动学峰值作为参考
    cp = configparser.ConfigParser()
    cp.read(shot_dir / "info.ini", encoding="utf-8")
    span_ms = int(cp["info"]["end_position"]) - int(cp["info"]["start_position"])
    step_ms = span_ms / max(n_frames - 1, 1)
    kick_from_meta = int(round(read_kick_offset_ms(shot_dir) / step_ms))
    kick_from_kinematics = estimate_kick_frame(seq_raw, names)

    kick_frame = int(np.clip(kick_from_meta, 0, n_frames - 1))
    fps = 1000.0 / step_ms if step_ms > 0 else 30.0

    clip = {
        "shotId": shot_dir.name,
        "fps": round(fps, 4),
        "kickFrame": kick_frame,
        "kickFrameKinematic": kick_from_kinematics,
        "jointNames": names,
        "frames": [
            {
                "t": round(i / fps, 5),
                "p": [round(float(v), 6) for v in seq[i].reshape(-1)],
            }
            for i in range(n_frames)
        ],
    }

    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"{shot_dir.name}.json"
    out_path.write_text(json.dumps(clip, ensure_ascii=False, indent=1), encoding="utf-8")

    return {
        "shot": shot_dir.name,
        "frames": n_frames,
        "fps": round(fps, 2),
        "kick_frame_meta": kick_from_meta,
        "kick_frame_kinematic": kick_from_kinematics,
        "out": str(out_path),
        "size_kb": round(out_path.stat().st_size / 1024, 1),
    }


def main() -> int:
    ap = argparse.ArgumentParser(description="导出 3DSP 射手姿态给 Unity")
    ap.add_argument("--shot", nargs="+", default=["00001"], help="shot 目录名，如 00001")
    ap.add_argument("--src", type=Path, default=DEFAULT_SRC, help="3DSP train 目录")
    ap.add_argument("--out", type=Path, default=DEFAULT_OUT, help="输出目录")
    ap.add_argument("--no-smooth", action="store_true", help="关闭时间平滑")
    args = ap.parse_args()

    if not args.src.exists():
        print(f"[错误] 找不到 3DSP 数据目录: {args.src}", file=sys.stderr)
        return 2

    results = []
    for name in args.shot:
        d = args.src / name
        if not (d / "posture").exists():
            print(f"[跳过] {name}: 无 posture 目录", file=sys.stderr)
            continue
        results.append(export(d, args.out, do_smooth=not args.no_smooth))

    print(json.dumps(results, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
