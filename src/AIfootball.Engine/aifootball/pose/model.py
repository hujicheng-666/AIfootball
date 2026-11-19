"""轻量时序编码器（CPU 可训练，参数量 < 0.1 M）。

为什么是 GRU 而不是 TCN/Transformer：
    C2 要回答的是"姿态里有没有方向信息"这个**可行性**问题，样本量只有 200 个
    源动作（合成样本虽多但高度相关）。在这种规模下，归纳偏置强、参数少的
    循环网络比注意力模型更不容易过拟合，而且 CPU 上单次实验几分钟就能跑完，
    便于把 5 个 Δt × 3 个实验组 × 多折交叉验证全部做完。
    模型不是本阶段的交付重点；如果可行性成立，模型容量才是下一步的事。
"""
from __future__ import annotations

import torch
import torch.nn as nn

from .body_frame import FEATURE_DIM


class PoseEncoder(nn.Module):
    """把 [B, T, F] 姿态序列编码成 [B, hidden*2] 向量。"""

    def __init__(self, feature_dim: int = FEATURE_DIM, hidden: int = 64, dropout: float = 0.1):
        super().__init__()
        # 输入特征量纲混杂（归一化坐标 ±2、角度 0–π、Δt 0–1），先做逐维标准化
        self.norm = nn.LayerNorm(feature_dim)
        self.gru = nn.GRU(feature_dim, hidden, batch_first=True)
        self.dropout = nn.Dropout(dropout)
        self.output_dim = hidden * 2

    def forward(self, features: torch.Tensor) -> torch.Tensor:
        normalized = self.norm(features)
        output, _ = self.gru(normalized)          # [B,T,hidden]
        # 末帧 + 全窗平均：末帧携带"触球瞬间"的信息，平均抑制单帧检测跳变
        pooled = torch.cat([output[:, -1], output.mean(dim=1)], dim=1)
        return self.dropout(pooled)


class PoseDirectionNet(nn.Module):
    """三分类方向头（左 / 中 / 右）。"""

    def __init__(
        self,
        feature_dim: int = FEATURE_DIM,
        hidden: int = 64,
        num_classes: int = 3,
        dropout: float = 0.1,
    ):
        super().__init__()
        self.encoder = PoseEncoder(feature_dim, hidden, dropout)
        self.head = nn.Sequential(
            nn.Linear(self.encoder.output_dim, 64),
            nn.ReLU(),
            nn.Dropout(dropout),
            nn.Linear(64, num_classes),
        )

    def forward(self, features: torch.Tensor) -> torch.Tensor:
        return self.head(self.encoder(features))


class PoseBinaryNet(nn.Module):
    """二分类头（射正 / 射偏），用于组 2 的真实数据迁移。

    `from_pretrained` 只搬运编码器：方向头的语义（左/中/右）与射正/射偏无关，
    强行复用会把无关的决策边界带进来。
    """

    def __init__(
        self,
        feature_dim: int = FEATURE_DIM,
        hidden: int = 64,
        dropout: float = 0.1,
        freeze_encoder: bool = False,
    ):
        super().__init__()
        self.encoder = PoseEncoder(feature_dim, hidden, dropout)
        self.head = nn.Linear(self.encoder.output_dim, 1)
        if freeze_encoder:
            self.freeze_encoder()

    def freeze_encoder(self) -> None:
        for parameter in self.encoder.parameters():
            parameter.requires_grad = False

    def unfreeze_encoder(self) -> None:
        for parameter in self.encoder.parameters():
            parameter.requires_grad = True

    def forward(self, features: torch.Tensor) -> torch.Tensor:
        return self.head(self.encoder(features)).squeeze(-1)   # logits [B]

    @classmethod
    def from_pretrained(
        cls,
        state_dict: dict,
        feature_dim: int = FEATURE_DIM,
        hidden: int = 64,
        dropout: float = 0.1,
        freeze_encoder: bool = False,
    ) -> "PoseBinaryNet":
        model = cls(feature_dim, hidden, dropout, freeze_encoder=freeze_encoder)
        encoder_state = {
            key: value
            for key, value in state_dict.items()
            if key.startswith("encoder.")
        }
        missing, unexpected = model.load_state_dict(encoder_state, strict=False)
        # 只允许缺 head.*（本来就要换头）；编码器缺权重说明预训练权重不匹配
        missing = [name for name in missing if not name.startswith("head.")]
        if missing or unexpected:
            raise ValueError(f"预训练权重不匹配: missing={missing} unexpected={unexpected}")
        return model


def count_parameters(model: nn.Module) -> int:
    """可训练参数量（报告里要交代模型规模）。"""
    return sum(p.numel() for p in model.parameters() if p.requires_grad)
