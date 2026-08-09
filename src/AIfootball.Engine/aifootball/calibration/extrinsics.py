"""外参标定 — 基于场地参考点的 PnP 求解"""
import json
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from aifootball.config import Config


@dataclass
class ExtrinsicsResult:
    name: str
    rvec: np.ndarray
    tvec: np.ndarray
    rotation_matrix: np.ndarray
    camera_center: np.ndarray
    reprojection_error: float


def estimate_extrinsics(cfg: Config):
    """估计外参（需要预先放置参考图和标定点）"""
    # 场地关键点 (世界坐标: X=场宽, Y=场长, Z=高度)
    field_points = {
        "penalty_spot": (0.0, 11.0, 0.0),
        "goal_left_post": (-3.66, 0.0, 0.0),
        "goal_right_post": (3.66, 0.0, 0.0),
        "goal_crossbar_center": (0.0, 0.0, 2.44),
        "corner_near_left": (-7.32, 0.0, 0.0),
        "corner_near_right": (7.32, 0.0, 0.0),
        "penalty_area_left": (-20.16, 16.5, 0.0),
        "penalty_area_right": (20.16, 16.5, 0.0),
    }

    print("外参标定需要交互式标注参考点，请使用 GUI 工具完成。")
    print(f"标定数据将保存到: {cfg.calib_dir}")

    # 检查是否已有标定数据
    required = [cfg.pose_left, cfg.pose_right,
                cfg.extrinsics_left, cfg.extrinsics_right]
    missing = [f for f in required if not f.exists()]
    if missing:
        print(f"缺少标定文件: {missing}")
        return None

    print("外参文件已存在，跳过。")
    return True
