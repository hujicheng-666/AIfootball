"""AIfootball Engine CLI — 与独立运行版共用同一套成熟算法。"""
import argparse
import subprocess
import sys
import os
from pathlib import Path

ENGINE_DIR = Path(__file__).parent


def resolve_pipeline_dir(workspace: str) -> Path:
    """查找独立运行版使用的完整流水线，避免维护两套不同算法。"""
    root = Path(workspace).resolve()
    python_root = Path(sys.executable).resolve().parent
    candidates = (
        root / "project",
        root / "runtime" / "project",
        root / "release" / "project",
        python_root / "project",
        # 嵌入式 Python 发布布局：python_env/ 与 project/ 同级
        python_root.parent / "project",
        root / "python_env" / "project",
    )
    required = (
        "reconstruct_3d_trajectory.py",
        "fit_ballistic_trajectory.py",
        "export_unity_trajectory.py",
        "config.py",
    )
    for candidate in candidates:
        if all((candidate / name).is_file() for name in required):
            return candidate
    searched = "\n  ".join(str(path) for path in candidates)
    raise FileNotFoundError("找不到完整的 3D 重建流水线。已检查：\n  " + searched)


def run_script(script_name: str, args: list, workspace: str, pipeline_dir: Path):
    """直接运行成熟流水线脚本，输出直通父进程。"""
    script = pipeline_dir / script_name
    # The embedded Python distributed with the desktop application runs in
    # isolated mode and ignores PYTHONPATH.  Add the package root explicitly
    # before executing the script so ``from project...`` always resolves.
    package_root = str(pipeline_dir.parent).replace("\\", "\\\\")
    script_path = str(script).replace("\\", "\\\\")
    bootstrap = (
        "import runpy, sys; "
        f"sys.path.insert(0, r'{package_root}'); "
        f"runpy.run_path(r'{script_path}', run_name='__main__')"
    )
    return subprocess.run([sys.executable, "-c", bootstrap] + args,
                          cwd=str(Path(workspace).resolve()), env=os.environ.copy())


def run_offline_pipeline(samples, skip_reconstruct, skip_ballistic, workspace, pipeline_dir):
    """对给定样本依次执行 3D 重建 -> 弹道拟合 -> Unity 导出。"""
    all_ok = True
    for name in samples:
        sa = ["--samples", name]
        if not skip_reconstruct:
            print(f"[1/3] 3D 重建: {name}")
            r = run_script("reconstruct_3d_trajectory.py", sa, workspace, pipeline_dir)
            if r.returncode != 0:
                print(f"  失败 (exit={r.returncode})")
                all_ok = False
                continue

        if not skip_ballistic:
            print(f"[2/3] 弹道拟合: {name}")
            r = run_script("fit_ballistic_trajectory.py", sa, workspace, pipeline_dir)
            if r.returncode != 0:
                print(f"  失败 (exit={r.returncode})")
                all_ok = False
                continue

        print(f"[3/3] Unity 导出: {name}")
        r = run_script("export_unity_trajectory.py", sa, workspace, pipeline_dir)
        if r.returncode != 0:
            print(f"  失败 (exit={r.returncode})")
            all_ok = False
            continue

        csv_path = Path(workspace).resolve() / "data" / f"{name}_trajectory.csv"
        if not csv_path.is_file():
            print(f"  失败：未生成 Unity CSV: {csv_path}")
            all_ok = False
            continue
        print(f"  {name} 完成")

    return all_ok


def record_online(cam_left, cam_right, sample_name, workspace, pipeline_dir):
    """使用成熟的双摄录制脚本采集视频到 samples/<sample_name>/。"""
    sample_dir = str(Path(workspace).resolve() / "samples" / sample_name).replace("\\", "\\\\")
    package_root = str(pipeline_dir.parent).replace("\\", "\\\\")
    cam_left = str(cam_left).replace("\\", "\\\\")
    cam_right = str(cam_right).replace("\\", "\\\\")
    bootstrap = (
        "import sys; "
        f"sys.path.insert(0, r'{package_root}'); "
        "from project.camera_capture import DualCameraRecorder; "
        f"rec = DualCameraRecorder(r'{cam_left}', r'{cam_right}', r'{sample_dir}'); "
        "sys.exit(0 if rec.start() else 3)"
    )
    return subprocess.run([sys.executable, "-c", bootstrap],
                          cwd=str(Path(workspace).resolve()), env=os.environ.copy())


def main():
    parser = argparse.ArgumentParser(description="AIfootball Engine")
    sub = parser.add_subparsers(dest="command")

    off = sub.add_parser("offline", help="离线处理")
    off.add_argument("--samples", nargs="*")
    off.add_argument("--skip-reconstruct", action="store_true")
    off.add_argument("--skip-ballistic", action="store_true")
    off.add_argument("--workspace", default=".")

    onl = sub.add_parser("online", help="在线录制并处理")
    onl.add_argument("--cam-left", default="0", help="左相机索引或 RTSP/HTTP URL")
    onl.add_argument("--cam-right", default="1", help="右相机索引或 RTSP/HTTP URL")
    onl.add_argument("--sample", default="sample_live", help="本次采集的样本名称")
    onl.add_argument("--workspace", default=".")

    liv = sub.add_parser("online-live", help="边录制边分析（实时检测叠加，停止后立即出结果）")
    liv.add_argument("--cam-left", default="0", help="左相机索引或 RTSP/HTTP URL")
    liv.add_argument("--cam-right", default="1", help="右相机索引或 RTSP/HTTP URL")
    liv.add_argument("--sample", default="sample_live", help="本次采集的样本名称")
    liv.add_argument("--imgsz", type=int, default=1280, help="检测输入尺寸")
    liv.add_argument("--conf", type=float, default=0.15, help="检测置信度阈值")
    liv.add_argument("--analyze-every", type=int, default=1, help="每隔 N 帧分析一次")
    liv.add_argument("--no-save", action="store_true", help="不落盘 mp4")
    liv.add_argument("--workspace", default=".")

    tst = sub.add_parser("teststream", help="测试网络视频流连接")
    tst.add_argument("--source", required=True, help="RTSP/HTTP 视频流地址，如 rtsp://192.168.1.5:8554/... 或 http://192.168.1.5:8080/video")
    tst.add_argument("--timeout", type=int, default=8, help="连接超时秒数")
    tst.add_argument("--workspace", default=".")

    cal = sub.add_parser("calibrate-intrinsics", help="视频内参标定：左右各用一段棋盘格视频")
    cal.add_argument("--left", required=True, help="左相机棋盘格视频文件")
    cal.add_argument("--right", required=True, help="右相机棋盘格视频文件")
    cal.add_argument("--calib", default="calib", help="标定输出目录（默认 calib）")
    cal.add_argument("--workspace", default=".")

    cex = sub.add_parser("calibrate-extrinsics", help="外参标定：射门视角左右各一张足球场照片 + 点击参考点")
    cex.add_argument("--left-image", dest="left_image", required=True, help="射门视角左侧参考照片（左相机）")
    cex.add_argument("--right-image", dest="right_image", required=True, help="射门视角右侧参考照片（右相机）")
    cex.add_argument("--intrinsics-left", dest="intrinsics_left", default=None, help="左内参 npz 路径")
    cex.add_argument("--intrinsics-right", dest="intrinsics_right", default=None, help="右内参 npz 路径")
    cex.add_argument("--calib", default="calib", help="标定输出目录（默认 calib）")
    cex.add_argument("--workspace", default=".")

    args = parser.parse_args()
    ws = getattr(args, "workspace", ".")

    if args.command in ("offline", "online", "online-live", "calibrate-intrinsics", "calibrate-extrinsics"):
        try:
            pipeline_dir = resolve_pipeline_dir(ws)
        except FileNotFoundError as exc:
            print(str(exc), file=sys.stderr)
            return 2

    if args.command == "offline":
        samples = args.samples or [d.name for d in (Path(ws) / "samples").iterdir()
                                   if d.is_dir() and len(list(d.glob("*.mp4"))) >= 2]
        if not samples:
            print("没有可用样本")
            return 1

        print(f"离线处理 {len(samples)} 个样本")
        return 0 if run_offline_pipeline(
            samples, args.skip_reconstruct, args.skip_ballistic, ws, pipeline_dir) else 1

    if args.command == "online":
        # 目标样本目录
        sample_dir = Path(ws).resolve() / "samples" / args.sample
        if sample_dir.exists() and len(list(sample_dir.glob("*.mp4"))) >= 2:
            print(f"样本目录已存在视频，跳过录制: {sample_dir}")
        else:
            print(f"在线录制 -> {sample_dir}")
            r = record_online(args.cam_left, args.cam_right, args.sample, ws, pipeline_dir)
            if r.returncode != 0:
                print(f"录制失败或取消 (exit={r.returncode})")
                return 1

        return 0 if run_offline_pipeline([args.sample], False, False, ws, pipeline_dir) else 1

    if args.command == "online-live":
        sa = [
            "--cam-left", str(args.cam_left),
            "--cam-right", str(args.cam_right),
            "--sample", args.sample,
            "--imgsz", str(args.imgsz),
            "--conf", str(args.conf),
            "--analyze-every", str(args.analyze_every),
        ]
        if args.no_save:
            sa.append("--no-save")
        print(f"边录制边分析 -> samples/{args.sample}")
        r = run_script("live_analysis.py", sa, ws, pipeline_dir)
        return 0 if r.returncode == 0 else 1

    if args.command == "teststream":
        try:
            pipeline_dir = resolve_pipeline_dir(ws)
        except FileNotFoundError as exc:
            print(str(exc), file=sys.stderr)
            return 2
        package_root = str(pipeline_dir.parent).replace("\\", "\\\\")
        source = args.source.replace("\\", "\\\\")
        bootstrap = (
            "import sys; "
            f"sys.path.insert(0, r'{package_root}'); "
            "from project.camera_capture import DualCameraRecorder; "
            f"ok, info = DualCameraRecorder.test_stream(r'{source}', {args.timeout}); "
            "print(info); sys.exit(0 if ok else 1)"
        )
        r = subprocess.run([sys.executable, "-c", bootstrap],
                           cwd=str(Path(ws).resolve()), env=os.environ.copy())
        return r.returncode

    if args.command == "calibrate-intrinsics":
        try:
            pipeline_dir = resolve_pipeline_dir(ws)
        except FileNotFoundError as exc:
            print(str(exc), file=sys.stderr)
            return 2
        sa = ["--video", f"left={args.left}", "--video", f"right={args.right}",
              "--out", args.calib]
        r = run_script("calibrate_intrinsics.py", sa, ws, pipeline_dir)
        return 0 if r.returncode == 0 else 1

    if args.command == "calibrate-extrinsics":
        try:
            pipeline_dir = resolve_pipeline_dir(ws)
        except FileNotFoundError as exc:
            print(str(exc), file=sys.stderr)
            return 2
        sa = ["--tasks", "left", "right", "--install-calib", args.calib,
              "--left-image", args.left_image, "--right-image", args.right_image]
        if args.intrinsics_left:
            sa += ["--intrinsics-left", args.intrinsics_left]
        if args.intrinsics_right:
            sa += ["--intrinsics-right", args.intrinsics_right]
        r = run_script("estimate_extrinsics.py", sa, ws, pipeline_dir)
        return 0 if r.returncode == 0 else 1

    parser.print_help()
    return 1


if __name__ == "__main__":
    sys.exit(main())
