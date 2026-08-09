"""球体检测模块 — 基于 YOLO 的足球检测"""
from pathlib import Path

import cv2
import numpy as np
from ultralytics import YOLO

SPORTS_BALL_CLASS_ID = 32


class BallDetector:
    """YOLO 球体检测器"""

    def __init__(self, model_path: str | Path, imgsz: int = 1280, conf: float = 0.15):
        self.model = YOLO(str(model_path))
        self.imgsz = imgsz
        self.conf = conf

    def detect(self, frame: np.ndarray):
        """检测单帧中的足球，返回 (cx, cy, confidence) 或 None"""
        results = self.model(frame, imgsz=self.imgsz, conf=self.conf, verbose=False)
        if results[0].boxes is None:
            return None

        best = None
        best_conf = 0.0
        for box in results[0].boxes:
            cls_id = int(box.cls[0])
            if cls_id != SPORTS_BALL_CLASS_ID:
                continue
            c = float(box.conf[0])
            if c > best_conf:
                best_conf = c
                xyxy = box.xyxy[0].cpu().numpy()
                cx = (xyxy[0] + xyxy[2]) / 2
                cy = (xyxy[1] + xyxy[3]) / 2
                best = (cx, cy, c)
        return best

    def detect_batch(self, frames: list[np.ndarray]):
        """批量检测多帧"""
        return [self.detect(f) for f in frames]
