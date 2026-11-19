"""离线/在线处理流水线：3D 重建 -> 弹道拟合 -> Unity 导出 / 实时分析。

注意：本包 __init__ 不导入子模块，避免 import 期拖入 torch/ultralytics；
各阶段按需 `from aifootball.pipeline import reconstruction` 单独导入。
"""
