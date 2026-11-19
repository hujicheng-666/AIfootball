"""双摄像头在线采集"""
import math
import os
import time
from pathlib import Path
from collections import deque
import cv2
import numpy as np

from ..control import ControlChannel
from ..preview import fit_scale


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


def _udp_endpoint(source):
    """从 udp://@host:port?... 里取出端口号；解析不出返回 None。"""
    try:
        text = str(source)
        if not text.lower().startswith(("udp://", "rtp://")):
            return None
        rest = text.split("://", 1)[1].split("?", 1)[0]
        if "@" in rest:
            rest = rest.split("@", 1)[1]
        return int(rest.rsplit(":", 1)[-1])
    except Exception:
        return None


def _udp_port_busy(port):
    """能否在本机独占绑定该 UDP 端口；绑不上说明已被别的进程占着。"""
    import socket
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.bind(("0.0.0.0", port))
        return False
    except OSError:
        return True
    finally:
        sock.close()


def _describe_udp_failure(source):
    """UDP 打不开时区分"端口被占用"和一般的打开失败。

    最常见的占用来源是上一次界面退出后没结束的采集进程：它继续占着端口，
    新进程既绑不上、收到的码流也会被它抢走一半，表现为满屏解码错误。
    """
    port = _udp_endpoint(source)
    if port is not None and _udp_port_busy(port):
        return (f"UDP 端口 {port} 已被其他进程占用（常见于上次的采集进程未退出）。"
                f"请结束占用该端口的进程后重试。")
    return f"无法打开流: {source}"


class FrameClock:
    """把流内时间戳换算成统一的秒，供左右两路按时间戳对齐。

    实测（OpenCV 5.0 + FFmpeg 后端，MPEG-TS/UDP）：
    - `CAP_PROP_PTS` 返回的是帧序号，不能用；
    - `CAP_PROP_POS_MSEC` 名字不可靠：单位随流的 time_base 变化，同样 30fps
      的流实测可能每帧 +33.3，也可能每帧 +33680，所以单位要用实测帧率反推；
    - 绝对时间（UTC 基准）会被 OpenCV 抹掉（给流加 3600s 偏移，读出来仍是
      接近 0 的相对值），因此拿到的是相对量。

    做法：帧与帧之间的关系完全由流内时间戳决定（不受接收抖动影响）；
    两路各自的零点用"到达时刻 - 流内时间"的滑动中位数估计一次即可，
    比逐帧用接收时刻稳定得多。
    """

    # 候选单位：时间戳 / scale = 秒
    SCALES = (1000.0, 1_000_000.0, 90000.0, 1.0)

    def __init__(self, fps, scale=1000.0, window=30):
        self.fps = max(float(fps), 1e-6)
        self.scale = float(scale) or 1000.0
        self._offsets = deque(maxlen=int(window))
        self._last = None

    @staticmethod
    def value_of(cap):
        """读取当前帧的流内时间戳；无效返回 None。"""
        try:
            value = cap.get(cv2.CAP_PROP_POS_MSEC)
        except Exception:
            return None
        try:
            value = float(value)
        except (TypeError, ValueError):
            return None
        if not math.isfinite(value) or value < 0:
            return None
        return value

    @classmethod
    def detect_scale(cls, cap, fps, samples=12):
        """用相邻帧时间戳增量与实测帧率比较，推断时间戳单位。"""
        deltas = []
        previous = None
        for _ in range(int(samples)):
            ret, _ = cap.read()
            if not ret:
                continue
            value = cls.value_of(cap)
            if value is None:
                continue
            if previous is not None and value > previous:
                deltas.append(value - previous)
            previous = value
        if not deltas or fps <= 0:
            return 1000.0
        deltas.sort()
        median = deltas[len(deltas) // 2]
        expected = 1.0 / float(fps)
        best_scale, best_error = 1000.0, None
        for scale in cls.SCALES:
            error = abs(median - expected * scale)
            if best_error is None or error < best_error:
                best_scale, best_error = scale, error
        # 只有确实贴合某个量纲才采信，否则按毫秒处理
        if best_error is not None and best_error <= 0.35 * expected * best_scale:
            return best_scale
        return 1000.0

    def stamp(self, cap):
        """返回当前帧在统一时间轴上的秒数。"""
        now = time.time()
        value = self.value_of(cap)
        if value is None:
            # 时间戳不可用（或回绕）时按实测帧率线性推进，保证单调
            self._last = now if self._last is None else self._last + 1.0 / self.fps
            return self._last
        seconds = value / self.scale
        self._offsets.append(now - seconds)
        offset = sorted(self._offsets)[len(self._offsets) // 2]
        self._last = seconds + offset
        return self._last


class DualCameraRecorder:
    def __init__(self, source_left, source_right, output_dir, fps=60, preview=True,
                 preview_dir=None, preview_size=(1280, 720), preview_interval=0.05,
                 open_timeout=8.0, record_seconds=None):
        self.source_left = source_left
        self.source_right = source_right
        self.output_dir = Path(output_dir)
        self.target_fps = fps
        self.preview = preview
        # 预览帧发布目录：把最近一帧编码成 JPEG 写到这里，供 WPF 端定时读取显示。
        # 仅在网络/录制预览时需要；None 表示关闭预览发布。
        self.preview_dir = Path(preview_dir) if preview_dir else None
        self.preview_size = preview_size
        # 预览编码限频：JPEG 编码约数毫秒/帧，逐帧发布会把采集线程拖慢，
        # 因此默认最多 20fps 发布一次（远高于 WPF 的读取节奏）。
        self.preview_interval = max(0.0, float(preview_interval))
        # 打开一路相机的最长等待：UDP 没有数据时 cap.read() 会无限阻塞，
        # 若不加限时，界面会停在"等待画面"且没有任何提示。
        self.open_timeout = max(1.0, float(open_timeout))
        # 给定秒数表示无人值守录制：跳过 ENTER 等待，录满自动停止。
        self.record_seconds = float(record_seconds) if record_seconds else None
        self._caps = [None, None]
        self._writers = [None, None]
        self._recording = False
        self._frame_buffers = [deque(maxlen=300), deque(maxlen=300)]
        self._frame_counts = [0, 0]
        self._fps = [fps, fps]      # 每路实际帧率（网络流读取后修正）
        self._clocks = [None, None]  # 每路的流内时间戳换算器（FrameClock）
        self._start_time = 0.0
        self._last_preview_at = 0.0
        self._preview_dir_ready = False
        self._partial_paths = [None, None]
        self._final_paths = [None, None]

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
    def _configure_network_capture(source):
        """Set FFmpeg options before VideoCapture is created.

        `fifo_size` 是 UDP 接收缓冲（字节），默认 5MB；此前设成 4KB（约 3 个
        数据包）：预览这种读得快的场景勉强够用，一旦进入分析、CPU 推理把读取
        拖慢就立刻溢出丢包，而 HEVC 丢一个参考帧就是满屏花屏。
        `analyzeduration`/`probesize` 一并去掉——它们让 FFmpeg 几乎不做流探测，
        中途加入时要拿到 HEVC 的 VPS/SPS/PPS 只能靠运气。
        """
        uri = str(source).lower()
        if uri.startswith("udp://"):
            os.environ["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = (
                "fflags;nobuffer|flags;low_delay|fifo_size;5000000|"
                "overrun_nonfatal;1|buffer_size;8388608"
            )
        elif uri.startswith("rtsp://"):
            os.environ["OPENCV_FFMPEG_CAPTURE_OPTIONS"] = "rtsp_transport;tcp|rw_timeout;5000000"

    @staticmethod
    def _open_network_capture(source):
        DualCameraRecorder._configure_network_capture(source)
        cap = cv2.VideoCapture(source, cv2.CAP_FFMPEG)
        if not cap.isOpened():
            cap.release()
            cap = cv2.VideoCapture(source)
        return cap

    @staticmethod
    def _measure_arrival_fps(cap, sample_seconds=1.2):
        """Measure decoded arrivals instead of trusting MPEG-TS header FPS."""
        warmup_until = time.monotonic() + 0.25
        while time.monotonic() < warmup_until:
            cap.read()
        started = time.monotonic()
        frames = 0
        while time.monotonic() - started < sample_seconds:
            ret, _ = cap.read()
            if ret:
                frames += 1
        elapsed = time.monotonic() - started
        return frames / elapsed if elapsed > 0 else 0.0

    @staticmethod
    def _open_capture_verify(source, timeout_seconds=4):
        """尝试打开 source 并读到一帧；成功返回 (cap, w, h, fps)，失败返回 None。

        用于 URL 归一化探活：不给候选 URL 长时间阻塞。调用方负责释放返回的 cap。
        """
        import threading
        _time = time
        result = {}

        def _worker():
            cap = DualCameraRecorder._open_network_capture(source) if DualCameraRecorder._is_network_source(source) else cv2.VideoCapture(source)
            if not cap.isOpened():
                cap.release()
                result["value"] = None
                return
            try:
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
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

        # UDP/RTP 是固定的监听地址，不存在"裸网页地址补 /video 等流路径"的场景。
        # 若照 RTSP/HTTP 那样扩展候选，每一路都要等满超时才失败，用户点一次
        # "开始采集"或"测试连接"要等几十秒才知道地址不可用。
        if str(source).split("://", 1)[0].lower() in ("udp", "rtp", "rtp+"):
            return source, "UDP/RTP 监听地址"

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
            cap = DualCameraRecorder._open_network_capture(open_source)
            if not cap.isOpened():
                cap.release()
                result["value"] = (False, f"无法打开流: {open_source}")
                return
            try:
                cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
            except Exception:
                pass
            deadline = time.time() + timeout_seconds
            while time.time() < deadline:
                ret, frame = cap.read()
                if ret:
                    h, w = frame.shape[:2]
                    measured_fps = DualCameraRecorder._measure_arrival_fps(cap)
                    header_fps = cap.get(cv2.CAP_PROP_FPS)
                    cap.release()
                    header_text = f"; stream header {header_fps:.1f} fps" if 0 < header_fps <= 120 else ""
                    info = f"{w}x{h} @ {measured_fps:.1f} fps measured{header_text}"
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
        """打开一路相机并确定分辨率/帧率。

        网络流在子线程里打开：UDP 地址没有数据时 cap.read() 会永久阻塞，
        主线程必须限时等待，才能把"这一路没有画面"变成一条明确的报错，
        而不是让采集界面无限期停在"等待画面"。
        """
        is_network = self._is_network_source(source)
        if is_network:
            # 归一化：裸地址 / https 自签证书 -> 找 OpenCV 可读的流地址
            normalized, _ = DualCameraRecorder.normalize_stream_url(source, timeout_seconds=4)
            return self._open_network_camera(normalized, name, idx)

        cap = cv2.VideoCapture(source)
        if not cap.isOpened():
            raise RuntimeError(f"无法打开摄像头 {name}: {source}")
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 1920)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 1080)
        cap.set(cv2.CAP_PROP_FPS, self.target_fps)
        # 读取一帧确认可用并获取真实分辨率/帧率
        ret, frame = cap.read()
        if not ret:
            cap.release()
            raise RuntimeError(f"摄像头 {name} 无画面输出: {source}")
        h, w = frame.shape[:2]
        reported_fps = cap.get(cv2.CAP_PROP_FPS)
        fps = reported_fps if 0 < reported_fps <= 120 else self.target_fps
        self._fps[idx] = fps
        self._clocks[idx] = FrameClock(fps, FrameClock.detect_scale(cap, fps))
        self._publish_first_frame(idx, frame)
        print(f"  {name}: {w}x{h} @ {fps:.1f} fps")
        return cap

    def _open_network_camera(self, source, name, idx):
        """在子线程里打开网络流，主线程最多等 open_timeout 秒。"""
        import threading

        box = {}
        abandoned = threading.Event()

        def _worker():
            cap = None
            try:
                cap = self._open_network_capture(source)
                if not cap.isOpened():
                    box["error"] = _describe_udp_failure(source)
                    return
                try:
                    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
                except Exception:
                    pass
                ret, frame = cap.read()
                if not ret:
                    box["error"] = f"没有画面输出: {source}"
                    return
                # 首帧先发布给 WPF：后续测量帧率还要花一秒多，
                # 不能让用户在这段时间里看不到任何画面。
                self._publish_first_frame(idx, frame)
                reported_fps = cap.get(cv2.CAP_PROP_FPS)
                measured = self._measure_arrival_fps(cap)
                if abandoned.is_set():
                    return
                fps = measured if 0 < measured <= 120 else (
                    reported_fps if 0 < reported_fps <= 120 else self.target_fps)
                # 时间戳单位随流的 time_base 变化，用实测帧率反推
                scale = FrameClock.detect_scale(cap, fps)
                if abandoned.is_set():
                    return
                self._fps[idx] = fps
                box["cap"] = cap
                box["frame"] = frame
                box["fps"] = fps
                box["scale"] = scale
                cap = None      # 所有权移交给调用方
            except Exception as exc:
                box["error"] = str(exc)
            finally:
                if cap is not None:
                    cap.release()

        worker = threading.Thread(target=_worker, daemon=True, name=f"open-{name}")
        worker.start()
        worker.join(self.open_timeout)
        if worker.is_alive():
            abandoned.set()
            raise RuntimeError(
                f"{name}相机 {self.open_timeout:.0f}s 内没有收到画面: {source}；"
                "请确认推流已开始、端口与地址一致，或先点「测试连接」检查这一路。")
        if box.get("error"):
            raise RuntimeError(f"无法打开{name}相机: {box['error']}")
        if "cap" not in box:
            raise RuntimeError(f"{name}相机没有可用画面: {source}")

        h, w = box["frame"].shape[:2]
        self._fps[idx] = box["fps"]
        self._clocks[idx] = FrameClock(box["fps"], box["scale"])
        print(f"  {name}: {w}x{h} @ {box['fps']:.1f} fps "
              f"(时间戳单位 1/{box['scale']:.0f}s)")
        return box["cap"]

    def clock(self, idx):
        """该路的流内时间戳换算器（FrameClock）；未打开时为 None。"""
        return self._clocks[idx]

    def _publish_first_frame(self, idx, frame):
        """相机刚打开时立刻发布首帧，让用户在点击采集后马上看到画面。"""
        if frame is None or not getattr(frame, "size", 0):
            return
        self._frame_buffers[idx].clear()
        self._frame_buffers[idx].append(frame.copy())
        self._publish_preview(force=True)

    def _preview_tiles(self, size=(640, 360)):
        """取左右最新帧的缩略图；某一路还没有画面时用黑帧占位，避免越界。"""
        tiles = []
        for i in range(2):
            frame = self._frame_buffers[i][-1] if self._frame_buffers[i] else None
            if frame is None or not getattr(frame, "size", 0):
                tiles.append(np.zeros((size[1], size[0], 3), dtype=np.uint8))
            else:
                tiles.append(cv2.resize(frame, size))
        return tiles

    def _publish_preview(self, force=False):
        """把最近一帧左右画面编码成 JPEG 写盘，供 WPF 端定时读取显示。

        预览通道用本地文件轮换（非 socket/HTTP）：WPF 以 ~20fps 读这两个
        JPEG 即可实时看到画面，不额外占用网络、不受防火墙/证书影响，也与
        "正在录制的流"同源。仅当构造时传入 preview_dir 才发布。分辨率按
        preview_size 缩放（默认 1280x720），检测/录制仍用原始帧，互不影响。
        发布按 preview_interval 限频，避免 JPEG 编码拖慢采集线程。
        """
        if self.preview_dir is None:
            return
        now = time.monotonic()
        if not force and now - self._last_preview_at < self.preview_interval:
            return
        self._last_preview_at = now
        try:
            if not self._preview_dir_ready:
                self.preview_dir.mkdir(parents=True, exist_ok=True)
                self._preview_dir_ready = True
            w, h = int(self.preview_size[0]), int(self.preview_size[1])
            for i, name in enumerate(("left", "right")):
                if not self._frame_buffers[i]:
                    continue
                frame = self._frame_buffers[i][-1]
                if frame is None or not frame.size:
                    continue
                # 等比缩放：预览要用来判断取景，不能改变画面比例
                frame = fit_scale(frame, (w, h))
                path = self.preview_dir / f"live_preview_{name}.jpg"
                # imwrite 不保证原子写；用临时文件+replace 避免 WPF 读到半张图
                tmp = path.with_suffix(".tmp.jpg")
                if cv2.imwrite(str(tmp), frame, [int(cv2.IMWRITE_JPEG_QUALITY), 80]):
                    os.replace(str(tmp), str(path))
        except Exception:
            # 预览失败不应阻断录制
            pass

    def start(self):
        self.output_dir.mkdir(parents=True, exist_ok=True)
        control = ControlChannel()
        # 界面驱动时不弹本机预览窗口：画面已经在界面里显示
        show_window = self.preview and control.interactive
        print(f"\n{'='*50}\n  双摄像头在线录制\n{'='*50}")
        print(f"  左: {self.source_left}  右: {self.source_right}")
        if self.record_seconds:
            print(f"  无人值守录制 {self.record_seconds:.0f}s\n{'='*50}")
        elif show_window:
            print(f"  按 ENTER 开始录制，按 Q 停止\n{'='*50}")
        else:
            print(f"  在界面上点「开始分析」后开始录制\n{'='*50}")
        print("\n打开摄像头...")
        try:
            self._caps[0] = self._open_camera(self.source_left, "左", 0)
            self._caps[1] = self._open_camera(self.source_right, "右", 1)
        except Exception:
            # 一路失败时另一路可能已经打开，先释放再抛出，
            # 别让相机/端口被这个进程一直占着。
            self._cleanup()
            raise

        if self.record_seconds is None:
            hint = "（或在本窗口按 ENTER）" if show_window else ""
            print(f"\n预览中：确认画面后点界面「开始分析」{hint} ...")
            while True:
                for i in range(2):
                    ret, frame = self._caps[i].read()
                    if ret:
                        self._frame_buffers[i].append(frame.copy())
                # 预览阶段也要发布画面：点了开始就该立刻看到画面，
                # 而不是等到按 ENTER / 点按钮开始录制才出现画面。
                self._publish_preview()
                commands = control.poll()
                if "stop" in commands:
                    self._cleanup()
                    return False
                if control.closed and not commands:
                    # 界面已经退出：立刻结束，别把 UDP 端口留着继续抢包
                    print("控制通道已关闭（界面已退出），结束预览")
                    self._cleanup()
                    return False
                if "start" in commands:
                    break
                if show_window:
                    f0, f1 = self._preview_tiles()
                    cv2.imshow("预览", np.hstack([f0, f1]))
                    key = cv2.waitKey(1) & 0xFF
                    if key == 13:
                        break
                    if key == ord('q'):
                        self._cleanup()
                        return False

        fourcc = cv2.VideoWriter_fourcc(*'mp4v')
        for i, name in enumerate(["left", "right"]):
            frame = self._frame_buffers[i][-1] if len(self._frame_buffers[i]) else None
            h, w = frame.shape[:2] if frame is not None else (1080, 1920)
            self._final_paths[i] = self.output_dir / name / "recording.mp4"
            # VideoWriter 靠文件扩展名推断容器，中间文件必须仍是 .mp4，
            # 所以放到 .partial/ 子目录而不是改成 .part 后缀。
            self._partial_paths[i] = self.output_dir / ".partial" / name / "recording.mp4"
            self._final_paths[i].parent.mkdir(parents=True, exist_ok=True)
            self._partial_paths[i].parent.mkdir(parents=True, exist_ok=True)
            # 上次中断可能留下半成品，先清掉，避免被当成一份可用录制
            for stale in (self._final_paths[i], self._partial_paths[i]):
                try:
                    if stale.exists():
                        stale.unlink()
                except OSError:
                    pass
            self._writers[i] = cv2.VideoWriter(
                str(self._partial_paths[i]), fourcc, self._fps[i], (w, h))
            if not self._writers[i].isOpened():
                raise RuntimeError(f"无法创建录制文件: {self._partial_paths[i]}")

        print("\n录制中... 在界面点「停止分析」，或按 Q 停止")
        self._recording = True
        self._start_time = time.time()
        print("[CONTROL] analyzing=1")
        deadline = (self._start_time + self.record_seconds) if self.record_seconds else None
        while self._recording:
            for i in range(2):
                ret, frame = self._caps[i].read()
                if ret:
                    self._writers[i].write(frame)
                    self._frame_counts[i] += 1
                    self._frame_buffers[i].append(frame.copy())
            # 向 WPF 预览发布最近一帧（若已配置 preview_dir）；限频，不阻塞录制
            self._publish_preview()
            if show_window:
                f0, f1 = self._preview_tiles()
                elapsed = time.time() - self._start_time
                cv2.putText(c := np.hstack([f0, f1]), f"录制 {elapsed:.1f}s Q=停止", (10, 30),
                            cv2.FONT_HERSHEY_SIMPLEX, 0.8, (0, 0, 255), 2)
                cv2.imshow("录制", c)
            commands = control.poll()
            if "stop" in commands:
                self._recording = False
            elif control.closed and not commands:
                print("控制通道已关闭（界面已退出），停止录制并收尾")
                self._recording = False
            if show_window and (cv2.waitKey(1) & 0xFF) == ord('q'):
                self._recording = False
            if deadline is not None and time.time() >= deadline:
                print(f"  已达到设定时长 {self.record_seconds:.0f}s，自动停止")
                self._recording = False

        print("[CONTROL] analyzing=0")
        self._finish_recording(success=True)
        self._cleanup()
        return True

    def _finish_recording(self, success):
        """收尾写盘：只有成功结束才把中间文件改名为最终 recording.mp4。

        录制被取消或进程被杀时，半成品留在 .partial/ 里，不会以损坏 mp4 的
        形式出现在 left/right 目录（那种文件读不出，还会让"跳过录制"的
        判断误以为样本已经录好）。
        """
        for i, writer in enumerate(self._writers):
            if writer is not None:
                writer.release()
                self._writers[i] = None
        for i in range(2):
            partial = self._partial_paths[i]
            final = self._final_paths[i]
            if partial is None or final is None:
                continue
            try:
                if success and partial.exists() and partial.stat().st_size > 0:
                    os.replace(str(partial), str(final))
                elif partial.exists():
                    partial.unlink()
            except OSError as exc:
                print(f"  录制收尾失败: {exc}")
            self._partial_paths[i] = None
            self._final_paths[i] = None
        # 中间目录空了就删掉，别在样本目录里留下空壳
        partial_root = self.output_dir / ".partial"
        try:
            for child in partial_root.glob("*"):
                try:
                    child.rmdir()
                except OSError:
                    pass
            partial_root.rmdir()
        except OSError:
            pass

    def _cleanup(self):
        self._recording = False
        # 走到这里说明没有正常收尾，丢弃中间文件
        self._finish_recording(success=False)
        for c in self._caps:
            if c: c.release()
        cv2.destroyAllWindows()
        e = time.time() - self._start_time if self._start_time else 0
        print(f"\n录制: {e:.1f}s, 帧: {self._frame_counts}")
        print(f"保存到: {self.output_dir}")
