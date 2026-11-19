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

from ..workspace import get_workspace, set_workspace

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


# 包自身位置：只用于解析随包分发的默认素材，与工作区无关
BASE_DIR = Path(__file__).resolve().parent

# 输出目录不再在 import 期固化（见 workspace.py）：
#   - 默认写 <workspace>/output；
#   - `--out DIR`（CLI 的 `--calib`）显式覆盖后固定下来。
_OUTPUT_DIR_OVERRIDE = None


def set_output_dir(path):
    """显式指定内参输出目录（对应 `--out` / `--calib`）；传 None 回到工作区默认值。"""
    global _OUTPUT_DIR_OVERRIDE
    _OUTPUT_DIR_OVERRIDE = Path(path).expanduser().resolve() if path else None
    return output_dir()


def output_dir():
    """内参/摘要/可视化输出目录（惰性解析；与旧行为一致地按需创建）。"""
    path = _OUTPUT_DIR_OVERRIDE if _OUTPUT_DIR_OVERRIDE is not None else get_workspace().output
    path.mkdir(parents=True, exist_ok=True)
    return path


def corners_root_dir():
    """棋盘格角点可视化根目录（固定在工作区 output 下，不随 --out 变化）。"""
    path = get_workspace().output / "calib_video_corners"
    path.mkdir(parents=True, exist_ok=True)
    return path


def intrinsics_registry_path():
    """内参 registry JSON 路径（跟随 output_dir）。"""
    return output_dir() / "intrinsics_registry.json"

# ========== Calibration config ==========
CHECKERBOARD = (9, 6)  # (cols, rows) inner corners
SQUARE_SIZE = 23.0
MIN_SELECTED_FRAMES = 10
# Start from every valid view.  Robust rejection below removes only views that
# demonstrably disagree with the common camera model; it must never reduce the
# final set below 80% of all valid detections.
DEFAULT_SELECTION_FRACTION = 1.00
MIN_RETAINED_FRACTION = 0.80
MAX_OUTLIER_REJECTION_ROUNDS = 4
OUTLIER_MAD_SCALE = 3.5
SCAN_EVERY = 2  # 抽帧扫描：每 N 帧检测一次棋盘格，显著加速 CPU 标定（60fps 视频仍足够密集）
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
    rejected_candidates: list
    per_view_errors: list
    rejection_rounds: int
    rms: float
    mean_error: float
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    intrinsics_uncertainty: dict | None = None


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

    candidate_bases = [Path.cwd(), get_workspace().root, BASE_DIR]
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

    return default_video_specs()


def default_video_specs():
    """按 DEFAULT_VIDEO_SPECS 自动查找随包分发的默认标定视频。"""
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


def select_best_candidates(candidates, selection_fraction, max_selected_frames=None):
    """Keep a diverse majority of valid detections instead of a small fixed cap."""
    target_count = max(MIN_SELECTED_FRAMES, int(math.ceil(len(candidates) * selection_fraction)))
    if max_selected_frames is not None:
        target_count = min(target_count, max_selected_frames)
    target_count = min(target_count, len(candidates))
    if len(candidates) <= target_count:
        return candidates

    sharpness_values = [item["sharpness"] for item in candidates]
    sharpness_min = min(sharpness_values)
    sharpness_max = max(sharpness_values)

    # Incrementally maintain each candidate's distance to the selected set.
    # The previous implementation recomputed every candidate-to-selected
    # distance on every round, which becomes impractical when retaining 80%
    # of a long calibration video.
    centers = np.asarray([item["center_norm"] for item in candidates], dtype=np.float64)
    areas = np.asarray([item["area_ratio"] for item in candidates], dtype=np.float64)
    angles = np.asarray([item["angle"] for item in candidates], dtype=np.float64)
    sharpness = np.asarray(sharpness_values, dtype=np.float64)
    sharpness_score = (
        np.ones(len(candidates), dtype=np.float64)
        if sharpness_max <= sharpness_min
        else (sharpness - sharpness_min) / (sharpness_max - sharpness_min)
    )

    selected_mask = np.zeros(len(candidates), dtype=bool)
    first_index = int(np.argmax(sharpness))
    selected_mask[first_index] = True
    min_distances = np.full(len(candidates), np.inf, dtype=np.float64)

    def update_min_distances(selected_index):
        center_distance = np.linalg.norm(centers - centers[selected_index], axis=1)
        area_distance = np.abs(areas - areas[selected_index]) * 3.0
        angle_delta = np.abs(angles - angles[selected_index])
        angle_distance = np.minimum(angle_delta, 2.0 * math.pi - angle_delta) / math.pi
        distances = center_distance + area_distance + angle_distance
        np.minimum(min_distances, distances, out=min_distances)

    update_min_distances(first_index)
    while int(np.count_nonzero(selected_mask)) < target_count:
        scores = 0.55 * min_distances + 0.45 * sharpness_score
        scores[selected_mask] = -np.inf
        best_index = int(np.argmax(scores))
        selected_mask[best_index] = True
        update_min_distances(best_index)

    selected = [item for idx, item in enumerate(candidates) if selected_mask[idx]]

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
    print(f"Scanning every {SCAN_EVERY} frame(s) for chessboard detections...")

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
        if frame_index % SCAN_EVERY != 0:
            report_progress(frame_index)
            continue
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
        # OpenCV 5 中 cornerSubPix 返回 (N,2)、projectPoints 返回 (N,1,2)，
        # 统一 reshape 为 (N,2) 再计算重投影误差，避免 cv2.norm 类型不匹配
        img_pts = np.asarray(imgpoints[i]).reshape(-1, 2).astype(np.float32)
        proj_pts = np.asarray(projected).reshape(-1, 2).astype(np.float32)
        error = cv2.norm(img_pts, proj_pts, cv2.NORM_L2) / len(proj_pts)
        total_error += error
    return total_error / len(objpoints)


def compute_per_view_reprojection_errors(objpoints, imgpoints, rvecs, tvecs, camera_matrix, dist_coeffs):
    """Return one RMS pixel error for every chessboard view.

    A global RMS can hide a small number of blurred, partially occluded, or
    false-positive boards.  Per-view errors let the calibration reject those
    frames without assuming a particular video, camera, or sample count.
    """
    errors = []
    for obj, image, rvec, tvec in zip(objpoints, imgpoints, rvecs, tvecs):
        projected, _ = cv2.projectPoints(obj, rvec, tvec, camera_matrix, dist_coeffs)
        residual = np.asarray(image, dtype=np.float64).reshape(-1, 2) - projected.reshape(-1, 2)
        errors.append(float(np.sqrt(np.mean(np.sum(residual * residual, axis=1)))))
    return np.asarray(errors, dtype=np.float64)


def robust_outlier_indices(per_view_errors, min_retained_count):
    """Select only statistically exceptional views, respecting the retention floor."""
    errors = np.asarray(per_view_errors, dtype=np.float64)
    if len(errors) <= min_retained_count:
        return np.empty(0, dtype=np.int32), float("inf")

    median = float(np.median(errors))
    mad = float(np.median(np.abs(errors - median)))
    robust_sigma = 1.4826 * mad
    if robust_sigma > 1e-9:
        threshold = median + OUTLIER_MAD_SCALE * robust_sigma
    else:
        # A nearly identical error distribution has no meaningful MAD scale;
        # use a high quantile rather than rejecting arbitrary ties.
        threshold = float(np.quantile(errors, 0.95))

    candidate_indices = np.flatnonzero(errors > threshold)
    max_remove = max(0, len(errors) - min_retained_count)
    if max_remove == 0 or len(candidate_indices) == 0:
        return np.empty(0, dtype=np.int32), threshold
    if len(candidate_indices) > max_remove:
        order = np.argsort(errors[candidate_indices])[::-1]
        candidate_indices = candidate_indices[order[:max_remove]]
    return np.asarray(candidate_indices, dtype=np.int32), threshold


def robust_calibrate(objp, candidates, img_size, min_retained_count):
    """Iteratively calibrate and remove only per-view reprojection outliers."""
    active_candidates = list(candidates)
    rejected_candidates = []
    camera_matrix = None
    dist_coeffs = None
    rejection_rounds = 0

    for round_index in range(MAX_OUTLIER_REJECTION_ROUNDS + 1):
        objpoints = [objp.copy() for _ in active_candidates]
        imgpoints = [item["corners"] for item in active_candidates]
        flags = cv2.CALIB_USE_INTRINSIC_GUESS if camera_matrix is not None else 0
        # 固定 k3=0：只用 k1,k2,p1,p2。高阶 k3 在棋盘格未覆盖的
        # 图像右侧/下缘/四角会外推失控（曾解出 k3≈31/41 的病态值），
        # 固定后畸变模型温和得多，边缘/角落不再爆炸。
        flags |= cv2.CALIB_FIX_K3
        rms, camera_matrix, dist_coeffs, rvecs, tvecs = cv2.calibrateCamera(
            objpoints,
            imgpoints,
            img_size,
            camera_matrix,
            dist_coeffs,
            flags=flags,
        )
        per_view_errors = compute_per_view_reprojection_errors(
            objpoints, imgpoints, rvecs, tvecs, camera_matrix, dist_coeffs
        )
        rejected_indices, threshold = robust_outlier_indices(per_view_errors, min_retained_count)
        print(
            f"  Robust round {round_index + 1}: {len(active_candidates)} views, "
            f"median={np.median(per_view_errors):.4f}px, "
            f"threshold={threshold:.4f}px, outliers={len(rejected_indices)}"
        )
        if len(rejected_indices) == 0 or round_index == MAX_OUTLIER_REJECTION_ROUNDS:
            return (
                rms,
                camera_matrix,
                dist_coeffs,
                rvecs,
                tvecs,
                active_candidates,
                rejected_candidates,
                per_view_errors,
                rejection_rounds,
            )

        rejected_set = set(int(index) for index in rejected_indices)
        rejected_candidates.extend(active_candidates[index] for index in sorted(rejected_set))
        active_candidates = [item for index, item in enumerate(active_candidates) if index not in rejected_set]
        rejection_rounds += 1

    raise AssertionError("Unreachable robust calibration state")


def estimate_intrinsics_uncertainty(
    objpoints,
    imgpoints,
    camera_matrix,
    dist_coeffs,
    rvecs,
    tvecs,
    rms,
    fixed_k3=True,
):
    """估计内参不确定度 σ(fx,fy,cx,cy,k1,k2,p1,p2) —— 一阶灵敏度传播。

    精确的数值 Hessian 逆在 OpenCV 标定里很脆弱：每个 view 的 rvec/tvec
    是 nuisance，扰动内参时反复 re-PnP 会把"外参再优化"带来的曲率抹平，
    σ 被严重低估且随收敛质量非单调。这里改为**一阶灵敏度传播**，更稳健
    且保证噪声增大 → σ 单调增大：

        σ_θ ≈ rms · sqrt( diag( (JᵀJ)^{-1} ) )

    其中 J 是"所有 view 的重投影残差对 θ"的雅可比，θ 为内参参数
    [fx,fy,cx,cy,k1,k2,p1,p2]，rms 是观测噪声尺度。对每个 view 用当前
    已标定的 rvec/tvec（不再精化，避免抹平曲率），只对固定位姿下
    θ 的扰动做中心差分求 J 的三列。

    记号：J 为 (2*N_points, 8)，N_points 为所有 view 角点数之和。协方差
    的经典高斯近似 Cov(θ) = σ²·(JᵀJ)^{-1}，std = sqrt(diag(Cov))。
    """
    labels = ["sigma_fx", "sigma_fy", "sigma_cx", "sigma_cy", "sigma_k1", "sigma_k2", "sigma_p1", "sigma_p2"]
    # θ = [fx, fy, cx, cy, k1, k2, p1, p2]，k3 固定为 0，不参与。
    n_dist = int(np.asarray(dist_coeffs).ravel().shape[0])
    theta = np.array(
        [float(camera_matrix[0, 0]), float(camera_matrix[1, 1]),
         float(camera_matrix[0, 2]), float(camera_matrix[1, 2]),
         float(dist_coeffs.ravel()[0]), float(dist_coeffs.ravel()[1]),
         float(dist_coeffs.ravel()[2]), float(dist_coeffs.ravel()[3])],
        dtype=np.float64,
    )

    def rebuild(theta_sub):
        fx, fy, cx, cy, k1, k2, p1, p2 = [float(v) for v in theta_sub]
        K = np.array([[fx, 0.0, cx], [0.0, fy, cy], [0.0, 0.0, 1.0]], dtype=np.float64)
        d = np.zeros(n_dist, dtype=np.float64)
        d[:4] = [k1, k2, p1, p2]
        dist = d.reshape(-1, 1) if np.asarray(dist_coeffs).ndim == 2 else d
        return K, dist

    def residuals(theta_sub):
        K, dist = rebuild(theta_sub)
        rows = []
        for obj, img, rvec, tvec in zip(objpoints, imgpoints, rvecs, tvecs):
            # 用已标定 rvec/tvec，不做 re-PnP（保持位姿固定，避免抹平曲率）
            proj, _ = cv2.projectPoints(obj, rvec, tvec, K, dist)
            rows.append((proj.reshape(-1, 2) - img).ravel())
        return np.concatenate(rows) if rows else np.zeros((0,), dtype=np.float64)

    base = residuals(theta)
    n = len(theta)
    J = np.zeros((len(base), n), dtype=np.float64)
    dtheta = np.maximum(1e-3, 1e-4 * np.abs(theta))
    for i in range(n):
        ep = np.zeros(n, dtype=np.float64); ep[i] = dtheta[i]
        J[:, i] = (residuals(theta + ep) - residuals(theta - ep)) / (2.0 * dtheta[i])

    # Gauss-Newton/高斯近似协方差：Cov = σ²·(JᵀJ)^{-1}
    sigma2 = float(rms) * float(rms)
    info = J.T @ J
    try:
        cov = sigma2 * np.linalg.inv(info)
    except np.linalg.LinAlgError:
        # 伪逆兜底（有些参数可能弱可观，仍给出可用的量级）
        cov = sigma2 * np.linalg.pinv(info)
    std = np.sqrt(np.maximum(np.diag(cov), 0.0))
    return dict(zip(labels, std.tolist()))


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

    cv2.imwrite(str(output_dir() / f"undistorted_{profile_name}.jpg"), undist)
    cv2.imwrite(str(output_dir() / f"undistorted_{profile_name}_crop.jpg"), undist_crop)
    print(f"[{profile_name}] Saved undistortion preview images")


def calibrate_profile(profile_name, video_path, selection_fraction, max_selected_frames=None):
    objp = build_object_points()
    candidates, img_size = collect_candidates(video_path)
    selected_candidates = select_best_candidates(candidates, selection_fraction, max_selected_frames)

    print(
        f"[{profile_name}] Selected {len(selected_candidates)}/{len(candidates)} valid frames "
        f"({len(selected_candidates) / len(candidates):.1%}) for calibration"
    )
    print(f"[{profile_name}] Frame indices: {[item['frame_index'] for item in selected_candidates]}")

    minimum_retained = max(
        MIN_SELECTED_FRAMES,
        int(math.ceil(len(candidates) * MIN_RETAINED_FRACTION)),
    )
    if len(selected_candidates) < minimum_retained:
        raise ValueError(
            f"Initial selection has only {len(selected_candidates)} views, but robust calibration "
            f"requires at least {minimum_retained} ({MIN_RETAINED_FRACTION:.0%} of valid detections). "
            "Increase --max-selected-frames or omit it."
        )
    (
        rms,
        camera_matrix,
        dist_coeffs,
        rvecs,
        tvecs,
        selected_candidates,
        rejected_candidates,
        per_view_errors,
        rejection_rounds,
    ) = robust_calibrate(objp, selected_candidates, img_size, minimum_retained)
    objpoints = [objp.copy() for _ in selected_candidates]
    imgpoints = [item["corners"] for item in selected_candidates]
    mean_error = compute_mean_reprojection_error(
        objpoints, imgpoints, rvecs, tvecs, camera_matrix, dist_coeffs
    )

    print(
        f"[{profile_name}] Robust calibration retained {len(selected_candidates)}/{len(candidates)} "
        f"valid frames; rejected {len(rejected_candidates)} outliers in {rejection_rounds} round(s)"
    )

    corners_dir = corners_root_dir() / profile_name
    save_selected_corner_visualizations(video_path, selected_candidates, corners_dir)
    sample_frame = load_frame_by_index(video_path, selected_candidates[0]["frame_index"])
    save_undistortion_example(sample_frame, camera_matrix, dist_coeffs, profile_name)

    # 内参不确定度：用最终 K/dist 与全部保留 view 估计 σ(fx,fy,cx,cy,k1,k2,p1,p2)。
    # 供下游三角测量传播误差（协方差加权）使用；失败则置 None 不阻塞标定。
    intrinsics_uncertainty = None
    try:
        intrinsics_uncertainty = estimate_intrinsics_uncertainty(
            objpoints, imgpoints, camera_matrix, dist_coeffs, rvecs, tvecs, rms,
        )
    except Exception as exc:
        print(f"[{profile_name}] 不确定度估计失败（忽略）: {exc}")

    return CalibrationResult(
        profile_name=profile_name,
        video_path=video_path,
        image_size=img_size,
        candidates=candidates,
        selected_candidates=selected_candidates,
        rejected_candidates=rejected_candidates,
        per_view_errors=per_view_errors.tolist(),
        rejection_rounds=rejection_rounds,
        rms=float(rms),
        mean_error=float(mean_error),
        camera_matrix=camera_matrix,
        dist_coeffs=dist_coeffs,
        intrinsics_uncertainty=intrinsics_uncertainty,
    )


def save_profile_outputs(result):
    intrinsics_path = output_dir() / f"intrinsics_{result.profile_name}.npz"
    summary_path = output_dir() / f"intrinsics_{result.profile_name}_summary.json"

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
        per_view_reprojection_errors=np.asarray(result.per_view_errors, dtype=np.float64),
        rejected_frame_indices=np.array([item["frame_index"] for item in result.rejected_candidates], dtype=np.int32),
    )
    if result.intrinsics_uncertainty is not None:
        # 追加不确定度到一个独立 npz，避免破坏 savez 的原子写
        unc_path = output_dir() / f"intrinsics_{result.profile_name}_uncertainty.npz"
        np.savez(
            unc_path,
            **{k: np.array([v], dtype=np.float64) for k, v in result.intrinsics_uncertainty.items()},
        )

    summary = {
        "profile_name": result.profile_name,
        "source_video": str(result.video_path),
        "image_size": list(result.image_size),
        "num_candidates_total": int(len(result.candidates)),
        "num_selected_frames": int(len(result.selected_candidates)),
        "num_rejected_outliers": int(len(result.rejected_candidates)),
        "rejection_rounds": int(result.rejection_rounds),
        "minimum_retained_fraction": MIN_RETAINED_FRACTION,
        "per_view_reprojection_error_px": result.per_view_errors,
        "rms": float(result.rms),
        "mean_error": float(result.mean_error),
        "camera_matrix": result.camera_matrix.tolist(),
        "dist_coeffs": result.dist_coeffs.reshape(-1).tolist(),
        "intrinsics_uncertainty": result.intrinsics_uncertainty,
        "corners_dir": str((corners_root_dir() / result.profile_name).resolve()),
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
        "rejected_outlier_frames": [
            int(item["frame_index"])
            for item in result.rejected_candidates
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
        intrinsics_path = output_dir() / f"intrinsics_{result.profile_name}.npz"
        summary_path = output_dir() / f"intrinsics_{result.profile_name}_summary.json"
        registry["profiles"][result.profile_name] = {
            "intrinsics_path": str(intrinsics_path.resolve()),
            "summary_path": str(summary_path.resolve()),
            "source_video": str(result.video_path.resolve()),
            "image_size": list(result.image_size),
            "mean_error": float(result.mean_error),
        }

    with open(intrinsics_registry_path(), "w", encoding="utf-8") as f:
        json.dump(registry, f, ensure_ascii=False, indent=2)
    print(f"Saved intrinsics registry: {intrinsics_registry_path()}")


def maybe_write_legacy_copy(results, write_legacy_single):
    if len(results) != 1 or not write_legacy_single:
        return

    result = results[0]
    legacy_path = output_dir() / "intrinsics.npz"
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
        per_view_reprojection_errors=np.asarray(result.per_view_errors, dtype=np.float64),
        rejected_frame_indices=np.array([item["frame_index"] for item in result.rejected_candidates], dtype=np.int32),
    )
    print(f"[{result.profile_name}] Saved legacy intrinsics copy: {legacy_path}")


# ============================================================================
# 照片内参标定（原 calibrate_intrinsics_photos.py 并入）
# ----------------------------------------------------------------------------
# 每个目录下放该相机拍摄的多张棋盘格照片（jpg/png/bmp），建议 10 张以上，
# 多角度、多距离、覆盖画面各区域、避免反光。
# ============================================================================

IMAGE_EXTS = (".jpg", ".jpeg", ".png", ".bmp")
MIN_USABLE_IMAGES = 6


def list_images(folder):
    folder = Path(folder)
    if not folder.is_dir():
        raise FileNotFoundError(f"目录不存在: {folder}")
    return sorted(p for p in folder.iterdir()
                  if p.is_file() and p.suffix.lower() in IMAGE_EXTS)


def calibrate_folder(folder, cols, rows, square_mm, label):
    """对单个相机的一组棋盘格照片标定内参。"""
    images = list_images(folder)
    if not images:
        raise RuntimeError(f"[{label}] 目录中没有棋盘格照片: {folder}")

    objp = np.zeros((cols * rows, 3), np.float32)
    objp[:, :2] = np.mgrid[0:cols, 0:rows].T.reshape(-1, 2) * float(square_mm)

    obj_points = []
    img_points = []
    image_size = None
    used = []
    failed = []
    criteria = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 0.001)

    print(f"\n[{label}] 共 {len(images)} 张照片，逐张检测棋盘格角点 ({cols}x{rows})...")
    for img_path in images:
        img = cv2.imread(str(img_path))
        if img is None:
            failed.append((img_path.name, "无法读取"))
            continue
        gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
        if image_size is None:
            image_size = (gray.shape[1], gray.shape[0])
        ok, corners = cv2.findChessboardCorners(gray, (cols, rows), None)
        if not ok:
            failed.append((img_path.name, "未检测到角点"))
            continue
        corners2 = cv2.cornerSubPix(gray, corners, (11, 11), (-1, -1), criteria)
        obj_points.append(objp)
        img_points.append(corners2)
        used.append(img_path.name)

    print(f"[{label}] 可用 {len(used)} 张，失败 {len(failed)} 张")
    for name, reason in failed:
        print(f"      - {name}: {reason}")

    if len(obj_points) < MIN_USABLE_IMAGES:
        raise RuntimeError(
            f"[{label}] 可用棋盘格照片不足：{len(obj_points)} 张（至少需要 "
            f"{MIN_USABLE_IMAGES} 张）。请补充多角度清晰的棋盘格照片。")

    ret, mtx, dist, rvecs, tvecs = cv2.calibrateCamera(
        obj_points, img_points, image_size, None, None)
    if not ret:
        raise RuntimeError(f"[{label}] calibrateCamera 失败")

    total_err = 0.0
    for i in range(len(obj_points)):
        proj, _ = cv2.projectPoints(obj_points[i], rvecs[i], tvecs[i], mtx, dist)
        err = cv2.norm(img_points[i], proj, cv2.NORM_L2) / len(proj)
        total_err += err
    rms = total_err / len(obj_points)

    return {
        "camera_matrix": mtx,
        "dist_coeffs": dist,
        "rms": float(rms),
        "image_size": image_size,
        "used": used,
    }


def save_intrinsics(calib_dir, side, result):
    calib_dir.mkdir(parents=True, exist_ok=True)
    out = calib_dir / f"intrinsics_{side}.npz"
    np.savez(
        out,
        camera_matrix=result["camera_matrix"],
        dist_coeffs=result["dist_coeffs"],
        rms=result["rms"],
        image_width=result["image_size"][0],
        image_height=result["image_size"][1],
    )
    print(f"[{side}] 内参已保存: {out}")
    return out


def calibrate_from_photos(left_dir, right_dir, calib_dir, cols=9, rows=6, square_mm=23.0):
    """照片内参标定入口：左右相机各用一组棋盘格照片标定内参（焦距/主点/畸变）。

    结果写入 <calib_dir>/intrinsics_left.npz、intrinsics_right.npz，
    供外参标定与重建使用。返回 (左, 右) 两份标定结果。
    """
    calib_dir = Path(calib_dir).expanduser().resolve()

    left = calibrate_folder(left_dir, cols, rows, square_mm, "左相机")
    right = calibrate_folder(right_dir, cols, rows, square_mm, "右相机")

    save_intrinsics(calib_dir, "left", left)
    save_intrinsics(calib_dir, "right", right)

    print("\n===== 内参标定结果 =====")
    for side, r in (("left", left), ("right", right)):
        print(f"[{side}] RMS 重投影误差: {r['rms']:.4f} px")
        print(f"        相机矩阵:\n{r['camera_matrix']}")
        print(f"        畸变系数: {r['dist_coeffs'].ravel()}")
        print(f"        图像尺寸: {r['image_size']}")

    print("\n内参标定完成。接下来请进行外参标定（左右各一张足球场照片）。")
    return left, right

def parse_args(argv=None):
    parser = argparse.ArgumentParser(description="Calibrate one or more camera intrinsics profiles from chessboard videos.")
    parser.add_argument(
        "--video",
        action="append",
        help="Repeatable calibration video spec: profile=path, e.g. --video old=... --video new=...",
    )
    parser.add_argument(
        "--selection-fraction",
        type=float,
        default=DEFAULT_SELECTION_FRACTION,
        help="Initial fraction of valid chessboard detections (default: 1.00; robust rejection keeps at least 80%).",
    )
    parser.add_argument(
        "--max-selected-frames",
        type=int,
        default=None,
        help="Optional explicit upper limit; omit to retain the requested selection fraction.",
    )
    parser.add_argument(
        "--out",
        default=None,
        help="Output directory for intrinsics files (default: output/). Pass the calib/ directory to install them there.",
    )
    parser.add_argument(
        "--write-legacy-single",
        action="store_true",
        help="When only one profile is calibrated, also write legacy output/intrinsics.npz",
    )
    parser.add_argument("--workspace", default=None,
                        help="工作区目录（默认: AIFOOTBALL_WORKSPACE 环境变量，其次当前目录）")
    parser.add_argument("--photos", action="store_true",
                        help="照片标定模式：--left/--right 传棋盘格照片目录")
    parser.add_argument("--left", default=None, help="[--photos] 左相机棋盘格照片目录")
    parser.add_argument("--right", default=None, help="[--photos] 右相机棋盘格照片目录")
    parser.add_argument("--calib", default=None, help="[--photos] 标定输出目录（默认 calib）")
    parser.add_argument("--cols", type=int, default=9, help="[--photos] 棋盘格内角点列数（默认 9）")
    parser.add_argument("--rows", type=int, default=6, help="[--photos] 棋盘格内角点行数（默认 6）")
    parser.add_argument("--square", type=float, default=23.0, help="[--photos] 棋盘格边长 mm（默认 23）")
    return parser.parse_args(argv)


def run(specs=None, out=None, selection_fraction=DEFAULT_SELECTION_FRACTION,
        max_selected_frames=None, write_legacy_single=False):
    """视频内参标定入口（可被 CLI 在同进程内直接调用）。

    specs: [(profile_name, video_path), ...]；None 表示按 DEFAULT_VIDEO_SPECS 自动查找。
    out:   输出目录；None 表示 <workspace>/output（CLI 传 `--calib` 时指向 calib/）。
    """
    set_output_dir(out)
    if specs is None:
        specs = default_video_specs()
    specs = [(str(profile_name), Path(video_path).expanduser().resolve())
             for profile_name, video_path in specs]
    if not MIN_RETAINED_FRACTION <= selection_fraction <= 1.0:
        raise ValueError(f"--selection-fraction must be in [{MIN_RETAINED_FRACTION:.2f}, 1].")
    if max_selected_frames is not None and max_selected_frames < MIN_SELECTED_FRAMES:
        raise ValueError(f"--max-selected-frames must be at least {MIN_SELECTED_FRAMES}.")
    results = []

    for profile_name, video_path in specs:
        print("\n========================================")
        print(f"Profile: {profile_name}")
        result = calibrate_profile(
            profile_name,
            video_path,
            selection_fraction,
            max_selected_frames,
        )
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
    maybe_write_legacy_copy(results, write_legacy_single)
    return results


def main(argv=None):
    """`python -m aifootball.calibration.intrinsics` 入口。

    默认是**视频**内参标定；加 `--photos` 切到照片标定（原
    calibrate_intrinsics_photos.py 的能力，已并入 calibrate_from_photos()）。
    """
    args = parse_args(argv)
    try:
        if args.workspace:
            set_workspace(args.workspace)
        if args.photos:
            if not args.left or not args.right:
                print("[错误] --photos 需要同时给出 --left 与 --right 照片目录", file=sys.stderr)
                return 2
            calib_dir = Path(args.calib).expanduser() if args.calib else get_workspace().calib
            if not calib_dir.is_absolute():
                calib_dir = get_workspace().root / calib_dir
            calibrate_from_photos(args.left, args.right, calib_dir,
                                  cols=args.cols, rows=args.rows, square_mm=args.square)
            return 0
        run(specs=resolve_video_specs(args), out=args.out,
            selection_fraction=args.selection_fraction,
            max_selected_frames=args.max_selected_frames,
            write_legacy_single=args.write_legacy_single)
    except Exception as exc:
        print(f"[错误] {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
