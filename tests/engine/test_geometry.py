"""3D 重建中的纯几何函数测试。

只 import `numpy` / `cv2` / `scipy`（reconstruction 模块已把 ultralytics 改成
惰性导入），因此不需要 torch、CUDA、YOLO 权重或摄像头。
"""
import unittest
from pathlib import Path

import cv2
import numpy as np

from aifootball.pipeline import reconstruction

CAMERA_MATRIX = np.array([[800.0, 0.0, 640.0],
                          [0.0, 800.0, 360.0],
                          [0.0, 0.0, 1.0]], dtype=np.float64)


def make_config(name, rotation, translation):
    """按 CameraConfig 的字段构造虚拟相机（不读任何标定文件）。"""
    rotation = np.asarray(rotation, dtype=np.float64)
    tvec = np.asarray(translation, dtype=np.float64).reshape(3, 1)
    rvec, _ = cv2.Rodrigues(rotation)
    return reconstruction.CameraConfig(
        name=name,
        pose_path=Path(f"{name}_pose.npz"),
        meta_path=Path(f"{name}_extrinsics.json"),
        camera_matrix=CAMERA_MATRIX.copy(),
        dist_coeffs=np.zeros(5, dtype=np.float64),
        rvec=rvec,
        tvec=tvec,
        rotation_matrix=rotation,
        camera_center_world=(-rotation.T @ tvec.reshape(3)),
    )


class SmoothPointsTest(unittest.TestCase):

    def test_short_sequence_is_returned_as_copy(self):
        points = np.array([[1.0, 2.0], [3.0, 4.0]])
        smoothed = reconstruction.smooth_points(points)
        self.assertTrue(np.array_equal(smoothed, points))
        self.assertIsNot(smoothed, points)

    def test_shape_and_dtype_preserved(self):
        points = np.arange(60, dtype=np.float32).reshape(20, 3)
        smoothed = reconstruction.smooth_points(points, window=5)
        self.assertEqual(smoothed.shape, (20, 3))
        self.assertEqual(smoothed.dtype, np.float64)

    def test_constant_sequence_is_unchanged(self):
        points = np.full((25, 2), 7.5, dtype=np.float64)
        self.assertTrue(np.allclose(reconstruction.smooth_points(points, window=7), points))

    def test_linear_sequence_inner_points_are_unchanged(self):
        """满窗内部点用对称三角权重，对线性数据取中心值，因此直线轨迹原样保留。"""
        index = np.arange(30, dtype=np.float64)
        points = np.column_stack([index, 2.0 * index - 4.0])
        smoothed = reconstruction.smooth_points(points, window=7)
        half = 3  # window // 2
        inner = slice(half, len(points) - half)
        self.assertTrue(np.allclose(smoothed[inner], points[inner], atol=1e-9))

    def test_truncated_boundary_window_pulls_endpoints_inward(self):
        """边界窗口被单侧截断，权重偏向内侧，端点因此被拉向轨迹内部（既有算法行为）。"""
        index = np.arange(30, dtype=np.float64)
        points = index.reshape(-1, 1)
        smoothed = reconstruction.smooth_points(points, window=7)
        self.assertGreater(float(smoothed[0, 0]), float(points[0, 0]))
        self.assertLess(float(smoothed[-1, 0]), float(points[-1, 0]))
        # 越贴近边界牵引越强，进入满窗后不再有额外偏移
        self.assertGreater(float(smoothed[0, 0] - points[0, 0]),
                           float(smoothed[2, 0] - points[2, 0]))
        self.assertAlmostEqual(float(smoothed[3, 0]), float(points[3, 0]), places=9)

    def test_impulse_is_attenuated(self):
        """孤立跳变点应被邻域拉回，这是去抖动的核心作用。"""
        points = np.zeros((21, 1), dtype=np.float64)
        points[10, 0] = 100.0
        smoothed = reconstruction.smooth_points(points, window=7)
        self.assertLess(float(smoothed[10, 0]), 100.0)
        self.assertGreater(float(smoothed[10, 0]), 0.0)
        self.assertLess(float(smoothed[10, 0]), float(smoothed[9, 0]) + 100.0)

    def test_noise_variance_is_reduced(self):
        rng = np.random.default_rng(20260319)
        index = np.arange(80, dtype=np.float64)
        clean = np.column_stack([index, np.sin(index / 8.0)])
        noisy = clean + rng.normal(scale=0.5, size=clean.shape)
        smoothed = reconstruction.smooth_points(noisy, window=9)
        noisy_error = float(np.mean((noisy - clean) ** 2))
        smoothed_error = float(np.mean((smoothed - clean) ** 2))
        self.assertLess(smoothed_error, noisy_error)

    def test_window_larger_than_sequence_still_works(self):
        points = np.arange(12, dtype=np.float64).reshape(6, 2)
        smoothed = reconstruction.smooth_points(points, window=99)
        self.assertEqual(smoothed.shape, points.shape)
        self.assertTrue(np.all(np.isfinite(smoothed)))


class BoxCenterCovarianceTest(unittest.TestCase):

    def test_returns_symmetric_positive_definite_matrix(self):
        covariance = reconstruction.estimate_box_center_covariance([0.0, 0.0, 40.0, 40.0], 0.9)
        self.assertEqual(covariance.shape, (2, 2))
        self.assertTrue(np.allclose(covariance, covariance.T))
        self.assertTrue(np.all(np.linalg.eigvalsh(covariance) > 0.0))

    def test_low_confidence_inflates_uncertainty(self):
        high = reconstruction.estimate_box_center_covariance([0.0, 0.0, 40.0, 40.0], 1.0)
        low = reconstruction.estimate_box_center_covariance([0.0, 0.0, 40.0, 40.0], 0.0)
        self.assertGreater(float(low[0, 0]), float(high[0, 0]))

    def test_elongated_box_inflates_uncertainty(self):
        square = reconstruction.estimate_box_center_covariance([0.0, 0.0, 80.0, 80.0], 0.8)
        elongated = reconstruction.estimate_box_center_covariance([0.0, 0.0, 80.0, 20.0], 0.8)
        self.assertGreater(float(elongated[0, 0]), float(square[0, 0]))
        self.assertGreater(float(elongated[0, 0]), float(elongated[1, 1]))

    def test_tiny_box_keeps_floor_variance(self):
        """退化的零面积框不能产生零方差（否则白化会除零）。"""
        covariance = reconstruction.estimate_box_center_covariance([10.0, 10.0, 10.0, 10.0], 1.0)
        self.assertAlmostEqual(float(covariance[0, 0]), 0.75 ** 2, places=9)
        self.assertAlmostEqual(float(covariance[1, 1]), 0.75 ** 2, places=9)


class TriangulationTest(unittest.TestCase):

    def setUp(self):
        self.camera_a = make_config("left", np.eye(3), [0.0, 0.0, 0.0])
        self.camera_b = make_config("right", np.eye(3), [-2.0, 0.0, 0.0])
        self.world_point = np.array([1.0, 1.0, 6.0], dtype=np.float64)

    def test_ray_origin_is_camera_centre(self):
        origin, ray = reconstruction.image_point_to_world_ray([700.0, 400.0], self.camera_a)
        self.assertTrue(np.allclose(origin, self.camera_a.camera_center_world))
        self.assertAlmostEqual(float(np.linalg.norm(ray)), 1.0, places=9)

    def test_ray_points_towards_the_projectected_ball(self):
        image_point = reconstruction.project_world_points(self.world_point.reshape(1, 3), self.camera_a)[0]
        origin, ray = reconstruction.image_point_to_world_ray(image_point, self.camera_a)
        expected = self.world_point - origin
        expected = expected / np.linalg.norm(expected)
        self.assertTrue(np.allclose(ray, expected, atol=1e-6))

    def test_triangulation_recovers_known_point(self):
        point_a = reconstruction.project_world_points(self.world_point.reshape(1, 3), self.camera_a)[0]
        point_b = reconstruction.project_world_points(self.world_point.reshape(1, 3), self.camera_b)[0]
        result = reconstruction.triangulate_ball_point(point_a, self.camera_a, point_b, self.camera_b)
        self.assertIsNotNone(result)
        self.assertTrue(np.allclose(result["world_point"], self.world_point, atol=1e-3))
        self.assertLess(result["ray_gap"], 1e-6)
        self.assertLess(result["reprojection_error"], 1e-3)

    def test_covariance_weighting_keeps_the_answer(self):
        point_a = reconstruction.project_world_points(self.world_point.reshape(1, 3), self.camera_a)[0]
        point_b = reconstruction.project_world_points(self.world_point.reshape(1, 3), self.camera_b)[0]
        covariance = reconstruction.estimate_box_center_covariance([0.0, 0.0, 12.0, 12.0], 0.8)
        result = reconstruction.triangulate_ball_point(
            point_a, self.camera_a, point_b, self.camera_b,
            covariance_a=covariance, covariance_b=covariance)
        self.assertIsNotNone(result)
        self.assertTrue(np.allclose(result["world_point"], self.world_point, atol=1e-3))

    def test_identical_cameras_cannot_triangulate(self):
        """两路完全重合时射线平行，必须返回 None 而不是发出垃圾点。"""
        point = reconstruction.project_world_points(self.world_point.reshape(1, 3), self.camera_a)[0]
        self.assertIsNone(
            reconstruction.triangulate_ball_point(point, self.camera_a, point, self.camera_a))

    def test_point_outside_field_limits_is_rejected(self):
        far_point = np.array([1.0, 1.0, 40.0], dtype=np.float64)
        point_a = reconstruction.project_world_points(far_point.reshape(1, 3), self.camera_a)[0]
        point_b = reconstruction.project_world_points(far_point.reshape(1, 3), self.camera_b)[0]
        self.assertIsNone(
            reconstruction.triangulate_ball_point(point_a, self.camera_a, point_b, self.camera_b))


if __name__ == "__main__":
    unittest.main()
