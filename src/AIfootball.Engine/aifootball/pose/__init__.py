"""C2：现有视角姿态 → 点球方向预测基线。

模块划分（与 `docs/design-individual-training-loop.md` §3 的实验设计一一对应）：

    dataset.py      3DSP 读取、触球帧估计、标签（射正/射偏）
    body_frame.py   身体坐标系归一化与运动学特征
    synthetic.py    以 3DSP 真实骨骼为运动源的合成数据生成（含域随机化）
    model.py        轻量时序编码器（GRU）+ 方向分类头
    train.py        训练循环（CPU）
    evaluate.py     时间截断评估、校准、AUC、混淆矩阵
    experiment.py   组 1 / 1b / 2 的编排与报告

为什么整个包都在 `aifootball` 里而不是独立工程：
    C2 的产物要能被 `aifootball pose ...` 调用、被主引擎复用（后续把预测注入
    Unity 门将），并且跟着仓库一起测试；独立工程会让路径与依赖再次分叉。
"""
from __future__ import annotations

__all__ = ["dataset", "body_frame", "synthetic", "model", "train", "evaluate", "experiment"]
