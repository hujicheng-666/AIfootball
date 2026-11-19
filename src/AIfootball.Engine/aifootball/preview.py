"""预览发布：等比缩放 + 后台线程编码写盘。

两条设计约束：

1. **按原始比例缩放**。手机流常见 2448x1080 这类超宽画面，套进 16:9 画布
   会既变形又浪费画面；预览正是用来判断取景的，必须保持原始比例，只按
   "不超过目标尺寸"缩小，尺寸随比例走。
2. **不阻塞采集**。JPEG 编码一帧 1280 宽的图要十几毫秒，30fps 下就是三分之
   一的时间；读帧一旦被拖慢，UDP 缓冲就溢出丢包，而 HEVC 丢一个参考帧就是
   满屏花屏。所以采集/分析循环只做一次极轻的"放入最新帧"，编码在后台线程
   按固定节奏取走；队列只保留最新一帧——界面要的是"现在"，不是补播历史。
"""
import os
import threading
import time
from pathlib import Path

import cv2


def fit_scale(frame, max_size):
    """按源画面比例缩放到不超过 max_size，尺寸随比例走、不补黑边。"""
    max_w, max_h = int(max_size[0]), int(max_size[1])
    height, width = frame.shape[:2]
    if height <= 0 or width <= 0:
        return frame
    scale = min(max_w / width, max_h / height)
    return cv2.resize(frame, (max(1, int(round(width * scale))),
                              max(1, int(round(height * scale)))))


class PreviewPublisher:
    """把最新一帧发布成 JPEG 供界面读取；None 目录表示关闭。"""

    def __init__(self, preview_dir, preview_size=(1280, 720), interval=0.05, quality=80):
        self.preview_dir = Path(preview_dir) if preview_dir else None
        self.preview_size = (int(preview_size[0]), int(preview_size[1]))
        self.interval = max(0.01, float(interval))
        self.quality = int(quality)
        self._frames = {}
        self._lock = threading.Lock()
        self._wake = threading.Event()
        self._stop = False
        self._dir_ready = False
        self._thread = None
        if self.preview_dir is not None:
            self._thread = threading.Thread(target=self._run, daemon=True,
                                            name="preview-publisher")
            self._thread.start()

    @property
    def enabled(self):
        return self.preview_dir is not None

    def submit(self, side, frame):
        """投递该路最新帧；只保留一帧、不做拷贝，立即返回。"""
        if self.preview_dir is None or frame is None or not getattr(frame, "size", 0):
            return
        with self._lock:
            self._frames[side] = frame
        self._wake.set()

    def _run(self):
        while not self._stop:
            self._wake.wait(self.interval)
            self._wake.clear()
            with self._lock:
                pending, self._frames = self._frames, {}
            for side, frame in pending.items():
                self._write(side, frame)
            if pending:
                time.sleep(self.interval)

    def _write(self, side, frame):
        try:
            if not self._dir_ready:
                self.preview_dir.mkdir(parents=True, exist_ok=True)
                self._dir_ready = True
            fitted = fit_scale(frame, self.preview_size)
            path = self.preview_dir / f"live_preview_{side}.jpg"
            # imwrite 不保证原子写；临时文件 + replace，避免界面读到半张图
            tmp = path.with_suffix(".tmp.jpg")
            if cv2.imwrite(str(tmp), fitted, [int(cv2.IMWRITE_JPEG_QUALITY), self.quality]):
                os.replace(str(tmp), str(path))
        except Exception:
            # 预览失败不应影响采集
            pass

    def close(self):
        self._stop = True
        self._wake.set()
        if self._thread is not None:
            self._thread.join(timeout=1.0)
