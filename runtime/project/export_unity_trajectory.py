#!/usr/bin/env python3
"""
ballistic_fit_curve.csv -> Unity CSV

Python 世界坐标系（Z-up）:
  X = 场宽 (goal line 方向, 单位 m)
  Y = 场长 (进攻方向, 从球门指向点球点, 单位 m)
  Z = 高度 (向上, 单位 m)

Unity CSV 列约定（与 Goal.CsvBallCenterToWorld 对应）:
  time, x(=前), y(=右), z(=上)
  x = Python Y（从球门向球场）
  y = Python X（门将右侧 / 射手左侧）
  z = Python Z（高度）
"""
import argparse, csv
from pathlib import Path
from project.config import WORKSPACE as BASE_DIR

BALLISTIC_ROOT = BASE_DIR / "output" / "trajectory_ballistic"
UNITY_DATA = BASE_DIR / "data"

# 足球半径（米），确保球心高度不低于此值，球不会陷入地面
BALL_RADIUS_M = 0.11


def convert_sample(name: str):
    p = BALLISTIC_ROOT / name / "ballistic_fit_curve.csv"
    if not p.exists():
        print(f"[{name}] 跳过: 无 ballistic_fit_curve.csv")
        return
    rows = list(csv.DictReader(open(p, encoding="utf-8")))
    if not rows:
        print(f"[{name}] 空")
        return

    UNITY_DATA.mkdir(parents=True, exist_ok=True)
    t0 = float(rows[0]["time_sec"])
    out = UNITY_DATA / f"{name}_trajectory.csv"
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["time", "x", "y", "z"])
        # Unity CsvBallCenterToWorld: x=前, y=门将右侧, z=上
        for r in rows:
            t = float(r["time_sec"]) - t0
            csv_x = r["fit_y_m"]   # Python Y (前) -> Unity CSV x
            csv_y = r["fit_x_m"]   # Python X (右) -> Unity CSV y
            csv_z = max(float(r["fit_z_m"]), BALL_RADIUS_M)  # 球心不低于半径
            w.writerow([f"{t:.6f}", csv_x, csv_y, csv_z])
    print(f"[{name}] -> {out}")


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--samples", nargs="*")
    a = p.parse_args()
    for n in (a.samples or sorted(d.name for d in BALLISTIC_ROOT.glob("sample*") if d.is_dir())):
        convert_sample(n)


if __name__ == "__main__":
    main()
