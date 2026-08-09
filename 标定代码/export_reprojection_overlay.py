import argparse
import json
import sys
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


BASE_DIR = Path(__file__).resolve().parent
WORKSPACE_DIR = BASE_DIR.parent
TRAJECTORY3D_ROOT = WORKSPACE_DIR / "output" / "trajectory_3d"
BALLISTIC_ROOT = WORKSPACE_DIR / "output" / "trajectory_ballistic"
OUTPUT_ROOT = WORKSPACE_DIR / "output" / "reprojection_overlay"


@dataclass
class CameraConfig:
    name: str
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    rvec: np.ndarray
    tvec: np.ndarray


@dataclass
class VideoInfo:
    video_path: Path
    camera_name: str
    fps: float
    frame_count: int
    frame_size: tuple[int, int]
    kick_frame: int


@dataclass
class CameraOverlayData:
    observed_times: np.ndarray
    observed_points: np.ndarray
    fit_times: np.ndarray
    fit_points: np.ndarray
    goal_line_time: float | None
    goal_line_point: np.ndarray | None


@dataclass
class SampleBundle:
    sample_name: str
    videos: list[VideoInfo]
    camera_data: dict[str, CameraOverlayData]
    fit_reprojection_rmse_px: float | None
    post_landing_model: str | None



def read_scalar(array_like, default=None):
    if array_like is None:
        return default
    arr = np.asarray(array_like)
    if arr.size == 0:
        return default
    return float(arr.reshape(-1)[0])



def load_camera_configs():
    configs = {}
    for name in ("left", "right"):
        pose_path = WORKSPACE_DIR / "output" / f"{name}_pose.npz"
        if not pose_path.exists():
            raise FileNotFoundError(f"Missing camera pose file: {pose_path}")
        pose = np.load(pose_path)
        configs[name] = CameraConfig(
            name=name,
            camera_matrix=np.asarray(pose["camera_matrix"], dtype=np.float64),
            dist_coeffs=np.asarray(pose["dist_coeffs"], dtype=np.float64),
            rvec=np.asarray(pose["rvec"], dtype=np.float64).reshape(3, 1),
            tvec=np.asarray(pose["tvec"], dtype=np.float64).reshape(3, 1),
        )
    return configs



def project_world_points(world_points, config):
    image_points, _ = cv2.projectPoints(
        np.asarray(world_points, dtype=np.float64),
        config.rvec,
        config.tvec,
        config.camera_matrix,
        config.dist_coeffs,
    )
    return image_points.reshape(-1, 2)



def load_video_infos(sample_name):
    summary_path = TRAJECTORY3D_ROOT / sample_name / "trajectory_3d_summary.json"
    if not summary_path.exists():
        raise FileNotFoundError(f"Missing 3D trajectory summary: {summary_path}")

    data = json.loads(summary_path.read_text(encoding="utf-8"))
    videos = []
    for item in data.get("videos", []):
        videos.append(
            VideoInfo(
                video_path=Path(item["video_path"]),
                camera_name=str(item["camera_name"]),
                fps=float(item["fps"]),
                frame_count=int(item["frame_count"]),
                frame_size=(int(item["frame_size"][0]), int(item["frame_size"][1])),
                kick_frame=int(item["kick_frame"]),
            )
        )
    if not videos:
        raise RuntimeError(f"No video metadata found for {sample_name}")
    return videos



def load_sample_bundle(sample_name, camera_configs):
    traj_npz_path = TRAJECTORY3D_ROOT / sample_name / "trajectory_3d_points.npz"
    fit_npz_path = BALLISTIC_ROOT / sample_name / "ballistic_fit.npz"
    fit_summary_path = BALLISTIC_ROOT / sample_name / "ballistic_fit_summary.json"
    if not traj_npz_path.exists():
        raise FileNotFoundError(f"Missing 3D trajectory file: {traj_npz_path}")
    if not fit_npz_path.exists():
        raise FileNotFoundError(f"Missing ballistic fit file: {fit_npz_path}")

    traj = np.load(traj_npz_path)
    fit = np.load(fit_npz_path)
    fit_summary = json.loads(fit_summary_path.read_text(encoding="utf-8")) if fit_summary_path.exists() else {}

    videos = load_video_infos(sample_name)
    offset_seconds = read_scalar(traj["offset_seconds"], 0.0)
    observed_times_left = np.asarray(traj["times"], dtype=np.float64)
    observed_points_left = np.asarray(traj["image_points_left"], dtype=np.float64)
    observed_points_right = np.asarray(traj["image_points_right"], dtype=np.float64)

    dense_times_left = np.asarray(fit["dense_times"], dtype=np.float64)
    dense_points = np.asarray(fit["dense_points"], dtype=np.float64)
    dense_points_left = project_world_points(dense_points, camera_configs["left"])
    dense_points_right = project_world_points(dense_points, camera_configs["right"])

    goal_line_detected = bool(int(np.asarray(fit["goal_line_crossing_detected"]).reshape(-1)[0]))
    goal_line_time_left = read_scalar(fit["goal_line_crossing_time_sec"], None) if goal_line_detected else None
    goal_line_point_world = None
    goal_line_point_left = None
    goal_line_point_right = None
    if goal_line_detected:
        goal_line_point_world = np.asarray(fit["goal_line_crossing_point"], dtype=np.float64).reshape(1, 3)
        goal_line_point_left = project_world_points(goal_line_point_world, camera_configs["left"])[0]
        goal_line_point_right = project_world_points(goal_line_point_world, camera_configs["right"])[0]

    camera_data = {
        "left": CameraOverlayData(
            observed_times=observed_times_left,
            observed_points=observed_points_left,
            fit_times=dense_times_left,
            fit_points=dense_points_left,
            goal_line_time=goal_line_time_left,
            goal_line_point=goal_line_point_left,
        ),
        "right": CameraOverlayData(
            observed_times=observed_times_left + offset_seconds,
            observed_points=observed_points_right,
            fit_times=dense_times_left + offset_seconds,
            fit_points=dense_points_right,
            goal_line_time=None if goal_line_time_left is None else goal_line_time_left + offset_seconds,
            goal_line_point=goal_line_point_right,
        ),
    }

    return SampleBundle(
        sample_name=sample_name,
        videos=videos,
        camera_data=camera_data,
        fit_reprojection_rmse_px=fit_summary.get("fit_reprojection_rmse_px"),
        post_landing_model=fit_summary.get("post_landing_fit", {}).get("model"),
    )



def clip_points_to_frame(points, width, height, margin=24):
    if len(points) == 0:
        return points
    mask = np.all(np.isfinite(points), axis=1)
    mask &= points[:, 0] >= -margin
    mask &= points[:, 0] < width + margin
    mask &= points[:, 1] >= -margin
    mask &= points[:, 1] < height + margin
    return points[mask]



def draw_polyline(frame, points, color, thickness):
    points = clip_points_to_frame(np.asarray(points, dtype=np.float64), frame.shape[1], frame.shape[0])
    if len(points) < 2:
        return
    poly = np.round(points).astype(np.int32).reshape(-1, 1, 2)
    cv2.polylines(frame, [poly], False, color, thickness, cv2.LINE_AA)



def draw_points(frame, points, color, radius, stroke=None, max_points=None):
    points = np.asarray(points, dtype=np.float64)
    if max_points is not None and len(points) > max_points:
        points = points[-max_points:]
    points = clip_points_to_frame(points, frame.shape[1], frame.shape[0])
    for point in points:
        center = tuple(np.round(point).astype(np.int32))
        if stroke is not None:
            cv2.circle(frame, center, radius + 2, stroke, -1, cv2.LINE_AA)
        cv2.circle(frame, center, radius, color, -1, cv2.LINE_AA)



def nearest_point_at_time(times, points, rel_time, tolerance_sec):
    if len(times) == 0:
        return None
    idx = int(np.searchsorted(times, rel_time))
    candidates = []
    for cand in (idx - 1, idx):
        if 0 <= cand < len(times):
            candidates.append(cand)
    if not candidates:
        return None
    best_idx = min(candidates, key=lambda item: abs(float(times[item]) - rel_time))
    if abs(float(times[best_idx]) - rel_time) > tolerance_sec:
        return None
    point = np.asarray(points[best_idx], dtype=np.float64)
    if not np.all(np.isfinite(point)):
        return None
    return point



def draw_hud(frame, sample_name, camera_name, rel_time, fit_reprojection_rmse_px, post_landing_model):
    lines = [
        f"sample={sample_name} | camera={camera_name} | t={rel_time:.3f}s",
        "green dots = observed 2D detections",
        "orange line = fitted reprojection",
    ]
    if fit_reprojection_rmse_px is not None:
        lines.append(f"fit reproj rmse = {float(fit_reprojection_rmse_px):.2f}px")
    if post_landing_model:
        lines.append(f"post-landing model = {post_landing_model}")

    x0, y0 = 28, 24
    line_h = 28
    box_w = 620
    box_h = 18 + line_h * len(lines)
    overlay = frame.copy()
    cv2.rectangle(overlay, (x0, y0), (x0 + box_w, y0 + box_h), (18, 24, 37), -1)
    cv2.addWeighted(overlay, 0.55, frame, 0.45, 0.0, frame)
    for idx, line in enumerate(lines):
        y = y0 + 32 + idx * line_h
        cv2.putText(frame, line, (x0 + 16, y), cv2.FONT_HERSHEY_SIMPLEX, 0.74, (245, 247, 250), 2, cv2.LINE_AA)



def render_overlay_video(bundle, video_info, out_path):
    overlay_data = bundle.camera_data[video_info.camera_name]
    cap = cv2.VideoCapture(str(video_info.video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Cannot open video: {video_info.video_path}")

    frame_width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)) or video_info.frame_size[0]
    frame_height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT)) or video_info.frame_size[1]
    fourcc = cv2.VideoWriter_fourcc(*"mp4v")
    writer = cv2.VideoWriter(str(out_path), fourcc, video_info.fps, (frame_width, frame_height))
    if not writer.isOpened():
        cap.release()
        raise RuntimeError(f"Cannot create output video: {out_path}")

    print(f"  rendering {out_path.name}")
    progress_step = max(1, video_info.frame_count // 8)
    highlight_tolerance = max(1.0 / 120.0, 0.75 / max(video_info.fps, 1.0))

    frame_idx = -1
    while True:
        ok, frame = cap.read()
        if not ok:
            break
        frame_idx += 1
        rel_time = (frame_idx - video_info.kick_frame) / max(video_info.fps, 1e-6)

        fit_mask = overlay_data.fit_times <= rel_time + 1e-9
        fit_points = overlay_data.fit_points[fit_mask]
        if len(fit_points) >= 2:
            draw_polyline(frame, fit_points, (0, 166, 255), 4)
        if len(fit_points) >= 1:
            draw_points(frame, [fit_points[-1]], (0, 62, 255), 8, stroke=(255, 255, 255))

        obs_mask = overlay_data.observed_times <= rel_time + 1e-9
        observed_points = overlay_data.observed_points[obs_mask]
        if len(observed_points) >= 1:
            draw_points(frame, observed_points, (72, 219, 112), 4, max_points=500)
        current_obs = nearest_point_at_time(overlay_data.observed_times, overlay_data.observed_points, rel_time, highlight_tolerance)
        if current_obs is not None:
            draw_points(frame, [current_obs], (38, 208, 206), 7, stroke=(255, 255, 255))

        if overlay_data.goal_line_time is not None and rel_time >= overlay_data.goal_line_time - 1e-9 and overlay_data.goal_line_point is not None:
            draw_points(frame, [overlay_data.goal_line_point], (245, 114, 182), 7, stroke=(255, 255, 255))

        draw_hud(frame, bundle.sample_name, video_info.camera_name, rel_time, bundle.fit_reprojection_rmse_px, bundle.post_landing_model)
        writer.write(frame)

        if frame_idx == 0 or frame_idx == video_info.frame_count - 1 or frame_idx % progress_step == 0:
            pct = 100.0 * frame_idx / max(video_info.frame_count - 1, 1)
            print(f"    progress {frame_idx + 1}/{video_info.frame_count} ({pct:5.1f}%)")

    cap.release()
    writer.release()



def save_summary(sample_dir, bundle, generated_files):
    summary = {
        "sample_name": bundle.sample_name,
        "fit_reprojection_rmse_px": bundle.fit_reprojection_rmse_px,
        "post_landing_model": bundle.post_landing_model,
        "generated_files": [str(path) for path in generated_files],
        "videos": [
            {
                "video_path": str(info.video_path),
                "camera_name": info.camera_name,
                "fps": float(info.fps),
                "kick_frame": int(info.kick_frame),
                "frame_count": int(info.frame_count),
            }
            for info in bundle.videos
        ],
    }
    (sample_dir / "overlay_summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")



def process_sample(sample_name, camera_configs):
    bundle = load_sample_bundle(sample_name, camera_configs)
    sample_output_dir = OUTPUT_ROOT / sample_name
    sample_output_dir.mkdir(parents=True, exist_ok=True)

    generated_files = []
    print(f"\n[{sample_name}] exporting reprojection overlays")
    for video_info in bundle.videos:
        out_path = sample_output_dir / f"{video_info.video_path.stem}_reprojection_overlay.mp4"
        render_overlay_video(bundle, video_info, out_path)
        generated_files.append(out_path)

    save_summary(sample_output_dir, bundle, generated_files)
    return generated_files



def parse_args():
    parser = argparse.ArgumentParser(description="Export video overlays of observed 2D detections versus fitted trajectory reprojections.")
    parser.add_argument("--samples", nargs="*", help="Sample directories to process, e.g. sample1 sample2 sample3")
    return parser.parse_args()



def main():
    args = parse_args()
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    camera_configs = load_camera_configs()

    sample_names = args.samples if args.samples else sorted(path.name for path in BALLISTIC_ROOT.glob("sample*") if path.is_dir())
    if not sample_names:
        raise FileNotFoundError("No sample outputs found under output/trajectory_ballistic")

    for sample_name in sample_names:
        process_sample(sample_name, camera_configs)


if __name__ == "__main__":
    main()
