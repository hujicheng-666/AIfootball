"""射门评估：生成覆盖球门的测试射门集，驱动 Unity 批量回放，汇总扑救率。

为什么需要它（见 `docs/feasibility-leg-pose-penalty-direction.md` §2.3）
------------------------------------------------------------------------
本项目最容易踩的失败模式是"模型很准，但游戏手感没变"。因此第一验收指标不是
方向预测准确率，而是**端到端扑救率**：球门 7.32 m × 2.44 m、点球点 11 m、
门将横向可达约 1–2.5 m —— 在这个几何下，只要门将拿到真值落点就几乎必然扑到。

本模块把该指标变成一条可重复执行的命令：

    python -m aifootball evaluate --shots 30 --seed 7

它做三件事：
    1. 生成 N 条覆盖球门网格的抛物线轨迹 CSV（首行严格落在点球点上，能被
       Unity 侧的点球点校验接受）；
    2. 写一份清单 JSON，交给 Unity 播放器 `--eval-list` 批量回放；
    3. 读回 `results.jsonl`，输出扑救率 / 方向判断正确率 / 分区域统计。

`--generate-only` 只做第 1 步（没有 Unity 环境时也能用），`--summarize-only`
只做第 3 步，两者都可独立测试。
"""
from __future__ import annotations

import json
import math
import random
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

#: 球门尺寸（与 Unity 场景一致）
DEFAULT_GOAL_WIDTH = 7.32
DEFAULT_GOAL_HEIGHT = 2.44
#: 点球点到球门线的水平距离（`Goal.PenaltySpotDistance`）
PENALTY_SPOT_DISTANCE = 11.0
#: 球半径（与 `data/*_trajectory.csv` 首行一致）
DEFAULT_BALL_RADIUS = 0.11
#: 采样间隔，与现有导出轨迹一致（120 Hz）
DEFAULT_SAMPLE_INTERVAL = 1.0 / 120.0
#: 重力加速度（与 Unity 侧 `Ball.gravity` 同量级）
GRAVITY = 9.81

#: 真实点球的横向落点分布（最左 → 最右，5 列）。经验取值：中路偏多、贴柱死角偏少。
#: 用途见 `pick_cells`：按格均匀铺开会让必进的最外列占掉 40% 样本。
PENALTY_COLUMN_WEIGHTS = (0.14, 0.21, 0.30, 0.21, 0.14)
#: 真实点球的高度分布（低 → 高，3 行）。低球占一半以上，高球最少。
PENALTY_ROW_WEIGHTS = (0.50, 0.32, 0.18)

RESULT_ORDER = ("save", "goal", "miss", "error")


@dataclass
class ShotSpec:
    """一条测试射门的元数据（生成阶段产出，汇总阶段复用）。"""

    name: str
    csv_path: Path
    lateral: float
    height: float
    speed: float
    column: int
    row: int


def zone_centers(
    columns: int = 3,
    rows: int = 3,
    goal_width: float = DEFAULT_GOAL_WIDTH,
    goal_height: float = DEFAULT_GOAL_HEIGHT,
    lateral_margin: float = 0.35,
    low_margin: float = 0.25,
    top_margin: float = 0.25,
):
    """返回球门内 `columns × rows` 个格心 (lateral, height, column, row)。

    留出边距，避免生成"贴着门柱/横梁"的球：那种球既可能撞柱，也会让统计
    被物理边界效应主导，掩盖门将决策本身的差异。
    """
    if columns < 1 or rows < 1:
        raise ValueError("columns/rows 必须 >= 1")
    half = goal_width * 0.5 - lateral_margin
    low = low_margin
    high = goal_height - top_margin
    if half <= 0 or high <= low:
        raise ValueError("球门尺寸与边距不兼容")

    centers = []
    for row in range(rows):
        height = low + (row + 0.5) * (high - low) / rows
        for column in range(columns):
            lateral = -half + (column + 0.5) * (2.0 * half) / columns
            centers.append((lateral, height, column, row))
    return centers


def _resample_weights(weights, count):
    """把经验分布（分段常数密度）重采样到 `count` 个等宽格。

    经验分布是按 5 列 / 3 行统计的，网格尺寸变了（例如 3 列）就必须重采样，
    否则"中路权重更高"这个结论会随网格尺寸一起丢掉。
    """
    if count == len(weights):
        return list(weights)
    edges = [index / len(weights) for index in range(len(weights) + 1)]
    result = []
    for index in range(count):
        low, high = index / count, (index + 1) / count
        mass = 0.0
        for position, weight in enumerate(weights):
            span = edges[position + 1] - edges[position]
            overlap = min(high, edges[position + 1]) - max(low, edges[position])
            if overlap > 0:
                mass += weight * overlap / span
        result.append(mass)
    total = sum(result)
    return [value / total for value in result]


def pick_cells(columns, rows, count, distribution, rng):
    """决定每一发球射向哪一格，返回 `count` 个 `(column, row)`。

    `uniform` 逐格轮转。`penalty` 先给每格各一发保证覆盖，其余按真实点球落点
    分布补足：真实点球低球多、中路多、死角少。若按格均匀铺开，最外两列会占掉
    40% 的样本，而 7.32 m 球门下最外列格心离门将站位 2.93 m，已经超出
    `JumpDistance × probabilityScale ≈ 2.09 m` 的身体位移上限——这些球本来就
    必进。均匀分布会让扑救率由射门集设计决定，而不是由观测模型决定。
    """
    if distribution not in ("uniform", "penalty"):
        raise ValueError(f"未知的射门分布: {distribution}")
    cells = [(column, row) for row in range(rows) for column in range(columns)]
    if distribution == "uniform" or count <= len(cells):
        return [cells[index % len(cells)] for index in range(count)]

    column_weights = _resample_weights(PENALTY_COLUMN_WEIGHTS, columns)
    row_weights = _resample_weights(PENALTY_ROW_WEIGHTS, rows)
    weights = [row_weights[row] * column_weights[column] for column, row in cells]
    extra = rng.choices(range(len(cells)), weights=weights, k=count - len(cells))
    targets = list(cells) + [cells[index] for index in extra]
    rng.shuffle(targets)
    return targets


def build_trajectory(
    target_lateral: float,
    target_height: float,
    speed: float,
    *,
    spot_x: float = PENALTY_SPOT_DISTANCE,
    goal_x: float = 0.0,
    ball_radius: float = DEFAULT_BALL_RADIUS,
    interval: float = DEFAULT_SAMPLE_INTERVAL,
    gravity: float = GRAVITY,
):
    """生成一条从点球点到球门平面目标点的抛物线轨迹。

    CSV 坐标约定（与 Unity `CsvBallCenterToWorld` 一致）：
        x = 距球门线的水平距离（点球点 = 11），y = 横向（+ 为门将右手侧），
        z = 高度。轨迹首行精确落在 `(spot_x, 0, ball_radius)` 上，因此能通过
        Unity 侧的点球点校验（容差 0.35 m）。
    """
    if speed <= 0:
        raise ValueError("speed 必须为正")
    distance = spot_x - goal_x
    if distance <= 0:
        raise ValueError("点球点必须在球门之前")

    flight_time = distance / speed
    steps = max(1, int(math.ceil(flight_time / interval)))
    points = []
    for index in range(steps + 1):
        time = min(index * interval, flight_time)
        progress = time / flight_time
        x = spot_x - distance * progress
        y = target_lateral * progress
        # z(t) = r + (h - r)·u + ½g·t·(T - t)：两端精确命中，中间为重力抛物线
        z = ball_radius + (target_height - ball_radius) * progress + 0.5 * gravity * time * (flight_time - time)
        points.append((time, x, y, z))
    return points


def write_trajectory_csv(path: Path, points) -> None:
    """写出 Unity 回放用的 CSV（表头 `time,x,y,z`）。"""
    path.parent.mkdir(parents=True, exist_ok=True)
    lines = ["time,x,y,z"]
    lines.extend(f"{t:.6f},{x:.6f},{y:.6f},{z:.6f}" for t, x, y, z in points)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def generate_shot_set(
    out_dir: Path,
    *,
    count: int = 162,
    seed: int = 20261019,
    columns: int = 5,
    rows: int = 3,
    speed_min: float = 18.0,
    speed_max: float = 26.0,
    ball_radius: float = DEFAULT_BALL_RADIUS,
    goal_width: float = DEFAULT_GOAL_WIDTH,
    goal_height: float = DEFAULT_GOAL_HEIGHT,
    jitter_ratio: float = 0.95,
    distribution: str = "penalty",
):
    """生成 `count` 条射门轨迹，返回 `ShotSpec` 列表。

    落点按格心轮转（保证每格都被覆盖），并在**格内**抖动。抖动不是装饰：
    真值若全部落在格心，量化误差恒为零，"门将只能分辨到格子"这一降级项就永远
    踩不到边界，方向判断正确率会虚高到 90% 以上，第二验收指标形同虚设。

    `distribution` 决定多出来的样本落在哪些格（见 `pick_cells`）：`penalty` 按真实
    点球落点分布加权，`uniform` 逐格均匀铺开。前者才是"端到端扑救率"该被评估的
    场景，后者适合做覆盖性检查。

    格的定义与 Unity 侧 `ShotObservationModel.ColumnOf/RowOf` 完全一致（整门均分），
    因此 Python 记录的 `column/row` 与 C# 计算的 `truthCell` 永远同格；
    `jitter_ratio` 以半格为单位（0.95 ≈ 格内近似均匀、且不跨格），落点再夹在格内并留出
    安全边，避免打到门柱/横梁而让统计被物理边界效应主导。
    """
    if count < 1:
        raise ValueError("count 必须 >= 1")
    if speed_max < speed_min:
        raise ValueError("speed_max 不能小于 speed_min")

    out_dir = Path(out_dir)
    shots_dir = out_dir / "shots"
    # 边距交给下面的"格内夹紧"处理，格心本身必须与 Unity 的分格同源。
    centers = zone_centers(columns, rows, goal_width, goal_height,
                           lateral_margin=0.0, low_margin=0.0, top_margin=0.0)
    center_by_cell = {(column, row): (lateral, height)
                      for lateral, height, column, row in centers}
    rng = random.Random(seed)
    targets = pick_cells(columns, rows, count, distribution, rng)

    cell_width = goal_width / columns
    cell_height = goal_height / rows
    # 安全边：球半径 0.11 m，留出余量保证球不会擦到门柱或横梁。
    lateral_inset = 0.20
    height_inset_low = 0.15
    height_inset_high = 0.15

    specs = []
    for index, (column, row) in enumerate(targets):
        lateral, height = center_by_cell[(column, row)]
        if jitter_ratio > 0:
            lateral += rng.uniform(-1.0, 1.0) * (cell_width * 0.5) * jitter_ratio
            height += rng.uniform(-1.0, 1.0) * (cell_height * 0.5) * jitter_ratio

        cell_low = -goal_width * 0.5 + column * cell_width
        cell_high = cell_low + cell_width
        row_low = row * cell_height
        row_high = row_low + cell_height
        lateral = min(max(lateral, cell_low + lateral_inset), cell_high - lateral_inset)
        height = min(max(height, row_low + height_inset_low), row_high - height_inset_high)

        speed = rng.uniform(speed_min, speed_max)

        name = f"shot{index + 1:03d}"
        csv_path = shots_dir / f"{name}.csv"
        points = build_trajectory(lateral, height, speed, ball_radius=ball_radius)
        write_trajectory_csv(csv_path, points)
        specs.append(ShotSpec(name, csv_path, lateral, height, speed, column, row))
    return specs


def build_evaluation_list(
    specs,
    *,
    output_path: Path,
    observation: bool = True,
    reaction_delay: float = 0.18,
    confidence: float = 0.66,
    noise: float = 0.80,
    columns: int = 3,
    rows: int = 3,
    seed: int = 20261019,
    goalkeeper: str | None = None,
    goalkeeper_path: Path | None = None,
):
    """构造 Unity `--eval-list` 需要的清单结构（字段名与 C# 侧一致）。

    `goalkeeper_path` 优先：播放器侧的 `runtime/data/goalkeepers` 目录在发行
    目录里并不存在，靠名字查找会静默退回场景默认门将，评估结果就不可比。
    """
    resolved_goalkeeper = ""
    if goalkeeper_path is not None:
        candidate = Path(goalkeeper_path)
        if candidate.is_file():
            resolved_goalkeeper = str(candidate.resolve())
        else:
            raise FileNotFoundError(f"找不到门将 JSON: {candidate}")

    return {
        "outputPath": str(Path(output_path).resolve()),
        "observation": {
            "enabled": bool(observation),
            "reactionDelaySeconds": float(reaction_delay),
            "gridColumns": int(columns),
            "gridRows": int(rows),
            "positionNoiseMeters": float(noise),
            "confidence": float(confidence),
            "randomSeed": int(seed),
        },
        "shots": [
            {
                "name": spec.name,
                "csv": str(Path(spec.csv_path).resolve()),
                "goalkeeper": goalkeeper or "",
                "goalkeeperPath": resolved_goalkeeper,
            }
            for spec in specs
        ],
    }


def write_evaluation_list(path: Path, payload) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def read_results(results_path: Path):
    """读取 `results.jsonl`，忽略空行与坏行（坏行计入解析失败数）。"""
    rows = []
    broken = 0
    for line in Path(results_path).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            rows.append(json.loads(line))
        except json.JSONDecodeError:
            broken += 1
    return rows, broken


def summarize(results_path: Path) -> dict:
    """把逐条结果汇总成扑救率报告。

    扑救率 = save / (save + goal + miss)，与可行性报告 §2.3 的口径一致：
    `error` 行（清单缺失、载入失败等）不计入分母，但会单独报告。
    """
    rows, broken = read_results(results_path)

    counts = {key: 0 for key in RESULT_ORDER}
    for row in rows:
        result = str(row.get("result", "error"))
        counts[result] = counts.get(result, 0) + 1

    decided = counts["save"] + counts["goal"] + counts["miss"]
    save_rate = counts["save"] / decided if decided else 0.0

    # "方向"指标只看横向（左/中/右）：高度判断错了是"扑救方式"问题，把它算进方向
    # 准确率会让指标同时被两个维度稀释，与可行性报告 §2.3 的口径也对不上。
    observed_rows = [row for row in rows if row.get("observationEnabled")]
    direction_hits = height_hits = cell_hits = 0
    for row in observed_rows:
        truth_cell = row.get("truthCell") or []
        observed_cell = row.get("observedCell") or []
        if len(truth_cell) != 2 or len(observed_cell) != 2:
            continue
        same_column = int(truth_cell[0]) == int(observed_cell[0])
        same_row = int(truth_cell[1]) == int(observed_cell[1])
        direction_hits += 1 if same_column else 0
        height_hits += 1 if same_row else 0
        cell_hits += 1 if (same_column and same_row) else 0
    observed_total = len(observed_rows)
    direction_accuracy = direction_hits / observed_total if observed_total else None
    height_accuracy = height_hits / observed_total if observed_total else None
    same_cell_rate = cell_hits / observed_total if observed_total else None

    lateral_errors = []
    for row in observed_rows:
        truth = row.get("truthLocal") or []
        observed = row.get("observedLocal") or []
        if len(truth) >= 1 and len(observed) >= 1:
            lateral_errors.append(float(observed[0]) - float(truth[0]))
    mean_lateral_error = sum(lateral_errors) / len(lateral_errors) if lateral_errors else None
    mean_abs_lateral_error = (
        sum(abs(value) for value in lateral_errors) / len(lateral_errors) if lateral_errors else None
    )

    by_zone: dict[str, dict] = {}
    for row in rows:
        cell = row.get("truthCell") or []
        if len(cell) != 2 or cell[0] is None or cell[1] is None:
            continue
        key = f"{int(cell[1])}-{int(cell[0])}"  # 行-列，行 0 = 最低
        bucket = by_zone.setdefault(key, {"save": 0, "goal": 0, "miss": 0, "error": 0})
        result = str(row.get("result", "error"))
        bucket[result] = bucket.get(result, 0) + 1

    for key, bucket in by_zone.items():
        zone_decided = bucket["save"] + bucket["goal"] + bucket["miss"]
        bucket["decided"] = zone_decided
        bucket["saveRate"] = bucket["save"] / zone_decided if zone_decided else 0.0

    # "未触"的两种成因必须分开看：偏出（球到了门线但不在门框内）是射门集设计问题，
    # 未到门线（轨迹没播完）则是回放链路问题，两者的排查方向完全不同。
    miss_rows = [row for row in rows if str(row.get("result", "error")) == "miss"]
    miss_wide = sum(1 for row in miss_rows if row.get("goalLineCrossed"))

    return {
        "total": len(rows),
        "brokenLines": broken,
        "save": counts["save"],
        "goal": counts["goal"],
        "miss": counts["miss"],
        "missWide": miss_wide,
        "missShort": len(miss_rows) - miss_wide,
        "error": counts["error"],
        "decided": decided,
        "saveRate": save_rate,
        "directionAccuracy": direction_accuracy,
        "heightAccuracy": height_accuracy,
        "sameCellRate": same_cell_rate,
        "meanLateralError": mean_lateral_error,
        "meanAbsLateralError": mean_abs_lateral_error,
        "observationEnabled": bool(observed_rows),
        "byZone": by_zone,
    }


def format_report(report: dict) -> str:
    """把汇总结果格式化成可直接贴进报告/日志的文本。"""
    lines = []
    lines.append("射门评估汇总")
    lines.append("=" * 46)
    lines.append(f"样本总数      : {report['total']}（解析失败行 {report['brokenLines']}）")
    lines.append(f"扑救 save     : {report['save']}")
    lines.append(f"进球 goal     : {report['goal']}")
    lines.append(f"未触 miss     : {report['miss']}")
    if report["miss"]:
        lines.append(f"  ├ 偏出      : {report['missWide']}（球到门线但不在门框内 → 射门集设计）")
        lines.append(f"  └ 未到门线  : {report['missShort']}（轨迹没播完 → 回放链路）")
    lines.append(f"错误 error    : {report['error']}")
    lines.append(f"有效判定      : {report['decided']}")
    lines.append(f"扑救率        : {report['saveRate']:.1%}   ← 第一验收指标（目标 18%–30%）")
    if report["observationEnabled"]:
        accuracy = report["directionAccuracy"]
        lines.append(
            "方向判断正确率: "
            + (f"{accuracy:.1%}" if accuracy is not None else "n/a")
            + "   ← 第二指标（横向左/中/右，目标 55%–75%）"
        )
        height = report["heightAccuracy"]
        same_cell = report["sameCellRate"]
        lines.append(
            "高度判断正确率: "
            + (f"{height:.1%}" if height is not None else "n/a")
            + "（低/中/高，不计入方向指标）"
        )
        lines.append(
            "横向+高度全对: "
            + (f"{same_cell:.1%}" if same_cell is not None else "n/a")
        )
        if report["meanAbsLateralError"] is not None:
            lines.append(
                f"横向观测误差  : 平均 {report['meanLateralError']:+.3f} m / "
                f"绝对 {report['meanAbsLateralError']:.3f} m"
            )
    else:
        lines.append("方向判断正确率: n/a（本次未启用观测降级）")

    if report["byZone"]:
        lines.append("")
        lines.append("按真值分区（行 0 = 最低，列 0 = 最左）：")
        lines.append(f"  {'区域':<8}{'扑救':>6}{'进球':>6}{'未触':>6}{'扑救率':>10}")
        for key in sorted(report["byZone"]):
            bucket = report["byZone"][key]
            lines.append(
                f"  {key:<8}{bucket['save']:>6}{bucket['goal']:>6}{bucket['miss']:>6}"
                f"{bucket['saveRate']:>10.1%}"
            )
    return "\n".join(lines)


def run_unity_evaluation(unity_exe: Path, list_path: Path, log_path: Path, timeout: float = 1800.0) -> int:
    """以批处理模式驱动 Unity 播放器执行清单，返回退出码。"""
    unity_exe = Path(unity_exe)
    if not unity_exe.is_file():
        raise FileNotFoundError(f"找不到 Unity 播放器: {unity_exe}")

    command = [
        str(unity_exe),
        "--eval-list", str(Path(list_path).resolve()),
        "-batchmode",
        "-nographics",
        "-logFile", str(Path(log_path).resolve()),
    ]
    completed = subprocess.run(command, cwd=str(unity_exe.parent), timeout=timeout)
    return completed.returncode


def run_evaluation(
    out_dir: Path,
    *,
    unity_exe: Path | None = None,
    count: int = 162,
    seed: int = 20261019,
    columns: int = 5,
    rows: int = 3,
    speed_min: float = 18.0,
    speed_max: float = 26.0,
    jitter_ratio: float = 0.95,
    distribution: str = "penalty",
    observation: bool = True,
    reaction_delay: float = 0.18,
    confidence: float = 0.66,
    noise: float = 0.80,
    goalkeeper: str | None = None,
    goalkeeper_path: Path | None = None,
    timeout: float = 1800.0,
):
    """完整流程：生成 → 驱动 Unity → 汇总。返回 (report, 退出码)。"""
    out_dir = Path(out_dir)
    specs = generate_shot_set(
        out_dir,
        count=count,
        seed=seed,
        columns=columns,
        rows=rows,
        speed_min=speed_min,
        speed_max=speed_max,
        jitter_ratio=jitter_ratio,
        distribution=distribution,
    )
    results_path = out_dir / "results.jsonl"
    list_path = out_dir / "shots.json"
    write_evaluation_list(
        list_path,
        build_evaluation_list(
            specs,
            output_path=results_path,
            observation=observation,
            reaction_delay=reaction_delay,
            confidence=confidence,
            noise=noise,
            columns=columns,
            rows=rows,
            seed=seed,
            goalkeeper=goalkeeper,
            goalkeeper_path=goalkeeper_path,
        ),
    )

    if unity_exe is None:
        return None, 0

    code = run_unity_evaluation(unity_exe, list_path, out_dir / "unity-eval.log", timeout=timeout)
    if not results_path.is_file():
        print(f"[错误] Unity 未产出结果文件：{results_path}", file=sys.stderr)
        return None, code or 1

    report = summarize(results_path)
    (out_dir / "report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return report, code
