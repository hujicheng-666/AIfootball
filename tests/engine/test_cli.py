"""CLI 契约与工作区解析测试。

只 import 轻量的 argparse 层（`aifootball.cli` 内部对 pipeline 做惰性
导入），因此不加载 torch / CUDA / YOLO，也不需要摄像头。
"""
import contextlib
import io
import os
import unittest

from aifootball import cli

try:  # 包方式（unittest discover -t .）与直接以目录为顶层两种跑法都支持
    from .test_workspace import WorkspaceTestCase
except ImportError:  # pragma: no cover - 仅在非包方式导入时走到
    from test_workspace import WorkspaceTestCase

#: 10 个子命令各自必须给出的参数（其余参数都有默认值）
SUBCOMMAND_REQUIRED_ARGS = {
    "offline": [],
    "online": [],
    "online-live": [],
    "teststream": ["--source", "rtsp://127.0.0.1:8554/live"],
    "list-cameras": [],
    "calibrate-intrinsics": ["--left", "left.mp4", "--right", "right.mp4"],
    "calibrate-extrinsics": ["--left-image", "l.jpg", "--right-image", "r.jpg"],
    "selfcheck": [],
    "evaluate": [],
    # pose 是三段式（pose <action> ...），默认走不碰 torch 的 inspect
    "pose": ["inspect"],
}


class ParserContractTest(WorkspaceTestCase):
    """App 侧按名字传参，所以子命令名与参数名都属于对外契约。"""

    def test_all_subcommands_exist(self):
        parser = cli.build_parser()
        actions = [a for a in parser._actions if hasattr(a, "choices") and a.choices]
        names = set()
        for action in actions:
            if action.dest == "command":
                names = set(action.choices)
        self.assertEqual(names, set(SUBCOMMAND_REQUIRED_ARGS))

    def test_every_subcommand_accepts_workspace(self):
        """每个子命令都要有 --workspace，且**不传时为 None**。

        若默认值写成 "."，解析参数就会无条件固定成 cwd，把
        AIFOOTBALL_WORKSPACE 环境变量盖掉。
        """
        parser = cli.build_parser()
        for command, required in SUBCOMMAND_REQUIRED_ARGS.items():
            with self.subTest(command=command):
                args = parser.parse_args([command] + required)
                self.assertIsNone(args.workspace)
                args = parser.parse_args([command] + required + ["--workspace", "somewhere"])
                self.assertEqual(args.workspace, "somewhere")

    def test_explicit_workspace_wins_over_environment(self):
        pinned, other = self.temp_dir(), self.temp_dir()
        os.environ["AIFOOTBALL_WORKSPACE"] = str(other)
        self.assertEqual(cli.resolve_workspace(str(pinned)).root, pinned.resolve())


class AcceptanceGateTest(WorkspaceTestCase):
    """验收区间要能当门禁用：越界必须是非 0 退出码，而不是只打印一行字。"""

    def test_window_is_parsed_as_two_values(self):
        args = cli.build_parser().parse_args(["evaluate", "--expect-save-rate", "0.18", "0.30"])
        self.assertEqual(args.expect_save_rate, [0.18, 0.30])

    def test_rate_inside_window_passes(self):
        self.assertEqual(cli._check_acceptance({"saveRate": 0.259}, [0.18, 0.30]), 0)

    def test_rate_above_window_fails(self):
        self.assertEqual(cli._check_acceptance({"saveRate": 0.9}, [0.18, 0.30]), 1)

    def test_rate_below_window_fails(self):
        self.assertEqual(cli._check_acceptance({"saveRate": 0.02}, [0.18, 0.30]), 1)

    def test_boundaries_are_inclusive(self):
        self.assertEqual(cli._check_acceptance({"saveRate": 0.18}, [0.18, 0.30]), 0)
        self.assertEqual(cli._check_acceptance({"saveRate": 0.30}, [0.18, 0.30]), 0)

    def test_no_expectation_never_fails(self):
        self.assertEqual(cli._check_acceptance({"saveRate": 0.9}, None), 0)
        self.assertEqual(cli._check_acceptance(None, [0.18, 0.30]), 0)

    def test_direction_window_is_parsed_as_two_values(self):
        args = cli.build_parser().parse_args(["evaluate", "--expect-direction", "0.55", "0.75"])
        self.assertEqual(args.expect_direction, [0.55, 0.75])

    def test_direction_inside_window_passes(self):
        report = {"saveRate": 0.22, "directionAccuracy": 0.70}
        self.assertEqual(cli._check_acceptance(report, [0.18, 0.30], [0.55, 0.75]), 0)

    def test_direction_above_window_fails(self):
        """方向正确率过高同样是失败：说明量化/噪声太弱，降级项形同虚设。"""
        report = {"saveRate": 0.22, "directionAccuracy": 0.85}
        self.assertEqual(cli._check_acceptance(report, [0.18, 0.30], [0.55, 0.75]), 1)

    def test_direction_below_window_fails(self):
        report = {"saveRate": 0.22, "directionAccuracy": 0.40}
        self.assertEqual(cli._check_acceptance(report, [0.18, 0.30], [0.55, 0.75]), 1)

    def test_direction_skipped_when_observation_is_off(self):
        """对照臂没有方向指标，门禁不能因此判失败——否则 off 臂永远跑不过。"""
        report = {"saveRate": 0.70, "directionAccuracy": None}
        self.assertEqual(cli._check_acceptance(report, None, [0.55, 0.75]), 0)

    def test_both_windows_must_pass(self):
        report = {"saveRate": 0.22, "directionAccuracy": 0.85}
        self.assertEqual(cli._check_acceptance(report, [0.18, 0.30], [0.55, 0.75]), 1)
        report = {"saveRate": 0.50, "directionAccuracy": 0.70}
        self.assertEqual(cli._check_acceptance(report, [0.18, 0.30], [0.55, 0.75]), 1)


class WorkspaceResolutionTest(WorkspaceTestCase):

    def test_none_keeps_lazy_resolution(self):
        """不传 --workspace 时回到惰性解析：环境变量优先，其次 cwd。"""
        tmp = self.temp_dir()
        os.environ["AIFOOTBALL_WORKSPACE"] = str(tmp)
        self.assertEqual(cli.resolve_workspace(None).root, tmp.resolve())

    def test_none_falls_back_to_cwd_without_env(self):
        tmp = self.temp_dir()
        os.chdir(tmp)
        self.assertEqual(cli.resolve_workspace(None).root, tmp.resolve())


class ErrorHandlingTest(WorkspaceTestCase):
    """失败路径必须给一行中文错误并返回非 0，而不是抛 traceback。"""

    def _run(self, argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
            code = cli.main(argv)
        return code, out.getvalue()

    def test_empty_workspace_reports_no_sample(self):
        tmp = self.temp_dir()
        code, text = self._run(["offline", "--workspace", str(tmp)])
        self.assertEqual(code, 1)
        self.assertIn("没有可用样本", text)

    def test_missing_sample_reports_failure_without_traceback(self):
        tmp = self.temp_dir()
        code, text = self._run(["offline", "--samples", "no_such_sample",
                                "--workspace", str(tmp)])
        self.assertEqual(code, 1)
        self.assertIn("no_such_sample", text)
        self.assertIn("未生成 Unity CSV", text)
        self.assertNotIn("Traceback", text)


if __name__ == "__main__":
    unittest.main()
