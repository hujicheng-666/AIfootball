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
import sys
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np

from project.camera_capture import DualCameraRecorder
from project.config import WORKSPACE as WORKSPACE_DIR
from project.reconstruct_3d_trajectory import (
    OUTPUT_ROOT,
    SPORTS_BALL_CLASS_ID,
    PENALTY_SPOT_WORLD,
    VideoTrack,
    load_camera_configs,
    pick_detection,
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


class _CamTracker:
    """单路实时检测状态：位置预测历史 + 累积 2D 检测点"""

    def __init__(self, config):
        self.config = config                      # CameraConfig 或 None
        self.history = deque(maxlen=8)
        self.miss = 0
        self.frame_w = 0
        self.frame_h = 0
        self.penalty_center = None                # 罚球点投影（需标定）
        self.times = []
        self.points = []                          # 2D 中心
        self.foots = []
        self.confs = []
        self.ground_points = []                   # 地面投影（需标定）

    def reset_geometry(self, frame_w, frame_h):
        self.frame_w, self.frame_h = frame_w, frame_h
        if self.config is not None:
            self.penalty_center = project_world_points(
                PENALTY_SPOT_WORLD.reshape(1, 3), self.config)[0]

    def detect(self, model, frame, imgsz, conf):
        """检测本帧足球；返回 chosen 或 None，并更新预测历史"""
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
        else:
            self.miss += 1
            if self.miss > 5:
                self.history.clear()
                self.miss = 0
        return chosen

    def record(self, t, chosen):
        """累积一个检测点（时间戳秒、中心、置信度、地面投影）"""
        if chosen is None:
            return
        self.times.append(float(t))
        self.points.append(chosen["center"].copy())
        self.foots.append(chosen["foot"].copy())
        self.confs.append(float(chosen["conf"]))
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
        times, points, confs = [], [], []
        for i in range(kick_frame, len(self.times)):
            times.append(self.times[i])
            points.append(self.points[i])
            confs.append(self.confs[i])
        return times, points, confs


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


def finish_reconstruction(sample_name, sample_dir, tracker_l, tracker_r,
                          left_cfg, right_cfg, model, imgsz, conf,
                          saved_ok=True, fallback=True):
    """停止后用已累积检测结果直接重建 + 弹道 + 导出；不足时回退离线重跑。"""
    from project.fit_ballistic_trajectory import process_sample as ballistic_sample
    from project.export_unity_trajectory import convert_sample

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
        times, points, confs = tracker.track_after_kick(kick_frame)
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
        )

    if left_cfg is None or right_cfg is None:
        print("缺少相机标定，无法进行 3D 重建，跳过（仅完成录制）。")
        return False

    if nl - kick < 8 or nr - kick < 8:
        if fallback:
            print(f"实时检测点不足（左 {nl}, 右 {nr}），回退到离线重跑检测...")
            from project.reconstruct_3d_trajectory import process_sample as recon_full
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
            from project.reconstruct_3d_trajectory import process_sample as recon_full
            try:
                recon_full(sample_dir, [left_cfg, right_cfg], model, imgsz=imgsz, conf=conf)
                ballistic_sample(sample_name)
                convert_sample(sample_name)
                return True
            except Exception as e2:
                print(f"离线回退失败: {e2}")
        return False

    out = OUTPUT_ROOT / sample_name
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
        imgsz=1280, conf=0.15, analyze_every=1, no_save=False):
    """边录制边分析核心入口（供 CLI 与独立版复用）"""
    sample_dir = Path("samples") / sample_name
    sample_dir.mkdir(parents=True, exist_ok=True)

    # YOLO 模型
    from ultralytics import YOLO
    model_path = os.environ.get("YOLO_MODEL_PATH") or str(WORKSPACE_DIR / "models" / "yolo11m.pt")
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

    # 相机标定（缺失则仅 2D 叠加）
    left_cfg = right_cfg = None
    try:
        configs = load_camera_configs()
        cfg_by_name = {c.name: c for c in configs}
        left_cfg, right_cfg = cfg_by_name["left"], cfg_by_name["right"]
        print("相机标定: 已加载，可实时 3D 显示与重建")
    except Exception as e:
        print(f"警告: 未加载相机标定（{e}），仅做 2D 检测叠加，录制后无法 3D 重建")

    # 打开两路（复用网络流增强）
    rec = DualCameraRecorder(cam_left, cam_right, sample_dir, fps=60, preview=False)
    print(f"\n边录制边分析 -> {sample_dir}")
    print(f"  左: {cam_left}  右: {cam_right}")
    print("  打开摄像头...")
    cap_l = rec._open_camera(cam_left, "左", 0)
    cap_r = rec._open_camera(cam_right, "右", 1)
    fps_l, fps_r = rec._fps
    rec._fps[0] = fps_l
    rec._fps[1] = fps_r
    tracker_l = _CamTracker(left_cfg)
    tracker_r = _CamTracker(right_cfg)

    # 写 mp4（保留录制文件）
    writers = [None, None]
    if not no_save:
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        wl = int(cap_l.get(cv2.CAP_PROP_FRAME_WIDTH)) or 1920
        hl = int(cap_l.get(cv2.CAP_PROP_FRAME_HEIGHT)) or 1080
        wr = int(cap_r.get(cv2.CAP_PROP_FRAME_WIDTH)) or 1920
        hr = int(cap_r.get(cv2.CAP_PROP_FRAME_HEIGHT)) or 1080
        left_dir = sample_dir / "left"
        right_dir = sample_dir / "right"
        left_dir.mkdir(parents=True, exist_ok=True)
        right_dir.mkdir(parents=True, exist_ok=True)
        writers[0] = cv2.VideoWriter(str(left_dir / "recording.mp4"), fourcc, fps_l, (wl, hl))
        writers[1] = cv2.VideoWriter(str(right_dir / "recording.mp4"), fourcc, fps_r, (wr, hr))

    t0 = time.time()
    world_buf = deque(maxlen=400)
    frame_idx = 0
    print("\n边录制边分析中... 按 Q 停止")

    while True:
        ret_l, frame_l = cap_l.read()
        ret_r, frame_r = cap_r.read()
        if not (ret_l and ret_r):
            break
        t = time.time() - t0

        # 首次设置几何
        if tracker_l.frame_w == 0:
            tracker_l.reset_geometry(frame_l.shape[1], frame_l.shape[0])
            tracker_r.reset_geometry(frame_r.shape[1], frame_r.shape[0])

        # 落盘
        if writers[0] is not None:
            writers[0].write(frame_l)
            writers[1].write(frame_r)

        # 分析（可抽帧）
        chosen_l = chosen_r = None
        if frame_idx % max(1, analyze_every) == 0:
            chosen_l = tracker_l.detect(model, frame_l, imgsz, conf)
            chosen_r = tracker_r.detect(model, frame_r, imgsz, conf)
            tracker_l.record(t, chosen_l)
            tracker_r.record(t, chosen_r)

            # 实时 3D 点
            if chosen_l is not None and chosen_r is not None and left_cfg is not None:
                tri = triangulate_ball_point(chosen_l["center"], left_cfg,
                                             chosen_r["center"], right_cfg)
                if tri is not None:
                    world_buf.append((t, tri["world_point"]))

        # 叠加绘制
        _draw_ball(frame_l, chosen_l)
        _draw_ball(frame_r, chosen_r)
        world_pts = np.asarray([p for _, p in world_buf], dtype=np.float64) if world_buf else None
        _draw_world_trace(frame_l, world_pts, left_cfg)
        _draw_world_trace(frame_r, world_pts, right_cfg)

        # 合并预览
        f0 = cv2.resize(frame_l, (640, 360))
        f1 = cv2.resize(frame_r, (640, 360))
        combined = np.hstack([f0, f1])
        speed = _estimate_speed(world_buf)
        status = f"LIVE {t:.1f}s | 3D pts: {len(world_buf)} | speed: {speed:.1f} m/s | Q=stop"
        cv2.putText(combined, status, (10, 28), cv2.FONT_HERSHEY_SIMPLEX,
                    0.7, (0, 0, 255), 2, cv2.LINE_AA)
        cv2.putText(combined, "L", (10, 350), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
        cv2.putText(combined, "R", (650, 350), cv2.FONT_HERSHEY_SIMPLEX, 0.6, (0, 255, 0), 2)
        cv2.imshow("AIfootball 边录制边分析 (Q to stop)", combined)

        key = cv2.waitKey(1) & 0xFF
        if key in (ord("q"), ord("Q"), 27):
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
    print(f"\n录制结束: {elapsed:.1f}s, 左 {len(tracker_l.times)} 检测点, "
          f"右 {len(tracker_r.times)} 检测点")
    if not no_save:
        print(f"已保存: {sample_dir / 'left' / 'recording.mp4'} / {sample_dir / 'right' / 'recording.mp4'}")

    tracker_l.fps = fps_l
    tracker_r.fps = fps_r
    finish_reconstruction(sample_name, sample_dir, tracker_l, tracker_r,
                          left_cfg, right_cfg, model, imgsz, conf,
                          saved_ok=not no_save)


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
    args = parser.parse_args()
    run(args.cam_left, args.cam_right, args.sample, args.imgsz, args.conf,
        args.analyze_every, args.no_save)


if __name__ == "__main__":
    main()
