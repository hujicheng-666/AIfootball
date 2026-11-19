"""3DSP（3D Shot Posture Dataset）读取与触球帧估计。

数据集事实（勘察于 2026-09-30，见 `docs/design-individual-training-loop.md` §3.2）：

    - 210 个 shot（train 200 + test 10），17 关节（H3WB 格式）；
    - 每 shot 20 帧姿态，`position_step` = 40 ms；
    - 坐标是 hip-relative、逐帧按身高归一化（脚 z=0、头 z=1），z 轴即世界竖直；
    - 标签只有"射正 / 射偏"，**没有方向标签** —— 这是 C2 最硬的约束；
    - test 集没有 `posture/` 目录，只能从 train 做留出。

触球时刻没有逐帧标注，但 `info.ini` 的比赛时间轴给出了精确值：
    `annotated_position` − `start_position` = **恒为 500 ms**（200/200），
    `position_step` = 40 ms → 触球落在第 12.5 帧（0-based）。
    **所有 shot 的时间对齐精确且一致**，这是 C2 能做 Δt 截断评估的前提。

`estimate_kick_frame`（踝速度峰值）只作为 info.ini 缺失时的兜底：实测它在
20 个样本上给出 9–17 的分布，而真值恒为 12.5，误差来自 3D 重建的帧间跳变。
"""
from __future__ import annotations

import configparser
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Optional

import numpy as np

#: H3WB 17 关节（顺序即索引，3DSP 的 JSON 里也带 `name` 字段，读取时按名字对齐）
JOINT_NAMES: tuple[str, ...] = (
    "Center of Hips",
    "Left Hip",
    "Left Knee",
    "Left Ankle",
    "Right Hip",
    "Right Knee",
    "Right Ankle",
    "Center of Body",
    "Center of Shoulder",
    "Neck",
    "Head",
    "Right Shoulder",
    "Right Elbow",
    "Right Wrist",
    "Left Shoulder",
    "Left Elbow",
    "Left Wrist",
)
JOINT_INDEX = {name: index for index, name in enumerate(JOINT_NAMES)}

HIPS = JOINT_INDEX["Center of Hips"]
SHOULDER = JOINT_INDEX["Center of Shoulder"]
LEFT_HIP = JOINT_INDEX["Left Hip"]
RIGHT_HIP = JOINT_INDEX["Right Hip"]
LEFT_KNEE = JOINT_INDEX["Left Knee"]
RIGHT_KNEE = JOINT_INDEX["Right Knee"]
LEFT_ANKLE = JOINT_INDEX["Left Ankle"]
RIGHT_ANKLE = JOINT_INDEX["Right Ankle"]
LEFT_SHOULDER = JOINT_INDEX["Left Shoulder"]
RIGHT_SHOULDER = JOINT_INDEX["Right Shoulder"]
NECK = JOINT_INDEX["Neck"]
HEAD = JOINT_INDEX["Head"]
CENTER_OF_BODY = JOINT_INDEX["Center of Body"]

#: 估算触球帧时取两个踝关节的最大帧间位移
ANKLE_INDICES: tuple[int, int] = (LEFT_ANKLE, RIGHT_ANKLE)

#: 3DSP 默认帧间隔（`info.ini` 的 `position_step=40`，200/200 一致）
DEFAULT_FRAME_STEP_MS = 40.0

#: 触球相对序列起点的默认时间（`annotated_position - start_position`，200/200 一致）
DEFAULT_KICK_TIME_MS = 500.0


@dataclass
class PoseShot:
    """一个射门的姿态序列与元数据。"""

    shot_id: str
    frames: np.ndarray          # [T, 17, 3] float32，hip-relative、身高归一化
    on_target: int              # 1 = 射正，0 = 射偏
    label_text: str             # info.ini 原文标签（"Shots on target" 等）
    frame_step_ms: float        # 相邻姿态帧的间隔
    kick_frame: int             # 0-based 触球帧（floor(kick_time_ms/step)）
    kick_time_ms: float = DEFAULT_KICK_TIME_MS   # 触球相对序列起点
    kick_frame_source: str = "info"              # "info" | "ankle-peak"
    source: str = "3dsp"

    @property
    def frame_count(self) -> int:
        return int(self.frames.shape[0])

    @property
    def pre_kick_frames(self) -> int:
        """触球前可用帧数（含触球帧本身）。"""
        return self.kick_frame + 1

    @property
    def lead_time_ms(self) -> float:
        """从序列首帧到触球的时间跨度。"""
        return self.kick_frame * self.frame_step_ms


def _read_info(shot_dir: Path) -> dict[str, str]:
    """读 info.ini（容错：缺 section、空文件都不能让整个数据集加载失败）。"""
    parser = configparser.ConfigParser()
    try:
        parser.read(shot_dir / "info.ini", encoding="utf-8")
    except (configparser.Error, OSError):
        return {}
    if not parser.has_section("info"):
        return {}
    return {key: value.strip() for key, value in parser.items("info")}


def _label_from_info(info: dict[str, str]) -> tuple[int, str]:
    """把 info.ini 的 label 映射成 1/0（射正/射偏）。"""
    text = str(info.get("label", "")).strip()
    normalized = text.lower()
    if "on target" in normalized:
        return 1, text
    if "off target" in normalized:
        return 0, text
    # 未知标签不静默当负类：直接报错，避免训练集被悄悄污染
    raise ValueError(f"无法识别的 3DSP 标签: {text!r}")


def estimate_kick_frame(frames: np.ndarray) -> int:
    """用踝关节帧间位移峰值估计触球帧（0-based）——仅作 info.ini 缺失时的兜底。

    实测误差大（分布 9–17，真值恒为 12.5），因为 3D 重建的帧间跳变会伪造
    速度峰值。正常路径请用 `info.ini` 的比赛时间轴。
    """
    if frames.shape[0] < 2:
        return 0
    deltas = np.linalg.norm(np.diff(frames, axis=0), axis=2)   # [T-1, 17]
    ankle_speed = deltas[:, list(ANKLE_INDICES)].max(axis=1)
    return int(np.argmax(ankle_speed)) + 1


def _read_posture_frames(shot_dir: Path) -> Optional[np.ndarray]:
    """读取一帧的 17×3 坐标；缺 posture 目录时返回 None（test 集就是这样）。"""
    posture_dir = shot_dir / "posture"
    if not posture_dir.is_dir():
        return None
    frame_paths = sorted(posture_dir.glob("*.json"))
    if not frame_paths:
        return None

    frames = np.zeros((len(frame_paths), len(JOINT_NAMES), 3), dtype=np.float32)
    for frame_index, path in enumerate(frame_paths):
        payload = json.loads(path.read_text(encoding="utf-8"))
        keypoints = payload.get("keypoint_3d") or {}
        # 按 name 对齐而不是按 JSON 的 key 顺序：不同版本的数据集 key 可能变
        for joint in keypoints.values():
            name = joint.get("name")
            index = JOINT_INDEX.get(name)
            if index is None:
                continue
            frames[frame_index, index] = (
                float(joint.get("x", 0.0)),
                float(joint.get("y", 0.0)),
                float(joint.get("z", 0.0)),
            )
    return frames


def _resolve_kick(frames: np.ndarray, info: dict[str, str]) -> tuple[int, float, str]:
    """确定触球帧：优先用 info.ini 的比赛时间轴，缺失时退回踝速度峰值。"""
    try:
        start = float(info["start_position"])
        annotated = float(info["annotated_position"])
        step = float(info.get("position_step", DEFAULT_FRAME_STEP_MS))
        kick_time = annotated - start
        if np.isfinite(kick_time) and np.isfinite(step) and step > 0 and kick_time > 0:
            kick_frame = int(kick_time // step)
            if 0 <= kick_frame < frames.shape[0]:
                return kick_frame, kick_time, "info"
    except (KeyError, TypeError, ValueError):
        pass
    kick_frame = estimate_kick_frame(frames)
    return kick_frame, kick_frame * DEFAULT_FRAME_STEP_MS, "ankle-peak"


def load_shot(shot_dir: Path) -> Optional[PoseShot]:
    """读一个 shot；没有 posture（test 集）或数据异常时返回 None。"""
    frames = _read_posture_frames(shot_dir)
    if frames is None or frames.shape[0] < 3:
        return None
    info = _read_info(shot_dir)
    try:
        on_target, label_text = _label_from_info(info)
    except ValueError:
        return None

    try:
        frame_step_ms = float(info.get("position_step", DEFAULT_FRAME_STEP_MS))
    except (TypeError, ValueError):
        frame_step_ms = DEFAULT_FRAME_STEP_MS
    if not np.isfinite(frame_step_ms) or frame_step_ms <= 0:
        frame_step_ms = DEFAULT_FRAME_STEP_MS

    kick_frame, kick_time_ms, kick_source = _resolve_kick(frames, info)

    return PoseShot(
        shot_id=shot_dir.name,
        frames=frames,
        on_target=on_target,
        label_text=label_text,
        frame_step_ms=frame_step_ms,
        kick_frame=kick_frame,
        kick_time_ms=kick_time_ms,
        kick_frame_source=kick_source,
    )


def load_split(dataset_root: Path, split: str = "train") -> list[PoseShot]:
    """读取一个 split（train/test）下所有带 posture 的 shot。"""
    split_dir = Path(dataset_root) / split
    if not split_dir.is_dir():
        raise FileNotFoundError(f"找不到 3DSP split 目录: {split_dir}")
    shots: list[PoseShot] = []
    for shot_dir in sorted(p for p in split_dir.iterdir() if p.is_dir()):
        shot = load_shot(shot_dir)
        if shot is not None:
            shots.append(shot)
    return shots


def load_train(dataset_root: Path) -> list[PoseShot]:
    """便捷入口：默认读取 train split。"""
    return load_split(dataset_root, "train")


def summarize(shots: Iterable[PoseShot]) -> dict:
    """数据勘察摘要（CLI 的 `pose inspect` 与测试都用它）。"""
    shots = list(shots)
    if not shots:
        return {"shots": 0}
    frames = np.array([shot.frame_count for shot in shots], dtype=np.int64)
    kicks = np.array([shot.kick_frame for shot in shots], dtype=np.int64)
    kick_times = np.array([shot.kick_time_ms for shot in shots], dtype=np.float64)
    steps = np.array([shot.frame_step_ms for shot in shots], dtype=np.float64)
    on_target = sum(shot.on_target for shot in shots)
    from_info = sum(shot.kick_frame_source == "info" for shot in shots)
    return {
        "shots": len(shots),
        "on_target": int(on_target),
        "off_target": int(len(shots) - on_target),
        "frames_min": int(frames.min()),
        "frames_max": int(frames.max()),
        "kick_frame_median": float(np.median(kicks)),
        "kick_frame_min": int(kicks.min()),
        "kick_frame_max": int(kicks.max()),
        "kick_time_ms_median": float(np.median(kick_times)),
        "frame_step_ms_median": float(np.median(steps)),
        "lead_time_ms_median": float(np.median(kicks * steps)),
        "pre_kick_frames_min": int(min(shot.pre_kick_frames for shot in shots)),
        "kick_frame_from_info": int(from_info),
        "has_20_frames": int(np.sum(frames == 20)),
    }
