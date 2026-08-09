import math
import os

output_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "runtime", "data")
os.makedirs(output_dir, exist_ok=True)

G = 9.8
DT = 0.01   # 100Hz 高精度采样
BALL_RADIUS = 0.11

# trajectory: (name, vx, vy, vz, description)
trajectories = [
    ("penalty_top_left",    23.0, -2.8,  6.5, "左上死角"),
    ("penalty_top_right",   23.0,  2.8,  6.5, "右上死角"),
    ("penalty_bottom_left", 27.0, -2.2,  0.3, "左下贴地"),
    ("penalty_bottom_right",27.0,  2.2,  0.3, "右下贴地"),
    ("penalty_center_chip", 16.0,  0.1,  9.5, "勺子点球"),
    ("penalty_center_power",28.0, -0.05, 4.0, "中路爆射"),
    ("penalty_mid_left",    24.0, -2.0,  3.5, "左路半高"),
    ("penalty_mid_right",   24.0,  2.0,  3.5, "右路半高"),
    ("penalty_curved_left", 22.0, -1.2,  5.0, "左弧线"),
    ("penalty_curved_right",22.0,  1.2,  5.0, "右弧线"),
]

for name, vx, vy, vz, desc in trajectories:
    filepath = os.path.join(output_dir, f"{name}_trajectory.csv")

    goal_time = 11.0 / vx
    total_time = (11.0 + 3.0) / vx  # 飞到球门后3米
    num_frames = int(total_time / DT) + 2

    # 弧线球的侧向加速度
    vy_drift = 0.0
    if "curved" in name:
        vy_drift = -2.0 if "left" in name else 2.0

    lines = ["time,x,y,z"]
    for i in range(num_frames):
        t = i * DT
        if t > total_time:
            break
        x = 11.0 - vx * t
        y = 0.0 + vy * t + 0.5 * vy_drift * t * t
        z = BALL_RADIUS + vz * t - 0.5 * G * t * t
        if z < BALL_RADIUS:
            z = BALL_RADIUS
        lines.append(f"{t:.6f},{x:.10f},{y:.10f},{z:.10f}")

    with open(filepath, "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

    y_goal = vy * goal_time + 0.5 * vy_drift * goal_time * goal_time
    z_goal = BALL_RADIUS + vz * goal_time - 0.5 * G * goal_time * goal_time
    spd = math.sqrt(vx*vx + vy*vy + vz*vz)
    print(f"  {name:30s}  {spd:4.0f}m/s {spd*3.6:4.0f}km/h  Y={y_goal:+5.2f} Z={z_goal:5.2f}  {len(lines)-1:4d}帧  {desc}")
