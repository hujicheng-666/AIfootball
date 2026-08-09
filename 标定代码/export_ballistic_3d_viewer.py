import argparse
import json
import sys
from pathlib import Path

import numpy as np

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

BASE_DIR = Path(__file__).resolve().parent
WORKSPACE_DIR = BASE_DIR.parent
BALLISTIC_ROOT = WORKSPACE_DIR / "output" / "trajectory_ballistic"
TRAJECTORY3D_ROOT = WORKSPACE_DIR / "output" / "trajectory_3d"
PENALTY_SPOT = [0.0, 11.0, 0.0]
GOAL_LINE_Y = 0.0
GOAL_HALF_WIDTH_M = 7.32 / 2.0
GOAL_HEIGHT_M = 2.44

HTML_TEMPLATE = """<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8" />
<meta http-equiv="Cache-Control" content="no-cache, no-store, must-revalidate" />
<meta http-equiv="Pragma" content="no-cache" />
<meta http-equiv="Expires" content="0" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>__TITLE__</title>
<style>
  :root {
    --bg: #08111c;
    --panel: rgba(8, 17, 28, 0.82);
    --text: #e8f0f8;
    --muted: #98a9bb;
    --accent: #ff9f43;
    --curve: #ff9f43;
    --point: #34d399;
    --outlier: #3b82f6;
    --grid: rgba(180, 210, 235, 0.18);
    --goal: #f8fafc;
    --cross: #fb7185;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    font-family: "Segoe UI", "PingFang SC", sans-serif;
    color: var(--text);
    background:
      radial-gradient(circle at top, rgba(36, 77, 122, 0.35), transparent 35%),
      linear-gradient(180deg, #0b1624 0%, #08111c 100%);
  }
  .layout {
    display: grid;
    grid-template-columns: minmax(280px, 360px) 1fr;
    min-height: 100vh;
  }
  .sidebar {
    padding: 22px 20px 18px;
    background: var(--panel);
    backdrop-filter: blur(12px);
    border-right: 1px solid rgba(255,255,255,0.08);
  }
  h1 { margin: 0 0 8px; font-size: 26px; line-height: 1.2; }
  .subtitle { color: var(--muted); font-size: 14px; line-height: 1.6; margin-bottom: 18px; }
  .section { margin-bottom: 18px; }
  .section h2 { font-size: 13px; color: var(--muted); letter-spacing: 0.08em; text-transform: uppercase; margin: 0 0 8px; }
  .metric { display: flex; justify-content: space-between; gap: 12px; padding: 8px 0; border-bottom: 1px solid rgba(255,255,255,0.06); font-size: 14px; }
  .metric span:last-child { color: var(--accent); font-weight: 600; text-align: right; }
  .legend-item { display: flex; align-items: center; gap: 10px; margin: 8px 0; font-size: 14px; color: var(--text); }
  .dot { width: 10px; height: 10px; border-radius: 999px; }
  .line { width: 18px; height: 0; border-top: 3px solid; }
  .hint { color: var(--muted); font-size: 13px; line-height: 1.7; }
  .viewer-wrap { position: relative; min-height: 100vh; }
  canvas { width: 100%; height: 100%; display: block; }
  .overlay { position: absolute; left: 18px; bottom: 18px; padding: 10px 12px; border-radius: 12px; background: rgba(10, 18, 30, 0.72); color: var(--muted); font-size: 12px; line-height: 1.6; }
  @media (max-width: 980px) {
    .layout { grid-template-columns: 1fr; }
    .sidebar { border-right: 0; border-bottom: 1px solid rgba(255,255,255,0.08); }
    .viewer-wrap { min-height: 68vh; }
  }
</style>
</head>
<body>
<div class="layout">
  <aside class="sidebar">
    <h1>__TITLE__</h1>
    <div class="subtitle">真正 3D 世界坐标系下的足球弹道查看器。坐标单位均为米，支持旋转、缩放与平移。</div>
    <div class="section">
      <h2>\u6307\u6807</h2>
      <div class="metric"><span>\u62df\u5408 RMSE</span><span>__RMSE__ m</span></div>
      <div class="metric"><span>\u5cf0\u503c\u9ad8\u5ea6</span><span>__PEAK_HEIGHT__ m</span></div>
      <div class="metric"><span>\u521d\u901f\u5ea6</span><span>__INITIAL_SPEED__ m/s</span></div>
      <div class="metric"><span>\u89c2\u6d4b\u70b9 / \u5185\u70b9</span><span>__NUM_POINTS__ / __NUM_INLIERS__</span></div>
      <div class="metric"><span>\u53cc\u673a\u65f6\u95f4\u504f\u79fb</span><span>__TIME_OFFSET__ s</span></div>
    </div>
    <div class="section">
      <h2>\u8fc7\u7ebf\u68c0\u6d4b</h2>
      <div class="metric"><span>\u72b6\u6001</span><span>__GOAL_LINE_STATUS__</span></div>
      <div class="metric"><span>\u9636\u6bb5</span><span>__GOAL_LINE_PHASE__</span></div>
      <div class="metric"><span>\u8fc7\u7ebf\u65f6\u523b</span><span>__GOAL_LINE_TIME__</span></div>
      <div class="metric"><span>\u8fc7\u7ebf\u5750\u6807</span><span>__GOAL_LINE_XYZ__</span></div>
      <div class="metric"><span>\u662f\u5426\u95e8\u6846\u5185</span><span>__GOAL_FRAME_STATUS__</span></div>
    </div>
    <div class="section">
      <h2>\u6307\u6807</h2>
      <div class="legend-item"><span class="line" style="border-color:#ff9f43"></span><span>\u5f39\u9053\u62df\u5408\u8f68\u8ff9</span></div>
      <div class="legend-item"><span class="dot" style="background:#f43f5e"></span><span>\u5cf0\u503c\u70b9</span></div>
      <div class="legend-item"><span class="dot" style="background:#facc15"></span><span>\u843d\u5730\u70b9</span></div>
      <div class="legend-item"><span class="line" style="border-color:#f8fafc"></span><span>\u7403\u95e8\u7ebf / \u95e8\u6846</span></div>
      <div class="legend-item"><span class="dot" style="background:#fb7185"></span><span>\u8fc7\u7ebf\u70b9</span></div>
      <div class="legend-item"><span class="dot" style="background:#e5e7eb"></span><span>\u539f\u70b9 / \u5750\u6807\u8f74</span></div>
    </div>
    <div class="section hint">
      \u5de6\u952e\u62d6\u52a8\u65cb\u8f6c\uff0c\u6eda\u8f6e\u7f29\u653e\uff0c\u53f3\u952e\u62d6\u52a8\u5e73\u79fb\u3002<br/>
      X \u8f74\u7ea2\u8272\uff0cY \u8f74\u7eff\u8272\uff0cZ \u8f74\u84dd\u8272\u3002<br/>
      \u767d\u8272\u95e8\u6846\u4f4d\u4e8e y = 0 \u5e73\u9762\uff0c\u5730\u9762\u7f51\u683c\u4f4d\u4e8e z = 0\u3002
    </div>
  </aside>
  <main class="viewer-wrap">
    <canvas id="view"></canvas>
    <div class="overlay">\u6837\u4f8b\uff1a__TITLE__<br/>\u4e09\u7ef4\u4e16\u754c\u5750\u6807\uff08\u7c73\uff09<br/>\u7403\u95e8\u7ebf\u5e73\u9762\uff1ay = 0</div>
  </main>
</div>
<script>
const sceneData = __SCENE_DATA__;
const canvas = document.getElementById('view');
const ctx = canvas.getContext('2d');

const state = {
  yaw: -0.95,
  pitch: 0.46,
  distance: 22,
  target: { x: 0, y: 6, z: 0.8 },
  dragging: false,
  panning: false,
  lastX: 0,
  lastY: 0,
};

function initializeCameraFromScene() {
  const allPoints = [];
  for (const p of sceneData.rawPoints) allPoints.push(p);
  for (const p of sceneData.curvePoints) allPoints.push(p);
  for (const p of sceneData.observedPoints) allPoints.push(p);
  if (sceneData.peakPoint) allPoints.push(sceneData.peakPoint);
  if (sceneData.landingPoint) allPoints.push(sceneData.landingPoint);
  if (sceneData.penaltySpot) allPoints.push(sceneData.penaltySpot);
  if (sceneData.goalLineCrossing && sceneData.goalLineCrossing.detected && sceneData.goalLineCrossing.point_xyz_m) {
    allPoints.push(sceneData.goalLineCrossing.point_xyz_m);
  }
  for (const p of goalFramePoints()) allPoints.push(p);
  if (!allPoints.length) return;

  const xs = allPoints.map((p) => p[0]);
  const ys = allPoints.map((p) => p[1]);
  const zs = allPoints.map((p) => p[2]);
  const minX = Math.min(...xs);
  const maxX = Math.max(...xs);
  const minY = Math.min(...ys);
  const maxY = Math.max(...ys);
  const minZ = Math.min(...zs);
  const maxZ = Math.max(...zs);

  state.target = {
    x: (minX + maxX) * 0.5,
    y: (minY + maxY) * 0.5,
    z: Math.max(0.35, (minZ + maxZ) * 0.5 + 0.2),
  };

  const spanX = maxX - minX;
  const spanY = maxY - minY;
  const spanZ = maxZ - minZ;
  const maxSpan = Math.max(spanX, spanY, spanZ, 1.0);
  state.distance = Math.max(10, Math.min(55, maxSpan * 2.2 + 6));
}

function goalFramePoints() {
  if (!sceneData.goalLine) return [];
  const goal = sceneData.goalLine;
  return [
    [-goal.halfWidth, goal.y, 0],
    [goal.halfWidth, goal.y, 0],
    [-goal.halfWidth, goal.y, goal.height],
    [goal.halfWidth, goal.y, goal.height],
  ];
}

function resize() {
  const dpr = window.devicePixelRatio || 1;
  const rect = canvas.getBoundingClientRect();
  canvas.width = Math.max(1, Math.floor(rect.width * dpr));
  canvas.height = Math.max(1, Math.floor(rect.height * dpr));
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  draw();
}
window.addEventListener('resize', resize);
canvas.addEventListener('contextmenu', (e) => e.preventDefault());

canvas.addEventListener('mousedown', (e) => {
  state.dragging = e.button === 0;
  state.panning = e.button === 2;
  state.lastX = e.clientX;
  state.lastY = e.clientY;
});
window.addEventListener('mouseup', () => {
  state.dragging = false;
  state.panning = false;
});
window.addEventListener('mousemove', (e) => {
  if (!state.dragging && !state.panning) return;
  const dx = e.clientX - state.lastX;
  const dy = e.clientY - state.lastY;
  state.lastX = e.clientX;
  state.lastY = e.clientY;

  if (state.dragging) {
    state.yaw -= dx * 0.008;
    state.pitch += dy * 0.006;
    state.pitch = Math.max(-1.35, Math.min(1.35, state.pitch));
  } else if (state.panning) {
    const basis = cameraBasis();
    const scale = state.distance * 0.0025;
    state.target.x += basis.right.x * dx * scale;
    state.target.y += basis.right.y * dx * scale;
    state.target.z += basis.right.z * dx * scale;
    state.target.x += basis.up.x * dy * scale;
    state.target.y += basis.up.y * dy * scale;
    state.target.z += basis.up.z * dy * scale;
  }
  draw();
});
canvas.addEventListener('wheel', (e) => {
  e.preventDefault();
  const zoom = Math.exp(e.deltaY * 0.0012);
  state.distance = Math.max(4, Math.min(120, state.distance * zoom));
  draw();
}, { passive: false });

function vec(x, y, z) { return { x, y, z }; }
function sub(a, b) { return vec(a.x - b.x, a.y - b.y, a.z - b.z); }
function add(a, b) { return vec(a.x + b.x, a.y + b.y, a.z + b.z); }
function mul(a, s) { return vec(a.x * s, a.y * s, a.z * s); }
function dot(a, b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
function cross(a, b) { return vec(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x); }
function norm(a) { return Math.hypot(a.x, a.y, a.z); }
function normalize(a) { const n = norm(a) || 1; return vec(a.x / n, a.y / n, a.z / n); }

function cameraBasis() {
  const eye = vec(
    state.target.x + state.distance * Math.cos(state.pitch) * Math.cos(state.yaw),
    state.target.y + state.distance * Math.cos(state.pitch) * Math.sin(state.yaw),
    state.target.z + state.distance * Math.sin(state.pitch)
  );
  const forward = normalize(sub(state.target, eye));
  let right = normalize(cross(forward, vec(0, 0, 1)));
  if (norm(right) < 1e-5) right = vec(1, 0, 0);
  const up = normalize(cross(right, forward));
  return { eye, forward, right, up };
}

function project(point) {
  const basis = cameraBasis();
  const p = vec(point[0], point[1], point[2]);
  const rel = sub(p, basis.eye);
  const cx = dot(rel, basis.right);
  const cy = dot(rel, basis.up);
  const cz = dot(rel, basis.forward);
  if (cz <= 0.05) return null;

  const width = canvas.clientWidth || 1;
  const height = canvas.clientHeight || 1;
  const focal = Math.min(width, height) * 0.82;
  return {
    x: width * 0.5 + (cx / cz) * focal,
    y: height * 0.54 - (cy / cz) * focal,
    depth: cz,
  };
}

function drawLine3D(a, b, color, width = 1, alpha = 1) {
  const pa = project(a);
  const pb = project(b);
  if (!pa || !pb) return;
  ctx.save();
  ctx.globalAlpha = alpha;
  ctx.strokeStyle = color;
  ctx.lineWidth = width;
  ctx.beginPath();
  ctx.moveTo(pa.x, pa.y);
  ctx.lineTo(pb.x, pb.y);
  ctx.stroke();
  ctx.restore();
}

function drawPolyline3D(points, color, width = 2) {
  ctx.save();
  ctx.strokeStyle = color;
  ctx.lineWidth = width;
  ctx.beginPath();
  let started = false;
  let prevWorld = null;
  for (const pt of points) {
    const p = project(pt);
    if (!p) {
      started = false;
      prevWorld = null;
      continue;
    }
    let breakSegment = false;
    if (prevWorld) {
      const dx = pt[0] - prevWorld[0];
      const dy = pt[1] - prevWorld[1];
      const dz = pt[2] - prevWorld[2];
      breakSegment = Math.hypot(dx, dy, dz) > 0.28;
    }
    if (!started || breakSegment) {
      ctx.moveTo(p.x, p.y);
      started = true;
    } else {
      ctx.lineTo(p.x, p.y);
    }
    prevWorld = pt;
  }
  ctx.stroke();
  ctx.restore();
}

function drawPoint3D(point, color, size = 4, stroke = null) {
  const p = project(point);
  if (!p) return;
  const radius = Math.max(1.5, size * (18 / (p.depth + 2)));
  ctx.save();
  ctx.fillStyle = color;
  ctx.beginPath();
  ctx.arc(p.x, p.y, radius, 0, Math.PI * 2);
  ctx.fill();
  if (stroke) {
    ctx.strokeStyle = stroke;
    ctx.lineWidth = 1.4;
    ctx.stroke();
  }
  ctx.restore();
}

function drawText3D(point, text, color) {
  const p = project(point);
  if (!p) return;
  ctx.save();
  ctx.fillStyle = color;
  ctx.font = '12px Segoe UI, PingFang SC, sans-serif';
  ctx.fillText(text, p.x + 6, p.y - 6);
  ctx.restore();
}

function drawGrid() {
  for (let x = -12; x <= 12; x += 2) {
    drawLine3D([x, -2, 0], [x, 24, 0], 'rgba(180,210,235,0.16)', 1);
  }
  for (let y = -2; y <= 24; y += 2) {
    drawLine3D([-12, y, 0], [12, y, 0], 'rgba(180,210,235,0.16)', 1);
  }
}

function drawGoalFrame() {
  if (!sceneData.goalLine) return;
  const goal = sceneData.goalLine;
  drawLine3D([-12, goal.y, 0], [12, goal.y, 0], 'rgba(248,250,252,0.28)', 1.8);
  drawLine3D([-goal.halfWidth, goal.y, 0], [goal.halfWidth, goal.y, 0], '#f8fafc', 3.0);
  drawLine3D([-goal.halfWidth, goal.y, 0], [-goal.halfWidth, goal.y, goal.height], 'rgba(248,250,252,0.92)', 2.2);
  drawLine3D([goal.halfWidth, goal.y, 0], [goal.halfWidth, goal.y, goal.height], 'rgba(248,250,252,0.92)', 2.2);
  drawLine3D([-goal.halfWidth, goal.y, goal.height], [goal.halfWidth, goal.y, goal.height], 'rgba(248,250,252,0.92)', 2.2);
  drawText3D([goal.halfWidth + 0.35, goal.y, 0.12], '\u7403\u95e8\u7ebf', '#f8fafc');
}

function drawAxes() {
  drawLine3D([0, 0, 0], [6, 0, 0], '#ef4444', 2.5);
  drawLine3D([0, 0, 0], [0, 16, 0], '#22c55e', 2.5);
  drawLine3D([0, 0, 0], [0, 0, 4], '#3b82f6', 2.5);
  drawPoint3D([0, 0, 0], '#e5e7eb', 4);
  drawText3D([6, 0, 0], 'X', '#ef4444');
  drawText3D([0, 16, 0], 'Y', '#22c55e');
  drawText3D([0, 0, 4], 'Z', '#3b82f6');
}

function drawScene() {
  drawGrid();
  drawGoalFrame();
  drawAxes();
  drawPoint3D(sceneData.penaltySpot, '#f1f5f9', 4, '#111827');
  drawText3D(sceneData.penaltySpot, '\u70b9\u7403\u70b9', '#e5e7eb');

  drawPolyline3D(sceneData.curvePoints, '#ff9f43', 3.2);
  if (sceneData.peakPoint) {
    drawPoint3D(sceneData.peakPoint, '#f43f5e', 6, '#ffffff');
    drawText3D(sceneData.peakPoint, '\u5cf0\u503c', '#fda4af');
  }
  if (sceneData.landingPoint) {
    drawPoint3D(sceneData.landingPoint, '#facc15', 6, '#ffffff');
    drawText3D(sceneData.landingPoint, '\u843d\u5730', '#fde68a');
  }
  if (sceneData.goalLineCrossing && sceneData.goalLineCrossing.detected && sceneData.goalLineCrossing.point_xyz_m) {
    const crossPoint = sceneData.goalLineCrossing.point_xyz_m;
    drawPoint3D(crossPoint, '#fb7185', 6.4, '#ffffff');
    drawLine3D([crossPoint[0], crossPoint[1], 0], crossPoint, 'rgba(251,113,133,0.45)', 1.8);
    drawText3D(
      crossPoint,
      sceneData.goalLineCrossing.phase === 'grounded' ? '\u8fc7\u7ebf\uff08\u5730\u9762\uff09' : '\u8fc7\u7ebf\uff08\u7a7a\u4e2d\uff09',
      '#fecdd3'
    );
  }
}

function drawHud() {
  const width = canvas.clientWidth || 1;
  const height = canvas.clientHeight || 1;
  const gradient = ctx.createLinearGradient(0, 0, 0, height);
  gradient.addColorStop(0, 'rgba(6, 12, 20, 0.08)');
  gradient.addColorStop(1, 'rgba(6, 12, 20, 0.32)');
  ctx.fillStyle = gradient;
  ctx.fillRect(0, 0, width, height);

  ctx.fillStyle = 'rgba(232,240,248,0.85)';
  ctx.font = '12px Segoe UI, PingFang SC, sans-serif';
  ctx.fillText(`\u822a\u5411 ${state.yaw.toFixed(2)}  \u4fef\u4ef0 ${state.pitch.toFixed(2)}  \u8ddd\u79bb ${state.distance.toFixed(1)}m`, 16, height - 18);
}

function draw() {
  const width = canvas.clientWidth || 1;
  const height = canvas.clientHeight || 1;
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = '#08111c';
  ctx.fillRect(0, 0, width, height);
  drawScene();
  drawHud();
}

initializeCameraFromScene();
resize();
</script>
</body>
</html>
"""


def load_fit(sample_name):
    sample_dir = BALLISTIC_ROOT / sample_name
    npz_path = sample_dir / "ballistic_fit.npz"
    summary_path = sample_dir / "ballistic_fit_summary.json"
    if not npz_path.exists() or not summary_path.exists():
        raise FileNotFoundError(f"缺少弹道拟合文件: {npz_path} 或 {summary_path}")

    data = np.load(npz_path)
    with open(summary_path, "r", encoding="utf-8") as f:
        summary = json.load(f)
    return sample_dir, data, summary



def load_raw_trajectory(sample_name):
    sample_dir = TRAJECTORY3D_ROOT / sample_name
    npz_path = sample_dir / "trajectory_3d_points.npz"
    if not npz_path.exists():
        raise FileNotFoundError(f"Missing raw triangulated trajectory file: {npz_path}")
    data = np.load(npz_path)
    if "raw_world_points" in data.files:
        return np.asarray(data["raw_world_points"], dtype=np.float64)
    return np.asarray(data["world_points"], dtype=np.float64)


def mirror_point_x(point):
    if point is None:
        return None
    arr = np.asarray(point, dtype=np.float64).reshape(-1).copy()
    if arr.size >= 1:
        arr[0] *= -1.0
    return arr.tolist()


def mirror_points_x(points):
    arr = np.asarray(points, dtype=np.float64).copy()
    if arr.ndim >= 2 and arr.shape[1] >= 1:
        arr[:, 0] *= -1.0
    return arr




def format_point(point):
    if not point:
        return "--"
    return "({:.3f}, {:.3f}, {:.3f})".format(*[float(v) for v in point])


def goal_line_display(summary):
    crossing = summary.get("goal_line_crossing") or {}
    crossing_display = dict(crossing)
    if crossing_display.get("point_xyz_m") is not None:
        crossing_display["point_xyz_m"] = mirror_point_x(crossing_display.get("point_xyz_m"))
    detected = bool(crossing_display.get("detected"))
    phase_map = {
        "airborne": "\u672a\u843d\u5730\u524d",
        "grounded": "\u843d\u5730\u540e",
    }
    frame_map = {
        True: "\u95e8\u6846\u5185",
        False: "\u95e8\u6846\u5916",
        None: "--",
    }
    return {
        "status": "\u5df2\u68c0\u6d4b\u5230" if detected else "\u672a\u68c0\u6d4b\u5230",
        "phase": phase_map.get(crossing_display.get("phase"), "--"),
        "time": "{:.4f} s".format(float(crossing_display["time_sec"])) if crossing_display.get("time_sec") is not None else "--",
        "xyz": format_point(crossing_display.get("point_xyz_m")),
        "frame": frame_map.get(crossing_display.get("inside_goal_frame"), "--"),
        "crossing": crossing_display,
    }





def write_html(sample_name):
    sample_dir, data, summary = load_fit(sample_name)
    raw_points = load_raw_trajectory(sample_name)
    goal_line = goal_line_display(summary)
    scene_data = {
        "sampleName": sample_name,
        "rawPoints": mirror_points_x(raw_points).tolist(),
        "curvePoints": mirror_points_x(np.asarray(data["dense_points"], dtype=np.float64)).tolist(),
        "observedPoints": mirror_points_x(np.asarray(data["observed_points"], dtype=np.float64)).tolist(),
        "inlierMask": np.asarray(data["inlier_mask"], dtype=np.uint8).astype(bool).tolist(),
        "peakPoint": mirror_point_x(summary.get("peak_point_xyz_m")),
        "landingPoint": mirror_point_x(summary.get("landing_point_xyz_m")),
        "penaltySpot": mirror_point_x(PENALTY_SPOT),
        "goalLine": {"y": GOAL_LINE_Y, "halfWidth": GOAL_HALF_WIDTH_M, "height": GOAL_HEIGHT_M},
        "goalLineCrossing": goal_line["crossing"],
    }

    html = HTML_TEMPLATE
    html = html.replace("__TITLE__", sample_name)
    html = html.replace("__RMSE__", f"{float(summary['rmse_m']):.4f}")
    html = html.replace("__PEAK_HEIGHT__", f"{float(summary['peak_point_xyz_m'][2]):.4f}")
    html = html.replace("__INITIAL_SPEED__", f"{float(summary['initial_speed_mps']):.3f}")
    html = html.replace("__NUM_POINTS__", str(int(summary['num_observed_points'])))
    html = html.replace("__NUM_INLIERS__", str(int(summary['num_inlier_points'])))
    html = html.replace("__TIME_OFFSET__", f"{float(summary['time_offset_seconds']):+.4f}")
    html = html.replace("__GOAL_LINE_STATUS__", goal_line["status"])
    html = html.replace("__GOAL_LINE_PHASE__", goal_line["phase"])
    html = html.replace("__GOAL_LINE_TIME__", goal_line["time"])
    html = html.replace("__GOAL_LINE_XYZ__", goal_line["xyz"])
    html = html.replace("__GOAL_FRAME_STATUS__", goal_line["frame"])
    html = html.replace("__SCENE_DATA__", json.dumps(scene_data, ensure_ascii=False))

    out_path = sample_dir / f"{sample_name}_ballistic_fit_3d.html"
    with open(out_path, "w", encoding="utf-8") as f:
        f.write(html)
    return out_path


def parse_args():
    parser = argparse.ArgumentParser(description="Export an interactive true-3D HTML viewer for ballistic football trajectories.")
    parser.add_argument("--samples", nargs="*", help="Sample names to process, e.g. sample1 sample2 sample3")
    return parser.parse_args()


def main():
    args = parse_args()
    sample_names = args.samples if args.samples else sorted(path.name for path in BALLISTIC_ROOT.glob("sample*") if path.is_dir())
    if not sample_names:
        raise FileNotFoundError("未找到 trajectory_ballistic 的 sample 输出。")

    for sample_name in sample_names:
        out_path = write_html(sample_name)
        print(f"[{sample_name}] 已生成真正 3D 轨迹查看器: {out_path}")


if __name__ == "__main__":
    main()









