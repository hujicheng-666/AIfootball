"""共享物理/场地常量 —— reconstruct / fit / live 共用，避免重复定义。"""
import numpy as np

GRAVITY = 9.81
SPORTS_BALL_CLASS_ID = 32
BALL_RADIUS_M = 0.11

# Keep the physical ground reference separate from the reconstructed ball
# centre.  2D kick detection projects a bounding-box foot to the ground,
# whereas triangulation and trajectory playback use the ball centre.
PENALTY_SPOT_GROUND_WORLD = np.array([0.0, 11.0, 0.0], dtype=np.float64)
PENALTY_SPOT_BALL_CENTER_WORLD = np.array(
    [0.0, 11.0, BALL_RADIUS_M], dtype=np.float64
)
FIELD_X_LIMITS = (-15.0, 15.0)
FIELD_Y_LIMITS = (-5.0, 25.0)
GOAL_LINE_Y = 0.0
GOAL_HALF_WIDTH_M = 7.32 / 2.0
GOAL_HEIGHT_M = 2.44
DEFAULT_IMGSZ = 1280
DEFAULT_CONF = 0.15
