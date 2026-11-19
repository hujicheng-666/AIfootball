# AIfootball Engine（Python 引擎包）

双目视频 → 足球三维轨迹重建 → 弹道拟合 → Unity 回放 CSV 的完整算法实现。
界面（WPF）通过命令行调用本包，算法只在 `aifootball/` 里维护一套。

## 目录结构

```
aifootball/
├─ __main__.py          入口：sys.exit(cli.main())（界面直接执行本文件）
├─ cli.py               argparse 全部子命令
├─ workspace.py         工作区路径上下文（惰性解析，见下）
├─ constants.py         共享物理/场地常量
├─ control.py           ControlChannel：界面经 stdin 下发 start/stop
├─ preview.py           PreviewPublisher / fit_scale：预览 JPEG 发布
├─ capture/
│  └─ dual_camera.py    DualCameraRecorder（含 list_cameras / test_stream）
├─ calibration/
│  ├─ intrinsics.py     视频内参标定 + calibrate_from_photos()（照片内参）
│  ├─ extrinsics.py     外参标定（手动点击参考点 + 光束法平差）
│  └─ assets/           预设参考照片
└─ pipeline/
   ├─ reconstruction.py 3D 重建（YOLO 检测 + 三角化）
   ├─ ballistics.py     弹道拟合（重力 + 空气阻力）
   ├─ live.py           边录制边分析
   └─ export_unity.py   导出 Unity CSV
```

## 用法

```bash
# 由界面调用的方式（工作区即进程工作目录，或由 --workspace / 环境变量指定）
python -u src/AIfootball.Engine/aifootball/__main__.py offline --samples sample1 --workspace D:\AIfootball

# 以模块方式运行（仓库自带解释器已由 python_env\python310._pth 提供
#   ..\src\AIfootball.Engine 搜索路径，无需再设 PYTHONPATH；用外部解释器时才需要）
python -m aifootball --help
python -m aifootball selfcheck
python -m aifootball list-cameras
```

各阶段也可单独运行：

```bash
python -m aifootball.pipeline.reconstruction --samples sample1
python -m aifootball.pipeline.ballistics --samples sample1
python -m aifootball.pipeline.export_unity --samples sample1
```

## 工作区（workspace）

代码里**没有**任何在 import 期固定的路径常量。路径在使用时由
`aifootball.workspace.get_workspace()` 解析：

1. `set_workspace()` 显式设置过的目录（CLI 解析 `--workspace` 后立即设置，此后固定）；
2. 否则环境变量 `AIFOOTBALL_WORKSPACE`；
3. 否则当前工作目录。

模型权重取 `YOLO_MODEL_PATH` 环境变量，未设置时用 `<workspace>/models/yolo11m.pt`。

## 测试

```bash
cd D:\AIfootball
python_env\python.exe -m unittest discover -s tests/engine -t .
```
