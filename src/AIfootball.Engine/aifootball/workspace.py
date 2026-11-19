"""工作区路径上下文 —— 惰性解析，取代原先 import 期固定的 `Path.cwd()`。

为什么不再用模块级 `WORKSPACE = Path.cwd()`：
    `Path.cwd()` 在 import 那一刻就被定死，进程中途切换工作目录（或界面
    传入 `--workspace`）都不会再生效；各个算法模块还各自缓存了一份派生
    路径（`output/trajectory_3d`、`calib`……），于是"工作区"到底指哪里，
    取决于谁先被 import，非常难排查。

现在的约定：
    - 任何模块都不在 import 期解析工作区，只在真正用到路径时调用
      `get_workspace()`；
    - 显式设置优先：CLI 解析完 `--workspace` 会先 `set_workspace()`，
      此后本进程固定使用该目录，不再受 cwd / 环境变量影响；
    - 未显式设置时惰性解析：环境变量 `AIFOOTBALL_WORKSPACE` 优先，
      其次当前工作目录（与旧的 `Path.cwd()` 行为一致）。

目录布局（与发布目录约定一致）::

    <workspace>/
      ├── calib/                      相机标定
      ├── samples/                    视频样本（每个样本一个目录）
      ├── models/                     YOLO 权重
      ├── data/                       Unity CSV 输出
      └── output/
          ├── trajectory_3d/          3D 重建结果
          ├── trajectory_ballistic/   弹道拟合结果
          ├── track_cache/            2D 检测缓存
          └── live_preview/           界面实时预览 JPEG
"""
from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Optional

#: 未显式设置工作区时读取的环境变量
WORKSPACE_ENV = "AIFOOTBALL_WORKSPACE"
#: YOLO 权重环境变量（优先于工作区内的 models/ 目录）
YOLO_MODEL_ENV = "YOLO_MODEL_PATH"
#: 工作区 models/ 下的默认权重文件名
DEFAULT_YOLO_MODEL = "yolo11m.pt"


@dataclass(frozen=True)
class Workspace:
    """一处工作区的全部路径。所有子路径都由 `root` 派生，不做任何缓存。"""

    root: Path

    def __post_init__(self):
        # 统一成绝对路径：后续拼接、比较、传给 OpenCV/FFmpeg 才不会因 cwd 变化而漂移
        object.__setattr__(self, "root", Path(self.root).expanduser().resolve())

    @property
    def calib(self) -> Path:
        """相机标定目录（intrinsics_*.npz / *_pose.npz / *_extrinsics.json）。"""
        return self.root / "calib"

    @property
    def samples(self) -> Path:
        """视频样本根目录。"""
        return self.root / "samples"

    @property
    def output(self) -> Path:
        """所有算法中间产物 / 可视化结果的根目录。"""
        return self.root / "output"

    @property
    def models(self) -> Path:
        """模型权重目录。"""
        return self.root / "models"

    @property
    def data(self) -> Path:
        """Unity 回放 CSV 输出目录。"""
        return self.root / "data"

    @property
    def trajectory_3d(self) -> Path:
        """3D 重建输出目录（重建的输入由弹道拟合读取）。"""
        return self.output / "trajectory_3d"

    @property
    def trajectory_ballistic(self) -> Path:
        """弹道拟合输出目录（Unity 导出的输入）。"""
        return self.output / "trajectory_ballistic"

    @property
    def track_cache(self) -> Path:
        """2D 检测缓存目录：同一视频+参数的检测结果复用，避免重复推理。"""
        return self.output / "track_cache"

    @property
    def live_preview(self) -> Path:
        """界面实时预览 JPEG 目录（live_preview_left.jpg / live_preview_right.jpg）。"""
        return self.output / "live_preview"

    def yolo_model_path(self) -> Path:
        """YOLO 权重路径：环境变量 `YOLO_MODEL_PATH` 优先，否则 `<workspace>/models/yolo11m.pt`。

        保留环境变量优先的语义：界面会把随发行包安装的权重路径通过该变量传入，
        此时不能再用工作区内的路径覆盖它。
        """
        override = os.environ.get(YOLO_MODEL_ENV)
        if override:
            return Path(override).expanduser()
        return self.models / DEFAULT_YOLO_MODEL


# 显式设置的工作区：一旦设置就固定下来（None = 未设置，走惰性解析）
_PINNED: Optional[Workspace] = None


def set_workspace(path) -> Workspace:
    """显式设置本进程的工作区并固定下来，返回解析后的 `Workspace`。"""
    global _PINNED
    _PINNED = Workspace(Path(path))
    return _PINNED


def get_workspace() -> Workspace:
    """取当前工作区。

    惰性解析顺序：
      1. `set_workspace()` 显式设置过的目录（优先级最高，且此后不再变化）；
      2. 环境变量 `AIFOOTBALL_WORKSPACE`；
      3. 当前工作目录（与旧 `Path.cwd()` 行为一致）。
    """
    if _PINNED is not None:
        return _PINNED
    env_root = os.environ.get(WORKSPACE_ENV)
    if env_root:
        return Workspace(Path(env_root))
    return Workspace(Path.cwd())


def reset_workspace() -> None:
    """清除显式设置，回到惰性解析（测试与嵌入式复用场景使用）。"""
    global _PINNED
    _PINNED = None
