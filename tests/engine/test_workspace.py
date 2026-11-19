"""工作区惰性解析测试（不依赖 torch / CUDA / 摄像头）。"""
import os
import shutil
import tempfile
import unittest
from pathlib import Path

from aifootball import workspace as workspace_module
from aifootball.workspace import (
    DEFAULT_YOLO_MODEL,
    Workspace,
    get_workspace,
    reset_workspace,
    set_workspace,
)

#: 旧实现里在 import 期固化的“工作区派生”路径名：新方案中任何一个都不允许再出现
#: （注意：BASE_DIR / ASSETS_DIR 这类“锚定包自身位置”的常量不在其列，见
#:  NoFrozenPathConstantsTest._assert_self_anchored 的附加校验）
FORBIDDEN_MODULE_PATH_ATTRS = (
    "WORKSPACE", "WORKSPACE_DIR", "CALIB", "SAMPLES",
    "OUTPUT_DIR", "OUTPUT_ROOT", "INPUT_ROOT", "TRACK_CACHE_DIR",
    "YOLO_MODEL_PATH", "BALLISTIC_ROOT", "UNITY_DATA",
    "INTRINSICS_REGISTRY_PATH", "DEFAULT_INTRINSICS_PATHS",
)

#: 允许存在的模块级路径常量，但必须指向包内资源（由 __file__ 推导），不能指向 cwd / 工作区
SELF_ANCHORED_ATTRS = ("BASE_DIR", "ASSETS_DIR")


class WorkspaceTestCase(unittest.TestCase):
    """统一保存/恢复环境变量与 cwd，避免测试之间互相污染。"""

    def setUp(self):
        self._env_backup = {name: os.environ.get(name)
                            for name in ("AIFOOTBALL_WORKSPACE", "YOLO_MODEL_PATH")}
        self._cwd_backup = Path.cwd()
        for name in self._env_backup:
            os.environ.pop(name, None)
        reset_workspace()

    def tearDown(self):
        for name, value in self._env_backup.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value
        os.chdir(self._cwd_backup)
        reset_workspace()

    def temp_dir(self):
        """建一个临时工作区目录，并登记清理。

        Windows 上无法删除“仍是当前工作目录”的目录，所以清理时先把 cwd
        切回测试开始前的位置；即使某个测试忘了切回来也不会留下残留目录。
        """
        path = Path(tempfile.mkdtemp(prefix="aifootball_ws_"))

        def _cleanup():
            os.chdir(self._cwd_backup)
            shutil.rmtree(path, ignore_errors=True)

        self.addCleanup(_cleanup)
        return path


class LazyResolutionTest(WorkspaceTestCase):
    def test_defaults_to_cwd(self):
        """既没有显式设置也没有环境变量时，工作区就是当前目录。"""
        self.assertEqual(get_workspace().root, Path.cwd().resolve())

    def test_env_var_overrides_cwd(self):
        tmp = self.temp_dir()
        os.environ["AIFOOTBALL_WORKSPACE"] = str(tmp)
        self.assertEqual(get_workspace().root, tmp.resolve())

    def test_resolution_is_lazy_not_frozen_at_import(self):
        """未显式设置时，工作区随当前目录走 —— 这正是“惰性”的含义。"""
        first, second = self.temp_dir(), self.temp_dir()
        os.chdir(first)
        self.assertEqual(get_workspace().root, first.resolve())
        os.chdir(second)
        self.assertEqual(get_workspace().root, second.resolve())

    def test_set_workspace_pins_the_value(self):
        """显式设置后固定：cwd 与环境变量都不再影响结果。"""
        pinned, other = self.temp_dir(), self.temp_dir()
        expected = set_workspace(pinned).root
        os.environ["AIFOOTBALL_WORKSPACE"] = str(other)
        os.chdir(other)
        self.assertEqual(get_workspace().root, expected)

    def test_reset_workspace_returns_to_lazy_resolution(self):
        pinned, env_dir = self.temp_dir(), self.temp_dir()
        set_workspace(pinned)
        reset_workspace()
        os.environ["AIFOOTBALL_WORKSPACE"] = str(env_dir)
        self.assertEqual(get_workspace().root, env_dir.resolve())

    def test_relative_path_is_resolved_against_cwd(self):
        tmp = self.temp_dir()
        os.chdir(tmp)
        os.mkdir("relative_ws")
        expected = (tmp / "relative_ws").resolve()
        self.assertEqual(set_workspace("relative_ws").root, expected)


class WorkspaceLayoutTest(WorkspaceTestCase):
    def test_derived_paths(self):
        root = Path(tempfile.gettempdir()) / "aifootball_ws_probe"
        ws = Workspace(root)
        self.assertEqual(ws.calib, root / "calib")
        self.assertEqual(ws.samples, root / "samples")
        self.assertEqual(ws.output, root / "output")
        self.assertEqual(ws.models, root / "models")
        self.assertEqual(ws.data, root / "data")
        self.assertEqual(ws.trajectory_3d, root / "output" / "trajectory_3d")
        self.assertEqual(ws.trajectory_ballistic, root / "output" / "trajectory_ballistic")
        self.assertEqual(ws.track_cache, root / "output" / "track_cache")
        self.assertEqual(ws.live_preview, root / "output" / "live_preview")

    def test_root_is_absolute(self):
        ws = Workspace(".")
        self.assertTrue(ws.root.is_absolute())

    def test_yolo_model_path_defaults_to_workspace_models(self):
        ws = Workspace(Path(tempfile.gettempdir()) / "aifootball_ws_probe")
        self.assertEqual(ws.yolo_model_path(), ws.models / DEFAULT_YOLO_MODEL)

    def test_yolo_model_path_prefers_env(self):
        ws = Workspace(Path(tempfile.gettempdir()) / "aifootball_ws_probe")
        os.environ["YOLO_MODEL_PATH"] = r"D:\weights\custom.pt"
        self.assertEqual(ws.yolo_model_path(), Path(r"D:\weights\custom.pt"))

    def test_get_workspace_reflects_env_model_override(self):
        tmp = self.temp_dir()
        os.environ["AIFOOTBALL_WORKSPACE"] = str(tmp)
        os.environ["YOLO_MODEL_PATH"] = str(tmp / "w.pt")
        self.assertEqual(get_workspace().yolo_model_path(), tmp / "w.pt")


class NoFrozenPathConstantsTest(WorkspaceTestCase):
    """需求 2 的回归护栏：任何模块都不许在 import 期固化路径常量。"""

    def _assert_clean(self, module):
        for name in FORBIDDEN_MODULE_PATH_ATTRS:
            self.assertFalse(
                hasattr(module, name),
                f"{module.__name__}.{name} 不应存在于模块级（应在函数内用 get_workspace()）")

    def _assert_self_anchored(self, module, name, expected):
        """模块级路径常量只允许锚定包自身位置（包内资源），且必须是绝对路径。"""
        value = getattr(module, name, None)
        if value is None:
            return
        self.assertIsInstance(value, Path, f"{module.__name__}.{name} 应为 pathlib.Path")
        self.assertTrue(value.is_absolute(), f"{module.__name__}.{name} 必须是绝对路径")
        self.assertEqual(
            value, expected,
            f"{module.__name__}.{name} 只能由 __file__ 推导（包内资源），不能锚定 cwd / 工作区")

    def test_workspace_module_itself(self):
        self._assert_clean(workspace_module)

    def test_pipeline_modules(self):
        from aifootball.pipeline import ballistics, export_unity, live, reconstruction
        for module in (reconstruction, ballistics, export_unity, live):
            self._assert_clean(module)

    def test_calibration_modules(self):
        """标定模块只允许保留“包自身位置”常量（BASE_DIR/ASSETS_DIR），其余一律惰性解析。"""
        from aifootball.calibration import extrinsics, intrinsics
        for module in (intrinsics, extrinsics):
            self._assert_clean(module)
            owner = Path(module.__file__).resolve().parent
            self._assert_self_anchored(module, "BASE_DIR", owner)
            self._assert_self_anchored(module, "ASSETS_DIR", owner / "assets")


if __name__ == "__main__":
    unittest.main()
