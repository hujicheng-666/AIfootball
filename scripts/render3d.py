"""离线 3D 人体姿态渲染器（不依赖 Unity）。

用途：把 ``ShooterPoseClip`` JSON 里的 17 关节重建成有体积的人体，用透视相机
从任意角度渲染，用来在**进 Unity 之前**判断三件事：

1. 受试者面朝哪边（是 +Z 还是 -Z）；
2. 左右手 / 左右脚有没有互换（镜像）；
3. 关节有没有反折（膝只能向后弯、肘只能向前弯）。

坐标系约定（与 Unity 侧一致）：X = 右，Y = 上，Z = 前（角色朝 +Z）。

命令行::

    python scripts/render3d.py --clip CMU_10_06 --frames 6 --out .tmp_3d
"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parents[1]

# 17 关节名（与 ShooterPoseClip.jointNames 顺序一致）
J = {
    "hips": 0, "lhip": 1, "lknee": 2, "lankle": 3,
    "rhip": 4, "rknee": 5, "rankle": 6,
    "body": 7, "chsh": 8, "neck": 9, "head": 10,
    "rsh": 11, "relb": 12, "rwri": 13,
    "lsh": 14, "lelb": 15, "lwri": 16,
}

# 骨段：(起点, 终点, 半径 m, 着色类别)
# 半径按真人比例给，只为观感服务，不影响几何判断。
BONES = [
    ("hips", "body", 0.135, "torso"),
    ("body", "chsh", 0.130, "torso"),
    ("chsh", "neck", 0.075, "skin"),
    ("neck", "head", 0.055, "skin"),
    ("hips", "lhip", 0.110, "torso"),
    ("hips", "rhip", 0.110, "torso"),
    ("lhip", "lknee", 0.085, "legL"),
    ("lknee", "lankle", 0.065, "legL"),
    ("lankle", "lankle", 0.060, "legL"),      # 脚（单独画）
    ("rhip", "rknee", 0.085, "legR"),
    ("rknee", "rankle", 0.065, "legR"),
    ("rhip", "rhip", 0.060, "legR"),
    ("chsh", "lsh", 0.070, "torso"),
    ("chsh", "rsh", 0.070, "torso"),
    ("lsh", "lelb", 0.056, "armL"),
    ("lelb", "lwri", 0.046, "armL"),
    ("rsh", "relb", 0.056, "armR"),
    ("relb", "rwri", 0.046, "armR"),
]

# 左肢暖色、右肢冷色 —— 这是判断左右有没有互换的关键：
# 一个面朝 +Z 的人，从 +Z 方向看（正面），他的左手应该出现在画面**右侧**。
PALETTE = {
    "torso": (86, 96, 112),
    "skin": (222, 186, 158),
    "legL": (206, 96, 74),
    "legR": (58, 128, 196),
    "armL": (206, 96, 74),
    "armR": (58, 128, 196),
}

# 方向标：从髋画两根短箭头，蓝=+Z(前)、绿=+X(右)
ARROW_COLORS = {"fwd": (40, 96, 200), "right": (30, 150, 90)}


def rot_y(deg: float) -> np.ndarray:
    r = math.radians(deg)
    c, s = math.cos(r), math.sin(r)
    return np.array([[c, 0, s], [0, 1, 0], [-s, 0, c]])


class Camera:
    """简单透视相机：位置 + 注视点 + 视场角。"""

    def __init__(self, eye: np.ndarray, target: np.ndarray, fov_deg: float,
                 width: int, height: int):
        self.eye = np.asarray(eye, dtype=np.float64)
        self.target = np.asarray(target, dtype=np.float64)
        self.fov = math.radians(fov_deg)
        self.w = width
        self.h = height

        fwd = self.target - self.eye
        fwd /= np.linalg.norm(fwd)
        up0 = np.array([0.0, 1.0, 0.0])
        if abs(np.dot(fwd, up0)) > 0.999:
            up0 = np.array([0.0, 0.0, 1.0])
        # 相机右方 = up × forward（LookRotation 的约定）。
        # 写成 cross(fwd, up) 会得到相反方向，整幅图左右镜像 ——
        # 对「判断左右有没有反」这种任务，镜像的渲染器等于没有。
        right = np.cross(up0, fwd)
        right /= np.linalg.norm(right)
        up = np.cross(fwd, right)
        self.fwd, self.right, self.up = fwd, right, up

    def project(self, pts: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
        """世界点 (N,3) → 屏幕 (N,2) 与相机深度 (N,)。"""
        rel = pts - self.eye
        x = rel @ self.right
        y = rel @ self.up
        z = rel @ self.fwd                      # 前向深度
        z = np.maximum(z, 1e-4)
        f = (self.h * 0.5) / math.tan(self.fov * 0.5)
        sx = self.w * 0.5 + x * f / z
        sy = self.h * 0.5 - y * f / z
        return np.stack([sx, sy], axis=-1), z


def shade(base: tuple[int, int, int], k: float) -> tuple[int, int, int]:
    return tuple(int(max(0, min(255, c * k))) for c in base)


def draw_capsule(draw: ImageDraw.ImageDraw, cam: Camera, a: np.ndarray, b: np.ndarray,
                 radius: float, color: tuple[int, int, int]) -> None:
    """把一段骨画成梯形管状体：宽度随透视缩放，带明暗。"""
    pts, z = cam.project(np.stack([a, b]))
    za, zb = z
    f = (cam.h * 0.5) / math.tan(cam.fov * 0.5)

    # 两端半径在该深度下投影成多少像素
    ra = radius * f / max(za, 1e-3)
    rb = radius * f / max(zb, 1e-3)

    d = pts[1] - pts[0]
    L = float(np.linalg.norm(d))
    if L < 1e-6:
        return
    n = np.array([-d[1], d[0]]) / L          # 屏幕内的法向

    quad = [tuple(pts[0] + n * ra), tuple(pts[1] + n * rb),
            tuple(pts[1] - n * rb), tuple(pts[0] - n * ra)]
    depth = float(min(za, zb))
    draw.polygon(quad, fill=color)

    # 亮部：往光源侧偏一段，模拟圆柱高光
    light = -n * 0.35
    quad2 = [tuple(pts[0] + n * ra * 0.55 + light * ra),
             tuple(pts[1] + n * rb * 0.55 + light * rb),
             tuple(pts[1] - n * rb * 0.15 + light * rb),
             tuple(pts[0] - n * ra * 0.15 + light * ra)]
    draw.polygon(quad2, fill=shade(color, 1.28))
    return depth


def draw_joint(draw: ImageDraw.ImageDraw, cam: Camera, p: np.ndarray,
               radius: float, color: tuple[int, int, int]) -> None:
    pts, z = cam.project(p[None, :])
    f = (cam.h * 0.5) / math.tan(cam.fov * 0.5)
    r = radius * f / max(float(z[0]), 1e-3)
    x, y = float(pts[0, 0]), float(pts[0, 1])
    draw.ellipse([x - r, y - r, x + r, y + r], fill=color)


def foot_box(points: dict, side: str) -> tuple[np.ndarray, np.ndarray]:
    """脚：从踝沿「胫骨方向的垂面」往前延伸 0.22m。方向取数据里的脚尖朝向。"""
    ankle = points[f"{side}ankle"]
    knee = points[f"{side}knee"]
    down = ankle - knee
    down /= max(np.linalg.norm(down), 1e-6)
    # 脚尖 = 世界 +Z 在垂直于小腿的平面上的投影
    fwd = np.array([0.0, 0.0, 1.0])
    fwd = fwd - down * np.dot(fwd, down)
    fwd /= max(np.linalg.norm(fwd), 1e-6)
    return ankle - fwd * 0.05, ankle + fwd * 0.20


def render(points: dict, cam: Camera, title: str = "") -> Image.Image:
    img = Image.new("RGB", (cam.w, cam.h), (245, 246, 248))
    draw = ImageDraw.Draw(img)

    # 地面网格：每 0.5m 一条，用来读位置与朝向
    grid = []
    for gx in np.arange(-3.0, 3.01, 0.5):
        grid.append((np.array([gx, 0.0, -3.0]), np.array([gx, 0.0, 3.0])))
        grid.append((np.array([-3.0, 0.0, gx]), np.array([3.0, 0.0, gx])))
    for a, b in grid:
        pts, z = cam.project(np.stack([a, b]))
        if z.min() <= 0.05:
            continue
        k = 0.55 if abs(a[0] % 1.0) < 1e-6 else 0.80
        draw.line([tuple(pts[0]), tuple(pts[1])], fill=shade((200, 205, 212), k), width=1)

    # 收集所有可绘制体，按深度排序（远的先画）
    items = []
    for na, nb, rad, kind in BONES:
        if na == nb:
            continue
        a, b = points[na], points[nb]
        if np.linalg.norm(b - a) < 1e-5:
            continue
        _, z = cam.project(np.stack([a, b]))
        items.append((float(z.min()), "bone", (a, b, rad, PALETTE[kind])))

    for side in ("l", "r"):
        a, b = foot_box(points, side)
        _, z = cam.project(np.stack([a, b]))
        items.append((float(z.min()), "bone", (a, b, 0.055, (48, 48, 56))))

    # 头：球体（用椭圆近似，半径按透视）
    _, zh = cam.project(points["head"][None, :])
    items.append((float(zh[0]) - 0.001, "head", points["head"]))

    items.sort(key=lambda t: -t[0])

    for _, kind, payload in items:
        if kind == "bone":
            a, b, rad, col = payload
            draw_capsule(draw, cam, a, b, rad, col)
        else:
            p = payload
            pts, z = cam.project(p[None, :])
            f = (cam.h * 0.5) / math.tan(cam.fov * 0.5)
            r = 0.115 * f / max(float(z[0]), 1e-3)
            x, y = float(pts[0, 0]), float(pts[0, 1])
            draw.ellipse([x - r, y - r, x + r, y + r], fill=PALETTE["skin"])

    draw_axes(draw, cam, points["hips"])

    if title:
        draw.text((10, 8), title, fill=(30, 30, 30))
        draw.text((10, cam.h - 18), "暖色=左 冷色=右  蓝箭头=前(+Z)  绿箭头=右(+X)",
                  fill=(90, 90, 96))
    return img


def draw_axes(draw: ImageDraw.ImageDraw, cam: Camera, origin: np.ndarray) -> None:
    """在髋位置画 +Z（蓝，前）与 +X（绿，右）两个箭头，长度 0.32m。

    有了这两根箭头，「人物朝向」就不用猜：只要看胸口朝哪一边，
    就知道数据里的「前」到底指哪儿。
    """
    for key, vec in (("fwd", np.array([0.0, 0.0, 1.0])),
                     ("right", np.array([1.0, 0.0, 0.0]))):
        a = origin
        b = origin + vec * 0.32
        x0, y0 = cam.project(a[None, :])[0][0]
        x1, y1 = cam.project(b[None, :])[0][0]
        col = ARROW_COLORS[key]
        draw.line([float(x0), float(y0), float(x1), float(y1)], fill=col, width=3)
        draw.ellipse([float(x1) - 3, float(y1) - 3, float(x1) + 3, float(y1) + 3], fill=col)


def views(center: np.ndarray, dist: float = 3.7, eye_h: float = 0.05):
    """三个标准机位：正面（从 +Z 看）、正侧（从 +X 看）、斜前 40°。

    center 应取身体中段（髋上方约 0.85m），相机只比它高一点点，
    这样是平视而不是俯视 —— 俯视会把前后方向压扁，看不出踢腿。
    """
    c = center
    return [
        ("从 +Z 看", c + np.array([0.0, eye_h, dist]), c),
        ("从 +X 看", c + np.array([dist, eye_h, 0.0]), c),
        ("从 -Z 看", c + np.array([0.0, eye_h, -dist]), c),
        ("斜前 40°", c + np.array([dist * 0.62, eye_h, dist * 0.78]), c),
    ]


def load_clip(path: Path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def frame_points(clip: dict, idx: int, use_hip: bool = True) -> dict:
    fr = clip["frames"][idx]
    p = np.array(fr["p"], dtype=np.float64).reshape(-1, 3)
    offset = np.zeros(3)
    if use_hip:
        offset = np.array([fr.get("hipX", 0.0), 0.0, fr.get("hipZ", 0.0)])
    p = p + offset
    return {name: p[i] for name, i in J.items()}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--clip", default="CMU_10_06")
    ap.add_argument("--dir", default=str(REPO / "Myproject" / "Assets" / "PenaltyKick"
                                        / "Resources" / "ShooterPoses"))
    ap.add_argument("--frames", type=int, default=6)
    ap.add_argument("--out", default=str(REPO / ".tmp_3d"))
    ap.add_argument("--tag", default="")
    args = ap.parse_args()

    path = Path(args.dir) / f"{args.clip}.json"
    clip = load_clip(path)
    n = len(clip["frames"])
    kick = clip["kickFrame"]
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    # 采样：覆盖助跑、触球、随挥
    idx = np.linspace(0, n - 1, args.frames).round().astype(int)

    W, H = 340, 470
    per_view = len(views(np.zeros(3)))
    sheet = Image.new("RGB", (W * len(idx), H * per_view), "white")

    for col, i in enumerate(idx):
        pts = frame_points(clip, int(i))
        # 身体中段：脚在髋下约 1.03m、头在髋上约 0.6m，取中点才不会把头或脚切掉
        center = np.array([pts["hips"][0], pts["hips"][1] - 0.18, pts["hips"][2]])
        for row, (label, eye, _t) in enumerate(views(center)):
            cam = Camera(eye, center, 32.0, W, H)
            mark = " ◆触球" if abs(i - kick) <= max(1, n // 40) else ""
            tag = f"{args.tag} f{i}/{n}{mark}  {label}" if col == 0 or True else label
            im = render(pts, cam, tag)
            sheet.paste(im, (col * W, row * H))

    dst = out / f"{args.clip}{args.tag}_3d.png"
    sheet.save(dst)
    print(f"saved {dst}  size={sheet.size}  帧 {list(map(int, idx))}  kick={kick}")


if __name__ == "__main__":
    main()
