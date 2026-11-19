"""训练循环（CPU）与温度标定。

两个刻意的设计：
1. **训练时逐样本随机采样 Δt**：否则模型只会拟合某一个提前量，评估曲线会
   出现"没训过的 Δt 反而更好"的假象。随机采样让同一个模型在所有 Δt 上都被
   训练过，条件化通道（特征最后一维）才有意义。
2. **验证集按源 shot 划分**：同一个源动作的 10 个变体骨架几乎一样，如果变体
   跨了训练/验证，指标会被泄漏抬到接近 100%，实验就失去判别力。
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Iterable, Optional, Sequence

import numpy as np
import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Dataset

from .body_frame import FEATURE_DIM, WINDOW, extract_window
from .model import PoseBinaryNet, PoseDirectionNet, count_parameters
from .synthetic import SyntheticSample

#: 评估用的提前量档位（毫秒）
DEFAULT_DELTAS_MS: tuple[int, ...] = (0, 100, 200, 300, 400)


@dataclass
class TrainConfig:
    epochs: int = 40
    batch_size: int = 64
    learning_rate: float = 3e-3
    weight_decay: float = 1e-4
    hidden: int = 64
    dropout: float = 0.1
    patience: int = 10          # 验证损失连续多少轮不降就停
    seed: int = 20261019
    window: int = WINDOW
    feature_dim: int = FEATURE_DIM
    deltas_ms: tuple[int, ...] = DEFAULT_DELTAS_MS
    num_classes: int = 3
    threads: int = 4


@dataclass
class TrainResult:
    model: PoseDirectionNet
    temperature: float
    history: list[dict] = field(default_factory=list)
    best_epoch: int = 0
    best_val_loss: float = float("inf")
    parameter_count: int = 0


class DirectionDataset(Dataset):
    """按需生成时间窗特征，避免把 5 个 Δt × N 个样本全部展开进内存。"""

    def __init__(
        self,
        samples: Sequence[SyntheticSample],
        deltas_ms: Sequence[int] = DEFAULT_DELTAS_MS,
        window: int = WINDOW,
        rotate_delta: bool = False,
        seed: int = 0,
    ):
        self.samples = list(samples)
        self.deltas_ms = tuple(float(value) for value in deltas_ms)
        self.window = int(window)
        self.rotate_delta = bool(rotate_delta)
        self.seed = int(seed)
        self.epoch = 0

    def set_epoch(self, epoch: int) -> None:
        self.epoch = int(epoch)

    def __len__(self) -> int:
        return len(self.samples)

    def delta_for(self, index: int) -> float:
        if not self.rotate_delta or len(self.deltas_ms) == 1:
            return self.deltas_ms[0]
        # 由 (样本, epoch) 决定：同一轮里每个样本的 Δt 固定，跨轮变化
        rng = np.random.default_rng(self.seed + index * 7919 + self.epoch * 104729)
        return float(rng.choice(self.deltas_ms))

    def __getitem__(self, index: int):
        sample = self.samples[index]
        features = extract_window(
            sample.frames,
            sample.kick_frame,
            self.delta_for(index),
            sample.frame_step_ms,
            self.window,
        )
        return torch.from_numpy(features), int(sample.label)


def _run_epoch(
    model: PoseDirectionNet,
    loader: DataLoader,
    criterion: nn.Module,
    optimizer: Optional[torch.optim.Optimizer],
) -> tuple[float, float]:
    training = optimizer is not None
    model.train(training)
    total_loss = 0.0
    total_correct = 0
    total = 0
    with torch.set_grad_enabled(training):
        for features, labels in loader:
            logits = model(features)
            loss = criterion(logits, labels)
            if training:
                optimizer.zero_grad()
                loss.backward()
                nn.utils.clip_grad_norm_(model.parameters(), 5.0)
                optimizer.step()
            total_loss += float(loss.item()) * labels.shape[0]
            total_correct += int((logits.argmax(dim=1) == labels).sum())
            total += int(labels.shape[0])
    if total == 0:
        return float("nan"), float("nan")
    return total_loss / total, total_correct / total


@torch.no_grad()
def collect_logits(
    model: PoseDirectionNet,
    samples: Sequence[SyntheticSample],
    delta_ms: float,
    window: int = WINDOW,
) -> tuple[np.ndarray, np.ndarray]:
    """在**固定** Δt 上收集 logits 与标签（标定与评估都要求固定 Δt）。"""
    model.eval()
    if not samples:
        return np.zeros((0, 3), dtype=np.float32), np.zeros((0,), dtype=np.int64)
    features = np.stack(
        [
            extract_window(
                sample.frames, sample.kick_frame, delta_ms, sample.frame_step_ms, window
            )
            for sample in samples
        ]
    )
    logits = model(torch.from_numpy(features)).numpy()
    labels = np.array([sample.label for sample in samples], dtype=np.int64)
    return logits.astype(np.float64), labels


def train_direction_model(
    train_samples: Sequence[SyntheticSample],
    val_samples: Sequence[SyntheticSample],
    config: TrainConfig = TrainConfig(),
    verbose: bool = False,
) -> TrainResult:
    """训练三分类方向模型，并在验证集的 Δt=0 上做温度标定。"""
    torch.manual_seed(config.seed)
    np.random.seed(config.seed)
    torch.set_num_threads(max(1, int(config.threads)))

    train_dataset = DirectionDataset(
        train_samples, config.deltas_ms, config.window, rotate_delta=True, seed=config.seed
    )
    val_dataset = DirectionDataset(
        val_samples, (0.0,), config.window, rotate_delta=False, seed=config.seed
    )
    train_loader = DataLoader(train_dataset, batch_size=config.batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=config.batch_size, shuffle=False)

    model = PoseDirectionNet(config.feature_dim, config.hidden, config.num_classes, config.dropout)
    criterion = nn.CrossEntropyLoss()
    optimizer = torch.optim.AdamW(
        model.parameters(), lr=config.learning_rate, weight_decay=config.weight_decay
    )
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=max(1, config.epochs))

    result = TrainResult(model=model, temperature=1.0, parameter_count=count_parameters(model))
    best_state = {key: value.clone() for key, value in model.state_dict().items()}
    stale = 0

    for epoch in range(max(1, config.epochs)):
        train_dataset.set_epoch(epoch)
        train_loss, train_accuracy = _run_epoch(model, train_loader, criterion, optimizer)
        val_loss, val_accuracy = _run_epoch(model, val_loader, criterion, None)
        scheduler.step()
        result.history.append(
            {
                "epoch": epoch,
                "train_loss": train_loss,
                "train_accuracy": train_accuracy,
                "val_loss": val_loss,
                "val_accuracy": val_accuracy,
            }
        )
        if verbose:
            print(
                f"epoch {epoch:3d} train_loss={train_loss:.4f} acc={train_accuracy:.3f}"
                f" | val_loss={val_loss:.4f} acc={val_accuracy:.3f}"
            )
        if val_loss < result.best_val_loss - 1e-4:
            result.best_val_loss = val_loss
            result.best_epoch = epoch
            best_state = {key: value.clone() for key, value in model.state_dict().items()}
            stale = 0
        else:
            stale += 1
            if stale >= config.patience:
                break

    model.load_state_dict(best_state)
    logits, labels = collect_logits(model, val_samples, 0.0, config.window)
    result.temperature = fit_temperature(logits, labels)
    return result


def fit_temperature(logits: np.ndarray, labels: np.ndarray) -> float:
    """在验证集上最小化 NLL 拟合单个温度参数（Guo et al. 2017）。"""
    if logits.shape[0] < 8:
        return 1.0
    logit_tensor = torch.from_numpy(np.asarray(logits, dtype=np.float64))
    label_tensor = torch.from_numpy(np.asarray(labels, dtype=np.int64))
    log_temperature = torch.zeros(1, dtype=torch.float64, requires_grad=True)
    criterion = nn.CrossEntropyLoss()
    optimizer = torch.optim.LBFGS([log_temperature], lr=0.1, max_iter=60)

    def closure():
        optimizer.zero_grad()
        temperature = torch.exp(log_temperature).clamp(0.05, 20.0)
        loss = criterion(logit_tensor / temperature, label_tensor)
        loss.backward()
        return loss

    optimizer.step(closure)
    return float(torch.exp(log_temperature).clamp(0.05, 20.0).item())


def apply_temperature(logits: np.ndarray, temperature: float) -> np.ndarray:
    """logits → 校准后的概率。"""
    scaled = np.asarray(logits, dtype=np.float64) / max(float(temperature), 1e-6)
    scaled -= scaled.max(axis=1, keepdims=True)
    exp = np.exp(scaled)
    return exp / exp.sum(axis=1, keepdims=True)


def save_checkpoint(path, result: TrainResult, config: TrainConfig, extra: Optional[dict] = None) -> None:
    """保存权重 + 温度 + 配置（便于复现与后续迁移实验）。"""
    payload = {
        "state_dict": result.model.state_dict(),
        "temperature": result.temperature,
        "feature_dim": config.feature_dim,
        "hidden": config.hidden,
        "num_classes": config.num_classes,
        "dropout": config.dropout,
        "window": config.window,
        "deltas_ms": list(config.deltas_ms),
        "parameter_count": result.parameter_count,
    }
    if extra:
        payload.update(extra)
    torch.save(payload, path)


def load_checkpoint(path) -> tuple[PoseDirectionNet, dict]:
    """载入权重并重建模型。"""
    payload = torch.load(path, map_location="cpu", weights_only=False)
    model = PoseDirectionNet(
        payload.get("feature_dim", FEATURE_DIM),
        payload.get("hidden", 64),
        payload.get("num_classes", 3),
        payload.get("dropout", 0.1),
    )
    model.load_state_dict(payload["state_dict"])
    model.eval()
    return model, payload


# --------------------------------------------------------------------------
# 组 2：真实 3DSP 数据上的射正/射偏二分类（含合成预训练迁移）
# --------------------------------------------------------------------------


@dataclass
class BinaryTrainConfig:
    epochs: int = 60
    batch_size: int = 32
    learning_rate: float = 1e-3
    weight_decay: float = 1e-4
    patience: int = 12
    seed: int = 20261019
    window: int = WINDOW
    feature_dim: int = FEATURE_DIM
    deltas_ms: tuple[int, ...] = DEFAULT_DELTAS_MS
    hidden: int = 64
    dropout: float = 0.1
    freeze_encoder: bool = False
    threads: int = 4
    #: 正类权重：3DSP 是 110 射正 / 90 射偏，轻微不平衡
    positive_weight: Optional[float] = None


@dataclass
class BinaryTrainResult:
    model: PoseBinaryNet
    temperature: float
    history: list[dict] = field(default_factory=list)
    best_epoch: int = 0
    best_val_loss: float = float("inf")
    parameter_count: int = 0


def _binary_epoch(
    model: PoseBinaryNet,
    loader: DataLoader,
    criterion: nn.Module,
    optimizer: Optional[torch.optim.Optimizer],
) -> tuple[float, float]:
    training = optimizer is not None
    model.train(training)
    total_loss = 0.0
    total_correct = 0
    total = 0
    with torch.set_grad_enabled(training):
        for features, labels in loader:
            logits = model(features)
            targets = labels.to(torch.float32)
            loss = criterion(logits, targets)
            if training:
                optimizer.zero_grad()
                loss.backward()
                nn.utils.clip_grad_norm_(
                    [p for p in model.parameters() if p.requires_grad], 5.0
                )
                optimizer.step()
            total_loss += float(loss.item()) * labels.shape[0]
            total_correct += int(((logits > 0).long() == labels).sum())
            total += int(labels.shape[0])
    if total == 0:
        return float("nan"), float("nan")
    return total_loss / total, total_correct / total


@torch.no_grad()
def collect_binary_logits(
    model: PoseBinaryNet,
    samples: Sequence[SyntheticSample],
    delta_ms: float,
    window: int = WINDOW,
) -> tuple[np.ndarray, np.ndarray]:
    """在固定 Δt 上收集二分类 logits 与标签。"""
    model.eval()
    if not samples:
        return np.zeros((0,)), np.zeros((0,), dtype=np.int64)
    features = np.stack(
        [
            extract_window(sample.frames, sample.kick_frame, delta_ms, sample.frame_step_ms, window)
            for sample in samples
        ]
    )
    logits = model(torch.from_numpy(features)).numpy().astype(np.float64)
    labels = np.array([sample.label for sample in samples], dtype=np.int64)
    return logits, labels


def train_binary_model(
    train_samples: Sequence[SyntheticSample],
    val_samples: Sequence[SyntheticSample],
    config: BinaryTrainConfig = BinaryTrainConfig(),
    pretrained_state: Optional[dict] = None,
    verbose: bool = False,
) -> BinaryTrainResult:
    """训练射正/射偏二分类模型；给了 `pretrained_state` 就用它初始化编码器。"""
    torch.manual_seed(config.seed)
    np.random.seed(config.seed)
    torch.set_num_threads(max(1, int(config.threads)))

    train_dataset = DirectionDataset(
        train_samples, config.deltas_ms, config.window, rotate_delta=True, seed=config.seed
    )
    val_dataset = DirectionDataset(
        val_samples, (0.0,), config.window, rotate_delta=False, seed=config.seed
    )
    train_loader = DataLoader(train_dataset, batch_size=config.batch_size, shuffle=True)
    val_loader = DataLoader(val_dataset, batch_size=config.batch_size, shuffle=False)

    if pretrained_state:
        model = PoseBinaryNet.from_pretrained(
            pretrained_state,
            config.feature_dim,
            config.hidden,
            config.dropout,
            freeze_encoder=config.freeze_encoder,
        )
    else:
        model = PoseBinaryNet(
            config.feature_dim, config.hidden, config.dropout,
            freeze_encoder=config.freeze_encoder,
        )

    weight = config.positive_weight
    criterion = nn.BCEWithLogitsLoss(
        pos_weight=None if weight is None else torch.tensor(float(weight))
    )
    parameters = [p for p in model.parameters() if p.requires_grad]
    optimizer = torch.optim.AdamW(parameters, lr=config.learning_rate, weight_decay=config.weight_decay)
    scheduler = torch.optim.lr_scheduler.CosineAnnealingLR(optimizer, T_max=max(1, config.epochs))

    result = BinaryTrainResult(model=model, temperature=1.0, parameter_count=count_parameters(model))
    best_state = {key: value.clone() for key, value in model.state_dict().items()}
    stale = 0

    for epoch in range(max(1, config.epochs)):
        train_dataset.set_epoch(epoch)
        train_loss, train_accuracy = _binary_epoch(model, train_loader, criterion, optimizer)
        val_loss, val_accuracy = _binary_epoch(model, val_loader, criterion, None)
        scheduler.step()
        result.history.append(
            {
                "epoch": epoch,
                "train_loss": train_loss,
                "train_accuracy": train_accuracy,
                "val_loss": val_loss,
                "val_accuracy": val_accuracy,
            }
        )
        if verbose:
            print(
                f"epoch {epoch:3d} train_loss={train_loss:.4f} acc={train_accuracy:.3f}"
                f" | val_loss={val_loss:.4f} acc={val_accuracy:.3f}"
            )
        if val_loss < result.best_val_loss - 1e-4:
            result.best_val_loss = val_loss
            result.best_epoch = epoch
            best_state = {key: value.clone() for key, value in model.state_dict().items()}
            stale = 0
        else:
            stale += 1
            if stale >= config.patience:
                break

    model.load_state_dict(best_state)
    logits, labels = collect_binary_logits(model, val_samples, 0.0, config.window)
    result.temperature = fit_temperature_binary(logits, labels)
    return result


def fit_temperature_binary(logits: np.ndarray, labels: np.ndarray) -> float:
    """二分类温度标定（对 logits 做单参数缩放，最小化 BCE）。"""
    if logits.shape[0] < 8:
        return 1.0
    logit_tensor = torch.from_numpy(np.asarray(logits, dtype=np.float64))
    target = torch.from_numpy(np.asarray(labels, dtype=np.float64))
    log_temperature = torch.zeros(1, dtype=torch.float64, requires_grad=True)
    criterion = nn.BCEWithLogitsLoss()
    optimizer = torch.optim.LBFGS([log_temperature], lr=0.1, max_iter=60)

    def closure():
        optimizer.zero_grad()
        temperature = torch.exp(log_temperature).clamp(0.05, 20.0)
        loss = criterion(logit_tensor / temperature, target)
        loss.backward()
        return loss

    optimizer.step(closure)
    return float(torch.exp(log_temperature).clamp(0.05, 20.0).item())


def binary_probabilities(logits: np.ndarray, temperature: float = 1.0) -> np.ndarray:
    """二分类 logits → 校准后的正类概率。"""
    scaled = np.asarray(logits, dtype=np.float64) / max(float(temperature), 1e-6)
    return 1.0 / (1.0 + np.exp(-scaled))
