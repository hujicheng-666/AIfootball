#!/usr/bin/env python
"""
足球轨迹分析工具  --  EXE 不包含任何数据，所有资源放在 exe 同级目录。
...
"""

import argparse
import os
import subprocess
import sys
from pathlib import Path


def get_base_dir():
    if getattr(sys, 'frozen', False):
        return Path(sys.executable).parent
    return Path(__file__).resolve().parent


BASE_DIR = get_base_dir()
os.chdir(str(BASE_DIR))

# 确保 BASE_DIR 在 Python 搜索路径中（嵌入式 Python 兼容）
if str(BASE_DIR) not in sys.path:
    sys.path.insert(0, str(BASE_DIR))

# 必须在导入其他 project 模块之前设置
from project.config import init as _init_config
_init_config(BASE_DIR)

# YOLO 模型路径：exe 内优先，外部文件兜底
def _find_yolo(name="yolo11m.pt"):
    if getattr(sys, 'frozen', False):
        internal = Path(sys._MEIPASS) / name
        if internal.exists():
            return str(internal)
    external = BASE_DIR / name
    if external.exists():
        return str(external)
    return name  # 让 ultralytics 自己下载


# ═══════════════════════════════════════════════════════════════
#  Unity 启动
# ═══════════════════════════════════════════════════════════════

UNITY_EXE_NAME = "FootballViewer.exe"  # 首选名称


def _find_unity_exe():
    """自动检测 Unity 构建的 exe"""
    # 1) 首选 FootballViewer.exe
    exe = BASE_DIR / UNITY_EXE_NAME
    if exe.exists():
        return exe

    # 2) 扫描目录下所有 exe，找有匹配 _Data 文件夹的（Unity 构建特征）
    for candidate in sorted(BASE_DIR.glob("*.exe")):
        if candidate.name == "FootballTrajectory.exe":
            continue
        data_dir = BASE_DIR / f"{candidate.stem}_Data"
        if data_dir.is_dir():
            return candidate

    return None


def _launch_unity_viewer(sample_names=None):
    """Launch Unity viewer after processing unless disabled by caller."""
    if os.environ.get("AI_FOOTBALL_NO_VIEWER") == "1":
        return
    exe = _find_unity_exe()
    if exe is None:
        print(f"\n[提示] 未找到 Unity exe，跳过启动展示程序。")
        print(f"       请将 Unity Build 的 exe 放置到: {BASE_DIR}")
        return

    cmd = [str(exe)]
    if sample_names:
        csv_path = BASE_DIR / "data" / f"{sample_names[0]}_trajectory.csv"
        if csv_path.exists():
            cmd += ["--csv", str(csv_path)]

    print(f"\n启动 Unity 展示程序: {' '.join(cmd)}")
    try:
        subprocess.Popen(cmd, cwd=str(BASE_DIR))
    except Exception as e:
        print(f"启动失败: {e}")


# ═══════════════════════════════════════════════════════════════
#  离线 pipeline
# ═══════════════════════════════════════════════════════════════

def run_offline(samples, yolo_model, imgsz, conf, skip_reconstruct, skip_ballistic):
    from project.reconstruct_3d_trajectory import load_camera_configs, process_sample as recon_sample
    from project.fit_ballistic_trajectory import process_sample as ballistic_sample
    from project.export_unity_trajectory import convert_sample

    camera_configs = load_camera_configs()
    model = None
    if not skip_reconstruct:
        from ultralytics import YOLO
        model = YOLO(_find_yolo(yolo_model))

    all_ok = True
    completed = []  # 成功处理的样本名，用于传给 Unity
    for name in samples:
        from project.config import SAMPLES
        sample_dir = SAMPLES / name
        videos = sorted(sample_dir.glob("*.mp4"))
        if len(videos) != 2:
            print(f"[{name}] 需要正好 2 个 mp4，找到 {len(videos)} 个")
            all_ok = False
            continue

        if not skip_reconstruct:
            print(f"\n[{name}] 3D 重建...")
            try:
                recon_sample(sample_dir, camera_configs, model, imgsz=imgsz, conf=conf)
                print(f"[{name}] 3D 重建 完成")
            except Exception as e:
                print(f"[{name}] 3D 重建 失败: {e}")
                all_ok = False
                continue

        if not skip_ballistic:
            print(f"[{name}] 弹道拟合...")
            try:
                ballistic_sample(name)
                print(f"[{name}] 弹道拟合 完成")
            except Exception as e:
                print(f"[{name}] 弹道拟合 失败: {e}")
                all_ok = False
                continue

        print(f"[{name}] 导出 Unity CSV...")
        try:
            convert_sample(name)
            print(f"[{name}] Unity CSV 完成")
            completed.append(name)
        except Exception as e:
            print(f"[{name}] Unity CSV 失败: {e}")
            all_ok = False

    if all_ok and completed:
        _launch_unity_viewer(completed)
    elif completed:
        _launch_unity_viewer(completed)
    return all_ok


# ═══════════════════════════════════════════════════════════════
#  在线模式
# ═══════════════════════════════════════════════════════════════

def run_online(cam_left, cam_right, sample_name, yolo_model, imgsz, conf):
    from project.camera_capture import DualCameraRecorder
    from project.config import SAMPLES

    sample_dir = SAMPLES / sample_name
    print(f"\n在线模式 -> {sample_dir}")

    recorder = DualCameraRecorder(cam_left, cam_right, sample_dir)
    if not recorder.start():
        print("用户取消录制")
        return False

    return run_offline([sample_name], yolo_model, imgsz, conf, False, False)


def run_online_live(cam_left, cam_right, sample_name, imgsz, conf,
                    analyze_every=1, no_save=False):
    """边录制边分析：实时检测叠加，停止后立即出结果"""
    from project.live_analysis import run as live_run
    return live_run(cam_left, cam_right, sample_name, imgsz, conf,
                    analyze_every, no_save)


# ═══════════════════════════════════════════════════════════════
#  交互模式 (exe 双击)
# ═══════════════════════════════════════════════════════════════

def run_interactive():
    print("\n" + "=" * 50)
    print("  足球轨迹分析工具")
    print("=" * 50)
    print("  [1] 离线模式 - 处理已有视频")
    print("  [2] 在线模式 - 连接摄像头录制并处理")
    print("  [3] 列出可用资源")
    print("  [0] 退出")
    print("=" * 50)

    try:
        choice = input("请选择: ").strip()
    except (EOFError, KeyboardInterrupt):
        return

    if choice == "1":
        samples = _scan_samples()
        if not samples:
            print("未找到包含两个 mp4 的样本。请将视频放入 samples/sampleX/ 中。")
            return
        print(f"\n可用: {', '.join(samples)}")
        sel = input("处理哪些? (空格分隔，默认全部): ").strip()
        targets = sel.split() if sel else samples
        ok = run_offline(targets, "yolo11m.pt", 640, 0.15, False, False)
        print("\n完成" if ok else "\n失败")

    elif choice == "2":
        from project.camera_capture import DualCameraRecorder

        print("\n采集方式:")
        print("  [1] USB/有线相机（电脑直连摄像头）")
        print("  [2] 手机/网络相机（手机推流到电脑）")
        src_mode = input("请选择 [1]: ").strip() or "1"

        if src_mode == "2":
            cl = input("左路视频流 (如 http://192.168.1.101:8080/video 或 rtsp://...): ").strip()
            cr = input("右路视频流: ").strip()
            if not cl or not cr:
                print("两个视频流地址都不能为空。")
                return
            print("\n测试连接（可跳过）...")
            ok, info = DualCameraRecorder.test_stream(cl)
            print(f"  左路: {info}")
            ok2, info2 = DualCameraRecorder.test_stream(cr)
            print(f"  右路: {info2}")
            if not (ok and ok2):
                print("存在连接失败，确认手机已推流且在同一路由器下。")
        else:
            cams = DualCameraRecorder.list_cameras()
            if len(cams) < 2:
                print(f"检测到 {len(cams)} 个摄像头，需要至少 2 个。请连接后重试。")
                return
            print(f"\n摄像头: {cams}")
            cl = input(f"左相机索引 [{cams[0]}]: ").strip()
            cr = input(f"右相机索引 [{cams[1]}]: ").strip()
            cl = int(cl) if cl else cams[0]
            cr = int(cr) if cr else cams[1]

        sn = input("样本名 [sample_live]: ").strip() or "sample_live"
        live = input("边录制边分析? [y/N]: ").strip().lower() in ("y", "yes")
        if live:
            ok = run_online_live(cl, cr, sn, 640, 0.15)
        else:
            ok = run_online(cl, cr, sn, "yolo11m.pt", 640, 0.15)
        print("\n完成" if ok else "\n失败")

    elif choice == "3":
        samples = _scan_samples()
        print(f"\n离线样本: {', '.join(samples) if samples else '无'}")
        from project.camera_capture import DualCameraRecorder
        print(f"摄像头: {DualCameraRecorder.list_cameras()}")
        calib = all((BASE_DIR / "calib" / f).exists() for f in [
            "left_pose.npz", "right_pose.npz",
            "left_extrinsics.json", "right_extrinsics.json",
            "intrinsics_left.npz", "intrinsics_right.npz",
        ])
        print(f"相机标定: {'已就绪' if calib else '缺失'}")
        print(f"YOLO 模型: 已打包在 exe 内")

    elif choice == "0":
        return


# ═══════════════════════════════════════════════════════════════
#  入口
# ═══════════════════════════════════════════════════════════════

def _scan_samples():
    """扫描 samples/ 下的样本目录（需包含 2 个 mp4）"""
    from project.config import SAMPLES
    return sorted(
        d.name for d in SAMPLES.iterdir()
        if d.is_dir() and d.name.startswith("sample")
        and len(list(d.glob("*.mp4"))) >= 2
    )

def main():
    parser = argparse.ArgumentParser(description="足球轨迹分析工具")
    parser.add_argument("--samples", nargs="*", help="离线模式: 要处理的 sample 目录")
    parser.add_argument("--online", action="store_true", help="在线模式: 连接摄像头录制并处理")
    parser.add_argument("--online-live", action="store_true", help="边录制边分析: 实时检测叠加，停止后立即出结果")
    parser.add_argument("--cam-left", help="左相机索引或 RTSP/HTTP URL")
    parser.add_argument("--cam-right", help="右相机索引或 RTSP/HTTP URL")
    parser.add_argument("--analyze-every", type=int, default=1, help="边录边分析: 每隔 N 帧分析一次")
    parser.add_argument("--no-save", action="store_true", help="边录边分析: 不落盘 mp4")
    parser.add_argument("--test-stream", help="测试网络视频流地址 (RTSP/HTTP)，如 http://192.168.1.101:8080/video")
    parser.add_argument("--sample", type=str, default="sample_live", help="在线模式输出目录名")
    parser.add_argument("--yolo-model", type=str, default="yolo11m.pt")
    parser.add_argument("--imgsz", type=int, default=640)
    parser.add_argument("--conf", type=float, default=0.15)
    parser.add_argument("--skip-reconstruct", action="store_true")
    parser.add_argument("--skip-ballistic", action="store_true")
    parser.add_argument("--list", action="store_true", help="列出可用资源")
    args = parser.parse_args()

    # 无参数 -> 交互模式
    if len(sys.argv) == 1:
        run_interactive()
        return

    if args.list:
        samples = _scan_samples()
        print("样本:", ", ".join(samples) if samples else "无")
        from project.camera_capture import DualCameraRecorder
        print("摄像头:", DualCameraRecorder.list_cameras())
        return

    if args.test_stream:
        from project.camera_capture import DualCameraRecorder
        ok, info = DualCameraRecorder.test_stream(args.test_stream)
        print(info)
        sys.exit(0 if ok else 1)

    if args.online_live:
        cl = args.cam_left if args.cam_left is not None else 0
        cr = args.cam_right if args.cam_right is not None else 1
        ok = run_online_live(cl, cr, args.sample, args.imgsz, args.conf,
                             args.analyze_every, args.no_save)
    elif args.online:
        cl = args.cam_left if args.cam_left is not None else 0
        cr = args.cam_right if args.cam_right is not None else 1
        ok = run_online(cl, cr, args.sample, args.yolo_model, args.imgsz, args.conf)
    else:
        targets = args.samples if args.samples else _scan_samples()
        if not targets:
            print("未找到包含两个 mp4 的 sample 目录。请将视频放入 samples/sampleX/ 中。")
            sys.exit(1)
        ok = run_offline(targets, args.yolo_model, args.imgsz, args.conf,
                         args.skip_reconstruct, args.skip_ballistic)

    print("\n全部完成！" if ok else "\n部分步骤失败")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
