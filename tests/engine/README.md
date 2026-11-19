# AIfootball Engine 测试

纯 `unittest`，**不需要** torch / CUDA / YOLO 权重 / 摄像头
（`reconstruction` 已把 ultralytics 改为惰性导入，测试只用到 numpy/OpenCV/scipy）。

| 文件 | 覆盖内容 |
| --- | --- |
| `test_workspace.py` | 工作区惰性解析、`AIFOOTBALL_WORKSPACE` 优先级、`set_workspace` 固定、派生路径、"模块级不得固化路径常量"护栏 |
| `test_cli.py` | 8 个子命令与 `--workspace` 契约、`--workspace` 默认不覆盖环境变量、空工作区/缺样本的报错路径 |
| `test_control.py` | `ControlChannel` 的 stdin 行协议 poll 行为（管道 / TTY / EOF 关闭） |
| `test_preview.py` | `preview.fit_scale` 等比缩放（超宽 / 竖幅 / 放大 / 不超过目标框） |
| `test_constants.py` | 重力、球半径、场地与球门常量、检测默认值 |
| `test_geometry.py` | `smooth_points`、`estimate_box_center_covariance`、`image_point_to_world_ray`、`triangulate_ball_point` |

## 运行

在 `D:\AIfootball` 目录下（仓库级 `tests/` 是包，`unittest` 才能把测试
识别成 `tests.engine.*`）：

```bat
python_env\python.exe -m unittest discover -s tests/engine -t .
```

PowerShell 写法相同：

```powershell
D:\AIfootball\python_env\python.exe -m unittest discover -s tests/engine -t .
```

> 说明：`PYTHONPATH` 对这个仓库自带的嵌入式解释器**不起作用** ——
> `python_env\python310._pth` 处于隔离模式，`aifootball` 包靠该文件里的
> `..\src\AIfootball.Engine` 找到，测试命令因此不需要额外设环境变量。

单独跑某个文件：

```bat
python_env\python.exe -m unittest discover -s tests/engine -t . -p test_geometry.py
```

### 若顶层包名冲突

`site-packages` 里有一个 ultralytics 自带的 `tests` 包，它和本仓库的
`tests/` 同名。上面命令能正常跑，是因为 `python_env\python310._pth` 中
`..`（仓库根）排在 `Lib\site-packages` **之前**。若该文件被重装环境覆盖、
顺序恢复成 site-packages 在前，`-t .` 会报
`ModuleNotFoundError: No module named 'tests.engine'`，此时两种改法都行：

```bat
:: 1) 把顶层目录指到 tests/，包名变成 engine，绕开同名冲突
python_env\python.exe -m unittest discover -s tests/engine -t tests

:: 2) 直接把 start dir 当顶层目录
python_env\python.exe -m unittest discover -s tests/engine
```
