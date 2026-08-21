import argparse
import json
import shutil
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from scipy.optimize import least_squares

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

"""
Estimate camera extrinsics from manually clicked football field reference points.
-------------------------------------------------
Features
1. Load intrinsics by profile name such as old / new / default.
2. Each camera task can override its own intrinsics profile.
3. Intrinsics are rescaled automatically if the image resolution differs.
4. The solver tries solvePnPRansac first, then falls back to solvePnP.
5. Results are written to output/ with both canonical and profile-tagged filenames.
"""


BASE_DIR = Path(__file__).resolve().parent
OUTPUT_DIR = BASE_DIR.parent / "output"
OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
INTRINSICS_REGISTRY_PATH = OUTPUT_DIR / "intrinsics_registry.json"
LEGACY_INTRINSICS_PATH = OUTPUT_DIR / "intrinsics.npz"
DEFAULT_INTRINSICS_PATHS = {
    "default": LEGACY_INTRINSICS_PATH,
    "left": OUTPUT_DIR / "intrinsics_left.npz",
    "right": OUTPUT_DIR / "intrinsics_right.npz",
}

# Each camera uses its own intrinsics profile (left -> intrinsics_left.npz, right -> intrinsics_right.npz).
# You can override these defaults from the command line if needed.
PRESET_TASKS = {
    "left": {
        "image_path": BASE_DIR / "IMG_20260314_222628.jpg",
        "view_type": "shooter_left",
        "output_prefix": "left",
        "description": "射门视角左侧参考照片（左相机，面向球门）",
        "intrinsics_profile": "left",
    },
    "right": {
        "image_path": BASE_DIR / "IMG_20260314_222612.jpg",
        "view_type": "shooter_right",
        "output_prefix": "right",
        "description": "射门视角右侧参考照片（右相机，面向球门）",
        "intrinsics_profile": "right",
    },
}


@dataclass
class IntrinsicsBundle:
    profile_name: str
    intrinsics_path: Path
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    image_size: tuple[int, int] | None


# =========================
# 1. Load intrinsics by profile name
# =========================
def load_intrinsics_registry():
    if not INTRINSICS_REGISTRY_PATH.exists():
        return {}
    with open(INTRINSICS_REGISTRY_PATH, "r", encoding="utf-8") as f:
        return json.load(f)


def resolve_intrinsics_path(profile_name):
    profile_name = (profile_name or "default").strip()
    if not profile_name:
        profile_name = "default"

    candidate_path = Path(profile_name)
    if candidate_path.suffix.lower() == ".npz" or candidate_path.exists():
        return candidate_path.expanduser().resolve(), candidate_path.stem

    registry = load_intrinsics_registry().get("profiles", {})
    if profile_name in registry:
        registry_path = Path(registry[profile_name]["intrinsics_path"])
        return registry_path.expanduser().resolve(), profile_name

    if profile_name in DEFAULT_INTRINSICS_PATHS:
        return DEFAULT_INTRINSICS_PATHS[profile_name].resolve(), profile_name

    available = sorted(set(DEFAULT_INTRINSICS_PATHS) | set(registry))
    raise FileNotFoundError(
        f"Unknown intrinsics profile: {profile_name}. Available profiles: {available}"
    )


def load_intrinsics(profile_name):
    intrinsics_path, normalized_profile = resolve_intrinsics_path(profile_name)
    if not intrinsics_path.exists():
        raise FileNotFoundError(f"Intrinsics file not found: {intrinsics_path}")

    with np.load(intrinsics_path) as data:
        camera_matrix = np.asarray(data["camera_matrix"], dtype=np.float64)
        dist_coeffs = np.asarray(data["dist_coeffs"], dtype=np.float64)
        image_size = None
        if "image_width" in data.files and "image_height" in data.files:
            image_size = (int(data["image_width"]), int(data["image_height"]))

    return IntrinsicsBundle(
        profile_name=normalized_profile,
        intrinsics_path=intrinsics_path,
        camera_matrix=camera_matrix,
        dist_coeffs=dist_coeffs,
        image_size=image_size,
    )


def get_camera_matrix_for_image(camera_matrix, intrinsics_image_size, image_size):
    if intrinsics_image_size is None:
        return camera_matrix.copy()

    calib_w, calib_h = intrinsics_image_size
    image_w, image_h = image_size

    if (calib_w, calib_h) == (image_w, image_h):
        return camera_matrix.copy()

    scale_x = image_w / calib_w
    scale_y = image_h / calib_h

    if abs(scale_x - scale_y) > 1e-6:
        raise ValueError(
            "Cannot rescale intrinsics because the calibration size and image size have different aspect ratios. "
            f"Calibration size: {calib_w}x{calib_h}, image size: {image_w}x{image_h}. "
            "Recompute intrinsics for this resolution instead."
        )

    scaled_camera_matrix = camera_matrix.copy()
    scaled_camera_matrix[0, 0] *= scale_x
    scaled_camera_matrix[0, 2] *= scale_x
    scaled_camera_matrix[1, 1] *= scale_y
    scaled_camera_matrix[1, 2] *= scale_y
    return scaled_camera_matrix


# =========================
# 2. Football field geometry
# =========================
GOAL_WIDTH = 7.32
GOAL_HEIGHT = 2.44
PENALTY_SPOT_DIST = 11.0
GOAL_AREA_DEPTH = 5.50
GOAL_AREA_WIDTH = 18.32

HALF_GOAL = GOAL_WIDTH / 2.0
HALF_GOAL_AREA = GOAL_AREA_WIDTH / 2.0

WORLD_POINTS = {
    "left_post_bottom": np.array([HALF_GOAL, 0.0, 0.0], dtype=np.float64),
    "right_post_bottom": np.array([-HALF_GOAL, 0.0, 0.0], dtype=np.float64),
    "left_post_top": np.array([HALF_GOAL, 0.0, GOAL_HEIGHT], dtype=np.float64),
    "right_post_top": np.array([-HALF_GOAL, 0.0, GOAL_HEIGHT], dtype=np.float64),
    "penalty_spot": np.array([0.0, PENALTY_SPOT_DIST, 0.0], dtype=np.float64),
    "left_goal_area_goal_line_intersection": np.array([HALF_GOAL_AREA, 0.0, 0.0], dtype=np.float64),
    "right_goal_area_goal_line_intersection": np.array([-HALF_GOAL_AREA, 0.0, 0.0], dtype=np.float64),
    "left_goal_area_corner": np.array([HALF_GOAL_AREA, GOAL_AREA_DEPTH, 0.0], dtype=np.float64),
    "right_goal_area_corner": np.array([-HALF_GOAL_AREA, GOAL_AREA_DEPTH, 0.0], dtype=np.float64),
}

POINT_LABELS = {
    "left_post_bottom": "left post base (射门视角)",
    "right_post_bottom": "right post base (射门视角)",
    "left_post_top": "left post top (射门视角)",
    "right_post_top": "right post top (射门视角)",
    "penalty_spot": "penalty spot",
    "left_goal_area_goal_line_intersection": "left small-box / goal-line intersection",
    "right_goal_area_goal_line_intersection": "right small-box / goal-line intersection",
    "left_goal_area_corner": "left goal-area corner (射门视角)",
    "right_goal_area_corner": "right goal-area corner (射门视角)",
}

# 坐标系：射门球员视角（面朝球门）
#   X: 球员左手为正(+), 右手为负(-)  即 左柱 x=+3.66, 右柱 x=-3.66
#   Y: 球门线 y=0, 罚球点 y=11（远离球门为正）
#   "shooter_left"  = 射门视角左侧照片（左相机，站在场地左侧面向球门）
#   "shooter_right" = 射门视角右侧照片（右相机，站在场地右侧面向球门）
#   小禁区侧线在球门线处的交点与外角构成已知 5.5m 线段；左相机(场地左侧)
#   对应射手视角右侧小禁区，右相机反之。
POINT_ORDERS = {
    "shooter_left": [
        "left_post_bottom",
        "right_post_bottom",
        "left_post_top",
        "right_post_top",
        "penalty_spot",
        "right_goal_area_goal_line_intersection",
        "right_goal_area_corner",
    ],
    "shooter_right": [
        "left_post_bottom",
        "right_post_bottom",
        "left_post_top",
        "right_post_top",
        "penalty_spot",
        "left_goal_area_goal_line_intersection",
        "left_goal_area_corner",
    ],
}


class PointClicker:
    def __init__(self, image, point_names, win_name="click_points"):
        self.base = image.copy()
        self.point_names = point_names
        self.win_name = win_name
        self.points = []

    def redraw(self):
        canvas = self.base.copy()

        for i, (x, y) in enumerate(self.points):
            label = POINT_LABELS.get(self.point_names[i], self.point_names[i])
            cv2.circle(canvas, (x, y), 6, (0, 0, 255), -1)
            cv2.putText(
                canvas,
                f"{i + 1}:{label}",
                (x + 8, y - 8),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.65,
                (0, 255, 255),
                2,
                cv2.LINE_AA,
            )

        if len(self.points) < len(self.point_names):
            next_name = POINT_LABELS.get(self.point_names[len(self.points)], self.point_names[len(self.points)])
            tip = f"Next: {len(self.points) + 1}/{len(self.point_names)} -> {next_name}"
        else:
            tip = "All points selected. Press Enter to confirm."

        cv2.putText(canvas, tip, (30, 40), cv2.FONT_HERSHEY_SIMPLEX, 0.9,
                    (0, 255, 0), 2, cv2.LINE_AA)
        cv2.putText(canvas, "click: select | r: reset | Backspace: delete last | Enter: confirm | q: quit",
                    (30, 80), cv2.FONT_HERSHEY_SIMPLEX, 0.65,
                    (255, 255, 255), 2, cv2.LINE_AA)

        cv2.imshow(self.win_name, canvas)

    def on_mouse(self, event, x, y, flags, param):
        if event == cv2.EVENT_LBUTTONDOWN and len(self.points) < len(self.point_names):
            self.points.append((x, y))
            self.redraw()

    def run(self):
        cv2.namedWindow(self.win_name, cv2.WINDOW_NORMAL)
        cv2.setMouseCallback(self.win_name, self.on_mouse)
        self.redraw()

        while True:
            key = cv2.waitKey(20) & 0xFF

            if key == ord("q"):
                cv2.destroyWindow(self.win_name)
                return None
            if key == ord("r"):
                self.points = []
                self.redraw()
            elif key == 8:
                if self.points:
                    self.points.pop()
                    self.redraw()
            elif key == 13:
                if len(self.points) == len(self.point_names):
                    cv2.destroyWindow(self.win_name)
                    return np.array(self.points, dtype=np.float64)


def project_points(object_points, rvec, tvec, camera_matrix, dist_coeffs):
    image_points, _ = cv2.projectPoints(
        object_points, rvec, tvec, camera_matrix, dist_coeffs
    )
    return image_points.reshape(-1, 2)


def compute_reprojection_errors(object_points, image_points, rvec, tvec, camera_matrix, dist_coeffs):
    projected = project_points(object_points, rvec, tvec, camera_matrix, dist_coeffs)
    errors = np.linalg.norm(projected - image_points, axis=1)
    return projected, errors


def refine_pose(object_points, image_points, camera_matrix, dist_coeffs, rvec, tvec):
    if hasattr(cv2, "solvePnPRefineLM"):
        try:
            rvec, tvec = cv2.solvePnPRefineLM(
                object_points,
                image_points,
                camera_matrix,
                dist_coeffs,
                rvec,
                tvec,
            )
            return rvec, tvec
        except cv2.error:
            pass

    ok, rvec, tvec = cv2.solvePnP(
        objectPoints=object_points,
        imagePoints=image_points,
        cameraMatrix=camera_matrix,
        distCoeffs=dist_coeffs,
        rvec=rvec,
        tvec=tvec,
        useExtrinsicGuess=True,
        flags=cv2.SOLVEPNP_ITERATIVE,
    )
    if not ok:
        raise RuntimeError("solvePnP ITERATIVE refinement failed")
    return rvec, tvec


def refine_pose_robust(object_points, image_points, camera_matrix, dist_coeffs, rvec, tvec):
    """Refine a pose against every reference point without letting one bad click dominate."""
    focal_px = max(float(np.mean([camera_matrix[0, 0], camera_matrix[1, 1]])), 1.0)
    initial_projected = project_points(object_points, rvec, tvec, camera_matrix, dist_coeffs)
    initial_errors = np.linalg.norm(initial_projected - image_points, axis=1) / focal_px
    median = float(np.median(initial_errors))
    mad_scale = 1.4826 * float(np.median(np.abs(initial_errors - median)))
    # Image-plane angular residual makes the loss independent of resolution
    # and focal length. One pixel is only a numerical quantisation floor.
    angular_scale = max(1.0 / focal_px, mad_scale)

    def residuals(values):
        projected = project_points(
            object_points,
            values[:3].reshape(3, 1),
            values[3:].reshape(3, 1),
            camera_matrix,
            dist_coeffs,
        )
        return ((projected - image_points) / focal_px).ravel()

    initial = np.concatenate([np.asarray(rvec, dtype=np.float64).reshape(3),
                              np.asarray(tvec, dtype=np.float64).reshape(3)])
    try:
        result = least_squares(
            residuals,
            initial,
            loss="soft_l1",
            f_scale=angular_scale,
            max_nfev=200,
        )
        return result.x[:3].reshape(3, 1), result.x[3:].reshape(3, 1)
    except (ValueError, np.linalg.LinAlgError, cv2.error):
        return rvec, tvec


def pose_robust_score(object_points, image_points, camera_matrix, dist_coeffs, rvec, tvec):
    """Rank pose hypotheses using all points in angular, robust error space."""
    projected, errors_px = compute_reprojection_errors(
        object_points, image_points, rvec, tvec, camera_matrix, dist_coeffs)
    del projected
    focal_px = max(float(np.mean([camera_matrix[0, 0], camera_matrix[1, 1]])), 1.0)
    errors = errors_px / focal_px
    median = float(np.median(errors))
    mad_scale = 1.4826 * float(np.median(np.abs(errors - median)))
    scale = max(1.0 / focal_px, mad_scale)
    normalized = errors / scale
    # Pseudo-Huber keeps the score smooth while limiting a bad manual point.
    loss = np.sqrt(1.0 + normalized ** 2) - 1.0
    camera_center = -cv2.Rodrigues(rvec)[0].T @ tvec.reshape(3)
    behind_goal_penalty = 1e3 if float(camera_center[1]) < 0.0 else 0.0
    return float(np.mean(loss) + 0.25 * np.percentile(loss, 75) + behind_goal_penalty)


def try_solve_pnp_ransac(object_points, image_points, camera_matrix, dist_coeffs, flag, solver_name, reprojection_error):
    try:
        ok, rvec, tvec, inliers = cv2.solvePnPRansac(
            objectPoints=object_points,
            imagePoints=image_points,
            cameraMatrix=camera_matrix,
            distCoeffs=dist_coeffs,
            flags=flag,
            reprojectionError=reprojection_error,
            confidence=0.99,
            iterationsCount=500,
        )
    except cv2.error:
        return None

    if not ok or inliers is None or len(inliers) < 4:
        return None

    inliers = inliers.flatten()
    obj_inliers = object_points[inliers]
    img_inliers = image_points[inliers]
    rvec, tvec = refine_pose(obj_inliers, img_inliers, camera_matrix, dist_coeffs, rvec, tvec)

    return {
        "solver_name": solver_name,
        "rvec": rvec,
        "tvec": tvec,
        "inliers": inliers,
    }


def estimate_extrinsics(object_points, image_points, camera_matrix, dist_coeffs, view_type="shooter_left"):
    methods = [
        (cv2.SOLVEPNP_EPNP, "solvePnPRansac(EPNP)", 8.0),
    ]

    if hasattr(cv2, "SOLVEPNP_SQPNP"):
        methods.append((cv2.SOLVEPNP_SQPNP, "solvePnPRansac(SQPNP)", 6.0))

    ransac_candidates = []
    for flag, solver_name, reprojection_error in methods:
        result = try_solve_pnp_ransac(
            object_points,
            image_points,
            camera_matrix,
            dist_coeffs,
            flag,
            solver_name,
            reprojection_error,
        )
        if result is not None:
            ransac_candidates.append(result)

    if ransac_candidates:
        # RANSAC proposes hypotheses only.  Evaluate every proposal against
        # every reference point and robustly refine it before committing a
        # runtime pose; returning the first successful proposal is order
        # dependent and can preserve a local, low-support solution.
        evaluated = []
        for candidate in ransac_candidates:
            for suffix, rvec, tvec in (
                ("", candidate["rvec"], candidate["tvec"]),
                (" + robust all-points refine", *refine_pose_robust(
                    object_points,
                    image_points,
                    camera_matrix,
                    dist_coeffs,
                    candidate["rvec"],
                    candidate["tvec"],
                )),
            ):
                evaluated.append({
                    "solver_name": candidate["solver_name"] + suffix,
                    "rvec": rvec,
                    "tvec": tvec,
                    "inliers": candidate["inliers"],
                })
        return min(
            evaluated,
            key=lambda item: pose_robust_score(
                object_points,
                image_points,
                camera_matrix,
                dist_coeffs,
                item["rvec"],
                item["tvec"],
            ),
        )

    # RANSAC all failed — try multiple strategies and pick best
    best_result = None
    best_error = float("inf")

    def _try_solve(method_name, rvec0=None, tvec0=None, use_guess=False, flag=cv2.SOLVEPNP_ITERATIVE):
        nonlocal best_result, best_error
        try:
            ok, rv, tv = cv2.solvePnP(
                objectPoints=object_points,
                imagePoints=image_points,
                cameraMatrix=camera_matrix,
                distCoeffs=dist_coeffs,
                rvec=rvec0 if use_guess else np.zeros((3, 1)),
                tvec=tvec0 if use_guess else np.zeros((3, 1)),
                useExtrinsicGuess=use_guess,
                flags=flag,
            )
            if ok:
                rv, tv = refine_pose(object_points, image_points, camera_matrix, dist_coeffs, rv, tv)
                C = -cv2.Rodrigues(rv)[0].T @ tv
                # reward cameras in front of goal (y > 0) 
                y_penalty = 1000.0 if C[1] < 0 else 0.0
                proj, _ = cv2.projectPoints(object_points, rv, tv, camera_matrix, dist_coeffs)
                err = float(np.mean(np.linalg.norm(proj.reshape(-1, 2) - image_points, axis=1)))
                total = err + y_penalty
                if total < best_error:
                    best_error = total
                    best_result = {"solver_name": method_name, "rvec": rv, "tvec": tv,
                                   "inliers": np.arange(len(object_points))}
        except Exception:
            pass

    # Strategy 1: EPNP directly (no RANSAC, no initial guess)
    _try_solve("EPNP direct", flag=cv2.SOLVEPNP_EPNP)

    # Strategy 2: camera in FRONT of goal (y>0), on sideline
    for side_sign, name in [(1, "right sideline"), (-1, "left sideline")]:
        cam_center = np.array([side_sign * 8.0, 16.0, 1.5], dtype=np.float64)
        # look from camera towards goal center
        look = cam_center - np.array([0.0, 0.0, 1.0])
        z_cam = look / np.linalg.norm(look)
        up = np.array([0.0, 0.0, 1.0])
        x_cam = np.cross(up, z_cam); x_cam /= np.linalg.norm(x_cam)
        y_cam = np.cross(z_cam, x_cam)
        R_init = np.column_stack([x_cam, y_cam, z_cam]).T
        rvec_init, _ = cv2.Rodrigues(R_init)
        tvec_init = (-R_init @ cam_center).reshape(3, 1)
        _try_solve(f"init {name} front", rvec_init, tvec_init, use_guess=True)

    # Strategy 3: camera BEHIND goal (y<0), as fallback
    for side_sign, name in [(1, "right"), (-1, "left")]:
        cam_center = np.array([side_sign * 8.0, -5.0, 1.5], dtype=np.float64)
        look = cam_center - np.array([0.0, 0.0, 1.0])
        z_cam = look / np.linalg.norm(look)
        up = np.array([0.0, 0.0, 1.0])
        x_cam = np.cross(up, z_cam); x_cam /= np.linalg.norm(x_cam)
        y_cam = np.cross(z_cam, x_cam)
        R_init = np.column_stack([x_cam, y_cam, z_cam]).T
        rvec_init, _ = cv2.Rodrigues(R_init)
        tvec_init = (-R_init @ cam_center).reshape(3, 1)
        _try_solve(f"init {name} behind", rvec_init, tvec_init, use_guess=True)

    if best_result is not None:
        refined_rvec, refined_tvec = refine_pose_robust(
            object_points,
            image_points,
            camera_matrix,
            dist_coeffs,
            best_result["rvec"],
            best_result["tvec"],
        )
        refined_result = {
            "solver_name": best_result["solver_name"] + " + robust all-points refine",
            "rvec": refined_rvec,
            "tvec": refined_tvec,
            "inliers": best_result["inliers"],
        }
        return min(
            (best_result, refined_result),
            key=lambda item: pose_robust_score(
                object_points,
                image_points,
                camera_matrix,
                dist_coeffs,
                item["rvec"],
                item["tvec"],
            ),
        )

    raise RuntimeError("solvePnPRansac failed and all fallback strategies also failed")


def compute_camera_center_world(rvec, tvec):
    rotation_matrix, _ = cv2.Rodrigues(rvec)
    camera_center = -rotation_matrix.T @ tvec
    return rotation_matrix, camera_center


def save_result_json(output_path, meta, point_names, object_points, image_points, projected_points,
                     errors, rvec, tvec, inliers, solver_name, camera_matrix, dist_coeffs,
                     intrinsics_bundle, working_image_size):
    rotation_matrix, camera_center = compute_camera_center_world(rvec, tvec)
    inlier_set = set(inliers.tolist())

    data = {
        "image_path": str(meta["image_path"]),
        "view_type": meta["view_type"],
        "output_prefix": meta["output_prefix"],
        "intrinsics_profile": intrinsics_bundle.profile_name,
        "intrinsics_path": str(intrinsics_bundle.intrinsics_path),
        "intrinsics_image_size": list(intrinsics_bundle.image_size) if intrinsics_bundle.image_size is not None else None,
        "working_image_size": list(working_image_size),
        "solver_name": solver_name,
        "rvec": rvec.reshape(-1).tolist(),
        "tvec": tvec.reshape(-1).tolist(),
        "rotation_matrix": rotation_matrix.tolist(),
        "camera_center_world": camera_center.reshape(-1).tolist(),
        "camera_matrix": camera_matrix.tolist(),
        "dist_coeffs": dist_coeffs.reshape(-1).tolist(),
        "mean_reprojection_error": float(np.mean(errors)),
        "max_reprojection_error": float(np.max(errors)),
        "inliers": inliers.tolist(),
        "swapped_left_right": False,
        "points": [],
    }

    for i, name in enumerate(point_names):
        data["points"].append({
            "name": name,
            "label": POINT_LABELS.get(name, name),
            "world": object_points[i].tolist(),
            "image": image_points[i].tolist(),
            "projected": projected_points[i].tolist(),
            "error": float(errors[i]),
            "is_inlier": i in inlier_set,
        })

    with open(output_path, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)


def save_result_npz(output_path, meta, object_points, image_points, rvec, tvec, inliers,
                    solver_name, camera_matrix, dist_coeffs, errors,
                    intrinsics_bundle, working_image_size):
    rotation_matrix, camera_center = compute_camera_center_world(rvec, tvec)
    np.savez(
        output_path,
        image_path=str(meta["image_path"]),
        view_type=meta["view_type"],
        output_prefix=meta["output_prefix"],
        intrinsics_profile=intrinsics_bundle.profile_name,
        intrinsics_path=str(intrinsics_bundle.intrinsics_path),
        intrinsics_image_width=-1 if intrinsics_bundle.image_size is None else intrinsics_bundle.image_size[0],
        intrinsics_image_height=-1 if intrinsics_bundle.image_size is None else intrinsics_bundle.image_size[1],
        working_image_width=working_image_size[0],
        working_image_height=working_image_size[1],
        camera_matrix=camera_matrix,
        dist_coeffs=dist_coeffs,
        object_points=object_points,
        image_points=image_points,
        rvec=rvec,
        tvec=tvec,
        R=rotation_matrix,
        camera_center_world=camera_center,
        mean_reprojection_error=float(np.mean(errors)),
        max_reprojection_error=float(np.max(errors)),
        inliers=inliers,
        swapped_left_right=False,
        solver_name=solver_name,
    )


def draw_reprojection(image, image_points, projected_points, point_names):
    vis = image.copy()
    for i, (p_img, p_prj) in enumerate(zip(image_points, projected_points)):
        xi, yi = int(round(p_img[0])), int(round(p_img[1]))
        xp, yp = int(round(p_prj[0])), int(round(p_prj[1]))

        cv2.circle(vis, (xi, yi), 6, (0, 0, 255), -1)
        cv2.circle(vis, (xp, yp), 5, (0, 255, 0), 2)
        cv2.line(vis, (xi, yi), (xp, yp), (255, 0, 0), 2)
        cv2.putText(vis, POINT_LABELS.get(point_names[i], point_names[i]), (xi + 8, yi - 8),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2, cv2.LINE_AA)
    return vis


def print_point_order(point_names):
    print("Click points in this order:")
    for i, name in enumerate(point_names, 1):
        label = POINT_LABELS.get(name, name)
        print(f"{i}. {name} ({label})")


def print_available_intrinsics_profiles():
    registry_profiles = sorted(load_intrinsics_registry().get("profiles", {}).keys())
    default_profiles = [name for name, path in DEFAULT_INTRINSICS_PATHS.items() if path.exists()]
    available = sorted(set(registry_profiles) | set(default_profiles))
    print(f"Available intrinsics profiles: {available if available else ['default (legacy output/intrinsics.npz)']}")


def resolve_task(task_name, args):
    if task_name in PRESET_TASKS:
        task = PRESET_TASKS[task_name].copy()
        image_override = getattr(args, f"{task_name}_image", None)
        if image_override:
            task["image_path"] = Path(image_override).expanduser()
        task["image_path"] = Path(task["image_path"]).resolve()
        override_profile = getattr(args, f"intrinsics_{task_name}", None)
        if override_profile:
            task["intrinsics_profile"] = override_profile
        return task

    if task_name != "custom":
        raise ValueError("Task must be one of: left / right / all / custom")

    image_path = Path(input("Enter image path: ").strip().strip('"')).expanduser().resolve()
    view_type = input("Enter view type (shooter_left / shooter_right): ").strip()
    if view_type not in POINT_ORDERS:
        raise ValueError("View type must be shooter_left or shooter_right")

    custom_profile = args.intrinsics_custom or input("Intrinsics profile (old / new / default / path): ").strip() or "default"
    output_prefix = input("Output prefix (blank = image stem): ").strip() or image_path.stem

    return {
        "image_path": image_path,
        "view_type": view_type,
        "output_prefix": output_prefix,
        "description": "Custom reference image",
        "intrinsics_profile": custom_profile,
    }


def choose_tasks(args):
    if args.tasks:
        task_names = args.tasks
        if "all" in task_names:
            if len(task_names) != 1:
                raise ValueError("--tasks may only contain all by itself")
            return [resolve_task("left", args), resolve_task("right", args)]
        return [resolve_task(task_name, args) for task_name in task_names]

    print_available_intrinsics_profiles()
    print("Task options:")
    print("  left  - 射门视角左侧照片（左相机）")
    print("  right - 射门视角右侧照片（右相机）")
    print("  all   - process both preset cameras")
    print("  custom - enter a custom image and intrinsics profile")
    choice = input("Select task [all]: ").strip().lower()

    if not choice or choice == "all":
        return [resolve_task("left", args), resolve_task("right", args)]
    if choice in ("left", "right", "custom"):
        return [resolve_task(choice, args)]
    raise ValueError("Task must be one of: left / right / all / custom")


def process_task(meta):
    image_path = Path(meta["image_path"])
    if not image_path.exists():
        raise FileNotFoundError(f"Failed to read image: {image_path}")

    image = cv2.imread(str(image_path))
    if image is None:
        raise FileNotFoundError(f"Failed to read image: {image_path}")

    intrinsics_bundle = load_intrinsics(meta["intrinsics_profile"])
    image_h, image_w = image.shape[:2]
    working_camera_matrix = get_camera_matrix_for_image(
        intrinsics_bundle.camera_matrix,
        intrinsics_bundle.image_size,
        (image_w, image_h),
    )
    if intrinsics_bundle.image_size is not None and intrinsics_bundle.image_size != (image_w, image_h):
        print(
            f"Rescaled intrinsics from calibration size: "
            f"{intrinsics_bundle.image_size[0]}x{intrinsics_bundle.image_size[1]} -> {image_w}x{image_h}"
        )

    point_names = POINT_ORDERS[meta["view_type"]]
    object_points = np.array([WORLD_POINTS[name] for name in point_names], dtype=np.float64)

    print("\n========================================")
    print(f"Output prefix: {meta['output_prefix']}")
    print(f"Image: {image_path}")
    print(f"View type: {meta['view_type']}")
    print(f"Intrinsics profile: {intrinsics_bundle.profile_name}")
    print(f"Intrinsics source: {intrinsics_bundle.intrinsics_path}")
    print_point_order(point_names)

    clicker = PointClicker(image, point_names, win_name=f"click_points_{meta['output_prefix']}")
    image_points = clicker.run()
    if image_points is None:
        print("Point selection cancelled.")
        return None

    result = estimate_extrinsics(object_points, image_points, working_camera_matrix, intrinsics_bundle.dist_coeffs, view_type=meta["view_type"])
    rvec = result["rvec"]
    tvec = result["tvec"]
    inliers = result["inliers"]
    solver_name = result["solver_name"]

    projected_points, errors = compute_reprojection_errors(
        object_points, image_points, rvec, tvec, working_camera_matrix, intrinsics_bundle.dist_coeffs
    )

    print("\n=== Extrinsics Result ===")
    print("solver =", solver_name)
    print("rvec =")
    print(rvec)
    print("tvec =")
    print(tvec)
    print(f"Mean reprojection error: {np.mean(errors):.3f} px")
    print(f"Max reprojection error: {np.max(errors):.3f} px")
    print(f"Inliers: {inliers.tolist()}")

    print("\n=== Per-Point Error ===")
    for name, err in zip(point_names, errors):
        print(f"{name:>24s}: {err:.3f} px")

    canonical_prefix = meta["output_prefix"]
    tagged_prefix = f"{meta['output_prefix']}_{intrinsics_bundle.profile_name}"
    json_path = OUTPUT_DIR / f"{canonical_prefix}_extrinsics.json"
    npz_path = OUTPUT_DIR / f"{canonical_prefix}_pose.npz"
    reproj_path = OUTPUT_DIR / f"{canonical_prefix}_reprojection.jpg"
    tagged_json_path = OUTPUT_DIR / f"{tagged_prefix}_extrinsics.json"
    tagged_npz_path = OUTPUT_DIR / f"{tagged_prefix}_pose.npz"
    tagged_reproj_path = OUTPUT_DIR / f"{tagged_prefix}_reprojection.jpg"

    save_result_json(
        json_path,
        meta,
        point_names,
        object_points,
        image_points,
        projected_points,
        errors,
        rvec,
        tvec,
        inliers,
        solver_name,
        working_camera_matrix,
        intrinsics_bundle.dist_coeffs,
        intrinsics_bundle,
        (image_w, image_h),
    )
    save_result_npz(
        npz_path,
        meta,
        object_points,
        image_points,
        rvec,
        tvec,
        inliers,
        solver_name,
        working_camera_matrix,
        intrinsics_bundle.dist_coeffs,
        errors,
        intrinsics_bundle,
        (image_w, image_h),
    )

    if tagged_json_path != json_path:
        save_result_json(
            tagged_json_path,
            meta,
            point_names,
            object_points,
            image_points,
            projected_points,
            errors,
            rvec,
            tvec,
            inliers,
            solver_name,
            working_camera_matrix,
            intrinsics_bundle.dist_coeffs,
            intrinsics_bundle,
            (image_w, image_h),
        )
        save_result_npz(
            tagged_npz_path,
            meta,
            object_points,
            image_points,
            rvec,
            tvec,
            inliers,
            solver_name,
            working_camera_matrix,
            intrinsics_bundle.dist_coeffs,
            errors,
            intrinsics_bundle,
            (image_w, image_h),
        )

    vis = draw_reprojection(image, image_points, projected_points, point_names)
    cv2.imwrite(str(reproj_path), vis)
    if tagged_reproj_path != reproj_path:
        cv2.imwrite(str(tagged_reproj_path), vis)

    print(f"\nSaved JSON: {json_path}")
    print(f"Saved pose NPZ: {npz_path}")
    print(f"Saved reprojection image: {reproj_path}")
    if tagged_json_path != json_path:
        print(f"Saved profile-tagged JSON: {tagged_json_path}")
        print(f"Saved profile-tagged pose NPZ: {tagged_npz_path}")
        print(f"Saved profile-tagged reprojection image: {tagged_reproj_path}")

    cv2.namedWindow(f"reprojection_check_{canonical_prefix}", cv2.WINDOW_NORMAL)
    cv2.imshow(f"reprojection_check_{canonical_prefix}", vis)
    cv2.waitKey(0)
    # 防止窗口已被关闭/未创建时 destroyWindow 抛 NULL window 错误
    try:
        if cv2.getWindowProperty(f"reprojection_check_{canonical_prefix}", cv2.WND_PROP_VISIBLE) >= 0:
            cv2.destroyWindow(f"reprojection_check_{canonical_prefix}")
    except cv2.error:
        pass

    return {
        "output_prefix": canonical_prefix,
        "intrinsics_profile": intrinsics_bundle.profile_name,
        "mean_error": float(np.mean(errors)),
    }


def parse_args():
    parser = argparse.ArgumentParser(description="Estimate camera extrinsics for football reconstruction.")
    parser.add_argument(
        "--tasks",
        nargs="*",
        choices=["left", "right", "all", "custom"],
        help="Tasks to run: left right all custom. If omitted, the script prompts interactively.",
    )
    parser.add_argument("--intrinsics-left", dest="intrinsics_left", help="Override the left task intrinsics profile")
    parser.add_argument("--intrinsics-right", dest="intrinsics_right", help="Override the right task intrinsics profile")
    parser.add_argument("--intrinsics-custom", dest="intrinsics_custom", help="Default intrinsics profile for custom tasks")
    parser.add_argument("--left-image", dest="left_image", help="射门视角左侧参考照片（左相机）")
    parser.add_argument("--right-image", dest="right_image", help="射门视角右侧参考照片（右相机）")
    parser.add_argument(
        "--install-calib",
        dest="install_calib",
        help="After both cameras succeed, atomically install runtime calibration files into this directory",
    )
    return parser.parse_args()


def install_runtime_calibration(destination_text):
    destination = Path(destination_text).expanduser().resolve()
    required = [
        "intrinsics_left.npz",
        "intrinsics_right.npz",
        "left_pose.npz",
        "right_pose.npz",
        "left_extrinsics.json",
        "right_extrinsics.json",
    ]
    missing = [name for name in required
               if not (OUTPUT_DIR / name).is_file() and not (destination / name).is_file()]
    if missing:
        raise FileNotFoundError("Cannot install incomplete calibration; missing: " + ", ".join(missing))

    destination.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix="calib_staging_", dir=str(destination.parent)))
    try:
        for name in required:
            src = OUTPUT_DIR / name if (OUTPUT_DIR / name).is_file() else destination / name
            shutil.copy2(src, staging / name)
        for name in required:
            (staging / name).replace(destination / name)
    finally:
        shutil.rmtree(staging, ignore_errors=True)
    print(f"Installed runtime calibration: {destination}")


def main():
    args = parse_args()
    tasks = choose_tasks(args)
    summary = []

    for meta in tasks:
        result = process_task(meta)
        if result is not None:
            summary.append(result)

    cv2.destroyAllWindows()

    if summary:
        print("\n===== Summary =====")
        for item in summary:
            print(
                f"{item['output_prefix']} | intrinsics={item['intrinsics_profile']} | "
                f"mean reprojection error {item['mean_error']:.3f} px"
            )
    if args.install_calib:
        if len(summary) != 2 or {item["output_prefix"] for item in summary} != {"left", "right"}:
            raise RuntimeError("Runtime installation requires successful left and right camera calibration.")
        install_runtime_calibration(args.install_calib)


if __name__ == "__main__":
    main()
