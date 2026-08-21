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
DEFAULT_CONF = 0.15


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


def rank_detections(boxes, expected_center, penalty_center, max_candidates=3):
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

    window = max(3, min(int(window), len(points)))
    half = window // 2
    smoothed = points.astype(np.float64, copy=True)
    for idx in range(len(points)):
        start = max(0, idx - half)
        end = min(len(points), idx + half + 1)
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
    Footballs are mostly white → HSV threshold finds precise contour centroid."""
    try:
        x1i, y1i = max(0, int(x1)), max(0, int(y1))
        x2i, y2i = min(frame.shape[1], int(x2)), min(frame.shape[0], int(y2))
        if x2i - x1i < 4 or y2i - y1i < 4:
            return None

        roi = frame[y1i:y2i, x1i:x2i]
        hsv = cv2.cvtColor(roi, cv2.COLOR_BGR2HSV)

        # White ball: low saturation, high value
        mask = cv2.inRange(hsv, np.array([0, 0, 160]), np.array([180, 60, 255]))

        # Also try green-field mask for ball-on-grass contrast (inverse)
        mask_green = cv2.inRange(hsv, np.array([35, 40, 40]), np.array([85, 255, 255]))
        mask_ball = cv2.bitwise_or(mask, cv2.bitwise_not(mask_green))

        # Find contours in ball mask
        contours, _ = cv2.findContours(mask_ball, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
        if not contours:
            return None

        # Pick largest contour near bbox center
        bcx, bcy = (x1 + x2) / 2 - x1i, (y1 + y2) / 2 - y1i
        best_contour = None
        best_score = -1
        for cnt in contours:
            area = cv2.contourArea(cnt)
            if area < 3:  # too small
                continue
            M = cv2.moments(cnt)
            if M["m00"] < 1e-6:
                continue
            cx_c = M["m10"] / M["m00"]
            cy_c = M["m01"] / M["m00"]
            # Score: area - distance from bbox center
            dist = np.hypot(cx_c - bcx, cy_c - bcy)
            circularity = 4 * np.pi * area / (cv2.arcLength(cnt, True) ** 2 + 1e-6)
            score = area * min(circularity, 1.0) - dist * 2.0
            if score > best_score:
                best_score = score
                best_contour = (cx_c, cy_c)

        if best_contour is not None:
            return np.array([best_contour[0] + x1i, best_contour[1] + y1i], dtype=np.float64)
    except Exception as exc:
        print(f"[refine] 颜色分割细化失败: {exc}", file=sys.stderr)
    return None


def _csrt_bbox_to_detection(bbox_tuple, frame_w, frame_h):
    """Convert CSRT tracker bounding box to the detection dict format used downstream."""
    x, y, w, h = [float(v) for v in bbox_tuple]
    x1, y1 = x, y
    x2, y2 = x + w, y + h
    center = np.array([(x1 + x2) * 0.5, (y1 + y2) * 0.5], dtype=np.float64)
    foot = np.array([(x1 + x2) * 0.5, y2], dtype=np.float64)
    return {
        "conf": 0.9,  # high confidence for tracked result
        "bbox": [x1, y1, x2, y2],
        "center": center,
        "foot": foot,
    }


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


def _yolo_detect_on_crop(model, frame, expected_center, penalty_center, imgsz, conf,
                         frame_w, frame_h):
    """Crop around expected position and run YOLO, map results back to full frame."""
    CROP_SIZE = min(640, min(frame_w, frame_h))
    cropped, (ox, oy) = _crop_around_point(frame, expected_center, CROP_SIZE, frame_w, frame_h)

    # Map penalty center into crop coordinates
    pc_in_crop = (penalty_center[0] - ox, penalty_center[1] - oy)
    ec_in_crop = (expected_center[0] - ox, expected_center[1] - oy)

    result = model.predict(cropped, classes=[SPORTS_BALL_CLASS_ID], conf=conf, imgsz=imgsz, verbose=False)[0]
    boxes = [] if result.boxes is None else list(result.boxes)
    chosen_in_crop = pick_detection(boxes,
                                    np.array(ec_in_crop, dtype=np.float64),
                                    np.array(pc_in_crop, dtype=np.float64))

    if chosen_in_crop is None:
        return None

    # Map back to full frame coordinates
    chosen_in_crop["bbox"][0] += ox
    chosen_in_crop["bbox"][1] += oy
    chosen_in_crop["bbox"][2] += ox
    chosen_in_crop["bbox"][3] += oy
    chosen_in_crop["center"][0] += ox
    chosen_in_crop["center"][1] += oy
    chosen_in_crop["foot"][0] += ox
    chosen_in_crop["foot"][1] += oy
    return chosen_in_crop


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
    stage1_imgsz = min(crop_size, imgsz)

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
                                conf=conf * 0.5, imgsz=min(bigger, imgsz), verbose=False)[0]
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
                                 conf=conf, imgsz=min(zoom_size, imgsz), verbose=False)[0]
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
    refined_center = _refine_ball_center(current_crop,
                                          bbox_crop[0], bbox_crop[1], bbox_crop[2], bbox_crop[3],
                                          chosen["center"])
    if refined_center is not None:
        dx = refined_center[0] - chosen["center"][0]
        dy = refined_center[1] - chosen["center"][1]
        chosen["center"] = refined_center
        chosen["foot"][0] += dx
        chosen["foot"][1] += dy

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
        })
    if full_candidates:
        full_candidates[0]["center"] = chosen["center"].copy()
    chosen["candidates"] = full_candidates
    return chosen


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
                               conf=conf, imgsz=imgsz, verbose=False)[0]
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


def detect_video_track(video_path, config, model, imgsz, conf):
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

        # --- predict next position with velocity+acceleration ---
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
            chosen = pick_detection(boxes, expected_center, penalty_center)

        # --- process chosen detection ---
        ground_point = None
        if chosen is not None:
            history.append(chosen["center"])
            consecutive_miss = 0
            if len(history) > 8:
                history = history[-8:]
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
                consecutive_miss = 0

        detections.append({
            "frame_idx": frame_idx,
            "time": frame_idx / fps,
            "detection": chosen,
            "ground_point": ground_point,
        })

    cap.release()
    print(f"  密集跟踪完成：{frame_count}/{frame_count} 帧，耗时 {time.monotonic() - started_at:.0f}s", flush=True)

    rel_times = []
    image_points = []
    confidences = []
    for det in detections:
        chosen = det["detection"]
        if chosen is None or det["frame_idx"] < kick_frame:
            continue
        rel_times.append((det["frame_idx"] - kick_frame) / fps)
        image_points.append(chosen["center"].astype(np.float64))
        confidences.append(float(chosen["conf"]))

    if not image_points:
        raise RuntimeError(f"未在视频中检测到可用的足球 2D 轨迹: {video_path}")

    return VideoTrack(
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
    )


def build_track_interpolators(track):
    if len(track.times) < 2:
        return None
    return {
        "t_min": float(track.times[0]),
        "t_max": float(track.times[-1]),
        "interp_x": interp1d(track.times, track.image_points[:, 0], kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_y": interp1d(track.times, track.image_points[:, 1], kind="linear", bounds_error=False, fill_value=np.nan),
        "interp_conf": interp1d(track.times, track.confidences, kind="linear", bounds_error=False, fill_value=np.nan),
    }


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
        candidates = best.get("candidates") or [{"center": best["center"], "conf": best["conf"]}]
        return candidates[:3]
    return [{"center": np.asarray(fallback_point, dtype=np.float64), "conf": float(fallback_confidence)}]


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
                tri = triangulate_ball_point(point_a, config_a, point_b, config_b)
                if tri is None:
                    continue
                states.append({
                    "point_a": point_a,
                    "point_b": point_b,
                    "confidence_a": float(candidate_a["conf"]),
                    "confidence_b": float(candidate_b["conf"]),
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
            geometry_nll = (
                np.log1p(state["ray_gap"] / gap_scale)
                + 0.5 * np.log1p(state["reprojection_error"] / reproj_scale)
            )
            state["emission"] = float(detection_nll + geometry_nll)
        state_rows.append(states)

    costs = [np.asarray([state["emission"] for state in state_rows[0]], dtype=np.float64)]
    parents = [np.full(len(state_rows[0]), -1, dtype=np.int32)]
    for row_index in range(1, len(state_rows)):
        previous_states = state_rows[row_index - 1]
        current_states = state_rows[row_index]
        dt = max(float(times[row_index] - times[row_index - 1]), 1e-4)
        current_costs = np.full(len(current_states), np.inf, dtype=np.float64)
        current_parents = np.full(len(current_states), -1, dtype=np.int32)
        for current_index, current_state in enumerate(current_states):
            for previous_index, previous_state in enumerate(previous_states):
                speed = np.linalg.norm(current_state["world_point"] - previous_state["world_point"]) / dt
                transition = max(0.0, (speed - 35.0) / 8.0) ** 2
                candidate_cost = costs[-1][previous_index] + transition
                if candidate_cost < current_costs[current_index]:
                    current_costs[current_index] = candidate_cost
                    current_parents[current_index] = previous_index
            current_costs[current_index] += current_state["emission"]
        costs.append(current_costs)
        parents.append(current_parents)

    selected = [int(np.argmin(costs[-1]))]
    for row_index in range(len(state_rows) - 1, 0, -1):
        selected.append(int(parents[row_index][selected[-1]]))
    selected.reverse()
    path = [state_rows[row_index][state_index] for row_index, state_index in enumerate(selected)]
    return (
        np.asarray([state["point_a"] for state in path], dtype=np.float64),
        np.asarray([state["point_b"] for state in path], dtype=np.float64),
        np.asarray([state["confidence_a"] for state in path], dtype=np.float64),
        np.asarray([state["confidence_b"] for state in path], dtype=np.float64),
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


def triangulate_ball_point(image_point_a, config_a, image_point_b, config_b):
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

    reproj_a = project_world_points(world_point.reshape(1, 3), config_a)[0]
    reproj_b = project_world_points(world_point.reshape(1, 3), config_b)[0]
    reproj_error_a = float(np.linalg.norm(reproj_a - image_point_a))
    reproj_error_b = float(np.linalg.norm(reproj_b - image_point_b))

    return {
        "world_point": world_point,
        "ray_gap": ray_gap,
        "reprojection_error": 0.5 * (reproj_error_a + reproj_error_b),
        "reprojection_errors": [reproj_error_a, reproj_error_b],
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
                                     confidences, config_a, config_b, use_ransac=False):
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
        residuals = np.hstack([projected_a - image_points_a, projected_b - image_points_b])
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
                f_scale=8.0,
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


def refine_joint_time_offset(times, image_points_a, confidences_a, interp_b, config_a, config_b,
                             initial_offset, initial_params, max_offset_step):
    """Continuously refine offset and gravity parameters in the pixel domain."""
    times = np.asarray(times, dtype=np.float64)
    image_points_a = np.asarray(image_points_a, dtype=np.float64)
    confidences_a = np.asarray(confidences_a, dtype=np.float64)
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
        residuals = np.hstack([projected_a - image_points_a, projected_b - image_points_b])
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
            f_scale=8.0,
            max_nfev=80,
        )
        return float(result.x[6])
    except (ValueError, np.linalg.LinAlgError):
        return float(initial_offset)


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
            points_a, points_b, conf_a, conf_b = selected_path

    tri_times = []
    world_points = []
    ray_gaps = []
    reprojection_errors = []
    image_points_left = []
    image_points_right = []
    conf_pairs = []
    for idx in np.where(finite)[0]:
        point_a = points_a[idx]
        point_b = points_b[idx]
        tri = triangulate_ball_point(point_a, config_a, point_b, config_b)
        if tri is None:
            continue

        tri_times.append(float(times[idx]))
        world_points.append(tri["world_point"])
        ray_gaps.append(tri["ray_gap"])
        reprojection_errors.append(tri["reprojection_error"])
        image_points_left.append(point_a)
        image_points_right.append(point_b)
        conf_pairs.append((float(conf_a[idx]), float(conf_b[idx])))

    if len(world_points) < 8:
        return None

    world_points = np.asarray(world_points, dtype=np.float64)
    ray_gaps = np.asarray(ray_gaps, dtype=np.float64)
    reprojection_errors = np.asarray(reprojection_errors, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    confidences = np.asarray(conf_pairs, dtype=np.float64)

    # A valid time offset must explain the *same ball centre* in both images.
    # Ray separation alone is insufficient: with an imperfect calibration it can
    # prefer a smooth but visibly misprojected correspondence.  Use robust
    # percentiles so a short detection loss cannot dominate the alignment.
    accel = np.linalg.norm(np.diff(world_points, n=2, axis=0), axis=1) if len(world_points) >= 3 else np.array([], dtype=np.float64)
    ballistic_p75_residual = estimate_ballistic_consistency_residual(
        np.asarray(tri_times, dtype=np.float64), world_points)
    joint_prefix_length = select_alignment_flight_prefix_length(
        np.asarray(tri_times, dtype=np.float64), world_points)
    _, joint_reprojection_errors, joint_inlier_count, joint_params = fit_joint_ballistic_reprojection(
        np.asarray(tri_times[:joint_prefix_length], dtype=np.float64),
        world_points[:joint_prefix_length],
        image_points_left[:joint_prefix_length],
        image_points_right[:joint_prefix_length],
        confidences[:joint_prefix_length],
        config_a,
        config_b,
        use_ransac=use_pixel_ransac,
    )
    gap_median, gap_p75, gap_p90 = np.percentile(ray_gaps, [50, 75, 90])
    reproj_median, reproj_p75, reproj_p90 = np.percentile(reprojection_errors, [50, 75, 90])
    joint_reproj_median, joint_reproj_p75, joint_reproj_p90 = np.percentile(
        joint_reprojection_errors, [50, 75, 90])
    accel_p90 = float(np.percentile(accel, 90)) if len(accel) else 0.0
    score = (
        float(gap_median)
        + 0.35 * float(gap_p75)
        + 0.15 * float(gap_p90)
        # Raw triangulation is a geometric safety signal.  The primary pixel
        # score is the reprojected, physically constrained trajectory.
        + 0.004 * float(reproj_median)
        + 0.003 * float(reproj_p75)
        + 0.002 * float(reproj_p90)
        + 0.016 * float(joint_reproj_median)
        + 0.010 * float(joint_reproj_p75)
        + 0.005 * float(joint_reproj_p90)
        + 0.05 * accel_p90
        + 0.05 * ballistic_p75_residual
        # Support is a fraction of the physical-flight segment, not a raw
        # count, so clips at different frame rates and durations are judged
        # on the same basis.
        - 0.48 * (float(joint_inlier_count) / max(float(joint_prefix_length), 1.0))
        - 0.03 * float(np.nanmean(confidences))
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
            "acceleration_p90_m": accel_p90,
            "ballistic_p75_residual_m": ballistic_p75_residual,
        },
        "score": score,
    }


def find_best_time_offset(track_a, config_a, track_b, config_b):
    fps_max = max(track_a.fps, track_b.fps)
    step = 1.0 / (4.0 * fps_max)  # finer step: 1/4 frame
    search_radius = 15.0 / fps_max  # keep correspondences within ±15 frames
    offsets = np.arange(-search_radius, search_radius + step * 0.5, step)

    # 插值器只依赖轨迹本身、与 offset 无关，循环外构建一次复用
    interp_a = build_track_interpolators(track_a)
    interp_b = build_track_interpolators(track_b)

    best_offset = None
    best_candidate = None
    best_score = None
    for offset in offsets:
        candidate = build_triangulation_candidate(
            track_a, config_a, track_b, config_b, float(offset),
            interp_a=interp_a, interp_b=interp_b)
        if candidate is None:
            continue
        score = candidate["score"]
        if best_score is None or score < best_score:
            best_score = score
            best_offset = float(offset)
            best_candidate = candidate

    if best_candidate is None:
        raise RuntimeError("无法在双机位之间找到可用的时间对齐结果。")

    metrics = best_candidate["alignment_metrics"]
    prefix_length = int(metrics["joint_flight_prefix_points"])
    refined_offset = refine_joint_time_offset(
        best_candidate["times"][:prefix_length],
        best_candidate["image_points_left"][:prefix_length],
        best_candidate["confidences_left"][:prefix_length],
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
        original_inliers = int(metrics["joint_reprojection_inlier_count"])
        refined_inliers = int(refined_candidate["alignment_metrics"]["joint_reprojection_inlier_count"])
        if refined_inliers > original_inliers or (
                refined_inliers == original_inliers
                and refined_candidate["score"] <= best_candidate["score"]):
            best_offset = refined_offset
            best_candidate = refined_candidate

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


def save_summary(sample_dir, video_tracks, trajectory, output_dir):
    summary = {
        "sample_dir": str(sample_dir),
        "time_offset_seconds": float(trajectory.offset_seconds),
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












