"""双摄像头在线采集"""
import time
from pathlib import Path
from collections import deque
import cv2
import numpy as np

class DualCameraRecorder:
    def __init__(self, source_left, source_right, output_dir, fps=60, preview=True):
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
        self._fps = [fps, fps]      # 每路实际帧率（网络流读取后修正）
        self._start_time = 0.0

    @staticmethod
    def _is_network_source(source):
        """判断是否为网络视频流 (RTSP/HTTP)，否则视为本地相机索引或文件"""
        return isinstance(source, str) and ("://" in source)

    @staticmethod
    def list_cameras(max_test=4):
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
    def test_stream(source, timeout_seconds=8):
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

    def _open_camera(self, source, name, idx):
        is_network = self._is_network_source(source)
        cap = cv2.VideoCapture(source)
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
        fps = cap.get(cv2.CAP_PROP_FPS)
        if not fps or fps <= 0 or fps > 120:
            fps = self.target_fps
        self._fps[idx] = fps
        print(f"  {name}: {w}x{h} @ {fps:.1f} fps")
        return cap

    def start(self):
        self.output_dir.mkdir(parents=True, exist_ok=True)
        print(f"\n{'='*50}\n  双摄像头在线录制\n{'='*50}")
        print(f"  左: {self.source_left}  右: {self.source_right}")
        print(f"  按 ENTER 开始，按 Q 停止\n{'='*50}")
        print("\n打开摄像头...")
        self._caps[0] = self._open_camera(self.source_left, "左", 0)
        self._caps[1] = self._open_camera(self.source_right, "右", 1)
        print("\n预览中（ENTER 开始）...")
        while True:
            for i in range(2):
                ret, frame = self._caps[i].read()
                if ret: self._frame_buffers[i].append(frame.copy())
            if self.preview and len(self._frame_buffers[0]) > 0:
                f0 = cv2.resize(self._frame_buffers[0][-1], (640, 360))
                f1 = cv2.resize(self._frame_buffers[1][-1], (640, 360))
                cv2.imshow("预览", np.hstack([f0, f1]))
            key = cv2.waitKey(1) & 0xFF
            if key == 13: break
            if key == ord('q'): self._cleanup(); return False
        fourcc = cv2.VideoWriter_fourcc(*'mp4v')
        for i, name in enumerate(["left", "right"]):
            frame = self._frame_buffers[i][-1] if len(self._frame_buffers[i]) else None
            h, w = frame.shape[:2] if frame is not None else (1080, 1920)
            camera_dir = self.output_dir / name
            camera_dir.mkdir(parents=True, exist_ok=True)
            self._writers[i] = cv2.VideoWriter(
                str(camera_dir / "recording.mp4"), fourcc, self._fps[i], (w, h))
        print("\n录制中... Q 停止")
        self._recording = True; self._start_time = time.time()
        while self._recording:
            for i in range(2):
                ret, frame = self._caps[i].read()
                if ret: self._writers[i].write(frame); self._frame_counts[i] += 1
                self._frame_buffers[i].append(frame.copy() if ret else self._frame_buffers[i][-1])
            if self.preview and len(self._frame_buffers[0]) > 0:
                f0 = cv2.resize(self._frame_buffers[0][-1], (640, 360))
                f1 = cv2.resize(self._frame_buffers[1][-1], (640, 360))
                elapsed = time.time() - self._start_time
                cv2.putText(c := np.hstack([f0, f1]), f"录制 {elapsed:.1f}s Q=停止", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)
                cv2.imshow("录制", c)
            if cv2.waitKey(1) & 0xFF == ord('q'): self._recording = False
        self._cleanup(); return True

    def _cleanup(self):
        for c in self._caps:
            if c: c.release()
        for w in self._writers:
            if w: w.release()
        cv2.destroyAllWindows()
        e = time.time() - self._start_time if self._start_time else 0
        print(f"\n录制: {e:.1f}s, 帧: {self._frame_counts}")
        print(f"保存到: {self.output_dir}")
