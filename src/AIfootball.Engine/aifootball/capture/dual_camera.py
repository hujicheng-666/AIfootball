"""双摄像头在线采集"""
import time
from collections import deque
from pathlib import Path

import cv2
import numpy as np


class DualCameraRecorder:
    """双摄像头同步录制器"""

    def __init__(self, source_left, source_right, output_dir: Path,
                 fps: int = 60, preview: bool = True):
        self.source_left = source_left
        self.source_right = source_right
        self.output_dir = Path(output_dir)
        self.target_fps = fps
        self.preview = preview
        self._caps = [None, None]
        self._writers = [None, None]
        self._recording = False
        self._frame_buffers = [deque(maxlen=300), deque(maxlen=300)]
        self._frame_counts = [0, 0]
        self._fps = [fps, fps]  # 每路实际帧率（网络流读取后修正）
        self._start_time = 0.0

    @staticmethod
    def _is_network_source(source) -> bool:
        """判断是否为网络视频流 (RTSP/HTTP)，否则视为本地相机索引或文件"""
        return isinstance(source, str) and ("://" in source)

    @staticmethod
    def list_cameras(max_test: int = 8):
        """列出可用摄像头"""
        available = []
        for i in range(max_test):
            cap = cv2.VideoCapture(i)
            if cap.isOpened():
                ret, _ = cap.read()
                if ret:
                    available.append(i)
            cap.release()
        return available

    @staticmethod
    def test_stream(source, timeout_seconds: int = 8):
        """测试网络视频流是否可打开并读取到画面。返回 (ok, 信息)。

        在子线程中打开/读取，主线程用 join 限定总耗时，避免不可达
        地址导致 cap.read() 无限阻塞。
        """
        import threading
        import time
        if not DualCameraRecorder._is_network_source(source):
            return False, f"无效的网络流地址: {source}"

        result = {}

        def _worker():
            cap = cv2.VideoCapture(source)
            if not cap.isOpened():
                cap.release()
                result["value"] = (False, f"无法打开流: {source}")
                return
            # 优先 TCP + 读取超时（rw_timeout 单位微秒）
            cap.set(cv2.CAP_PROP_BUFFERSIZE, 2)
            cap.set(cv2.CAP_PROP_OPENCV_FFMPEG_CAPTURE_OPTIONS,
                    "rtsp_transport;tcp|rw_timeout;3000000")
            deadline = time.time() + timeout_seconds
            while time.time() < deadline:
                ret, frame = cap.read()
                if ret:
                    h, w = frame.shape[:2]
                    fps = cap.get(cv2.CAP_PROP_FPS)
                    cap.release()
                    if fps <= 0 or fps > 120:
                        fps = 0
                    info = f"{w}x{h} @ {fps:.0f} fps" if fps else f"{w}x{h}"
                    result["value"] = (True, f"连接成功: {info}")
                    return
            cap.release()
            result["value"] = (False, f"连接超时（{timeout_seconds}s 内未收到画面）: {source}")

        worker = threading.Thread(target=_worker, daemon=True)
        worker.start()
        worker.join(timeout_seconds + 2)
        if worker.is_alive():
            return False, f"连接超时（{timeout_seconds}s）: {source}"
        return result.get("value", (False, "连接失败"))

    def _open_camera(self, source, name: str, idx: int):
        is_network = self._is_network_source(source)
        if is_network:
            cap = cv2.VideoCapture(source)  # RTSP/HTTP 网络流
        else:
            try:
                idx_ = int(source)
                cap = cv2.VideoCapture(idx_)
            except ValueError:
                cap = cv2.VideoCapture(source)  # 文件路径

        if not cap.isOpened():
            raise RuntimeError(f"无法打开摄像头 {name}: {source}")

        if is_network:
            # 网络流：降低缓冲、优先 TCP 传输、读取超时，减少延迟与卡死
            cap.set(cv2.CAP_PROP_BUFFERSIZE, 2)
            cap.set(cv2.CAP_PROP_OPENCV_FFMPEG_CAPTURE_OPTIONS,
                    "rtsp_transport;tcp|rw_timeout;5000000")
        else:
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1920)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 1080)
            cap.set(cv2.CAP_PROP_FPS, self.target_fps)

        # 读取一帧确认可用并获取真实分辨率/帧率
        ret, frame = cap.read()
        if not ret:
            cap.release()
            raise RuntimeError(f"摄像头 {name} 无画面输出: {source}")
        h, w = frame.shape[:2]
        actual_fps = cap.get(cv2.CAP_PROP_FPS)
        if not actual_fps or actual_fps <= 0 or actual_fps > 120:
            actual_fps = self.target_fps
        self._fps[idx] = actual_fps
        print(f"  {name}: {w}x{h} @ {actual_fps:.1f} fps")
        return cap

    def start(self):
        """开始录制交互流程"""
        self.output_dir.mkdir(parents=True, exist_ok=True)
        print(f"\n{'='*50}")
        print(f"  双摄像头在线录制")
        print(f"{'='*50}")
        print(f"  左: {self.source_left}  右: {self.source_right}")
        print(f"  按 ENTER 开始录制，按 Q 停止")
        print(f"{'='*50}")

        self._caps[0] = self._open_camera(self.source_left, "左", 0)
        self._caps[1] = self._open_camera(self.source_right, "右", 1)

        print("\n预览中 (ENTER 开始)...")
        while True:
            for i in range(2):
                ret, frame = self._caps[i].read()
                if ret:
                    self._frame_buffers[i].append(frame.copy())

            if self.preview and len(self._frame_buffers[0]) > 0:
                f0 = cv2.resize(self._frame_buffers[0][-1], (640, 360))
                f1 = cv2.resize(self._frame_buffers[1][-1], (640, 360))
                combined = np.hstack([f0, f1])
                cv2.putText(combined, "Left", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
                cv2.putText(combined, "Right", (650, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 255, 0), 2)
                cv2.imshow("Camera Preview (ENTER to start)", combined)

            key = cv2.waitKey(1) & 0xFF
            if key == 13:  # ENTER
                break
            elif key == ord("q"):
                self._cleanup()
                return False
            elif key == 27:  # ESC
                self._cleanup()
                return False

        return self._record()

    def _record(self):
        """实际录制"""
        fourcc = cv2.VideoWriter_fourcc(*"mp4v")
        names = ["left.mp4", "right.mp4"]

        for i in range(2):
            frame = self._frame_buffers[i][-1] if len(self._frame_buffers[i]) else None
            h, w = frame.shape[:2] if frame is not None else (1080, 1920)
            path = str(self.output_dir / names[i])
            self._writers[i] = cv2.VideoWriter(path, fourcc, self._fps[i], (w, h))

        self._recording = True
        self._start_time = time.time()
        print("\n录制中... 按 Q 停止")

        # 先写入缓冲帧
        for i in range(2):
            for frame in self._frame_buffers[i]:
                self._writers[i].write(frame)
                self._frame_counts[i] += 1

        while self._recording:
            for i in range(2):
                ret, frame = self._caps[i].read()
                if ret:
                    self._writers[i].write(frame)
                    self._frame_counts[i] += 1

            if self.preview and len(self._frame_buffers[0]) > 0:
                f0 = cv2.resize(self._frame_buffers[0][-1], (640, 360))
                f1 = cv2.resize(self._frame_buffers[1][-1], (640, 360))
                combined = np.hstack([f0, f1])
                elapsed = time.time() - self._start_time
                cv2.putText(combined, f"REC {elapsed:.1f}s", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.7, (0, 0, 255), 2)
                cv2.imshow("Recording (Q to stop)", combined)

            if cv2.waitKey(1) & 0xFF == ord("q"):
                break

        self._cleanup()
        elapsed = time.time() - self._start_time
        print(f"录制完成: {elapsed:.1f}s, "
              f"左 {self._frame_counts[0]} 帧, "
              f"右 {self._frame_counts[1]} 帧")
        return True

    def _cleanup(self):
        self._recording = False
        for cap in self._caps:
            if cap is not None:
                cap.release()
        for writer in self._writers:
            if writer is not None:
                writer.release()
        cv2.destroyAllWindows()
