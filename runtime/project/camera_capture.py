"""双摄像头在线采集"""
import os
import time
from pathlib import Path
from collections import deque
import cv2
import numpy as np


def _split_url(url):
    """把 URL 拆成 scheme/netloc/path 三段（不含 query/fragment 的简化版）。

    用于 URL 归一化：scheme 决定是否走 https->http 兜底，path 为空则
    说明用户输入的是裸地址，需要补常见流路径。
    """
    scheme = "http"
    rest = url
    if "://" in url:
        scheme, rest = url.split("://", 1)
    query = ""
    if "?" in rest:
        rest, query = rest.split("?", 1)
        query = "?" + query
    # 去掉可能的尾部 '/'
    netloc = rest
    path = ""
    if "/" in rest:
        netloc, path = rest.split("/", 1)
        path = "/" + path
    # 分离 host:port 与可能带 user:pass@ 凭据
    if "@" in netloc:
        # 保留凭据原样
        pass
    return {"scheme": scheme.lower(), "netloc": netloc, "path": path, "query": query}


class DualCameraRecorder:
    def __init__(self, source_left, source_right, output_dir, fps=60, preview=True,
                 preview_dir=None, preview_size=(1280, 720)):
        self.source_left = source_left
        self.source_right = source_right
        self.output_dir = Path(output_dir)
        self.target_fps = fps
        self.preview = preview
        # 预览帧发布目录：把最近一帧编码成 JPEG 写到这里，供 WPF 端定时读取显示。
        # 仅在网络/录制预览时需要；None 表示关闭预览发布。
        self.preview_dir = Path(preview_dir) if preview_dir else None
        self.preview_size = preview_size
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
    def _ffmpeg_capture_options():
        """返回可用的 FFMPEG 抓取选项常量，不存在时返回 None 并跳过 set。

        不同 OpenCV 版本对 `CAP_PROP_OPENCV_FFMPEG_CAPTURE_OPTIONS` 的
        支持不一致：OpenCV 4.x 个别 build 有，5.x / 部分 4.x 没有。
        直接用 `cv2.CAP_PROP_OPENCV_FFMPEG_CAPTURE_OPTIONS` 在缺失时抛
        AttributeError，会令 `cap.read()` 之前就先崩溃。这里安全取用。
        """
        return getattr(cv2, "CAP_PROP_OPENCV_FFMPEG_CAPTURE_OPTIONS", None)

    @staticmethod
    def _open_capture_verify(source, timeout_seconds=4):
        """尝试打开 source 并读到一帧；成功返回 (cap, w, h, fps)，失败返回 None。

        用于 URL 归一化探活：不给候选 URL 长时间阻塞。调用方负责释放返回的 cap。
        """
        import threading
        _time = time
        result = {}

        def _worker():
            cap = cv2.VideoCapture(source)
            if not cap.isOpened():
                cap.release()
                result["value"] = None
                return
            opt = DualCameraRecorder._ffmpeg_capture_options()
            if opt is not None:
                try:
                    cap.set(opt, "rtsp_transport;tcp|rw_timeout;3000000")
                except Exception:
                    pass
            try:
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 2)
            except Exception:
                pass
            deadline = _time.time() + timeout_seconds
            while _time.time() < deadline:
                ret, frame = cap.read()
                if ret:
                    h, w = frame.shape[:2]
                    fps = cap.get(cv2.CAP_PROP_FPS)
                    if not fps or fps <= 0 or fps > 120:
                        fps = 0.0
                    result["value"] = (cap, w, h, float(fps))
                    return
            cap.release()
            result["value"] = None

        worker = threading.Thread(target=_worker, daemon=True)
        worker.start()
        worker.join(timeout_seconds + 2)
        if worker.is_alive():
            return None
        return result.get("value")

    @classmethod
    def normalize_stream_url(cls, source, timeout_seconds=4):
        """把用户输入的 URL 归一化为一个 OpenCV 可读的真实流地址。

        用户常在浏览器里打开的是一家摄像头/手机 app 的**网页管理地址**，
        例如 `https://10.70.98.78:8080`（IP Webcam Server）。这类地址：
          - 无路径（裸 IP:端口），OpenCV 无法断定是哪种流；
          - https 自签证书常让 FFMPEG 的 TLS 握手失败（SEC_E_NO_CREDENTIALS）。

        这里按候选顺序探活，返回第一个能打开并读到帧的 URL：
          1. 原 URL 原样；
          2. 补常见 MJPEG/直流路径：/video、/live、/stream、/mjpeg；
          3. https -> http 等价版本（内网自签证书常见，http 能直接避开 TLS 握手）。

        返回 (实际可用 URL, 描述)；全部失败返回 (原 URL, 失败原因)。
        """
        if not DualCameraRecorder._is_network_source(source):
            return source, "非网络流"

        # 候选生成
        candidates = []
        seen = set()

        def _add(candidate):
            c = candidate.strip()
            if c and c not in seen:
                seen.add(c)
                candidates.append(c)

        _add(source)

        # 分析路径：若 URL 无路径（结尾是端口或 host），补常见流路径
        parsed = _split_url(source)
        base = parsed["scheme"] + "://" + parsed["netloc"]
        if not parsed["path"] or parsed["path"] in ("/", ""):
            for p in ("/video", "/live", "/stream", "/mjpeg"):
                _add(base + p)
        else:
            _add(source)
        # https -> http 版本（自签证书最省事）
        if parsed["scheme"] == "https":
            _add("http://" + parsed["netloc"] + (parsed["path"] or "/video"))
            if not parsed["path"] or parsed["path"] in ("/", ""):
                for p in ("/video", "/live", "/stream", "/mjpeg"):
                    _add("http://" + parsed["netloc"] + p)
        elif parsed["scheme"] == "http":
            # http 打不开，也顺带试 https（个别设备只开 https）
            _add("https://" + parsed["netloc"] + (parsed["path"] or "/video"))

        last_err = None
        for cand in candidates:
            res = DualCameraRecorder._open_capture_verify(cand, timeout_seconds)
            if res is not None:
                cap, w, h, fps = res
                cap.release()
                info = f"{w}x{h}" + (f" @ {fps:.0f} fps" if fps else "")
                return cand, f"连接成功: {info}"
            last_err = f"无法打开: {cand}"
        return source, (last_err or "无法打开流")

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

        先对 URL 做归一化（裸地址 -> 常见流路径、https -> http 兜底），再在
        子线程中打开/读取，主线程用 join 限定总耗时，避免不可达地址导致
        cap.read() 无限阻塞。
        """
        import threading
        import time
        if not DualCameraRecorder._is_network_source(source):
            return False, f"无效的网络流地址: {source}"

        # 归一化：找到 OpenCV 真正能读的流地址。
        normalized, norm_info = DualCameraRecorder.normalize_stream_url(
            source, timeout_seconds=min(timeout_seconds, 4))

        result = {}
        open_source = normalized

        def _worker():
            cap = cv2.VideoCapture(open_source)
            if not cap.isOpened():
                cap.release()
                result["value"] = (False, f"无法打开流: {open_source}")
                return
            # 优先 TCP + 读取超时（rw_timeout 单位微秒）；常量缺失时跳过 set
            opt = DualCameraRecorder._ffmpeg_capture_options()
            if opt is not None:
                try:
                    cap.set(opt, "rtsp_transport;tcp|rw_timeout;3000000")
                except Exception:
                    pass
            try:
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 2)
            except Exception:
                pass
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
            result["value"] = (False, f"连接超时（{timeout_seconds}s 内未收到画面）: {open_source}")

        worker = threading.Thread(target=_worker, daemon=True)
        worker.start()
        worker.join(timeout_seconds + 2)
        if worker.is_alive():
            return False, f"连接超时（{timeout_seconds}s）: {open_source}"
        return result.get("value", (False, "连接失败"))

    def _open_camera(self, source, name, idx):
        is_network = self._is_network_source(source)
        if is_network:
            # 归一化：裸地址 / https 自签证书 -> 找 OpenCV 可读的流地址
            normalized, _ = DualCameraRecorder.normalize_stream_url(source, timeout_seconds=4)
            source = normalized
        cap = cv2.VideoCapture(source)
        if not cap.isOpened():
            raise RuntimeError(f"无法打开摄像头 {name}: {source}")
        if is_network:
            # 网络流：降低缓冲、优先 TCP 传输、读取超时，减少延迟与卡死
            opt = DualCameraRecorder._ffmpeg_capture_options()
            if opt is not None:
                try:
                    cap.set(opt, "rtsp_transport;tcp|rw_timeout;5000000")
                except Exception:
                    pass
            try:
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 2)
            except Exception:
                pass
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

    def _publish_preview(self):
        """把最近一帧左右画面编码成 JPEG 写盘，供 WPF 端定时读取显示。

        预览通道用本地文件轮换（非 socket/HTTP）：WPF 以 ~20fps 读这两个
        JPEG 即可实时看到画面，不额外占用网络、不受防火墙/证书影响，也与
        "正在录制的流"同源。仅当构造时传入 preview_dir 才发布。分辨率按
        preview_size 缩放（默认 1280x720），检测仍用原始帧，互不影响。
        每隔若干帧写一次，避免低分辨率预览消耗过多编码 CPU。
        """
        if self.preview_dir is None:
            return
        try:
            self.preview_dir.mkdir(parents=True, exist_ok=True)
            w, h = int(self.preview_size[0]), int(self.preview_size[1])
            for i, name in enumerate(("left", "right")):
                if self._frame_buffers[i]:
                    frame = self._frame_buffers[i][-1]
                    if frame is not None and frame.size:
                        frame_r = cv2.resize(frame, (w, h)) if frame.shape[1] != w else frame
                        path = self.preview_dir / f"live_preview_{name}.jpg"
                        # imwrite 不保证原子写；用临时文件+replace 避免 WPF 读到半张图
                        tmp = path.with_suffix(".tmp.jpg")
                        if cv2.imwrite(str(tmp), frame_r, [int(cv2.IMWRITE_JPEG_QUALITY), 80]):
                            os.replace(str(tmp), str(path))
        except Exception:
            # 预览失败不应阻断录制
            pass

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
            # 向 WPF 预览发布最近一帧（若已配置 preview_dir）；不阻塞录制
            if self.preview_dir is not None:
                self._publish_preview()
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
