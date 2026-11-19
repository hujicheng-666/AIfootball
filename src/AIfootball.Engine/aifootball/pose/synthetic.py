"""以 3DSP 真实骨骼为运动源的合成方向数据。

为什么需要合成：
    3DSP 只有"射正/射偏"标签，没有方向标签；而 C2 要验证的第一件事是
    "现有视角的姿态序列是否足以预测射门方向"。没有方向标签就无法直接监督，
    所以用真实运动结构 + 受控变形造出带方向标签的样本，先回答**可行性**问题
    （上界），再去真实数据上回答**可迁移性**问题（组 2）。

方向信息的物理假设（这是本模块最关键的建模决定，必须在报告里写明）：
    射门方向由"踢球腿摆动平面相对髋轴的偏航"决定，支撑脚落点与躯干侧倾
    是它的协同后果。因此三个线索**同号**注入：

        direction = +1（射向球员右侧）
            踢球腿绕同侧髋关节在水平面内向右偏 10°±3°
            支撑脚（另一条腿）横向向右偏 10±2.5 cm
            肩中心横向向右偏 4±1.5 cm

    之所以不做"整个骨架绕竖直轴旋转"，是因为特征在身体坐标系里计算，全局
    旋转会被归一化消掉——那样造出来的标签根本无法被任何模型学到，实验会
    假阴性。同理也不做"只改一个关节"：design 文档明确警告那会训练出捷径。

变形是在**身体坐标系**里施加、再用原始正交基转回世界坐标的。左右髋与肩
中心构成的髋轴/躯干轴保持不变，所以下游重新归一化得到的坐标系与变形时
一致，注入的偏移会原样出现在特征里。
"""
from __future__ import annotations

from dataclasses import dataclass, replace
from typing import Iterable, Optional, Sequence

import numpy as np

from .body_frame import body_axes
from .dataset import (
    HIPS,
    LEFT_ANKLE,
    LEFT_HIP,
    LEFT_KNEE,
    LEFT_SHOULDER,
    RIGHT_ANKLE,
    RIGHT_HIP,
    RIGHT_KNEE,
    RIGHT_SHOULDER,
    SHOULDER,
    PoseShot,
)

#: 3DSP 坐标的隐含单位是"身高 = 1"，变形幅度用米给定时需要这个换算系数
ASSUMED_HEIGHT_M = 1.75

#: 方向标签 → 类别索引（0 = 左，1 = 中，2 = 右）
DIRECTION_TO_CLASS = {-1: 0, 0: 1, 1: 2}
CLASS_TO_DIRECTION = {value: key for key, value in DIRECTION_TO_CLASS.items()}
CLASS_NAMES = ("左", "中", "右")

_SHOULDER_LINE = (LEFT_SHOULDER, RIGHT_SHOULDER)
_TORSO_UPPER = (SHOULDER, * _SHOULDER_LINE)


@dataclass(frozen=True)
class DeformConfig:
    """方向变形的幅度（米 / 度）。默认值来自成年男性点球的量级估计。"""

    yaw_deg: float = 10.0            # 摆动平面偏航角
    yaw_jitter_deg: float = 3.0      # 每个样本的随机化
    support_shift_m: float = 0.10    # 支撑脚横向偏移
    support_jitter_m: float = 0.025
    torso_lean_m: float = 0.04       # 肩中心横向偏移
    torso_jitter_m: float = 0.015
    neutral_yaw_jitter_deg: float = 4.0   # 中间类：只留噪声
    neutral_support_jitter_m: float = 0.03
    variants_per_source: int = 40


@dataclass(frozen=True)
class RandomizationConfig:
    """域随机化。组 1b 把它整体关掉，用来检验模型是否依赖真实误差结构。"""

    enabled: bool = True
    joint_noise_m: float = 0.015
    joint_dropout: float = 0.02
    leg_swap: float = 0.05
    time_jitter: float = 0.03
    body_scale_min: float = 0.90
    body_scale_max: float = 1.10


@dataclass
class SyntheticSample:
    """一个样本：骨架序列 + 标签 + 溯源信息。

    `binary_label` 用于组 2 的迁移实验（真实 3DSP 只有射正/射偏，没有方向），
    给了它就覆盖方向标签——同一个数据结构、同一套评估管线，避免为二分类再抄
    一份特征提取代码。
    """

    sample_id: str
    frames: np.ndarray           # [T,17,3] float32，世界坐标
    direction: int               # -1 / 0 / +1（组 2 无意义，填 0）
    source_shot_id: str
    kick_frame: int
    frame_step_ms: float
    binary_label: Optional[int] = None

    @property
    def label(self) -> int:
        if self.binary_label is not None:
            return int(self.binary_label)
        return DIRECTION_TO_CLASS[self.direction]


def real_samples(shots: Iterable[PoseShot]) -> list[SyntheticSample]:
    """把真实 3DSP shot 包成样本（标签 = 射正/射偏），供组 2 使用。

    不做任何变形：这一组的全部意义就是"合成域学到的东西能不能迁移到真实姿态"。
    """
    samples: list[SyntheticSample] = []
    for shot in shots:
        stop = min(shot.kick_frame + 1, shot.frames.shape[0])
        samples.append(
            SyntheticSample(
                sample_id=shot.shot_id,
                frames=shot.frames[:stop].copy(),
                direction=0,
                source_shot_id=shot.shot_id,
                kick_frame=stop - 1,
                frame_step_ms=shot.frame_step_ms,
                binary_label=int(shot.on_target),
            )
        )
    return samples


def augment_samples(
    samples: Sequence[SyntheticSample],
    copies: int,
    seed: int = 20261019,
    randomization: RandomizationConfig = RandomizationConfig(),
) -> list[SyntheticSample]:
    """对真实样本做域随机化复制（组 2 只有 200 个 shot，必须靠增强撑住训练）。"""
    rng = np.random.default_rng(seed)
    augmented: list[SyntheticSample] = []
    for sample in samples:
        augmented.append(sample)          # 保留原样，避免增强把真实分布整体挪走
        for copy_index in range(max(0, copies - 1)):
            augmented.append(
                replace(
                    sample,
                    sample_id=f"{sample.sample_id}-aug{copy_index:02d}",
                    frames=randomize(sample.frames, rng, randomization),
                )
            )
    return augmented


def kicking_leg(frames: np.ndarray, kick_frame: int) -> str:
    """判断踢球腿：触球前踝关节累计位移更大的那条腿。"""
    limit = min(kick_frame + 1, frames.shape[0])
    if limit < 2:
        return "Right"
    segment = frames[:limit]
    deltas = np.linalg.norm(np.diff(segment, axis=0), axis=2)
    left = float(np.sum(deltas[:, LEFT_ANKLE]))
    right = float(np.sum(deltas[:, RIGHT_ANKLE]))
    return "Left" if left > right else "Right"


def _split_body(frames: np.ndarray) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """世界坐标 → (身体坐标, 正交基, 尺度, 髋中心)。

    coords[t,n,j] = Σ_k (p[t,n,k] − hips[t,k]) · axes[t,j,k] —— 与
    `body_frame.normalize_sequence` 必须逐位一致，否则变形会落在错误的坐标系里。
    早期版本把基矩阵写成了转置，恰好与 `_merge_body` 的同类错误互相抵消，
    往返仍然精确、但"横向"根本不是球员右向，方向标签全部失去语义。
    单元测试 `split_matches_normalize_sequence` 守住这条一致性。
    """
    frames = np.asarray(frames, dtype=np.float64)
    axes, scale = body_axes(frames)
    hips = frames[:, HIPS]
    relative = frames - hips[:, None, :]
    coords = np.einsum("tnk,tjk->tnj", relative, axes)
    coords = coords / np.maximum(scale, 1e-6)[:, None, None]
    return coords, axes, scale, hips


def _merge_body(
    coords: np.ndarray, axes: np.ndarray, scale: np.ndarray, hips: np.ndarray
) -> np.ndarray:
    """身体坐标 → 世界坐标。

    `coords[t, n, j]` 是关节 n 在第 j 个基上的分量，`axes[t, j, k]` 是第 j 个基的
    世界分量 k，因此 world[t,n,k] = Σ_j coords[t,n,j]·axes[t,j,k]（收缩的是
    **基索引** j）。早期版本写成 `tjc,tkc->tjk`，等价于用了基矩阵的转置——在接近
    单位阵的合成数据上误差只有几毫米，但真实 3DSP 的髋轴朝向任意，整段骨架会错位。
    """
    scaled = coords * np.maximum(scale, 1e-6)[:, None, None]
    relative = np.einsum("tnj,tjk->tnk", scaled, axes)
    return (hips[:, None, :] + relative).astype(np.float32)


def _rotate_about_up(coords: np.ndarray, joint: int, pivot: int, angle_rad: float) -> None:
    """把 `joint` 绕 `pivot` 在水平面（身体坐标 x-y）内旋转，就地修改。"""
    cos, sin = float(np.cos(angle_rad)), float(np.sin(angle_rad))
    offset = coords[:, joint] - coords[:, pivot]
    x = offset[:, 0] * cos - offset[:, 1] * sin
    y = offset[:, 0] * sin + offset[:, 1] * cos
    offset[:, 0] = x
    offset[:, 1] = y
    coords[:, joint] = coords[:, pivot] + offset


def _translate(coords: np.ndarray, joints: Sequence[int], lateral_m: np.ndarray,
               scale: np.ndarray) -> None:
    """沿身体右向平移若干关节（`lateral_m` 为逐帧米数）。

    米 → 躯干长的换算：米 / 假定身高，再除以该帧躯干长（单位：身高）。
    """
    shift = (lateral_m / ASSUMED_HEIGHT_M) / np.maximum(scale, 1e-6)
    for joint in joints:
        coords[:, joint, 0] += shift


def deform_for_direction(
    frames: np.ndarray,
    kick_frame: int,
    direction: int,
    rng: np.random.Generator,
    config: DeformConfig,
) -> np.ndarray:
    """把一个真实骨架序列变形成指定方向，返回新的世界坐标序列。

    只变形到触球帧为止的片段（训练/评估都只看触球前的信息），保留其余帧
    以维持序列长度与触球帧索引不变。
    """
    frames = np.asarray(frames, dtype=np.float32)
    coords, axes, scale, hips = _split_body(frames)

    leg = kicking_leg(frames, kick_frame)
    if leg == "Right":
        swing_hip, swing_knee, swing_ankle = RIGHT_HIP, RIGHT_KNEE, RIGHT_ANKLE
        support = (LEFT_HIP, LEFT_KNEE, LEFT_ANKLE)
    else:
        swing_hip, swing_knee, swing_ankle = LEFT_HIP, LEFT_KNEE, LEFT_ANKLE
        support = (RIGHT_HIP, RIGHT_KNEE, RIGHT_ANKLE)

    sign = float(direction)
    if direction == 0:
        yaw_deg = rng.normal(0.0, config.neutral_yaw_jitter_deg)
        support_m = rng.normal(0.0, config.neutral_support_jitter_m)
        torso_m = rng.normal(0.0, config.torso_jitter_m)
    else:
        yaw_deg = sign * (config.yaw_deg + rng.normal(0.0, config.yaw_jitter_deg))
        support_m = sign * (config.support_shift_m + rng.normal(0.0, config.support_jitter_m))
        torso_m = sign * (config.torso_lean_m + rng.normal(0.0, config.torso_jitter_m))

    # 1) 摆动平面：整条踢球腿绕同侧髋关节偏航
    angle = np.deg2rad(yaw_deg)
    for joint in (swing_knee, swing_ankle):
        _rotate_about_up(coords, joint, swing_hip, angle)

    # 2) 支撑脚落点：支撑腿的膝/踝横向平移（髋不动，避免改变身体坐标系）
    _translate(coords, support[1:], np.full(coords.shape[0], support_m), scale)

    # 3) 躯干侧倾：上半身沿横向偏移
    _translate(coords, _TORSO_UPPER, np.full(coords.shape[0], torso_m), scale)

    return _merge_body(coords, axes, scale, hips)


def _swap_legs(frames: np.ndarray) -> np.ndarray:
    """左右腿关节互换（模拟检测器左右混淆）。"""
    swapped = frames.copy()
    pairs = (
        (LEFT_HIP, RIGHT_HIP),
        (LEFT_KNEE, RIGHT_KNEE),
        (LEFT_ANKLE, RIGHT_ANKLE),
        (LEFT_SHOULDER, RIGHT_SHOULDER),
    )
    for left, right in pairs:
        swapped[:, left], swapped[:, right] = frames[:, right].copy(), frames[:, left].copy()
    return swapped


def randomize(
    frames: np.ndarray,
    rng: np.random.Generator,
    config: RandomizationConfig,
) -> np.ndarray:
    """施加域随机化：关节噪声、丢关节、左右腿互换、时间抖动、体型缩放。"""
    if not config.enabled:
        return np.asarray(frames, dtype=np.float32)

    result = np.asarray(frames, dtype=np.float64).copy()

    # 体型缩放：以每帧髋中心为基准缩放全身
    if config.body_scale_max > config.body_scale_min:
        ratio = rng.uniform(config.body_scale_min, config.body_scale_max)
        hips = result[:, HIPS]
        result = hips[:, None, :] + (result - result[:, HIPS][:, None, :]) * ratio

    # 左右腿互换
    if rng.random() < config.leg_swap:
        result = _swap_legs(result).astype(np.float64)

    # 时间抖动：随机把某一帧替换成与相邻帧的插值
    if config.time_jitter > 0 and result.shape[0] > 3:
        count = int(rng.binomial(result.shape[0] - 2, config.time_jitter))
        for _ in range(count):
            index = int(rng.integers(1, result.shape[0] - 1))
            alpha = float(rng.uniform(0.3, 0.7))
            result[index] = result[index - 1] * alpha + result[index + 1] * (1.0 - alpha)

    # 关节噪声（米 → 身高单位）
    if config.joint_noise_m > 0:
        result += rng.normal(0.0, config.joint_noise_m / ASSUMED_HEIGHT_M, size=result.shape)

    # 丢关节：用父关节位置替代（模拟漏检/平滑动捕）
    if config.joint_dropout > 0:
        parents = _JOINT_PARENTS
        drops = rng.random(result.shape[1]) < config.joint_dropout
        for joint, dropped in enumerate(drops):
            if not dropped:
                continue
            parent = parents[joint]
            if parent is None:
                continue
            result[:, joint] = result[:, parent]

    return result.astype(np.float32)


#: 每个关节的"父关节"，用于模拟漏检时的回填
_JOINT_PARENTS: tuple[Optional[int], ...] = (
    None,        # Center of Hips
    0,           # Left Hip
    1,           # Left Knee
    2,           # Left Ankle
    0,           # Right Hip
    4,           # Right Knee
    5,           # Right Ankle
    0,           # Center of Body
    7,           # Center of Shoulder
    8,           # Neck
    9,           # Head
    8,           # Right Shoulder
    11,          # Right Elbow
    12,          # Right Wrist
    8,           # Left Shoulder
    14,          # Left Elbow
    15,          # Left Wrist
)


def build_synthetic_dataset(
    shots: Iterable[PoseShot],
    seed: int = 20261019,
    deform: DeformConfig = DeformConfig(),
    randomization: RandomizationConfig = RandomizationConfig(),
    directions: Sequence[int] = (-1, 0, 1),
) -> list[SyntheticSample]:
    """每个真实 shot × 每个方向 × `variants_per_source` 个变体。"""
    shots = list(shots)
    if not shots:
        return []
    rng = np.random.default_rng(seed)
    samples: list[SyntheticSample] = []
    per_direction = max(1, int(deform.variants_per_source))

    for shot in shots:
        stop = min(shot.kick_frame + 1, shot.frames.shape[0])
        clip = shot.frames[:stop]
        for direction in directions:
            for variant in range(per_direction):
                deformed = deform_for_direction(clip, stop - 1, direction, rng, deform)
                final = randomize(deformed, rng, randomization)
                samples.append(
                    SyntheticSample(
                        sample_id=f"{shot.shot_id}-{direction:+d}-{variant:03d}",
                        frames=final,
                        direction=int(direction),
                        source_shot_id=shot.shot_id,
                        kick_frame=stop - 1,
                        frame_step_ms=shot.frame_step_ms,
                    )
                )
    return samples


def subset_by_source(
    samples: Sequence[SyntheticSample], source_ids: set[str]
) -> list[SyntheticSample]:
    """按源 shot 过滤（做"源 shot 不跨训练/验证"的划分）。"""
    return [sample for sample in samples if sample.source_shot_id in source_ids]
