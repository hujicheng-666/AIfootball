import argparse
import json
import math
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
    "left_goal_area_corner": np.array([HALF_GOAL_AREA, GOAL_AREA_DEPTH, 0.0], dtype=np.float64),
    "right_goal_area_corner": np.array([-HALF_GOAL_AREA, GOAL_AREA_DEPTH, 0.0], dtype=np.float64),
}

POINT_LABELS = {
    "left_post_bottom": "left post base (射门视角)",
    "right_post_bottom": "right post base (射门视角)",
    "left_post_top": "left post top (射门视角)",
    "right_post_top": "right post top (射门视角)",
    "penalty_spot": "penalty spot",
    "left_goal_area_corner": "left goal-area corner (射门视角)",
    "right_goal_area_corner": "right goal-area corner (射门视角)",
}

# 坐标系：射门球员视角（面朝球门）
#   X: 球员左手为正(+), 右手为负(-)  即 左柱 x=+3.66, 右柱 x=-3.66
#   Y: 球门线 y=0, 罚球点 y=11（远离球门为正）
#   "shooter_left"  = 射门视角左侧照片（左相机，站在场地左侧面向球门）
#   "shooter_right" = 射门视角右侧照片（右相机，站在场地右侧面向球门）
#   第6个禁区角：左相机(场地左侧)画面中心偏右 → 点射手视角右侧禁区角；右相机反之。
POINT_ORDERS = {
    "shooter_left": [
        "left_post_bottom",
        "right_post_bottom",
        "left_post_top",
        "right_post_top",
        "penalty_spot",
        "right_goal_area_corner",
    ],
    "shooter_right": [
        "left_post_bottom",
        "right_post_bottom",
        "left_post_top",
        "right_post_top",
        "penalty_spot",
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


def try_solve_ippe_plane(object_points, image_points, camera_matrix, dist_coeffs, plane_eps=1e-3):
    """用 RO 地面共面子集做闭式全局最优求解（IPPE），返回候选或 None。

    参考点里门柱底两点、罚球点、小禁区角共 4 点落在 z=0 地面平面，
    cv2.SOLVEPNP_IPPE 是该平面 PnP 的闭式全局最优解算器（相对平面内
    的两义解取投影误差更小者），比 EPNP/SQPNP 对共面配置更稳、无局部
    极小。求解结果作为初始位姿，再用**全部点**（含离面门柱顶）做鲁棒
    精化——IPPE 负责给出一个可信的共面初值，精化负责吸收离面点。

    注意：IPPE 要求所有"传入的" objectPoints 严格共面，故这里只喂
    ground 子集；离面点仅在之后的 refine_pose_robust 中使用。
    """
    obj = np.asarray(object_points, dtype=np.float64).reshape(-1, 3)
    if obj.shape[0] < 4:
        return None
    # 挑共面（z≈0）子集
    ground_mask = np.abs(obj[:, 2]) <= plane_eps
    if int(ground_mask.sum()) < 4:
        return None
    ground_obj = obj[ground_mask]
    ground_img = np.asarray(image_points, dtype=np.float64).reshape(-1, 2)[ground_mask]
    if ground_obj.shape[0] < 4:
        return None

    # 共面判定：IPPE 要求同一平面，跳过异面情形
    if not hasattr(cv2, "SOLVEPNP_IPPE"):
        return None

    best = None
    for use_guess, rvec0 in ((False, None),):
        try:
            if use_guess:
                ok, rvec, tvec = cv2.solvePnP(
                    objectPoints=ground_obj, imagePoints=ground_img,
                    cameraMatrix=camera_matrix, distCoeffs=dist_coeffs,
                    rvec=rvec0, tvec=np.zeros((3, 1)),
                    flags=cv2.SOLVEPNP_IPPE)
            else:
                ok, rvec, tvec = cv2.solvePnP(
                    objectPoints=ground_obj, imagePoints=ground_img,
                    cameraMatrix=camera_matrix, distCoeffs=dist_coeffs,
                    flags=cv2.SOLVEPNP_IPPE)
        except cv2.error:
            continue
        if not ok:
            continue
        # IPPE 返回相对 ground 平面的位姿；ground 平面即世界 z=0 平面，
        # 因为 objectPoints 已是世界坐标，故 rvec/tvec 即世界系位姿。
        # 用全部点（含离面门柱顶）做鲁棒精化。
        rvec_r, tvec_r = refine_pose_robust(
            object_points, image_points, camera_matrix, dist_coeffs, rvec, tvec)
        # 过滤明显跑到门后的解（相机应在 y>0 的场地同侧）
        C = -cv2.Rodrigues(rvec_r)[0].T @ tvec_r
        if C[1] < 0:
            continue
        score = pose_robust_score(object_points, image_points, camera_matrix,
                                  dist_coeffs, rvec_r, tvec_r)
        if best is None or score < best["score"]:
            best = {
                "solver_name": "solvePnP(IPPE) + robust all-points refine",
                "rvec": rvec_r,
                "tvec": tvec_r,
                "inliers": np.arange(len(ground_obj)),
                "score": score,
                "_ippe": True,
            }
    return best


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

    # IPPE 共面全局最优假设：4 个地面点闭式求解 + 全点鲁棒精化。
    # 与上方 EPNP/SQPNP 并列为一个候选，进入统一 pose_robust_score 评选，
    # 不会破坏原流程，仅在共面配置下提供更稳的初值。
    try:
        ippe_candidate = try_solve_ippe_plane(
            object_points, image_points, camera_matrix, dist_coeffs)
        if ippe_candidate is not None:
            ransac_candidates.append(ippe_candidate)
    except Exception:
        pass

    if ransac_candidates:
        # RANSAC proposes hypotheses only.  Evaluate every proposal against
        # every reference point and robustly refine it before committing a
        # runtime pose; returning the first successful proposal is order
        # dependent and can preserve a local, low-support solution.
        evaluated = []
        for candidate in ransac_candidates:
            if candidate.get("_ippe"):
                # IPPE 候选已在 try_solve_ippe_plane 内做过全点鲁棒精化，
                # 这里直接入评，避免二次精化改变其共面初值特性。
                variants = [("", candidate["rvec"], candidate["tvec"])]
            else:
                variants = [
                    ("", candidate["rvec"], candidate["tvec"]),
                    (" + robust all-points refine", *refine_pose_robust(
                        object_points,
                        image_points,
                        camera_matrix,
                        dist_coeffs,
                        candidate["rvec"],
                        candidate["tvec"],
                    )),
                ]
            for suffix, rvec, tvec in variants:
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


def reject_single_correspondence_outlier(
    object_points, image_points, camera_matrix, dist_coeffs, initial_result, view_type,
):
    """Use leave-one-out validation to prevent one ambiguous manual click corrupting PnP.

    This is intentionally conservative: a point is rejected only when the pose
    estimated from all remaining points both improves substantially and predicts
    the withheld observation as an unmistakable outlier.  It is independent of
    point labels, camera side, and sample-specific pixel thresholds.
    """
    count = len(object_points)
    metadata = {
        "enabled": True,
        "accepted": False,
        "reason": "no validated single-point outlier",
        "candidates": [],
    }
    if count < 6:
        metadata["reason"] = "need at least six correspondences for leave-one-out validation"
        return initial_result, metadata

    _, initial_errors = compute_reprojection_errors(
        object_points, image_points, initial_result["rvec"], initial_result["tvec"],
        camera_matrix, dist_coeffs,
    )
    initial_inliers = np.asarray(initial_result["inliers"], dtype=np.int32)
    initial_mean = float(np.mean(initial_errors[initial_inliers]))
    accepted = []

    for excluded_index in range(count):
        keep_indices = np.array([index for index in range(count) if index != excluded_index], dtype=np.int32)
        try:
            subset = estimate_extrinsics(
                object_points[keep_indices], image_points[keep_indices],
                camera_matrix, dist_coeffs, view_type=view_type,
            )
        except RuntimeError:
            continue

        _, all_errors = compute_reprojection_errors(
            object_points, image_points, subset["rvec"], subset["tvec"],
            camera_matrix, dist_coeffs,
        )
        train_errors = all_errors[keep_indices]
        train_mean = float(np.mean(train_errors))
        train_median = float(np.median(train_errors))
        train_sigma = 1.4826 * float(np.median(np.abs(train_errors - train_median)))
        predicted_error = float(all_errors[excluded_index])
        expected_upper = max(12.0, train_median + 6.0 * train_sigma, train_mean * 4.0)
        _, camera_center = compute_camera_center_world(subset["rvec"], subset["tvec"])
        camera_above_ground = float(camera_center[2]) > 0.0
        materially_better = train_mean < initial_mean * 0.70
        unequivocal_outlier = predicted_error > expected_upper
        candidate_info = {
            "excluded_index": int(excluded_index),
            "training_mean_error_px": train_mean,
            "withheld_prediction_error_px": predicted_error,
            "withheld_expected_upper_px": float(expected_upper),
            "camera_height_m": float(camera_center[2]),
            "accepted": bool(materially_better and unequivocal_outlier and camera_above_ground),
        }
        metadata["candidates"].append(candidate_info)
        if candidate_info["accepted"]:
            accepted.append((train_mean, excluded_index, keep_indices, subset, candidate_info))

    if not accepted:
        return initial_result, metadata

    _, excluded_index, keep_indices, subset, selected_info = min(accepted, key=lambda item: item[0])
    metadata.update({
        "accepted": True,
        "reason": "leave-one-out validation rejected one geometrically inconsistent correspondence",
        "excluded_index": int(excluded_index),
        "training_inliers": keep_indices.tolist(),
        "selected_candidate": selected_info,
    })
    return {
        "solver_name": subset["solver_name"] + " + leave-one-out outlier rejection",
        "rvec": subset["rvec"],
        "tvec": subset["tvec"],
        "inliers": keep_indices,
    }, metadata


def calibration_line_pairs(view_type):
    """Known field segments that can be observed from the configured click anchors."""
    goal_area_side = "right" if view_type == "shooter_left" else "left"
    return [
        ("left_goal_post", "left_post_bottom", "left_post_top"),
        ("right_goal_post", "right_post_bottom", "right_post_top"),
        ("crossbar", "left_post_top", "right_post_top"),
        (
            "goal_line_to_small_box",
            f"{goal_area_side}_post_bottom",
            f"{goal_area_side}_goal_area_goal_line_intersection",
        ),
        (
            "small_box_side",
            f"{goal_area_side}_goal_area_goal_line_intersection",
            f"{goal_area_side}_goal_area_corner",
        ),
    ]


def point_line_distance(points, line_start, line_end):
    """Perpendicular image-space distance from points to an infinite 2D line."""
    points = np.asarray(points, dtype=np.float64).reshape(-1, 2)
    start = np.asarray(line_start, dtype=np.float64).reshape(2)
    end = np.asarray(line_end, dtype=np.float64).reshape(2)
    direction = end - start
    length = float(np.linalg.norm(direction))
    if length < 1e-8:
        return np.full(len(points), np.inf, dtype=np.float64)
    return np.abs(direction[0] * (points[:, 1] - start[1]) - direction[1] * (points[:, 0] - start[0])) / length


def segment_overlap_ratio(reference_start, reference_end, candidate_start, candidate_end):
    """Return candidate coverage along a reference segment, independent of direction."""
    reference_start = np.asarray(reference_start, dtype=np.float64).reshape(2)
    reference_end = np.asarray(reference_end, dtype=np.float64).reshape(2)
    candidate_start = np.asarray(candidate_start, dtype=np.float64).reshape(2)
    candidate_end = np.asarray(candidate_end, dtype=np.float64).reshape(2)
    direction = reference_end - reference_start
    length = float(np.linalg.norm(direction))
    if length < 1e-8:
        return 0.0
    unit = direction / length
    candidate_range = np.sort(np.array([
        float(np.dot(candidate_start - reference_start, unit)),
        float(np.dot(candidate_end - reference_start, unit)),
    ]))
    overlap = max(0.0, min(length, candidate_range[1]) - max(0.0, candidate_range[0]))
    return float(overlap / length)


def detect_line_near_anchors(image, image_start, image_end):
    """Find the strongest edge segment near a clicked, known field segment.

    The manual anchors make association deterministic: generic Hough lines are
    never accepted merely because they are long elsewhere in the image.
    """
    start = np.asarray(image_start, dtype=np.float64).reshape(2)
    end = np.asarray(image_end, dtype=np.float64).reshape(2)
    length = float(np.linalg.norm(end - start))
    if length < 30.0:
        return None

    direction = (end - start) / length
    normal = np.array([-direction[1], direction[0]], dtype=np.float64)
    corridor = float(np.clip(length * 0.045, 7.0, 24.0))
    extension = min(18.0, length * 0.08)
    polygon = np.array([
        start - direction * extension + normal * corridor,
        start - direction * extension - normal * corridor,
        end + direction * extension - normal * corridor,
        end + direction * extension + normal * corridor,
    ], dtype=np.int32)

    mask = np.zeros(image.shape[:2], dtype=np.uint8)
    cv2.fillConvexPoly(mask, polygon, 255)
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    edges = cv2.Canny(gray, 60, 160)
    edges = cv2.bitwise_and(edges, mask)
    min_length = max(24, int(round(length * 0.28)))
    segments = cv2.HoughLinesP(
        edges,
        rho=1.0,
        theta=np.pi / 360.0,
        threshold=max(16, int(round(length * 0.10))),
        minLineLength=min_length,
        maxLineGap=max(8, int(round(length * 0.08))),
    )
    if segments is None:
        return None

    best = None
    for item in segments.reshape(-1, 4):
        candidate_start = item[:2].astype(np.float64)
        candidate_end = item[2:].astype(np.float64)
        candidate_direction = candidate_end - candidate_start
        candidate_length = float(np.linalg.norm(candidate_direction))
        if candidate_length < min_length:
            continue
        candidate_unit = candidate_direction / candidate_length
        angle_alignment = abs(float(np.dot(candidate_unit, direction)))
        if angle_alignment < math.cos(math.radians(12.0)):
            continue
        midpoint_distance = float(point_line_distance(
            0.5 * (candidate_start + candidate_end), start, end
        )[0])
        if midpoint_distance > corridor * 0.75:
            continue
        coverage = segment_overlap_ratio(start, end, candidate_start, candidate_end)
        if coverage < 0.28:
            continue
        score = coverage * angle_alignment - 0.35 * midpoint_distance / max(corridor, 1.0)
        if best is None or score > best["score"]:
            best = {
                "image_start": candidate_start,
                "image_end": candidate_end,
                "coverage": coverage,
                "distance_px": midpoint_distance,
                "score": score,
            }
    return best


def build_line_constraints(image, view_type, point_names, object_points, image_points):
    point_index = {name: idx for idx, name in enumerate(point_names)}
    constraints = []
    for line_name, start_name, end_name in calibration_line_pairs(view_type):
        if start_name not in point_index or end_name not in point_index:
            continue
        start_idx = point_index[start_name]
        end_idx = point_index[end_name]
        detected = detect_line_near_anchors(image, image_points[start_idx], image_points[end_idx])
        if detected is None:
            continue
        constraints.append({
            "name": line_name,
            "world_start": np.asarray(object_points[start_idx], dtype=np.float64),
            "world_end": np.asarray(object_points[end_idx], dtype=np.float64),
            **detected,
        })
    return constraints


def line_constraint_rms_px(constraints, rvec, tvec, camera_matrix, dist_coeffs):
    all_distances = []
    for constraint in constraints:
        weights = np.linspace(0.05, 0.95, num=12, dtype=np.float64)[:, None]
        world_points = (
            (1.0 - weights) * constraint["world_start"]
            + weights * constraint["world_end"]
        )
        projected = project_points(world_points, rvec, tvec, camera_matrix, dist_coeffs)
        all_distances.append(point_line_distance(
            projected,
            constraint["image_start"],
            constraint["image_end"],
        ))
    if not all_distances:
        return float("inf")
    return float(np.sqrt(np.mean(np.concatenate(all_distances) ** 2)))


def refine_pose_with_line_constraints(
    image,
    view_type,
    point_names,
    object_points,
    image_points,
    camera_matrix,
    dist_coeffs,
    rvec,
    tvec,
):
    """Refine PnP pose with automatically detected, anchor-associated field lines."""
    constraints = build_line_constraints(image, view_type, point_names, object_points, image_points)
    metadata = {
        "enabled": True,
        "accepted": False,
        "reason": "no reliable image line found",
        "detected_line_count": len(constraints),
        "lines": [
            {
                "name": item["name"],
                "coverage": float(item["coverage"]),
                "anchor_distance_px": float(item["distance_px"]),
                "world_start": item["world_start"].tolist(),
                "world_end": item["world_end"].tolist(),
                "image_start": item["image_start"].tolist(),
                "image_end": item["image_end"].tolist(),
            }
            for item in constraints
        ],
    }
    if len(constraints) < 2:
        return rvec, tvec, metadata, constraints

    focal_px = max(float(np.mean([camera_matrix[0, 0], camera_matrix[1, 1]])), 1.0)
    initial = np.concatenate([
        np.asarray(rvec, dtype=np.float64).reshape(3),
        np.asarray(tvec, dtype=np.float64).reshape(3),
    ])
    initial_point_score = pose_robust_score(
        object_points, image_points, camera_matrix, dist_coeffs, rvec, tvec)
    initial_line_rms = line_constraint_rms_px(constraints, rvec, tvec, camera_matrix, dist_coeffs)

    def residuals(values):
        current_rvec = values[:3].reshape(3, 1)
        current_tvec = values[3:].reshape(3, 1)
        projected = project_points(object_points, current_rvec, current_tvec, camera_matrix, dist_coeffs)
        point_residuals = ((projected - image_points) / focal_px).ravel()
        line_residuals = []
        for constraint in constraints:
            weights = np.linspace(0.05, 0.95, num=12, dtype=np.float64)[:, None]
            world_points = (
                (1.0 - weights) * constraint["world_start"]
                + weights * constraint["world_end"]
            )
            line_projected = project_points(
                world_points, current_rvec, current_tvec, camera_matrix, dist_coeffs
            )
            # A modest weight prevents automatically extracted pixels from
            # overriding the manually verified point correspondences.
            line_residuals.append(0.28 * point_line_distance(
                line_projected,
                constraint["image_start"],
                constraint["image_end"],
            ) / focal_px)
        return np.concatenate([point_residuals, *line_residuals])

    try:
        result = least_squares(
            residuals,
            initial,
            loss="soft_l1",
            f_scale=max(1.5 / focal_px, initial_line_rms / focal_px),
            max_nfev=250,
        )
    except (ValueError, np.linalg.LinAlgError, cv2.error):
        metadata["reason"] = "line refinement solver failed"
        return rvec, tvec, metadata, constraints

    candidate_rvec = result.x[:3].reshape(3, 1)
    candidate_tvec = result.x[3:].reshape(3, 1)
    candidate_point_score = pose_robust_score(
        object_points, image_points, camera_matrix, dist_coeffs, candidate_rvec, candidate_tvec)
    candidate_line_rms = line_constraint_rms_px(
        constraints, candidate_rvec, candidate_tvec, camera_matrix, dist_coeffs)
    metadata.update({
        "initial_line_rms_px": float(initial_line_rms),
        "refined_line_rms_px": float(candidate_line_rms),
        "initial_point_score": float(initial_point_score),
        "refined_point_score": float(candidate_point_score),
    })

    line_improved = candidate_line_rms <= initial_line_rms * 0.88
    point_preserved = candidate_point_score <= initial_point_score * 1.03
    if line_improved and point_preserved:
        metadata["accepted"] = True
        metadata["reason"] = "line RMS improved without degrading point fit"
        return candidate_rvec, candidate_tvec, metadata, constraints

    metadata["reason"] = "candidate did not pass line-improvement / point-fit safeguard"
    return rvec, tvec, metadata, constraints


def compute_camera_center_world(rvec, tvec):
    rotation_matrix, _ = cv2.Rodrigues(rvec)
    camera_center = -rotation_matrix.T @ tvec
    return rotation_matrix, camera_center


def _angular_reprojection_residual(object_points, image_points, rvec, tvec, camera_matrix, dist_coeffs):
    """Image residual normalised by focal length, comparable between cameras."""
    focal_px = max(float(np.mean([camera_matrix[0, 0], camera_matrix[1, 1]])), 1.0)
    projected = project_points(object_points, rvec, tvec, camera_matrix, dist_coeffs)
    return ((projected - image_points) / focal_px).reshape(-1)


def joint_stereo_bundle_adjustment(left, right):
    """Run a guarded two-camera bundle adjustment over the clicked field points.

    The field coordinates remain *fixed* because they are measured dimensions,
    not free latent points.  This avoids inventing a camera baseline or bending
    the field to fit a bad click.  With fixed points the two pose blocks are
    mathematically almost separable; the joint solve is consequently used as a
    common robust optimisation and a consistency check, never as a reason to
    force a worse pose into the runtime calibration.
    """
    observations = (left, right)

    def active_correspondences(item):
        indices = np.asarray(item["inliers"], dtype=np.int32)
        return item["object_points"][indices], item["image_points"][indices]
    initial = np.concatenate([
        np.asarray(item["rvec"], dtype=np.float64).reshape(3)
        for item in observations
    ] + [
        np.asarray(item["tvec"], dtype=np.float64).reshape(3)
        for item in observations
    ])

    # Parameter order is [left rotation, right rotation, left translation,
    # right translation], deliberately keeping rotation and translation units
    # separate while allowing scipy's Jacobian scaling to condition the solve.
    def unpack(values, camera_index):
        rotation_offset = camera_index * 3
        translation_offset = 6 + camera_index * 3
        return (
            values[rotation_offset:rotation_offset + 3].reshape(3, 1),
            values[translation_offset:translation_offset + 3].reshape(3, 1),
        )

    initial_residual = np.concatenate([
        _angular_reprojection_residual(
            *active_correspondences(item), item["rvec"], item["tvec"],
            item["camera_matrix"], item["dist_coeffs"],
        )
        for item in observations
    ])
    median_residual = float(np.median(initial_residual))
    mad_sigma = 1.4826 * float(np.median(np.abs(initial_residual - median_residual)))
    focal_floor = min(
        1.0 / max(float(np.mean([item["camera_matrix"][0, 0], item["camera_matrix"][1, 1]])), 1.0)
        for item in observations
    )
    robust_scale = max(focal_floor, mad_sigma)

    def residuals(values):
        residual_blocks = []
        for index, item in enumerate(observations):
            rvec, tvec = unpack(values, index)
            residual_blocks.append(_angular_reprojection_residual(
                *active_correspondences(item), rvec, tvec,
                item["camera_matrix"], item["dist_coeffs"],
            ))
        return np.concatenate(residual_blocks)

    metadata = {
        "enabled": True,
        "accepted": False,
        "reason": "not evaluated",
        "loss": "soft_l1 angular reprojection",
        "world_points_fixed": True,
        "shared_robust_scale_rad": float(robust_scale),
        "cameras": {},
    }
    try:
        result = least_squares(
            residuals,
            initial,
            loss="soft_l1",
            f_scale=robust_scale,
            x_scale="jac",
            max_nfev=400,
        )
    except (ValueError, np.linalg.LinAlgError, cv2.error) as exc:
        metadata["reason"] = f"joint bundle solver failed: {type(exc).__name__}"
        return None, metadata

    candidate_residual = residuals(result.x)
    initial_rms = float(np.sqrt(np.mean(initial_residual ** 2)))
    candidate_rms = float(np.sqrt(np.mean(candidate_residual ** 2)))
    candidates = []
    per_camera_non_degraded = True
    for index, item in enumerate(observations):
        candidate_rvec, candidate_tvec = unpack(result.x, index)
        active_object_points, active_image_points = active_correspondences(item)
        _, initial_errors = compute_reprojection_errors(
            active_object_points, active_image_points, item["rvec"], item["tvec"],
            item["camera_matrix"], item["dist_coeffs"],
        )
        _, candidate_errors = compute_reprojection_errors(
            active_object_points, active_image_points, candidate_rvec, candidate_tvec,
            item["camera_matrix"], item["dist_coeffs"],
        )
        initial_mean = float(np.mean(initial_errors))
        candidate_mean = float(np.mean(candidate_errors))
        # Never exchange a small global improvement for a material regression
        # in one camera.  One percent leaves room for robust-loss trade-offs.
        per_camera_non_degraded &= candidate_mean <= initial_mean * 1.01 + 1e-9
        metadata["cameras"][item["output_prefix"]] = {
            "initial_mean_reprojection_error_px": initial_mean,
            "candidate_mean_reprojection_error_px": candidate_mean,
            "initial_max_reprojection_error_px": float(np.max(initial_errors)),
            "candidate_max_reprojection_error_px": float(np.max(candidate_errors)),
        }
        candidates.append((candidate_rvec, candidate_tvec, candidate_errors))

    metadata["initial_joint_rms_rad"] = initial_rms
    metadata["candidate_joint_rms_rad"] = candidate_rms
    metadata["solver_status"] = int(result.status)
    metadata["solver_evaluations"] = int(result.nfev)
    materially_improved = candidate_rms < initial_rms * (1.0 - 1e-4)
    if materially_improved and per_camera_non_degraded:
        metadata["accepted"] = True
        metadata["reason"] = "joint robust reprojection RMS improved without degrading either camera"
        return candidates, metadata

    metadata["reason"] = (
        "candidate rejected: no material joint improvement or a camera would regress"
    )
    return None, metadata


def save_result_json(output_path, meta, point_names, object_points, image_points, projected_points,
                     errors, rvec, tvec, inliers, solver_name, camera_matrix, dist_coeffs,
                     intrinsics_bundle, working_image_size, line_refinement=None,
                     stereo_bundle_adjustment=None, outlier_rejection=None):
    rotation_matrix, camera_center = compute_camera_center_world(rvec, tvec)
    inlier_set = set(inliers.tolist())

    inlier_errors = np.asarray(errors, dtype=np.float64)[np.asarray(inliers, dtype=np.int32)]
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
        "mean_reprojection_error": float(np.mean(inlier_errors)),
        "max_reprojection_error": float(np.max(inlier_errors)),
        "mean_all_point_reprojection_error": float(np.mean(errors)),
        "max_all_point_reprojection_error": float(np.max(errors)),
        "inliers": inliers.tolist(),
        "swapped_left_right": False,
        "points": [],
    }
    if line_refinement is not None:
        data["line_refinement"] = line_refinement
    if stereo_bundle_adjustment is not None:
        data["stereo_bundle_adjustment"] = stereo_bundle_adjustment
    if outlier_rejection is not None:
        data["outlier_rejection"] = outlier_rejection

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
    inlier_errors = np.asarray(errors, dtype=np.float64)[np.asarray(inliers, dtype=np.int32)]
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
        mean_reprojection_error=float(np.mean(inlier_errors)),
        max_reprojection_error=float(np.max(inlier_errors)),
        mean_all_point_reprojection_error=float(np.mean(errors)),
        max_all_point_reprojection_error=float(np.max(errors)),
        inliers=inliers,
        swapped_left_right=False,
        solver_name=solver_name,
    )


def draw_reprojection(image, image_points, projected_points, point_names,
                      line_constraints=None, rvec=None, tvec=None,
                      camera_matrix=None, dist_coeffs=None):
    vis = image.copy()
    for constraint in line_constraints or []:
        detected_start = tuple(np.rint(constraint["image_start"]).astype(int))
        detected_end = tuple(np.rint(constraint["image_end"]).astype(int))
        cv2.line(vis, detected_start, detected_end, (0, 165, 255), 3, cv2.LINE_AA)
        if rvec is not None and tvec is not None and camera_matrix is not None and dist_coeffs is not None:
            projected_line = project_points(
                np.asarray([constraint["world_start"], constraint["world_end"]], dtype=np.float64),
                rvec,
                tvec,
                camera_matrix,
                dist_coeffs,
            )
            cv2.line(
                vis,
                tuple(np.rint(projected_line[0]).astype(int)),
                tuple(np.rint(projected_line[1]).astype(int)),
                (255, 255, 0),
                2,
                cv2.LINE_AA,
            )
    for i, (p_img, p_prj) in enumerate(zip(image_points, projected_points)):
        xi, yi = int(round(p_img[0])), int(round(p_img[1]))
        xp, yp = int(round(p_prj[0])), int(round(p_prj[1]))

        cv2.circle(vis, (xi, yi), 6, (0, 0, 255), -1)
        cv2.circle(vis, (xp, yp), 5, (0, 255, 0), 2)
        cv2.line(vis, (xi, yi), (xp, yp), (255, 0, 0), 2)
        cv2.putText(vis, POINT_LABELS.get(point_names[i], point_names[i]), (xi + 8, yi - 8),
                    cv2.FONT_HERSHEY_SIMPLEX, 0.55, (0, 255, 255), 2, cv2.LINE_AA)
    return vis


def overwrite_task_artifacts(task, stereo_bundle_adjustment):
    """Persist the accepted joint result (or its rejection diagnostic) for one camera."""
    meta = task["meta"]
    intrinsics_bundle = task["intrinsics_bundle"]
    canonical_prefix = meta["output_prefix"]
    tagged_prefix = f"{canonical_prefix}_{intrinsics_bundle.profile_name}"
    json_path = OUTPUT_DIR / f"{canonical_prefix}_extrinsics.json"
    npz_path = OUTPUT_DIR / f"{canonical_prefix}_pose.npz"
    reproj_path = OUTPUT_DIR / f"{canonical_prefix}_reprojection.jpg"
    tagged_json_path = OUTPUT_DIR / f"{tagged_prefix}_extrinsics.json"
    tagged_npz_path = OUTPUT_DIR / f"{tagged_prefix}_pose.npz"
    tagged_reproj_path = OUTPUT_DIR / f"{tagged_prefix}_reprojection.jpg"
    projected_points, errors = compute_reprojection_errors(
        task["object_points"], task["image_points"], task["rvec"], task["tvec"],
        task["camera_matrix"], task["dist_coeffs"],
    )

    def write_pair(current_json_path, current_npz_path):
        save_result_json(
            current_json_path, meta, task["point_names"], task["object_points"],
            task["image_points"], projected_points, errors, task["rvec"], task["tvec"],
            task["inliers"], task["solver_name"], task["camera_matrix"], task["dist_coeffs"],
            intrinsics_bundle, task["working_image_size"], task["line_refinement"],
            stereo_bundle_adjustment, task["outlier_rejection"],
        )
        save_result_npz(
            current_npz_path, meta, task["object_points"], task["image_points"],
            task["rvec"], task["tvec"], task["inliers"], task["solver_name"],
            task["camera_matrix"], task["dist_coeffs"], errors, intrinsics_bundle,
            task["working_image_size"],
        )

    write_pair(json_path, npz_path)
    if tagged_json_path != json_path:
        write_pair(tagged_json_path, tagged_npz_path)

    vis = draw_reprojection(
        task["image"], task["image_points"], projected_points, task["point_names"],
        task["line_constraints"], task["rvec"], task["tvec"],
        task["camera_matrix"], task["dist_coeffs"],
    )
    cv2.imwrite(str(reproj_path), vis)
    if tagged_reproj_path != reproj_path:
        cv2.imwrite(str(tagged_reproj_path), vis)
    task["errors"] = errors
    task["mean_error"] = float(np.mean(errors[np.asarray(task["inliers"], dtype=np.int32)]))


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


def process_task(meta, enable_line_refinement=False):
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
    result, outlier_rejection = reject_single_correspondence_outlier(
        object_points,
        image_points,
        working_camera_matrix,
        intrinsics_bundle.dist_coeffs,
        result,
        meta["view_type"],
    )
    rvec = result["rvec"]
    tvec = result["tvec"]
    inliers = result["inliers"]
    solver_name = result["solver_name"]

    line_constraints = []
    line_refinement = {
        "enabled": bool(enable_line_refinement),
        "accepted": False,
        "reason": "disabled; using manual-point robust PnP only",
        "detected_line_count": 0,
        "lines": [],
    }
    if enable_line_refinement:
        rvec, tvec, line_refinement, line_constraints = refine_pose_with_line_constraints(
            image,
            meta["view_type"],
            point_names,
            object_points,
            image_points,
            working_camera_matrix,
            intrinsics_bundle.dist_coeffs,
            rvec,
            tvec,
        )
        if line_refinement["accepted"]:
            solver_name += " + automatic line refinement"

    projected_points, errors = compute_reprojection_errors(
        object_points, image_points, rvec, tvec, working_camera_matrix, intrinsics_bundle.dist_coeffs
    )
    inlier_errors = errors[np.asarray(inliers, dtype=np.int32)]

    print("\n=== Extrinsics Result ===")
    print("solver =", solver_name)
    print("rvec =")
    print(rvec)
    print("tvec =")
    print(tvec)
    print(f"Mean inlier reprojection error: {np.mean(inlier_errors):.3f} px")
    print(f"Max inlier reprojection error: {np.max(inlier_errors):.3f} px")
    if len(inlier_errors) != len(errors):
        print(f"All-point reprojection error: mean={np.mean(errors):.3f} px, max={np.max(errors):.3f} px")
    print(f"Inliers: {inliers.tolist()}")
    print(f"Correspondence validation: {outlier_rejection['reason']}")
    print(
        "Automatic line refinement: "
        f"{line_refinement['reason']} "
        f"({line_refinement['detected_line_count']} reliable lines)"
    )
    if "initial_line_rms_px" in line_refinement:
        print(
            "Line RMS: "
            f"{line_refinement['initial_line_rms_px']:.3f} px -> "
            f"{line_refinement['refined_line_rms_px']:.3f} px"
        )

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
        line_refinement,
        None,
        outlier_rejection,
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
            line_refinement,
            None,
            outlier_rejection,
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

    vis = draw_reprojection(
        image,
        image_points,
        projected_points,
        point_names,
        line_constraints,
        rvec,
        tvec,
        working_camera_matrix,
        intrinsics_bundle.dist_coeffs,
    )
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
        "mean_error": float(np.mean(inlier_errors)),
        "meta": meta,
        "image": image,
        "point_names": point_names,
        "object_points": object_points,
        "image_points": image_points,
        "rvec": rvec,
        "tvec": tvec,
        "inliers": inliers,
        "solver_name": solver_name,
        "camera_matrix": working_camera_matrix,
        "dist_coeffs": intrinsics_bundle.dist_coeffs,
        "intrinsics_bundle": intrinsics_bundle,
        "working_image_size": (image_w, image_h),
        "line_constraints": line_constraints,
        "line_refinement": line_refinement,
        "outlier_rejection": outlier_rejection,
        "errors": errors,
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
    parser.add_argument(
        "--enable-line-refinement",
        action="store_true",
        help="Experimental: refine the manual-point PnP result with automatically detected field lines.",
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
        result = process_task(meta, enable_line_refinement=args.enable_line_refinement)
        if result is not None:
            summary.append(result)

    cv2.destroyAllWindows()

    by_prefix = {item["output_prefix"]: item for item in summary}
    if {"left", "right"}.issubset(by_prefix):
        bundle_candidates, bundle_metadata = joint_stereo_bundle_adjustment(
            by_prefix["left"], by_prefix["right"]
        )
        print("\n=== Joint Stereo Bundle Adjustment ===")
        print(bundle_metadata["reason"])
        print(
            "Joint angular RMS: "
            f"{bundle_metadata.get('initial_joint_rms_rad', float('nan')):.7f} -> "
            f"{bundle_metadata.get('candidate_joint_rms_rad', float('nan')):.7f} rad"
        )
        if bundle_candidates is not None:
            for item, (rvec, tvec, errors) in zip(
                (by_prefix["left"], by_prefix["right"]), bundle_candidates
            ):
                item["rvec"] = rvec
                item["tvec"] = tvec
                item["errors"] = errors
                item["solver_name"] += " + joint stereo bundle adjustment"
        for item in (by_prefix["left"], by_prefix["right"]):
            overwrite_task_artifacts(item, bundle_metadata)
            camera_metrics = bundle_metadata["cameras"].get(item["output_prefix"], {})
            print(
                f"{item['output_prefix']}: "
                f"{camera_metrics.get('initial_mean_reprojection_error_px', float('nan')):.3f} -> "
                f"{item['mean_error']:.3f} px"
            )

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
