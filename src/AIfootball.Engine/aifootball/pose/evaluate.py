"""评估指标与 Δt 条件化评估。

C2 的主指标是**校准误差**而不是准确率：门将的决策（扑哪边、要不要提前移动）
依赖的是概率，一个"准确率 70% 但过度自信"的模型在实战里比"准确率 65% 但
校准良好"的更危险。所以每个 Δt 档位都同时报告准确率、log loss 与 ECE，
并按 design 文档的要求画"准确率随提前量"的单调曲线。
"""
from __future__ import annotations

from dataclasses import asdict, dataclass
from typing import Sequence

import numpy as np
import torch

from .body_frame import WINDOW, extract_window
from .model import PoseDirectionNet, PoseBinaryNet
from .synthetic import CLASS_NAMES, SyntheticSample
from .train import apply_temperature


def _rankdata(values: np.ndarray) -> np.ndarray:
    """平均秩（处理并列），用于 Mann-Whitney U 形式的 AUC。"""
    values = np.asarray(values, dtype=np.float64)
    order = np.argsort(values, kind="mergesort")
    ranks = np.empty(len(values), dtype=np.float64)
    sorted_values = values[order]
    index = 0
    while index < len(values):
        end = index
        while end + 1 < len(values) and sorted_values[end + 1] == sorted_values[index]:
            end += 1
        average = (index + end) / 2.0 + 1.0
        ranks[order[index : end + 1]] = average
        index = end + 1
    return ranks


def auc_binary(scores: np.ndarray, labels: np.ndarray) -> float:
    """ROC-AUC（0.5 = 无判别力）。"""
    scores = np.asarray(scores, dtype=np.float64)
    labels = np.asarray(labels, dtype=np.int64)
    positive = scores[labels == 1]
    negative = scores[labels == 0]
    if positive.size == 0 or negative.size == 0:
        return float("nan")
    ranks = _rankdata(scores)
    rank_sum = float(ranks[labels == 1].sum())
    return (rank_sum - positive.size * (positive.size + 1) / 2.0) / (
        positive.size * negative.size
    )


def expected_calibration_error(
    probabilities: np.ndarray, labels: np.ndarray, bins: int = 15
) -> float:
    """多分类 ECE：按最大概率分箱，衡量 |准确率 − 置信度| 的加权平均。"""
    probabilities = np.asarray(probabilities, dtype=np.float64)
    labels = np.asarray(labels, dtype=np.int64)
    if probabilities.size == 0:
        return float("nan")
    confidence = probabilities.max(axis=1)
    prediction = probabilities.argmax(axis=1)
    correct = (prediction == labels).astype(np.float64)
    edges = np.linspace(0.0, 1.0, bins + 1)
    total = len(labels)
    error = 0.0
    for lower, upper in zip(edges[:-1], edges[1:]):
        if upper >= 1.0:
            mask = (confidence > lower) & (confidence <= upper)
        else:
            mask = (confidence > lower) & (confidence <= upper)
        count = int(mask.sum())
        if count == 0:
            continue
        error += (count / total) * abs(correct[mask].mean() - confidence[mask].mean())
    return float(error)


def log_loss(probabilities: np.ndarray, labels: np.ndarray) -> float:
    """多分类交叉熵（概率已被裁剪，避免 log(0)）。"""
    probabilities = np.asarray(probabilities, dtype=np.float64)
    labels = np.asarray(labels, dtype=np.int64)
    if probabilities.size == 0:
        return float("nan")
    picked = probabilities[np.arange(len(labels)), labels]
    return float(-np.log(np.clip(picked, 1e-12, 1.0)).mean())


def confusion_matrix(prediction: np.ndarray, labels: np.ndarray, num_classes: int) -> list[list[int]]:
    """行 = 真值，列 = 预测。"""
    matrix = np.zeros((num_classes, num_classes), dtype=np.int64)
    for truth, guess in zip(np.asarray(labels).ravel(), np.asarray(prediction).ravel()):
        if 0 <= truth < num_classes and 0 <= guess < num_classes:
            matrix[int(truth), int(guess)] += 1
    return matrix.tolist()


def macro_f1(prediction: np.ndarray, labels: np.ndarray, num_classes: int) -> float:
    """宏平均 F1（类别不平衡时比准确率更能暴露问题）。"""
    matrix = np.array(confusion_matrix(prediction, labels, num_classes), dtype=np.float64)
    scores = []
    for index in range(num_classes):
        true_positive = matrix[index, index]
        false_positive = matrix[:, index].sum() - true_positive
        false_negative = matrix[index, :].sum() - true_positive
        denominator = 2 * true_positive + false_positive + false_negative
        scores.append(0.0 if denominator == 0 else 2 * true_positive / denominator)
    return float(np.mean(scores))


@dataclass
class DeltaMetrics:
    """单个 Δt 档位下的完整指标。"""

    delta_ms: float
    sample_count: int
    accuracy: float
    log_loss: float
    ece: float
    macro_f1: float
    mean_confidence: float
    usable_frames: float
    confusion: list[list[int]]
    per_class_accuracy: list[float]

    def to_dict(self) -> dict:
        return asdict(self)


@torch.no_grad()
def collect_logits_at_delta(
    model: PoseDirectionNet,
    samples: Sequence[SyntheticSample],
    delta_ms: float,
    window: int = WINDOW,
    batch_size: int = 256,
) -> tuple[np.ndarray, np.ndarray]:
    """在固定 Δt 上收集 logits 与标签。"""
    if not samples:
        return np.zeros((0, 3)), np.zeros((0,), dtype=np.int64)
    features = np.stack(
        [
            extract_window(sample.frames, sample.kick_frame, delta_ms, sample.frame_step_ms, window)
            for sample in samples
        ]
    )
    model.eval()
    chunks = []
    for start in range(0, features.shape[0], batch_size):
        chunk = torch.from_numpy(features[start : start + batch_size])
        chunks.append(model(chunk).numpy())
    logits = np.concatenate(chunks, axis=0).astype(np.float64)
    labels = np.array([sample.label for sample in samples], dtype=np.int64)
    return logits, labels


def usable_frames(kick_frame: int, delta_ms: float, frame_step_ms: float) -> float:
    """在给定提前量下，触球前还有多少帧真实数据（用于解释曲线）。"""
    return max(0.0, (kick_frame + 1) - float(delta_ms) / max(float(frame_step_ms), 1e-6))


def evaluate_at_deltas(
    model: PoseDirectionNet,
    samples: Sequence[SyntheticSample],
    deltas_ms: Sequence[float],
    temperature: float = 1.0,
    window: int = WINDOW,
    num_classes: int = 3,
) -> list[DeltaMetrics]:
    """按 Δt 逐档评估（校准后的概率用于 ECE / log loss）。"""
    results: list[DeltaMetrics] = []
    for delta_ms in deltas_ms:
        logits, labels = collect_logits_at_delta(model, samples, float(delta_ms), window)
        if labels.size == 0:
            continue
        probabilities = apply_temperature(logits, temperature)
        prediction = probabilities.argmax(axis=1)
        frames = np.array(
            [usable_frames(sample.kick_frame, delta_ms, sample.frame_step_ms) for sample in samples]
        )
        per_class = []
        for index in range(num_classes):
            mask = labels == index
            per_class.append(
                float((prediction[mask] == index).mean()) if mask.any() else float("nan")
            )
        results.append(
            DeltaMetrics(
                delta_ms=float(delta_ms),
                sample_count=int(labels.size),
                accuracy=float((prediction == labels).mean()),
                log_loss=log_loss(probabilities, labels),
                ece=expected_calibration_error(probabilities, labels),
                macro_f1=macro_f1(prediction, labels, num_classes),
                mean_confidence=float(probabilities.max(axis=1).mean()),
                usable_frames=float(frames.mean()),
                confusion=confusion_matrix(prediction, labels, num_classes),
                per_class_accuracy=per_class,
            )
        )
    return results


def class_names(num_classes: int = 3) -> tuple[str, ...]:
    """类别名（三分类固定为 左/中/右）。"""
    if num_classes == len(CLASS_NAMES):
        return CLASS_NAMES
    return tuple(f"类{index}" for index in range(num_classes))


@dataclass
class BinaryMetrics:
    """组 2（真实数据射正/射偏）的指标。"""

    sample_count: int
    positive_rate: float
    accuracy: float
    log_loss: float
    auc: float
    brier: float
    ece: float
    confusion: list[list[int]]

    def to_dict(self) -> dict:
        return asdict(self)


def binary_metrics(probabilities: np.ndarray, labels: np.ndarray) -> BinaryMetrics:
    """二分类指标；`probabilities` 是"射正"的概率。"""
    probabilities = np.asarray(probabilities, dtype=np.float64).ravel()
    labels = np.asarray(labels, dtype=np.int64).ravel()
    if labels.size == 0:
        return BinaryMetrics(0, float("nan"), float("nan"), float("nan"), float("nan"),
                             float("nan"), float("nan"), [[0, 0], [0, 0]])
    prediction = (probabilities >= 0.5).astype(np.int64)
    two_column = np.stack([1.0 - probabilities, probabilities], axis=1)
    return BinaryMetrics(
        sample_count=int(labels.size),
        positive_rate=float(labels.mean()),
        accuracy=float((prediction == labels).mean()),
        log_loss=log_loss(two_column, labels),
        auc=auc_binary(probabilities, labels),
        brier=float(np.mean((probabilities - labels) ** 2)),
        ece=expected_calibration_error(two_column, labels),
        confusion=confusion_matrix(prediction, labels, 2),
    )


@torch.no_grad()
def binary_scores_at_delta(
    model: PoseBinaryNet,
    samples: Sequence[SyntheticSample],
    delta_ms: float,
    window: int = WINDOW,
) -> np.ndarray:
    """二分类模型在固定 Δt 上的射正概率。"""
    if not samples:
        return np.zeros((0,), dtype=np.float64)
    features = np.stack(
        [
            extract_window(sample.frames, sample.kick_frame, delta_ms, sample.frame_step_ms, window)
            for sample in samples
        ]
    )
    model.eval()
    logits = model(torch.from_numpy(features)).numpy().astype(np.float64)
    return 1.0 / (1.0 + np.exp(-logits))
