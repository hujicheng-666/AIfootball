import argparse
import json
import os
import sys
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from scipy.interpolate import interp1d
from ultralytics import YOLO

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


BASE_DIR = Path(__file__).resolve().parent
WORKSPACE_DIR = BASE_DIR.parent
OUTPUT_ROOT = WORKSPACE_DIR / "output" / "trajectory_redraw"
DEFAULT_YOLO_MODEL = "yolo11x.pt"
YOLO_MODEL_PATH = Path(os.environ.get("YOLO_MODEL_PATH", WORKSPACE_DIR / DEFAULT_YOLO_MODEL))
SPORTS_BALL_CLASS_ID = 32
PENALTY_SPOT_WORLD = np.array([0.0, 11.0, 0.0], dtype=np.float64)
FIELD_X_LIMITS = (-15.0, 15.0)
FIELD_Y_LIMITS = (-3.0, 20.0)
PREFERRED_VIDEO_CAMERA_BY_STEM = {
    "VID_011": "right",
    "VID_012": "right",
    "VID_013": "right",
    "VID_014": "left",
    "VID_015": "left",
    "VID_016": "left",
}
DEFAULT_IMGSZ = 1280
DEFAULT_CONF = 0.15


@dataclass
class CameraConfig:
    name: str
    pose_path: Path
    meta_path: Path
    reference_image_path: Path
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    rvec: np.ndarray
    tvec: np.ndarray
    rotation_matrix: np.ndarray
    reference_signature: np.ndarray


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
    world_points: np.ndarray


@dataclass
class FusedTrack:
    times: np.ndarray
    world_points: np.ndarray



def build_signature_from_frame(frame):
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    small = cv2.resize(gray, (320, 140), interpolation=cv2.INTER_AREA)
    return cv2.GaussianBlur(small, (7, 7), 0)



def build_signature_from_image(image_path):
    image = cv2.imread(str(image_path))
    if image is None:
        raise FileNotFoundError(f"无法读取参考图像: {image_path}")
    return build_signature_from_frame(image)



def load_camera_configs():
    configs = []
    for name in ("left", "right"):
        pose_path = WORKSPACE_DIR / "output" / f"{name}_pose.npz"
        meta_path = WORKSPACE_DIR / "output" / f"{name}_extrinsics.json"
        if not pose_path.exists() or not meta_path.exists():
            raise FileNotFoundError(f"缺少相机参数文件: {pose_path} 或 {meta_path}")

        with open(meta_path, "r", encoding="utf-8") as f:
            meta = json.load(f)

        reference_image_path = Path(meta["image_path"])
        pose = np.load(pose_path)
        camera_matrix = np.asarray(pose["camera_matrix"], dtype=np.float64)
        dist_coeffs = np.asarray(pose["dist_coeffs"], dtype=np.float64)
        rvec = np.asarray(pose["rvec"], dtype=np.float64)
        tvec = np.asarray(pose["tvec"], dtype=np.float64)
        rotation_matrix = np.asarray(pose["R"], dtype=np.float64)

        configs.append(
            CameraConfig(
                name=name,
                pose_path=pose_path,
                meta_path=meta_path,
                reference_image_path=reference_image_path,
                camera_matrix=camera_matrix,
                dist_coeffs=dist_coeffs,
                rvec=rvec,
                tvec=tvec,
                rotation_matrix=rotation_matrix,
                reference_signature=build_signature_from_image(reference_image_path),
            )
        )
    return configs



def read_first_frame(video_path):
    cap = cv2.VideoCapture(str(video_path))
    ok, frame = cap.read()
    cap.release()
    if not ok or frame is None:
        raise RuntimeError(f"无法读取视频首帧: {video_path}")
    return frame



def assign_videos_to_cameras(video_paths, configs):
    if len(video_paths) != len(configs):
        raise ValueError("??????????????????????")

    config_by_name = {config.name: config for config in configs}
    preferred_assignment = {}
    for video_path in video_paths:
        preferred_camera = PREFERRED_VIDEO_CAMERA_BY_STEM.get(video_path.stem)
        if preferred_camera is None or preferred_camera not in config_by_name:
            preferred_assignment = None
            break
        preferred_assignment[video_path] = config_by_name[preferred_camera]

    if preferred_assignment is not None and len({cfg.name for cfg in preferred_assignment.values()}) == len(video_paths):
        return preferred_assignment

    video_signatures = [build_signature_from_frame(read_first_frame(video)) for video in video_paths]
    distances = np.zeros((len(video_paths), len(configs)), dtype=np.float64)
    for i, signature in enumerate(video_signatures):
        for j, config in enumerate(configs):
            distances[i, j] = float(np.mean(np.abs(signature.astype(np.float32) - config.reference_signature.astype(np.float32))))

    assignment_a = {video_paths[0]: configs[0], video_paths[1]: configs[1]}
    cost_a = distances[0, 0] + distances[1, 1]
    assignment_b = {video_paths[0]: configs[1], video_paths[1]: configs[0]}
    cost_b = distances[0, 1] + distances[1, 0]

    return assignment_a if cost_a <= cost_b else assignment_b


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
    camera_center_world = -config.rotation_matrix.T @ config.tvec.reshape(3)
    ray_world = config.rotation_matrix.T @ ray_cam

    if abs(ray_world[2]) < 1e-8:
        return None

    scale = (ground_z - camera_center_world[2]) / ray_world[2]
    world_point = camera_center_world + scale * ray_world
    return world_point



def pick_detection(boxes, expected_center, penalty_center):
    best = None
    best_score = None
    MIN_CONF = 0.08
    MAX_AREA = 20000

    for box in boxes:
        x1, y1, x2, y2 = [float(v) for v in box.xyxy[0].tolist()]
        conf = float(box.conf.item())
        if conf < MIN_CONF:
            continue
        w = max(x2 - x1, 1.0)
        h = max(y2 - y1, 1.0)
        area = w * h
        if area > MAX_AREA or area < 4.0:
            continue
        center = np.array([(x1 + x2) * 0.5, (y1 + y2) * 0.5], dtype=np.float64)
        foot = np.array([(x1 + x2) * 0.5, y2], dtype=np.float64)
        dist_expected = np.linalg.norm(center - expected_center)
        dist_penalty = np.linalg.norm(center - penalty_center)
        score = 8.0 * conf - 0.002 * dist_expected - 0.0005 * dist_penalty + 0.00005 * area
        if best_score is None or score > best_score:
            best_score = score
            best = {
                "conf": conf,
                "bbox": [x1, y1, x2, y2],
                "center": center,
                "foot": foot,
            }

    return best



def estimate_kick_frame(detections):
    distances = []
    for det in detections:
        if det["world_point"] is None:
            distances.append(np.nan)
        else:
            distances.append(float(np.linalg.norm(det["world_point"][:2] - PENALTY_SPOT_WORLD[:2])))

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



def smooth_world_points(points):
    if len(points) < 5:
        return points.copy()

    window = min(len(points), 11)
    if window < 3:
        return points.copy()

    half = window // 2

    smoothed = points.astype(np.float64, copy=True)
    for idx in range(len(points)):
        start = max(0, idx - half)
        end = min(len(points), idx + half + 1)
        segment = points[start:end]
        if len(segment) == 1:
            smoothed[idx] = segment[0]
            continue

        center = idx - start
        positions = np.arange(len(segment), dtype=np.float64)
        distances = np.abs(positions - float(center))
        weights = distances.max() + 1.0 - distances
        weights = np.clip(weights, 1.0, None)
        weights = weights / weights.sum()
        smoothed[idx] = np.sum(segment * weights[:, None], axis=0)
    return smoothed


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
    return crop, (x1, y1)


def _yolo_detect_on_crop(model, frame, expected_center, penalty_center, imgsz, conf,
                         frame_w, frame_h):
    CROP_SIZE = min(640, min(frame_w, frame_h))
    cropped, (ox, oy) = _crop_around_point(frame, expected_center, CROP_SIZE, frame_w, frame_h)
    pc_in_crop = (penalty_center[0] - ox, penalty_center[1] - oy)
    ec_in_crop = (expected_center[0] - ox, expected_center[1] - oy)
    result = model.predict(cropped, classes=[SPORTS_BALL_CLASS_ID], conf=conf, imgsz=imgsz, verbose=False)[0]
    boxes = [] if result.boxes is None else list(result.boxes)
    chosen_in_crop = pick_detection(boxes,
                                    np.array(ec_in_crop, dtype=np.float64),
                                    np.array(pc_in_crop, dtype=np.float64))
    if chosen_in_crop is None:
        return None
    chosen_in_crop["bbox"][0] += ox
    chosen_in_crop["bbox"][1] += oy
    chosen_in_crop["bbox"][2] += ox
    chosen_in_crop["bbox"][3] += oy
    chosen_in_crop["center"][0] += ox
    chosen_in_crop["center"][1] += oy
    chosen_in_crop["foot"][0] += ox
    chosen_in_crop["foot"][1] += oy
    return chosen_in_crop


def _sparse_scan_kick_frame(cap, fps, frame_count, penalty_center, model, config, imgsz, conf):
    """Phase 1: sparse YOLO scan (~6 fps) to find kick frame."""
    SPARSE_EVERY = max(1, int(fps / 6.0))
    sparse_dets = []
    cap.set(cv2.CAP_PROP_POS_FRAMES, 0)
    frame_idx = -1

    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame_idx += 1
        if frame_idx % SPARSE_EVERY != 0:
            continue

        result = model.predict(frame, classes=[SPORTS_BALL_CLASS_ID],
                               conf=conf, imgsz=imgsz, verbose=False)[0]
        boxes = [] if result.boxes is None else list(result.boxes)
        chosen = pick_detection(boxes, penalty_center, penalty_center)
        wp = None
        if chosen is not None:
            wp = image_point_to_ground_world(chosen["foot"], config, ground_z=0.0)
            if wp is not None:
                x_ok = FIELD_X_LIMITS[0] <= wp[0] <= FIELD_X_LIMITS[1]
                y_ok = FIELD_Y_LIMITS[0] <= wp[1] <= FIELD_Y_LIMITS[1]
                if not (x_ok and y_ok):
                    wp = None

        sparse_dets.append({
            "frame_idx": frame_idx,
            "detection": chosen,
            "world_point": wp,
        })

    if not sparse_dets:
        raise RuntimeError("稀疏扫描未检测到任何帧。")

    sparse_kick_idx = estimate_kick_frame(sparse_dets)
    kick_frame = sparse_dets[sparse_kick_idx]["frame_idx"]

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
    penalty_center = project_world_points(PENALTY_SPOT_WORLD.reshape(1, 3), config)[0]

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

    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame_idx += 1

        if len(history) >= 2:
            velocity = history[-1] - history[-2]
            expected_center = history[-1] + velocity
        elif history:
            expected_center = history[-1]
        else:
            expected_center = penalty_center

        chosen = _yolo_detect_on_crop(model, frame, expected_center, penalty_center,
                                       imgsz, conf, frame_width, frame_height)

        if chosen is None:
            result = model.predict(frame, classes=[SPORTS_BALL_CLASS_ID],
                                   conf=conf, imgsz=imgsz, verbose=False)[0]
            boxes = [] if result.boxes is None else list(result.boxes)
            chosen = pick_detection(boxes, expected_center, penalty_center)

        world_point = None
        if chosen is not None:
            history.append(chosen["center"])
            consecutive_miss = 0
            if len(history) > 8:
                history = history[-8:]
            world_point = image_point_to_ground_world(chosen["foot"], config, ground_z=0.0)
            if world_point is not None:
                x_ok = FIELD_X_LIMITS[0] <= world_point[0] <= FIELD_X_LIMITS[1]
                y_ok = FIELD_Y_LIMITS[0] <= world_point[1] <= FIELD_Y_LIMITS[1]
                if not (x_ok and y_ok):
                    world_point = None
        else:
            consecutive_miss += 1
            if consecutive_miss > MAX_MISS:
                history.clear()
                consecutive_miss = 0

        detections.append({
            "frame_idx": frame_idx,
            "time": frame_idx / fps,
            "detection": chosen,
            "world_point": world_point,
        })

    cap.release()

    rel_times = []
    world_points = []
    for det in detections:
        if det["world_point"] is None:
            continue
        rel_times.append((det["frame_idx"] - kick_frame) / fps)
        world_points.append(det["world_point"][:2].astype(np.float64))

    if not world_points:
        raise RuntimeError(f"未在视频中检测到可用的足球轨迹: {video_path}")

    rel_times = np.asarray(rel_times, dtype=np.float64)
    world_points = np.asarray(world_points, dtype=np.float64)
    keep = rel_times >= 0.0
    if np.any(keep):
        rel_times = rel_times[keep]
        world_points = world_points[keep]

    world_points = smooth_world_points(world_points)

    return VideoTrack(
        video_path=video_path,
        camera_name=config.name,
        fps=fps,
        frame_count=frame_count,
        frame_size=(frame_width, frame_height),
        kick_frame=kick_frame,
        detections=detections,
        times=rel_times,
        world_points=world_points,
    )



def build_fused_track(tracks):
    max_fps = max(track.fps for track in tracks)
    dt = 1.0 / max_fps
    max_time = max(float(track.times[-1]) for track in tracks if len(track.times) > 0)
    fused_times = np.arange(0.0, max_time + dt * 0.5, dt)

    interpolators = []
    for track in tracks:
        if len(track.times) < 2:
            continue
        interpolators.append(
            {
                "t_min": float(track.times[0]),
                "t_max": float(track.times[-1]),
                "interp_x": interp1d(track.times, track.world_points[:, 0], kind="linear", bounds_error=False),
                "interp_y": interp1d(track.times, track.world_points[:, 1], kind="linear", bounds_error=False),
            }
        )

    fused_points = []
    for t in fused_times:
        points = []
        for interp in interpolators:
            if interp["t_min"] <= t <= interp["t_max"]:
                points.append(np.array([float(interp["interp_x"](t)), float(interp["interp_y"](t))], dtype=np.float64))
        if not points:
            continue
        fused_points.append((t, np.mean(points, axis=0)))

    if not fused_points:
        raise RuntimeError("无法融合双机位轨迹。")

    fused_times = np.asarray([item[0] for item in fused_points], dtype=np.float64)
    fused_world = np.asarray([item[1] for item in fused_points], dtype=np.float64)
    fused_world = smooth_world_points(fused_world)
    return FusedTrack(times=fused_times, world_points=fused_world)



def world_to_canvas(world_xy, width, height):
    x_min, x_max = FIELD_X_LIMITS
    y_min, y_max = FIELD_Y_LIMITS
    x = int(round((world_xy[0] - x_min) / (x_max - x_min) * (width - 1)))
    y = int(round((y_max - world_xy[1]) / (y_max - y_min) * (height - 1)))
    return x, y



def render_topdown(sample_name, fused_track, out_path):
    width, height = 1200, 900
    canvas = np.full((height, width, 3), (34, 110, 56), dtype=np.uint8)

    def draw_line(p1, p2, color=(235, 235, 235), thickness=2):
        cv2.line(canvas, world_to_canvas(p1, width, height), world_to_canvas(p2, width, height), color, thickness, cv2.LINE_AA)

    draw_line((FIELD_X_LIMITS[0], 0.0), (FIELD_X_LIMITS[1], 0.0), thickness=3)
    draw_line((-3.66, 0.0), (3.66, 0.0), color=(255, 255, 255), thickness=5)
    draw_line((-9.16, 5.5), (9.16, 5.5))
    draw_line((-9.16, 0.0), (-9.16, 5.5))
    draw_line((9.16, 0.0), (9.16, 5.5))
    penalty_xy = world_to_canvas(PENALTY_SPOT_WORLD[:2], width, height)
    cv2.circle(canvas, penalty_xy, 6, (255, 255, 255), -1, cv2.LINE_AA)

    pts = np.array([world_to_canvas(p, width, height) for p in fused_track.world_points], dtype=np.int32)
    if len(pts) >= 2:
        cv2.polylines(canvas, [pts], False, (40, 180, 255), 4, cv2.LINE_AA)
    if len(pts) >= 1:
        cv2.circle(canvas, tuple(pts[0]), 8, (0, 255, 255), -1, cv2.LINE_AA)
        cv2.circle(canvas, tuple(pts[-1]), 8, (0, 140, 255), -1, cv2.LINE_AA)

    cv2.putText(canvas, f"{sample_name} fused ground trajectory", (40, 50),
                cv2.FONT_HERSHEY_SIMPLEX, 1.0, (255, 255, 255), 2, cv2.LINE_AA)
    cv2.imwrite(str(out_path), canvas)



def render_overlay_video(video_track, config, fused_track, out_path):
    cap = cv2.VideoCapture(str(video_track.video_path))
    if not cap.isOpened():
        raise RuntimeError(f"无法打开视频: {video_track.video_path}")

    width, height = video_track.frame_size
    fourcc = cv2.VideoWriter_fourcc(*"mp4v")
    writer = cv2.VideoWriter(str(out_path), fourcc, video_track.fps, (width, height))

    world_points_3d = np.hstack([fused_track.world_points, np.zeros((len(fused_track.world_points), 1), dtype=np.float64)])
    projected_points = project_world_points(world_points_3d, config)

    frame_idx = -1
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame_idx += 1
        rel_time = (frame_idx - video_track.kick_frame) / video_track.fps
        valid_mask = fused_track.times <= rel_time
        pts = projected_points[valid_mask]

        if len(pts) >= 2:
            poly = np.round(pts).astype(np.int32).reshape(-1, 1, 2)
            cv2.polylines(frame, [poly], False, (0, 220, 255), 4, cv2.LINE_AA)
        if len(pts) >= 1:
            cur = tuple(np.round(pts[-1]).astype(np.int32))
            cv2.circle(frame, cur, 8, (0, 0, 255), -1, cv2.LINE_AA)

        cv2.putText(frame, f"{video_track.video_path.name} | kick frame={video_track.kick_frame}", (30, 45),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.9, (255, 255, 255), 2, cv2.LINE_AA)
        writer.write(frame)

    cap.release()
    writer.release()



def save_sample_summary(sample_dir, video_tracks, fused_track, output_dir):
    summary = {
        "sample_dir": str(sample_dir),
        "videos": [],
        "fused_track": {
            "times": fused_track.times.tolist(),
            "world_points_xy": fused_track.world_points.tolist(),
        },
    }

    for track in video_tracks:
        summary["videos"].append(
            {
                "video_path": str(track.video_path),
                "camera_name": track.camera_name,
                "fps": track.fps,
                "frame_count": track.frame_count,
                "frame_size": list(track.frame_size),
                "kick_frame": track.kick_frame,
                "num_world_points": int(len(track.world_points)),
            }
        )

    with open(output_dir / "summary.json", "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)



def process_sample(sample_dir, camera_configs, model, imgsz, conf):
    video_paths = sorted(sample_dir.glob("*.mp4"))
    if len(video_paths) != 2:
        raise RuntimeError(f"{sample_dir} 下应当正好有 2 个视频，当前为 {len(video_paths)} 个。")

    output_dir = OUTPUT_ROOT / sample_dir.name
    output_dir.mkdir(parents=True, exist_ok=True)

    assignment = assign_videos_to_cameras(video_paths, camera_configs)
    print(f"\n[{sample_dir.name}] 相机分配结果:")
    for video_path, config in assignment.items():
        print(f"  {video_path.name} -> {config.name} ({config.reference_image_path.name})")

    video_tracks = []
    for video_path in video_paths:
        config = assignment[video_path]
        print(f"[{sample_dir.name}] 检测足球并重建地面轨迹: {video_path.name}")
        track = detect_video_track(video_path, config, model, imgsz=imgsz, conf=conf)
        print(f"  kick_frame={track.kick_frame}, detected_points={len(track.world_points)}")
        video_tracks.append(track)

    fused_track = build_fused_track(video_tracks)
    render_topdown(sample_dir.name, fused_track, output_dir / f"{sample_dir.name}_topdown.png")
    save_sample_summary(sample_dir, video_tracks, fused_track, output_dir)

    config_by_name = {cfg.name: cfg for cfg in camera_configs}
    for track in video_tracks:
        config = config_by_name[track.camera_name]
        overlay_path = output_dir / f"{track.video_path.stem}_trajectory.mp4"
        print(f"[{sample_dir.name}] 重绘轨迹视频: {overlay_path.name}")
        render_overlay_video(track, config, fused_track, overlay_path)



def parse_args():
    parser = argparse.ArgumentParser(description="Use YOLO and camera poses to redraw football trajectories from paired penalty videos.")
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
    sample_dirs = [WORKSPACE_DIR / name for name in args.samples] if args.samples else sorted(WORKSPACE_DIR.glob("sample*"))
    sample_dirs = [path for path in sample_dirs if path.is_dir()]
    if not sample_dirs:
        raise FileNotFoundError("未找到 sample 目录。")

    model = YOLO(args.yolo_model)

    for sample_dir in sample_dirs:
        process_sample(sample_dir, camera_configs, model, imgsz=args.imgsz, conf=args.conf)


if __name__ == "__main__":
    main()



