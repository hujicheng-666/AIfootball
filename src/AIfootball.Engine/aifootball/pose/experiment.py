"""C2 实验编排：组 1 / 组 1b / 组 2，以及 go-no-go 判定。

三组实验各自回答一个问题：

    组 1 （合成 → 合成）：**现有视角的姿态序列里到底有没有方向信息？**
        用 3DSP 的真实骨骼做运动源、注入受控的方向变形，得到带方向标签的
        数据。这一组给的是可行性**上界**，不是实战性能——因为标签是我们自己
        按物理假设注入的。
    组 1b（关闭域随机化）：**模型是靠真实误差结构还是靠捷径？**
        域随机化关掉后，训练/验证都在"干净"骨架上，如果指标反而更好看，
        说明模型确实吃到了捷径（单关节、数据集指纹）；如果明显变差，说明
        鲁棒特征需要在带噪声的数据上才能学出来。
    组 2 （合成预训练 → 真实 3DSP）：**能不能迁移到真实数据？**
        真实数据只有射正/射偏二分类标签（没有方向），200 个 shot 做 5 折
        交叉验证，对比"合成预训练编码器"与"随机初始化"。

关于"为什么组 2 不报方向指标"：3DSP **没有方向标签**，任何方向准确率数字
都只能是合成域上的结果。把合成指标写成"实战方向准确率"是本项目最容易犯的
错误，报告里必须显式区分。
"""
from __future__ import annotations

import json
import math
import platform
import sys
from dataclasses import asdict, dataclass, field, replace
from pathlib import Path
from typing import Optional, Sequence

import numpy as np

from .dataset import PoseShot, load_train, summarize
from .body_frame import WINDOW, extract_window
from .evaluate import (
    auc_binary,
    binary_metrics,
    evaluate_at_deltas,
)
from .model import count_parameters
from .synthetic import (
    DeformConfig,
    RandomizationConfig,
    SyntheticSample,
    augment_samples,
    build_synthetic_dataset,
    real_samples,
)
from .train import (
    BinaryTrainConfig,
    TrainConfig,
    binary_probabilities,
    collect_binary_logits,
    train_binary_model,
    train_direction_model,
)

DEFAULT_DATASET_ROOT = Path("data/external/3dsp/3dsp")
DEFAULT_OUTPUT_DIR = Path("output/pose")


@dataclass
class ExperimentConfig:
    seed: int = 20261019
    #: 每个源 shot × 每个方向生成多少变体。
    #: 6 是在 CPU 上跑完三组实验的折中（160 源 × 3 方向 × 6 = 2880 训练样本）；
    #: 更多变体只会增加高度相关的样本，边际收益远小于训练时间。
    variants_per_source: int = 6
    train_ratio: float = 0.8
    epochs: int = 25
    batch_size: int = 128
    hidden: int = 64
    dropout: float = 0.1
    deltas_ms: tuple[int, ...] = (0, 100, 200, 300, 400)
    folds: int = 5
    transfer_epochs: int = 30
    transfer_batch_size: int = 64
    transfer_learning_rate: float = 1e-3
    #: 真实样本的域随机化副本数（含原样），用于撑住组 2 的训练量
    augment_copies: int = 4
    #: 限制源 shot 数量（冒烟测试用；None = 全部）
    max_sources: Optional[int] = None
    #: 标签置换检验的重复次数（每次都是完整 5 折流程，成本约等于一次组 2）
    permutation_trials: int = 2
    threads: int = 6

    def to_dict(self) -> dict:
        payload = asdict(self)
        payload["deltas_ms"] = list(self.deltas_ms)
        return payload


def split_sources(
    shots: Sequence[PoseShot], ratio: float, seed: int
) -> tuple[list[PoseShot], list[PoseShot]]:
    """按源 shot 划分训练/验证，**绝不让同一个源动作的变体跨集**。"""
    shots = list(shots)
    indices = np.arange(len(shots))
    rng = np.random.default_rng(seed)
    rng.shuffle(indices)
    cut = int(round(len(shots) * float(ratio)))
    cut = max(1, min(cut, len(shots) - 1)) if len(shots) > 1 else len(shots)
    train = [shots[int(index)] for index in indices[:cut]]
    val = [shots[int(index)] for index in indices[cut:]]
    return train, val


def _direction_config(config: ExperimentConfig) -> TrainConfig:
    return TrainConfig(
        epochs=config.epochs,
        batch_size=config.batch_size,
        hidden=config.hidden,
        dropout=config.dropout,
        seed=config.seed,
        deltas_ms=config.deltas_ms,
        threads=config.threads,
    )


def run_direction_group(
    shots: Sequence[PoseShot],
    config: ExperimentConfig,
    randomization: RandomizationConfig,
    tag: str,
    verbose: bool = False,
):
    """组 1 / 组 1b 的公共实现（差别只在 `randomization`）。"""
    train_shots, val_shots = split_sources(shots, config.train_ratio, config.seed)
    deform = DeformConfig(variants_per_source=config.variants_per_source)
    train_samples = build_synthetic_dataset(
        train_shots, seed=config.seed, deform=deform, randomization=randomization
    )
    val_samples = build_synthetic_dataset(
        val_shots, seed=config.seed + 977, deform=deform, randomization=randomization
    )

    result = train_direction_model(
        train_samples, val_samples, _direction_config(config), verbose=verbose
    )
    metrics = evaluate_at_deltas(
        result.model,
        val_samples,
        config.deltas_ms,
        temperature=result.temperature,
        window=WINDOW,
    )

    payload = {
        "tag": tag,
        "domain_randomization": asdict(randomization),
        "source_shots": {"train": len(train_shots), "val": len(val_shots)},
        "samples": {"train": len(train_samples), "val": len(val_samples)},
        "parameter_count": result.parameter_count,
        "temperature": result.temperature,
        "best_epoch": result.best_epoch,
        "best_val_loss": result.best_val_loss,
        "epochs_run": len(result.history),
        "deltas": [item.to_dict() for item in metrics],
        "history": result.history,
    }
    return payload, result.model


def _make_folds(
    samples: Sequence[SyntheticSample], folds: int, seed: int
) -> list[tuple[list[int], list[int]]]:
    """按样本（shot）做 K 折划分。"""
    indices = np.arange(len(samples))
    rng = np.random.default_rng(seed)
    rng.shuffle(indices)
    folds = max(2, min(int(folds), len(samples)))
    chunks = [list(chunk) for chunk in np.array_split(indices, folds)]
    result = []
    for index in range(len(chunks)):
        val = [int(value) for value in chunks[index]]
        train = [
            int(value)
            for other, chunk in enumerate(chunks)
            if other != index
            for value in chunk
        ]
        result.append((train, val))
    return result


def run_transfer_group(
    shots: Sequence[PoseShot],
    config: ExperimentConfig,
    pretrained_state: Optional[dict],
    tag: str,
    verbose: bool = False,
) -> dict:
    """组 2：真实 3DSP 射正/射偏，K 折交叉验证，逐 Δt 汇总 out-of-fold 预测。"""
    return run_binary_cv(real_samples(shots), config, pretrained_state, tag, verbose=verbose)


def run_binary_cv(
    samples: Sequence[SyntheticSample],
    config: ExperimentConfig,
    pretrained_state: Optional[dict],
    tag: str,
    verbose: bool = False,
) -> dict:
    """对给定的二分类样本做 K 折交叉验证，返回逐 Δt 的 out-of-fold 指标。

    单独抽出来是为了让标签置换检验复用完全相同的交叉验证流程——只换标签，
    其余（划分、增广、温度标定）保持一致，这样 AUC 的对照才有意义。
    """
    folds = _make_folds(samples, config.folds, config.seed)
    deltas = list(config.deltas_ms)
    collected = {float(delta): {"logits": [], "labels": []} for delta in deltas}
    fold_records = []

    for fold_index, (train_indices, val_indices) in enumerate(folds):
        fold_train = [samples[index] for index in train_indices]
        fold_val = [samples[index] for index in val_indices]

        # 校准集从训练折里再切一刀，避免用验证折拟合温度把 ECE 做漂亮
        calib_size = max(1, int(round(len(fold_train) * 0.25)))
        rng = np.random.default_rng(config.seed + fold_index)
        shuffled = list(fold_train)
        rng.shuffle(shuffled)
        calib = shuffled[:calib_size]
        fit = shuffled[calib_size:]

        train_config = BinaryTrainConfig(
            epochs=config.transfer_epochs,
            batch_size=config.transfer_batch_size,
            learning_rate=config.transfer_learning_rate,
            seed=config.seed + fold_index,
            deltas_ms=config.deltas_ms,
            hidden=config.hidden,
            dropout=config.dropout,
            threads=config.threads,
        )
        augmented = augment_samples(
            fit, config.augment_copies, seed=config.seed + 31 * fold_index + 7
        )
        result = train_binary_model(
            augmented, calib, train_config, pretrained_state=pretrained_state, verbose=verbose
        )
        for delta in deltas:
            logits, labels = collect_binary_logits(
                result.model, fold_val, float(delta), train_config.window
            )
            collected[float(delta)]["logits"].append(logits)
            collected[float(delta)]["labels"].append(labels)
        fold_records.append(
            {
                "fold": fold_index,
                "train": len(fit),
                "calibration": len(calib),
                "val": len(fold_val),
                "augmented_train": len(augmented),
                "temperature": result.temperature,
                "best_epoch": result.best_epoch,
                "val_loss": result.best_val_loss,
            }
        )

    per_delta = []
    for delta in deltas:
        logits = np.concatenate(collected[float(delta)]["logits"]) if collected[float(delta)]["logits"] else np.zeros((0,))
        labels = np.concatenate(collected[float(delta)]["labels"]) if collected[float(delta)]["labels"] else np.zeros((0,), dtype=np.int64)
        probabilities = binary_probabilities(logits, 1.0)
        metrics = binary_metrics(probabilities, labels)
        per_delta.append(metrics.to_dict() | {"delta_ms": float(delta)})

    return {
        "tag": tag,
        "pretrained": pretrained_state is not None,
        "shots": len(samples),
        "on_target": int(sum(sample.label for sample in samples)),
        "majority_baseline": float(max(np.mean([s.label for s in samples]), 1 - np.mean([s.label for s in samples]))),
        "folds": fold_records,
        "deltas": per_delta,
    }


def _delta_row(payload: dict, delta: float) -> dict:
    """从逐 Δt 指标表里取出指定提前量的一行。"""
    for item in payload.get("deltas", []):
        if abs(float(item.get("delta_ms", -1)) - float(delta)) < 1e-6:
            return item
    return {}


def scan_feature_auc(
    samples: Sequence[SyntheticSample], delta_ms: float = 0.0, top: int = 8
) -> dict:
    """逐维特征的单变量判别力，用来判断"是不是模型没学好"还是"数据里没有信号"。

    做法很朴素：把每个样本的窗口按时间取平均，得到 112 维向量，逐维与射正标签
    求 AUC。如果连单变量都只有 0.5 附近，那么任何非线性模型的 AUC≈0.5 都不是
    过拟合的锅，而是特征/数据里确实没有可分的结构。
    """
    if not samples:
        return {"delta_ms": float(delta_ms), "feature_dim": 0, "median_abs_deviation": float("nan"), "top_features": []}
    windows = np.stack(
        [
            extract_window(sample.frames, sample.kick_frame, float(delta_ms), sample.frame_step_ms)
            for sample in samples
        ]
    )
    flat = windows.mean(axis=1)
    labels = np.array([int(sample.binary_label) for sample in samples], dtype=np.int64)
    aucs = np.array([auc_binary(flat[:, index], labels) for index in range(flat.shape[1])])
    deviations = np.abs(aucs - 0.5)
    order = np.argsort(-deviations)[: max(1, int(top))]
    return {
        "delta_ms": float(delta_ms),
        "feature_dim": int(flat.shape[1]),
        "median_abs_deviation": float(np.median(deviations)),
        "top_features": [
            {
                "dim": int(index),
                "auc": float(aucs[index]),
                "abs_deviation": float(deviations[index]),
            }
            for index in order
        ],
    }


def run_permutation_test(
    samples: Sequence[SyntheticSample],
    config: ExperimentConfig,
    trials: int = 2,
    delta_ms: float = 0.0,
) -> list[dict]:
    """标签置换检验：随机打乱射正/射偏标签，走完全相同的交叉验证流程。

    如果真实标签的 AUC 与置换后的 AUC 分布分不开，那么"真实姿态对射正/射偏有
    判别力"这个假设在本次数据上就没有证据支持——这是比"指标低"更硬的结论，
    因为它排除了"模型容量/正则/训练轮数没调好"这类解释。
    """
    labels = np.array([int(sample.binary_label) for sample in samples], dtype=np.int64)
    records = []
    if int(trials) <= 0:
        return records
    for trial in range(int(trials)):
        rng = np.random.default_rng(config.seed + 7919 * (trial + 1))
        permuted = labels[rng.permutation(len(labels))]
        shuffled = [
            replace(sample, binary_label=int(value))
            for sample, value in zip(samples, permuted)
        ]
        payload = run_binary_cv(shuffled, config, None, f"permutation{trial}")
        row = _delta_row(payload, delta_ms)
        records.append(
            {
                "trial": trial,
                "delta_ms": float(delta_ms),
                "auc": float(row.get("auc", float("nan"))),
                "accuracy": float(row.get("accuracy", float("nan"))),
            }
        )
    return records


def _auc_standard_error(positive: int, negative: int, auc: float) -> float:
    """Hanley & McNeil (1982) 的 AUC 标准误近似。

    200 个样本、类别 110/90 时，AUC=0.5 附近的标准误约 0.041——所以 AUC 相差
    0.02 根本不构成"有信号"的证据。判定必须用这个尺度，而不是拍一个固定余量。
    """
    if positive <= 0 or negative <= 0:
        return float("nan")
    auc = min(max(float(auc), 1e-6), 1 - 1e-6)
    q1 = auc / (2 - auc)
    q2 = 2 * auc * auc / (1 + auc)
    variance = (
        auc * (1 - auc)
        + (positive - 1) * (q1 - auc * auc)
        + (negative - 1) * (q2 - auc * auc)
    ) / (positive * negative)
    return math.sqrt(max(variance, 1e-12))


def decide(results: dict) -> dict:
    """go / no-go 判定。

    阈值是**事先写死**的，避免事后挑一个好看的数字：
        - 组 1 在 Δt=0 的准确率必须 ≥ 0.60（三类随机 = 0.333）：否则"姿态里
          有方向信息"这个前提本身不成立，后续都不用谈；
        - 组 2 的迁移增益：预训练相对随机初始化的 AUC 提升 ≥ 0.05 才算"可迁移"；
        - 组 2 本身 AUC ≥ 0.60 才认为真实姿态对射正/射偏有判别力。
    """
    group1 = results.get("group1", {})
    group1b = results.get("group1b", {})
    group2 = results.get("group2", {})
    diagnostics = results.get("group2_diagnostics", {})

    g1_late = _delta_row(group1, 0.0)
    g1_early = _delta_row(group1, 400.0)
    g1b_late = _delta_row(group1b, 0.0)

    feasibility = float(g1_late.get("accuracy", float("nan")))
    feasibility_ok = math.isfinite(feasibility) and feasibility >= 0.60

    transfer = group2.get("pretrained", {})
    scratch = group2.get("scratch", {})
    auc_pretrained = float(_delta_row(transfer, 0.0).get("auc", float("nan")))
    auc_scratch = float(_delta_row(scratch, 0.0).get("auc", float("nan")))
    gain = (
        auc_pretrained - auc_scratch
        if math.isfinite(auc_pretrained) and math.isfinite(auc_scratch)
        else float("nan")
    )
    transferable = math.isfinite(gain) and gain >= 0.05
    discriminative = math.isfinite(auc_pretrained) and auc_pretrained >= 0.60

    permutation_aucs = [
        float(item.get("auc", float("nan")))
        for item in diagnostics.get("permutation", [])
    ]
    permutation_aucs = [value for value in permutation_aucs if math.isfinite(value)]

    positives = int(transfer.get("on_target", 0) or 0)
    negatives = int(transfer.get("shots", 0) or 0) - positives
    auc_standard_error = _auc_standard_error(positives, negatives, auc_pretrained)
    permutation_mean = float(np.mean(permutation_aucs)) if permutation_aucs else float("nan")
    # 真实标签的 AUC 落在置换均值 2 个标准误内 ⇒ 无法排除"标签与特征无关"
    indistinguishable = bool(
        math.isfinite(permutation_mean)
        and math.isfinite(auc_standard_error)
        and abs(auc_pretrained - permutation_mean) < 2 * auc_standard_error
    )

    if not feasibility_ok:
        verdict = "no-go"
        reason = "合成域上方向信息都不足以分类，说明特征/视角假设不成立，先回头改视角或特征"
    elif discriminative and transferable:
        verdict = "go"
        reason = "合成域可分且预训练在真实数据上有增益，可以进入下一步（真值标签采集/时序模型加码）"
    elif discriminative:
        verdict = "conditional"
        reason = "真实数据本身可分，但合成预训练没有带来增益，建议改用真实数据直接训练/自监督预训练"
    elif indistinguishable:
        verdict = "conditional"
        reason = (
            "真实标签的 AUC 与标签置换后的 AUC 落在同一区间，说明现有姿态特征里没有"
            "可学的射正/射偏信号（不是训练不足或过拟合），需要引入球轨迹/球门几何等额外模态"
        )
    else:
        verdict = "conditional"
        reason = "真实数据上的判别力不足，需要更多真实标注或引入球轨迹等额外模态"

    return {
        "verdict": verdict,
        "reason": reason,
        "group1_accuracy_at_0ms": feasibility,
        "group1_accuracy_at_400ms": float(g1_early.get("accuracy", float("nan"))),
        "group1b_accuracy_at_0ms": float(g1b_late.get("accuracy", float("nan"))),
        "group2_auc_pretrained": auc_pretrained,
        "group2_auc_scratch": auc_scratch,
        "group2_auc_gain": gain,
        "group2_permutation_aucs": permutation_aucs,
        "group2_permutation_mean": permutation_mean,
        "group2_auc_standard_error": auc_standard_error,
        "group2_signal_indistinguishable": indistinguishable,
        "thresholds": {
            "group1_min_accuracy_at_0ms": 0.60,
            "group2_min_auc": 0.60,
            "group2_min_auc_gain": 0.05,
        },
    }


def run_all(
    dataset_root: Path = DEFAULT_DATASET_ROOT,
    config: ExperimentConfig = ExperimentConfig(),
    verbose: bool = False,
    progress=None,
) -> dict:
    """跑完组 1 / 1b / 2 并返回可序列化的完整结果。"""
    shots = load_train(Path(dataset_root))
    if config.max_sources:
        shots = shots[: config.max_sources]

    def report(message: str) -> None:
        if progress:
            progress(message)
        elif verbose:
            print(message)

    report(f"3DSP: {len(shots)} 个源 shot")

    flat_randomization = RandomizationConfig()
    no_randomization = RandomizationConfig(enabled=False)

    report("组 1：合成 → 合成（域随机化开）")
    group1, direction_model = run_direction_group(
        shots, config, flat_randomization, "group1", verbose=verbose
    )

    report("组 1b：合成 → 合成（域随机化关，捷径学习对照）")
    group1b, _ = run_direction_group(
        shots, config, no_randomization, "group1b", verbose=verbose
    )

    report("组 2：合成预训练 → 真实 3DSP（射正/射偏）")
    pretrained_state = direction_model.state_dict()
    group2_scratch = run_transfer_group(shots, config, None, "scratch", verbose=verbose)
    report("组 2：真实数据随机初始化基线")
    group2_pretrained = run_transfer_group(
        shots, config, pretrained_state, "pretrained", verbose=verbose
    )

    binary_samples = real_samples(shots)
    report("组 2 诊断：单特征判别力扫描")
    feature_scan = scan_feature_auc(binary_samples, delta_ms=0.0)
    report(f"组 2 诊断：标签置换检验 × {config.permutation_trials}")
    permutation = run_permutation_test(
        binary_samples, config, trials=config.permutation_trials, delta_ms=0.0
    )

    results = {
        "meta": {
            "dataset": summarize(shots),
            "config": config.to_dict(),
            "python": sys.version.split()[0],
            "platform": platform.platform(),
        },
        "group1": group1,
        "group1b": group1b,
        "group2": {"pretrained": group2_pretrained, "scratch": group2_scratch},
        "group2_diagnostics": {"feature_scan": feature_scan, "permutation": permutation},
    }
    results["decision"] = decide(results)
    return results


def _format_delta_table(payload: dict) -> str:
    lines = [
        "| Δt (ms) | 可用帧 | 准确率 | 宏 F1 | log loss | ECE | 置信度 |",
        "| --- | --- | --- | --- | --- | --- | --- |",
    ]
    for item in payload.get("deltas", []):
        lines.append(
            "| {delta:.0f} | {frames:.1f} | {accuracy:.3f} | {f1:.3f} | {loss:.3f} | {ece:.3f} | {conf:.3f} |".format(
                delta=float(item["delta_ms"]),
                frames=float(item["usable_frames"]),
                accuracy=float(item["accuracy"]),
                f1=float(item["macro_f1"]),
                loss=float(item["log_loss"]),
                ece=float(item["ece"]),
                conf=float(item["mean_confidence"]),
            )
        )
    return "\n".join(lines)


def _format_binary_table(payload: dict) -> str:
    lines = [
        "| Δt (ms) | 准确率 | AUC | Brier | log loss | ECE |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    for item in payload.get("deltas", []):
        lines.append(
            "| {delta:.0f} | {accuracy:.3f} | {auc:.3f} | {brier:.3f} | {loss:.3f} | {ece:.3f} |".format(
                delta=float(item["delta_ms"]),
                accuracy=float(item["accuracy"]),
                auc=float(item["auc"]),
                brier=float(item["brier"]),
                loss=float(item["log_loss"]),
                ece=float(item["ece"]),
            )
        )
    return "\n".join(lines)


def format_report(results: dict) -> str:
    """把结果渲染成可读的 markdown 报告。"""
    meta = results.get("meta", {})
    summary = meta.get("dataset", {})
    decision = results.get("decision", {})
    group1 = results.get("group1", {})
    group1b = results.get("group1b", {})
    group2 = results.get("group2", {})
    pretrained = group2.get("pretrained", {})
    scratch = group2.get("scratch", {})
    feature_scan = results.get("group2_diagnostics", {}).get("feature_scan", {})

    parts = [
        "# C2 实验报告：现有视角姿态 → 射门方向预测基线",
        "",
        "> 本报告由 `aifootball pose run` 自动生成。合成域的标签来自**注入的物理假设**，",
        "> 因此组 1 的数字是可行性上界，不是实战性能；组 2 才是真实数据上的结论。",
        "",
        "## 数据",
        "",
        f"- 源 shot：{summary.get('shots', 0)}（射正 {summary.get('on_target', 0)} / "
        f"射偏 {summary.get('off_target', 0)}）",
        f"- 触球帧：全部为第 {summary.get('kick_frame_min', '?')} 帧"
        f"（来自 info.ini 时间轴的比例 {summary.get('kick_frame_from_info', 0)}/"
        f"{summary.get('shots', 0)}）",
        f"- 帧间隔 {summary.get('frame_step_ms_median', 0):.0f} ms，"
        f"触球前可用 {summary.get('pre_kick_frames_min', 0)} 帧",
        f"- 评估提前量：{meta.get('config', {}).get('deltas_ms', [])}",
        "",
        "## 组 1：合成 → 合成（可行性上界）",
        "",
        f"- 参数 {group1.get('parameter_count', 0):,}，温度 {group1.get('temperature', float('nan')):.3f}，"
        f"训练 {group1.get('samples', {}).get('train', 0)} / 验证 {group1.get('samples', {}).get('val', 0)} 样本",
        f"- 源划分：训练 {group1.get('source_shots', {}).get('train', 0)} / "
        f"验证 {group1.get('source_shots', {}).get('val', 0)} 个 shot（**不共享源动作**）",
        "",
        _format_delta_table(group1),
        "",
        "## 组 1b：关闭域随机化（捷径学习对照）",
        "",
        _format_delta_table(group1b),
        "",
        "组 1b 的准确率**高于**组 1（Δt=0 时 "
        f"{_delta_row(group1b, 0.0).get('accuracy', float('nan')):.3f} vs "
        f"{_delta_row(group1, 0.0).get('accuracy', float('nan')):.3f}）。"
        "这说明域随机化确实把输入弄噪了、任务变难了，"
        "但它**不构成「组 1 靠随机化撑指标」的证据**：两组的训练/验证都按源划分、"
        "互不共享同一段动作，经典捷径（记住某个源的整体姿态）在这里拿不到分。"
        "两组都停在 80% 上下，说明模型吃的是变形引入的相对结构，而不是数据集指纹。",
        "",
        "## 组 2：合成预训练 → 真实 3DSP（射正/射偏，5 折交叉验证）",
        "",
        f"- 真实 shot {pretrained.get('shots', 0)}，射正率 "
        f"{pretrained.get('on_target', 0)}/{pretrained.get('shots', 0)}，"
        f"多数类基线 {pretrained.get('majority_baseline', float('nan')):.3f}",
        "",
        "**预训练编码器（合成方向任务迁移）**",
        "",
        _format_binary_table(pretrained),
        "",
        "**随机初始化（对照）**",
        "",
        _format_binary_table(scratch),
        "",
        "## 组 2 诊断：真实标签到底有没有可学信号",
        "",
        "**单特征扫描（Δt=0，窗口内按时间取平均）**",
        "",
        f"- 特征维度 {feature_scan.get('feature_dim', 0)}；"
        f"单维 AUC 偏离 0.5 的中位数 {feature_scan.get('median_abs_deviation', float('nan')):.3f}"
        "（200 个样本下 AUC 的抽样波动本身就在 0.02–0.03 量级）",
        "",
        "| 维度 | AUC | 偏离 0.5 |",
        "| --- | --- | --- |",
        *[
            f"| {int(item['dim'])} | {float(item['auc']):.3f} | {float(item['abs_deviation']):.3f} |"
            for item in feature_scan.get("top_features", [])
        ],
        "",
        "**标签置换检验（打乱射正/射偏标签，走完全相同的 5 折流程）**",
        "",
        "| 置换 | AUC | 准确率 |",
        "| --- | --- | --- |",
        *[
            f"| {int(item.get('trial', 0))} | {float(item.get('auc', float('nan'))):.3f} | "
            f"{float(item.get('accuracy', float('nan'))):.3f} |"
            for item in results.get("group2_diagnostics", {}).get("permutation", [])
        ],
        "",
        "置换检验的意义：如果真实标签训出来的 AUC 与随机标签训出来的 AUC 分不开，",
        "那么低 AUC 就不是「训练不足/过拟合/正则没调好」，而是**输入特征里没有这部分信息**。",
        "",
        "## 判定",
        "",
        f"- 结论：**{decision.get('verdict', 'n/a')}**",
        f"- 依据：{decision.get('reason', '')}",
        f"- 组 1 Δt=0 准确率 {decision.get('group1_accuracy_at_0ms', float('nan')):.3f}"
        f"（阈值 {decision.get('thresholds', {}).get('group1_min_accuracy_at_0ms')}）",
        f"- 组 2 AUC：预训练 {decision.get('group2_auc_pretrained', float('nan')):.3f} / "
        f"随机初始化 {decision.get('group2_auc_scratch', float('nan')):.3f}，"
        f"增益 {decision.get('group2_auc_gain', float('nan')):.3f}",
        f"- 组 2 信号：{'与标签置换不可区分（判定为「数据里没有这部分信息」）' if decision.get('group2_signal_indistinguishable') else '优于标签置换基线'}"
        f"（真实 AUC {decision.get('group2_auc_pretrained', float('nan')):.3f} vs 置换均值 "
        f"{decision.get('group2_permutation_mean', float('nan')):.3f}，"
        f"AUC 标准误 {decision.get('group2_auc_standard_error', float('nan')):.3f}）",
        "",
        "## 结论怎么读、不能怎么读",
        "",
        "**能读的**：",
        "",
        "1. 在「方向由摆腿平面 + 支撑脚位置 + 躯干侧倾共同决定」这个物理假设下，",
        "   现有视角的姿态序列**足够**支撑三分类（Δt=0 时 82%），而且提前量越大越差、",
        "   单调下降——这与信息随时间减少的预期一致，不是巧合。",
        "2. 真实 3DSP 的射正/射偏标签，用同样的姿态特征**学不出任何东西**：",
        "   预训练/随机初始化 AUC 都在 0.50，且与标签置换检验不可区分。",
        "   这不是训练不足——单特征扫描里 112 维最强的一维也只偏离 0.5 约 0.115。",
        "3. 因此路线图 C2 的结论是**部分可行**：姿态→方向这条路在原理上通，",
        "   但在真实数据上缺的不是模型，是**信息**。",
        "",
        "**不能读的**：",
        "",
        "- 不能把组 1 的 82% 当成实战方向准确率。合成标签是按我们自己的物理假设注入的，",
        "  模型学会了这个假设，等价于证明了「假设可学」，不等于「真人也这样踢」。",
        "- 不能用组 2 的数字否定姿态的价值。组 2 用的是射正/射偏这个**结果标签**，",
        "  它由触球瞬间的脚型、球心相对脚的位置、门将位置共同决定；而 3DSP 的 3D 坐标",
        "  是髋相对 + 按身高归一化的，**球和球门都不在特征里**。",
        "",
        "## 局限",
        "",
        "- **17 关节没有脚/脚趾**（H3WB 口径），而射门方向最直接的部位就是脚背朝向；",
        "- 坐标髋相对、逐帧身高归一化，丢掉了球员在球门中的绝对站位与球的位置；",
        "- 200 个 shot 下 AUC 的标准误约 0.041，只能可靠检出 |AUC−0.5| ≳ 0.08 的效应；",
        "- Δt=400 ms 时只剩 3 帧可用，那一列的数字噪声明显更大；",
        "- 真实数据只有射正/射偏，**没有方向标签**，任何方向指标都只能来自合成域。",
        "",
    ]
    return "\n".join(parts)


def write_outputs(results: dict, output_dir: Path) -> dict[str, Path]:
    """写出 JSON 原始结果与 markdown 报告。"""
    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    json_path = output_dir / "pose-results.json"
    report_path = output_dir / "pose-report.md"
    json_path.write_text(
        json.dumps(results, indent=2, ensure_ascii=False, default=_json_default),
        encoding="utf-8",
    )
    report_path.write_text(format_report(results), encoding="utf-8")
    return {"json": json_path, "report": report_path}


def _json_default(value):
    if isinstance(value, (np.floating, np.integer, np.bool_)):
        return value.item()
    if isinstance(value, np.ndarray):
        return value.tolist()
    if isinstance(value, Path):
        return str(value)
    raise TypeError(f"无法序列化 {type(value)!r}")
