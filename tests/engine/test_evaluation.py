"""射门评估测试：轨迹生成、清单契约与扑救率汇总。

这些测试全部离线（不加载 torch / YOLO，也不启动 Unity 播放器），因为
`evaluation` 模块把"生成"、"驱动"、"汇总"三段拆开了：
    - 生成/清单/汇总在这里直接测；
    - 驱动 Unity 的那一步由 `--generate-only` / 真实播放器覆盖。
"""
import collections
import json
import math
import random
import unittest

from aifootball import evaluation

try:  # 包方式与直接以目录为顶层两种跑法都支持
    from .test_workspace import WorkspaceTestCase
except ImportError:  # pragma: no cover
    from test_workspace import WorkspaceTestCase


class TrajectoryTest(unittest.TestCase):
    """轨迹必须能被 Unity 侧的点球点校验接受，并且真的落到目标点。"""

    def test_first_sample_sits_on_penalty_spot(self):
        points = evaluation.build_trajectory(target_lateral=1.5, target_height=1.2, speed=20.0)
        time, x, y, z = points[0]
        self.assertEqual(time, 0.0)
        self.assertAlmostEqual(x, evaluation.PENALTY_SPOT_DISTANCE, places=6)
        self.assertAlmostEqual(y, 0.0, places=6)
        self.assertAlmostEqual(z, evaluation.DEFAULT_BALL_RADIUS, places=6)

    def test_last_sample_hits_requested_target(self):
        points = evaluation.build_trajectory(target_lateral=-2.0, target_height=1.8, speed=22.0)
        _, x, y, z = points[-1]
        self.assertAlmostEqual(x, 0.0, places=6)
        self.assertAlmostEqual(y, -2.0, places=6)
        self.assertAlmostEqual(z, 1.8, places=6)

    def test_flight_time_matches_speed(self):
        speed = 20.0
        points = evaluation.build_trajectory(0.0, 1.0, speed)
        self.assertAlmostEqual(points[-1][0], evaluation.PENALTY_SPOT_DISTANCE / speed, places=6)

    def test_time_is_monotonic_and_samples_are_dense(self):
        points = evaluation.build_trajectory(0.5, 0.9, 25.0)
        times = [p[0] for p in points]
        self.assertEqual(times, sorted(times))
        self.assertGreater(len(points), 20)

    def test_vertical_motion_is_a_constant_gravity_parabola(self):
        """直线上升会让"预测"退化成几何题；真实球路必须是常重力抛物线。"""
        points = evaluation.build_trajectory(0.0, 2.0, 20.0)
        index = len(points) // 2
        (_, _, _, z0), (_, _, _, z1), (_, _, _, z2) = points[index - 1], points[index], points[index + 1]
        dt = points[index][0] - points[index - 1][0]
        acceleration = (z2 - 2 * z1 + z0) / (dt * dt)
        self.assertAlmostEqual(acceleration, -evaluation.GRAVITY, places=3)

    def test_rising_shot_arcs_above_the_straight_line(self):
        """上抛球在重力下必须比直线更高（初速更大），否则不是真实球路。"""
        points = evaluation.build_trajectory(0.0, 2.0, 20.0)
        midpoint = points[len(points) // 2]
        straight_height = evaluation.DEFAULT_BALL_RADIUS + (2.0 - evaluation.DEFAULT_BALL_RADIUS) * 0.5
        self.assertGreater(midpoint[3], straight_height)

    def test_rejects_non_positive_speed(self):
        with self.assertRaises(ValueError):
            evaluation.build_trajectory(0.0, 1.0, 0.0)


class ZoneCenterTest(unittest.TestCase):

    def test_three_by_three_has_nine_cells(self):
        centers = evaluation.zone_centers(3, 3)
        self.assertEqual(len(centers), 9)
        self.assertEqual(len({(c[2], c[3]) for c in centers}), 9)

    def test_centers_stay_inside_the_goal_mouth(self):
        for lateral, height, _, _ in evaluation.zone_centers(3, 3):
            self.assertLess(abs(lateral), evaluation.DEFAULT_GOAL_WIDTH / 2)
            self.assertGreater(height, 0.0)
            self.assertLess(height, evaluation.DEFAULT_GOAL_HEIGHT)

    def test_row_zero_is_the_lowest_band(self):
        centers = evaluation.zone_centers(3, 3)
        lowest = [c[1] for c in centers if c[3] == 0]
        highest = [c[1] for c in centers if c[3] == 2]
        self.assertLess(max(lowest), min(highest))

    def test_rejects_degenerate_grid(self):
        with self.assertRaises(ValueError):
            evaluation.zone_centers(0, 3)


class ShotSetTest(WorkspaceTestCase):

    def test_generates_one_csv_per_shot_and_covers_every_cell(self):
        out_dir = self.temp_dir()
        specs = evaluation.generate_shot_set(out_dir, count=9, seed=1, columns=3, rows=3)
        self.assertEqual(len(specs), 9)
        self.assertEqual(len({(s.column, s.row) for s in specs}), 9)
        for spec in specs:
            self.assertTrue(spec.csv_path.is_file(), spec.csv_path)

    def test_csv_has_header_and_parsable_rows(self):
        out_dir = self.temp_dir()
        specs = evaluation.generate_shot_set(out_dir, count=1, seed=1)
        lines = specs[0].csv_path.read_text(encoding="utf-8").strip().splitlines()
        self.assertEqual(lines[0], "time,x,y,z")
        for line in lines[1:]:
            values = [float(part) for part in line.split(",")]
            self.assertEqual(len(values), 4)
            self.assertFalse(any(math.isnan(v) for v in values))

    def test_more_shots_than_cells_adds_jitter_without_leaving_the_goal(self):
        out_dir = self.temp_dir()
        specs = evaluation.generate_shot_set(out_dir, count=30, seed=3)
        self.assertEqual(len(specs), 30)
        for spec in specs:
            self.assertLess(abs(spec.lateral), evaluation.DEFAULT_GOAL_WIDTH / 2)
            self.assertGreater(spec.height, 0.0)
            self.assertLess(spec.height, evaluation.DEFAULT_GOAL_HEIGHT)

    def test_same_seed_is_reproducible(self):
        first = evaluation.generate_shot_set(self.temp_dir(), count=6, seed=42)
        second = evaluation.generate_shot_set(self.temp_dir(), count=6, seed=42)
        self.assertEqual([(s.lateral, s.height, s.speed) for s in first],
                         [(s.lateral, s.height, s.speed) for s in second])

    def test_rejects_bad_speed_range(self):
        with self.assertRaises(ValueError):
            evaluation.generate_shot_set(self.temp_dir(), count=3, speed_min=30.0, speed_max=20.0)

    def test_penalty_distribution_favours_the_centre(self):
        """真实点球中路多、死角少；按格均匀铺开会让必进的最外列占掉四成样本，
        扑救率就由射门集设计决定，而不是由观测模型决定。"""
        targets = evaluation.pick_cells(5, 3, 810, "penalty", random.Random(5))
        per_column = collections.Counter(column for column, _ in targets)
        self.assertGreater(per_column[2], per_column[1])
        self.assertGreater(per_column[1], per_column[0])
        self.assertAlmostEqual(per_column[0], per_column[4], delta=25)
        self.assertEqual(len(set(targets)), 15)

    def test_shot_set_covers_every_cell_under_penalty_distribution(self):
        out_dir = self.temp_dir()
        specs = evaluation.generate_shot_set(out_dir, count=81, seed=5, columns=5, rows=3,
                                             distribution="penalty")
        self.assertEqual(len(specs), 81)
        self.assertEqual(len({(s.column, s.row) for s in specs}), 15)

    def test_uniform_distribution_round_robins_every_cell(self):
        out_dir = self.temp_dir()
        specs = evaluation.generate_shot_set(out_dir, count=30, seed=5, columns=5, rows=3,
                                             distribution="uniform")
        per_column = collections.Counter(spec.column for spec in specs)
        self.assertEqual(set(per_column.values()), {6})

    def test_rejects_unknown_distribution(self):
        with self.assertRaises(ValueError):
            evaluation.generate_shot_set(self.temp_dir(), count=3, distribution="gaussian")

    def test_resampled_weights_stay_normalised_and_symmetric(self):
        for count in (1, 2, 3, 5, 7, 11):
            weights = evaluation._resample_weights(evaluation.PENALTY_COLUMN_WEIGHTS, count)
            self.assertEqual(len(weights), count)
            self.assertAlmostEqual(sum(weights), 1.0, places=9)
        weights = evaluation._resample_weights(evaluation.PENALTY_COLUMN_WEIGHTS, 3)
        self.assertGreater(weights[1], weights[0])
        self.assertAlmostEqual(weights[0], weights[2], places=9)


class EvaluationListContractTest(WorkspaceTestCase):
    """清单字段名是 Python 与 C# 之间的契约，改名会让评估静默跑成默认参数。"""

    def _build(self, **overrides):
        out_dir = self.temp_dir()
        specs = evaluation.generate_shot_set(out_dir, count=2, seed=5)
        payload = evaluation.build_evaluation_list(
            specs,
            output_path=out_dir / "results.jsonl",
            **overrides,
        )
        return payload

    def test_uses_camel_case_field_names_expected_by_unity(self):
        payload = self._build(observation=True, reaction_delay=0.25, confidence=0.5, noise=0.4)
        observation = payload["observation"]
        self.assertEqual(
            set(observation),
            {"enabled", "reactionDelaySeconds", "gridColumns", "gridRows",
             "positionNoiseMeters", "confidence", "randomSeed"},
        )
        self.assertTrue(observation["enabled"])
        self.assertAlmostEqual(observation["reactionDelaySeconds"], 0.25)
        self.assertAlmostEqual(observation["confidence"], 0.5)
        self.assertAlmostEqual(observation["positionNoiseMeters"], 0.4)

    def test_shot_entries_point_at_existing_csv_files(self):
        payload = self._build()
        self.assertEqual(len(payload["shots"]), 2)
        for entry in payload["shots"]:
            self.assertEqual(set(entry), {"name", "csv", "goalkeeper", "goalkeeperPath"})
            self.assertTrue(entry["csv"].endswith(".csv"))

    def test_goalkeeper_json_is_embedded_as_absolute_path(self):
        gk = self.temp_dir() / "GK_Test.json"
        gk.write_text('{"DisplayName": "测试门将"}', encoding="utf-8")
        payload = self._build(goalkeeper_path=gk)
        self.assertEqual(payload["shots"][0]["goalkeeperPath"], str(gk.resolve()))

    def test_missing_goalkeeper_json_fails_loudly(self):
        """宁可报错也不要静默退回场景默认门将，否则评估结果不可比。"""
        with self.assertRaises(FileNotFoundError):
            self._build(goalkeeper_path=self.temp_dir() / "nope.json")

    def test_write_evaluation_list_round_trips_as_json(self):
        payload = self._build()
        path = self.temp_dir() / "shots.json"
        evaluation.write_evaluation_list(path, payload)
        self.assertEqual(json.loads(path.read_text(encoding="utf-8")), payload)


class SummarizeTest(WorkspaceTestCase):

    def _write_results(self, rows):
        path = self.temp_dir() / "results.jsonl"
        path.write_text("\n".join(json.dumps(row) for row in rows) + "\n", encoding="utf-8")
        return path

    @staticmethod
    def _row(result, *, same_cell=True, truth=(0.0, 1.0), observed=(0.1, 1.1),
             cell=(1, 1), observed_cell=(1, 1), enabled=True, crossed=True):
        return {
            "result": result,
            "sameCell": same_cell,
            "truthLocal": list(truth),
            "observedLocal": list(observed),
            "truthCell": list(cell),
            "observedCell": list(observed_cell),
            "observationEnabled": enabled,
            "goalLineCrossed": crossed,
        }

    def test_save_rate_uses_only_decided_shots(self):
        path = self._write_results([
            self._row("save"), self._row("save"), self._row("goal"),
            self._row("miss"), self._row("error"),
        ])
        report = evaluation.summarize(path)
        self.assertEqual(report["save"], 2)
        self.assertEqual(report["decided"], 4)
        self.assertAlmostEqual(report["saveRate"], 0.5)
        self.assertEqual(report["error"], 1)

    def test_direction_accuracy_counts_lateral_column_only(self):
        """方向 = 横向左/中/右；高度判断属于另一维度，混进方向指标会同时稀释两者。"""
        path = self._write_results([
            self._row("save", cell=(0, 0), observed_cell=(0, 2),
                      truth=(1.0, 1.0), observed=(1.4, 1.0)),
            self._row("goal", cell=(2, 1), observed_cell=(1, 1),
                      truth=(-2.0, 0.5), observed=(-1.0, 0.5)),
        ])
        report = evaluation.summarize(path)
        self.assertAlmostEqual(report["directionAccuracy"], 0.5)
        self.assertAlmostEqual(report["heightAccuracy"], 0.5)
        self.assertAlmostEqual(report["sameCellRate"], 0.0)
        self.assertAlmostEqual(report["meanLateralError"], (0.4 + 1.0) / 2)
        self.assertAlmostEqual(report["meanAbsLateralError"], (0.4 + 1.0) / 2)

    def test_perfect_cells_report_full_accuracy(self):
        path = self._write_results([
            self._row("save", cell=(2, 0), observed_cell=(2, 0)),
        ])
        report = evaluation.summarize(path)
        self.assertAlmostEqual(report["directionAccuracy"], 1.0)
        self.assertAlmostEqual(report["heightAccuracy"], 1.0)
        self.assertAlmostEqual(report["sameCellRate"], 1.0)

    def test_observation_off_reports_no_direction_accuracy(self):
        path = self._write_results([self._row("save", enabled=False)])
        report = evaluation.summarize(path)
        self.assertFalse(report["observationEnabled"])
        self.assertIsNone(report["directionAccuracy"])
        self.assertIsNone(report["heightAccuracy"])
        self.assertIsNone(report["sameCellRate"])

    def test_miss_is_split_into_wide_and_short(self):
        """偏出是射门集问题，未到门线是回放链路问题，混在一起会掩盖故障。"""
        path = self._write_results([
            self._row("miss", crossed=True),
            self._row("miss", crossed=True),
            self._row("miss", crossed=False),
        ])
        report = evaluation.summarize(path)
        self.assertEqual(report["missWide"], 2)
        self.assertEqual(report["missShort"], 1)
        self.assertIn("偏出", evaluation.format_report(report))

    def test_zone_breakdown_groups_by_truth_cell(self):
        # truthCell 的顺序与 Unity 侧一致： [column, row]；分区键写作 "<row>-<column>"。
        path = self._write_results([
            self._row("save", cell=(0, 0)), self._row("goal", cell=(0, 0)),
            self._row("save", cell=(1, 2)),
        ])
        report = evaluation.summarize(path)
        self.assertAlmostEqual(report["byZone"]["0-0"]["saveRate"], 0.5)
        self.assertAlmostEqual(report["byZone"]["2-1"]["saveRate"], 1.0)

    def test_empty_results_do_not_divide_by_zero(self):
        path = self._write_results([])
        report = evaluation.summarize(path)
        self.assertEqual(report["total"], 0)
        self.assertEqual(report["saveRate"], 0.0)

    def test_broken_lines_are_counted_not_fatal(self):
        path = self.temp_dir() / "results.jsonl"
        path.write_text('{"result": "save"}\nnot-json\n\n', encoding="utf-8")
        report = evaluation.summarize(path)
        self.assertEqual(report["total"], 1)
        self.assertEqual(report["brokenLines"], 1)

    def test_report_text_mentions_the_acceptance_window(self):
        path = self._write_results([self._row("save"), self._row("goal")])
        text = evaluation.format_report(evaluation.summarize(path))
        self.assertIn("扑救率", text)
        self.assertIn("18%", text)


class GenerateOnlyFlowTest(WorkspaceTestCase):

    def test_run_without_unity_writes_list_and_returns_no_report(self):
        out_dir = self.temp_dir() / "eval"
        report, code = evaluation.run_evaluation(out_dir, unity_exe=None, count=4, seed=2)
        self.assertIsNone(report)
        self.assertEqual(code, 0)
        payload = json.loads((out_dir / "shots.json").read_text(encoding="utf-8"))
        self.assertEqual(len(payload["shots"]), 4)
        self.assertTrue((out_dir / "shots" / "shot001.csv").is_file())

    def test_run_rejects_missing_unity_executable(self):
        with self.assertRaises(FileNotFoundError):
            evaluation.run_unity_evaluation(
                self.temp_dir() / "missing.exe",
                self.temp_dir() / "shots.json",
                self.temp_dir() / "log.txt",
            )


if __name__ == "__main__":
    unittest.main()
