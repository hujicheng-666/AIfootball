"""共享常量测试：这些数值直接决定物理模型，改动必须是深思熟虑的。"""
import unittest

import numpy as np

from aifootball import constants


class ConstantsTest(unittest.TestCase):

    def test_physics_constants(self):
        self.assertEqual(constants.GRAVITY, 9.81)
        self.assertEqual(constants.BALL_RADIUS_M, 0.11)
        self.assertEqual(constants.SPORTS_BALL_CLASS_ID, 32)

    def test_field_geometry(self):
        self.assertEqual(constants.FIELD_X_LIMITS, (-15.0, 15.0))
        self.assertEqual(constants.FIELD_Y_LIMITS, (-5.0, 25.0))
        self.assertEqual(constants.GOAL_LINE_Y, 0.0)
        self.assertAlmostEqual(constants.GOAL_HALF_WIDTH_M, 3.66, places=6)
        self.assertEqual(constants.GOAL_HEIGHT_M, 2.44)

    def test_penalty_spot_ground_vs_ball_centre(self):
        """地面参考点与球心参考点必须相差一个球半径（2D 踢球检测用前者）。"""
        ground = constants.PENALTY_SPOT_GROUND_WORLD
        centre = constants.PENALTY_SPOT_BALL_CENTER_WORLD
        self.assertTrue(np.allclose(ground, [0.0, 11.0, 0.0]))
        self.assertTrue(np.allclose(centre, [0.0, 11.0, constants.BALL_RADIUS_M]))
        self.assertAlmostEqual(float(centre[2] - ground[2]), constants.BALL_RADIUS_M, places=9)
        self.assertEqual(ground.dtype, np.float64)

    def test_detection_defaults(self):
        self.assertEqual(constants.DEFAULT_IMGSZ, 1280)
        self.assertEqual(constants.DEFAULT_CONF, 0.15)


if __name__ == "__main__":
    unittest.main()
