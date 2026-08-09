import argparse
import csv
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np
from scipy.integrate import solve_ivp
from scipy.optimize import minimize

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass


from project.config import WORKSPACE as WORKSPACE_DIR, CALIB

INPUT_ROOT = WORKSPACE_DIR / "output" / "trajectory_3d"
OUTPUT_ROOT = WORKSPACE_DIR / "output" / "trajectory_ballistic"
GRAVITY = 9.81
FIELD_X_LIMITS = (-15.0, 15.0)
FIELD_Y_LIMITS = (-5.0, 25.0)
Z_LIMITS = (-0.5, 4.5)
GOAL_LINE_Y = 0.0
GOAL_HALF_WIDTH_M = 7.32 / 2.0
GOAL_HEIGHT_M = 2.44
PENALTY_SPOT_WORLD = np.array([0.0, 11.0, 0.0], dtype=np.float64)


@dataclass
class Trajectory3D:
    sample_name: str
    times: np.ndarray
    world_points: np.ndarray
    ray_gaps: np.ndarray
    reprojection_errors: np.ndarray
    image_points_left: np.ndarray
    image_points_right: np.ndarray
    confidences_left: np.ndarray
    confidences_right: np.ndarray
    offset_seconds: float


@dataclass
class BallisticFit:
    sample_name: str
    time_origin_sec: float
    times: np.ndarray
    observed_points: np.ndarray
    fitted_points: np.ndarray
    inlier_mask: np.ndarray
    residuals: np.ndarray
    fit_reprojection_errors_px: np.ndarray
    dense_times: np.ndarray
    dense_points: np.ndarray
    ray_gaps: np.ndarray
    reprojection_errors: np.ndarray
    offset_seconds: float
    x0: float
    vx: float
    y0: float
    vy: float
    z0: float
    vz: float
    gravity: float
    k_drag: float | None
    use_drag_model: bool
    rmse_m: float
    max_residual_m: float
    reprojection_rmse_px: float
    peak_time_sec: float
    peak_point: np.ndarray
    landing_time_sec: float | None
    landing_point: np.ndarray | None
    goal_line_crossing_time_sec: float | None
    goal_line_crossing_point: np.ndarray | None
    goal_line_crossing_phase: str | None
    goal_line_inside_frame: bool | None
    post_landing_model: str | None
    post_landing_rmse_m: float | None
    post_landing_reprojection_rmse_px: float | None
    post_landing_num_points: int


@dataclass
class CameraConfig:
    name: str
    camera_matrix: np.ndarray
    dist_coeffs: np.ndarray
    rvec: np.ndarray
    tvec: np.ndarray


def load_camera_configs():
    configs = {}
    for name in ("left", "right"):
        pose_path = CALIB / f"{name}_pose.npz"
        if not pose_path.exists():
            raise FileNotFoundError(f"missing camera pose file: {pose_path}")
        pose = np.load(pose_path)
        configs[name] = CameraConfig(
            name=name,
            camera_matrix=np.asarray(pose["camera_matrix"], dtype=np.float64),
            dist_coeffs=np.asarray(pose["dist_coeffs"], dtype=np.float64),
            rvec=np.asarray(pose["rvec"], dtype=np.float64).reshape(3, 1),
            tvec=np.asarray(pose["tvec"], dtype=np.float64).reshape(3, 1),
        )
    return configs


def load_trajectory(sample_name):
    sample_dir = INPUT_ROOT / sample_name
    npz_path = sample_dir / "trajectory_3d_points.npz"
    if not npz_path.exists():
        raise FileNotFoundError(f"找不到 3D 重建结果: {npz_path}")

    data = np.load(npz_path)
    required = ["image_points_left", "image_points_right", "confidences_left", "confidences_right"]
    missing = [key for key in required if key not in data]
    if missing:
        raise RuntimeError(
            f"{npz_path} 缺少 2D 观测数据 {missing}，请先重新运行 reconstruct_3d_trajectory.py"
        )

    offset = float(np.asarray(data["offset_seconds"]).reshape(-1)[0]) if "offset_seconds" in data else 0.0
    return Trajectory3D(
        sample_name=sample_name,
        times=np.asarray(data["times"], dtype=np.float64),
        world_points=np.asarray(data["world_points"], dtype=np.float64),
        ray_gaps=np.asarray(data["ray_gaps"], dtype=np.float64),
        reprojection_errors=np.asarray(data["reprojection_errors"], dtype=np.float64),
        image_points_left=np.asarray(data["image_points_left"], dtype=np.float64),
        image_points_right=np.asarray(data["image_points_right"], dtype=np.float64),
        confidences_left=np.asarray(data["confidences_left"], dtype=np.float64),
        confidences_right=np.asarray(data["confidences_right"], dtype=np.float64),
        offset_seconds=offset,
    )



def weighted_linear_fit(times_rel, values, weights):
    t = np.asarray(times_rel, dtype=np.float64)
    y = np.asarray(values, dtype=np.float64)
    w = np.clip(np.asarray(weights, dtype=np.float64), 1e-8, None)

    s0 = float(np.sum(w))
    s1 = float(np.sum(w * t))
    s2 = float(np.sum(w * t * t))
    sy = float(np.sum(w * y))
    sty = float(np.sum(w * t * y))
    denom = s0 * s2 - s1 * s1
    if abs(denom) < 1e-10:
        intercept = sy / max(s0, 1e-8)
        slope = 0.0
    else:
        intercept = (sy * s2 - s1 * sty) / denom
        slope = (s0 * sty - s1 * sy) / denom
    return float(intercept), float(slope)


def fit_once(times_rel, points, weights):
    x0, vx = weighted_linear_fit(times_rel, points[:, 0], weights)
    y0, vy = weighted_linear_fit(times_rel, points[:, 1], weights)
    z_target = points[:, 2] + 0.5 * GRAVITY * (times_rel ** 2)
    z0, vz = weighted_linear_fit(times_rel, z_target, weights)
    return x0, vx, y0, vy, z0, vz


def weighted_slope_through_origin(times_rel, values, weights):
    t = np.asarray(times_rel, dtype=np.float64)
    y = np.asarray(values, dtype=np.float64)
    w = np.clip(np.asarray(weights, dtype=np.float64), 1e-8, None)
    denom = float(np.sum(w * t * t))
    if abs(denom) < 1e-10:
        return 0.0
    return float(np.sum(w * t * y) / denom)


def fit_ballistic_from_origin(times_abs, points, weights, origin_time_sec, origin_point):
    tau = np.asarray(times_abs, dtype=np.float64) - float(origin_time_sec)
    tau = np.clip(tau, 0.0, None)
    points = np.asarray(points, dtype=np.float64)
    weights = np.clip(np.asarray(weights, dtype=np.float64), 1e-8, None)
    origin_point = np.asarray(origin_point, dtype=np.float64).reshape(3)

    vx = weighted_slope_through_origin(tau, points[:, 0] - origin_point[0], weights)
    vy = weighted_slope_through_origin(tau, points[:, 1] - origin_point[1], weights)
    z_target = points[:, 2] - origin_point[2] + 0.5 * GRAVITY * (tau ** 2)
    vz = weighted_slope_through_origin(tau, z_target, weights)
    params = (float(origin_point[0]), vx, float(origin_point[1]), vy, float(origin_point[2]), vz)
    fitted = evaluate_ballistic(params, times_abs, origin_time_sec)
    residuals = np.linalg.norm(points - fitted, axis=1)
    return params, fitted, residuals


def evaluate_ballistic(params, times_abs, time_origin_sec):
    if len(params) == 7:
        return evaluate_ballistic_drag(params, times_abs, time_origin_sec)
    x0, vx, y0, vy, z0, vz = params
    tau = np.asarray(times_abs, dtype=np.float64) - float(time_origin_sec)
    x = x0 + vx * tau
    y = y0 + vy * tau
    z = z0 + vz * tau - 0.5 * GRAVITY * (tau ** 2)
    return np.column_stack([x, y, z])


# ── Football physical constants ──
BALL_MASS_KG = 0.43       # FIFA standard
BALL_RADIUS_M = 0.11      # size 5
BALL_AREA_M2 = np.pi * BALL_RADIUS_M ** 2
AIR_DENSITY = 1.225       # kg/m³ at sea level, 15°C
DRAG_COEFF_NOMINAL = 0.25  # typical for smooth sphere at Re ~ 10⁵
K_DRAG_FACTOR = 0.5 * AIR_DENSITY * DRAG_COEFF_NOMINAL * BALL_AREA_M2 / BALL_MASS_KG


def _drag_ode(t, state, k_drag):
    """ODE for ballistic motion with quadratic air drag.
    state = [x, y, z, vx, vy, vz]"""
    vx, vy, vz = state[3], state[4], state[5]
    speed = np.sqrt(vx*vx + vy*vy + vz*vz)
    drag = k_drag * speed
    return [vx, vy, vz,
            -drag * vx,
            -drag * vy,
            -GRAVITY - drag * vz]


def evaluate_ballistic_drag(params, times_abs, time_origin_sec):
    """Evaluate trajectory with air drag using numerical integration.
    The initial state (x0, y0, z0, vx0, vy0, vz0) is defined at time_origin_sec (tau=0).
    Handles evaluation at times both before and after time_origin_sec correctly."""
    x0, vx0, y0, vy0, z0, vz0, k_drag = params
    tau = np.asarray(times_abs, dtype=np.float64) - float(time_origin_sec)
    if len(tau) == 0:
        return np.empty((0, 3))

    state0 = [x0, y0, z0, vx0, vy0, vz0]
    t_min, t_max = float(np.min(tau)), float(np.max(tau))
    result = np.empty((len(tau), 3), dtype=np.float64)

    # Split: negative tau (backward integration) and non-negative tau (forward)
    neg_mask = tau < 0
    pos_mask = tau >= 0

    if np.any(pos_mask):
        tau_pos = tau[pos_mask]
        t_eval_pos = tau_pos  # tau is relative to time_origin (tau=0)
        sol = solve_ivp(
            _drag_ode, [0.0, t_max + 0.01],
            state0,
            args=(k_drag,),
            t_eval=t_eval_pos,
            method='RK45', rtol=1e-6, atol=1e-8
        )
        result[pos_mask] = np.column_stack([sol.y[0], sol.y[1], sol.y[2]])

    if np.any(neg_mask):
        tau_neg = tau[neg_mask]
        # Integrate backwards: t goes from 0 to t_min (negative).
        # solve_ivp requires t_eval sorted in the same direction as t_span.
        sort_idx = np.argsort(tau_neg)[::-1]  # decreasing: 0 → t_min
        tau_neg_sorted = tau_neg[sort_idx]
        sol = solve_ivp(
            _drag_ode, [0.0, t_min - 0.01],
            state0,
            args=(k_drag,),
            t_eval=tau_neg_sorted,
            method='RK45', rtol=1e-6, atol=1e-8
        )
        # Reorder back to original tau order
        inv_idx = np.argsort(sort_idx)
        result[neg_mask] = np.column_stack([sol.y[0][inv_idx], sol.y[1][inv_idx], sol.y[2][inv_idx]])

    return result


def _fit_drag_from_params(init_guess, times_abs, points, weights, time_origin_sec):
    """Fit drag model parameters using optimization with physical constraints."""
    points = np.asarray(points, dtype=np.float64)
    weights = np.clip(np.asarray(weights, dtype=np.float64), 1e-8, None)
    k_min, k_max = 0.002, 0.08  # physically plausible range for a football

    def cost(p):
        x0, vx, y0, vy, z0, vz, k = p
        # Hard constraints
        if k < k_min or k > k_max or z0 < -2.0 or z0 > 5.0:
            return 1e9
        params7 = (x0, vx, y0, vy, z0, vz, k)
        try:
            fitted = evaluate_ballistic_drag(params7, times_abs, time_origin_sec)
        except Exception:
            return 1e9
        residuals = np.linalg.norm(points - fitted, axis=1)
        rmse = float(np.sqrt(np.average(residuals ** 2, weights=weights)))
        # Penalize unphysical: negative peak, excessive height
        peak_z = float(np.max(fitted[:, 2]))
        penalty = 0.0
        if peak_z < 0.05:
            penalty += (0.05 - peak_z) * 50.0
        if peak_z > 5.0:
            penalty += (peak_z - 5.0) * 10.0
        return rmse + penalty

    result = minimize(cost, init_guess, method='Nelder-Mead',
                      options={'maxiter': 800, 'xatol': 1e-6})
    return result.x, result.fun


def _find_peak_time_drag(params, time_origin_sec):
    """Find peak z numerically for drag model."""
    ts = np.linspace(0, 3.0, 300)
    pts = evaluate_ballistic_drag(params, ts + time_origin_sec, time_origin_sec)
    idx = np.argmax(pts[:, 2])
    return float(ts[idx])


def _find_landing_drag(params, time_origin_sec):
    """Find landing time (z=0) numerically for drag model."""
    ts = np.linspace(0, 5.0, 500)
    pts = evaluate_ballistic_drag(params, ts + time_origin_sec, time_origin_sec)
    for i in range(1, len(ts)):
        if pts[i, 2] <= 0 and pts[i-1, 2] >= 0:
            frac = pts[i-1, 2] / (pts[i-1, 2] - pts[i, 2] + 1e-10)
            t_land = ts[i-1] + frac * (ts[i] - ts[i-1])
            return float(t_land), pts[i-1] + frac * (pts[i] - pts[i-1])
    return None, None


def _find_launch_drag(params, time_origin_sec):
    """Find launch time (z=0) numerically for drag model, going backwards."""
    ts = np.linspace(-2.0, 0.0, 500)
    pts = evaluate_ballistic_drag(params, ts + time_origin_sec, time_origin_sec)
    for i in range(len(ts) - 1, 0, -1):
        if pts[i, 2] >= 0 and pts[i - 1, 2] <= 0:
            frac = pts[i - 1, 2] / (pts[i - 1, 2] - pts[i, 2] + 1e-10)
            t_launch = ts[i - 1] + frac * (ts[i] - ts[i - 1])
            return float(t_launch)
    return None


def fit_ballistic_with_drag(times_abs, points, weights, time_origin_sec, parabolic_params=None):
    """Given parabolic parameters, evaluate drag model with different k and pick best.
    If parabolic_params is None, fit parabolic from data first."""
    points = np.asarray(points, dtype=np.float64)
    weights = np.clip(np.asarray(weights, dtype=np.float64), 1e-8, None)

    if parabolic_params is not None:
        x0_p, vx_p, y0_p, vy_p, z0_p, vz_p = parabolic_params[:6]
    else:
        tau = np.asarray(times_abs, dtype=np.float64) - float(time_origin_sec)
        x0_p, vx_p = weighted_linear_fit(tau, points[:, 0], weights)
        y0_p, vy_p = weighted_linear_fit(tau, points[:, 1], weights)
        z_target = points[:, 2] + 0.5 * GRAVITY * (tau ** 2)
        z0_p, vz_p = weighted_linear_fit(tau, z_target, weights)

    # Try each k, pick the one that best matches the data
    best_rmse = float('inf')
    best_k = K_DRAG_FACTOR
    for k_try in [0.002, 0.005, 0.01, 0.015, 0.02, 0.03, 0.04]:
        p = np.array([x0_p, vx_p, y0_p, vy_p, z0_p, vz_p, k_try])
        try:
            fitted = evaluate_ballistic_drag(p, times_abs, time_origin_sec)
        except Exception:
            continue
        res = np.linalg.norm(points - fitted, axis=1)
        rmse = float(np.sqrt(np.average(res ** 2, weights=weights)))
        if rmse < best_rmse:
            best_rmse = rmse
            best_k = k_try

    param_k = [x0_p, vx_p, y0_p, vy_p, z0_p, vz_p, best_k]
    fitted = evaluate_ballistic_drag(param_k, times_abs, time_origin_sec)
    residuals = np.linalg.norm(points - fitted, axis=1)
    return param_k, fitted, residuals


def compose_params_from_penalty_launch(launch_time_sec, vx, vy, vz_launch, reference_time_sec):
    tau0 = float(reference_time_sec) - float(launch_time_sec)
    x0 = float(PENALTY_SPOT_WORLD[0] + float(vx) * tau0)
    y0 = float(PENALTY_SPOT_WORLD[1] + float(vy) * tau0)
    z0 = float(PENALTY_SPOT_WORLD[2] + float(vz_launch) * tau0 - 0.5 * GRAVITY * (tau0 ** 2))
    vz0 = float(vz_launch - GRAVITY * tau0)
    return x0, float(vx), y0, float(vy), z0, vz0



def estimate_launch_time_from_standard_params(params, time_origin_sec):
    _, _, _, _, z0, vz = [float(v) for v in params]
    disc = vz * vz + 2.0 * GRAVITY * z0
    if disc >= 0.0:
        tau_launch = (vz - math.sqrt(disc)) / GRAVITY
    else:
        tau_launch = -max(1.0 / 120.0, min(0.2, abs(z0) / max(abs(vz) + 1e-6, 1.0)))
    if tau_launch > 0.0:
        tau_launch = -1.0 / 120.0
    return float(time_origin_sec + tau_launch)



def estimate_penalty_launch_init_params(
    init_params,
    time_origin_sec,
    times,
    points,
    point_weights,
    image_points_left,
    image_points_right,
    camera_configs,
):
    times = np.asarray(times, dtype=np.float64)
    points = np.asarray(points, dtype=np.float64)
    point_weights = np.clip(np.asarray(point_weights, dtype=np.float64), 1e-8, None)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)

    init_launch_time = estimate_launch_time_from_standard_params(init_params, time_origin_sec)
    upper = float(np.min(times) - 1e-4)
    lower = float(min(init_launch_time - 0.25, np.min(times) - 0.9))
    if lower >= upper:
        lower = float(np.min(times) - 0.6)

    candidates = np.linspace(lower, upper, num=72, dtype=np.float64)
    candidates = np.unique(np.concatenate([candidates, np.array([init_launch_time], dtype=np.float64)]))

    best_params = None
    best_score = None
    for launch_time_sec in candidates:
        if launch_time_sec >= upper:
            continue
        params_origin, _, _ = fit_ballistic_from_origin(
            times,
            points,
            point_weights,
            float(launch_time_sec),
            PENALTY_SPOT_WORLD,
        )
        vx = float(params_origin[1])
        vy = float(params_origin[3])
        vz = max(0.05, float(params_origin[5]))
        launch_params = np.array([float(launch_time_sec), vx, vy, vz], dtype=np.float64)
        params = compose_params_from_penalty_launch(launch_params[0], launch_params[1], launch_params[2], launch_params[3], time_origin_sec)
        world_points = evaluate_ballistic(params, times, time_origin_sec)
        mean_err, _, _ = compute_world_reprojection_errors_px(
            world_points,
            image_points_left,
            image_points_right,
            camera_configs,
        )
        residuals = np.linalg.norm(points - world_points, axis=1)
        score = float(
            np.average(mean_err ** 2, weights=point_weights)
            + 25.0 * np.average(residuals ** 2, weights=point_weights)
        )
        if best_score is None or score < best_score:
            best_score = score
            best_params = launch_params

    if best_params is None:
        _, vx, _, vy, _, vz = [float(v) for v in init_params]
        return np.array([init_launch_time, vx, vy, max(0.1, vz)], dtype=np.float64)
    return best_params


def project_world_points(world_points, config):
    image_points, _ = cv2.projectPoints(
        np.asarray(world_points, dtype=np.float64),
        config.rvec,
        config.tvec,
        config.camera_matrix,
        config.dist_coeffs,
    )
    return image_points.reshape(-1, 2)


def compute_world_reprojection_errors_px(world_points, image_points_left, image_points_right, camera_configs):
    proj_left = project_world_points(world_points, camera_configs["left"])
    proj_right = project_world_points(world_points, camera_configs["right"])
    left_err = np.linalg.norm(proj_left - image_points_left, axis=1)
    right_err = np.linalg.norm(proj_right - image_points_right, axis=1)
    mean_err = 0.5 * (left_err + right_err)
    return mean_err, left_err, right_err


def compute_fit_reprojection_errors_px(params, time_origin_sec, times, image_points_left, image_points_right, camera_configs):
    world_points = evaluate_ballistic(params, times, time_origin_sec)
    return compute_world_reprojection_errors_px(world_points, image_points_left, image_points_right, camera_configs)


def optimize_ballistic_reprojection(
    init_params,
    time_origin_sec,
    times,
    observed_points,
    image_points_left,
    image_points_right,
    base_weights,
    confidences_left,
    confidences_right,
    camera_configs,
):
    times = np.asarray(times, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    base_weights = np.clip(np.asarray(base_weights, dtype=np.float64), 1e-8, None)
    conf_left = np.clip(np.asarray(confidences_left, dtype=np.float64), 0.05, 1.0)
    conf_right = np.clip(np.asarray(confidences_right, dtype=np.float64), 0.05, 1.0)
    point_weights = np.clip(base_weights * np.sqrt(conf_left * conf_right), 1e-8, None)

    observed_points = np.asarray(observed_points, dtype=np.float64)
    current = estimate_penalty_launch_init_params(
        init_params,
        time_origin_sec,
        times,
        observed_points,
        point_weights,
        image_points_left,
        image_points_right,
        camera_configs,
    )
    launch_lower = float(np.min(times) - 1.2)
    launch_upper = float(np.min(times))
    lower = np.array([launch_lower, -45.0, -65.0, 0.05], dtype=np.float64)
    upper = np.array([launch_upper, 45.0, 15.0, 25.0], dtype=np.float64)
    current = np.clip(current, lower, upper)

    def compose_params(launch_params):
        launch_time_sec, vx, vy, vz = [float(v) for v in launch_params]
        return compose_params_from_penalty_launch(launch_time_sec, vx, vy, vz, time_origin_sec)

    def objective(launch_params):
        params = compose_params(launch_params)
        mean_err, _, _ = compute_fit_reprojection_errors_px(
            params,
            time_origin_sec,
            times,
            image_points_left,
            image_points_right,
            camera_configs,
        )
        return float(np.average(mean_err ** 2, weights=point_weights))

    best_score = objective(current)
    step = np.array([0.02, 0.85, 1.1, 0.75], dtype=np.float64)
    rng = np.random.default_rng(42)

    for _ in range(14):
        improved = False
        for axis in range(len(current)):
            axis_best = current.copy()
            axis_best_score = best_score
            for direction in (-1.0, 1.0):
                trial = current.copy()
                trial[axis] = np.clip(trial[axis] + direction * step[axis], lower[axis], upper[axis])
                score = objective(trial)
                if score < axis_best_score:
                    axis_best = trial
                    axis_best_score = score
            if axis_best_score < best_score:
                current = axis_best
                best_score = axis_best_score
                improved = True

        for _ in range(24):
            trial = np.clip(current + rng.normal(scale=step, size=current.shape), lower, upper)
            score = objective(trial)
            if score < best_score:
                current = trial
                best_score = score
                improved = True

        step *= 0.8 if improved else 0.5
        if float(np.max(step)) < 1e-3:
            break

    return compose_params(current)

def optimize_ballistic_from_origin_reprojection(
    init_params,
    origin_time_sec,
    origin_point,
    times,
    image_points_left,
    image_points_right,
    base_weights,
    confidences_left,
    confidences_right,
    camera_configs,
):
    times = np.asarray(times, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    base_weights = np.clip(np.asarray(base_weights, dtype=np.float64), 1e-8, None)
    conf_left = np.clip(np.asarray(confidences_left, dtype=np.float64), 0.05, 1.0)
    conf_right = np.clip(np.asarray(confidences_right, dtype=np.float64), 0.05, 1.0)
    point_weights = np.clip(base_weights * np.sqrt(conf_left * conf_right), 1e-8, None)

    origin_point = np.asarray(origin_point, dtype=np.float64).reshape(3)
    current = np.array([init_params[1], init_params[3], init_params[5]], dtype=np.float64)
    lower = np.array([-45.0, -65.0, -25.0], dtype=np.float64)
    upper = np.array([45.0, 15.0, 25.0], dtype=np.float64)
    current = np.clip(current, lower, upper)

    def compose_params(velocities):
        return (
            float(origin_point[0]),
            float(velocities[0]),
            float(origin_point[1]),
            float(velocities[1]),
            float(origin_point[2]),
            float(velocities[2]),
        )

    def objective(velocities):
        params = compose_params(velocities)
        mean_err, _, _ = compute_fit_reprojection_errors_px(
            params,
            origin_time_sec,
            times,
            image_points_left,
            image_points_right,
            camera_configs,
        )
        return float(np.average(mean_err ** 2, weights=point_weights))

    best_score = objective(current)
    step = np.array([0.55, 1.1, 0.95], dtype=np.float64)
    rng = np.random.default_rng(123)

    for _ in range(14):
        improved = False
        for axis in range(len(current)):
            axis_best = current.copy()
            axis_best_score = best_score
            for direction in (-1.0, 1.0):
                trial = current.copy()
                trial[axis] = np.clip(trial[axis] + direction * step[axis], lower[axis], upper[axis])
                score = objective(trial)
                if score < axis_best_score:
                    axis_best = trial
                    axis_best_score = score
            if axis_best_score < best_score:
                current = axis_best
                best_score = axis_best_score
                improved = True

        for _ in range(20):
            trial = np.clip(current + rng.normal(scale=step, size=current.shape), lower, upper)
            score = objective(trial)
            if score < best_score:
                current = trial
                best_score = score
                improved = True

        step *= 0.8 if improved else 0.5
        if float(np.max(step)) < 1e-3:
            break

    return compose_params(current)

def detect_goal_line_crossing(times, points, goal_line_y=GOAL_LINE_Y):
    times = np.asarray(times, dtype=np.float64)
    points = np.asarray(points, dtype=np.float64)
    if len(times) < 2 or len(points) < 2:
        return None, None

    for idx in range(1, len(times)):
        prev_point = points[idx - 1]
        cur_point = points[idx]
        if not np.all(np.isfinite(prev_point)) or not np.all(np.isfinite(cur_point)):
            continue

        prev_y = float(prev_point[1])
        cur_y = float(cur_point[1])
        if not (prev_y > goal_line_y and cur_y <= goal_line_y):
            continue

        denom = cur_y - prev_y
        alpha = 0.0 if abs(denom) < 1e-9 else (goal_line_y - prev_y) / denom
        if alpha < -1e-6 or alpha > 1.0 + 1e-6:
            continue
        alpha = float(np.clip(alpha, 0.0, 1.0))

        cross_time = float(times[idx - 1] + alpha * (times[idx] - times[idx - 1]))
        cross_point = prev_point + alpha * (cur_point - prev_point)
        cross_point = np.asarray(cross_point, dtype=np.float64)
        cross_point[1] = goal_line_y
        return cross_time, cross_point

    return None, None


def classify_goal_line_phase(cross_time_sec, cross_point, landing_time_sec):
    if cross_time_sec is None or cross_point is None:
        return None

    if landing_time_sec is not None:
        return "airborne" if cross_time_sec <= landing_time_sec + 1e-6 else "grounded"

    return "airborne" if float(cross_point[2]) > 0.15 else "grounded"


def is_inside_goal_frame(point):
    if point is None:
        return None

    x, _, z = np.asarray(point, dtype=np.float64)
    return bool(abs(float(x)) <= GOAL_HALF_WIDTH_M and 0.0 <= float(z) <= GOAL_HEIGHT_M)


def build_base_weights(ray_gaps, reprojection_errors):
    gap_term = np.clip(ray_gaps / max(1e-6, float(np.median(ray_gaps) + 1e-6)), 0.0, 6.0)
    reproj_term = np.clip(reprojection_errors / max(1e-6, float(np.median(reprojection_errors) + 1e-6)), 0.0, 6.0)
    weights = 1.0 / (1.0 + 0.7 * gap_term + 0.5 * reproj_term)
    return np.clip(weights, 0.05, 1.0)


def select_flight_prefix_length(times, points, base_weights):
    if len(times) <= 18:
        return len(times)

    candidates = []
    for end in range(18, len(times) + 1):
        cur_times = times[:end]
        cur_points = points[:end]
        cur_weights = base_weights[:end]
        time_origin_sec = float(cur_times[0])
        params = fit_once(cur_times - time_origin_sec, cur_points, cur_weights)
        fitted = evaluate_ballistic(params, cur_times, time_origin_sec)
        residuals = np.linalg.norm(cur_points - fitted, axis=1)
        rmse = float(np.sqrt(np.mean(residuals ** 2)))
        z0 = float(params[4])
        vz = float(params[5])
        peak_height = z0 + (vz * vz) / (2.0 * GRAVITY) if vz > 0.0 else z0
        candidates.append(
            {
                "end": end,
                "rmse": rmse,
                "z0": z0,
                "peak_height": peak_height,
            }
        )

    valid = [
        cand for cand in candidates
        if cand["z0"] >= -0.05 and cand["rmse"] <= 1.0 and cand["peak_height"] <= 4.5
    ]
    if not valid:
        return len(times)

    best_rmse = min(cand["rmse"] for cand in valid)
    divergence_threshold = max(best_rmse * 1.6, best_rmse + 0.03)
    selected_end = valid[0]["end"]
    for cand in valid:
        if cand["rmse"] <= divergence_threshold:
            selected_end = cand["end"]
        else:
            break
    return int(selected_end)



def fit_endpoint_constrained_quadratic_axis(tau, observed, weights, start_value, end_value, tau_end):
    tau = np.asarray(tau, dtype=np.float64)
    observed = np.asarray(observed, dtype=np.float64)
    weights = np.clip(np.asarray(weights, dtype=np.float64), 1e-8, None)
    tau_end = float(tau_end)

    if tau_end <= 1e-8:
        pred = np.full_like(observed, float(end_value), dtype=np.float64)
        return 0.0, 0.0, pred

    baseline = float(start_value) + (float(end_value) - float(start_value)) * (tau / tau_end)
    basis = 0.5 * (tau * tau - tau_end * tau)
    denom = float(np.sum(weights * basis * basis))
    if denom < 1e-10:
        accel = 0.0
    else:
        accel = float(np.sum(weights * basis * (observed - baseline)) / denom)
    velocity = float((float(end_value) - float(start_value) - 0.5 * accel * tau_end * tau_end) / tau_end)
    pred = float(start_value) + velocity * tau + 0.5 * accel * (tau * tau)
    return velocity, accel, pred


def fit_rebound_ballistic_segment(
    times,
    points,
    weights,
    image_points_left,
    image_points_right,
    confidences_left,
    confidences_right,
    camera_configs,
    landing_time_sec,
    landing_point,
    cross_time_sec,
    cross_point,
):
    if landing_time_sec is None or landing_point is None or cross_time_sec is None:
        return None

    times = np.asarray(times, dtype=np.float64)
    points = np.asarray(points, dtype=np.float64)
    weights = np.asarray(weights, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    confidences_left = np.asarray(confidences_left, dtype=np.float64)
    confidences_right = np.asarray(confidences_right, dtype=np.float64)
    finite_mask = np.isfinite(times) & np.all(np.isfinite(points), axis=1)
    finite_mask &= np.all(np.isfinite(image_points_left), axis=1)
    finite_mask &= np.all(np.isfinite(image_points_right), axis=1)
    if np.count_nonzero(finite_mask) < 6:
        return None

    finite_times = times[finite_mask]
    search_start = max(float(np.min(finite_times)), float(landing_time_sec) - 1e-6)
    search_end = min(float(np.max(finite_times)), float(cross_time_sec) + 0.06)
    search_mask = finite_mask & (times >= search_start) & (times <= search_end)
    if np.count_nonzero(search_mask) < 5:
        return None

    search_points = points[search_mask]
    if float(np.max(search_points[:, 2])) < 0.05:
        return None
    if float(np.max(search_points[:, 2]) - np.min(search_points[:, 2])) < 0.06:
        return None

    origin_time_sec = float(landing_time_sec)
    origin_point = np.asarray(landing_point, dtype=np.float64).reshape(3).copy()
    origin_point[2] = 0.0
    candidates = []
    for extra_after_cross in (0.0, 0.02, 0.04, 0.06):
        segment_end = min(float(np.max(finite_times)), float(cross_time_sec) + extra_after_cross)
        segment_mask = finite_mask & (times >= origin_time_sec - 1e-6) & (times <= segment_end + 1e-6)
        if np.count_nonzero(segment_mask) < 5:
            continue

        obs_times = times[segment_mask]
        obs_points = points[segment_mask]
        obs_image_points_left = image_points_left[segment_mask]
        obs_image_points_right = image_points_right[segment_mask]
        obs_confidences_left = confidences_left[segment_mask]
        obs_confidences_right = confidences_right[segment_mask]
        base_weights = np.clip(weights[segment_mask], 1e-8, None)
        robust_weights = base_weights.copy()
        params = None
        fitted_obs = None
        residuals = None
        fit_reprojection_errors_px = None

        for _ in range(5):
            init_params, _, _ = fit_ballistic_from_origin(
                obs_times,
                obs_points,
                robust_weights,
                origin_time_sec,
                origin_point,
            )
            params = optimize_ballistic_from_origin_reprojection(
                init_params,
                origin_time_sec,
                origin_point,
                obs_times,
                obs_image_points_left,
                obs_image_points_right,
                robust_weights,
                obs_confidences_left,
                obs_confidences_right,
                camera_configs,
            )
            fitted_obs = evaluate_ballistic(params, obs_times, origin_time_sec)
            residuals = np.linalg.norm(obs_points - fitted_obs, axis=1)
            fit_reprojection_errors_px, _, _ = compute_fit_reprojection_errors_px(
                params,
                origin_time_sec,
                obs_times,
                obs_image_points_left,
                obs_image_points_right,
                camera_configs,
            )
            scale = max(2.5, float(np.percentile(fit_reprojection_errors_px, 75)) * 1.35) if len(fit_reprojection_errors_px) else 2.5
            robust_weights = np.clip(base_weights / (1.0 + (fit_reprojection_errors_px / scale) ** 2), 1e-8, None)

        if params is None or fitted_obs is None or residuals is None or fit_reprojection_errors_px is None:
            continue

        if abs(params[3]) < 1e-8:
            continue
        tau_cross = (GOAL_LINE_Y - origin_point[1]) / params[3]
        if tau_cross <= 0.0:
            continue
        fit_cross_time = float(origin_time_sec + tau_cross)
        if fit_cross_time > float(cross_time_sec) + 0.12:
            continue

        fit_cross_point = evaluate_ballistic(params, np.array([fit_cross_time], dtype=np.float64), origin_time_sec)[0]
        fit_cross_point = np.asarray(fit_cross_point, dtype=np.float64)
        fit_cross_point[1] = GOAL_LINE_Y
        if float(fit_cross_point[2]) < -0.05:
            continue

        dt_candidates = np.diff(obs_times)
        dt = float(np.median(dt_candidates)) if len(dt_candidates) > 0 else max(1.0 / 120.0, tau_cross / 20.0)
        dt = max(1.0 / 240.0, min(dt * 0.5, 1.0 / 60.0))
        dense_times = np.arange(origin_time_sec, fit_cross_time + dt * 0.5, dt)
        if len(dense_times) == 0:
            dense_times = np.array([origin_time_sec, fit_cross_time], dtype=np.float64)
        if dense_times[-1] < fit_cross_time - 1e-6:
            dense_times = np.append(dense_times, fit_cross_time)
        dense_points = evaluate_ballistic(params, dense_times, origin_time_sec)
        dense_points[0] = origin_point
        dense_points[-1] = fit_cross_point

        weighted_rmse = float(np.sqrt(np.average(residuals ** 2, weights=robust_weights)))
        reprojection_rmse_px = float(np.sqrt(np.average(fit_reprojection_errors_px ** 2, weights=robust_weights)))
        point_error = 0.0 if cross_point is None else float(np.linalg.norm(fit_cross_point - np.asarray(cross_point, dtype=np.float64)))
        time_error = abs(fit_cross_time - float(cross_time_sec))
        score = reprojection_rmse_px + 0.35 * time_error + 0.75 * point_error + 0.15 * weighted_rmse

        item = {
            'origin_time_sec': origin_time_sec,
            'origin_point': origin_point.copy(),
            'cross_time_sec': fit_cross_time,
            'cross_point': fit_cross_point,
            'params': params,
            'observed_times': obs_times,
            'observed_points': obs_points,
            'fitted_points': fitted_obs,
            'residuals': residuals,
            'fit_reprojection_errors_px': fit_reprojection_errors_px,
            'reprojection_rmse_px': reprojection_rmse_px,
            'dense_times': dense_times,
            'dense_points': dense_points,
            'rmse_m': weighted_rmse,
            'point_error_m': point_error,
            'time_error_sec': time_error,
            'score': score,
        }
        candidates.append(item)

    if not candidates:
        return None

    best_reprojection = min(float(item['reprojection_rmse_px']) for item in candidates)
    shortlist = [
        item
        for item in candidates
        if float(item['reprojection_rmse_px']) <= best_reprojection + 2.0
        and float(item['time_error_sec']) <= 0.12
        and float(item['point_error_m']) <= 0.35
    ]
    if shortlist:
        best = min(
            shortlist,
            key=lambda item: (
                float(item['point_error_m']),
                float(item['time_error_sec']),
                float(item['rmse_m']),
                float(item['reprojection_rmse_px']),
            ),
        )
    else:
        best = min(candidates, key=lambda item: float(item['score']))

    reprojection_ok = float(best['reprojection_rmse_px']) <= 18.0
    geometry_ok = (
        float(best['rmse_m']) <= 0.45
        and float(best['point_error_m']) <= 0.30
        and float(best['time_error_sec']) <= 0.12
    )
    if not (reprojection_ok or geometry_ok):
        return None
    return best

def evaluate_endpoint_constrained_quadratic(start_point, end_point, tau, tau_end, accelerations):
    start_point = np.asarray(start_point, dtype=np.float64).reshape(3)
    end_point = np.asarray(end_point, dtype=np.float64).reshape(3)
    tau = np.asarray(tau, dtype=np.float64)
    accelerations = np.asarray(accelerations, dtype=np.float64).reshape(3)

    points = np.zeros((len(tau), 3), dtype=np.float64)
    velocities = np.zeros(3, dtype=np.float64)
    if tau_end <= 1e-8:
        points[:] = end_point
        return points, velocities

    for axis in range(3):
        velocities[axis] = (end_point[axis] - start_point[axis] - 0.5 * accelerations[axis] * tau_end * tau_end) / tau_end
        points[:, axis] = start_point[axis] + velocities[axis] * tau + 0.5 * accelerations[axis] * (tau ** 2)

    return points, velocities



def optimize_ground_rollout_reprojection(
    start_point,
    end_point,
    tau,
    tau_end,
    image_points_left,
    image_points_right,
    base_weights,
    confidences_left,
    confidences_right,
    camera_configs,
    init_accelerations,
):
    tau = np.asarray(tau, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    base_weights = np.clip(np.asarray(base_weights, dtype=np.float64), 1e-8, None)
    conf_left = np.clip(np.asarray(confidences_left, dtype=np.float64), 0.05, 1.0)
    conf_right = np.clip(np.asarray(confidences_right, dtype=np.float64), 0.05, 1.0)
    point_weights = np.clip(base_weights * np.sqrt(conf_left * conf_right), 1e-8, None)

    current = np.asarray(init_accelerations, dtype=np.float64).reshape(3)
    lower = np.array([-30.0, -30.0, -40.0], dtype=np.float64)
    upper = np.array([30.0, 30.0, 40.0], dtype=np.float64)
    current = np.clip(current, lower, upper)

    def objective(accelerations):
        world_points, _ = evaluate_endpoint_constrained_quadratic(start_point, end_point, tau, tau_end, accelerations)
        mean_err, _, _ = compute_world_reprojection_errors_px(
            world_points,
            image_points_left,
            image_points_right,
            camera_configs,
        )
        z_penalty = np.average(np.maximum(np.abs(world_points[:, 2]) - 0.08, 0.0) ** 2, weights=point_weights)
        return float(np.average(mean_err ** 2, weights=point_weights) + 220.0 * z_penalty)

    best_score = objective(current)
    step = np.array([1.4, 1.8, 2.0], dtype=np.float64)
    rng = np.random.default_rng(321)

    for _ in range(14):
        improved = False
        for axis in range(len(current)):
            axis_best = current.copy()
            axis_best_score = best_score
            for direction in (-1.0, 1.0):
                trial = current.copy()
                trial[axis] = np.clip(trial[axis] + direction * step[axis], lower[axis], upper[axis])
                score = objective(trial)
                if score < axis_best_score:
                    axis_best = trial
                    axis_best_score = score
            if axis_best_score < best_score:
                current = axis_best
                best_score = axis_best_score
                improved = True

        for _ in range(20):
            trial = np.clip(current + rng.normal(scale=step, size=current.shape), lower, upper)
            score = objective(trial)
            if score < best_score:
                current = trial
                best_score = score
                improved = True

        step *= 0.8 if improved else 0.5
        if float(np.max(step)) < 1e-3:
            break

    return current



def fit_ground_rollout_segment(
    times,
    points,
    weights,
    image_points_left,
    image_points_right,
    confidences_left,
    confidences_right,
    camera_configs,
    landing_time_sec,
    landing_point,
    cross_time_sec,
    cross_point,
):
    if landing_time_sec is None or landing_point is None or cross_time_sec is None or cross_point is None:
        return None

    tau_end = float(cross_time_sec) - float(landing_time_sec)
    if tau_end <= 1e-4:
        return None

    times = np.asarray(times, dtype=np.float64)
    points = np.asarray(points, dtype=np.float64)
    weights = np.asarray(weights, dtype=np.float64)
    image_points_left = np.asarray(image_points_left, dtype=np.float64)
    image_points_right = np.asarray(image_points_right, dtype=np.float64)
    confidences_left = np.asarray(confidences_left, dtype=np.float64)
    confidences_right = np.asarray(confidences_right, dtype=np.float64)

    finite_mask = np.isfinite(times) & np.all(np.isfinite(points), axis=1)
    finite_mask &= np.all(np.isfinite(image_points_left), axis=1)
    finite_mask &= np.all(np.isfinite(image_points_right), axis=1)
    rollout_mask = finite_mask & (times >= float(landing_time_sec) - 1e-6) & (times <= float(cross_time_sec) + 1e-6)
    if np.count_nonzero(rollout_mask) < 3:
        return None

    obs_times = times[rollout_mask]
    obs_points = points[rollout_mask]
    obs_image_points_left = image_points_left[rollout_mask]
    obs_image_points_right = image_points_right[rollout_mask]
    obs_confidences_left = confidences_left[rollout_mask]
    obs_confidences_right = confidences_right[rollout_mask]
    base_weights = np.clip(weights[rollout_mask], 1e-8, None)
    tau = np.clip(obs_times - float(landing_time_sec), 0.0, tau_end)

    start_point = np.asarray(landing_point, dtype=np.float64).reshape(3)
    end_point = np.asarray(cross_point, dtype=np.float64).reshape(3)
    robust_weights = base_weights.copy()
    fitted_obs = np.zeros_like(obs_points)
    residuals = None
    fit_reprojection_errors_px = None
    accelerations = np.zeros(3, dtype=np.float64)

    for _ in range(5):
        init_accelerations = np.zeros(3, dtype=np.float64)
        for axis in range(3):
            _, acc, pred = fit_endpoint_constrained_quadratic_axis(
                tau,
                obs_points[:, axis],
                robust_weights,
                start_point[axis],
                end_point[axis],
                tau_end,
            )
            init_accelerations[axis] = acc
            fitted_obs[:, axis] = pred

        accelerations = optimize_ground_rollout_reprojection(
            start_point,
            end_point,
            tau,
            tau_end,
            obs_image_points_left,
            obs_image_points_right,
            robust_weights,
            obs_confidences_left,
            obs_confidences_right,
            camera_configs,
            init_accelerations,
        )
        fitted_obs, _ = evaluate_endpoint_constrained_quadratic(start_point, end_point, tau, tau_end, accelerations)
        residuals = np.linalg.norm(obs_points - fitted_obs, axis=1)
        fit_reprojection_errors_px, _, _ = compute_world_reprojection_errors_px(
            fitted_obs,
            obs_image_points_left,
            obs_image_points_right,
            camera_configs,
        )
        scale = max(2.5, float(np.percentile(fit_reprojection_errors_px, 75)) * 1.35) if len(fit_reprojection_errors_px) else 2.5
        robust_weights = np.clip(base_weights / (1.0 + (fit_reprojection_errors_px / scale) ** 2), 1e-8, None)

    if residuals is None or fit_reprojection_errors_px is None:
        return None

    rmse_m = float(np.sqrt(np.average(residuals ** 2, weights=robust_weights)))
    reprojection_rmse_px = float(np.sqrt(np.average(fit_reprojection_errors_px ** 2, weights=robust_weights)))
    if reprojection_rmse_px > 18.0:
        return None

    dt_candidates = np.diff(obs_times)
    dt = float(np.median(dt_candidates)) if len(dt_candidates) > 0 else tau_end / 20.0
    dt = max(1.0 / 240.0, min(dt * 0.5, 1.0 / 60.0))
    dense_times = np.arange(float(landing_time_sec), float(cross_time_sec) + dt * 0.5, dt)
    if len(dense_times) == 0 or dense_times[-1] < float(cross_time_sec) - 1e-6:
        dense_times = np.append(dense_times, float(cross_time_sec))

    dense_tau = np.clip(dense_times - float(landing_time_sec), 0.0, tau_end)
    dense_points, _ = evaluate_endpoint_constrained_quadratic(start_point, end_point, dense_tau, tau_end, accelerations)
    dense_points[0] = start_point
    dense_points[-1] = end_point

    return {
        'observed_times': obs_times,
        'observed_points': obs_points,
        'fitted_points': fitted_obs,
        'residuals': residuals,
        'fit_reprojection_errors_px': fit_reprojection_errors_px,
        'reprojection_rmse_px': reprojection_rmse_px,
        'dense_times': dense_times,
        'dense_points': dense_points,
        'rmse_m': rmse_m,
    }


def fit_ballistic_trajectory(trajectory, camera_configs):
    times = trajectory.times.astype(np.float64)
    points = trajectory.world_points.astype(np.float64)
    image_points_left = trajectory.image_points_left.astype(np.float64)
    image_points_right = trajectory.image_points_right.astype(np.float64)
    confidences_left = trajectory.confidences_left.astype(np.float64)
    confidences_right = trajectory.confidences_right.astype(np.float64)

    sort_idx = np.argsort(times)
    times = times[sort_idx]
    points = points[sort_idx]
    image_points_left = image_points_left[sort_idx]
    image_points_right = image_points_right[sort_idx]
    confidences_left = confidences_left[sort_idx]
    confidences_right = confidences_right[sort_idx]

    full_times = times.copy()
    full_points = points.copy()
    full_image_points_left = image_points_left.copy()
    full_image_points_right = image_points_right.copy()
    full_confidences_left = confidences_left.copy()
    full_confidences_right = confidences_right.copy()
    ray_gaps = trajectory.ray_gaps[sort_idx]
    reprojection_errors = trajectory.reprojection_errors[sort_idx]
    full_ray_gaps = ray_gaps.copy()
    full_reprojection_errors = reprojection_errors.copy()
    full_base_weights = build_base_weights(full_ray_gaps, full_reprojection_errors)

    if len(times) < 8:
        raise RuntimeError(f"{trajectory.sample_name} has too few 3D points for ballistic fitting.")

    prefix_end = select_flight_prefix_length(times, points, full_base_weights)
    times = times[:prefix_end]
    points = points[:prefix_end]
    image_points_left = image_points_left[:prefix_end]
    image_points_right = image_points_right[:prefix_end]
    confidences_left = confidences_left[:prefix_end]
    confidences_right = confidences_right[:prefix_end]
    ray_gaps = ray_gaps[:prefix_end]
    reprojection_errors = reprojection_errors[:prefix_end]
    base_weights = full_base_weights[:prefix_end]

    time_origin_sec = float(times[0])
    times_rel = times - time_origin_sec
    mask = np.isfinite(times_rel)
    mask &= np.all(np.isfinite(points), axis=1)
    mask &= np.all(np.isfinite(image_points_left), axis=1)
    mask &= np.all(np.isfinite(image_points_right), axis=1)

    params = None
    residuals = None
    fit_reprojection_errors_px = None
    for _ in range(6):
        active_idx = np.where(mask)[0]
        if len(active_idx) < 8:
            break
        init_params = fit_once(times_rel[active_idx], points[active_idx], base_weights[active_idx]) if params is None else params
        params = optimize_ballistic_reprojection(
            init_params,
            time_origin_sec,
            times[active_idx],
            points[active_idx],
            image_points_left[active_idx],
            image_points_right[active_idx],
            base_weights[active_idx],
            confidences_left[active_idx],
            confidences_right[active_idx],
            camera_configs,
        )
        fitted = evaluate_ballistic(params, times, time_origin_sec)
        residuals = np.linalg.norm(points - fitted, axis=1)
        fit_reprojection_errors_px, _, _ = compute_fit_reprojection_errors_px(
            params,
            time_origin_sec,
            times,
            image_points_left,
            image_points_right,
            camera_configs,
        )

        inlier_reproj = fit_reprojection_errors_px[mask]
        if len(inlier_reproj) == 0:
            break
        threshold = float(np.percentile(inlier_reproj, 75) * 1.35)
        threshold = max(3.0, min(threshold, 18.0))
        new_mask = fit_reprojection_errors_px <= threshold
        new_mask &= points[:, 2] >= -0.10
        new_mask &= np.isfinite(fit_reprojection_errors_px)
        if np.array_equal(new_mask, mask):
            mask = new_mask
            break
        if np.count_nonzero(new_mask) >= 8:
            mask = new_mask
        else:
            break

    if params is None:
        raise RuntimeError(f"{trajectory.sample_name} ballistic fitting failed.")

    final_idx = np.where(mask)[0]
    params = optimize_ballistic_reprojection(
        params,
        time_origin_sec,
        times[final_idx],
        points[final_idx],
        image_points_left[final_idx],
        image_points_right[final_idx],
        base_weights[final_idx],
        confidences_left[final_idx],
        confidences_right[final_idx],
        camera_configs,
    )
    fitted_points = evaluate_ballistic(params, times, time_origin_sec)
    residuals = np.linalg.norm(points - fitted_points, axis=1)

    # Drag model disabled — use pure gravity parabolic model only.
    fit_reprojection_errors_px, _, _ = compute_fit_reprojection_errors_px(
        params,
        time_origin_sec,
        times,
        image_points_left,
        image_points_right,
        camera_configs,
    )
    final_threshold = float(np.percentile(fit_reprojection_errors_px, 75) * 1.35)
    final_threshold = max(3.0, min(final_threshold, 18.0))
    final_mask = fit_reprojection_errors_px <= final_threshold
    final_mask &= points[:, 2] >= -0.10
    final_mask &= np.isfinite(fit_reprojection_errors_px)
    if np.count_nonzero(final_mask) >= 8:
        mask = final_mask

    use_drag = len(params) == 7
    x0, vx, y0, vy, z0, vz = params[:6]
    k_drag = float(params[6]) if use_drag else None
    tau_peak = max(0.0, vz / GRAVITY) if not use_drag else _find_peak_time_drag(params, time_origin_sec)
    peak_time_sec = time_origin_sec + tau_peak
    peak_point = evaluate_ballistic(params, np.array([peak_time_sec], dtype=np.float64), time_origin_sec)[0]

    landing_time_sec = None
    landing_point = None
    launch_time_sec = None
    if use_drag:
        landing_time_sec, landing_point_raw = _find_landing_drag(params, time_origin_sec)
        if landing_point_raw is not None:
            landing_point = landing_point_raw
        launch_tau = _find_launch_drag(params, time_origin_sec)
        if launch_tau is not None and launch_tau <= 0.0:
            launch_time_sec = time_origin_sec + launch_tau
    else:
        disc = vz * vz + 2.0 * GRAVITY * z0
        if disc >= 0.0:
            sqrt_disc = math.sqrt(disc)
            tau_launch = (vz - sqrt_disc) / GRAVITY
            if tau_launch <= 0.0:
                launch_time_sec = time_origin_sec + tau_launch
            tau_land = (vz + sqrt_disc) / GRAVITY
            if tau_land >= 0.0:
                landing_time_sec = time_origin_sec + tau_land
                landing_point = evaluate_ballistic(params, np.array([landing_time_sec], dtype=np.float64), time_origin_sec)[0]

    observed_goal_line_crossing_time_sec, observed_goal_line_crossing_point = detect_goal_line_crossing(full_times, full_points)
    observed_goal_line_crossing_phase = classify_goal_line_phase(
        observed_goal_line_crossing_time_sec,
        observed_goal_line_crossing_point,
        landing_time_sec,
    )

    dt_candidates = np.diff(times)
    dt = float(np.median(dt_candidates)) if len(dt_candidates) > 0 else 1.0 / 60.0
    dt = max(1.0 / 240.0, min(dt * 0.5, 1.0 / 60.0))
    dense_start = float(times[0])
    if launch_time_sec is not None:
        dense_start = min(dense_start, max(launch_time_sec, float(times[0]) - 1.0))
    dense_end = float(times[-1])
    if landing_time_sec is not None:
        dense_end = max(dense_end, min(landing_time_sec, times[-1] + 1.0))
    dense_times = np.arange(dense_start, dense_end + dt * 0.5, dt)
    key_times = []
    if launch_time_sec is not None:
        key_times.append(float(launch_time_sec))
    if landing_time_sec is not None:
        key_times.append(float(landing_time_sec))
    if len(dense_times) == 0:
        dense_times = np.asarray([dense_start, dense_end], dtype=np.float64)
    if key_times:
        dense_times = np.unique(np.concatenate([dense_times, np.asarray(key_times, dtype=np.float64)]))
    dense_times = dense_times[dense_times <= dense_end + 1e-9]
    dense_points = evaluate_ballistic(params, dense_times, time_origin_sec)
    if launch_time_sec is not None:
        launch_idx = int(np.argmin(np.abs(dense_times - float(launch_time_sec))))
        if abs(float(dense_times[launch_idx]) - float(launch_time_sec)) <= 1e-5:
            dense_points[launch_idx] = PENALTY_SPOT_WORLD
    if landing_time_sec is not None and landing_point is not None:
        landing_idx = int(np.argmin(np.abs(dense_times - float(landing_time_sec))))
        if abs(float(dense_times[landing_idx]) - float(landing_time_sec)) <= 1e-5:
            dense_points[landing_idx] = np.asarray(landing_point, dtype=np.float64)
    dense_keep = dense_points[:, 2] >= -0.05
    if np.any(dense_keep):
        dense_times = dense_times[dense_keep]
        dense_points = dense_points[dense_keep]

    post_landing_model = None
    post_landing_fit = None
    if observed_goal_line_crossing_phase == 'grounded':
        rebound_fit = fit_rebound_ballistic_segment(
            full_times,
            full_points,
            full_base_weights,
            full_image_points_left,
            full_image_points_right,
            full_confidences_left,
            full_confidences_right,
            camera_configs,
            landing_time_sec,
            landing_point,
            observed_goal_line_crossing_time_sec,
            observed_goal_line_crossing_point,
        )
        if rebound_fit is not None and len(rebound_fit['dense_times']) > 0:
            post_landing_model = 'rebound_ballistic'
            post_landing_fit = rebound_fit
            keep_air = dense_times < float(landing_time_sec) - 1e-6
            if np.any(keep_air):
                dense_times = np.concatenate([dense_times[keep_air], rebound_fit['dense_times']])
                dense_points = np.vstack([dense_points[keep_air], rebound_fit['dense_points']])
            else:
                dense_times = rebound_fit['dense_times']
                dense_points = rebound_fit['dense_points']
        else:
            rollout_fit = fit_ground_rollout_segment(
                full_times,
                full_points,
                full_base_weights,
                full_image_points_left,
                full_image_points_right,
                full_confidences_left,
                full_confidences_right,
                camera_configs,
                landing_time_sec,
                landing_point,
                observed_goal_line_crossing_time_sec,
                observed_goal_line_crossing_point,
            )
            if rollout_fit is not None and len(rollout_fit['dense_times']) > 0:
                post_landing_model = 'ground_rollout'
                post_landing_fit = rollout_fit
                keep_air = dense_times < float(landing_time_sec) - 1e-6
                if np.any(keep_air):
                    dense_times = np.concatenate([dense_times[keep_air], rollout_fit['dense_times']])
                    dense_points = np.vstack([dense_points[keep_air], rollout_fit['dense_points']])
                else:
                    dense_times = rollout_fit['dense_times']
                    dense_points = rollout_fit['dense_points']

    goal_line_crossing_time_sec, goal_line_crossing_point = detect_goal_line_crossing(dense_times, dense_points)
    if goal_line_crossing_point is None:
        goal_line_crossing_time_sec = observed_goal_line_crossing_time_sec
        goal_line_crossing_point = observed_goal_line_crossing_point
    goal_line_crossing_phase = classify_goal_line_phase(
        goal_line_crossing_time_sec,
        goal_line_crossing_point,
        landing_time_sec,
    )
    goal_line_inside_frame = is_inside_goal_frame(goal_line_crossing_point)

    inlier_residuals = residuals[mask]
    rmse_m = float(np.sqrt(np.mean(inlier_residuals ** 2))) if len(inlier_residuals) else float(np.sqrt(np.mean(residuals ** 2)))
    max_residual_m = float(np.max(inlier_residuals)) if len(inlier_residuals) else float(np.max(residuals))
    inlier_reprojection = fit_reprojection_errors_px[mask]
    reprojection_rmse_px = float(np.sqrt(np.mean(inlier_reprojection ** 2))) if len(inlier_reprojection) else float(np.sqrt(np.mean(fit_reprojection_errors_px ** 2)))

    post_landing_rmse_m = None if post_landing_fit is None else float(post_landing_fit['rmse_m'])
    post_landing_reprojection_rmse_px = None
    if post_landing_fit is not None and post_landing_fit.get('reprojection_rmse_px') is not None:
        post_landing_reprojection_rmse_px = float(post_landing_fit['reprojection_rmse_px'])
    post_landing_num_points = 0 if post_landing_fit is None else int(len(post_landing_fit['observed_times']))

    return BallisticFit(
        sample_name=trajectory.sample_name,
        time_origin_sec=time_origin_sec,
        times=times,
        observed_points=points,
        fitted_points=fitted_points,
        inlier_mask=mask,
        residuals=residuals,
        fit_reprojection_errors_px=fit_reprojection_errors_px,
        dense_times=dense_times,
        dense_points=dense_points,
        ray_gaps=ray_gaps,
        reprojection_errors=reprojection_errors,
        offset_seconds=trajectory.offset_seconds,
        x0=x0,
        vx=vx,
        y0=y0,
        vy=vy,
        z0=z0,
        vz=vz,
        gravity=GRAVITY,
        k_drag=k_drag,
        use_drag_model=use_drag,
        rmse_m=rmse_m,
        max_residual_m=max_residual_m,
        reprojection_rmse_px=reprojection_rmse_px,
        peak_time_sec=peak_time_sec,
        peak_point=peak_point,
        landing_time_sec=landing_time_sec,
        landing_point=landing_point,
        goal_line_crossing_time_sec=goal_line_crossing_time_sec,
        goal_line_crossing_point=goal_line_crossing_point,
        goal_line_crossing_phase=goal_line_crossing_phase,
        goal_line_inside_frame=goal_line_inside_frame,
        post_landing_model=post_landing_model,
        post_landing_rmse_m=post_landing_rmse_m,
        post_landing_reprojection_rmse_px=post_landing_reprojection_rmse_px,
        post_landing_num_points=post_landing_num_points,
    )



def write_observed_csv(path, fit):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow([
            "time_sec",
            "obs_x_m",
            "obs_y_m",
            "obs_z_m",
            "fit_x_m",
            "fit_y_m",
            "fit_z_m",
            "residual_m",
            "fit_reprojection_error_px",
            "ray_gap_m",
            "reprojection_error_px",
            "is_inlier",
        ])
        for idx in range(len(fit.times)):
            writer.writerow([
                float(fit.times[idx]),
                float(fit.observed_points[idx, 0]),
                float(fit.observed_points[idx, 1]),
                float(fit.observed_points[idx, 2]),
                float(fit.fitted_points[idx, 0]),
                float(fit.fitted_points[idx, 1]),
                float(fit.fitted_points[idx, 2]),
                float(fit.residuals[idx]),
                float(fit.fit_reprojection_errors_px[idx]),
                float(fit.ray_gaps[idx]),
                float(fit.reprojection_errors[idx]),
                bool(fit.inlier_mask[idx]),
            ])


def write_curve_csv(path, fit):
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["time_sec", "fit_x_m", "fit_y_m", "fit_z_m"])
        for idx in range(len(fit.dense_times)):
            writer.writerow([
                float(fit.dense_times[idx]),
                float(fit.dense_points[idx, 0]),
                float(fit.dense_points[idx, 1]),
                float(fit.dense_points[idx, 2]),
            ])


def save_summary(path, fit):
    goal_line_crossing = {
        "detected": fit.goal_line_crossing_point is not None,
        "time_sec": None if fit.goal_line_crossing_time_sec is None else float(fit.goal_line_crossing_time_sec),
        "point_xyz_m": None if fit.goal_line_crossing_point is None else fit.goal_line_crossing_point.tolist(),
        "phase": fit.goal_line_crossing_phase,
        "inside_goal_frame": None if fit.goal_line_inside_frame is None else bool(fit.goal_line_inside_frame),
    }
    summary = {
        "sample_name": fit.sample_name,
        "time_origin_sec": float(fit.time_origin_sec),
        "time_offset_seconds": float(fit.offset_seconds),
        "num_observed_points": int(len(fit.times)),
        "num_inlier_points": int(np.count_nonzero(fit.inlier_mask)),
        "gravity_mps2": float(fit.gravity),
        "x0_m": float(fit.x0),
        "vx_mps": float(fit.vx),
        "y0_m": float(fit.y0),
        "vy_mps": float(fit.vy),
        "z0_m": float(fit.z0),
        "vz_mps": float(fit.vz),
        "initial_speed_mps": float(np.linalg.norm([fit.vx, fit.vy, fit.vz])),
        "rmse_m": float(fit.rmse_m),
        "max_residual_m": float(fit.max_residual_m),
        "fit_reprojection_rmse_px": float(fit.reprojection_rmse_px),
        "peak_time_sec": float(fit.peak_time_sec),
        "peak_point_xyz_m": fit.peak_point.tolist(),
        "landing_time_sec": None if fit.landing_time_sec is None else float(fit.landing_time_sec),
        "landing_point_xyz_m": None if fit.landing_point is None else fit.landing_point.tolist(),
        "goal_line_crossing": goal_line_crossing,
        "post_landing_fit": {
            "applied": fit.post_landing_model is not None,
            "model": fit.post_landing_model,
            "rmse_m": None if fit.post_landing_rmse_m is None else float(fit.post_landing_rmse_m),
            "reprojection_rmse_px": None if fit.post_landing_reprojection_rmse_px is None else float(fit.post_landing_reprojection_rmse_px),
            "num_observed_points": int(fit.post_landing_num_points),
        },
        "observed_points": [],
        "fitted_curve_preview": [],
    }

    for idx in range(len(fit.times)):
        summary["observed_points"].append(
            {
                "time_sec": float(fit.times[idx]),
                "obs_xyz_m": fit.observed_points[idx].tolist(),
                "fit_xyz_m": fit.fitted_points[idx].tolist(),
                "residual_m": float(fit.residuals[idx]),
                "fit_reprojection_error_px": float(fit.fit_reprojection_errors_px[idx]),
                "ray_gap_m": float(fit.ray_gaps[idx]),
                "reprojection_error_px": float(fit.reprojection_errors[idx]),
                "is_inlier": bool(fit.inlier_mask[idx]),
            }
        )

    preview_idx = np.linspace(0, len(fit.dense_times) - 1, num=min(40, len(fit.dense_times)), dtype=int)
    preview_idx = np.unique(preview_idx)
    for idx in preview_idx:
        summary["fitted_curve_preview"].append(
            {
                "time_sec": float(fit.dense_times[idx]),
                "fit_xyz_m": fit.dense_points[idx].tolist(),
            }
        )

    with open(path, "w", encoding="utf-8") as f:
        json.dump(summary, f, ensure_ascii=False, indent=2)


def render_preview(path, fit):
    canvas_h, canvas_w = 1000, 1600
    canvas = np.full((canvas_h, canvas_w, 3), 247, dtype=np.uint8)
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

    def draw_border(rect, title):
        x0, y0, w, h = rect
        cv2.rectangle(canvas, (x0, y0), (x0 + w, y0 + h), (185, 185, 185), 2, cv2.LINE_AA)
        cv2.putText(canvas, title, (x0 + 12, y0 + 28), cv2.FONT_HERSHEY_SIMPLEX, 0.8, (30, 30, 30), 2, cv2.LINE_AA)

    def draw_curve(xs, ys, range_x, range_y, rect, color, thickness=3):
        pts = [map_point(vx, vy, range_x, range_y, rect) for vx, vy in zip(xs, ys)]
        if len(pts) >= 2:
            cv2.polylines(canvas, [np.array(pts, dtype=np.int32)], False, color, thickness, cv2.LINE_AA)

    def draw_points(xs, ys, mask, range_x, range_y, rect):
        for vx, vy, keep in zip(xs, ys, mask):
            color = (46, 204, 113) if keep else (52, 87, 230)
            cv2.circle(canvas, map_point(vx, vy, range_x, range_y, rect), 4, color, -1, cv2.LINE_AA)

    top_rect = panel_rect(0, 0)
    side_rect = panel_rect(0, 1)
    front_rect = panel_rect(1, 0)
    resid_rect = panel_rect(1, 1)

    obs = fit.observed_points
    fit_dense = fit.dense_points
    time_range = (float(np.min(fit.times)), float(np.max(fit.times)))
    resid_max = max(0.2, float(np.max(fit.residuals)) * 1.15)
    z_range = (min(-0.2, float(np.min(obs[:, 2])) - 0.05), max(2.0, float(np.max(fit_dense[:, 2])) + 0.15))

    draw_border(top_rect, "Top View (x-y)")
    draw_curve(fit_dense[:, 0], fit_dense[:, 1], FIELD_X_LIMITS, FIELD_Y_LIMITS, top_rect, (0, 140, 255), 3)
    draw_points(obs[:, 0], obs[:, 1], fit.inlier_mask, FIELD_X_LIMITS, FIELD_Y_LIMITS, top_rect)

    draw_border(side_rect, "Side View (y-z)")
    draw_curve(fit_dense[:, 1], fit_dense[:, 2], FIELD_Y_LIMITS, z_range, side_rect, (180, 80, 200), 3)
    draw_points(obs[:, 1], obs[:, 2], fit.inlier_mask, FIELD_Y_LIMITS, z_range, side_rect)

    draw_border(front_rect, "Front View (x-z)")
    draw_curve(fit_dense[:, 0], fit_dense[:, 2], FIELD_X_LIMITS, z_range, front_rect, (40, 180, 180), 3)
    draw_points(obs[:, 0], obs[:, 2], fit.inlier_mask, FIELD_X_LIMITS, z_range, front_rect)

    draw_border(resid_rect, "Residual by Time")
    draw_curve(fit.times, fit.residuals, time_range, (0.0, resid_max), resid_rect, (120, 90, 60), 2)
    draw_points(fit.times, fit.residuals, fit.inlier_mask, time_range, (0.0, resid_max), resid_rect)

    header = (
        f"{fit.sample_name} | ballistic fit | rmse={fit.rmse_m:.3f}m | reproj={fit.reprojection_rmse_px:.2f}px | "
        f"peak={float(np.max(fit_dense[:, 2])):.3f}m | inliers={int(np.count_nonzero(fit.inlier_mask))}/{len(fit.inlier_mask)}"
    )
    cv2.putText(canvas, header, (40, 24), cv2.FONT_HERSHEY_SIMPLEX, 0.72, (25, 25, 25), 2, cv2.LINE_AA)
    cv2.imwrite(str(path), canvas)


def process_sample(sample_name):
    fit = fit_ballistic_trajectory(load_trajectory(sample_name), load_camera_configs())

    output_dir = OUTPUT_ROOT / sample_name
    output_dir.mkdir(parents=True, exist_ok=True)
    observed_csv_path = output_dir / "ballistic_fit_observed.csv"
    curve_csv_path = output_dir / "ballistic_fit_curve.csv"
    npz_path = output_dir / "ballistic_fit.npz"
    summary_path = output_dir / "ballistic_fit_summary.json"
    preview_path = output_dir / f"{sample_name}_ballistic_fit.png"

    write_observed_csv(observed_csv_path, fit)
    write_curve_csv(curve_csv_path, fit)
    np.savez(
        npz_path,
        times=fit.times,
        observed_points=fit.observed_points,
        fitted_points=fit.fitted_points,
        inlier_mask=fit.inlier_mask.astype(np.uint8),
        residuals=fit.residuals,
        dense_times=fit.dense_times,
        dense_points=fit.dense_points,
        ray_gaps=fit.ray_gaps,
        reprojection_errors=fit.reprojection_errors,
        offset_seconds=np.array([fit.offset_seconds], dtype=np.float64),
        time_origin_sec=np.array([fit.time_origin_sec], dtype=np.float64),
        ballistic_params=np.array([fit.x0, fit.vx, fit.y0, fit.vy, fit.z0, fit.vz, fit.gravity], dtype=np.float64),
        goal_line_crossing_detected=np.array([int(fit.goal_line_crossing_point is not None)], dtype=np.uint8),
        goal_line_crossing_time_sec=np.array(
            [np.nan if fit.goal_line_crossing_time_sec is None else fit.goal_line_crossing_time_sec],
            dtype=np.float64,
        ),
        goal_line_crossing_point=np.full(3, np.nan, dtype=np.float64)
        if fit.goal_line_crossing_point is None
        else np.asarray(fit.goal_line_crossing_point, dtype=np.float64),
        goal_line_crossing_phase_code=np.array(
            [0 if fit.goal_line_crossing_phase is None else (1 if fit.goal_line_crossing_phase == "airborne" else 2)],
            dtype=np.uint8,
        ),
        goal_line_inside_frame=np.array(
            [-1 if fit.goal_line_inside_frame is None else int(fit.goal_line_inside_frame)],
            dtype=np.int8,
        ),
    )
    save_summary(summary_path, fit)
    render_preview(preview_path, fit)

    model_type = "空气阻力模型" if getattr(fit, 'use_drag_model', False) else "纯重力抛物线"
    print(f"[{sample_name}] 拟合点数: {len(fit.times)}，内点: {int(np.count_nonzero(fit.inlier_mask))}")
    print(f"[{sample_name}] 拟合模型: {model_type}")
    print(f"[{sample_name}] 拟合 RMSE: {fit.rmse_m:.4f} m")
    print(f"[{sample_name}] 峰值高度: {float(fit.peak_point[2]):.4f} m")
    if fit.landing_time_sec is not None and fit.landing_point is not None:
        print(f"[{sample_name}] 预计落地时刻: {fit.landing_time_sec:.4f} s")
        print(f"[{sample_name}] 预计落地点: ({fit.landing_point[0]:.3f}, {fit.landing_point[1]:.3f}, {fit.landing_point[2]:.3f})")
    if fit.goal_line_crossing_time_sec is not None and fit.goal_line_crossing_point is not None:
        goal_line_result = "门框内" if fit.goal_line_inside_frame else "门框外"
        phase_label = "未落地前" if fit.goal_line_crossing_phase == "airborne" else "落地后"
        print(
            f"[{sample_name}] goal-line time: {fit.goal_line_crossing_time_sec:.4f} s | "
            f"{phase_label} | {goal_line_result}"
        )
        print(
            f"[{sample_name}] goal-line xyz: "
            f"({fit.goal_line_crossing_point[0]:.3f}, {fit.goal_line_crossing_point[1]:.3f}, {fit.goal_line_crossing_point[2]:.3f})"
        )
    print(f"[{sample_name}] 已保存: {curve_csv_path}")
    print(f"[{sample_name}] 已保存: {summary_path}")


def parse_args():
    parser = argparse.ArgumentParser(description="Fit a ballistic trajectory to reconstructed football 3D points.")
    parser.add_argument("--samples", nargs="*", help="Sample names to process, e.g. sample1 sample2 sample3")
    return parser.parse_args()


def main():
    args = parse_args()
    OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
    sample_names = args.samples if args.samples else sorted(path.name for path in INPUT_ROOT.glob("sample*") if path.is_dir())
    if not sample_names:
        raise FileNotFoundError("未找到 trajectory_3d 的 sample 输出。")

    for sample_name in sample_names:
        process_sample(sample_name)


if __name__ == "__main__":
    main()



