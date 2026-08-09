"""内参标定 — 基于棋盘格的相机内参计算"""
import json
import time
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from aifootball.config import Config

CHECKERBOARD = (9, 6)
SQUARE_SIZE = 23.0
MIN_FRAMES = 10
MAX_FRAMES = 25
CRITERIA = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 0.001)


@dataclass
class CalibrationResult:
    profile_name: str
    image_size: tuple
    rms: float
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray


def calibrate_intrinsics(cfg: Config, video_path: str | Path = None):
    """运行内参标定流程"""
    if video_path is None:
        print("请提供标定视频路径")
        return None

    video_path = Path(video_path)
    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"无法打开视频: {video_path}")

    objp = np.zeros((CHECKERBOARD[0] * CHECKERBOARD[1], 3), np.float32)
    objp[:, :2] = np.mgrid[0:CHECKERBOARD[0], 0:CHECKERBOARD[1]].T.reshape(-1, 2)
    objp *= SQUARE_SIZE

    obj_points = []
    img_points = []
    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    img_size = None
    last_report = 0.0

    print(f"处理标定视频: {video_path.name} ({total_frames} 帧)")

    for i in range(total_frames):
        ret, frame = cap.read()
        if not ret:
            break

        if img_size is None:
            img_size = (frame.shape[1], frame.shape[0])

        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        found, corners = cv2.findChessboardCorners(gray, CHECKERBOARD, None)

        if found:
            corners = cv2.cornerSubPix(gray, corners, (11, 11), (-1, -1), CRITERIA)
            obj_points.append(objp)
            img_points.append(corners)

        now = time.time()
        if now - last_report > 0.5:
            pct = (i + 1) / total_frames * 100
            print(f"\r  进度: {pct:.0f}% | 棋盘格: {len(obj_points)}", end="")
            last_report = now

    cap.release()
    print()

    if len(obj_points) < MIN_FRAMES:
        print(f"棋盘格帧不足: {len(obj_points)} < {MIN_FRAMES}")
        return None

    # 选取分布均匀的帧
    if len(obj_points) > MAX_FRAMES:
        indices = np.linspace(0, len(obj_points) - 1, MAX_FRAMES, dtype=int)
        obj_points = [obj_points[i] for i in indices]
        img_points = [img_points[i] for i in indices]

    rms, mtx, dist, rvecs, tvecs = cv2.calibrateCamera(
        obj_points, img_points, img_size, None, None
    )

    cfg.calib_dir.mkdir(parents=True, exist_ok=True)
    np.savez(cfg.intrinsics_left, mtx=mtx, dist=dist, rms=rms, size=img_size)
    np.savez(cfg.intrinsics_right, mtx=mtx, dist=dist, rms=rms, size=img_size)

    print(f"标定完成: RMS={rms:.4f}, 图像={img_size}")
    return CalibrationResult(
        profile_name="default",
        image_size=img_size,
        rms=rms,
        camera_matrix=mtx,
        dist_coeffs=dist,
    )
