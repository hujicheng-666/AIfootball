"""
AIfootball Engine — 足球轨迹分析核心算法库
============================================
提供: 相机标定 | 球体检测 | 3D重建 | 弹道拟合 | 轨迹导出

包布局::

    aifootball/
      ├── cli.py           命令行入口（argparse 子命令）
      ├── workspace.py     工作区路径上下文（惰性解析）
      ├── constants.py     共享物理/场地常量
      ├── control.py       采集进程控制通道（stdin 行协议）
      ├── preview.py       预览发布（等比缩放 + 后台编码线程）
      ├── capture/         双摄采集（DualCameraRecorder）
      ├── calibration/     内参 / 外参标定
      └── pipeline/        3D 重建 → 弹道拟合 → Unity 导出 / 实时分析
"""

__version__ = "2.0.0"
__author__ = "AIfootball Team"

__all__ = ["__version__", "__author__"]
