"""C2 姿态方向预测的单元测试。

全部离线、不依赖真实 3DSP 数据（用临时目录造最小数据集），因为要能在
CI/健康检查里几秒跑完。真实数据的统计特性由 `aifootball pose inspect`
和实验报告覆盖，不放进单元测试。
"""
import json
import math
import tempfile
import unittest
from pathlib import Path

import numpy as np

from aifootball.pose import body_frame, dataset, evaluate, model as pose_model, synthetic, train

JOINTS = dataset.JOINT_NAMES


def _frame(offset: float = 0.0) -> dict:
    """造一帧 17 关节（坐标是身高归一化的 hip-relative 量级）。"""
    keypoints = {}
    for index, name in enumerate(JOINTS):
        keypoints[str(index)] = {
            "name": name,
            "x": 0.01 * (index % 5) + offset,
            "y": 0.02 * (index % 3),
            "z": 0.1 + 0.05 * index,
        }
    return {
        "image_path": "00001/img/1.jpg",
        "shooter_tracklet_id": 1,
        "bbox": {"x": 0, "y": 0, "w": 100, "h": 100},
        "keypoint_2d": {},
        "keypoint_3d": keypoints,
    }


def _write_shot(root: Path, shot_id: str, label: str, frames: int = 20,
                with_info: bool = True, kick_jump_at: int = -1) -> Path:
    """在临时目录里造一个最小 shot。"""
    shot_dir = root / shot_id
    (shot_dir / "posture").mkdir(parents=True, exist_ok=True)
    for index in range(frames):
        payload = _frame(offset=0.0)
        if index == kick_jump_at:
            # 制造一个踝关节速度峰值（用于验证 fallback 路径）
            payload["keypoint_3d"][str(dataset.RIGHT_ANKLE)]["x"] += 0.9
        (shot_dir / "posture" / f"{index + 1:03d}.json").write_text(
            json.dumps(payload), encoding="utf-8"
        )
    if with_info:
        (shot_dir / "info.ini").write_text(
            "[info]\n"
            f"id = {shot_id}\n"
            "start_position = 1000\n"
            "annotated_position = 1500\n"
            "end_position = 1600\n"
            "position_step = 40\n"
            f"label = {label}\n",
            encoding="utf-8",
        )
    return shot_dir


def _walking_frames(count: int = 20, seed: int = 3) -> np.ndarray:
    """造一个几何上自洽的骨架序列（髋轴水平、躯干竖直）。"""
    rng = np.random.default_rng(seed)
    frames = np.zeros((count, len(JOINTS), 3), dtype=np.float32)
    for step in range(count):
        phase = step / max(count - 1, 1)
        frames[step, dataset.HIPS] = (0.0, 0.0, 0.55)
        frames[step, dataset.LEFT_HIP] = (-0.09, 0.0, 0.55)
        frames[step, dataset.RIGHT_HIP] = (0.09, 0.0, 0.55)
        frames[step, dataset.SHOULDER] = (0.0, 0.02 * math.sin(phase * 3), 0.95)
        frames[step, dataset.NECK] = (0.0, 0.0, 0.98)
        frames[step, dataset.HEAD] = (0.0, 0.0, 1.05)
        swing = 0.35 * math.sin(phase * math.pi)
        frames[step, dataset.LEFT_KNEE] = (-0.09, 0.02, 0.28)
        frames[step, dataset.LEFT_ANKLE] = (-0.09, 0.0, 0.02)
        frames[step, dataset.RIGHT_KNEE] = (0.09, -swing, 0.30)
        frames[step, dataset.RIGHT_ANKLE] = (0.09, -swing * 1.6, 0.05 + 0.25 * swing)
    frames += rng.normal(0.0, 0.002, frames.shape).astype(np.float32)
    return frames


class DatasetTest(unittest.TestCase):
    def test_kick_frame_comes_from_goal_time_axis(self):
        with tempfile.TemporaryDirectory() as tmp:
            shot_dir = _write_shot(Path(tmp), "00001", "Shots on target")
            shot = dataset.load_shot(shot_dir)
            self.assertIsNotNone(shot)
            # 500 ms / 40 ms = 12.5 → 第 12 帧（0-based）
            self.assertEqual(shot.kick_frame, 12)
            self.assertEqual(shot.kick_frame_source, "info")
            self.assertEqual(shot.pre_kick_frames, 13)
            self.assertEqual(shot.on_target, 1)
            self.assertAlmostEqual(shot.kick_time_ms, 500.0)

    def test_off_target_label(self):
        with tempfile.TemporaryDirectory() as tmp:
            shot_dir = _write_shot(Path(tmp), "00002", "Shots off target")
            shot = dataset.load_shot(shot_dir)
            self.assertEqual(shot.on_target, 0)

    def test_unknown_label_is_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            shot_dir = _write_shot(Path(tmp), "00003", "Something else")
            self.assertIsNone(dataset.load_shot(shot_dir))

    def test_missing_posture_returns_none(self):
        with tempfile.TemporaryDirectory() as tmp:
            shot_dir = Path(tmp) / "00004"
            shot_dir.mkdir()
            (shot_dir / "info.ini").write_text(
                "[info]\nlabel = Shots on target\n", encoding="utf-8"
            )
            self.assertIsNone(dataset.load_shot(shot_dir))

    def test_falls_back_to_ankle_peak_without_info(self):
        with tempfile.TemporaryDirectory() as tmp:
            shot_dir = _write_shot(Path(tmp), "00005", "x", with_info=False, kick_jump_at=15)
            # 没有 info.ini 时标签无法确定 → 仍然返回 None（标签是硬要求）
            self.assertIsNone(dataset.load_shot(shot_dir))

    def test_load_split_and_summarize(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp) / "3dsp"
            _write_shot(root / "train", "00001", "Shots on target")
            _write_shot(root / "train", "00002", "Shots off target")
            shots = dataset.load_split(root, "train")
            self.assertEqual(len(shots), 2)
            summary = dataset.summarize(shots)
            self.assertEqual(summary["shots"], 2)
            self.assertEqual(summary["on_target"], 1)
            self.assertEqual(summary["kick_frame_from_info"], 2)
            self.assertEqual(summary["kick_frame_min"], 12)


class BodyFrameTest(unittest.TestCase):
    def test_axes_are_orthonormal(self):
        frames = _walking_frames()
        axes, scale = body_frame.body_axes(frames)
        self.assertEqual(axes.shape, (frames.shape[0], 3, 3))
        for step in range(frames.shape[0]):
            matrix = axes[step]
            np.testing.assert_allclose(matrix @ matrix.T, np.eye(3), atol=1e-6)
            np.testing.assert_allclose(matrix[1], np.cross(matrix[2], matrix[0]), atol=1e-6)
            self.assertAlmostEqual(float(np.linalg.norm(matrix[2] - np.array([0, 0, 1]))), 0.0, places=6)
        self.assertTrue(np.all(scale > 0))

    def test_shoulder_lands_on_unit_up_axis(self):
        """肩中心相对髋中心必然落在身体坐标的 (0,·,1) 位置：尺度就是躯干长。"""
        frames = _walking_frames()
        coords, _ = body_frame.normalize_sequence(frames)
        for step in range(frames.shape[0]):
            np.testing.assert_allclose(coords[step, dataset.HIPS], np.zeros(3), atol=1e-6)
            self.assertAlmostEqual(float(np.linalg.norm(coords[step, dataset.SHOULDER])), 1.0, places=5)

    def test_degenerate_hip_axis_does_not_produce_nan(self):
        frames = _walking_frames()
        frames[5, dataset.RIGHT_HIP] = frames[5, dataset.LEFT_HIP]
        axes, _ = body_frame.body_axes(frames)
        self.assertFalse(np.isnan(axes).any())
        coords, _ = body_frame.normalize_sequence(frames)
        self.assertFalse(np.isnan(coords).any())

    def test_hip_yaw_delta_starts_at_zero(self):
        frames = _walking_frames()
        yaw = body_frame.hip_yaw_delta(frames)
        self.assertAlmostEqual(float(yaw[0, 0]), 0.0, places=6)
        self.assertAlmostEqual(float(yaw[0, 1]), 0.0, places=6)

    def test_extract_window_shape_and_delta_channel(self):
        frames = _walking_frames()
        window = body_frame.extract_window(frames, 12, 100.0, 40.0, length=16)
        self.assertEqual(window.shape, (16, body_frame.FEATURE_DIM))
        np.testing.assert_allclose(window[:, -1], 0.25, atol=1e-6)
        self.assertFalse(np.isnan(window).any())

    def test_larger_delta_looks_earlier(self):
        """提前量越大，窗口应当越早结束（信息更少）。"""
        frames = _walking_frames()
        early = body_frame.extract_window(frames, 12, 200.0, 40.0, length=4)
        late = body_frame.extract_window(frames, 12, 0.0, 40.0, length=4)
        # Δt=0 时窗口末尾是第 12 帧（已在序列内），Δt=200 时末尾是第 7 帧
        self.assertFalse(np.allclose(early, late))

    def test_window_pads_when_history_is_short(self):
        frames = _walking_frames()
        window = body_frame.extract_window(frames, 1, 400.0, 40.0, length=8)
        self.assertEqual(window.shape, (8, body_frame.FEATURE_DIM))
        self.assertFalse(np.isnan(window).any())


class SyntheticTest(unittest.TestCase):
    def _shot(self, frames=None, kick_frame=12) -> dataset.PoseShot:
        frames = _walking_frames() if frames is None else frames
        return dataset.PoseShot(
            shot_id="00001",
            frames=frames,
            on_target=1,
            label_text="Shots on target",
            frame_step_ms=40.0,
            kick_frame=kick_frame,
        )

    def test_kicking_leg_follows_the_moving_ankle(self):
        frames = _walking_frames()
        self.assertEqual(synthetic.kicking_leg(frames, 12), "Right")

    def test_deformation_is_monotonic_in_direction(self):
        """三条线索同号：方向 +1 的摆动腿横向位置必须大于方向 −1。"""
        shot = self._shot()
        rng = np.random.default_rng(11)
        config = synthetic.DeformConfig()
        positions = {}
        for direction in (-1, 0, 1):
            samples = [
                synthetic.deform_for_direction(
                    shot.frames[: shot.kick_frame + 1], shot.kick_frame, direction, rng, config
                )[-1]
                for _ in range(8)
            ]
            coords, _ = body_frame.normalize_sequence(np.stack(samples))
            positions[direction] = float(coords[:, dataset.RIGHT_ANKLE, 0].mean())
        self.assertLess(positions[-1], positions[0])
        self.assertLess(positions[0], positions[1])

    def test_deformation_keeps_hips_and_shoulder_axis(self):
        """变形只动四肢与上半身横向：左右髋必须保持不动。"""
        shot = self._shot()
        rng = np.random.default_rng(5)
        deformed = synthetic.deform_for_direction(
            shot.frames, shot.kick_frame, 1, rng, synthetic.DeformConfig()
        )
        np.testing.assert_allclose(deformed[:, dataset.LEFT_HIP], shot.frames[:, dataset.LEFT_HIP], atol=1e-5)
        np.testing.assert_allclose(deformed[:, dataset.RIGHT_HIP], shot.frames[:, dataset.RIGHT_HIP], atol=1e-5)

    def test_split_matches_normalize_sequence(self):
        """`_split_body` 与 `normalize_sequence` 必须逐位一致（防止两份实现分叉）。"""
        frames = _walking_frames().astype(np.float64)
        # 造一个髋轴朝向非平凡的姿态：把整个骨架绕竖直轴转 37°
        angle = math.radians(37.0)
        rotation = np.array(
            [[math.cos(angle), -math.sin(angle), 0.0],
             [math.sin(angle), math.cos(angle), 0.0],
             [0.0, 0.0, 1.0]]
        )
        rotated = (frames - frames[:, :1]) @ rotation.T + frames[:, :1]
        expected, _ = body_frame.normalize_sequence(rotated)
        actual, _, _, _ = synthetic._split_body(rotated)
        np.testing.assert_allclose(actual, expected, atol=1e-6)

    def test_split_merge_roundtrip_is_exact(self):
        frames = _walking_frames().astype(np.float64)
        coords, axes, scale, hips = synthetic._split_body(frames)
        restored = synthetic._merge_body(coords, axes, scale, hips)
        np.testing.assert_allclose(restored, frames, atol=1e-5)

    def test_randomization_can_be_disabled(self):
        frames = _walking_frames()
        rng = np.random.default_rng(1)
        unchanged = synthetic.randomize(frames, rng, synthetic.RandomizationConfig(enabled=False))
        np.testing.assert_allclose(unchanged, frames)

    def test_randomization_changes_data_without_nan(self):
        frames = _walking_frames()
        rng = np.random.default_rng(2)
        changed = synthetic.randomize(frames, rng, synthetic.RandomizationConfig())
        self.assertFalse(np.isnan(changed).any())
        self.assertGreater(float(np.abs(changed - frames).max()), 1e-4)

    def test_build_dataset_is_reproducible_and_balanced(self):
        shots = [self._shot(), self._shot()]
        config = synthetic.DeformConfig(variants_per_source=3)
        first = synthetic.build_synthetic_dataset(shots, seed=7, deform=config)
        second = synthetic.build_synthetic_dataset(shots, seed=7, deform=config)
        self.assertEqual(len(first), 2 * 3 * 3)
        labels = [sample.label for sample in first]
        self.assertEqual(sorted(set(labels)), [0, 1, 2])
        np.testing.assert_allclose(first[0].frames, second[0].frames)

    def test_subset_by_source(self):
        shots = [self._shot()]
        samples = synthetic.build_synthetic_dataset(
            shots, seed=1, deform=synthetic.DeformConfig(variants_per_source=1)
        )
        self.assertEqual(len(synthetic.subset_by_source(samples, {"00001"})), len(samples))
        self.assertEqual(len(synthetic.subset_by_source(samples, {"99999"})), 0)


class MetricsTest(unittest.TestCase):
    def test_ece_is_zero_for_perfectly_calibrated(self):
        probabilities = np.array([[0.8, 0.1, 0.1]] * 8 + [[0.1, 0.8, 0.1]] * 2)
        labels = np.array([0] * 8 + [1] * 2)
        # 置信度 0.8，准确率 1.0（分箱后仍会有偏差），用完全一致的分箱验证零误差
        perfect = np.array([[1.0, 0.0], [0.0, 1.0]] * 5)
        perfect_labels = np.array([0, 1] * 5)
        self.assertLess(evaluate.expected_calibration_error(perfect, perfect_labels), 1e-9)
        self.assertGreater(evaluate.expected_calibration_error(probabilities, labels), 0.0)

    def test_auc_endpoints(self):
        labels = np.array([0, 0, 1, 1])
        self.assertAlmostEqual(evaluate.auc_binary(np.array([0.1, 0.2, 0.8, 0.9]), labels), 1.0)
        self.assertAlmostEqual(evaluate.auc_binary(np.array([0.9, 0.8, 0.2, 0.1]), labels), 0.0)
        self.assertAlmostEqual(evaluate.auc_binary(np.array([0.5, 0.5, 0.5, 0.5]), labels), 0.5)

    def test_auc_handles_single_class(self):
        self.assertTrue(math.isnan(evaluate.auc_binary(np.array([0.1, 0.2]), np.array([1, 1]))))

    def test_confusion_and_macro_f1(self):
        prediction = np.array([0, 1, 1])
        labels = np.array([0, 1, 2])
        matrix = evaluate.confusion_matrix(prediction, labels, 3)
        self.assertEqual(matrix[0][0], 1)
        self.assertEqual(matrix[2][1], 1)
        # 类 0 全对（F1=1）、类 1 有一次误报（F1=2/3）、类 2 全漏（F1=0）
        self.assertAlmostEqual(evaluate.macro_f1(prediction, labels, 3), (1 + 2 / 3 + 0) / 3, places=6)

    def test_log_loss_matches_hand_computation(self):
        probabilities = np.array([[0.5, 0.5], [0.25, 0.75]])
        labels = np.array([0, 1])
        expected = -(math.log(0.5) + math.log(0.75)) / 2
        self.assertAlmostEqual(evaluate.log_loss(probabilities, labels), expected, places=6)

    def test_binary_metrics_shape(self):
        probabilities = np.array([0.9, 0.2, 0.8, 0.1])
        labels = np.array([1, 0, 1, 0])
        metrics = evaluate.binary_metrics(probabilities, labels)
        self.assertAlmostEqual(metrics.accuracy, 1.0)
        self.assertAlmostEqual(metrics.auc, 1.0)
        self.assertEqual(metrics.confusion, [[2, 0], [0, 2]])

    def test_usable_frames_shrinks_with_delta(self):
        self.assertAlmostEqual(evaluate.usable_frames(12, 0.0, 40.0), 13.0)
        self.assertAlmostEqual(evaluate.usable_frames(12, 400.0, 40.0), 3.0)


class ModelTest(unittest.TestCase):
    def test_parameter_budget(self):
        model = pose_model.PoseDirectionNet()
        self.assertLess(pose_model.count_parameters(model), 1_000_000)

    def test_forward_shapes(self):
        model = pose_model.PoseDirectionNet()
        batch = model(torch_from_numpy(np.zeros((2, 16, body_frame.FEATURE_DIM), dtype=np.float32)))
        self.assertEqual(tuple(batch.shape), (2, 3))

    def test_binary_model_transfers_encoder_only(self):
        source = pose_model.PoseDirectionNet()
        with self.assertRaises(ValueError):
            # 空 state_dict 缺少编码器权重：必须显式失败，不能静默随机初始化
            pose_model.PoseBinaryNet.from_pretrained({})
        transferred = pose_model.PoseBinaryNet.from_pretrained(source.state_dict())
        for key, value in source.encoder.state_dict().items():
            np.testing.assert_allclose(
                transferred.encoder.state_dict()[key].numpy(), value.numpy(), atol=1e-6
            )
        self.assertEqual(tuple(transferred.head.weight.shape), (1, source.encoder.output_dim))


def torch_from_numpy(array: np.ndarray):
    import torch

    return torch.from_numpy(array)


class TrainSmokeTest(unittest.TestCase):
    """极小规模跑通训练/评估闭环（不追求精度）。"""

    def test_training_loop_and_delta_evaluation(self):
        shots = []
        for index in range(6):
            frames = _walking_frames(seed=index + 1)
            shots.append(
                dataset.PoseShot(
                    shot_id=f"{index:05d}",
                    frames=frames,
                    on_target=index % 2,
                    label_text="Shots on target",
                    frame_step_ms=40.0,
                    kick_frame=12,
                )
            )
        config = synthetic.DeformConfig(variants_per_source=2)
        samples = synthetic.build_synthetic_dataset(shots, seed=3, deform=config)
        train_samples = synthetic.subset_by_source(samples, {f"{i:05d}" for i in range(4)})
        val_samples = synthetic.subset_by_source(samples, {f"{i:05d}" for i in (4, 5)})
        self.assertTrue(train_samples and val_samples)

        train_config = train.TrainConfig(epochs=2, batch_size=8, patience=2, threads=1)
        result = train.train_direction_model(train_samples, val_samples, train_config)
        self.assertGreater(result.temperature, 0.0)
        self.assertEqual(len(result.history), 2)
        self.assertLess(result.parameter_count, 1_000_000)

        metrics = evaluate.evaluate_at_deltas(
            result.model, val_samples, (0.0, 200.0), result.temperature, window=train_config.window
        )
        self.assertEqual(len(metrics), 2)
        for item in metrics:
            self.assertEqual(item.sample_count, len(val_samples))
            self.assertGreaterEqual(item.accuracy, 0.0)
            self.assertLessEqual(item.accuracy, 1.0)
            self.assertFalse(math.isnan(item.log_loss))
            self.assertEqual(len(item.confusion), 3)
        self.assertGreater(metrics[0].usable_frames, metrics[1].usable_frames)

    def test_checkpoint_roundtrip(self):
        model = pose_model.PoseDirectionNet()
        result = train.TrainResult(model=model, temperature=1.7, parameter_count=pose_model.count_parameters(model))
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "pose.pt"
            train.save_checkpoint(path, result, train.TrainConfig())
            loaded, payload = train.load_checkpoint(path)
            self.assertAlmostEqual(payload["temperature"], 1.7)
            for key, value in model.state_dict().items():
                np.testing.assert_allclose(loaded.state_dict()[key].numpy(), value.numpy(), atol=1e-6)


if __name__ == "__main__":
    unittest.main()
