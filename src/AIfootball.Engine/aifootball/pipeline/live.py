"""
边录制边分析 —— 实时双摄检测 + 叠加显示 + 停止后立即出完整结果。

原理：
- 录制的同时，对左右两路画面做实时足球检测（复用成熟离线检测的
  _adaptive_ball_detect：基于上一帧位置预测的多尺度裁剪检测）。
- 实时在预览窗口叠加：检测框、置信度、实时 3D 轨迹点（需标定）。
- 停止后直接用录制过程中已累积的两路 2D 检测结果重建 3D 轨迹 +
  弹道拟合 + Unity 导出，不需要重跑整段视频检测（秒出结果）。
- 录制文件分别落盘到 left/recording.mp4、right/recording.mp4，可随时重跑离线流程。

用法（由 __main__.py online-live 子命令调用）:
  python live_analysis.py --cam-left <L> --cam-right <R> --sample <name>
                          [--imgsz 1280] [--conf 0.15] [--analyze-every 1] [--no-save]
"""
import argparse
import os
import queue
import sys
import threading
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np

from ..capture.dual_camera import DualCameraRecorder
from ..control import ControlChannel
from ..preview import PreviewPublisher, fit_scale
from ..workspace import get_workspace, set_workspace
from .reconstruction import (
    SPORTS_BALL_CLASS_ID,
    PENALTY_SPOT_GROUND_WORLD,
    VideoTrack,
    load_camera_configs,
    pick_detection,
    ConstantAccelerationFilter,
    _predict_ball_position,
    _adaptive_ball_detect,
    project_world_points,
    image_point_to_ground_world,
    triangulate_ball_point,
    estimate_kick_frame,
    build_3d_trajectory,
    write_csv,
    write_raw_csv,
    save_summary,
    render_trajectory_plot,
)


def _frame_timestamp(cap, frame_index, fps):
    """单路帧时间戳：优先容器/流内 POS_MSEC，否则帧号/帧率。

    原实现用挂钟时间 time.time()-t0 给帧打戳，而检测发生在 read 之后：
    CPU 跟不上时 read 返回的是缓冲区里的旧帧，挂钟把"检测耗时"记到了
    帧上，时间轴出现与负载相关的抖动，直接污染起脚判定与实时预测。
    容器时间戳（或帧号推算）与帧内容一一对应，总是单调、均匀的。
    """
    try:
        msec = cap.get(cv2.CAP_PROP_POS_MSEC)
    except cv2.error:
        msec = 0.0
    if msec and np.isfinite(msec) and msec > 0:
        return float(msec) / 1000.0
    return frame_index / max(float(fps), 1e-6)


def _match_pair(pending_left, pending_right, max_gap):
    """在两路待配对队列中找时间最接近的一对检测；超过 max_gap 视为无对应帧。

    两路时间戳独立后，同一 while 轮次读到的左右帧不再保证对应同一物理
    时刻，实时三角测量必须按时间就近配对而不是按读取轮次配对。
    """
    if not pending_left or not pending_right:
        return None
    best_gap = None
    best = (0, 0)
    for li, (t_l, _) in enumerate(pending_left):
        for ri, (t_r, _) in enumerate(pending_right):
            gap = abs(t_l - t_r)
            if best_gap is None or gap < best_gap:
                best_gap = gap
                best = (li, ri)
    if best_gap is None or best_gap > max_gap:
        return None
    li, ri = best
    return pending_left.pop(li), pending_right.pop(ri)


class _CamTracker:
    """单路实时检测状态：卡尔曼预测 + 累积 2D 检测点"""

    def __init__(self, config):
        self.config = config                      # CameraConfig 或 None
        self.history = deque(maxlen=8)
        self.miss = 0
        self.frame_w = 0
        self.frame_h = 0
        self.penalty_center = None                # 罚球点投影（需标定）
        self.filter = ConstantAccelerationFilter()
        self.filter_interval = 1.0 / 60.0
        self.times = []
        self.points = []                          # 2D 中心
        self.foots = []
        self.confs = []
        self.center_covariances = []
        self.ground_points = []                   # 地面投影（需标定）

    def reset_geometry(self, frame_w, frame_h):
        self.frame_w, self.frame_h = frame_w, frame_h
        if self.config is not None:
            self.penalty_center = project_world_points(
                PENALTY_SPOT_GROUND_WORLD.reshape(1, 3), self.config)[0]

    def detect(self, model, frame, imgsz, conf):
        """检测本帧足球；返回 chosen 或 None，并更新卡尔曼状态"""
        if self.filter.initialized:
            predicted, vel = self.filter.predict_next(self.filter_interval)
        else:
            predicted, vel = _predict_ball_position(self.history)
        if predicted is None:
            predicted = self.penalty_center if self.penalty_center is not None \
                else np.array([frame.shape[1] / 2.0, frame.shape[0] / 2.0])

        chosen = _adaptive_ball_detect(
            model, frame, predicted, self.penalty_center, imgsz, conf,
            self.frame_w, self.frame_h, velocity_mag=vel)

        # 裁剪检测失败时回退到全帧 YOLO（兜底只做粗分辨率）
        if chosen is None and self.penalty_center is not None:
            result = model.predict(frame, classes=[SPORTS_BALL_CLASS_ID],
                                   conf=conf, imgsz=min(imgsz, 640), verbose=False)[0]
            boxes = [] if result.boxes is None else list(result.boxes)
            chosen = pick_detection(boxes, predicted, self.penalty_center)

        if chosen is not None:
            self.history.append(chosen["center"].copy())
            self.miss = 0
            self.filter.update(
                chosen["center"],
                chosen.get("center_covariance"),
                self.filter_interval,
            )
        else:
            self.miss += 1
            if self.miss > 5:
                self.history.clear()
                self.miss = 0
                self.filter.reset()
        return chosen

    def record(self, t, chosen):
        """累积一个检测点（时间戳秒、中心、置信度、地面投影）"""
        if chosen is None:
            return
        self.times.append(float(t))
        self.points.append(chosen["center"].copy())
        self.foots.append(chosen["foot"].copy())
        self.confs.append(float(chosen["conf"]))
        self.center_covariances.append(np.asarray(
            chosen.get("center_covariance", np.eye(2, dtype=np.float64) * 9.0),
            dtype=np.float64,
        ).reshape(2, 2).copy())
        if self.config is not None:
            gp = image_point_to_ground_world(chosen["foot"], self.config, ground_z=0.0)
            self.ground_points.append(None if gp is None else gp.copy())
        else:
            self.ground_points.append(None)

    def to_detections(self):
        """构造与离线 estimate_kick_frame 兼容的 detections 列表"""
        return [
            {"ground_point": self.ground_points[i], "frame_idx": i}
            for i in range(len(self.times))
        ]

    def track_after_kick(self, kick_frame):
        """剔除起脚前的点（球静止在罚球点附近），返回新时间/点/置信度"""
        times, points, confs, covariances = [], [], [], []
        for i in range(kick_frame, len(self.times)):
            times.append(self.times[i])
            points.append(self.points[i])
            confs.append(self.confs[i])
            covariances.append(self.center_covariances[i])
        return times, points, confs, covariances


def _draw_ball(frame, chosen, color=(0, 255, 0)):
    if chosen is None:
        return
    x1, y1, x2, y2 = [int(v) for v in chosen["bbox"]]
    cv2.rectangle(frame, (x1, y1), (x2, y2), color, 2, cv2.LINE_AA)
    cx, cy = int(chosen["center"][0]), int(chosen["center"][1])
    cv2.circle(frame, (cx, cy), 4, color, -1, cv2.LINE_AA)
    cv2.putText(frame, f"{chosen['conf']:.2f}", (x1, max(12, y1 - 6)),
                cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1, cv2.LINE_AA)


def _draw_world_trace(frame, world_points, config, color=(0, 200, 255)):
    """把世界系轨迹点投影回图像并连线"""
    if world_points is None or len(world_points) < 1 or config is None:
        return
    img_pts = project_world_points(np.asarray(world_points, dtype=np.float64), config)
    pts = [(int(p[0]), int(p[1])) for p in img_pts]
    if len(pts) >= 2:
        cv2.polylines(frame, [np.asarray(pts, dtype=np.int32)], False, color, 2, cv2.LINE_AA)
    for p in pts:
        cv2.circle(frame, p, 3, color, -1, cv2.LINE_AA)


def _estimate_speed(world_buf):
    if len(world_buf) >= 2:
        (t1, p1), (t2, p2) = world_buf[-2], world_buf[-1]
        dt = t2 - t1
        if dt > 1e-4:
            return float(np.linalg.norm(p2 - p1) / dt)
    return 0.0


def _fit_letterbox(frame, size):
    """按比例缩放并居中放进固定尺寸画布。

    手机流经常是 2448x1080 这类超宽画面，直接 resize 到 1280x720 会明显
    拉伸变形；这里保持比例、其余补黑边。
    """
    width, height = int(size[0]), int(size[1])
    source_h, source_w = frame.shape[:2]
    if source_h <= 0 or source_w <= 0:
        return np.zeros((height, width, 3), dtype=np.uint8)
    scale = min(width / source_w, height / source_h)
    new_w = max(1, int(round(source_w * scale)))
    new_h = max(1, int(round(source_h * scale)))
    resized = cv2.resize(frame, (new_w, new_h))
    if new_w == width and new_h == height:
        return resized
    canvas = np.zeros((height, width, 3), dtype=np.uint8)
    offset_x = (width - new_w) // 2
    offset_y = (height - new_h) // 2
    canvas[offset_y:offset_y + new_h, offset_x:offset_x + new_w] = resized
    return canvas


def _publish_frame(frame, preview_dir, side, preview_size=(1280, 720)):
    """把一帧写成 JPEG 供界面显示（无发布线程时的同步回退路径）。

    等比缩放而不是套固定画幅：预览要用来判断取景，保持原始比例。
    """
    if not preview_dir or frame is None or frame.size == 0:
        return
    try:
        pd = Path(preview_dir)
        pd.mkdir(parents=True, exist_ok=True)
        fitted = fit_scale(frame, preview_size)
        path = pd / f"live_preview_{side}.jpg"
        tmp = path.with_suffix(".tmp.jpg")
        if cv2.imwrite(str(tmp), fitted, [int(cv2.IMWRITE_JPEG_QUALITY), 80]):
            os.replace(str(tmp), str(path))
    except Exception:
        pass


def _preview_panel(left, right, names, caps, size=(640, 360)):
    """把预览画面拼成一张面板；还没有画面的一路用黑底占位。"""
    panels = []
    for idx, frame in ((0, left), (1, right)):
        if frame is None:
            tile = np.zeros((size[1], size[0], 3), dtype=np.uint8)
            # cv2.putText 不支持中文，这里用英文避免出现方块
            label = f"{names[idx]}: waiting" if caps[idx] is None else f"{names[idx]}: no signal"
            cv2.putText(tile, label, (16, size[1] // 2), cv2.FONT_HERSHEY_SIMPLEX,
                        0.6, (110, 110, 110), 1, cv2.LINE_AA)
        else:
            tile = _fit_letterbox(frame, size)
        panels.append(tile)
    return np.hstack(panels)


def _preview_until_start(rec, caps, names, sources, publisher, control,
                         retry_interval=3.0):
    """预览阶段：只发布画面，直到可以开始分析。

    返回 True 表示两路都就绪且用户要求开始分析；返回 False 表示用户放弃。
    某一路没有画面时不阻塞另一路，并持续在后台重试取流，用户补上推流后
    不需要重开界面。
    """
    show_window = control.interactive
    stop_retry = threading.Event()
    retry_results = queue.Queue()

    def _retry(idx):
        while not stop_retry.is_set() and caps[idx] is None:
            if stop_retry.wait(retry_interval):
                return
            if caps[idx] is not None:
                return
            try:
                retry_results.put((idx, rec._open_camera(sources[idx], names[idx], idx), None))
                return
            except Exception as exc:
                retry_results.put((idx, None, str(exc)))

    for idx in (0, 1):
        if caps[idx] is None:
            threading.Thread(target=_retry, args=(idx,), daemon=True,
                             name=f"retry-{names[idx]}").start()

    hint = "（或在本窗口按 ENTER）" if show_window else ""
    print(f"\n预览中：确认画面后点界面「开始分析」{hint} ...")
    next_publish = 0.0
    try:
        while True:
            while True:
                try:
                    idx, cap, error = retry_results.get_nowait()
                except queue.Empty:
                    break
                if cap is not None:
                    caps[idx] = cap
                    print(f"  {names[idx]}路已就绪")
                elif error:
                    print(f"  {names[idx]}路仍不可用: {error}")

            tiles = {}
            for idx in (0, 1):
                cap = caps[idx]
                if cap is None:
                    continue
                ret, frame = cap.read()
                if ret:
                    tiles[idx] = frame
            if not tiles:
                time.sleep(0.05)

            now = time.monotonic()
            if tiles and now >= next_publish:
                for idx, frame in tiles.items():
                    publisher.submit("left" if idx == 0 else "right", frame)
                next_publish = now + 0.05

            if show_window:
                panel = _preview_panel(tiles.get(0), tiles.get(1), names, caps)
                cv2.imshow("AIfootball 预览 (ENTER 开始分析 / Q 退出)", panel)

            commands = control.poll()
            if "stop" in commands:
                return False
            if control.closed and not commands:
                # 界面已经退出：立刻结束，别把 UDP 端口留着继续抢包
                print("控制通道已关闭（界面已退出），结束预览")
                return False
            key = cv2.waitKey(1) & 0xFF if show_window else 0xFF
            if key == 13:
                commands.append("start")
            elif key in (ord("q"), ord("Q"), 27):
                return False
            if "start" in commands:
                missing = [names[i] for i in (0, 1) if caps[i] is None]
                if missing:
                    print(f"暂时无法开始分析：{'、'.join(missing)}路还没有画面。"
                          "请确认这一路推流已开始；画面出现后再点一次「开始分析」。")
                    continue
                return True
    finally:
        stop_retry.set()


def _load_detector():
    """加载 YOLO 检测模型（只在真正开始分析时调用，避免预览前干等）。"""
    from ultralytics import YOLO
    model_path = str(get_workspace().yolo_model_path())
    if not Path(model_path).exists():
        print(f"YOLO 模型不存在，将自动下载: {model_path}")
    model = YOLO(model_path)

    # 预热：仅 GPU 需要编译 CUDA kernel，减少首帧延迟
    try:
        import torch
        if torch.cuda.is_available():
            model.predict(np.zeros((640, 640, 3), dtype=np.uint8),
                          classes=[SPORTS_BALL_CLASS_ID], imgsz=320, conf=0.25,
                          verbose=False)
    except Exception:
        pass
    return model


def finish_reconstruction(sample_name, sample_dir, tracker_l, tracker_r,
                          left_cfg, right_cfg, model, imgsz, conf,
                          saved_ok=True, fallback=True):
    """停止后用已累积检测结果直接重建 + 弹道 + 导出；不足时回退离线重跑。"""
    from .ballistics import process_sample as ballistic_sample
    from .export_unity import convert_sample

    nl = len(tracker_l.times)
    nr = len(tracker_r.times)
    print(f"\n实时检测统计: 左 {nl} 点, 右 {nr} 点")

    # 起脚帧剔除（需标定/地面投影）
    kick = 0
    if left_cfg is not None and nl >= 4 and nr >= 4:
        try:
            kl = estimate_kick_frame(tracker_l.to_detections())
            kr = estimate_kick_frame(tracker_r.to_detections())
            kick = max(kl, kr)
        except Exception:
            kick = 0

    def make_track(tracker, cam_name, kick_frame):
        times, points, confs, covariances = tracker.track_after_kick(kick_frame)
        video_path = (sample_dir / cam_name / "recording.mp4") if saved_ok else sample_dir / f"live_{cam_name}.mp4"
        return VideoTrack(
            video_path=video_path,
            camera_name=cam_name,
            fps=tracker.fps,
            frame_count=len(tracker.times),
            frame_size=(tracker.frame_w, tracker.frame_h),
            kick_frame=kick_frame,
            detections=[],
            times=np.asarray(times, dtype=np.float64),
            image_points=np.asarray(points, dtype=np.float64),
            confidences=np.asarray(confs, dtype=np.float64),
            center_covariances=np.asarray(covariances, dtype=np.float64),
            path_diagnostics={
                "enabled": False,
                "applied": False,
                "reason": "live stream uses its causal detector path",
            },
        )

    if left_cfg is None or right_cfg is None:
        print("缺少相机标定，无法进行 3D 重建，跳过（仅完成录制）。")
        return False

    if nl - kick < 8 or nr - kick < 8:
        if fallback:
            print(f"实时检测点不足（左 {nl}, 右 {nr}），回退到离线重跑检测...")
            from .reconstruction import process_sample as recon_full
            configs = [left_cfg, right_cfg]
            try:
                recon_full(sample_dir, configs, model, imgsz=imgsz, conf=conf)
                ballistic_sample(sample_name)
                convert_sample(sample_name)
                return True
            except Exception as e:
                print(f"离线回退失败: {e}")
                return False
        return False

    left_track = make_track(tracker_l, "left", kick)
    right_track = make_track(tracker_r, "right", kick)
    print(f"构造轨迹: 左 {len(left_track.times)} 点, 右 {len(right_track.times)} 点")

    try:
        trajectory = build_3d_trajectory(left_track, left_cfg, right_track, right_cfg)
    except Exception as e:
        print(f"3D 重建失败: {e}")
        if fallback:
            print("回退到离线重跑检测...")
            from .reconstruction import process_sample as recon_full
            try:
                recon_full(sample_dir, [left_cfg, right_cfg], model, imgsz=imgsz, conf=conf)
                ballistic_sample(sample_name)
                convert_sample(sample_name)
                return True
            except Exception as e2:
                print(f"离线回退失败: {e2}")
        return False

    out = get_workspace().trajectory_3d / sample_name
    out.mkdir(parents=True, exist_ok=True)
    csv_path = out / "trajectory_3d_points.csv"
    raw_csv_path = out / "trajectory_3d_points_raw.csv"
    npz_path = out / "trajectory_3d_points.npz"
    fig_path = out / f"{sample_name}_trajectory_3d.png"
    write_csv(csv_path, trajectory)
    write_raw_csv(raw_csv_path, trajectory)
    np.savez(
        npz_path,
        raw_times=trajectory.raw_times,
        raw_world_points=trajectory.raw_world_points,
        raw_ray_gaps=trajectory.raw_ray_gaps,
        raw_reprojection_errors=trajectory.raw_reprojection_errors,
        raw_keep_mask=trajectory.raw_keep_mask.astype(np.uint8),
        raw_image_points_left=trajectory.raw_image_points_left,
        raw_image_points_right=trajectory.raw_image_points_right,
        raw_confidences_left=trajectory.raw_confidences_left,
        raw_confidences_right=trajectory.raw_confidences_right,
        times=trajectory.times,
        world_points=trajectory.world_points,
        ray_gaps=trajectory.ray_gaps,
        reprojection_errors=trajectory.reprojection_errors,
        image_points_left=trajectory.image_points_left,
        image_points_right=trajectory.image_points_right,
        confidences_left=trajectory.confidences_left,
        confidences_right=trajectory.confidences_right,
        offset_seconds=np.array([trajectory.offset_seconds], dtype=np.float64),
    )
    render_trajectory_plot(sample_name, trajectory, [left_cfg, right_cfg], fig_path)
    save_summary(sample_dir, [left_track, right_track], trajectory, out)

    print(f"[{sample_name}] 实时重建 Raw 3D 点数: {len(trajectory.raw_times)}")
    print(f"[{sample_name}] 实时重建 过滤后点数: {len(trajectory.times)}")
    print(f"[{sample_name}] Stereo time offset: {trajectory.offset_seconds:+.4f} s")
    print(f"[{sample_name}] Max height: {np.max(trajectory.world_points[:, 2]):.3f} m")
    print(f"[{sample_name}] Saved: {csv_path}")

    # 弹道拟合 + Unity 导出（读上面写的 npz）
    try:
        ballistic_sample(sample_name)
        convert_sample(sample_name)
        return True
    except Exception as e:
        print(f"弹道/导出失败: {e}")
        return False


def run(cam_left="0", cam_right="1", sample_name="sample_live",
        imgsz=1280, conf=0.15, analyze_every=1, no_save=False, preview_dir=None,
        preview_size=(1280, 720), auto_start=False, auto_stop=None, workspace=None):
    """预览 -> 开始分析 -> 停止后立即出结果。

    1. 先打开左右两路进入预览：只发布画面，不写视频、不加载检测模型，
       某一路没有画面不影响另一路，并持续为缺失的一路重试取流；
    2. 收到「开始分析」（WPF 指令或预览窗口 ENTER）后才加载检测模型、
       开始录制与实时检测叠加；
    3. 收到「停止分析」（WPF 指令或 Q 键）后收尾并立即重建/拟合/导出。
    """
    if workspace is not None:
        set_workspace(workspace)
    sample_dir = get_workspace().samples / sample_name
    sample_dir.mkdir(parents=True, exist_ok=True)
    sources = [cam_left, cam_right]
    names = ["左", "右"]
    control = ControlChannel()

    print(f"\n边录制边分析 -> {sample_dir}")
    print(f"  左: {cam_left}  右: {cam_right}")
    # 打开两路（复用网络流增强）；某一路暂时没画面不影响另一路预览
    rec = DualCameraRecorder(cam_left, cam_right, sample_dir, fps=60, preview=False)
    # 预览发布走独立线程：编码写盘不占用读帧时间，避免拖慢读取导致丢包
    publisher = PreviewPublisher(preview_dir, preview_size)
    caps = [None, None]
    for idx in (0, 1):
        try:
            caps[idx] = rec._open_camera(sources[idx], names[idx], idx)
        except Exception as exc:
            print(f"  {names[idx]}路暂不可用: {exc}")

    if not (auto_start and all(caps)):
        if not _preview_until_start(rec, caps, names, sources, publisher, control):
            publisher.close()
            rec._cleanup()
            print("预览结束，未开始分析")
            return False

    # ── 开始分析：模型与标定在这一步才加载，保证点开就能先看到画面 ──
    print("\n开始分析：加载检测模型与标定...")
    model = _load_detector()

    # 相机标定（缺失则仅 2D 叠加）
    left_cfg = right_cfg = None
    try:
        configs = load_camera_configs()
        cfg_by_name = {c.name: c for c in configs}
        left_cfg, right_cfg = cfg_by_name["left"], cfg_by_name["right"]
        print("相机标定: 已加载，可实时 3D 显示与重建")
    except Exception as e:
        print(f"警告: 未加载相机标定（{e}），仅做 2D 检测叠加，录制后无法 3D 重建")

    cap_l, cap_r = caps[0], caps[1]
    fps_l, fps_r = rec._fps
    rec._fps[0] = fps_l
    rec._fps[1] = fps_r
    # 时间戳换算器：帧时间完全由流内时间戳决定，不随接收抖动变化
    clock_l, clock_r = rec.clock(0), rec.clock(1)
    tracker_l = _CamTracker(left_cfg)
    tracker_r = _CamTracker(right_cfg)
    # 卡尔曼帧间隔 = 分析抽帧间隔（analyze_every 帧分析一次）
    tracker_l.filter_interval = max(analyze_every, 1) / max(fps_l, 1e-6)
    tracker_r.filter_interval = max(analyze_every, 1) / max(fps_r, 1e-6)
    print("[CONTROL] analyzing=1")

    # 写 mp4（保留录制文件）：先写 .partial 中间文件，正常结束才改名为
    # recording.mp4，中途取消/被杀时不会留下读不出 moov atom 的坏视频
    # （那种文件会让后续离线处理直接报"无法打开视频"）。
    writers = [None, None]
    final_paths = [sample_dir / "left" / "recording.mp4",
                   sample_dir / "right" / "recording.mp4"]
    partial_paths = [sample_dir / ".partial" / "left" / "recording.mp4",
                     sample_dir / ".partial" / "right" / "recording.mp4"]
    if not no_save:
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")

        def _frame_size(idx, cap):
            """优先用已读到的首帧尺寸：MPEG-TS/UDP 流常拿不到 CAP_PROP 宽高，
            回退成 1920x1080 会和真实帧尺寸不匹配，写出来的 mp4 不可用。"""
            if rec._frame_buffers[idx]:
                frame = rec._frame_buffers[idx][-1]
                if frame is not None and getattr(frame, "size", 0):
                    return int(frame.shape[1]), int(frame.shape[0])
            return (int(cap.get(cv2.CAP_PROP_FRAME_WIDTH)) or 1920,
                    int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT)) or 1080)

        wl, hl = _frame_size(0, cap_l)
        wr, hr = _frame_size(1, cap_r)
        for path in final_paths + partial_paths:
            path.parent.mkdir(parents=True, exist_ok=True)
        writers[0] = cv2.VideoWriter(str(partial_paths[0]), fourcc, fps_l, (wl, hl))
        writers[1] = cv2.VideoWriter(str(partial_paths[1]), fourcc, fps_r, (wr, hr))

    t0 = time.time()
    world_buf = deque(maxlen=400)
    pending_left = []
    pending_right = []
    pair_max_gap = max(1.5 / min(fps_l, fps_r), 0.01)
    frame_idx = 0
    read_failures = 0
    # 上一次的检测结果：非分析帧继续显示，避免检测框一闪一闪
    last_l = last_r = None
    print("\n边录制边分析中... 按 Q 停止")

    while True:
        ret_l, frame_l = cap_l.read()
        ret_r, frame_r = cap_r.read()
        if not (ret_l and ret_r):
            # UDP 流偶发丢包会让单次 read 失败；只有连续失败才判定流已断，
            # 否则一次丢包就会结束整场录制。
            read_failures += 1
            if read_failures >= 30:
                print("连续 30 次读取失败，结束录制")
                break
            continue
        read_failures = 0
        # 各路独立帧时间戳：来自码流本身（PTS），替代挂钟/接收时刻
        if clock_l is not None:
            t_l = clock_l.stamp(cap_l)
        else:
            t_l = _frame_timestamp(cap_l, frame_idx, fps_l)
        if clock_r is not None:
            t_r = clock_r.stamp(cap_r)
        else:
            t_r = _frame_timestamp(cap_r, frame_idx, fps_r)
        t = max(t_l, t_r)

        # 首次设置几何
        if tracker_l.frame_w == 0:
            tracker_l.reset_geometry(frame_l.shape[1], frame_l.shape[0])
            tracker_r.reset_geometry(frame_r.shape[1], frame_r.shape[0])

        # 落盘
        if writers[0] is not None:
            writers[0].write(frame_l)
            writers[1].write(frame_r)

        # 分析（可抽帧）：未分析的帧沿用上一次检测结果，避免检测框一闪一闪
        if frame_idx % max(1, analyze_every) == 0:
            last_l = tracker_l.detect(model, frame_l, imgsz, conf)
            last_r = tracker_r.detect(model, frame_r, imgsz, conf)
            tracker_l.record(t_l, last_l)
            tracker_r.record(t_r, last_r)
            # 实时 3D 点：按各自帧时间戳就近配对，而不是按读取轮次配对
            if last_l is not None:
                pending_left.append((t_l, last_l["center"]))
            if last_r is not None:
                pending_right.append((t_r, last_r))
        chosen_l, chosen_r = last_l, last_r

        if len(pending_left) + len(pending_right) >= 4:
            pair = _match_pair(pending_left, pending_right, pair_max_gap)
            if pair is not None and left_cfg is not None:
                tri = triangulate_ball_point(pair[0][1], left_cfg,
                                             pair[1][1], right_cfg)
                if tri is not None:
                    world_buf.append((0.5 * (pair[0][0] + pair[1][0]), tri["world_point"]))

        # 叠加绘制
        _draw_ball(frame_l, chosen_l)
        _draw_ball(frame_r, chosen_r)
        world_pts = np.asarray([p for _, p in world_buf], dtype=np.float64) if world_buf else None
        _draw_world_trace(frame_l, world_pts, left_cfg)
        _draw_world_trace(frame_r, world_pts, right_cfg)

        # 投递给预览线程（编码在后台，不占用读帧时间）
        publisher.submit("left", frame_l)
        publisher.submit("right", frame_r)

        # 本机预览窗口：只有终端交互运行时才弹，WPF 场景由界面显示
        if control.interactive:
            f0 = _fit_letterbox(frame_l, (640, 360))
            f1 = _fit_letterbox(frame_r, (640, 360))
            combined = np.hstack([f0, f1])
            speed = _estimate_speed(world_buf)
            status = (f"LIVE {t:.1f}s | 3D pts: {len(world_buf)} | "
                      f"speed: {speed:.1f} m/s | Q=stop")
            cv2.putText(combined, status, (10, 28), cv2.FONT_HERSHEY_SIMPLEX,
                        0.7, (0, 0, 255), 2, cv2.LINE_AA)
            cv2.imshow("AIfootball 边录制边分析 (Q to stop)", combined)

        key = cv2.waitKey(1) & 0xFF
        if key in (ord("q"), ord("Q"), 27):
            break
        # 界面上的「停止分析」
        commands = control.poll()
        if "stop" in commands:
            break
        if control.closed and not commands:
            print("控制通道已关闭（界面已退出），停止分析并收尾")
            break
        if auto_stop and (time.time() - t0) >= auto_stop:
            print(f"  已达到设定时长 {auto_stop:.0f}s，自动停止")
            break
        frame_idx += 1

    # 收尾
    elapsed = time.time() - t0
    for w in writers:
        if w is not None:
            w.release()
    cap_l.release()
    cap_r.release()
    cv2.destroyAllWindows()
    publisher.close()
    print("[CONTROL] analyzing=0")
    print(f"\n录制结束: {elapsed:.1f}s, 左 {len(tracker_l.times)} 检测点, "
          f"右 {len(tracker_r.times)} 检测点")
    saved_ok = False
    if not no_save:
        # 有内容才把中间文件转正；否则删掉，避免留下打不开的空视频
        for idx in range(2):
            partial, final = partial_paths[idx], final_paths[idx]
            try:
                if frame_idx > 0 and partial.exists() and partial.stat().st_size > 0:
                    os.replace(str(partial), str(final))
                    print(f"已保存: {final}")
                    saved_ok = True
                elif partial.exists():
                    partial.unlink()
            except OSError as exc:
                print(f"保存 {final} 失败: {exc}")

    tracker_l.fps = fps_l
    tracker_r.fps = fps_r
    finish_reconstruction(sample_name, sample_dir, tracker_l, tracker_r,
                          left_cfg, right_cfg, model, imgsz, conf,
                          saved_ok=saved_ok)


def main():
    parser = argparse.ArgumentParser(description="AIfootball 边录制边分析")
    parser.add_argument("--cam-left", default="0", help="左相机索引或 RTSP/HTTP URL")
    parser.add_argument("--cam-right", default="1", help="右相机索引或 RTSP/HTTP URL")
    parser.add_argument("--sample", default="sample_live", help="样本名称")
    parser.add_argument("--imgsz", type=int, default=1280, help="检测输入尺寸")
    parser.add_argument("--conf", type=float, default=0.15, help="检测置信度阈值")
    parser.add_argument("--analyze-every", type=int, default=1,
                        help="每隔 N 帧分析一次（CPU 慢时调大，如 2/3）")
    parser.add_argument("--no-save", action="store_true", help="不落盘 mp4（仅实时分析）")
    parser.add_argument("--preview-dir", default=None,
                        help="把实时画面(叠加检测框)写到此目录供 WPF 显示")
    parser.add_argument("--auto-start", action="store_true",
                        help="跳过预览直接开始分析（无人值守/自检用）")
    parser.add_argument("--auto-stop", type=float, default=None,
                        help="分析指定秒数后自动停止（无人值守/自检用）")
    parser.add_argument("--workspace", default=None,
                        help="工作区目录（默认: AIFOOTBALL_WORKSPACE 环境变量，其次当前目录）")
    args = parser.parse_args()
    run(args.cam_left, args.cam_right, args.sample, args.imgsz, args.conf,
        args.analyze_every, args.no_save, preview_dir=args.preview_dir,
        auto_start=args.auto_start, auto_stop=args.auto_stop,
        workspace=args.workspace)
    return 0


if __name__ == "__main__":
    sys.exit(main())
