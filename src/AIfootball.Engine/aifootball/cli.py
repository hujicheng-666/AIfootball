"""AIfootball Engine 命令行入口 —— 界面（WPF）与脚本都调用这一层。

对外契约：**子命令名与参数名不要随意改**，App 侧按名字传参：

    offline --samples A B [--skip-reconstruct] [--skip-ballistic] [--workspace DIR] [--imgsz N] [--conf F]
    online --cam-left X --cam-right Y --sample NAME [--workspace DIR] [--reuse-existing]
    online-live --cam-left X --cam-right Y --sample NAME [--workspace DIR] [--imgsz N] [--conf F]
                [--analyze-every N] [--no-save]
    teststream --source URL [--timeout S] [--workspace DIR]
    list-cameras [--workspace DIR]
    calibrate-intrinsics --left VIDEO --right VIDEO [--calib DIR] [--workspace DIR]
    calibrate-extrinsics --left-image F --right-image F [--intrinsics-left F] [--intrinsics-right F]
                         [--calib DIR] [--workspace DIR]
    selfcheck [--workspace DIR]
    evaluate --shots N [--seed N] [--columns N] [--rows N] [--observation on|off]
             [--reaction-delay S] [--confidence F] [--noise F] [--jitter F] [--goalkeeper NAME]
             [--unity EXE] [--out DIR] [--expect-save-rate MIN MAX]
             [--generate-only] [--summarize-only RESULTS.jsonl] [--workspace DIR]
    pose inspect [--dataset DIR] [--split train|test] [--workspace DIR]
    pose train --out DIR [--variants N] [--epochs N] [--seed N] [--dataset DIR] [--workspace DIR]
    pose run [--dataset DIR] [--out DIR] [--variants N] [--epochs N] [--folds N]
             [--transfer-epochs N] [--augment N] [--max-sources N] [--threads N]
             [--fail-on-no-go] [--workspace DIR]

单进程流水线：`offline` / `online` 在**同一个解释器**里顺序调用
`pipeline.reconstruction` -> `pipeline.ballistics` -> `pipeline.export_unity`，
因此 YOLO/torch 只加载一次。旧实现为每个阶段各起一个 Python 进程，
每个进程都要重新 import torch+ultralytics（冷启动 10 秒以上）。
"""
import argparse
import contextlib
import importlib
import json
import platform
import shutil
import sys
from pathlib import Path

from . import __version__
from .workspace import get_workspace, set_workspace

try:
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
except Exception:
    pass

# 一段真实录制至少也有几十 KB；中断的录制常留下几十字节、读不出 moov atom
# 的空壳文件，不能把它当成"这个样本已经录好了"。
MIN_VIDEO_BYTES = 1024


def resolve_workspace(value):
    """解析工作区：只有显式传入 `--workspace` 才把它固定下来。

    未传入时保持惰性解析（`AIFOOTBALL_WORKSPACE` 环境变量优先，其次当前
    目录）。如果给 argparse 的默认值写成 "."，解析参数时就会无条件
    `set_workspace(".")`，把环境变量盖掉 —— 界面只设环境变量、不传参数
    时便会认错工作区。
    """
    return set_workspace(value) if value else get_workspace()


def has_camera_videos(sample_dir: Path) -> bool:
    """A sample is valid only when each named camera folder contains one MP4."""
    return all(
        len([path for path in (sample_dir / camera_name).glob("*.mp4")
             if path.is_file() and path.stat().st_size >= MIN_VIDEO_BYTES]) == 1
        for camera_name in ("left", "right")
    )


def clear_previous_recording(sample_dir: Path) -> None:
    """录制前清掉上一次的成片与半成品。

    中断的录制会留下读不出 moov atom 的几十字节 mp4；留着它既可能被
    VideoWriter 续写，也会让离线阶段报"无法打开视频"。每次点在线采集都
    应该从零开始，所以这里显式清理，并顺手清掉 .partial/ 中间目录。
    """
    for name in ("left", "right"):
        camera_dir = sample_dir / name
        if not camera_dir.is_dir():
            continue
        for stale in list(camera_dir.glob("*.mp4")) + list(camera_dir.glob("*.part")):
            try:
                stale.unlink()
            except OSError as exc:
                print(f"  清理旧录制失败 {stale}: {exc}")
    shutil.rmtree(sample_dir / ".partial", ignore_errors=True)


def resolve_calib_dir(value, ws) -> Path:
    """把 --calib 解析成绝对路径：相对路径按工作区根解析。

    旧实现是把脚本的 cwd 设成工作区，`calib` 这样的相对路径自然落在
    <workspace>/calib；现在进程 cwd 可能不同，必须显式拼工作区。
    """
    path = Path(value).expanduser() if value else ws.calib
    if not path.is_absolute():
        path = ws.root / path
    return path


def _run_pipeline(ws, samples, skip_reconstruct, skip_ballistic,
                  imgsz=None, conf=None) -> int:
    """单进程依次跑 重建 -> 拟合 -> 导出，返回退出码。

    打印顺序与旧的"每个阶段一个子进程"版本保持一致（[1/3]…[3/3]），
    阶段失败即中止后续阶段并给出非 0 退出码。
    """
    from .pipeline import ballistics, export_unity, reconstruction

    all_ok = True
    # None 表示"用算法自身的默认值"，与旧脚本不带参数时一致
    detect_kwargs = {}
    if imgsz is not None:
        detect_kwargs["imgsz"] = imgsz
    if conf is not None:
        detect_kwargs["conf"] = conf

    if not skip_reconstruct:
        print(f"[1/3] 3D 重建: {len(samples)} 个样本")
        try:
            reconstruction.run(samples, **detect_kwargs)
        except Exception as exc:
            print(f"  失败: {exc}")
            all_ok = False

    if not skip_ballistic and all_ok:
        print(f"[2/3] 弹道拟合: {len(samples)} 个样本")
        try:
            ballistics.run(samples)
        except Exception as exc:
            print(f"  失败: {exc}")
            all_ok = False

    if all_ok:
        print(f"[3/3] Unity 导出: {len(samples)} 个样本")
        try:
            export_unity.run(samples)
        except Exception as exc:
            print(f"  失败: {exc}")
            all_ok = False

    for name in samples:
        csv_path = ws.data / f"{name}_trajectory.csv"
        if not csv_path.is_file():
            print(f"  {name} 失败：未生成 Unity CSV: {csv_path}")
            all_ok = False
        else:
            print(f"  {name} 完成")

    return 0 if all_ok else 1


# --------------------------------------------------------------------------- 子命令
def cmd_offline(args) -> int:
    """离线处理：可选样本名，缺省时自动挑选含双路视频的样本目录。"""
    ws = resolve_workspace(args.workspace)
    if args.samples:
        samples = list(args.samples)
    elif ws.samples.is_dir():
        samples = [d.name for d in ws.samples.iterdir()
                   if d.is_dir() and has_camera_videos(d)]
    else:
        samples = []
    if not samples:
        print("没有可用样本")
        return 1
    print(f"离线处理 {len(samples)} 个样本")
    return _run_pipeline(ws, samples, args.skip_reconstruct, args.skip_ballistic,
                         imgsz=args.imgsz, conf=args.conf)


def cmd_online(args) -> int:
    """在线录制 + 立即离线处理该样本。"""
    ws = resolve_workspace(args.workspace)
    sample_dir = ws.samples / args.sample
    # 点了"在线采集"就是要录一段新的：只有显式要求复用素材时才跳过录制。
    # 以前只要目录里存在任意 .mp4（包括中断留下的几十字节坏文件）就静默
    # 跳过，于是相机根本没打开，界面上永远等不到画面。
    if args.reuse_existing and has_camera_videos(sample_dir):
        print(f"样本目录已存在视频，跳过录制: {sample_dir}")
    else:
        print(f"在线录制 -> {sample_dir}")
        clear_previous_recording(sample_dir)
        from .capture.dual_camera import DualCameraRecorder

        recorder = DualCameraRecorder(
            args.cam_left, args.cam_right, sample_dir,
            preview_dir=ws.live_preview, preview_size=(1280, 720))
        try:
            recorded = bool(recorder.start())
        except Exception as exc:
            print(f"录制失败: {exc}")
            return 1
        if not recorded:
            print("录制失败或取消")
            return 1

    return _run_pipeline(ws, [args.sample], False, False)


def cmd_online_live(args) -> int:
    """边录制边分析：实时叠加检测结果，停止后立即出完整结果。

    退出码沿用旧契约：旧实现是等子进程自然结束，预览取消也算 0；
    这里保持一致，只有真正的异常才返回非 0。
    """
    ws = resolve_workspace(args.workspace)
    print(f"边录制边分析 -> samples/{args.sample}")
    from .pipeline import live

    live.run(args.cam_left, args.cam_right, args.sample, args.imgsz, args.conf,
             args.analyze_every, args.no_save, preview_dir=str(ws.live_preview))
    return 0


def cmd_teststream(args) -> int:
    """测试网络视频流是否可打开并读到画面。"""
    resolve_workspace(args.workspace)
    from .capture.dual_camera import DualCameraRecorder

    ok, info = DualCameraRecorder.test_stream(args.source, args.timeout)
    print(info)
    return 0 if ok else 1


def cmd_list_cameras(args) -> int:
    """打印可用摄像头索引，逗号分隔（无摄像头时打印空行）。"""
    resolve_workspace(args.workspace)
    from .capture.dual_camera import DualCameraRecorder

    cameras = DualCameraRecorder.list_cameras()
    print(",".join(str(index) for index in cameras))
    return 0


def cmd_calibrate_intrinsics(args) -> int:
    """视频内参标定：左右各一段棋盘格视频。"""
    ws = resolve_workspace(args.workspace)
    from .calibration import intrinsics

    calib_dir = resolve_calib_dir(args.calib, ws)
    try:
        # resolve_input_path 保留旧的相对路径多基回退（cwd -> 工作区 -> 包目录）
        specs = [("left", intrinsics.resolve_input_path(args.left)),
                 ("right", intrinsics.resolve_input_path(args.right))]
        intrinsics.run(specs=specs, out=calib_dir)
    except Exception as exc:
        print(f"[错误] {exc}", file=sys.stderr)
        return 1
    return 0


def cmd_calibrate_extrinsics(args) -> int:
    """外参标定：射门视角左右各一张足球场照片 + 点击参考点。"""
    ws = resolve_workspace(args.workspace)
    from .calibration import extrinsics

    calib_dir = resolve_calib_dir(args.calib, ws)
    try:
        extrinsics.run(tasks=["left", "right"],
                       left_image=args.left_image,
                       right_image=args.right_image,
                       intrinsics_left=args.intrinsics_left,
                       intrinsics_right=args.intrinsics_right,
                       install_calib=str(calib_dir))
    except Exception as exc:
        print(f"[错误] {exc}", file=sys.stderr)
        return 1
    return 0


def _module_version(module_name):
    """取第三方库版本号；导入失败返回 None（自检不允许因此崩溃）。

    导入期间把库自身的 stdout 噪音改道到 stderr：selfcheck 的 stdout
    必须只有一行 JSON，供界面直接解析。
    """
    try:
        with contextlib.redirect_stdout(sys.stderr):
            module = importlib.import_module(module_name)
    except Exception:
        return None
    version = getattr(module, "__version__", None)
    if version is None:
        return None
    # torch 的本地构建标签（+cpu / +cu124）对界面判断能力没有意义，去掉
    return str(version).split("+", 1)[0]


def _cuda_available() -> bool:
    """CUDA 是否可用；torch 缺失时视为不可用。"""
    try:
        with contextlib.redirect_stdout(sys.stderr):
            import torch
        return bool(torch.cuda.is_available())
    except Exception:
        return False


def cmd_selfcheck(args) -> int:
    """打印一行 JSON 环境自检结果（缺哪个库就把对应字段设为 null）。"""
    ws = resolve_workspace(args.workspace)
    model_path = ws.yolo_model_path()
    payload = {
        "python": platform.python_version(),
        "torch": _module_version("torch"),
        "opencv": _module_version("cv2"),
        "cuda": _cuda_available(),
        "ultralytics": _module_version("ultralytics"),
        "model": str(model_path),
        "model_present": model_path.exists(),
    }
    print(json.dumps(payload, ensure_ascii=False))
    return 0


def default_unity_player(ws) -> Path:
    """发行目录里的 Unity 播放器路径（与 `installer` / WPF 侧一致）。"""
    return ws.root / "runtime" / "Myproject.exe"


def resolve_goalkeeper_json(ws, value):
    """解析评估用的门将 JSON：显式路径优先，否则回退到工作区默认门将。

    不靠名字查找：播放器的 `runtime/data/goalkeepers` 在发行目录里并不存在，
    按名字找会静默退回场景默认门将，评估结果就失去可比性。
    """
    if value:
        path = Path(value).expanduser()
        return path if path.is_absolute() else ws.root / path
    default = ws.data / "goalkeepers" / "GK_Elite.json"
    return default if default.is_file() else None


def _check_acceptance(report, expected, expected_direction=None) -> int:
    """把验收区间变成退出码：越界返回 1，让脚本/CI 能直接把它当门禁用。

    两个指标各有各的失败模式：扑救率过高说明门将仍然过强（观测降级没生效），
    方向正确率过高说明量化/噪声太弱（"只能分辨到格子"这条降级形同虚设），
    过低则说明观测模型已经把门将变成瞎子。任一项越界都算验收失败。
    """
    if report is None:
        return 0
    exit_code = 0
    if expected:
        low, high = expected
        rate = report.get("saveRate")
        if rate is not None:
            if low <= rate <= high:
                print(f"[验收] 扑救率 {rate:.1%} 落在 [{low:.0%}, {high:.0%}] ✓")
            else:
                print(f"[验收] 扑救率 {rate:.1%} 越界，期望 [{low:.0%}, {high:.0%}]", file=sys.stderr)
                exit_code = 1
    if expected_direction:
        low, high = expected_direction
        accuracy = report.get("directionAccuracy")
        if accuracy is None:
            print("[验收] 方向判断正确率 n/a（未启用观测降级），跳过")
        elif low <= accuracy <= high:
            print(f"[验收] 方向判断正确率 {accuracy:.1%} 落在 [{low:.0%}, {high:.0%}] ✓")
        else:
            print(f"[验收] 方向判断正确率 {accuracy:.1%} 越界，期望 [{low:.0%}, {high:.0%}]",
                  file=sys.stderr)
            exit_code = 1
    return exit_code


def cmd_evaluate(args) -> int:
    """生成覆盖球门的测试射门集，驱动 Unity 批量回放，输出端到端扑救率。

    这是"手感是否真实"的唯一客观口径（可行性报告 §2.3）：扑救率落在
    18%–30% 才算合理，高于 35% 说明门将仍然过强。
    """
    from . import evaluation

    ws = resolve_workspace(args.workspace)

    if args.summarize_only:
        results_path = Path(args.summarize_only)
        if not results_path.is_absolute():
            results_path = ws.root / results_path
        if not results_path.is_file():
            print(f"[错误] 找不到结果文件: {results_path}", file=sys.stderr)
            return 1
        report = evaluation.summarize(results_path)
        print(evaluation.format_report(report))
        return _check_acceptance(report, args.expect_save_rate, args.expect_direction)

    out_dir = Path(args.out) if args.out else Path("output") / "evaluation" / "latest"
    if not out_dir.is_absolute():
        out_dir = ws.root / out_dir

    unity_exe = None
    if not args.generate_only:
        unity_exe = Path(args.unity) if args.unity else default_unity_player(ws)
        if not Path(unity_exe).is_absolute():
            unity_exe = ws.root / unity_exe
        if not Path(unity_exe).is_file():
            print(f"[错误] 找不到 Unity 播放器: {unity_exe}", file=sys.stderr)
            print("       先构建播放器（Myproject 的 Build/Build Windows EXE），"
                  "或用 --generate-only 只生成射门集。", file=sys.stderr)
            return 1

    report, code = evaluation.run_evaluation(
        out_dir,
        unity_exe=unity_exe,
        count=args.shots,
        seed=args.seed,
        columns=args.columns,
        rows=args.rows,
        speed_min=args.speed_min,
        speed_max=args.speed_max,
        jitter_ratio=args.jitter,
        distribution=args.shot_distribution,
        observation=(args.observation == "on"),
        reaction_delay=args.reaction_delay,
        confidence=args.confidence,
        noise=args.noise,
        goalkeeper=args.goalkeeper,
        goalkeeper_path=resolve_goalkeeper_json(ws, args.goalkeeper_json),
        timeout=args.timeout,
    )

    print(f"轨迹与清单: {out_dir}")
    if report is None:
        print("已生成射门集，未运行 Unity。")
        return code
    print(evaluation.format_report(report))
    acceptance = _check_acceptance(report, args.expect_save_rate, args.expect_direction)
    if acceptance != 0:
        return acceptance
    if code != 0:
        print(f"[警告] Unity 播放器退出码 {code}", file=sys.stderr)
    return code


# --------------------------------------------------------------------------- C2 姿态方向
def _pose_dataset_root(ws, value) -> Path:
    """解析 3DSP 根目录（默认 data/external/3dsp/3dsp）。"""
    if value:
        path = Path(value).expanduser()
        return path if path.is_absolute() else ws.root / path
    return ws.data / "external" / "3dsp" / "3dsp"


def _pose_output_dir(ws, value) -> Path:
    if value:
        path = Path(value).expanduser()
        return path if path.is_absolute() else ws.root / path
    return ws.output / "pose"


def _pose_experiment_config(args):
    """把 CLI 参数映射成 ExperimentConfig（torch 相关模块在调用处才导入）。"""
    from .pose.experiment import ExperimentConfig

    return ExperimentConfig(
        seed=args.seed,
        variants_per_source=args.variants,
        epochs=args.epochs,
        folds=args.folds,
        transfer_epochs=args.transfer_epochs,
        augment_copies=args.augment,
        max_sources=args.max_sources,
        permutation_trials=args.permutation_trials,
        threads=args.threads,
        train_ratio=args.train_ratio,
    )


def cmd_pose_inspect(args) -> int:
    """打印 3DSP 的数据勘察摘要（不加载 torch）。"""
    from .pose.dataset import load_split, summarize

    ws = get_workspace()
    root = _pose_dataset_root(ws, args.dataset)
    shots = load_split(root, args.split)
    summary = summarize(shots)
    summary["dataset_root"] = str(root)
    summary["split"] = args.split
    print(json.dumps(summary, indent=2, ensure_ascii=False))
    return 0 if shots else 1


def cmd_pose_train(args) -> int:
    """只训练组 1 的方向模型并保存 checkpoint（供后续迁移/部署复用）。"""
    from .pose.dataset import load_train
    from .pose.experiment import split_sources
    from .pose.synthetic import DeformConfig, RandomizationConfig, build_synthetic_dataset
    from .pose.train import TrainConfig, save_checkpoint, train_direction_model

    ws = get_workspace()
    root = _pose_dataset_root(ws, args.dataset)
    output_dir = _pose_output_dir(ws, args.out)
    output_dir.mkdir(parents=True, exist_ok=True)

    shots = load_train(root)
    if args.max_sources:
        shots = shots[: args.max_sources]
    train_shots, val_shots = split_sources(shots, args.train_ratio, args.seed)
    deform = DeformConfig(variants_per_source=args.variants)
    randomization = RandomizationConfig()
    train_samples = build_synthetic_dataset(
        train_shots, seed=args.seed, deform=deform, randomization=randomization
    )
    val_samples = build_synthetic_dataset(
        val_shots, seed=args.seed + 977, deform=deform, randomization=randomization
    )

    config = TrainConfig(
        epochs=args.epochs, batch_size=args.batch_size, seed=args.seed, threads=args.threads
    )
    print(
        f"训练方向模型：源 {len(train_shots)}/{len(val_shots)}，"
        f"样本 {len(train_samples)}/{len(val_samples)}",
        flush=True,
    )
    result = train_direction_model(train_samples, val_samples, config, verbose=True)

    from .pose.evaluate import evaluate_at_deltas

    metrics = evaluate_at_deltas(result.model, val_samples, config.deltas_ms, result.temperature)
    checkpoint = output_dir / "pose-direction.pt"
    save_checkpoint(
        checkpoint,
        result,
        config,
        extra={
            "groups": {"train_sources": len(train_shots), "val_sources": len(val_shots)},
            "deltas": [item.to_dict() for item in metrics],
        },
    )
    print(f"已保存 {checkpoint}（参数量 {result.parameter_count:,}，温度 {result.temperature:.3f}）")
    for item in metrics:
        print(
            f"  Δt={item.delta_ms:>3.0f} ms 准确率={item.accuracy:.3f} "
            f"宏F1={item.macro_f1:.3f} ECE={item.ece:.3f}"
        )
    return 0


def cmd_pose_run(args) -> int:
    """跑完 C2 的三组实验，写出 JSON 与 markdown 报告。"""
    from .pose.experiment import run_all, write_outputs

    ws = get_workspace()
    root = _pose_dataset_root(ws, args.dataset)
    output_dir = _pose_output_dir(ws, args.out)
    config = _pose_experiment_config(args)

    results = run_all(root, config, progress=lambda message: print(message, flush=True))
    paths = write_outputs(results, output_dir)
    decision = results.get("decision", {})

    print(json.dumps(decision, indent=2, ensure_ascii=False))
    print(f"报告：{paths['report']}")
    print(f"原始结果：{paths['json']}")

    verdict = decision.get("verdict")
    if args.fail_on_no_go and verdict == "no-go":
        print("[验收] C2 判定为 no-go", file=sys.stderr)
        return 1
    return 0


# --------------------------------------------------------------------------- 参数解析
def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="aifootball",
        description="AIfootball Engine —— 足球三维轨迹重建引擎")
    parser.add_argument("--version", action="version", version=f"aifootball {__version__}")
    sub = parser.add_subparsers(dest="command")

    def add_workspace(p):
        p.add_argument("--workspace", default=None,
                       help="工作区目录（默认: AIFOOTBALL_WORKSPACE 环境变量，其次当前目录）")

    off = sub.add_parser("offline", help="离线处理")
    off.add_argument("--samples", nargs="*")
    off.add_argument("--skip-reconstruct", action="store_true")
    off.add_argument("--skip-ballistic", action="store_true")
    off.add_argument("--imgsz", type=int, default=None, help="检测输入尺寸（默认由算法常量决定）")
    off.add_argument("--conf", type=float, default=None, help="检测置信度阈值（默认由算法常量决定）")
    add_workspace(off)
    off.set_defaults(handler=cmd_offline)

    onl = sub.add_parser("online", help="在线录制并处理")
    onl.add_argument("--cam-left", default="0", help="左相机索引或 RTSP/HTTP URL")
    onl.add_argument("--cam-right", default="1", help="右相机索引或 RTSP/HTTP URL")
    onl.add_argument("--sample", default="sample_live", help="本次采集的样本名称")
    onl.add_argument("--reuse-existing", action="store_true",
                     help="样本目录已有视频时跳过录制，直接处理现有素材")
    add_workspace(onl)
    onl.set_defaults(handler=cmd_online)

    liv = sub.add_parser("online-live", help="边录制边分析（实时检测叠加，停止后立即出结果）")
    liv.add_argument("--cam-left", default="0", help="左相机索引或 RTSP/HTTP URL")
    liv.add_argument("--cam-right", default="1", help="右相机索引或 RTSP/HTTP URL")
    liv.add_argument("--sample", default="sample_live", help="本次采集的样本名称")
    liv.add_argument("--imgsz", type=int, default=1280, help="检测输入尺寸")
    liv.add_argument("--conf", type=float, default=0.15, help="检测置信度阈值")
    liv.add_argument("--analyze-every", type=int, default=1, help="每隔 N 帧分析一次")
    liv.add_argument("--no-save", action="store_true", help="不落盘 mp4")
    add_workspace(liv)
    liv.set_defaults(handler=cmd_online_live)

    tst = sub.add_parser("teststream", help="测试网络视频流连接")
    tst.add_argument("--source", required=True,
                     help="RTSP/HTTP 视频流地址，如 rtsp://192.168.1.5:8554/... 或 http://192.168.1.5:8080/video")
    tst.add_argument("--timeout", type=int, default=8, help="连接超时秒数")
    add_workspace(tst)
    tst.set_defaults(handler=cmd_teststream)

    cam = sub.add_parser("list-cameras", help="列出可用摄像头索引（逗号分隔）")
    add_workspace(cam)
    cam.set_defaults(handler=cmd_list_cameras)

    cal = sub.add_parser("calibrate-intrinsics", help="视频内参标定：左右各用一段棋盘格视频")
    cal.add_argument("--left", required=True, help="左相机棋盘格视频文件")
    cal.add_argument("--right", required=True, help="右相机棋盘格视频文件")
    cal.add_argument("--calib", default="calib", help="标定输出目录（默认 calib）")
    add_workspace(cal)
    cal.set_defaults(handler=cmd_calibrate_intrinsics)

    cex = sub.add_parser("calibrate-extrinsics",
                         help="外参标定：射门视角左右各一张足球场照片 + 点击参考点")
    cex.add_argument("--left-image", dest="left_image", required=True, help="射门视角左侧参考照片（左相机）")
    cex.add_argument("--right-image", dest="right_image", required=True, help="射门视角右侧参考照片（右相机）")
    cex.add_argument("--intrinsics-left", dest="intrinsics_left", default=None, help="左内参 npz 路径")
    cex.add_argument("--intrinsics-right", dest="intrinsics_right", default=None, help="右内参 npz 路径")
    cex.add_argument("--calib", default="calib", help="标定输出目录（默认 calib）")
    add_workspace(cex)
    cex.set_defaults(handler=cmd_calibrate_extrinsics)

    chk = sub.add_parser("selfcheck", help="打印一行 JSON 环境自检结果")
    add_workspace(chk)
    chk.set_defaults(handler=cmd_selfcheck)

    ev = sub.add_parser("evaluate", help="生成测试射门集并统计端到端扑救率（可驱动 Unity 批量回放）")
    ev.add_argument("--shots", type=int, default=27, help="射门条数（默认 27：3×3 网格各 3 次）")
    ev.add_argument("--seed", type=int, default=20261019, help="随机种子（落点抖动与球速）")
    ev.add_argument("--columns", type=int, default=3, help="横向分格数")
    ev.add_argument("--rows", type=int, default=3, help="纵向分格数")
    ev.add_argument("--speed-min", dest="speed_min", type=float, default=18.0, help="球速下限（m/s）")
    ev.add_argument("--speed-max", dest="speed_max", type=float, default=26.0, help="球速上限（m/s）")
    ev.add_argument("--observation", choices=("on", "off"), default="on",
                    help="是否启用观测降级（off = 门将拿真值，用于对照）")
    ev.add_argument("--reaction-delay", dest="reaction_delay", type=float, default=0.18,
                    help="起扑反应延迟（秒）")
    ev.add_argument("--confidence", type=float, default=0.66, help="观测先验置信度 0–1")
    ev.add_argument("--noise", type=float, default=0.80, help="观测噪声基准（米，高斯 σ）")
    ev.add_argument("--jitter", type=float, default=0.95,
                    help="落点在格内的抖动幅度（以半格为单位；1.0 ≈ 格内均匀）")
    ev.add_argument("--shot-distribution", dest="shot_distribution",
                    choices=("penalty", "uniform"), default="penalty",
                    help="多出的样本落在哪些格：penalty 按真实点球落点分布加权，"
                         "uniform 逐格均匀铺开（覆盖性检查用）")
    ev.add_argument("--goalkeeper", default=None, help="门将名（data/goalkeepers/*.json 的 DisplayName）")
    ev.add_argument("--goalkeeper-json", dest="goalkeeper_json", default=None,
                    help="门将 JSON 路径（默认 data/goalkeepers/GK_Elite.json）")
    ev.add_argument("--unity", default=None, help="Unity 播放器路径（默认 runtime/Myproject.exe）")
    ev.add_argument("--out", default=None, help="输出目录（默认 output/evaluation/latest）")
    ev.add_argument("--timeout", type=float, default=1800.0, help="Unity 批处理超时（秒）")
    ev.add_argument("--generate-only", dest="generate_only", action="store_true",
                    help="只生成轨迹与清单，不运行 Unity")
    ev.add_argument("--summarize-only", dest="summarize_only", default=None,
                    help="只汇总已有的 results.jsonl")
    ev.add_argument("--expect-save-rate", dest="expect_save_rate", type=float, nargs=2, default=None,
                    metavar=("MIN", "MAX"),
                    help="验收门禁：扑救率必须落在 [MIN, MAX]（可行性报告 §2.3 为 0.18 0.30）；"
                         "越界时退出码 1，可直接用作 CI 判据")
    ev.add_argument("--expect-direction", dest="expect_direction", type=float, nargs=2, default=None,
                    metavar=("MIN", "MAX"),
                    help="验收门禁：方向判断正确率（横向左/中/右）必须落在 [MIN, MAX]"
                         "（可行性报告 §2.3 为 0.55 0.75）；越界时退出码 1")
    add_workspace(ev)
    ev.set_defaults(handler=cmd_evaluate)

    pose = sub.add_parser("pose", help="C2：现有视角姿态 → 射门方向预测基线")
    pose_sub = pose.add_subparsers(dest="pose_command")

    def add_pose_common(p):
        p.add_argument("--dataset", default=None,
                       help="3DSP 根目录（默认 data/external/3dsp/3dsp）")
        p.add_argument("--seed", type=int, default=20261019, help="随机种子")
        p.add_argument("--variants", type=int, default=6,
                       help="每个源 shot × 每个方向生成多少变体（默认 6）")
        p.add_argument("--epochs", type=int, default=25, help="组 1 训练轮数（默认 25）")
        p.add_argument("--batch-size", dest="batch_size", type=int, default=128)
        p.add_argument("--train-ratio", dest="train_ratio", type=float, default=0.8,
                       help="按源 shot 划分的训练比例")
        p.add_argument("--max-sources", dest="max_sources", type=int, default=None,
                       help="只用前 N 个源 shot（冒烟测试用）")
        p.add_argument("--threads", type=int, default=6, help="torch CPU 线程数")
        add_workspace(p)

    pin = pose_sub.add_parser("inspect", help="打印 3DSP 数据勘察摘要")
    pin.add_argument("--dataset", default=None, help="3DSP 根目录（默认 data/external/3dsp/3dsp）")
    pin.add_argument("--split", choices=("train", "test"), default="train")
    add_workspace(pin)
    pin.set_defaults(handler=cmd_pose_inspect)

    ptr = pose_sub.add_parser("train", help="训练方向模型并保存 checkpoint")
    add_pose_common(ptr)
    ptr.add_argument("--out", default=None, help="输出目录（默认 output/pose）")
    ptr.set_defaults(handler=cmd_pose_train)

    prn = pose_sub.add_parser("run", help="跑完组 1 / 1b / 2 并生成报告")
    add_pose_common(prn)
    prn.add_argument("--out", default=None, help="输出目录（默认 output/pose）")
    prn.add_argument("--folds", type=int, default=5, help="组 2 交叉验证折数")
    prn.add_argument("--transfer-epochs", dest="transfer_epochs", type=int, default=30,
                     help="组 2 训练轮数")
    prn.add_argument("--augment", type=int, default=4,
                     help="组 2 每个真实样本的域随机化副本数（含原样）")
    prn.add_argument("--permutation-trials", dest="permutation_trials", type=int, default=2,
                     help="标签置换检验重复次数（0 = 跳过；每次成本约等于一遍组 2）")
    prn.add_argument("--fail-on-no-go", dest="fail_on_no_go", action="store_true",
                     help="判定为 no-go 时退出码 1，可直接当 CI 门禁")
    prn.set_defaults(handler=cmd_pose_run)

    return parser


def main(argv=None) -> int:
    """CLI 主入口：成功返回 0，失败返回非 0。"""
    parser = build_parser()
    args = parser.parse_args(argv)
    if not getattr(args, "command", None):
        parser.print_help()
        return 1
    try:
        return args.handler(args)
    except KeyboardInterrupt:
        print("已中断", file=sys.stderr)
        return 130
    except Exception as exc:
        print(f"[错误] {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
