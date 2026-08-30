import argparse
import csv
import json
import os
import sys
import time
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from concurrent.futures import ThreadPoolExecutor
from scipy.interpolate import interp1d
from scipy.optimize import least_squares
from ultralytics import YOLO

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


from project.config import WORKSPACE as WORKSPACE_DIR, CALIB, SAMPLES
from project.constants import (
    SPORTS_BALL_CLASS_ID,
    PENALTY_SPOT_GROUND_WORLD,
    FIELD_X_LIMITS,
    FIELD_Y_LIMITS,
    DEFAULT_IMGSZ,
    DEFAULT_CONF,
)

OUTPUT_ROOT = WORKSPACE_DIR / "output" / "trajectory_3d"
DEFAULT_YOLO_MODEL = "yolo11m.pt"
YOLO_MODEL_PATH = Path(os.environ.get("YOLO_MODEL_PATH", WORKSPACE_DIR / "models" / DEFAULT_YOLO_MODEL))
WORLD_Z_LIMITS = (-1.0, 8.0)
# 相机语义由样本目录决定：left/ 中的视频对应 left 标定，right/ 对应 right 标定。
DEFAULT_IMGSZ = 1280
# The ball spans only ~37 px on screen, far smaller than the detection
# resolution; a fast ball currently triggers a 960-px inference (~1.1 s/frame
# on CPU).  Capping the inference size to 640 cuts that to ~0.52 s (~2.1x)
# with negligible localisation loss.  The crop window is kept large so the
# fast-moving ball does not leave the search region.
DETECT_IMGSZ_CAP = 640
DEFAULT_CONF = 0.15
MAX_TRACK_CANDIDATES = 4


@dataclass
class CameraConfig:
    name: str
    pose_path: Path
    meta_path: Path
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    rvec: np.ndarray
    tvec: np.ndarray
    rotation_matrix: np.ndarray
    camera_center_world: np.ndarray


@dataclass
class VideoTrack:
    video_path: Path
    camera_name: str
    fps: float
    frame_count: int
    frame_size: tuple[int, int]
    kick_frame: int
    detections: list
    times: np.ndarray
    image_points: np.ndarray
    confidences: np.ndarray
    center_covariances: np.ndarray
    path_diagnostics: dict


@dataclass
class Trajectory3D:
    raw_times: np.ndarray
    raw_world_points: np.ndarray
    raw_ray_gaps: np.ndarray
    raw_reprojection_errors: np.ndarray
    raw_keep_mask: np.ndarray
    raw_image_points_left: np.ndarray
    raw_image_points_right: np.ndarray
    raw_confidences_left: np.ndarray
    raw_confidences_right: np.ndarray
    times: np.ndarray
    world_points: np.ndarray
    ray_gaps: np.ndarray
    reprojection_errors: np.ndarray
    image_points_left: np.ndarray
    image_points_right: np.ndarray
    confidences_left: np.ndarray
    confidences_right: np.ndarray
    offset_seconds: float
    alignment_metrics: dict


def load_camera_configs():
    configs = []
    for name in ("left", "right"):
        pose_path = CALIB / f"{name}_pose.npz"
        meta_path = CALIB / f"{name}_extrinsics.json"
        if not pose_path.exists() or not meta_path.exists():
            raise FileNotFoundError(f"缺少相机参数文件: {pose_path} 或 {meta_path}")

        pose = np.load(pose_path)
        camera_matrix = np.asarray(pose["camera_matrix"], dtype=np.float64)
        dist_coeffs = np.asarray(pose["dist_coeffs"], dtype=np.float64)
        rvec = np.asarray(pose["rvec"], dtype=np.float64).reshape(3, 1)
        tvec = np.asarray(pose["tvec"], dtype=np.float64).reshape(3, 1)
        rotation_matrix = np.asarray(pose["R"], dtype=np.float64)
        camera_center_world = -rotation_matrix.T @ tvec.reshape(3)
        configs.append(
            CameraConfig(
                name=name,
                pose_path=pose_path,
                meta_path=meta_path,
                camera_matrix=camera_matrix,
                dist_coeffs=dist_coeffs,
                rvec=rvec,
                tvec=tvec,
                rotation_matrix=rotation_matrix,
                camera_center_world=camera_center_world,
            )
        )
    return configs


def resolve_camera_videos(sample_dir):
    """Read camera identity only from the sample's left/right folders."""
    videos = {}
    for camera_name in ("left", "right"):
        camera_dir = sample_dir / camera_name
        camera_videos = sorted(path for path in camera_dir.glob("*.mp4") if path.is_file())
        if len(camera_videos) != 1:
            raise RuntimeError(
                f"{camera_dir} must contain exactly one MP4 video; found {len(camera_videos)}."
            )
        videos[camera_name] = camera_videos[0]
    return videos


def project_world_points(world_points, config):
    image_points, _ = cv2.projectPoints(
        np.asarray(world_points, dtype=np.float64),
        config.rvec,
        config.tvec,
        config.camera_matrix,
        config.dist_coeffs,
    )
    return image_points.reshape(-1, 2)


def image_point_to_ground_world(image_point, config, ground_z=0.0):
    pts = np.asarray(image_point, dtype=np.float64).reshape(1, 1, 2)
    undist = cv2.undistortPoints(pts, config.camera_matrix, config.dist_coeffs)
    x_norm, y_norm = undist.reshape(2)

    ray_cam = np.array([x_norm, y_norm, 1.0], dtype=np.float64)
    ray_world = config.rotation_matrix.T @ ray_cam
    if abs(ray_world[2]) < 1e-8:
        return None

    scale = (ground_z - config.camera_center_world[2]) / ray_world[2]
    return config.camera_center_world + scale * ray_world


def estimate_box_center_covariance(bbox, confidence):
    """Conservative pixel covariance for a YOLO ball centre measurement."""
    x1, y1, x2, y2 = [float(value) for value in bbox]
    width = max(x2 - x1, 1.0)
    height = max(y2 - y1, 1.0)
    # Bounding-box scale is a proxy for the visible ball radius.  Low
    # confidence and elongated boxes inflate the uncertainty rather than
    # pretending every detector centre is equally precise.
    anisotropy = max(width, height) / min(width, height)
    confidence_factor = 1.0 + 1.5 * (1.0 - float(np.clip(confidence, 0.0, 1.0)))
    sigma_x = max(0.75, 0.12 * width * confidence_factor * np.sqrt(anisotropy))
    sigma_y = max(0.75, 0.12 * height * confidence_factor * np.sqrt(anisotropy))
    return np.diag([sigma_x * sigma_x, sigma_y * sigma_y]).astype(np.float64)


def center_covariance_log_area(covariance):
    covariance = np.asarray(covariance, dtype=np.float64).reshape(2, 2)
    determinant = max(float(np.linalg.det(covariance)), 1e-9)
    return float(np.log(determinant))


def regularize_center_covariance(covariance):
    """Return a finite positive-definite 2D pixel covariance.

    Covariances are propagated through linear time interpolation, so this
    guard is deliberately data-independent: it only removes numerical
    degeneracy and never encodes sample-specific tolerances.
    """
    covariance = np.asarray(covariance, dtype=np.float64).reshape(2, 2)
    covariance = 0.5 * (covariance + covariance.T)
    if not np.all(np.isfinite(covariance)):
        return np.eye(2, dtype=np.float64) * 9.0
    eigenvalues, eigenvectors = np.linalg.eigh(covariance)
    eigenvalues = np.clip(eigenvalues, 0.25 ** 2, 64.0 ** 2)
    return (eigenvectors * eigenvalues) @ eigenvectors.T


def whiten_pixel_residuals(residuals, covariances):
    """Whiten Nx2 pixel residuals using their measurement covariances."""
    residuals = np.asarray(residuals, dtype=np.float64).reshape(-1, 2)
    covariances = np.asarray(covariances, dtype=np.float64).reshape(-1, 2, 2)
    whitened = np.empty_like(residuals)
    for index, residual in enumerate(residuals):
        cholesky = np.linalg.cholesky(regularize_center_covariance(covariances[index]))
        whitened[index] = np.linalg.solve(cholesky, residual)
    return whitened


def rank_detections(boxes, expected_center, penalty_center, max_candidates=MAX_TRACK_CANDIDATES):
    ranked = []
    MIN_CONF = 0.08   # absolute minimum confidence to even consider a detection
    MAX_AREA = 20000  # reject unrealistically large boxes

    for box in boxes:
        x1, y1, x2, y2 = [float(v) for v in box.xyxy[0].tolist()]
        conf = float(box.conf.item())
        if conf < MIN_CONF:
            continue
        w = max(x2 - x1, 1.0)
        h = max(y2 - y1, 1.0)
        area = w * h
        # Reject boxes that are too large (likely false positives) or too tiny
        if area > MAX_AREA or area < 4.0:
            continue
        center = np.array([(x1 + x2) * 0.5, (y1 + y2) * 0.5], dtype=np.float64)
        foot = np.array([(x1 + x2) * 0.5, y2], dtype=np.float64)
        dist_expected = np.linalg.norm(center - expected_center)
        dist_penalty = np.linalg.norm(center - penalty_center)
        # Heavier weight on confidence, spatial proximity to prediction
        score = 8.0 * conf - 0.002 * dist_expected - 0.0005 * dist_penalty + 0.00005 * area
        ranked.append((score, {
            "conf": conf,
            "bbox": [x1, y1, x2, y2],
            "center": center,
            "foot": foot,
            "center_covariance": estimate_box_center_covariance([x1, y1, x2, y2], conf),
        }))

    ranked.sort(key=lambda item: item[0], reverse=True)
    return [candidate for _, candidate in ranked[:max_candidates]]


def pick_detection(boxes, expected_center, penalty_center):
    ranked = rank_detections(boxes, expected_center, penalty_center, max_candidates=1)
    return ranked[0] if ranked else None

    return best


def estimate_kick_frame(detections):
    distances = []
    for det in detections:
        if det["ground_point"] is None:
            distances.append(np.nan)
        else:
            distances.append(float(np.linalg.norm(det["ground_point"][:2] - PENALTY_SPOT_GROUND_WORLD[:2])))

    distances = np.asarray(distances, dtype=np.float64)
    for idx in range(len(distances)):
        if not np.isfinite(distances[idx]):
            continue
        prev_window = distances[max(0, idx - 8):idx]
        next_window = distances[idx:min(len(distances), idx + 3)]
        if len(next_window) == 0:
            continue
        prev_ok = np.any(np.isfinite(prev_window) & (prev_window < 0.35)) or idx == 0
        next_ok = np.nanmean(next_window) > 0.45
        if prev_ok and next_ok:
            return idx

    finite_indices = np.where(np.isfinite(distances))[0]
    return int(finite_indices[0]) if len(finite_indices) else 0


def smooth_points(points, window=7):
    if len(points) < 3:
        return points.copy()

    points = points.astype(np.float64)
    window = max(3, min(int(window), len(points)))
    half = window // 2
    smoothed = points.copy()
    count = len(points)

    if count >= window:
        # 满窗行向量化：三角权重一次算好，sliding_window_view 免拷贝取窗
        positions = np.arange(window, dtype=np.float64)
        weights = np.clip(half + 1.0 - np.abs(positions - half), 1.0, None)
        weights = weights / weights.sum()
        windows = np.lib.stride_tricks.sliding_window_view(points, window, axis=0)
        center_rows = np.arange(half, count - half)
        smoothed[center_rows] = np.einsum("k,ifk->if", weights, windows)

    # 边界行窗口被截断、权重随实际窗口宽度变化，保留逐点计算（至多 2*half 行）
    for idx in list(range(min(half, count))) + list(range(max(half, count - half), count)):
        start = max(0, idx - half)
        end = min(count, idx + half + 1)
        segment = points[start:end]
        if len(segment) == 1:
            continue
        center = idx - start
        positions = np.arange(len(segment), dtype=np.float64)
        distances = np.abs(positions - float(center))
        weights = distances.max() + 1.0 - distances
        weights = np.clip(weights, 1.0, None)
        weights = weights / weights.sum()
        smoothed[idx] = np.sum(segment * weights[:, None], axis=0)
    return smoothed


def _refine_ball_center(frame, x1, y1, x2, y2, orig_center):
    """Refine ball center within YOLO bbox using color segmentation.

    Footballs are mostly white and must appear as a small, highly circular
    white blob near the bbox centre.  The scene also contains many white /
    non-green objects (player shirts, goal net lines, pitch lines, distant
    clutter), so the old "white OR NOT-green" mask is not used -- it absorbed
    large non-green regions and snapped the ball centre onto a shirt or the
    net.  Instead only the low-saturation/high-value white mask is kept and the
    blob must pass circularity, area-ratio and centre-proximity checks; if no
    trustworthy round blob is found we return None so the caller keeps the YOLO
    centre rather than being dragged onto an unrelated white object.
    """
    try:
        x1i, y1i = max(0, int(x1)), max(0, int(y1))
        x2i, y2i = min(frame.shape[1], int(x2)), min(frame.shape[0], int(y2))
        if x2i - x1i < 4 or y2i - y1i < 4:
            return None

        bw = float(x2i - x1i)
        bh = float(y2i - y1i)
        roi = frame[y1i:y2i, x1i:x2i]
        hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)

        # White ball only (low saturation, high value).  Do not OR in the
        # non-green complement -- that absorbed shirts / net / clutter.
        mask = cv2.inRange(hsv, np.array([0, 0, 160]), np.array([180, 60, 255]))
        contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not contours:
            return None

        bcx, bcy = bw * 0.5, bh * 0.5
        best_contour = None
        best_score = -1.0
        for cnt in contours:
            area = cv2.contourArea(cnt)
            if area < 4.0:  # too small
                continue
            M = cv2.moments(cnt)
            if M["m00"] < 1e-6:
                continue
            cx_c = M["m10"] / M["m00"]
            cy_c = M["m01"] / M["m00"]
            circularity = 4.0 * np.pi * area / (cv2.arcLength(cnt, True) ** 2 + 1e-6)
            if circularity < 0.62:  # a ball is nearly circular
                continue
            area_ratio = area / (bw * bh + 1e-6)
            if area_ratio < 0.04 or area_ratio > 0.85:  # shirt/net fills the box
                continue
            dist_norm = np.hypot((cx_c - bcx) / max(bw * 0.5, 1.0),
                                 (cy_c - bcy) / max(bh * 0.5, 1.0))
            if dist_norm > 0.55:  # blob must be near the box centre
                continue
            # Score favours a round blob that fills a sensible part of the box.
            score = circularity * area_ratio - 0.4 * dist_norm
            if score > best_score:
                best_score = score
                radial_scale = max(np.sqrt(area / np.pi), 1.0)
                contour_covariance = np.array([
                    [M["mu20"] / M["m00"], M["mu11"] / M["m00"]],
                    [M["mu11"] / M["m00"], M["mu02"] / M["m00"]],
                ], dtype=np.float64)
                contour_covariance = 0.5 * (contour_covariance + contour_covariance.T)
                inflation = 0.18 * (1.0 + max(0.0, 1.0 - circularity))
                measurement_covariance = contour_covariance * inflation * inflation
                measurement_covariance += np.eye(2, dtype=np.float64) * max(0.50, 0.12 * radial_scale) ** 2
                best_contour = (cx_c, cy_c, measurement_covariance)

        if best_contour is not None:
            return (
                np.array([best_contour[0] + x1i, best_contour[1] + y1i], dtype=np.float64),
                best_contour[2],
            )
    except Exception as exc:
        print(f"[refine] 颜色分割细化失败: {exc}", file=sys.stderr)
    return None


def _crop_around_point(frame, center_xy, crop_size, frame_w, frame_h):
    """Crop a square region around center_xy, padding with black if out of bounds."""
    cx, cy = center_xy
    half = crop_size // 2
    x1 = int(cx - half)
    y1 = int(cy - half)
    x2 = x1 + crop_size
    y2 = y1 + crop_size

    pad_left = max(0, -x1)
    pad_top = max(0, -y1)
    pad_right = max(0, x2 - frame_w)
    pad_bottom = max(0, y2 - frame_h)

    x1 = max(0, x1)
    y1 = max(0, y1)
    x2 = min(frame_w, x2)
    y2 = min(frame_h, y2)

    crop = frame[y1:y2, x1:x2].copy()
    if pad_left > 0 or pad_top > 0 or pad_right > 0 or pad_bottom > 0:
        crop = cv2.copyMakeBorder(crop, pad_top, pad_bottom, pad_left, pad_right,
                                  cv2.BORDER_CONSTANT, value=(0, 0, 0))
    return crop, (x1, y1)  # offset of crop in original frame


def _predict_ball_position(history):
    """Predict next ball position using velocity + acceleration from history.
    Returns (predicted_center, velocity_magnitude)."""
    if len(history) >= 3:
        v1 = history[-1] - history[-2]
        v2 = history[-2] - history[-3]
        accel = v1 - v2
        predicted = history[-1] + v1 + 0.5 * accel
        vel_mag = float(np.linalg.norm(v1 + accel))
    elif len(history) >= 2:
        v = history[-1] - history[-2]
        predicted = history[-1] + v
        vel_mag = float(np.linalg.norm(v))
    elif history:
        predicted = history[-1]
        vel_mag = 0.0
    else:
        return None, 0.0
    return predicted, vel_mag


class ConstantAccelerationFilter:
    """2D 像素域匀加速(CA)卡尔曼滤波，替代二阶有限差分外推做逐帧预测。

    线性高斯假设下的 MMSE 最优估计：状态 [x, y, vx, vy, ax, ay]，转移
    矩阵用实际帧间隔；测量噪声直接使用检测端输出的中心协方差（YOLO
    框尺度/圆度轮廓矩），过程噪声采用分段常白加速度模型。差分外推把
    最近一次原始检测的差分当作真值，检测抖动被原样放大进预测与速度
    自适应阈值；卡尔曼以协方差加权递归融合全部历史，对单帧抖动不敏感。
    """

    def __init__(self, acceleration_psd=4.0e6):
        # 像素域重力投影加速度量级 ~f/depth·g ≈ 2e3 px/s²，PSD 取其平方
        self.acceleration_psd = float(acceleration_psd)
        self.state = None          # (6,) [x, y, vx, vy, ax, ay]，px、px/s
        self.covariance = None     # (6, 6)
        self.last_dt = 1.0 / 60.0

    def reset(self):
        self.state = None
        self.covariance = None

    @property
    def initialized(self):
        return self.state is not None

    @staticmethod
    def _transition(dt):
        f = np.eye(6, dtype=np.float64)
        f[0, 2] = dt
        f[1, 3] = dt
        f[0, 4] = 0.5 * dt * dt
        f[1, 5] = 0.5 * dt * dt
        f[2, 4] = dt
        f[3, 5] = dt
        return f

    def _process_noise(self, dt):
        q = self.acceleration_psd
        block = q * np.array([
            [dt ** 5 / 20.0, dt ** 4 / 8.0, dt ** 3 / 6.0],
            [dt ** 4 / 8.0, dt ** 3 / 3.0, dt ** 2 / 2.0],
            [dt ** 3 / 6.0, dt ** 2 / 2.0, dt],
        ], dtype=np.float64)
        noise = np.zeros((6, 6), dtype=np.float64)
        noise[np.ix_([0, 2, 4], [0, 2, 4])] = block
        noise[np.ix_([1, 3, 5], [1, 3, 5])] = block
        return noise

    def _propagate(self, dt):
        s = self.state
        s = np.array([
            s[0] + s[2] * dt + 0.5 * s[4] * dt * dt,
            s[1] + s[3] * dt + 0.5 * s[5] * dt * dt,
            s[2] + s[4] * dt,
            s[3] + s[5] * dt,
            s[4],
            s[5],
        ], dtype=np.float64)
        f = self._transition(dt)
        self.state = s
        self.covariance = f @ self.covariance @ f.T + self._process_noise(dt)

    def predict_next(self, dt):
        """推进到下一帧并返回 (预测中心, 速度幅值 px/frame)。

        未初始化返回 (None, 0.0)。无检测帧连续调用即为纯外推：
        状态按过程模型前进、协方差按 Q 增长，速度幅值来自滤波状态。
        """
        if self.state is None:
            return None, 0.0
        dt = max(float(dt), 1e-4)
        self.last_dt = dt
        self._propagate(dt)
        speed_per_frame = float(np.hypot(self.state[2], self.state[3]) * dt)
        return self.state[:2].copy(), speed_per_frame

    def update(self, center, covariance):
        """在已预测状态上融合一个检测（不再次推进时间）。"""
        center = np.asarray(center, dtype=np.float64).reshape(2)
        if covariance is None:
            r = np.eye(2, dtype=np.float64) * 9.0
        else:
            r = np.asarray(covariance, dtype=np.float64).reshape(2, 2)
            r = 0.5 * (r + r.T)
        if not np.all(np.isfinite(r)) or np.linalg.det(r) <= 1e-9:
            r = np.eye(2, dtype=np.float64) * 9.0

        if self.state is None:
            self.state = np.zeros(6, dtype=np.float64)
            self.state[:2] = center
            self.covariance = np.diag([25.0, 25.0, 1.0e6, 1.0e6, 1.0e8, 1.0e8])
            return

        h = np.zeros((2, 6), dtype=np.float64)
        h[0, 0] = 1.0
        h[1, 1] = 1.0
        innovation = center - h @ self.state
        s = h @ self.covariance @ h.T + r
        gain = np.linalg.solve(s, h @ self.covariance).T
        self.state = self.state + gain @ innovation
        # Joseph 形式更新，协方差数值保正定
        i_kh = np.eye(6) - gain @ h
        self.covariance = i_kh @ self.covariance @ i_kh.T + gain @ r @ gain.T


def _adaptive_ball_detect(model, frame, expected_center, penalty_center, imgsz, conf,
                          frame_w, frame_h, velocity_mag=0.0):
    """Adaptive multi-scale ball detection:
    - Slow ball → smaller crop, higher effective resolution
    - Fast ball → larger crop for context
    - Zoom-in refinement when confidence is borderline."""

    # Stage 1: velocity-adaptive crop
    if velocity_mag > 25.0:
        crop_size, stage1_conf = 960, conf * 0.7
    elif velocity_mag > 10.0:
        crop_size, stage1_conf = 640, conf
    else:
        crop_size, stage1_conf = 480, conf

    crop_size = min(crop_size, min(frame_w, frame_h))
    stage1_imgsz = min(crop_size, min(imgsz, DETECT_IMGSZ_CAP))

    cropped, (ox, oy) = _crop_around_point(frame, expected_center, crop_size, frame_w, frame_h)
    current_crop = cropped  # 默认使用 stage-1 裁剪图；stage-1 失败并放大重试成功后会更新
    pc_crop = (penalty_center[0] - ox, penalty_center[1] - oy)
    ec_crop = (expected_center[0] - ox, expected_center[1] - oy)

    result = model.predict(cropped, classes=[SPORTS_BALL_CLASS_ID],
                           conf=stage1_conf, imgsz=stage1_imgsz, verbose=False)[0]
    boxes = [] if result.boxes is None else list(result.boxes)
    candidates = rank_detections(boxes,
                                 np.array(ec_crop, dtype=np.float64),
                                 np.array(pc_crop, dtype=np.float64))
    chosen = candidates[0] if candidates else None

    # Stage 1 failed → expand and retry
    if chosen is None and crop_size < min(frame_w, frame_h):
        bigger = min(crop_size * 2, min(frame_w, frame_h))
        cropped2, (ox2, oy2) = _crop_around_point(frame, expected_center, bigger, frame_w, frame_h)
        pc2 = (penalty_center[0] - ox2, penalty_center[1] - oy2)
        ec2 = (expected_center[0] - ox2, expected_center[1] - oy2)
        result2 = model.predict(cropped2, classes=[SPORTS_BALL_CLASS_ID],
                                conf=conf * 0.5, imgsz=min(bigger, min(imgsz, DETECT_IMGSZ_CAP)), verbose=False)[0]
        boxes2 = [] if result2.boxes is None else list(result2.boxes)
        candidates = rank_detections(boxes2,
                                     np.array(ec2, dtype=np.float64),
                                     np.array(pc2, dtype=np.float64))
        chosen = candidates[0] if candidates else None
        if chosen is not None:
            ox, oy = ox2, oy2
            current_crop = cropped2  # 同步更新裁剪图，保持与 ox/oy 偏移一致

    if chosen is None:
        return None

    # Stage 2: zoom-in refinement for low-confidence or slow-ball detections
    if chosen["conf"] < 0.6 and crop_size > 320:
        zoom_center = np.array([chosen["center"][0] + ox, chosen["center"][1] + oy])
        zoom_size = max(320, crop_size // 2)
        cropped_z, (oz, oyz) = _crop_around_point(frame, zoom_center, zoom_size, frame_w, frame_h)
        pcz = (penalty_center[0] - oz, penalty_center[1] - oyz)
        ecz = (zoom_center[0] - oz, zoom_center[1] - oyz)

        result_z = model.predict(cropped_z, classes=[SPORTS_BALL_CLASS_ID],
                                 conf=conf, imgsz=min(zoom_size, min(imgsz, DETECT_IMGSZ_CAP)), verbose=False)[0]
        boxes_z = [] if result_z.boxes is None else list(result_z.boxes)
        zoom_candidates = rank_detections(boxes_z,
                                          np.array(ecz, dtype=np.float64),
                                          np.array(pcz, dtype=np.float64))
        chosen_z = zoom_candidates[0] if zoom_candidates else None
        if chosen_z is not None and chosen_z["conf"] >= chosen["conf"] * 0.8:
            chosen = chosen_z
            candidates = zoom_candidates
            ox, oy = oz, oyz
            current_crop = cropped_z

    # Refine center using color segmentation within YOLO bbox
    bbox_crop = [chosen["bbox"][0], chosen["bbox"][1], chosen["bbox"][2], chosen["bbox"][3]]
    refined = _refine_ball_center(current_crop,
                                  bbox_crop[0], bbox_crop[1], bbox_crop[2], bbox_crop[3],
                                  chosen["center"])
    if refined is not None:
        refined_center, refined_covariance = refined
        dx = refined_center[0] - chosen["center"][0]
        dy = refined_center[1] - chosen["center"][1]
        chosen["center"] = refined_center
        chosen["foot"][0] += dx
        chosen["foot"][1] += dy
        chosen["center_covariance"] = refined_covariance

    # Map back to full frame
    chosen["bbox"][0] += ox
    chosen["bbox"][1] += oy
    chosen["bbox"][2] += ox
    chosen["bbox"][3] += oy
    chosen["center"][0] += ox
    chosen["center"][1] += oy
    chosen["foot"][0] += ox
    chosen["foot"][1] += oy
    full_candidates = []
    for candidate in candidates:
        full_candidates.append({
            "center": candidate["center"].astype(np.float64) + np.array([ox, oy], dtype=np.float64),
            "conf": float(candidate["conf"]),
            "center_covariance": np.asarray(candidate["center_covariance"], dtype=np.float64),
        })
    if full_candidates:
        full_candidates[0]["center"] = chosen["center"].copy()
        full_candidates[0]["center_covariance"] = np.asarray(
            chosen["center_covariance"], dtype=np.float64
        )
    chosen["candidates"] = full_candidates
    return chosen


def _huber_cost(normalized_residual):
    """Smooth robust cost for a non-negative, scale-normalised residual."""
    value = float(abs(normalized_residual))
    return value * value if value <= 1.0 else 2.0 * value - 1.0


def _huber_cost_array(normalized_residuals):
    """_huber_cost 的向量化版本（输入为非负或任意符号数组，取绝对值）。"""
    value = np.abs(np.asarray(normalized_residuals, dtype=np.float64))
    return np.where(value <= 1.0, value * value, 2.0 * value - 1.0)


def second_order_viterbi(state_rows, times, emission_costs,
                         first_transition_batch, second_transition_batch):
    """Find a global path with a second-order motion model.

    Each row contains alternatives for one observation time.  First-order
    selection cannot distinguish a locally plausible false positive from a
    track that changes acceleration discontinuously; this dynamic programme
    carries the two preceding states so the entire sequence participates in
    every choice.

    `emission_costs` is one cost array per row; the transition callbacks are
    batched: `first_transition_batch(prev_states, cur_states, dt)` returns a
    (Nprev, Ncur) matrix and `second_transition_batch(pp_states, prev_states,
    cur_states, dt_previous, dt_current)` returns a (Npp, Nprev, Ncur) tensor,
    so the O(T·K³) recurrence runs as one vectorised minimisation per step.
    """
    if not state_rows or any(len(row) == 0 for row in state_rows):
        return None
    if len(state_rows) == 1:
        return [int(np.argmin(emission_costs[0]))]

    first_dt = max(float(times[1] - times[0]), 1e-4)
    pair_costs = (
        emission_costs[0][:, None]
        + emission_costs[1][None, :]
        + first_transition_batch(state_rows[0], state_rows[1], first_dt)
    )

    if len(state_rows) == 2:
        first_index, second_index = np.unravel_index(np.argmin(pair_costs), pair_costs.shape)
        return [int(first_index), int(second_index)]

    parents = [None, None]
    for row_index in range(2, len(state_rows)):
        previous_previous_states = state_rows[row_index - 2]
        previous_states = state_rows[row_index - 1]
        current_states = state_rows[row_index]
        dt_previous = max(float(times[row_index - 1] - times[row_index - 2]), 1e-4)
        dt_current = max(float(times[row_index] - times[row_index - 1]), 1e-4)
        transition = second_transition_batch(
            previous_previous_states, previous_states, current_states,
            dt_previous, dt_current,
        )
        total = pair_costs[:, :, None] + transition
        parent_indices = np.argmin(total, axis=0)
        best_cost = np.take_along_axis(total, parent_indices[None, :, :], axis=0)[0]
        pair_costs = best_cost + emission_costs[row_index][None, :]
        parents.append(parent_indices)

    previous_index, current_index = np.unravel_index(np.argmin(pair_costs), pair_costs.shape)
    selected = [-1] * len(state_rows)
    selected[-2] = int(previous_index)
    selected[-1] = int(current_index)
    for row_index in range(len(state_rows) - 1, 1, -1):
        previous_previous_index = int(parents[row_index][selected[row_index - 1], selected[row_index]])
        selected[row_index - 2] = previous_previous_index
    return selected


def optimize_detection_path(detections, kick_frame, fps, frame_size):
    """Globally select one 2D candidate per detected frame using motion continuity."""
    rows = []
    row_times = []
    original_centers = []
    for detection in detections:
        chosen = detection["detection"]
        if chosen is None or detection["frame_idx"] < kick_frame:
            continue
        candidates = chosen.get("candidates") or [
            {
                "center": chosen["center"],
                "conf": chosen["conf"],
                "center_covariance": chosen.get("center_covariance", np.eye(2, dtype=np.float64) * 9.0),
            }
        ]
        normalized = []
        for candidate in candidates[:MAX_TRACK_CANDIDATES]:
            center = np.asarray(candidate["center"], dtype=np.float64)
            if np.all(np.isfinite(center)):
                covariance = np.asarray(
                    candidate.get("center_covariance", chosen.get("center_covariance", np.eye(2) * 9.0)),
                    dtype=np.float64,
                ).reshape(2, 2)
                normalized.append({
                    "center": center,
                    "conf": max(float(candidate["conf"]), 1e-6),
                    "center_covariance": covariance,
                })
        if not normalized:
            continue
        rows.append((detection, normalized))
        row_times.append((detection["frame_idx"] - kick_frame) / fps)
        original_centers.append(np.asarray(chosen["center"], dtype=np.float64).copy())

    if len(rows) < 3:
        return {"enabled": True, "applied": False, "reason": "fewer than three candidate frames"}

    diagonal = float(np.hypot(frame_size[0], frame_size[1]))
    motion_scale_px = max(8.0, diagonal * 0.008)

    def emission(state):
        # Absolute confidence remains meaningful even when a row has only one
        # candidate; the old relative-only score lost this information.
        return -0.45 * np.log(state["conf"]) + 0.10 * center_covariance_log_area(state["center_covariance"])

    def first_transition_batch(previous_states, current_states, dt):
        previous = np.asarray([state["center"] for state in previous_states], dtype=np.float64)
        current = np.asarray([state["center"] for state in current_states], dtype=np.float64)
        frame_displacement = np.linalg.norm(current[None, :, :] - previous[:, None, :], axis=-1)
        speed_px_per_sec = frame_displacement / dt
        # A weak guard only rejects implausible detector jumps; acceleration is
        # evaluated below by the actual second-order state transition.
        speed_limit = diagonal * fps * 0.12
        return 0.05 * _huber_cost_array(frame_displacement / motion_scale_px) + 0.20 * _huber_cost_array(
            np.maximum(0.0, speed_px_per_sec - speed_limit) / max(speed_limit, 1.0)
        )

    def second_transition_batch(previous_previous_states, previous_states, current_states,
                                dt_previous, dt_current):
        previous_previous = np.asarray([s["center"] for s in previous_previous_states], dtype=np.float64)
        previous = np.asarray([s["center"] for s in previous_states], dtype=np.float64)
        current = np.asarray([s["center"] for s in current_states], dtype=np.float64)
        predicted = (
            previous[None, :, None, :]
            + (previous[None, :, None, :] - previous_previous[:, None, None, :])
            * (dt_current / dt_previous)
        )
        residual = np.linalg.norm(current[None, None, :, :] - predicted, axis=-1)
        return 1.25 * _huber_cost_array(residual / motion_scale_px)

    selected_indices = second_order_viterbi(
        [row[1] for row in rows],
        np.asarray(row_times, dtype=np.float64),
        [np.asarray([emission(state) for state in row[1]], dtype=np.float64) for row in rows],
        first_transition_batch,
        second_transition_batch,
    )
    if selected_indices is None:
        return {"enabled": True, "applied": False, "reason": "Viterbi path unavailable"}

    substitutions = 0
    for (detection, candidates), selected_index, original_center in zip(rows, selected_indices, original_centers):
        selected_candidate = candidates[selected_index]
        chosen = detection["detection"]
        if np.linalg.norm(selected_candidate["center"] - original_center) > 1e-3:
            substitutions += 1
        chosen["center"] = selected_candidate["center"].copy()
        chosen["conf"] = float(selected_candidate["conf"])
        chosen["center_covariance"] = selected_candidate["center_covariance"].copy()
        # Keep the globally selected state first so later cross-view Viterbi
        # treats it as the primary hypothesis without discarding alternatives.
        chosen["candidates"] = [selected_candidate] + [
            candidate for index, candidate in enumerate(candidates) if index != selected_index
        ]

    return {
        "enabled": True,
        "applied": True,
        "candidate_frames": int(len(rows)),
        "substituted_frames": int(substitutions),
        "motion_scale_px": float(motion_scale_px),
        "model": "second_order_constant_velocity_viterbi",
    }


def _sparse_scan_kick_frame(cap, fps, frame_count, penalty_center, model, config, imgsz, conf):
    """Phase 1: sparse YOLO scan (~6 fps) to find kick frame."""
    SPARSE_EVERY = max(1, int(fps / 6.0))
    sparse_dets = []
    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
    frame_idx = -1
    started_at = time.monotonic()
    next_report_frame = 0
    # 提高进度报告频率，避免起脚扫描阶段进度长时间不动
    report_every = max(SPARSE_EVERY, int(frame_count / 20))
    print(f"  起脚定位扫描：0/{frame_count} 帧 (0%)", flush=True)

    while True:
        frame_idx += 1
        if frame_idx % SPARSE_EVERY != 0:
            # 跳过的帧只解复用（grab）不完整解码，避免稀疏扫描浪费解码时间
            if not cap.grab():
                break
            continue
        ok, frame = cap.read()
        if not ok:
            break

        if frame_idx >= next_report_frame:
            elapsed = time.monotonic() - started_at
            percent = min(100, round(frame_idx * 100 / max(frame_count, 1)))
            print(f"  起脚定位扫描：{frame_idx}/{frame_count} 帧 ({percent}%)，耗时 {elapsed:.0f}s", flush=True)
            next_report_frame = frame_idx + report_every

        result = model.predict(frame, classes=[SPORTS_BALL_CLASS_ID],
                               conf=conf, imgsz=min(imgsz, DETECT_IMGSZ_CAP), verbose=False)[0]
        boxes = [] if result.boxes is None else list(result.boxes)
        chosen = pick_detection(boxes, penalty_center, penalty_center)
        gp = None
        if chosen is not None:
            gp = image_point_to_ground_world(chosen["foot"], config, ground_z=0.0)
            if gp is not None:
                x_ok = FIELD_X_LIMITS[0] <= gp[0] <= FIELD_X_LIMITS[1]
                y_ok = FIELD_Y_LIMITS[0] <= gp[1] <= FIELD_Y_LIMITS[1]
                if not (x_ok and y_ok):
                    gp = None

        sparse_dets.append({
            "frame_idx": frame_idx,
            "detection": chosen,
            "ground_point": gp,
        })

    if not sparse_dets:
        raise RuntimeError("稀疏扫描未检测到任何帧。")

    # estimate_kick_frame returns an index into the sparse_dets list
    sparse_kick_idx = estimate_kick_frame(sparse_dets)
    kick_frame = sparse_dets[sparse_kick_idx]["frame_idx"]

    # Collect history from sparse detections before kick for seeding
    history = []
    for det in sparse_dets:
        if det["frame_idx"] < kick_frame and det["detection"] is not None:
            history.append(det["detection"]["center"])
    if len(history) > 8:
        history = history[-8:]

    return kick_frame, history, SPARSE_EVERY, sparse_dets


TRACK_CACHE_DIR = WORKSPACE_DIR / "output" / "track_cache"


def _track_cache_path(video_path):
    # Different samples use identically named "recording.mp4", so key the cache
    # on the sample and camera directory levels as well to avoid collisions.
    vp = Path(video_path)
    return TRACK_CACHE_DIR / f"{vp.parent.parent.name}_{vp.parent.name}_{vp.stem}_track.npz"


def _track_cache_valid(cache_path, video_path, imgsz, conf):
    """Cache is valid only while the source video and detection settings match."""
    if not cache_path.exists() or not Path(video_path).exists():
        return False
    try:
        with np.load(cache_path, allow_pickle=True) as data:
            if float(data["video_mtime"]) != Path(video_path).stat().st_mtime:
                return False
            if int(data["imgsz"]) != int(imgsz) or float(data["conf"]) != float(conf):
                return False
        return True
    except Exception:
        return False


def _load_track_cache(cache_path, video_path, config):
    with np.load(cache_path, allow_pickle=True) as data:
        times = np.asarray(data["times"], dtype=np.float64)
        img = np.asarray(data["image_points"], dtype=np.float64)
        conf = np.asarray(data["confidences"], dtype=np.float64)
        cov = np.asarray(data["center_covariances"], dtype=np.float64)
        kick = int(data["kick_frame"]); fps = float(data["fps"])
        fw = int(data["frame_w"]); fh = int(data["frame_h"]); fc = int(data["frame_count"])
    detections = []
    for i, t in enumerate(times):
        frame_idx = kick + int(round(t * fps))
        cand = {"center": img[i].copy(), "conf": float(conf[i]),
                "center_covariance": cov[i].reshape(2, 2).copy()}
        detections.append({
            "frame_idx": frame_idx,
            "detection": {**cand, "bbox": [0, 0, 1, 1], "foot": img[i].copy(),
                          "candidates": [cand]},
            "ground_point": None,
        })
    return VideoTrack(video_path=Path(video_path), camera_name=config.name, fps=fps,
                      frame_count=fc, frame_size=(fw, fh), kick_frame=kick,
                      detections=detections, times=times, image_points=img,
                      confidences=conf, center_covariances=cov, path_diagnostics={})


def _save_track_cache(cache_path, video_path, track, imgsz, conf):
    TRACK_CACHE_DIR.mkdir(parents=True, exist_ok=True)
    np.savez(
        cache_path,
        times=track.times, image_points=track.image_points,
        confidences=track.confidences, center_covariances=track.center_covariances,
        kick_frame=track.kick_frame, fps=track.fps, frame_count=track.frame_count,
        frame_w=track.frame_size[0], frame_h=track.frame_size[1],
        video_mtime=Path(video_path).stat().st_mtime, imgsz=int(imgsz), conf=float(conf),
    )


def detect_video_track(video_path, config, model, imgsz, conf):
    cache_path = _track_cache_path(video_path)
    if _track_cache_valid(cache_path, video_path, imgsz, conf):
        try:
            track = _load_track_cache(cache_path, video_path, config)
            print(
                f"  [{config.name}] 命中检测缓存: {cache_path.name} "
                f"({len(track.times)} points) 跳过 YOLO",
                flush=True,
            )
            return track
        except Exception as exc:
            print(f"  [{config.name}] 检测缓存加载失败, 重新检测: {exc}", flush=True)

    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"无法打开视频: {video_path}")

    fps = float(cap.get(cv2.CAP_PROP_FPS)) or 60.0
    frame_count = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    frame_width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    frame_height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))
    penalty_center = project_world_points(PENALTY_SPOT_GROUND_WORLD.reshape(1, 3), config)[0]

    # ═══ Phase 1: 稀疏扫描锁定起脚帧 ═══
    kick_frame, history, sparse_every, _ = _sparse_scan_kick_frame(
        cap, fps, frame_count, penalty_center, model, config, imgsz, conf)

    # ═══ Phase 2: 密集跟踪（从起脚帧前开始） ═══
    LEAD_IN = max(sparse_every, int(fps * 0.3))
    start_frame = max(0, kick_frame - LEAD_IN)
    cap.set(cv2.CAP_PROP_POS_FRAMES, start_frame)

    detections = []
    consecutive_miss = 0
    MAX_MISS = 5
    frame_dt = 1.0 / max(fps, 1e-6)
    ball_filter = ConstantAccelerationFilter()
    frame_idx = start_frame - 1
    started_at = time.monotonic()
    last_report_time = 0.0
    next_report_frame = start_frame
    # 提高进度报告频率（每秒约 5 次），使进度条连续平滑增长；同时用时间
    # 节流，避免高帧率视频下 stdout 刷新过于频繁拖慢子进程通信。
    report_every = max(1, int(fps / 5))
    print(f"  密集跟踪：{start_frame}/{frame_count} 帧 (0%)", flush=True)

    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame_idx += 1

        if frame_idx >= next_report_frame:
            now = time.monotonic()
            if now - last_report_time >= 0.5:
                elapsed = now - started_at
                completed = frame_idx - start_frame
                total = max(frame_count - start_frame, 1)
                percent = min(100, round(completed * 100 / total))
                print(
                    f"  密集跟踪：{frame_idx}/{frame_count} 帧 ({percent}%)，"
                    f"已检测 {len(detections)} 帧，耗时 {elapsed:.0f}s",
                    flush=True,
                )
                last_report_time = now
            next_report_frame = frame_idx + report_every

        # --- predict next position: Kalman first, sparse-history diff fallback ---
        if ball_filter.initialized:
            predicted, velocity_mag = ball_filter.predict_next(frame_dt)
        else:
            predicted, velocity_mag = _predict_ball_position(history)
        if predicted is not None:
            expected_center = predicted
        else:
            expected_center = penalty_center

        # --- adaptive multi-scale detection ---
        chosen = _adaptive_ball_detect(model, frame, expected_center, penalty_center,
                                        imgsz, conf, frame_width, frame_height,
                                        velocity_mag=velocity_mag)

        # Fallback: full-frame YOLO if adaptive detection failed
        # 兜底只做粗分辨率检测即可，避免全帧 1280 推理拖慢整体速度
        if chosen is None:
            result = model.predict(frame, classes=[SPORTS_BALL_CLASS_ID],
                                   conf=conf, imgsz=min(imgsz, 640), verbose=False)[0]
            boxes = [] if result.boxes is None else list(result.boxes)
            fallback_candidates = rank_detections(
                boxes,
                np.asarray(expected_center, dtype=np.float64),
                np.asarray(penalty_center, dtype=np.float64),
            )
            chosen = fallback_candidates[0] if fallback_candidates else None
            if chosen is not None:
                chosen["candidates"] = [
                    {
                        "center": candidate["center"].copy(),
                        "conf": float(candidate["conf"]),
                        "center_covariance": np.asarray(candidate["center_covariance"], dtype=np.float64).copy(),
                    }
                    for candidate in fallback_candidates
                ]

        # --- process chosen detection ---
        ground_point = None
        if chosen is not None:
            history.append(chosen["center"])
            consecutive_miss = 0
            if len(history) > 8:
                history = history[-8:]
            ball_filter.update(chosen["center"], chosen.get("center_covariance"))
            ground_point = image_point_to_ground_world(chosen["foot"], config, ground_z=0.0)
            if ground_point is not None:
                x_ok = FIELD_X_LIMITS[0] <= ground_point[0] <= FIELD_X_LIMITS[1]
                y_ok = FIELD_Y_LIMITS[0] <= ground_point[1] <= FIELD_Y_LIMITS[1]
                if not (x_ok and y_ok):
                    ground_point = None
        else:
            consecutive_miss += 1
            if consecutive_miss > MAX_MISS:
                history.clear()
                ball_filter.reset()
                consecutive_miss = 0

        detections.append({
            "frame_idx": frame_idx,
            "time": frame_idx / fps,
            "detection": chosen,
            "ground_point": ground_point,
        })

    cap.release()
    print(f"  密集跟踪完成：{frame_count}/{frame_count} 帧，耗时 {time.monotonic() - started_at:.0f}s", flush=True)

    path_diagnostics = optimize_detection_path(
        detections,
        kick_frame,
        fps,
        (frame_width, frame_height),
    )
    if path_diagnostics.get("applied"):
        print(
            f"  {config.name} global candidate path: "
            f"{path_diagnostics['substituted_frames']}/{path_diagnostics['candidate_frames']} frames replaced",
            flush=True,
        )

    rel_times = []
    image_points = []
    confidences = []
    center_covariances = []
    for det in detections:
        chosen = det["detection"]
        if chosen is None or det["frame_idx"] < kick_frame:
            continue
        rel_times.append((det["frame_idx"] - kick_frame) / fps)
        image_points.append(chosen["center"].astype(np.float64))
        confidences.append(float(chosen["conf"]))
        center_covariances.append(np.asarray(
            chosen.get("center_covariance", np.eye(2, dtype=np.float64) * 9.0), dtype=np.float64
        ).reshape(2, 2))

    if not image_points:
        raise RuntimeError(f"未在视频中检测到可用的足球 2D 轨迹: {video_path}")

    track = VideoTrack(
        video_path=video_path,
        camera_name=config.name,
        fps=fps,
        frame_count=frame_count,
        frame_size=(frame_width, frame_height),
        kick_frame=kick_frame,
        detections=detections,
        times=np.asarray(rel_times, dtype=np.float64),
        image_points=np.asarray(image_points, dtype=np.float64),
        confidences=np.asarray(confidences, dtype=np.float64),
        center_covariances=np.asarray(center_covariances, dtype=np.float64),
        path_diagnostics=path_diagnostics,
    )
    try:
        _save_track_cache(cache_path, video_path, track, imgsz, conf)
        print(
            f"  [{config.name}] 已写入检测缓存: {_track_cache_path(video_path).name} "
            f"({len(track.times)} points)",
            flush=True,
        )
    except Exception as exc:
        print(f"  [{config.name}] 检测缓存写入失败: {exc}", flush=True)
    return track


def build_track_interpolators(track):
    if len(track.times) < 2:
        return None
    covariances = np.asarray(track.center_covariances, dtype=np.float64).reshape(-1, 2, 2)
    if len(covariances) != len(track.times):
        covariances = np.repeat(np.eye(2, dtype=np.float64)[None, :, :] * 9.0, len(track.times), axis=0)
    return {
        "t_min": float(track.times[0]),
        "t_max": float(track.times[-1]),
        "interp_x": interp1d(track.times, track.image_points[:, 0], kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_y": interp1d(track.times, track.image_points[:, 1], kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_conf": interp1d(track.times, track.confidences, kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_cov_xx": interp1d(track.times, covariances[:, 0, 0], kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_cov_xy": interp1d(track.times, covariances[:, 0, 1], kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_cov_yy": interp1d(track.times, covariances[:, 1, 1], kind="linear", bounds_error=False, fill_value=np.nan),
    }


def interpolate_center_covariances(interpolators, times):
    """Linearly propagate per-frame 2D measurement covariance in time."""
    times = np.asarray(times, dtype=np.float64)
    covariances = np.empty((len(times), 2, 2), dtype=np.float64)
    covariances[:, 0, 0] = interpolators["interp_cov_xx"](times)
    covariances[:, 0, 1] = interpolators["interp_cov_xy"](times)
    covariances[:, 1, 0] = covariances[:, 0, 1]
    covariances[:, 1, 1] = interpolators["interp_cov_yy"](times)
    return np.asarray([regularize_center_covariance(covariance) for covariance in covariances])


def _track_candidates_near_time(track, relative_time, fallback_point, fallback_confidence):
    """Return ranked detector candidates nearest to a track-relative time."""
    best = None
    best_distance = float("inf")
    tolerance = 0.60 / max(track.fps, 1.0)
    for detection in track.detections:
        if detection["detection"] is None:
            continue
        time_value = (detection["frame_idx"] - track.kick_frame) / track.fps
        distance = abs(time_value - relative_time)
        if distance < best_distance:
            best = detection["detection"]
            best_distance = distance

    if best is not None and best_distance <= tolerance:
        candidates = best.get("candidates") or [{
            "center": best["center"],
            "conf": best["conf"],
            "center_covariance": best.get("center_covariance", np.eye(2, dtype=np.float64) * 9.0),
        }]
        return candidates[:MAX_TRACK_CANDIDATES]
    return [{
        "center": np.asarray(fallback_point, dtype=np.float64),
        "conf": float(fallback_confidence),
        "center_covariance": np.eye(2, dtype=np.float64) * 9.0,
    }]


def select_cross_view_candidate_path(track_a, config_a, track_b, config_b, times, offset_seconds,
                                     fallback_a, fallback_b, fallback_conf_a, fallback_conf_b):
    """Viterbi-select a physically continuous path through cross-view candidates."""
    state_rows = []
    for index, time_value in enumerate(times):
        candidates_a = _track_candidates_near_time(
            track_a, float(time_value), fallback_a[index], fallback_conf_a[index])
        candidates_b = _track_candidates_near_time(
            track_b, float(time_value + offset_seconds), fallback_b[index], fallback_conf_b[index])
        states = []
        confidence_mass_a = sum(max(float(candidate["conf"]), 1e-6) for candidate in candidates_a)
        confidence_mass_b = sum(max(float(candidate["conf"]), 1e-6) for candidate in candidates_b)
        for candidate_a in candidates_a:
            for candidate_b in candidates_b:
                point_a = np.asarray(candidate_a["center"], dtype=np.float64)
                point_b = np.asarray(candidate_b["center"], dtype=np.float64)
                tri = triangulate_ball_point(
                    point_a,
                    config_a,
                    point_b,
                    config_b,
                    candidate_a.get("center_covariance"),
                    candidate_b.get("center_covariance"),
                )
                if tri is None:
                    continue
                states.append({
                    "point_a": point_a,
                    "point_b": point_b,
                    "confidence_a": float(candidate_a["conf"]),
                    "confidence_b": float(candidate_b["conf"]),
                    "covariance_a": np.asarray(candidate_a.get("center_covariance", np.eye(2) * 9.0), dtype=np.float64),
                    "covariance_b": np.asarray(candidate_b.get("center_covariance", np.eye(2) * 9.0), dtype=np.float64),
                    "world_point": tri["world_point"],
                    "ray_gap": float(tri["ray_gap"]),
                    "reprojection_error": float(tri["reprojection_error"]),
                })
        if not states:
            return None

        # Compare geometry only relative to the alternatives visible in this
        # frame and treat detector confidence as probability mass.  The old
        # absolute mixture made a low-confidence false detection competitive
        # whenever it shaved a small amount off ray gap.
        gap_scale = max(float(np.median([state["ray_gap"] for state in states])), 1e-6)
        reproj_scale = max(float(np.median([state["reprojection_error"] for state in states])), 1e-6)
        for state in states:
            prob_a = max(state["confidence_a"] / confidence_mass_a, 1e-6)
            prob_b = max(state["confidence_b"] / confidence_mass_b, 1e-6)
            detection_nll = -np.log(prob_a) - np.log(prob_b)
            absolute_confidence_nll = -0.20 * np.log(max(state["confidence_a"], 1e-6)) \
                                      -0.20 * np.log(max(state["confidence_b"], 1e-6))
            uncertainty_nll = 0.05 * (
                center_covariance_log_area(state["covariance_a"])
                + center_covariance_log_area(state["covariance_b"])
            )
            geometry_nll = (
                np.log1p(state["ray_gap"] / gap_scale)
                + 0.5 * np.log1p(state["reprojection_error"] / reproj_scale)
            )
            state["emission"] = float(
                detection_nll + absolute_confidence_nll + uncertainty_nll + geometry_nll
            )
        state_rows.append(states)

    def first_transition_batch(previous_states, current_states, dt):
        previous = np.asarray([s["world_point"] for s in previous_states], dtype=np.float64)
        current = np.asarray([s["world_point"] for s in current_states], dtype=np.float64)
        speed = np.linalg.norm(current[None, :, :] - previous[:, None, :], axis=-1) / dt
        return 0.15 * _huber_cost_array(np.maximum(0.0, speed - 35.0) / 8.0)

    def second_transition_batch(previous_previous_states, previous_states, current_states,
                                dt_previous, dt_current):
        previous_previous = np.asarray([s["world_point"] for s in previous_previous_states], dtype=np.float64)
        previous = np.asarray([s["world_point"] for s in previous_states], dtype=np.float64)
        current = np.asarray([s["world_point"] for s in current_states], dtype=np.float64)
        gravity = np.array([0.0, 0.0, -9.81], dtype=np.float64)
        predicted = (
            previous[None, :, None, :]
            + (previous[None, :, None, :] - previous_previous[:, None, None, :])
            * (dt_current / dt_previous)
            + 0.5 * gravity * dt_current * (dt_previous + dt_current)
        )
        ballistic_residual = np.linalg.norm(current[None, None, :, :] - predicted, axis=-1)
        speed = np.linalg.norm(current[None, None, :, :] - previous[None, :, None, :], axis=-1) / dt_current
        return (
            1.10 * _huber_cost_array(ballistic_residual / 0.25)
            + 0.15 * _huber_cost_array(np.maximum(0.0, speed - 35.0) / 8.0)
        )

    selected = second_order_viterbi(
        state_rows,
        times,
        [np.asarray([state["emission"] for state in states], dtype=np.float64) for states in state_rows],
        first_transition_batch,
        second_transition_batch,
    )
    if selected is None:
        return None
    path = [state_rows[row_index][state_index] for row_index, state_index in enumerate(selected)]
    return (
        np.asarray([state["point_a"] for state in path], dtype=np.float64),
        np.asarray([state["point_b"] for state in path], dtype=np.float64),
        np.asarray([state["confidence_a"] for state in path], dtype=np.float64),
        np.asarray([state["confidence_b"] for state in path], dtype=np.float64),
        np.asarray([state["covariance_a"] for state in path], dtype=np.float64),
        np.asarray([state["covariance_b"] for state in path], dtype=np.float64),
    )


def image_point_to_world_ray(image_point, config):
    pts = np.asarray(image_point, dtype=np.float64).reshape(1, 1, 2)
    undist = cv2.undistortPoints(pts, config.camera_matrix, config.dist_coeffs)
    x_norm, y_norm = undist.reshape(2)
    ray_cam = np.array([x_norm, y_norm, 1.0], dtype=np.float64)
    ray_world = config.rotation_matrix.T @ ray_cam
    ray_norm = np.linalg.norm(ray_world)
    if ray_norm < 1e-10:
        return None, None
    return config.camera_center_world, ray_world / ray_norm


def triangulate_ball_point(image_point_a, config_a, image_point_b, config_b,
                           covariance_a=None, covariance_b=None):
    """Triangulate by covariance-weighted nonlinear reprojection minimisation.

    The closest-point ray intersection is retained as a stable geometric
    initialisation and for its ray-gap diagnostic.  The returned 3D point is
    then refined in the image domain, where detector uncertainty is meaningful.
    """
    origin_a, ray_a = image_point_to_world_ray(image_point_a, config_a)
    origin_b, ray_b = image_point_to_world_ray(image_point_b, config_b)
    if origin_a is None or origin_b is None:
        return None

    w0 = origin_a - origin_b
    a = float(np.dot(ray_a, ray_a))
    b = float(np.dot(ray_a, ray_b))
    c = float(np.dot(ray_b, ray_b))
    d = float(np.dot(ray_a, w0))
    e = float(np.dot(ray_b, w0))
    denom = a * c - b * b
    if abs(denom) < 1e-10:
        return None

    scale_a = (b * e - c * d) / denom
    scale_b = (a * e - b * d) / denom
    if scale_a <= 0.0 or scale_b <= 0.0:
        return None

    point_a = origin_a + scale_a * ray_a
    point_b = origin_b + scale_b * ray_b
    world_point = 0.5 * (point_a + point_b)
    ray_gap = float(np.linalg.norm(point_a - point_b))

    cam_a = config_a.rotation_matrix @ world_point + config_a.tvec.reshape(3)
    cam_b = config_b.rotation_matrix @ world_point + config_b.tvec.reshape(3)
    if cam_a[2] <= 0.0 or cam_b[2] <= 0.0:
        return None

    x_ok = FIELD_X_LIMITS[0] - 5.0 <= world_point[0] <= FIELD_X_LIMITS[1] + 5.0
    y_ok = FIELD_Y_LIMITS[0] - 5.0 <= world_point[1] <= FIELD_Y_LIMITS[1] + 5.0
    z_ok = WORLD_Z_LIMITS[0] <= world_point[2] <= WORLD_Z_LIMITS[1]
    if not (x_ok and y_ok and z_ok):
        return None

    covariance_a = regularize_center_covariance(
        np.eye(2, dtype=np.float64) * 9.0 if covariance_a is None else covariance_a)
    covariance_b = regularize_center_covariance(
        np.eye(2, dtype=np.float64) * 9.0 if covariance_b is None else covariance_b)

    def residual_vector(candidate):
        projected_a = project_world_points(candidate.reshape(1, 3), config_a)
        projected_b = project_world_points(candidate.reshape(1, 3), config_b)
        residual_a = whiten_pixel_residuals(projected_a - image_point_a, covariance_a[None, :, :])
        residual_b = whiten_pixel_residuals(projected_b - image_point_b, covariance_b[None, :, :])
        return np.hstack([residual_a.ravel(), residual_b.ravel()])

    try:
        result = least_squares(
            residual_vector,
            world_point,
            loss="linear",
            max_nfev=24,
        )
        if result.success and np.all(np.isfinite(result.x)):
            refined_point = result.x
            refined_cam_a = config_a.rotation_matrix @ refined_point + config_a.tvec.reshape(3)
            refined_cam_b = config_b.rotation_matrix @ refined_point + config_b.tvec.reshape(3)
            if refined_cam_a[2] > 0.0 and refined_cam_b[2] > 0.0:
                refined_x_ok = FIELD_X_LIMITS[0] - 5.0 <= refined_point[0] <= FIELD_X_LIMITS[1] + 5.0
                refined_y_ok = FIELD_Y_LIMITS[0] - 5.0 <= refined_point[1] <= FIELD_Y_LIMITS[1] + 5.0
                refined_z_ok = WORLD_Z_LIMITS[0] <= refined_point[2] <= WORLD_Z_LIMITS[1]
                if refined_x_ok and refined_y_ok and refined_z_ok:
                    world_point = refined_point
    except (ValueError, np.linalg.LinAlgError):
        pass

    reproj_a = project_world_points(world_point.reshape(1, 3), config_a)[0]
    reproj_b = project_world_points(world_point.reshape(1, 3), config_b)[0]
    reproj_error_a = float(np.linalg.norm(reproj_a - image_point_a))
    reproj_error_b = float(np.linalg.norm(reproj_b - image_point_b))

    return {
        "world_point": world_point,
        "ray_gap": ray_gap,
        "reprojection_error": 0.5 * (reproj_error_a + reproj_error_b),
        "reprojection_errors": [reproj_error_a, reproj_error_b],
        "normalized_reprojection_error": float(0.5 * (
            np.linalg.norm(whiten_pixel_residuals((reproj_a - image_point_a).reshape(1, 2), covariance_a[None, :, :]))
            + np.linalg.norm(whiten_pixel_residuals((reproj_b - image_point_b).reshape(1, 2), covariance_b[None, :, :]))
        )),
    }


def estimate_ballistic_consistency_residual(times, points):
    """Return a robust 3D residual for a pure-gravity trajectory candidate."""
    times = np.asarray(times, dtype=np.float64)
    points = np.asarray(points, dtype=np.float64)
    if len(times) < 6:
        return float("inf")

    tau = times - float(times[0])
    design = np.column_stack([np.ones(len(tau), dtype=np.float64), tau])
    targets = np.column_stack([
        points[:, 0],
        points[:, 1],
        points[:, 2] + 0.5 * 9.81 * tau * tau,
    ])
    mask = np.all(np.isfinite(targets), axis=1)
    if np.count_nonzero(mask) < 6:
        return float("inf")

    residuals = None
    for _ in range(3):
        coefficients, _, _, _ = np.linalg.lstsq(design[mask], targets[mask], rcond=None)
        fitted = design @ coefficients
        residuals = np.linalg.norm(targets - fitted, axis=1)
        robust_scale = max(0.03, float(np.percentile(residuals[mask], 75)))
        new_mask = np.isfinite(residuals) & (residuals <= robust_scale * 1.5)
        if np.count_nonzero(new_mask) < 6 or np.array_equal(new_mask, mask):
            break
        mask = new_mask

    return float(np.percentile(residuals[np.isfinite(residuals)], 75))


def select_alignment_flight_prefix_length(times, points):
    """Choose the longest leading segment still consistent with one flight arc."""
    if len(times) <= 24:
        return len(times)

    candidate_ends = list(range(12, len(times) + 1, 4))
    if candidate_ends[-1] != len(times):
        candidate_ends.append(len(times))
    residuals = [estimate_ballistic_consistency_residual(times[:end], points[:end]) for end in candidate_ends]
    best_residual = min(residuals)
    # Keep a useful amount of the ascending/airborne segment while excluding
    # landing, rebound, and rollout observations from the one-flight model.
    acceptable_residual = max(0.20, min(0.45, best_residual * 2.5))
    accepted = [end for end, residual in zip(candidate_ends, residuals) if residual <= acceptable_residual]
    return max(accepted) if accepted else candidate_ends[int(np.argmin(residuals))]


def estimate_joint_reprojection_threshold(errors_px, config_a, config_b):
    """Estimate a detector- and resolution-independent consensus tolerance.

    Pixel errors are converted to image-plane angle before estimating a robust
    scale. This keeps the consensus decision comparable when cameras, focal
    lengths, or input resolutions change, without relying on a hand-tuned
    absolute pixel cut-off for any particular sample.
    """
    errors_px = np.asarray(errors_px, dtype=np.float64)
    finite = errors_px[np.isfinite(errors_px)]
    focal_px = float(np.mean([
        config_a.camera_matrix[0, 0], config_a.camera_matrix[1, 1],
        config_b.camera_matrix[0, 0], config_b.camera_matrix[1, 1],
    ]))
    focal_px = max(focal_px, 1.0)
    if len(finite) == 0:
        return 3.0

    angular_errors = finite / focal_px
    median = float(np.median(angular_errors))
    mad_scale = 1.4826 * float(np.median(np.abs(angular_errors - median)))
    # One pixel is the quantisation floor; the remaining scale comes from the
    # actual detector/camera pair rather than an absolute pixel cut-off.
    angular_scale = max(1.0 / focal_px, mad_scale)
    return float((median + 2.8 * angular_scale) * focal_px)


def fit_joint_ballistic_reprojection(times, world_points, image_points_a, image_points_b,
                                     confidences, config_a, config_b, use_ransac=False,
                                     covariances_a=None, covariances_b=None):
    """Fit one gravity trajectory directly to both image tracks for one offset."""
    times = np.asarray(times, dtype=np.float64)
    world_points = np.asarray(world_points, dtype=np.float64)
    tau = times - float(times[0])
    design = np.column_stack([np.ones(len(tau), dtype=np.float64), tau])
    targets = np.column_stack([
        world_points[:, 0],
        world_points[:, 1],
        world_points[:, 2] + 0.5 * 9.81 * tau * tau,
    ])
    coefficients, _, _, _ = np.linalg.lstsq(design, targets, rcond=None)
    initial = np.array([
        coefficients[0, 0], coefficients[1, 0],
        coefficients[0, 1], coefficients[1, 1],
        coefficients[0, 2], coefficients[1, 2],
    ], dtype=np.float64)
    point_weights = np.clip(np.sqrt(np.prod(confidences, axis=1)), 0.10, 1.0)
    if covariances_a is None:
        covariances_a = np.repeat(np.eye(2, dtype=np.float64)[None, :, :], len(times), axis=0)
    if covariances_b is None:
        covariances_b = np.repeat(np.eye(2, dtype=np.float64)[None, :, :], len(times), axis=0)
    covariances_a = np.asarray([regularize_center_covariance(covariance)
                                for covariance in covariances_a], dtype=np.float64)
    covariances_b = np.asarray([regularize_center_covariance(covariance)
                                for covariance in covariances_b], dtype=np.float64)

    def evaluate(params):
        return np.column_stack([
            params[0] + params[1] * tau,
            params[2] + params[3] * tau,
            params[4] + params[5] * tau - 0.5 * 9.81 * tau * tau,
        ])

    def residual_vector(params, indices):
        predicted = evaluate(params)
        projected_a = project_world_points(predicted, config_a)
        projected_b = project_world_points(predicted, config_b)
        residuals = np.hstack([
            whiten_pixel_residuals(projected_a - image_points_a, covariances_a),
            whiten_pixel_residuals(projected_b - image_points_b, covariances_b),
        ])
        return (residuals[indices] * np.sqrt(point_weights[indices])[:, None]).ravel()

    lower = np.array([-15.0, -45.0, -20.0, -65.0, -1.0, -25.0])
    upper = np.array([15.0, 45.0, 20.0, 25.0, 8.0, 25.0])
    def seed_from_indices(indices):
        seed_coefficients, _, _, _ = np.linalg.lstsq(design[indices], targets[indices], rcond=None)
        return np.array([
            seed_coefficients[0, 0], seed_coefficients[1, 0],
            seed_coefficients[0, 1], seed_coefficients[1, 1],
            seed_coefficients[0, 2], seed_coefficients[1, 2],
        ], dtype=np.float64)

    def solve(seed, indices, max_nfev):
        try:
            return least_squares(
                lambda params: residual_vector(params, indices),
                np.clip(seed, lower, upper),
                bounds=(lower, upper),
                loss="soft_l1",
                f_scale=2.5,
                max_nfev=max_nfev,
            ).x
        except (ValueError, np.linalg.LinAlgError):
            return np.clip(seed, lower, upper)

    def calculate_errors(params):
        predicted = evaluate(params)
        error_a = np.linalg.norm(project_world_points(predicted, config_a) - image_points_a, axis=1)
        error_b = np.linalg.norm(project_world_points(predicted, config_b) - image_points_b, axis=1)
        return predicted, 0.5 * (error_a + error_b)

    all_indices = np.arange(len(times))
    params = solve(initial, all_indices, max_nfev=48)
    _, preliminary_errors = calculate_errors(params)
    consensus_threshold = estimate_joint_reprojection_threshold(
        preliminary_errors, config_a, config_b)
    if use_ransac and len(times) >= 8:
        rng = np.random.default_rng(20260820)
        best_params = params
        best_errors = preliminary_errors
        best_mask = best_errors <= consensus_threshold
        best_key = (int(np.count_nonzero(best_mask)), -float(np.median(best_errors[best_mask])) if np.any(best_mask) else -float("inf"))
        subset_size = min(7, len(times))
        for _ in range(28):
            subset = np.sort(rng.choice(all_indices, size=subset_size, replace=False))
            candidate_params = solve(seed_from_indices(subset), subset, max_nfev=20)
            _, candidate_errors = calculate_errors(candidate_params)
            candidate_mask = candidate_errors <= consensus_threshold
            candidate_key = (
                int(np.count_nonzero(candidate_mask)),
                -float(np.median(candidate_errors[candidate_mask])) if np.any(candidate_mask) else -float("inf"),
            )
            if candidate_key > best_key:
                best_params = candidate_params
                best_mask = candidate_mask
                best_key = candidate_key
        if np.count_nonzero(best_mask) >= 6:
            params = solve(best_params, np.where(best_mask)[0], max_nfev=48)

    predicted, errors = calculate_errors(params)
    final_threshold = estimate_joint_reprojection_threshold(errors, config_a, config_b)
    return predicted, errors, int(np.count_nonzero(errors <= final_threshold)), params


def refine_joint_time_offset(times, image_points_a, confidences_a, covariances_a,
                             interp_b, config_a, config_b, initial_offset, initial_params,
                             max_offset_step):
    """Continuously refine offset and gravity parameters in the pixel domain."""
    times = np.asarray(times, dtype=np.float64)
    image_points_a = np.asarray(image_points_a, dtype=np.float64)
    confidences_a = np.asarray(confidences_a, dtype=np.float64)
    covariances_a = np.asarray([regularize_center_covariance(covariance)
                                for covariance in covariances_a], dtype=np.float64)
    tau = times - float(times[0])
    lower = np.array([-15.0, -45.0, -20.0, -65.0, -1.0, -25.0, initial_offset - max_offset_step])
    upper = np.array([15.0, 45.0, 20.0, 25.0, 8.0, 25.0, initial_offset + max_offset_step])

    def residual_vector(values):
        params = values[:6]
        offset = float(values[6])
        image_points_b = np.column_stack([
            interp_b["interp_x"](times + offset),
            interp_b["interp_y"](times + offset),
        ])
        confidences_b = interp_b["interp_conf"](times + offset)
        covariances_b = interpolate_center_covariances(interp_b, times + offset)
        valid = np.all(np.isfinite(image_points_b), axis=1) & np.isfinite(confidences_b)
        if np.count_nonzero(valid) < 6:
            return np.full(len(times) * 4, 1e4, dtype=np.float64)
        predicted = np.column_stack([
            params[0] + params[1] * tau,
            params[2] + params[3] * tau,
            params[4] + params[5] * tau - 0.5 * 9.81 * tau * tau,
        ])
        projected_a = project_world_points(predicted, config_a)
        projected_b = project_world_points(predicted, config_b)
        residuals = np.hstack([
            whiten_pixel_residuals(projected_a - image_points_a, covariances_a),
            whiten_pixel_residuals(projected_b - image_points_b, covariances_b),
        ])
        weights = np.clip(np.sqrt(confidences_a * confidences_b), 0.10, 1.0)
        residuals = residuals * np.sqrt(weights)[:, None]
        residuals[~valid] = 1e4
        return residuals.ravel()

    initial = np.append(np.asarray(initial_params, dtype=np.float64), float(initial_offset))
    try:
        result = least_squares(
            residual_vector,
            np.clip(initial, lower, upper),
            bounds=(lower, upper),
            loss="soft_l1",
            f_scale=2.5,
            max_nfev=80,
        )
        return float(result.x[6])
    except (ValueError, np.linalg.LinAlgError):
        return float(initial_offset)


def joint_ballistic_normalized_nll(predicted_points, image_points_a, image_points_b,
                                   covariances_a, covariances_b, config_a, config_b):
    """Robust image-domain likelihood of one predicted 3D flight segment."""
    normalized_errors = 0.5 * (
        np.linalg.norm(whiten_pixel_residuals(
            project_world_points(predicted_points, config_a) - image_points_a,
            covariances_a,
        ), axis=1)
        + np.linalg.norm(whiten_pixel_residuals(
            project_world_points(predicted_points, config_b) - image_points_b,
            covariances_b,
        ), axis=1)
    )
    return float(np.mean([_huber_cost(error / 2.5) for error in normalized_errors]))


def _evaluate_gravity_parameters(params, times, time_origin):
    tau = np.asarray(times, dtype=np.float64) - float(time_origin)
    return np.column_stack([
        params[0] + params[1] * tau,
        params[2] + params[3] * tau,
        params[4] + params[5] * tau - 0.5 * 9.81 * tau * tau,
    ])


def evaluate_cross_validated_ballistic_alignment(candidate, config_a, config_b):
    """Validate one globally fitted trajectory on held-out temporal blocks.

    Every fold estimates one gravity trajectory from the other observations
    and evaluates it on an unseen contiguous block.  Unlike independent
    window fits, an incorrect offset cannot hide behind a different set of
    trajectory parameters for each validation block.
    """
    metrics = candidate["alignment_metrics"]
    prefix_length = int(metrics["joint_flight_prefix_points"])
    if prefix_length < 12:
        return {
            "fold_count": 1,
            "validation_normalized_nll": float(metrics["joint_ballistic_normalized_nll"]),
            "validation_nll_spread": 0.0,
            "validation_support_fraction": float(metrics["joint_ballistic_support_fraction"]),
            "score": float(metrics["joint_ballistic_normalized_nll"]),
        }

    indices = np.arange(prefix_length)
    folds = [fold for fold in np.array_split(indices, 3) if len(fold) > 0]
    validation_nlls = []
    validation_supports = []
    for validation_indices in folds:
        training_mask = np.ones(prefix_length, dtype=bool)
        training_mask[validation_indices] = False
        training_indices = indices[training_mask]
        if len(training_indices) < 8:
            continue
        train_times = candidate["times"][training_indices]
        _, _, _, params = fit_joint_ballistic_reprojection(
            train_times,
            candidate["world_points"][training_indices],
            candidate["image_points_left"][training_indices],
            candidate["image_points_right"][training_indices],
            np.column_stack([
                candidate["confidences_left"][training_indices],
                candidate["confidences_right"][training_indices],
            ]),
            config_a,
            config_b,
            covariances_a=candidate["center_covariances_left"][training_indices],
            covariances_b=candidate["center_covariances_right"][training_indices],
        )
        predicted = _evaluate_gravity_parameters(
            params, candidate["times"][validation_indices], train_times[0])
        validation_nlls.append(joint_ballistic_normalized_nll(
            predicted,
            candidate["image_points_left"][validation_indices],
            candidate["image_points_right"][validation_indices],
            candidate["center_covariances_left"][validation_indices],
            candidate["center_covariances_right"][validation_indices],
            config_a,
            config_b,
        ))
        train_predicted = _evaluate_gravity_parameters(params, train_times, train_times[0])
        train_errors = 0.5 * (
            np.linalg.norm(whiten_pixel_residuals(
                project_world_points(train_predicted, config_a)
                - candidate["image_points_left"][training_indices],
                candidate["center_covariances_left"][training_indices],
            ), axis=1)
            + np.linalg.norm(whiten_pixel_residuals(
                project_world_points(train_predicted, config_b)
                - candidate["image_points_right"][training_indices],
                candidate["center_covariances_right"][training_indices],
            ), axis=1)
        )
        median = float(np.median(train_errors))
        mad = 1.4826 * float(np.median(np.abs(train_errors - median)))
        threshold = max(1.0, median + 2.8 * mad)
        validation_predicted_a = project_world_points(predicted, config_a)
        validation_predicted_b = project_world_points(predicted, config_b)
        validation_errors = 0.5 * (
            np.linalg.norm(whiten_pixel_residuals(
                validation_predicted_a - candidate["image_points_left"][validation_indices],
                candidate["center_covariances_left"][validation_indices],
            ), axis=1)
            + np.linalg.norm(whiten_pixel_residuals(
                validation_predicted_b - candidate["image_points_right"][validation_indices],
                candidate["center_covariances_right"][validation_indices],
            ), axis=1)
        )
        validation_supports.append(float(np.mean(validation_errors <= threshold)))

    if not validation_nlls:
        return {
            "fold_count": 1,
            "validation_normalized_nll": float(metrics["joint_ballistic_normalized_nll"]),
            "validation_nll_spread": 0.0,
            "validation_support_fraction": float(metrics["joint_ballistic_support_fraction"]),
            "score": float(metrics["joint_ballistic_normalized_nll"]),
        }

    nll = float(np.mean(validation_nlls))
    nll_spread = float(np.percentile(validation_nlls, 75) - np.percentile(validation_nlls, 25))
    return {
        "fold_count": int(len(validation_nlls)),
        "validation_normalized_nll": nll,
        "validation_nll_spread": nll_spread,
        "validation_support_fraction": float(np.mean(validation_supports)),
        # The validation likelihood is primary.  Dispersion only resolves
        # near-ties, so it cannot make a poorer held-out fit win.
        "score": nll + 0.02 * nll_spread,
    }


def build_triangulation_candidate(track_a, config_a, track_b, config_b, offset_seconds,
                                  interp_a=None, interp_b=None, use_pixel_ransac=False,
                                  use_multi_hypothesis=False):
    interp_a = build_track_interpolators(track_a) if interp_a is None else interp_a
    interp_b = build_track_interpolators(track_b) if interp_b is None else interp_b
    if interp_a is None or interp_b is None:
        return None

    dt = 1.0 / max(track_a.fps, track_b.fps)
    t_start = max(interp_a["t_min"], interp_b["t_min"] - offset_seconds)
    t_end = min(interp_a["t_max"], interp_b["t_max"] - offset_seconds)
    if not np.isfinite(t_start) or not np.isfinite(t_end) or t_end <= t_start:
        return None

    times = np.arange(t_start, t_end + dt * 0.5, dt)
    if len(times) == 0:
        return None

    # 向量化插值：一次求出整条轨迹的左右 2D 点与置信度，避免逐点标量调用
    points_a = np.column_stack([
        interp_a["interp_x"](times), interp_a["interp_y"](times)]).astype(np.float64)
    points_b = np.column_stack([
        interp_b["interp_x"](times + offset_seconds),
        interp_b["interp_y"](times + offset_seconds)]).astype(np.float64)
    finite = np.all(np.isfinite(points_a), axis=1) & np.all(np.isfinite(points_b), axis=1)
    conf_a = interp_a["interp_conf"](times)
    conf_b = interp_b["interp_conf"](times + offset_seconds)
    cov_a = interpolate_center_covariances(interp_a, times)
    cov_b = interpolate_center_covariances(interp_b, times + offset_seconds)

    if use_multi_hypothesis:
        selected_path = select_cross_view_candidate_path(
            track_a,
            config_a,
            track_b,
            config_b,
            times,
            offset_seconds,
            points_a,
            points_b,
            conf_a,
            conf_b,
        )
        if selected_path is not None:
            points_a, points_b, conf_a, conf_b, cov_a, cov_b = selected_path

    tri_times = []
    world_points = []
    ray_gaps = []
    reprojection_errors = []
    image_points_left = []
    image_points_right = []
    conf_pairs = []
    covariance_pairs_a = []
    covariance_pairs_b = []
    for idx in np.where(finite)[0]:
        point_a = points_a[idx]
        point_b = points_b[idx]
        tri = triangulate_ball_point(
            point_a, config_a, point_b, config_b, cov_a[idx], cov_b[idx])
        if tri is None:
            continue

        tri_times.append(float(times[idx]))
        world_points.append(tri["world_point"])
        ray_gaps.append(tri["ray_gap"])
        reprojection_errors.append(tri["reprojection_error"])
        image_points_left.append(point_a)
        image_points_right.append(point_b)
        conf_pairs.append((float(conf_a[idx]), float(conf_b[idx])))
        covariance_pairs_a.append(cov_a[idx])
        covariance_pairs_b.append(cov_b[idx])

    if len(world_points) < 8:
        return None

    world_points = np.asarray(world_points, dtype=np.float64)
    ray_gaps = np.asarray(ray_gaps, dtype=np.float64)
    reprojection_errors = np.asarray(reprojection_errors, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    confidences = np.asarray(conf_pairs, dtype=np.float64)
    center_covariances_a = np.asarray(covariance_pairs_a, dtype=np.float64)
    center_covariances_b = np.asarray(covariance_pairs_b, dtype=np.float64)

    # A valid time offset must explain the *same ball centre* in both images.
    # Ray separation alone is insufficient: with an imperfect calibration it can
    # prefer a smooth but visibly misprojected correspondence.  Use robust
    # percentiles so a short detection loss cannot dominate the alignment.
    accel = np.linalg.norm(np.diff(world_points, n=2, axis=0), axis=1) if len(world_points) >= 3 else np.array([], dtype=np.float64)
    ballistic_p75_residual = estimate_ballistic_consistency_residual(
        np.asarray(tri_times, dtype=np.float64), world_points)
    joint_prefix_length = select_alignment_flight_prefix_length(
        np.asarray(tri_times, dtype=np.float64), world_points)
    joint_predicted, joint_reprojection_errors, joint_inlier_count, joint_params = fit_joint_ballistic_reprojection(
        np.asarray(tri_times[:joint_prefix_length], dtype=np.float64),
        world_points[:joint_prefix_length],
        image_points_left[:joint_prefix_length],
        image_points_right[:joint_prefix_length],
        confidences[:joint_prefix_length],
        config_a,
        config_b,
        use_ransac=use_pixel_ransac,
        covariances_a=center_covariances_a[:joint_prefix_length],
        covariances_b=center_covariances_b[:joint_prefix_length],
    )
    gap_median, gap_p75, gap_p90 = np.percentile(ray_gaps, [50, 75, 90])
    reproj_median, reproj_p75, reproj_p90 = np.percentile(reprojection_errors, [50, 75, 90])
    joint_reproj_median, joint_reproj_p75, joint_reproj_p90 = np.percentile(
        joint_reprojection_errors, [50, 75, 90])
    # This is the negative log-likelihood proxy of a *single gravity arc* in
    # both images.  Unlike ray gap, it cannot be improved merely by pairing
    # points that are locally close but belong to different flight instants.
    joint_ballistic_nll = joint_ballistic_normalized_nll(
        joint_predicted,
        image_points_left[:joint_prefix_length],
        image_points_right[:joint_prefix_length],
        center_covariances_a[:joint_prefix_length],
        center_covariances_b[:joint_prefix_length],
        config_a,
        config_b,
    )
    accel_p90 = float(np.percentile(accel, 90)) if len(accel) else 0.0
    support_fraction = float(joint_inlier_count) / max(float(joint_prefix_length), 1.0)
    # Time alignment is a model-selection problem: select the offset that has
    # the best robust likelihood under one physically valid flight, rather
    # than the offset with the smallest local ray separation.  The two weak
    # terms only resolve near-equal likelihoods in favour of broader support
    # and a smoother triangulated seed; they cannot override a worse arc.
    score = (
        joint_ballistic_nll
        - 0.02 * support_fraction
        + 0.002 * ballistic_p75_residual
        + 0.0005 * accel_p90
    )
    # Preserve the pre-experimental synchronisation objective as a stable
    # baseline.  A learned/weighted alternative is allowed to replace it only
    # after it proves itself on held-out temporal observations.
    # Stereo time alignment is fundamentally a geometric problem: the correct
    # offset places the *same physical ball centre* in both images, so on real
    # samples the winning offset is the one whose triangulated midpoint
    # re-projects into both cameras with the smallest error and ray separation.
    # The earlier score blended in acceleration / ballistic-curve / support /
    # confidence terms that could reward a smooth but temporally misaligned arc;
    # those are removed so the alignment objective cannot be bought by prettier
    # arc statistics.  (joint reprojection is retained as a light geometric tie.)
    geometric_score = (
        1.0 * float(gap_median)
        + 0.30 * float(gap_p75)
        + 0.10 * float(gap_p90)
        + 1.0 * float(reproj_median)
        + 0.40 * float(reproj_p75)
        + 0.15 * float(reproj_p90)
        + 0.30 * float(joint_reproj_median)
        + 0.10 * float(joint_reproj_p75)
    )

    return {
        "times": np.asarray(tri_times, dtype=np.float64),
        "world_points": world_points,
        "ray_gaps": ray_gaps,
        "reprojection_errors": reprojection_errors,
        "image_points_left": image_points_left,
        "image_points_right": image_points_right,
        "confidences_left": confidences[:, 0],
        "confidences_right": confidences[:, 1],
        "center_covariances_left": center_covariances_a,
        "center_covariances_right": center_covariances_b,
        "alignment_metrics": {
            "ray_gap_median_m": float(gap_median),
            "ray_gap_p90_m": float(gap_p90),
            "reprojection_median_px": float(reproj_median),
            "reprojection_p90_px": float(reproj_p90),
            "joint_reprojection_median_px": float(joint_reproj_median),
            "joint_reprojection_p90_px": float(joint_reproj_p90),
            "joint_reprojection_inlier_count": joint_inlier_count,
            "joint_flight_prefix_points": joint_prefix_length,
            "joint_ballistic_params": joint_params,
            "joint_ballistic_normalized_nll": joint_ballistic_nll,
            "joint_ballistic_support_fraction": support_fraction,
            "legacy_geometric_score": geometric_score,
            "acceleration_p90_m": accel_p90,
            "ballistic_p75_residual_m": ballistic_p75_residual,
        },
        "score": score,
        "geometric_score": geometric_score,
    }


def find_best_time_offset(track_a, config_a, track_b, config_b):
    fps_max = max(track_a.fps, track_b.fps)
    step = 1.0 / (4.0 * fps_max)  # finer step: 1/4 frame
    search_radius = 15.0 / fps_max  # keep correspondences within ±15 frames
    offsets = np.arange(-search_radius, search_radius + step * 0.5, step)

    # 插值器只依赖轨迹本身、与 offset 无关，循环外构建一次复用
    interp_a = build_track_interpolators(track_a)
    interp_b = build_track_interpolators(track_b)

    coarse_candidates = []
    for offset in offsets:
        candidate = build_triangulation_candidate(
            track_a, config_a, track_b, config_b, float(offset),
            interp_a=interp_a, interp_b=interp_b)
        if candidate is None:
            continue
        coarse_candidates.append((float(offset), candidate))

    if not coarse_candidates:
        raise RuntimeError("无法在双机位之间找到可用的时间对齐结果。")

    # The established geometric objective remains a conservative baseline.
    # A covariance-aware offset must beat it on unseen temporal observations;
    # otherwise the baseline is retained.
    baseline_offset, baseline_candidate = min(
        coarse_candidates, key=lambda item: item[1]["geometric_score"])

    # A full-flight likelihood cheaply narrows the wide offset search.  The
    # square-root schedule keeps the cross-validation work bounded while
    # adapting automatically to camera frame rate and search resolution.
    shortlist_size = min(
        len(coarse_candidates),
        max(7, int(np.ceil(np.sqrt(len(coarse_candidates))))),
    )
    shortlisted = sorted(coarse_candidates, key=lambda item: item[1]["score"])[:shortlist_size]
    if not any(candidate is baseline_candidate for _, candidate in shortlisted):
        shortlisted.append((baseline_offset, baseline_candidate))
    for _, candidate in shortlisted:
        candidate["alignment_metrics"]["cross_validated_consensus"] = evaluate_cross_validated_ballistic_alignment(
            candidate, config_a, config_b)

    baseline_consensus = baseline_candidate["alignment_metrics"]["cross_validated_consensus"]
    # On real samples the joint-ballistic cross-validation term dropped the
    # offset onto a temporally misaligned arc, so the final offset is chosen
    # purely on stereo geometric alignment (the geometric_score minimiser).  The
    # cross-validation score is retained only as a diagnostic, not a decision.
    best_offset, best_candidate = baseline_offset, baseline_candidate
    selection_mode = "geometric_alignment"

    metrics = best_candidate["alignment_metrics"]
    metrics["time_offset_selection"] = {
        "mode": selection_mode,
        "baseline_offset_seconds": float(baseline_offset),
        "selected_offset_seconds": float(best_offset),
        "baseline_validation_score": float(baseline_consensus["score"]),
        "selected_validation_score": float(
            metrics["cross_validated_consensus"]["score"]),
    }
    prefix_length = int(metrics["joint_flight_prefix_points"])
    refined_offset = refine_joint_time_offset(
        best_candidate["times"][:prefix_length],
        best_candidate["image_points_left"][:prefix_length],
        best_candidate["confidences_left"][:prefix_length],
        best_candidate["center_covariances_left"][:prefix_length],
        interp_b,
        config_a,
        config_b,
        best_offset,
        metrics["joint_ballistic_params"],
        max_offset_step=1.0 / fps_max,
    )
    refined_candidate = build_triangulation_candidate(
        track_a,
        config_a,
        track_b,
        config_b,
        refined_offset,
        interp_a=interp_a,
        interp_b=interp_b,
        use_pixel_ransac=True,
        use_multi_hypothesis=True,
    )
    if refined_candidate is not None:
        refined_metrics = refined_candidate["alignment_metrics"]
        original_consensus = metrics["cross_validated_consensus"]
        refined_consensus = evaluate_cross_validated_ballistic_alignment(
            refined_candidate, config_a, config_b)
        refined_metrics["cross_validated_consensus"] = refined_consensus
        # A sub-frame optimisation is accepted only when it does not worsen the
        # pure stereo geometric alignment (re-projection + ray separation);
        # the joint-ballistic likelihood is diagnostic only.
        if float(refined_candidate["geometric_score"]) <= float(best_candidate["geometric_score"]):
            best_offset = refined_offset
            best_candidate = refined_candidate
            refined_metrics["time_offset_selection"] = {
                "mode": "geometric_alignment_refined",
                "baseline_offset_seconds": float(baseline_offset),
                "selected_offset_seconds": float(best_offset),
                "baseline_validation_score": float(baseline_consensus["score"]),
                "selected_validation_score": float(refined_consensus["score"]),
            }

    return best_offset, best_candidate


def build_3d_trajectory(track_a, config_a, track_b, config_b):
    offset_seconds, candidate = find_best_time_offset(track_a, config_a, track_b, config_b)

    raw_times = candidate["times"].copy()
    raw_world_points = candidate["world_points"].copy()
    raw_ray_gaps = candidate["ray_gaps"].copy()
    raw_reprojection_errors = candidate["reprojection_errors"].copy()
    raw_image_points_left = candidate["image_points_left"].copy()
    raw_image_points_right = candidate["image_points_right"].copy()
    raw_confidences_left = candidate["confidences_left"].copy()
    raw_confidences_right = candidate["confidences_right"].copy()

    # Stage 1: IQR-based outlier rejection on reprojection error (robust to extreme values)
    n = len(raw_reprojection_errors)
    if n >= 8:
        q1_reproj = float(np.percentile(raw_reprojection_errors, 25))
        q3_reproj = float(np.percentile(raw_reprojection_errors, 75))
        iqr_reproj = max(q3_reproj - q1_reproj, 1.0)
        reprojection_limit = q3_reproj + 3.0 * iqr_reproj
        # Never tighter than 40px, never looser than 400px
        reprojection_limit = max(40.0, min(reprojection_limit, 400.0))
    else:
        reprojection_limit = 160.0

    # Stage 2: IQR-based outlier rejection on ray gap
    if n >= 8:
        q1_gap = float(np.percentile(raw_ray_gaps, 25))
        q3_gap = float(np.percentile(raw_ray_gaps, 75))
        iqr_gap = max(q3_gap - q1_gap, 0.05)
        gap_limit = q3_gap + 3.0 * iqr_gap
        gap_limit = max(0.2, min(gap_limit, 3.0))
    else:
        gap_limit = 1.5

    # Stage 3: temporal consistency — reject points where velocity > 35 m/s
    keep = np.ones(n, dtype=bool)
    keep &= (raw_reprojection_errors <= reprojection_limit)
    keep &= (raw_ray_gaps <= gap_limit)

    if np.count_nonzero(keep) >= 4:
        # Compute velocities on surviving points to detect spatial jumps
        surviving_indices = np.where(keep)[0]
        for i in range(1, len(surviving_indices)):
            idx_prev = surviving_indices[i - 1]
            idx_curr = surviving_indices[i]
            dt = raw_times[idx_curr] - raw_times[idx_prev]
            if dt <= 0:
                continue
            dp = np.linalg.norm(raw_world_points[idx_curr] - raw_world_points[idx_prev])
            if dp / dt > 35.0:  # >35 m/s is physically impossible for a football
                keep[idx_curr] = False

    if np.count_nonzero(keep) >= 8:
        times = raw_times[keep]
        world_points = raw_world_points[keep]
        ray_gaps = raw_ray_gaps[keep]
        reprojection_errors = raw_reprojection_errors[keep]
        image_points_left = raw_image_points_left[keep]
        image_points_right = raw_image_points_right[keep]
        confidences_left = raw_confidences_left[keep]
        confidences_right = raw_confidences_right[keep]
        raw_keep_mask = keep
    else:
        times = raw_times
        world_points = raw_world_points
        ray_gaps = raw_ray_gaps
        reprojection_errors = raw_reprojection_errors
        image_points_left = raw_image_points_left
        image_points_right = raw_image_points_right
        confidences_left = raw_confidences_left
        confidences_right = raw_confidences_right
        raw_keep_mask = np.ones(len(raw_times), dtype=bool)

    world_points = smooth_points(world_points, window=7)
    return Trajectory3D(
        raw_times=raw_times,
        raw_world_points=raw_world_points,
        raw_ray_gaps=raw_ray_gaps,
        raw_reprojection_errors=raw_reprojection_errors,
        raw_keep_mask=raw_keep_mask,
        raw_image_points_left=raw_image_points_left,
        raw_image_points_right=raw_image_points_right,
        raw_confidences_left=raw_confidences_left,
        raw_confidences_right=raw_confidences_right,
        times=times,
        world_points=world_points,
        ray_gaps=ray_gaps,
        reprojection_errors=reprojection_errors,
        image_points_left=image_points_left,
        image_points_right=image_points_right,
        confidences_left=confidences_left,
        confidences_right=confidences_right,
        offset_seconds=offset_seconds,
        alignment_metrics=candidate["alignment_metrics"],
    )


def write_csv(path, trajectory):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["time_sec", "x_m", "y_m", "z_m", "ray_gap_m", "reprojection_error_px"])
        for idx in range(len(trajectory.times)):
            writer.writerow(
                [
                    float(trajectory.times[idx]),
                    float(trajectory.world_points[idx, 0]),
                    float(trajectory.world_points[idx, 1]),
                    float(trajectory.world_points[idx, 2]),
                    float(trajectory.ray_gaps[idx]),
                    float(trajectory.reprojection_errors[idx]),
                ]
            )


def write_raw_csv(path, trajectory):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "time_sec",
            "x_m",
            "y_m",
            "z_m",
            "ray_gap_m",
            "reprojection_error_px",
            "kept_after_filter",
        ])
        for idx in range(len(trajectory.raw_times)):
            writer.writerow(
                [
                    float(trajectory.raw_times[idx]),
                    float(trajectory.raw_world_points[idx, 0]),
                    float(trajectory.raw_world_points[idx, 1]),
                    float(trajectory.raw_world_points[idx, 2]),
                    float(trajectory.raw_ray_gaps[idx]),
                    float(trajectory.raw_reprojection_errors[idx]),
                    bool(trajectory.raw_keep_mask[idx]),
                ]
            )


def _json_compatible(value):
    if isinstance(value, np.ndarray):
        return value.tolist()
    if isinstance(value, np.generic):
        return value.item()
    if isinstance(value, dict):
        return {str(key): _json_compatible(item) for key, item in value.items()}
    if isinstance(value, (list, tuple)):
        return [_json_compatible(item) for item in value]
    return value


def save_summary(sample_dir, video_tracks, trajectory, output_dir):
    summary = {
        "sample_dir": str(sample_dir),
        "time_offset_seconds": float(trajectory.offset_seconds),
        "time_alignment": _json_compatible(trajectory.alignment_metrics),
        "num_raw_trajectory_points": int(len(trajectory.raw_times)),
        "num_filtered_trajectory_points": int(len(trajectory.times)),
        "mean_ray_gap_m": float(np.mean(trajectory.ray_gaps)),
        "median_ray_gap_m": float(np.median(trajectory.ray_gaps)),
        "mean_reprojection_error_px": float(np.mean(trajectory.reprojection_errors)),
        "max_height_m": float(np.max(trajectory.world_points[:, 2])),
        "raw_trajectory": [],
        "trajectory": [],
        "videos": [],
    }

    for track in video_tracks:
        summary["videos"].append(
            {
                "video_path": str(track.video_path),
                "camera_name": track.camera_name,
                "fps": float(track.fps),
                "frame_count": int(track.frame_count),
                "frame_size": list(track.frame_size),
                "kick_frame": int(track.kick_frame),
                "num_detected_points": int(len(track.image_points)),
                "mean_center_sigma_px": float(np.mean(
                    np.sqrt(np.maximum(np.trace(track.center_covariances, axis1=1, axis2=2) * 0.5, 0.0))
                )),
                "global_path_optimization": track.path_diagnostics,
            }
        )

    for idx in range(len(trajectory.raw_times)):
        summary["raw_trajectory"].append(
            {
                "time_sec": float(trajectory.raw_times[idx]),
                "x_m": float(trajectory.raw_world_points[idx, 0]),
                "y_m": float(trajectory.raw_world_points[idx, 1]),
                "z_m": float(trajectory.raw_world_points[idx, 2]),
                "ray_gap_m": float(trajectory.raw_ray_gaps[idx]),
                "reprojection_error_px": float(trajectory.raw_reprojection_errors[idx]),
                "kept_after_filter": bool(trajectory.raw_keep_mask[idx]),
            }
        )

    for idx in range(len(trajectory.times)):
        summary["trajectory"].append(
            {
                "time_sec": float(trajectory.times[idx]),
                "x_m": float(trajectory.world_points[idx, 0]),
                "y_m": float(trajectory.world_points[idx, 1]),
                "z_m": float(trajectory.world_points[idx, 2]),
                "ray_gap_m": float(trajectory.ray_gaps[idx]),
                "reprojection_error_px": float(trajectory.reprojection_errors[idx]),
            }
        )

    with open(output_dir / "trajectory_3d_summary.json", "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)


def render_trajectory_plot(sample_name, trajectory, camera_configs, out_path):
    points = trajectory.world_points
    x = points[:, 0]
    y = points[:, 1]
    z = points[:, 2]
    gaps = trajectory.ray_gaps
    times = trajectory.times
    camera_centers = np.asarray([config.camera_center_world for config in camera_configs], dtype=np.float64)

    canvas_h, canvas_w = 1000, 1600
    canvas = np.full((canvas_h, canvas_w, 3), 248, dtype=np.uint8)
    margin = 40
    gutter = 30
    panel_w = (canvas_w - margin * 2 - gutter) // 2
    panel_h = (canvas_h - margin * 2 - gutter) // 2

    def panel_rect(row, col):
        x0 = margin + col * (panel_w + gutter)
        y0 = margin + row * (panel_h + gutter)
        return x0, y0, panel_w, panel_h

    def map_point(value_x, value_y, range_x, range_y, rect):
        x0, y0, w, h = rect
        rx0, rx1 = range_x
        ry0, ry1 = range_y
        px = x0 + int(round((value_x - rx0) / max(1e-6, (rx1 - rx0)) * (w - 1)))
        py = y0 + int(round((ry1 - value_y) / max(1e-6, (ry1 - ry0)) * (h - 1)))
        return px, py

    def draw_panel_border(rect, title):
        x0, y0, w, h = rect
        cv2.rectangle(canvas, (x0, y0), (x0 + w, y0 + h), (180, 180, 180), 2, cv2.LINE_AA)
        cv2.putText(canvas, title, (x0 + 12, y0 + 28), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (30, 30, 30), 2, cv2.LINE_AA)

    def draw_polyline(values_x, values_y, range_x, range_y, rect, color):
        pts = [map_point(vx, vy, range_x, range_y, rect) for vx, vy in zip(values_x, values_y)]
        if len(pts) >= 2:
            cv2.polylines(canvas, [np.array(pts, dtype=np.int32)], False, color, 3, cv2.LINE_AA)
        if pts:
            cv2.circle(canvas, pts[0], 6, (46, 204, 113), -1, cv2.LINE_AA)
            cv2.circle(canvas, pts[-1], 6, (52, 87, 230), -1, cv2.LINE_AA)

    top_rect = panel_rect(0, 0)
    side_rect = panel_rect(0, 1)
    front_rect = panel_rect(1, 0)
    gap_rect = panel_rect(1, 1)

    z_min = min(-0.3, float(np.min(z)) - 0.1)
    z_max = max(2.5, float(np.max(z)) + 0.2)
    gap_min = 0.0
    gap_max = max(1.0, float(np.max(gaps)) * 1.15)
    time_min = float(np.min(times))
    time_max = max(time_min + 1e-6, float(np.max(times)))

    draw_panel_border(top_rect, 'Top View (x-y)')
    draw_polyline(x, y, FIELD_X_LIMITS, FIELD_Y_LIMITS, top_rect, (0, 140, 255))
    for center in camera_centers:
        cv2.circle(canvas, map_point(center[0], center[1], FIELD_X_LIMITS, FIELD_Y_LIMITS, top_rect), 5, (20, 20, 20), -1, cv2.LINE_AA)
    cv2.circle(canvas, map_point(PENALTY_SPOT_GROUND_WORLD[0], PENALTY_SPOT_GROUND_WORLD[1], FIELD_X_LIMITS, FIELD_Y_LIMITS, top_rect), 5, (220, 120, 0), -1, cv2.LINE_AA)

    draw_panel_border(side_rect, 'Side View (y-z)')
    draw_polyline(y, z, FIELD_Y_LIMITS, (z_min, z_max), side_rect, (180, 80, 200))
    for center in camera_centers:
        cv2.circle(canvas, map_point(center[1], center[2], FIELD_Y_LIMITS, (z_min, z_max), side_rect), 5, (20, 20, 20), -1, cv2.LINE_AA)

    draw_panel_border(front_rect, 'Front View (x-z)')
    draw_polyline(x, z, FIELD_X_LIMITS, (z_min, z_max), front_rect, (40, 180, 180))
    for center in camera_centers:
        cv2.circle(canvas, map_point(center[0], center[2], FIELD_X_LIMITS, (z_min, z_max), front_rect), 5, (20, 20, 20), -1, cv2.LINE_AA)

    draw_panel_border(gap_rect, 'Ray Gap Quality')
    draw_polyline(times, gaps, (time_min, time_max), (gap_min, gap_max), gap_rect, (120, 90, 60))

    header = f'{sample_name} | offset={trajectory.offset_seconds:+.4f}s | max height={np.max(z):.2f}m | points={len(times)}'
    cv2.putText(canvas, header, (40, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.7, (25, 25, 25), 2, cv2.LINE_AA)
    cv2.imwrite(str(out_path), canvas)


_CUDA_FLAG = None


def _cuda_available():
    """是否可用 CUDA（懒加载 + 缓存）。CPU 下并行检测会线程超订反而更慢。"""
    global _CUDA_FLAG
    if _CUDA_FLAG is None:
        try:
            import torch
            _CUDA_FLAG = bool(torch.cuda.is_available())
        except Exception:
            _CUDA_FLAG = False
    return _CUDA_FLAG


def process_sample(sample_dir, camera_configs, model, imgsz, conf, model_path=None):
    videos_by_camera = resolve_camera_videos(sample_dir)
    video_paths = list(videos_by_camera.values())
    if len(video_paths) != 2:
        raise RuntimeError(f"{sample_dir} 下应当正好有 2 个视频，当前为 {len(video_paths)} 个。")

    output_dir = OUTPUT_ROOT / sample_dir.name
    output_dir.mkdir(parents=True, exist_ok=True)

    configs_by_name = {config.name: config for config in camera_configs}
    if set(configs_by_name) != {"left", "right"}:
        raise RuntimeError("Camera calibration must define both left and right cameras.")
    camera_videos = [(camera_name, videos_by_camera[camera_name]) for camera_name in ("left", "right")]
    print(f"\n[{sample_dir.name}] 相机分配结果:")
    for camera_name, video_path in camera_videos:
        print(f"  {video_path} -> {camera_name}")

    def _detect_one(camera_name, video_path, m):
        config = configs_by_name[camera_name]
        print(f"[{sample_dir.name}] 检测足球并提取 2D 轨迹: {video_path.name}", flush=True)
        track = detect_video_track(video_path, config, m, imgsz=imgsz, conf=conf)
        print(f"  kick_frame={track.kick_frame}, detected_points={len(track.image_points)}", flush=True)
        return track

    # 左右两路并行检测：仅在 GPU(CUDA) 上并行（GPU 可并发流）；CPU 上 PyTorch
    # 已用多线程做算子并行，双路并发反而线程超订导致更慢，故 CPU 保持串行。
    # 每个线程使用独立的模型实例，避免共享 ultralytics predictor 的竞态。
    if model_path is not None and _cuda_available():
        with ThreadPoolExecutor(max_workers=2) as ex:
            models = [model, YOLO(model_path)]
            futures = [ex.submit(_detect_one, camera_name, video_path, m)
                       for (camera_name, video_path), m in zip(camera_videos, models)]
            tracks = [f.result() for f in futures]
    else:
        tracks = [_detect_one(camera_name, video_path, model)
                  for camera_name, video_path in camera_videos]

    tracks_by_name = {track.camera_name: track for track in tracks}
    if "left" not in tracks_by_name or "right" not in tracks_by_name:
        raise RuntimeError("当前样例未能匹配到 left/right 两个相机视角。")

    trajectory = build_3d_trajectory(
        tracks_by_name["left"],
        configs_by_name["left"],
        tracks_by_name["right"],
        configs_by_name["right"],
    )

    csv_path = output_dir / "trajectory_3d_points.csv"
    raw_csv_path = output_dir / "trajectory_3d_points_raw.csv"
    npz_path = output_dir / "trajectory_3d_points.npz"
    fig_path = output_dir / f"{sample_dir.name}_trajectory_3d.png"
    write_csv(csv_path, trajectory)
    write_raw_csv(raw_csv_path, trajectory)
    np.savez(
        npz_path,
        raw_times=trajectory.raw_times,
        raw_world_points=trajectory.raw_world_points,
        raw_ray_gaps=trajectory.raw_ray_gaps,
        raw_reprojection_errors=trajectory.raw_reprojection_errors,
        raw_keep_mask=trajectory.raw_keep_mask.astype(np.uint8),
        raw_image_points_left=trajectory.raw_image_points_left,
        raw_image_points_right=trajectory.raw_image_points_right,
        raw_confidences_left=trajectory.raw_confidences_left,
        raw_confidences_right=trajectory.raw_confidences_right,
        times=trajectory.times,
        world_points=trajectory.world_points,
        ray_gaps=trajectory.ray_gaps,
        reprojection_errors=trajectory.reprojection_errors,
        image_points_left=trajectory.image_points_left,
        image_points_right=trajectory.image_points_right,
        confidences_left=trajectory.confidences_left,
        confidences_right=trajectory.confidences_right,
        offset_seconds=np.array([trajectory.offset_seconds], dtype=np.float64),
    )
    render_trajectory_plot(sample_dir.name, trajectory, camera_configs, fig_path)
    save_summary(sample_dir, tracks, trajectory, output_dir)

    print(f"[{sample_dir.name}] Raw 3D point count: {len(trajectory.raw_times)}")
    print(f"[{sample_dir.name}] Filtered 3D point count: {len(trajectory.times)}")
    print(f"[{sample_dir.name}] Stereo time offset: {trajectory.offset_seconds:+.4f} s")
    print(f"[{sample_dir.name}] Max height: {np.max(trajectory.world_points[:, 2]):.3f} m")
    print(f"[{sample_dir.name}] Mean ray gap: {np.mean(trajectory.ray_gaps):.3f} m")
    print(f"[{sample_dir.name}] Saved: {csv_path}")
    print(f"[{sample_dir.name}] Saved: {raw_csv_path}")
    print(f"[{sample_dir.name}] Saved: {fig_path}")

def parse_args():
    parser = argparse.ArgumentParser(description="Reconstruct football 3D trajectories from paired penalty videos.")
    parser.add_argument("--samples", nargs="*", help="Sample directories to process, e.g. sample1 sample2 sample3")
    parser.add_argument("--imgsz", type=int, default=DEFAULT_IMGSZ)
    parser.add_argument("--conf", type=float, default=DEFAULT_CONF)
    parser.add_argument(
        "--yolo-model",
        type=str,
        default=str(YOLO_MODEL_PATH),
        help=f"YOLO model path or model name (default: {DEFAULT_YOLO_MODEL})",
    )
    return parser.parse_args()


def main():
    args = parse_args()
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)

    camera_configs = load_camera_configs()
    sample_dirs = [SAMPLES / name for name in args.samples] if args.samples else sorted(SAMPLES.glob("sample*"))
    sample_dirs = [path for path in sample_dirs if path.is_dir()]
    if not sample_dirs:
        raise FileNotFoundError("未找到 sample 目录。")

    model = YOLO(args.yolo_model)
    # 预热：仅 GPU 需要编译 CUDA kernel；CPU 上无 kernel 可编译，反而徒增启动耗时
    if _cuda_available():
        try:
            model.predict(np.zeros((640, 640, 3), dtype=np.uint8),
                          classes=[SPORTS_BALL_CLASS_ID], imgsz=320, conf=0.25,
                          verbose=False)
        except Exception:
            pass
    for sample_dir in sample_dirs:
        process_sample(sample_dir, camera_configs, model, imgsz=args.imgsz,
                       conf=args.conf, model_path=args.yolo_model)


if __name__ == "__main__":
    main()












