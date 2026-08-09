import argparse
import json
import math
import shutil
import sys
import time
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
OUTPUT_DIR = WORKSPACE_DIR / "output"
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
CORNERS_ROOT_DIR = OUTPUT_DIR / "calib_video_corners"
CORNERS_ROOT_DIR.mkdir(parents=True, exist_ok=True)
INTRINSICS_REGISTRY_PATH = OUTPUT_DIR / "intrinsics_registry.json"

# ========== Calibration config ==========
CHECKERBOARD = (9, 6)  # (cols, rows) inner corners
SQUARE_SIZE = 23.0
MIN_SELECTED_FRAMES = 10
MAX_SELECTED_FRAMES = 25
PROGRESS_REPORT_INTERVAL_SECONDS = 0.5
CRITERIA = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 0.001)
DEFAULT_VIDEO_SPECS = {
    "old": BASE_DIR / "calib_images" / "VID_20260319_175209.mp4",
    "new": BASE_DIR / "calib_images" / "VID_20260330_122246 (2).mp4",
}


@dataclass
class CalibrationResult:
    profile_name: str
    video_path: Path
    image_size: tuple[int, int]
    candidates: list
    selected_candidates: list
    rms: float
    mean_error: float
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray


def build_object_points():
    objp = np.zeros((CHECKERBOARD[0] * CHECKERBOARD[1], 3), np.float32)
    objp[:, :2] = np.mgrid[0:CHECKERBOARD[0], 0:CHECKERBOARD[1]].T.reshape(-1, 2)
    objp *= SQUARE_SIZE
    return objp


def resolve_input_path(path_text):
    raw_path = Path(path_text.strip().strip('"')).expanduser()
    if raw_path.is_absolute():
        resolved = raw_path.resolve()
        if resolved.exists():
            return resolved
        raise FileNotFoundError(f"Path does not exist: {resolved}")

    candidate_bases = [Path.cwd(), WORKSPACE_DIR, BASE_DIR]
    attempted = []
    seen = set()
    for base in candidate_bases:
        candidate = (base / raw_path).resolve()
        candidate_key = str(candidate).lower()
        if candidate_key in seen:
            continue
        seen.add(candidate_key)
        attempted.append(candidate)
        if candidate.exists():
            return candidate

    attempted_text = " | ".join(str(item) for item in attempted)
    raise FileNotFoundError(f"Path not found: {raw_path}. Tried: {attempted_text}")


def parse_video_spec(spec):
    spec = spec.strip().strip('"')
    if not spec:
        raise ValueError("Video spec cannot be empty.")

    if "=" in spec:
        profile_name, path_text = spec.split("=", 1)
    else:
        path_text = spec
        profile_name = Path(path_text).stem

    profile_name = profile_name.strip().lower()
    if not profile_name:
        raise ValueError(f"Invalid video spec: {spec}")

    video_path = resolve_input_path(path_text)
    return profile_name, video_path


def resolve_video_specs(args):
    if args.video:
        specs = []
        seen = set()
        for item in args.video:
            profile_name, video_path = parse_video_spec(item)
            if profile_name in seen:
                raise ValueError(f"Duplicate profile name: {profile_name}")
            seen.add(profile_name)
            specs.append((profile_name, video_path))
        return specs

    specs = []
    for profile_name, video_path in DEFAULT_VIDEO_SPECS.items():
        if video_path.exists():
            specs.append((profile_name, video_path.resolve()))

    if not specs:
        raise FileNotFoundError(
            "No calibration videos were found automatically. Provide at least one video via "
            "`--video profile=path`, for example `--video old=path --video new=path`."
        )
    return specs


def clear_directory(path):
    if not path.exists():
        return
    for child in path.iterdir():
        if child.is_dir():
            shutil.rmtree(child)
        else:
            child.unlink()


def find_chessboard_corners(gray):
    flags = cv2.CALIB_CB_ADAPTIVE_THRESH + cv2.CALIB_CB_NORMALIZE_IMAGE
    found, corners = cv2.findChessboardCorners(gray, CHECKERBOARD, flags=flags)
    if not found:
        return False, None

    corners_subpix = cv2.cornerSubPix(
        gray,
        corners,
        winSize=(11, 11),
        zeroZone=(-1, -1),
        criteria=CRITERIA,
    )
    return True, corners_subpix


def compute_candidate_metrics(gray, corners):
    points = corners.reshape(-1, 2)
    frame_h, frame_w = gray.shape[:2]

    center = points.mean(axis=0)
    center_norm = np.array([center[0] / frame_w, center[1] / frame_h], dtype=np.float64)

    min_xy = points.min(axis=0)
    max_xy = points.max(axis=0)
    bbox_w = max(max_xy[0] - min_xy[0], 1.0)
    bbox_h = max(max_xy[1] - min_xy[1], 1.0)
    area_ratio = float((bbox_w * bbox_h) / (frame_w * frame_h))

    top_left = points[0]
    top_right = points[CHECKERBOARD[0] - 1]
    angle = math.atan2(float(top_right[1] - top_left[1]), float(top_right[0] - top_left[0]))

    sharpness = float(cv2.Laplacian(gray, cv2.CV_64F).var())

    return {
        "center_norm": center_norm,
        "area_ratio": area_ratio,
        "angle": angle,
        "sharpness": sharpness,
    }


def score_candidate(candidate, selected_candidates, sharpness_min, sharpness_max):
    if sharpness_max > sharpness_min:
        sharpness_score = (candidate["sharpness"] - sharpness_min) / (sharpness_max - sharpness_min)
    else:
        sharpness_score = 1.0

    if not selected_candidates:
        return sharpness_score

    distances = []
    for other in selected_candidates:
        center_dist = float(np.linalg.norm(candidate["center_norm"] - other["center_norm"]))
        area_dist = abs(candidate["area_ratio"] - other["area_ratio"]) * 3.0
        angle_delta = abs(candidate["angle"] - other["angle"])
        angle_delta = min(angle_delta, 2.0 * math.pi - angle_delta)
        angle_dist = angle_delta / math.pi
        distances.append(center_dist + area_dist + angle_dist)

    diversity_score = min(distances)
    return 0.55 * diversity_score + 0.45 * sharpness_score


def select_best_candidates(candidates, max_selected_frames):
    if len(candidates) <= max_selected_frames:
        return candidates

    sharpness_values = [item["sharpness"] for item in candidates]
    sharpness_min = min(sharpness_values)
    sharpness_max = max(sharpness_values)

    remaining = candidates[:]
    remaining.sort(key=lambda item: item["sharpness"], reverse=True)
    selected = [remaining.pop(0)]

    while remaining and len(selected) < max_selected_frames:
        best_index = None
        best_score = None

        for idx, candidate in enumerate(remaining):
            score = score_candidate(candidate, selected, sharpness_min, sharpness_max)
            if best_score is None or score > best_score:
                best_score = score
                best_index = idx

        selected.append(remaining.pop(best_index))

    selected.sort(key=lambda item: item["frame_index"])
    return selected


def collect_candidates(video_path):
    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Failed to open video: {video_path}")

    total_frames = int(cap.get(cv2.CAP_PROP_FRAME_COUNT))
    fps = cap.get(cv2.CAP_PROP_FPS)
    frame_width = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    frame_height = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

    print(f"Video: {video_path}")
    print(f"Resolution: {frame_width}x{frame_height}")
    if total_frames > 0:
        print(f"Frame count: {total_frames}")
    if fps > 0:
        print(f"FPS: {fps:.3f}")
    print("Scanning every frame for chessboard detections...")

    candidates = []
    frame_index = -1
    last_report_time = 0.0

    def report_progress(current_frame_index, force=False):
        nonlocal last_report_time
        now = time.monotonic()
        if not force and (now - last_report_time) < PROGRESS_REPORT_INTERVAL_SECONDS:
            return

        frames_done = max(current_frame_index + 1, 0)
        if total_frames > 0:
            percent = min(100.0, (frames_done / total_frames) * 100.0)
            progress_text = (
                f"\rProgress: {frames_done}/{total_frames} frames "
                f"({percent:6.2f}%) | detections: {len(candidates)}"
            )
        else:
            progress_text = f"\rProgress: {frames_done} frames | detections: {len(candidates)}"

        print(progress_text, end="", flush=True)
        last_report_time = now

    while True:
        ok, frame = cap.read()
        if not ok:
            break

        frame_index += 1
        gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
        found, corners = find_chessboard_corners(gray)
        if not found:
            report_progress(frame_index)
            continue

        metrics = compute_candidate_metrics(gray, corners)
        candidates.append(
            {
                "frame_index": frame_index,
                "corners": corners,
                "center_norm": metrics["center_norm"],
                "area_ratio": metrics["area_ratio"],
                "angle": metrics["angle"],
                "sharpness": metrics["sharpness"],
            }
        )
        report_progress(frame_index)

    cap.release()
    report_progress(frame_index, force=True)
    print()

    print(f"Found {len(candidates)} valid chessboard frames")
    if len(candidates) < MIN_SELECTED_FRAMES:
        raise RuntimeError(
            f"Only {len(candidates)} chessboard detections were found; "
            f"need at least {MIN_SELECTED_FRAMES}, and 10-15 good frames are recommended."
        )

    return candidates, (frame_width, frame_height)


def load_frame_by_index(video_path, frame_index):
    cap = cv2.VideoCapture(str(video_path))
    if not cap.isOpened():
        raise RuntimeError(f"Failed to reopen video: {video_path}")

    cap.set(cv2.CAP_PROP_POS_FRAMES, frame_index)
    ok, frame = cap.read()
    cap.release()

    if not ok or frame is None:
        raise RuntimeError(f"Failed to decode frame index {frame_index}.")
    return frame


def save_selected_corner_visualizations(video_path, selected_candidates, output_dir):
    output_dir.mkdir(parents=True, exist_ok=True)
    clear_directory(output_dir)
    for idx, candidate in enumerate(selected_candidates, 1):
        frame = load_frame_by_index(video_path, candidate["frame_index"])
        vis = frame.copy()
        cv2.drawChessboardCorners(vis, CHECKERBOARD, candidate["corners"], True)
        save_path = output_dir / f"corners_selected_{idx:02d}_frame_{candidate['frame_index']:06d}.jpg"
        cv2.imwrite(str(save_path), vis)


def compute_mean_reprojection_error(objpoints, imgpoints, rvecs, tvecs, camera_matrix, dist_coeffs):
    total_error = 0.0
    for i in range(len(objpoints)):
        projected, _ = cv2.projectPoints(
            objpoints[i], rvecs[i], tvecs[i], camera_matrix, dist_coeffs
        )
        error = cv2.norm(imgpoints[i], projected, cv2.NORM_L2) / len(projected)
        total_error += error
    return total_error / len(objpoints)


def save_undistortion_example(sample_frame, camera_matrix, dist_coeffs, profile_name):
    h, w = sample_frame.shape[:2]
    new_camera_matrix, roi = cv2.getOptimalNewCameraMatrix(
        camera_matrix, dist_coeffs, (w, h), 1, (w, h)
    )
    undist = cv2.undistort(sample_frame, camera_matrix, dist_coeffs, None, new_camera_matrix)

    x, y, rw, rh = roi
    if rw > 0 and rh > 0:
        undist_crop = undist[y:y + rh, x:x + rw]
    else:
        undist_crop = undist

    cv2.imwrite(str(OUTPUT_DIR / f"undistorted_{profile_name}.jpg"), undist)
    cv2.imwrite(str(OUTPUT_DIR / f"undistorted_{profile_name}_crop.jpg"), undist_crop)
    print(f"[{profile_name}] Saved undistortion preview images")


def calibrate_profile(profile_name, video_path, max_selected_frames):
    objp = build_object_points()
    candidates, img_size = collect_candidates(video_path)
    selected_candidates = select_best_candidates(candidates, max_selected_frames)

    print(f"[{profile_name}] Selected {len(selected_candidates)} frames for calibration")
    print(f"[{profile_name}] Frame indices: {[item['frame_index'] for item in selected_candidates]}")

    objpoints = [objp.copy() for _ in selected_candidates]
    imgpoints = [item["corners"] for item in selected_candidates]

    rms, camera_matrix, dist_coeffs, rvecs, tvecs = cv2.calibrateCamera(
        objpoints,
        imgpoints,
        img_size,
        None,
        None,
    )
    mean_error = compute_mean_reprojection_error(
        objpoints, imgpoints, rvecs, tvecs, camera_matrix, dist_coeffs
    )

    corners_dir = CORNERS_ROOT_DIR / profile_name
    save_selected_corner_visualizations(video_path, selected_candidates, corners_dir)
    sample_frame = load_frame_by_index(video_path, selected_candidates[0]["frame_index"])
    save_undistortion_example(sample_frame, camera_matrix, dist_coeffs, profile_name)

    return CalibrationResult(
        profile_name=profile_name,
        video_path=video_path,
        image_size=img_size,
        candidates=candidates,
        selected_candidates=selected_candidates,
        rms=float(rms),
        mean_error=float(mean_error),
        camera_matrix=camera_matrix,
        dist_coeffs=dist_coeffs,
    )


def save_profile_outputs(result):
    intrinsics_path = OUTPUT_DIR / f"intrinsics_{result.profile_name}.npz"
    summary_path = OUTPUT_DIR / f"intrinsics_{result.profile_name}_summary.json"

    np.savez(
        intrinsics_path,
        profile_name=result.profile_name,
        camera_matrix=result.camera_matrix,
        dist_coeffs=result.dist_coeffs,
        rms=result.rms,
        mean_error=result.mean_error,
        image_width=result.image_size[0],
        image_height=result.image_size[1],
        source_video=str(result.video_path),
        selected_frame_indices=np.array([item["frame_index"] for item in result.selected_candidates], dtype=np.int32),
        selected_frame_sharpness=np.array([item["sharpness"] for item in result.selected_candidates], dtype=np.float64),
    )

    summary = {
        "profile_name": result.profile_name,
        "source_video": str(result.video_path),
        "image_size": list(result.image_size),
        "num_candidates_total": int(len(result.candidates)),
        "num_selected_frames": int(len(result.selected_candidates)),
        "rms": float(result.rms),
        "mean_error": float(result.mean_error),
        "camera_matrix": result.camera_matrix.tolist(),
        "dist_coeffs": result.dist_coeffs.reshape(-1).tolist(),
        "corners_dir": str((CORNERS_ROOT_DIR / result.profile_name).resolve()),
        "selected_frames": [
            {
                "frame_index": int(item["frame_index"]),
                "sharpness": float(item["sharpness"]),
                "area_ratio": float(item["area_ratio"]),
                "center_norm": [float(v) for v in item["center_norm"]],
                "angle": float(item["angle"]),
            }
            for item in result.selected_candidates
        ],
    }
    with open(summary_path, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)

    print(f"[{result.profile_name}] Saved intrinsics: {intrinsics_path}")
    print(f"[{result.profile_name}] Saved summary: {summary_path}")
    return intrinsics_path, summary_path


def write_registry(results):
    registry = {
        "profiles": {},
    }
    for result in results:
        intrinsics_path = OUTPUT_DIR / f"intrinsics_{result.profile_name}.npz"
        summary_path = OUTPUT_DIR / f"intrinsics_{result.profile_name}_summary.json"
        registry["profiles"][result.profile_name] = {
            "intrinsics_path": str(intrinsics_path.resolve()),
            "summary_path": str(summary_path.resolve()),
            "source_video": str(result.video_path.resolve()),
            "image_size": list(result.image_size),
            "mean_error": float(result.mean_error),
        }

    with open(INTRINSICS_REGISTRY_PATH, "w", encoding="utf-8") as f:
        json.dump(registry, f, ensure_ascii=False, indent=2)
    print(f"Saved intrinsics registry: {INTRINSICS_REGISTRY_PATH}")


def maybe_write_legacy_copy(results, write_legacy_single):
    if len(results) != 1 or not write_legacy_single:
        return

    result = results[0]
    legacy_path = OUTPUT_DIR / "intrinsics.npz"
    np.savez(
        legacy_path,
        profile_name=result.profile_name,
        camera_matrix=result.camera_matrix,
        dist_coeffs=result.dist_coeffs,
        rms=result.rms,
        mean_error=result.mean_error,
        image_width=result.image_size[0],
        image_height=result.image_size[1],
        source_video=str(result.video_path),
        selected_frame_indices=np.array([item["frame_index"] for item in result.selected_candidates], dtype=np.int32),
        selected_frame_sharpness=np.array([item["sharpness"] for item in result.selected_candidates], dtype=np.float64),
    )
    print(f"[{result.profile_name}] Saved legacy intrinsics copy: {legacy_path}")


def parse_args():
    parser = argparse.ArgumentParser(description="Calibrate one or more camera intrinsics profiles from chessboard videos.")
    parser.add_argument(
        "--video",
        action="append",
        help="Repeatable calibration video spec: profile=path, e.g. --video old=... --video new=...",
    )
    parser.add_argument("--max-selected-frames", type=int, default=MAX_SELECTED_FRAMES)
    parser.add_argument(
        "--write-legacy-single",
        action="store_true",
        help="When only one profile is calibrated, also write legacy output/intrinsics.npz",
    )
    return parser.parse_args()


def main():
    args = parse_args()
    specs = resolve_video_specs(args)
    results = []

    for profile_name, video_path in specs:
        print("\n========================================")
        print(f"Profile: {profile_name}")
        result = calibrate_profile(profile_name, video_path, args.max_selected_frames)
        save_profile_outputs(result)
        results.append(result)

        print(f"\n===== [{profile_name}] Calibration Summary =====")
        print("RMS reprojection error:", result.rms)
        print("Camera matrix:")
        print(result.camera_matrix)
        print("Distortion coefficients:")
        print(result.dist_coeffs.ravel())
        print("Mean reprojection error:", result.mean_error)

    write_registry(results)
    maybe_write_legacy_copy(results, args.write_legacy_single)


if __name__ == "__main__":
    main()
