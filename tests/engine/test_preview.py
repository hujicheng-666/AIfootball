"""预览等比缩放测试（纯 numpy，不打开窗口、不读摄像头）。"""
import unittest

import numpy as np

from aifootball.preview import fit_scale


class FitScaleTest(unittest.TestCase):

    def test_wide_frame_follows_width_limit(self):
        """超宽手机画面（2448x1080）按宽度受限缩放，比例保持不变（允许取整误差）。"""
        frame = np.zeros((1080, 2448, 3), dtype=np.uint8)
        out = fit_scale(frame, (1280, 720))
        self.assertEqual(out.shape, (565, 1280, 3))
        # 缩放系数由宽度决定，高度按同一系数取整，宽高比因此有 <1 像素级的偏差
        self.assertAlmostEqual(out.shape[1] / out.shape[0], 2448 / 1080, delta=0.01)

    def test_tall_frame_follows_height_limit(self):
        frame = np.zeros((1080, 720, 3), dtype=np.uint8)
        out = fit_scale(frame, (1280, 720))
        self.assertEqual(out.shape, (720, 480, 3))

    def test_small_frame_is_scaled_up(self):
        """目标框比源画面大时同样等比放大（保证预览尺寸稳定）。"""
        frame = np.zeros((50, 100, 3), dtype=np.uint8)
        out = fit_scale(frame, (1280, 720))
        self.assertEqual(out.shape, (640, 1280, 3))

    def test_result_never_exceeds_target_box(self):
        for height, width in ((1080, 2448), (1080, 1920), (480, 640), (721, 1281)):
            frame = np.zeros((height, width, 3), dtype=np.uint8)
            out = fit_scale(frame, (1280, 720))
            self.assertLessEqual(out.shape[1], 1280)
            self.assertLessEqual(out.shape[0], 720)

    def test_degenerate_frame_is_returned_untouched(self):
        frame = np.zeros((0, 0, 3), dtype=np.uint8)
        self.assertIs(fit_scale(frame, (1280, 720)), frame)

    def test_content_is_preserved(self):
        frame = np.zeros((10, 20, 3), dtype=np.uint8)
        frame[:, :10] = 255
        out = fit_scale(frame, (40, 40))
        self.assertEqual(out.shape, (20, 40, 3))
        # 左半白、右半黑，缩放后不应被拉伸变形
        self.assertGreater(float(out[:, :20].mean()), float(out[:, 20:].mean()))


if __name__ == "__main__":
    unittest.main()
