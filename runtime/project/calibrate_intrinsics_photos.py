"""
照片内参标定：左右相机各用多张棋盘格照片标定内参（焦距/主点/畸变）。

用法：
  python calibrate_intrinsics_photos.py \
      --left  <左相机棋盘照片目录> \
      --right <右相机棋盘照片目录> \
      [--calib calib] [--cols 9 --rows 6 --square 23]

说明：
  - 每个目录下放该相机拍摄的多张棋盘格照片（jpg/png/bmp），建议 10 张以上，
    多角度、多距离、覆盖画面各区域、避免反光。
  - --cols/--rows 为棋盘格内角点数（默认 9x6），--square 为格子边长 mm（默认 23）。
  - 结果写入 calib/intrinsics_left.npz、calib/intrinsics_right.npz，供外参标定与重建使用。
"""
import argparse
import sys
from pathlib import Path

import cv2
import numpy as np

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

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


def main():
    parser = argparse.ArgumentParser(
        description="照片内参标定：左右相机各用多张棋盘格照片标定内参。")
    parser.add_argument("--left", required=True, help="左相机棋盘格照片目录")
    parser.add_argument("--right", required=True, help="右相机棋盘格照片目录")
    parser.add_argument("--calib", default="calib", help="标定输出目录（默认 calib）")
    parser.add_argument("--cols", type=int, default=9, help="棋盘格内角点列数（默认 9）")
    parser.add_argument("--rows", type=int, default=6, help="棋盘格内角点行数（默认 6）")
    parser.add_argument("--square", type=float, default=23.0, help="棋盘格边长 mm（默认 23）")
    args = parser.parse_args()

    calib_dir = Path(args.calib).resolve()

    left = calibrate_folder(args.left, args.cols, args.rows, args.square, "左相机")
    right = calibrate_folder(args.right, args.cols, args.rows, args.square, "右相机")

    save_intrinsics(calib_dir, "left", left)
    save_intrinsics(calib_dir, "right", right)

    print("\n===== 内参标定结果 =====")
    for side, r in (("left", left), ("right", right)):
        print(f"[{side}] RMS 重投影误差: {r['rms']:.4f} px")
        print(f"        相机矩阵:\n{r['camera_matrix']}")
        print(f"        畸变系数: {r['dist_coeffs'].ravel()}")
        print(f"        图像尺寸: {r['image_size']}")

    print("\n内参标定完成。接下来请进行外参标定（左右各一张足球场照片）。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
