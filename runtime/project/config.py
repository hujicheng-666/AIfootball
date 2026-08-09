"""
共享路径 — exe 发布目录结构:
  FootballTrajectory.exe
  ├── calib/          ← 相机标定
  ├── samples/        ← 视频样本
  └── My project/     ← Unity CSV 输出
"""
from pathlib import Path

WORKSPACE = Path.cwd()
CALIB = WORKSPACE / "calib"
SAMPLES = WORKSPACE / "samples"

def init(workspace_dir):
    global WORKSPACE, CALIB, SAMPLES
    WORKSPACE = Path(workspace_dir).resolve()
    CALIB = WORKSPACE / "calib"
    SAMPLES = WORKSPACE / "samples"
