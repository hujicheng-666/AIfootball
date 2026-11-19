"""身体坐标系归一化与运动学特征。

坐标系（重力对齐 + 髋轴对齐，右手系）：
    ẑ = 世界 +z（3DSP 已保证 z 竖直：脚在 0、头在 1）
    x̂ = 左髋 → 右髋 在水平面内的投影（球员右向）
    ŷ = ẑ × x̂（球员前向）
    原点 = 髋中心，尺度 = |肩中心 − 髋中心|（躯干长）

为什么 up 用世界竖直、而不是躯干轴：
    第一版用躯干轴做 up，结果"肩中心在 x̂ 上的坐标"恒为 0——躯干轴本身
    定义了一个基向量，躯干怎么倾斜都会被坐标系吸收，前倾/侧倾信息在特征里
    彻底消失。改成世界竖直后，躯干相对髋轴的倾斜（重心预判方向）才是可观测
    的。同理，髋轴的全局朝向被坐标系吸收，所以另用 `hip_yaw` 相对首帧的变化
    把"髋部旋转"补回特征——这是射门发力的核心变量，不能丢。

3DSP 的坐标已被逐帧缩放成"身高 = 1"（脚 0、头 1，髋中心 x/y = 0），
所以本模块输出的单位是"躯干长"，与身高成正比，跨 shot 可比。
"""
from __future__ import annotations

import numpy as np

from .dataset import (
    HIPS,
    LEFT_ANKLE,
    LEFT_HIP,
    LEFT_KNEE,
    RIGHT_ANKLE,
    RIGHT_HIP,
    RIGHT_KNEE,
    SHOULDER,
)

#: 数值保护
_EPS = 1e-6

_UP = np.array([0.0, 0.0, 1.0])


def body_axes(frames: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """返回 (三轴 [T,3,3]（右/前/上）, 尺度 [T])。

    逐帧计算、不跨帧平滑：平滑会把触球前的快速转髋抹掉，而方向信息恰好
    集中在最后几帧。
    """
    frames = np.asarray(frames, dtype=np.float64)
    if frames.ndim != 3 or frames.shape[1] != 17 or frames.shape[2] != 3:
        raise ValueError(f"期望 [T,17,3]，收到 {frames.shape}")

    count = frames.shape[0]
    hips = frames[:, HIPS]
    shoulder = frames[:, SHOULDER]
    torso = shoulder - hips
    scale = np.linalg.norm(torso, axis=1)

    # 髋轴：只取水平分量（蹲姿时左右髋可能不等高）
    hip_axis = frames[:, RIGHT_HIP] - frames[:, LEFT_HIP]
    horizontal = hip_axis[:, :2]
    length = np.linalg.norm(horizontal, axis=1)
    right = np.zeros((count, 3), dtype=np.float64)
    valid = length > _EPS
    right[valid, 0] = horizontal[valid, 0] / length[valid]
    right[valid, 1] = horizontal[valid, 1] / length[valid]
    if np.any(~valid):
        # 退化帧（左右髋重合，检测失败）：沿用上一个有效帧，没有就退回世界 x
        last = np.array([1.0, 0.0, 0.0])
        for index in range(count):
            if valid[index]:
                last = right[index]
            else:
                right[index] = last

    forward = np.cross(np.broadcast_to(_UP, right.shape), right)
    forward_length = np.linalg.norm(forward, axis=1)
    forward /= np.maximum(forward_length, _EPS)[:, None]

    up = np.broadcast_to(_UP, right.shape).copy()
    axes = np.stack([right, forward, up], axis=1)   # [T,3,3]
    return axes, scale.astype(np.float32)


def normalize_sequence(frames: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    """世界坐标 → 身体坐标 [T,17,3]（单位：躯干长）。"""
    frames = np.asarray(frames, dtype=np.float64)
    axes, scale = body_axes(frames)
    hips = frames[:, HIPS]
    relative = frames - hips[:, None, :]
    coords = np.einsum("tjc,tkc->tjk", relative, axes)
    coords = coords / np.maximum(scale, _EPS)[:, None, None]
    return coords.astype(np.float32), scale


def _angle(a: np.ndarray, b: np.ndarray, c: np.ndarray) -> np.ndarray:
    """b 为顶点、a/c 为两端的夹角（弧度）。"""
    ba = a - b
    bc = c - b
    cos = np.sum(ba * bc, axis=1) / (
        np.maximum(np.linalg.norm(ba, axis=1), _EPS)
        * np.maximum(np.linalg.norm(bc, axis=1), _EPS)
    )
    return np.arccos(np.clip(cos, -1.0, 1.0))


def hip_yaw_delta(frames: np.ndarray) -> np.ndarray:
    """髋轴方位角相对首帧的变化，返回 (sin, cos−1) 两通道 [T,2]。

    用相对量而不是绝对值：3DSP 每个 shot 的拍摄朝向不同，绝对值会把
    "相机在哪"当成特征；相对量只保留"髋部转了多少"。
    """
    frames = np.asarray(frames, dtype=np.float64)
    hip_axis = frames[:, RIGHT_HIP] - frames[:, LEFT_HIP]
    yaw = np.arctan2(hip_axis[:, 1], hip_axis[:, 0])
    delta = yaw - yaw[0]
    return np.stack([np.sin(delta), np.cos(delta) - 1.0], axis=1)


def trunk_tilt(frames: np.ndarray) -> np.ndarray:
    """躯干轴相对世界竖直的倾角 [T,2]：(侧倾, 前倾)，弧度。"""
    frames = np.asarray(frames, dtype=np.float64)
    torso = frames[:, SHOULDER] - frames[:, HIPS]
    torso /= np.maximum(np.linalg.norm(torso, axis=1), _EPS)[:, None]
    return np.stack(
        [
            np.arcsin(np.clip(torso[:, 0], -1.0, 1.0)),
            np.arcsin(np.clip(torso[:, 1], -1.0, 1.0)),
        ],
        axis=1,
    )


def kinematic_features(frames: np.ndarray, coords: np.ndarray) -> np.ndarray:
    """派生运动学特征 [T, 9]。

    选这些的理由——它们都是"射门方向"的生物力学代理，且不依赖相机朝向：
        1/2. 左右膝屈曲角：支撑腿绷直 vs 踢球腿折叠；
        3/4. 左右髋屈曲角：摆腿幅度；
        5.   肩中心横向坐标：躯干侧倾（重心预判方向）；
        6.   两踝横向间距：支撑脚落点相对髋轴；
        7/8. 髋轴方位角变化 sin/cos：转髋幅度与方向；
        9.   躯干侧倾角：与 5 互补（5 带尺度，9 纯角度）。
    """
    frames = np.asarray(frames, dtype=np.float64)
    coords = np.asarray(coords, dtype=np.float64)

    left_knee = _angle(frames[:, LEFT_HIP], frames[:, LEFT_KNEE], frames[:, LEFT_ANKLE])
    right_knee = _angle(frames[:, RIGHT_HIP], frames[:, RIGHT_KNEE], frames[:, RIGHT_ANKLE])
    left_hip = _angle(frames[:, SHOULDER], frames[:, LEFT_HIP], frames[:, LEFT_KNEE])
    right_hip = _angle(frames[:, SHOULDER], frames[:, RIGHT_HIP], frames[:, RIGHT_KNEE])

    shoulder_lateral = coords[:, SHOULDER, 0]
    ankle_separation = coords[:, RIGHT_ANKLE, 0] - coords[:, LEFT_ANKLE, 0]
    yaw = hip_yaw_delta(frames)
    tilt = trunk_tilt(frames)

    return np.concatenate(
        [
            np.stack([left_knee, right_knee, left_hip, right_hip], axis=1),
            shoulder_lateral[:, None],
            ankle_separation[:, None],
            yaw,
            tilt[:, :1],
        ],
        axis=1,
    ).astype(np.float32)


#: 每帧特征维度：51（归一化坐标）+ 51（速度）+ 9（运动学）+ 1（Δt）
FEATURE_DIM = 51 + 51 + 9 + 1

#: 默认回看窗口（帧）
WINDOW = 16


def window_slice(kick_frame: int, delta_frames: int, length: int) -> tuple[int, int]:
    """返回"触球前 `delta_frames` 帧回看 `length` 帧"的切片 [start, stop)。"""
    stop = int(kick_frame) + 1 - int(delta_frames)
    stop = max(1, min(stop, 10 ** 9))
    return stop - int(length), stop


def extract_window(
    frames: np.ndarray,
    kick_frame: int,
    delta_ms: float,
    frame_step_ms: float,
    length: int = WINDOW,
) -> np.ndarray:
    """按提前量截取时间窗，返回 [length, FEATURE_DIM]。

    最后一维是 Δt/400ms —— 这是"条件化"：模型能知道自己在多早的时点做判断，
    从而在信息更少时输出更保守的分布，而不是靠训练多个模型。
    """
    frames = np.asarray(frames, dtype=np.float32)
    total = frames.shape[0]
    delta_frames = int(round(float(delta_ms) / max(float(frame_step_ms), _EPS)))

    start, stop = window_slice(kick_frame, delta_frames, length)
    stop = min(stop, total)
    start = stop - length
    if start >= 0:
        window = frames[start:stop]
    else:
        pad = np.repeat(frames[:1], -start, axis=0)
        window = np.concatenate([pad, frames[0:stop]], axis=0)
    if window.shape[0] < length:
        pad = np.repeat(window[-1:], length - window.shape[0], axis=0)
        window = np.concatenate([window, pad], axis=0)
    elif window.shape[0] > length:
        window = window[-length:]

    coords, _ = normalize_sequence(window)
    features = kinematic_features(window, coords)
    velocity = np.diff(coords, axis=0, prepend=coords[:1])
    flat = np.concatenate(
        [coords.reshape(length, -1), velocity.reshape(length, -1), features],
        axis=1,
    )
    delta_channel = np.full((length, 1), float(delta_ms) / 400.0, dtype=np.float32)
    return np.concatenate([flat, delta_channel], axis=1).astype(np.float32)
