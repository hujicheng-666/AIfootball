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
import argparse, csv, json, sys
from pathlib import Path
from ..constants import BALL_RADIUS_M
from ..workspace import get_workspace, set_workspace

# 路径不再在 import 期固化（见 workspace.py）：
#   拟合结果 output/trajectory_ballistic -> get_workspace().trajectory_ballistic
#   Unity CSV 输出 data/                 -> get_workspace().data

def convert_sample(name: str):
    ws = get_workspace()
    ballistic_root = ws.trajectory_ballistic
    unity_data = ws.data
    p = ballistic_root / name / "ballistic_fit_curve.csv"
    if not p.exists():
        print(f"[{name}] 跳过: 无 ballistic_fit_curve.csv")
        return
    rows = list(csv.DictReader(open(p, encoding="utf-8")))
    if not rows:
        print(f"[{name}] 空")
        return

    summary_path = ballistic_root / name / "ballistic_fit_summary.json"
    if not summary_path.exists():
        raise RuntimeError(f"[{name}] missing ballistic-fit quality summary: {summary_path}")
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    quality_gate = summary.get("quality_gate", {})
    if not quality_gate.get("export_allowed", False):
        raise RuntimeError(f"[{name}] ballistic fit is not eligible for Unity export.")
    if not quality_gate.get("passed", False):
        print(
            f"[{name}] warning: exporting {quality_gate.get('mode', 'best_effort')} trajectory "
            f"with {quality_gate.get('final_inlier_count', 0)}/"
            f"{quality_gate.get('minimum_inlier_count', 0)} required final inliers."
        )

    below_ground = [row for row in rows if float(row["fit_z_m"]) < BALL_RADIUS_M - 1e-4]
    if below_ground:
        raise RuntimeError(f"[{name}] curve contains {len(below_ground)} ball centres below the ground.")

    unity_data.mkdir(parents=True, exist_ok=True)
    t0 = float(rows[0]["time_sec"])
    out = unity_data / f"{name}_trajectory.csv"
    with open(out, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["time", "x", "y", "z"])
        # Unity CsvBallCenterToWorld: x=前, y=门将右侧, z=上
        for r in rows:
            t = float(r["time_sec"]) - t0
            csv_x = r["fit_y_m"]   # Python Y (前) -> Unity CSV x
            csv_y = r["fit_x_m"]   # Python X (右) -> Unity CSV y
            csv_z = float(r["fit_z_m"])
            w.writerow([f"{t:.6f}", csv_x, csv_y, csv_z])
    print(f"[{name}] -> {out}")


def run(sample_names=None, workspace=None):
    """Unity 导出入口：把拟合曲线转成 Unity 可读 CSV（可被 CLI 同进程调用）。"""
    if workspace is not None:
        set_workspace(workspace)
    ballistic_root = get_workspace().trajectory_ballistic
    names = list(sample_names) if sample_names else sorted(
        d.name for d in ballistic_root.glob("sample*") if d.is_dir())
    for n in names:
        convert_sample(n)
    return names


def parse_args(argv=None):
    p = argparse.ArgumentParser(description="Export ballistic fit curves to Unity CSV.")
    p.add_argument("--samples", nargs="*")
    p.add_argument("--workspace", default=None,
                   help="工作区目录（默认: AIFOOTBALL_WORKSPACE 环境变量，其次当前目录）")
    return p.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    try:
        run(args.samples, workspace=args.workspace)
    except Exception as exc:
        print(f"[错误] {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
