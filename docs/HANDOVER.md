# AIfootball 交接文档（Handover）

> 当前 Windows 克隆、构建、运行和分发流程见 [README 快速开始](../README.md#快速开始)。本文后续提到的仓库内 `python_env` 是历史环境布局。

> 最后更新：2026-10-19 · 版本 3.0.0 · 适用于 `D:\AIfootball`
> 读者：接手本项目的开发者 / 下一个 AI 会话。
> 阅读顺序建议：**第 1 节（当前状态）→ 第 3 节（架构）→ 第 8 节（怎么跑起来）→ 第 11 节（坑）**。
> 接了姿态预测的活，直接跳到 **第 16 节**（C2 数据集真相 / 为什么真实数据无信号 / 下一步）。

---

## ⚠️ 0. 2026-10-20 UI / Unity 回退（动手前必读）

本会话按用户要求把**界面**和**Unity 工程**回退到了重构前的备份版，引擎层保持不变。本文档第 1、9、13 节里描述的新版 UI 已经不在了，别照着找文件。

**UI 已回退**

- `src\AIfootball.App` 恢复为重构前的单体 WPF：`MainWindow.xaml` + `DashboardPage` / `PipelinePage` / `CalibrationPage` / `ResultsPage` + `Themes\Colors.xaml` + `Themes\Styles.xaml` + `StartupWindow`。
- `src\AIfootball.sln` 也换回只含 `AIfootball.App` 一个工程；根目录的 `AIfootball.sln` 已删除。
- 我上一轮加的设计系统（`Design\Tokens.xaml`、`Design\Controls.xaml`、`Design\Icons.xaml`）、`Views\Pages\*`、`Diagnostics\UiDiagnostics.cs`、`app.manifest` 全部移除。
- 因此 **`AIfootball.exe --diagnose` 离屏自检已不存在**；§1 表格里的 `--diagnose` 行、§9 里"卡片无边框 / 选中态只用文字色"的理由行、§13 里 `Design/`、`Views/` 的索引均已过时。`output\ui_diagnose\` 里还留着回退前的四张页面图与 `window.png`，只能当历史参考，无法再生成。
- 现在的构建与启动方式：`dotnet build src\AIfootball.sln -c Release`，产物在 `src\AIfootball.App\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\AIfootball.exe`。

**Unity 已完整回退**

- `Myproject\`（`Assets` / `ProjectSettings` / `Packages` / `Library` / `Builds` / `UserSettings`）按备份快照整目录同步，文件集合与备份逐一对齐（406 个 Assets 文件、0 多 0 少）。
- `runtime\` 的播放器产物（`Myproject.exe`、`Myproject_Data\`、`UnityPlayer.dll`、`UnityCrashHandler64.exe`、`MonoBleedingEdge\`）同样恢复；`runtime\data`、`runtime\models` 未动。
- 门将恢复"全知"：C1 观测降级、`Scripts\Simulation\ShotEvaluationRunner.cs`、`ShotObservationModel.cs` 及其 `.meta` 一并消失，`aifootball evaluate` 不再驱动 Unity 批量回放（命令仍能跑，只统计 Python 侧数据）。
- 已验证：`Myproject.exe --csv data\sample1_trajectory.csv` 能正常载入 127 个轨迹点（`[GameManager] 已载入 127 个轨迹点，总时长 1.048 秒`），CSV 坐标契约（`x=前, y=门将右侧, z=上`）与 `export_unity.py` 仍然一致。

**保留下来的（未被回退）**

- `src\AIfootball.Engine`：含 `pose` 子命令与 C2 全部代码、`cli.py`、`calibration/`、`pipeline/`，137 个 Python 测试通过；
- `src\AIfootball.Core` 与 `tests\`：44 个 C# 测试仍在磁盘上且可跑（`dotnet test tests\AIfootball.Core.Tests\AIfootball.Core.Tests.csproj`，已验证 44/44 通过），但已不在 `src\AIfootball.sln` 里；
- `output\pose\final\`（C2 报告与结果）、本文档、`README.md`。

**备份快照（不要删）**

- `D:\AIfootball_backup_20261019_1755\` —— 重构前基线，本次回退的来源；
- `D:\AIfootball_snapshot_20261020\` —— 回退前（新架构 UI + C2）的完整快照，要找回新版界面时从这里取；
- `D:\AIfootball_runtime_backup_20261019_233820\` —— 早前 `runtime\` 的播放器产物备份（路径记录在 `output\runtime-backup-path.txt`）；
- `D:\AIfootball.zip` —— 更早的整包备份。

---

## 1. 当前状态：可以跑，且全部验证通过

> 下表记录的是**回退前**的验收结果。UI 回退后 `--diagnose`、`dotnet test AIfootball.sln` 两行不再适用（见第 0 节）。

| 检查项 | 命令 | 结果 |
| --- | --- | --- |
| 解决方案构建 | `dotnet build AIfootball.sln` | ✅ 0 警告 0 错误 |
| C# 单元测试 | `dotnet test AIfootball.sln` | ✅ 44/44 通过 |
| Python 引擎测试 | `python_env\python.exe -m unittest discover -s tests/engine -t .` | ✅ 137/137 通过 |
| 界面离屏自检 | `AIfootball.exe --diagnose` | ✅ 退出码 0，4 个页面 0 缺陷、0 绑定告警（约 6.5 s） |
| 引擎 CLI | `python_env\python.exe -m aifootball --help` 及各子命令 | ✅ 10 个子命令全部正常（新增 `pose`） |
| 门将手感验收（最终，162 发 5×3） | `aifootball evaluate --shots 162 --columns 5 --observation on --noise 0.80 --expect-save-rate 0.18 0.30 --expect-direction 0.55 0.75` | ✅ 扑救率 24.1%、方向 72.8%（见 §15） |
| 门将手感验收（快速门禁，27 发 3×3） | `aifootball evaluate --shots 27 --observation on --expect-save-rate 0.18 0.30` | ✅ 只验扑救率；方向准确率在 3 列下无意义（见 §15.3） |
| 姿态方向基线（可行性 C2） | `aifootball pose run --out output\pose\final` | ✅ 组 1 82.2%、组 2 AUC 0.499（与标签置换不可区分，见 §16） |

`--diagnose` 的产物在 `output\ui_diagnose\`：

- `report.txt`：每页的元素数、版面尺寸快照、零尺寸元素、溢出元素、被截断文本、对比度不足文本、数据绑定告警；
- `page-Overview.png` / `page-Capture.png` / `page-Calibration.png` / `page-Replay.png`：1240×728 离屏渲染图（无头环境下的"肉眼"）；
- `window.png`：**整窗**渲染（标题栏 + 导航 + 页面同框）。判断"选中态、层次、导航"只能看这张，
  单独的 page 图不包含导航栏。

**一句话状态**：三维重建、弹道拟合、Unity 内嵌回放、门将建模、UI 重构的代码全部完成并通过自检；
门将"观测降级"（可行性报告修正 C1）已实现并完成端到端标定（§15）；
射手姿态→方向预测基线（修正 C2）已实现并跑完，结论是"合成域可行、真实数据无信号"（§16）；
唯一没有真正"跑到底"的是 Unity 播放器的画面（需要人眼确认，见第 12 节）。

### 本轮重构（相对重构前）

- **架构**：单体工程拆成 `AIfootball.Core`（无 UI 的领域层）+ `AIfootball.App`（WPF）+ `aifootball`（Python 包），
  路径真相收敛到 `AifootballWorkspace` 一处，Python 侧改为单进程 `python -m aifootball <子命令>` 调用。
- **UI**：保留**原来那套浅色专业工具配色**（主蓝 `#2F6FAD`、青绿 `#23836B`、白卡片、`#EEF1F4` 底色、`#102B37` 深青标题栏），
  在此基础上升级为完整设计系统（Token 化 + 控件模板 + 自绘图表）。**深色"指挥中心"风格已被明确否决，不要改回去。**
- **Unity**：恢复**原项目的内嵌方式**——`HwndHost`（`UnityHostView`）把 Unity 播放器作为子窗口嵌进 WPF 页面，
  不是独立窗口，也不是 WebGL/纹理方案。独立窗口只作为兜底按钮保留。
- **建模（新增）**：门将扑救模型 `SaveModel`、对位模型 `MatchupModel`、射手画像落点 `ShotPlacement`，
  以及门将/射手可视化（3×5 热区、覆盖椭圆、落点散点、站位图）。
- **清理**：删除了重复的旧管道脚本（`runtime\project\*.py` 已并入 `aifootball` 包）、废弃的 MSIX 打包工程
  （`src\AIfootball.Package`、`packages\`）、根目录安装日志（`install-log.txt`/`install-run.ps1`）、
  临时调试脚本目录 `.agents\`、Unity 构建与编辑器用户设置（`Myproject\Builds`、`Myproject\UserSettings`）、
  各处构建残留。**被代码/文档引用的数据资产一个都没删**（`data\external`、`report_assets`、
  引擎内标定照片均已确认在位，见第 4 节"不要当成冗余删掉的资产"）。

### 备份

重构前的完整快照在 **`D:\AIfootball_backup_20261019_1755`**（robocopy /MIR，已逐字节校验）。
它**不属于**本项目交付物，确认无需回滚后可自行删除。

---

## 2. 项目是什么

面向**点球训练**的 Windows 桌面分析应用：

1. 左右两台相机（双目）各拍一段视频；
2. YOLO11 检测足球中心 → 用已标定的双目参数三角化出三维点 → 时序滤波；
3. 弹道拟合（重力模型 + 鲁棒内点）得到可回放轨迹；
4. 导出 Unity 回放的 CSV，并在**内嵌 Unity 播放器**里回放门将扑救；
5. WPF 侧同时给出射门热区、门将覆盖范围、射手画像与"射手 × 门将"对位胜率。

硬件基线：CPU 推理（无 CUDA）、`net8.0-windows10.0.19041.0`、Win10/11 x64。

---

## 3. 架构总览

```text
┌─────────────────────────────── WPF (AIfootball.App) ───────────────────────────────┐
│ MainWindow（自绘铬条 / 导航轨 / 状态栏 / 日志抽屉 / 通知）                            │
│  ├ OverviewPage    环境就绪度、KPI、交付列表、射门热区、对位建模                       │
│  ├ CapturePage     样本选择、双摄实时预览、离线批处理、阶段进度                        │
│  ├ CalibrationPage 内参/外参标定流程与结果                                            │
│  └ ReplayPage      ← HwndHost(UnityHostView) 嵌入 Unity 播放器 + 轨迹投影 + 对位面板   │
│ ViewModels(Shell/Overview/Capture/Calibration/Replay) + WorkspaceState(共享状态)      │
└───────────────┬──────────────────────────────────────────────┬───────────────────────┘
                │ 引用（只单向依赖）                             │ 子进程 + 文件
        ┌───────▼─────────────────────┐              ┌──────────▼──────────────────────┐
        │ AIfootball.Core（无 UI 依赖）│              │ aifootball（Python 引擎包）      │
        │ Workspace 路径真相           │              │ cli / capture / calibration /    │
        │ PythonEngine（进程宿主）      │              │ pipeline(reconstruction,         │
        │ Services（扫描/解析/运行/安装）│              │          ballistics, live,       │
        │ Models（样本/门将/轨迹/模型）  │              │          export_unity)           │
        └─────────────────────────────┘              └──────────┬──────────────────────┘
                                                                 │ CSV + 命令文件
                                                        ┌────────▼────────┐
                                                        │ Unity 播放器     │
                                                        │ runtime\*.exe   │
                                                        └─────────────────┘
```

**依赖方向**：`App → Core`，`Core` 不认识 WPF，`Python` 不认识 .NET。任何跨层通信只走
**进程 + 文件**（CSV / JSON / 命令文件 / 预览帧），没有隐藏耦合。

---

## 4. 目录结构（这是整理后的"应有样子"）

```text
D:\AIfootball
├─ AIfootball.sln              # 解决方案（Core + App + Tests）
├─ README.md                   # 门面文档（快速开始）
├─ docs/                       # 设计/可行性/交接文档
│   └─ HANDOVER.md             # ← 本文
├─ src/
│   ├─ AIfootball.Core/        # 领域层（无 UI）
│   │   ├─ Workspace/          # AifootballWorkspace：唯一路径真相 + 工作区探测
│   │   ├─ Engine/             # PythonEngine：python -m aifootball 宿主
│   │   ├─ Services/           # 扫描/读取/运行/解析/安装/Unity 启动/射手画像
│   │   └─ Models/             # Sample/GoalkeeperProfile/Trajectory/SaveModel/...
│   ├─ AIfootball.App/         # WPF 应用（net8.0-windows）
│   │   ├─ Design/             # Tokens.xaml / Controls.xaml / Icons.xaml（设计系统）
│   │   ├─ Views/              # MainWindow + Pages + Controls(UnityHostView/图表)
│   │   ├─ ViewModels/         # Shell/Overview/Capture/Calibration/Replay
│   │   ├─ Services/           # 通知中心/预览泵/文件选择/IUnitySurface
│   │   ├─ Diagnostics/        # UiDiagnostics（--diagnose 离屏自检）
│   │   ├─ Assets/             # 品牌图标（Brand/mark.png、logo.png、AIfootball.ico）
│   │   └─ WorkspaceState.cs   # 跨页共享的可观察状态
│   └─ AIfootball.Engine/      # Python 引擎（打包时被复制到输出目录 python_engine\）
│       ├─ aifootball/         # 包本体（cli/workspace/constants/control/preview/evaluation/...）
│       │   └─ pose/           # 姿态方向基线（C2）：dataset/body_frame/synthetic/model/train/evaluate/experiment
│       ├─ requirements.txt
│       └─ tests? → 实际测试在仓库 tests/engine/
├─ tests/
│   ├─ AIfootball.Core.Tests/  # xUnit（44 个用例：球门几何/扑救模型/对位模型）
│   └─ engine/                 # unittest（137 个用例：CLI/工作区/几何/预览/控制/评估/姿态）
├─ samples/sample1..3/         # 训练样本（left/recording.mp4 + right/recording.mp4）
├─ calib/                      # 相机标定产物（*.npz / *_extrinsics.json / *_summary.json）
├─ data/                       # 门将配置（goalkeepers/*.json）+ 回放 CSV + external/（3DSP 数据集，327 MB）
├─ models/yolo11m.pt           # 检测模型（约 39 MB）
├─ runtime/                    # Unity 播放器（Myproject.exe + *_Data + UnityPlayer.dll）
├─ python_env/                 # 内嵌 CPython 3.10（torch/ultralytics/opencv/scipy/numpy）
├─ Myproject/                  # Unity 工程源码（C# + Assets）
├─ mobile-camera-bridge/       # 安卓局域网相机（替代 IP Webcam）
├─ installer/                  # Inno Setup 脚本 + 安装包构建脚本
├─ scripts/                    # build_release.bat + 射手姿态导出 / 门将生成 / 测试数据生成
├─ report_assets/              # 汇报插图素材（fig_ballistic / fig_flow / fig_heatmap / fig_radar）
├─ assets/icons/               # 应用图标（源 PNG + .ico）
├─ assets/brand/source/        # 早期品牌（SpotIQ）设计源图（留档）
└─ output/                     # 运行期产物（git 忽略）：trajectory_3d/ ballistic/ live_preview/ ui_diagnose/
```

**规则**：`output/`、`logs/`、`bin/`、`obj/`、`__pycache__/` 全部属于运行期产物，不进版本库、不进交付清单。

**不要当成"冗余"删掉的资产**（曾经误删过一次，已恢复）：

| 路径 | 依赖它的东西 |
| --- | --- |
| `data/external/3dsp/` | `scripts/export_shooter_pose.py` 提取真实球员骨骼（`docs/shooter-setup-guide.md` 第 1 步），删了射手助跑动画就没有数据来源 |
| `src/AIfootball.Engine/aifootball/calibration/assets/IMG_20260314_*.jpg` | `calibration/extrinsics.py` 的 `ASSETS_DIR`（**包内相对路径**），是外参标定的两张预设参考照片；`AIfootball.App.csproj` 已加规则把它们复制到 `python_engine/aifootball/calibration/assets/` |
| `report_assets/` | 汇报/论文插图原始素材，不是缓存 |
| `assets/brand/source/SpotIQ*` | 早期品牌设计源图，留档用 |

> 判断标准：**被代码或文档按路径引用的文件 = 资产，不是冗余**。清理前用
> `git grep -n "<文件名>"` 或 `Select-String -Path docs\*.md -Pattern "<路径>"` 确认一遍。

---

## 5. 关键路径与契约

### 5.1 工作区解析（`AifootballWorkspace.Resolve`）

优先级：显式参数 → 环境变量 `AIFOOTBALL_WORKSPACE` → 从 `AppContext.BaseDirectory` **向上查找布局特征** → 当前目录。

布局特征（`LooksLikeWorkspace`）：存在引擎包（`src\AIfootball.Engine\aifootball` 或
`python_engine\aifootball` 或 `engine\aifootball`）**且**至少存在 `python_env` / `samples` / `calib` / `models` 之一。
因此开发目录与发行包根目录都能被正确识别。

### 5.2 Python 引擎调用

```text
python_env\python.exe -m aifootball <子命令> --workspace <工作区根>
环境变量：PYTHONPATH=<EngineDir>；AIFOOTBALL_WORKSPACE=<工作区根>
```

| 子命令 | 用途 |
| --- | --- |
| `offline --samples A B [--skip-reconstruct] [--skip-ballistic] [--imgsz N] [--conf F]` | 离线批处理 |
| `online --cam-left <src> --cam-right <src> --sample <name>` | 录制 + 分析 |
| `online-live ... [--analyze-every N] [--no-save]` | 边采边分析 |
| `teststream --source <src>` | 单路取流自检 |
| `calibrate-intrinsics` / `calibrate-extrinsics` | 内参 / 外参标定 |
| `list-cameras` | 枚举本机相机 |
| `selfcheck` | 单行 JSON 输出环境能力（供 WPF 解析） |
| `evaluate --shots N [--columns C] [--observation on\|off] [--noise F] [--expect-save-rate MIN MAX] [--expect-direction MIN MAX]` | 端到端扑救率验收：生成射门集 → 驱动 Unity 批处理回放 → 汇总（见 §15） |

**长驻进程协议**：`online` / `online-live` 启动后从 stdin 读行，`start` 开始录制、`stop` 结束并收尾；
WPF 关闭抽屉或退出时会结束子进程。

**实时预览**：引擎把最新一帧写成 JPEG：`output/live_preview/live_preview_left.jpg`、`..._right.jpg`；
WPF 用 10 Hz 的 `PreviewPump` 读取并 `BitmapImage` 显示（避开文件占用）。

### 5.3 回放 CSV（给 Unity）

```text
文件：data\<sample>_trajectory.csv     头：time,x,y,z
坐标：x = 向前（朝门，米）、y = 门将右手侧、z = 高度
例：  0.000000,11.0,0.0,0.11
同目录：<sample>_trajectory.meta.json  （记录该轨迹绑定的门将，键名 goalkeeper）
```

### 5.4 WPF → Unity 命令通道

```text
启动：runtime\Myproject.exe --csv <csv> --goalkeeper <name> --wpf-host --wpf-command <file>
      -parentHWND <hwnd> delayed  -popupwindow -screen-fullscreen 0
命令文件：runtime\data\wpf-unity-command.txt   格式：第一行序号，第二行命令
命令：csv:<path> | replay | reset | view | speed:<float> | goalkeeper:<name> | goalkeeper:previous | goalkeeper:next
```

### 5.5 门将 JSON（`data/goalkeepers/*.json`）

键：`DisplayName`、`Description`、`Height`、`DiveSpeed`、`JumpDistance`、`JumpHeight`、`Reach`、
`TendGoalSpeed`、`TendGoalDistance`、`GoalKeeping`、`SidePreference`、`HeightPreference`、
`ProbabilityMap`（15 个键：`Low/Mid/High` × `Left/MidLeft/Center/MidRight/Right`）。

### 5.6 门线与区域划分

球门 7.32 × 2.44 m，点球点 11 m；门平面分 **3 行 × 5 列**，键名与 Unity 侧完全一致。
`GoalGeometry.ZoneKey(lateral, height)` 是唯一映射函数（有单元测试锁定往返一致性）。

---

## 6. 领域层（AIfootball.Core）

| 文件 | 职责 |
| --- | --- |
| `Workspace/AifootballWorkspace.cs` | 所有磁盘路径 + 工作区探测 + `EnsureRuntimeDirectories()` |
| `Engine/PythonEngine.cs` | 启动/停止 Python 子进程、注入环境变量、按行收集 stdout/stderr |
| `Services/SampleCatalog.cs` | 扫描 `samples/`、判断样本完整度、扫描 `data/*.csv` 交付物 |
| `Services/CalibrationReader.cs` | 读 `calib/*_summary.json` / `*_extrinsics.json`，给出就绪状态与摘要 |
| `Services/TrajectoryStore.cs` | 读写 `data/*_trajectory.csv`（含头校验、缺列容错） |
| `Services/PipelineRunner.cs` | 离线/在线流程编排：选样 → 调引擎 → 解析进度 → 落盘 |
| `Services/PipelineProgressParser.cs` | 解析引擎日志标记（`[N/3] 3D 重建`、`[N/3] 弹道拟合`、`[N/3] Unity 导出`、`[sample] ...`） |
| `Services/EnvironmentInstaller.cs` | 一键安装/修复 Python 环境（pip 安装 + 模型下载） |
| `Services/GraphicsProbe.cs` | 探测 CPU/GPU 与推理设备标签 |
| `Services/LogBus.cs` | 内存 + 文件日志（`logs/aifootball-YYYYMMDD.log`） |
| `Services/UnityViewerService.cs` | 发现 `runtime\` 下的 Unity 播放器、拼装命令行参数、写命令文件 |
| `Services/ShooterProfileService.cs` | 由轨迹推导射手画像（去重、力量/稳定性/偏好/落点） |
| `Models/SaveModel.cs` | 门将扑救模型 + 对位模型 + 球门几何（**新增，含全部单元测试**） |
| `Models/Trajectory.cs` | 轨迹、速度、最高点、门线穿越插值 |
| `Models/GoalkeeperProfile.cs` | 门将数据 + 雷达轴 + 左右侧均值 |
| `Models/{Sample,CalibrationSnapshot,EngineReport,PipelineModels}.cs` | 其余数据结构 |

---

## 7. 界面层（AIfootball.App）

### 7.1 设计系统

- `Design/Tokens.xaml`：颜色、渐变、阴影、字体、字号、圆角、间距。"日间分析台"语义命名
  （`InkSurface`/`LineHair`/`TextHi`/`AccentBlue`…），改主题只改这一处。
- `Design/Controls.xaml`：全套控件模板（按钮 4 种、导航项、分段标签、输入、下拉、滑块、滚动条、
  进度条、MeterBar、胶囊、卡片壳、分隔线、ToolTip、列表行）与文本样式
  （`TextDisplay/Title/Section/Body/Caption/Eyebrow/Metric/Mono/Icon`）。
  **注意**：隐式 `TextBlock` 样式故意不设 `Foreground`，否则会压过按钮模板的继承，导致实心按钮文字变灰。
- `Design/Icons.xaml`：30+ `StreamGeometry` 图标（`Path Data={StaticResource IconXxx}`）。

### 7.2 窗口骨架（`Views/MainWindow.xaml`）

自绘标题栏（`WindowChrome`，48 px，深青 `#102B37`）+ 左侧 236 px 导航轨 + 内容区 + 30 px 状态栏；
底部日志抽屉（Ctrl+L 开合）、右上通知栈。页面**常驻**，切页只改可见性——
这样 Unity 子窗口句柄不会被销毁重建。

### 7.3 内嵌 Unity（关键！）

`Views/Controls/UnityHostView.cs`：`HwndHost` 子类。

1. `BuildWindowCore` 建一个 `STATIC` 子窗口占位；
2. `AttachAsync(process)` 轮询查找 Unity 的 HWND（先找子窗口，再退回顶层窗口），
   要求句柄稳定 1 s，然后去掉 `WS_POPUP/WS_CAPTION/WS_THICKFRAME`、加 `WS_CHILD`、
   `SetParent` 并**校验**父子关系，最后 `ResizeUnity()` 让它铺满占位窗口；
3. `OnWindowPositionChanged` 用物理像素重设 Unity 窗口尺寸（DPI 安全）；
4. `WndProc` 处理 `WM_ERASEBKGND`/`WM_PAINT`（画深色底，避免闪烁）与 `WM_MOUSEACTIVATE`（点击即把焦点给 Unity）。

**空域规则（务必遵守）**：`HwndHost` 的子窗口永远浮在 WPF 内容之上。因此：
- 页面不可见时 `SetSuspended(true)` 隐藏 Unity（`ReplayPage.xaml.cs` 监听 `IsVisibleChanged`）；
- 任何 WPF 浮层（日志抽屉、通知）出现时也要隐藏——由 `WorkspaceState.IsOverlayOpen` 驱动
  （`ReplayViewModel.ShowUnitySurface = IsViewerRunning && !IsOverlayOpen && !ShowPlotAnalysis`）。

### 7.4 自绘图表（无第三方图表库）

| 控件 | 用途 |
| --- | --- |
| `Controls/Primitives.cs` | `ArcGauge`（环表）、`RadarChart`（雷达）、`Sparkline`（趋势）、`PulseDot`（呼吸点）、`BrandMark` |
| `Controls/TrajectoryPlot.cs` | 三维轨迹的门平面投影 + 侧视/俯视，带动画播放 |
| `Controls/GoalZoneMap.cs` | 3×5 扑救概率热区 + 覆盖椭圆 + 射门落点（散点/主标记/判定文字） |

### 7.5 页面

| 页面 | 内容 |
| --- | --- |
| 总览 | 环境横幅、4 张 KPI、训练方案、最近交付、射门热区、对位建模卡片 |
| 视频采集与解算 | 左列样本勾选 + 参数 + 门将；右列双摄预览、采集控制条（两行布局）、阶段进度 |
| 相机标定 | 左列内参/外参流程 + 任务状态；右列当前标定结果、完成度环表、产物清单 |
| 轨迹回放 | 左列轨迹/门将；中间内嵌 Unity 画布 + 播放控制条（窄窗口自动换行）；右列热区/对位/射手画像 |

---

## 8. 怎么跑起来

```powershell
cd D:\AIfootball

# 构建全部（Core + App + Tests）
dotnet build AIfootball.sln

# 运行应用（等价：直接跑下面这个 exe）
dotnet run --project src\AIfootball.App\AIfootball.App.csproj

# exe 路径（调试版）
src\AIfootball.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\AIfootball.exe

# 单元测试
dotnet test AIfootball.sln
python_env\python.exe -m unittest discover -s tests/engine -t .

# 界面自检（无头：离屏布局 + 渲染 + 报告，退出码 0 表示无缺陷）
src\AIfootball.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\AIfootball.exe --diagnose

# 门将手感验收（需要 runtime\Myproject.exe）：扑救率必须落在 18%–30%
# ① 快速门禁：27 发 3×3（约 1 分钟）——日常改完代码先跑这个
python_env\python.exe -m aifootball evaluate --shots 27 --observation on --expect-save-rate 0.18 0.30
# ② 最终验收：162 发 5×3（约 20 分钟，含方向准确率门禁）——发布前跑，见 §15
python_env\python.exe -m aifootball evaluate --shots 162 --columns 5 --observation on --noise 0.80 --expect-save-rate 0.18 0.30 --expect-direction 0.55 0.75
# ③ 对照臂：关闭观测降级（门将拿真值），扑救率应显著高于 ②（"作弊被移除"的量化证据）
python_env\python.exe -m aifootball evaluate --shots 162 --columns 5 --observation off --out output\evaluation\control
# ④ 只汇总已有结果（不重跑 Unity，秒级）
python_env\python.exe -m aifootball evaluate --summarize-only output\evaluation\verify-final\results.jsonl

# 射手姿态 → 方向基线（C2，纯 CPU，不碰 Unity/相机；见 §16）
# ① 数据勘察（秒级，不加载 torch）
python_env\python.exe -m aifootball pose inspect
# ② 完整三组 + 诊断（约 7 分钟）
python_env\python.exe -m aifootball pose run --out output\pose\final
# ③ 冒烟（约 2 分钟）
python_env\python.exe -m aifootball pose run --max-sources 40 --folds 2 --transfer-epochs 3 --permutation-trials 1
```

**注意**：重新构建前先结束正在运行的实例，否则会 `MSB3021/MSB3027`（文件被占用）：

```powershell
Get-Process -Name AIfootball -ErrorAction SilentlyContinue | Stop-Process -Force
```

发布包（Inno Setup 前置产物）：

```powershell
scripts\build_release.bat          # 产出 dist\app\ 与 dist\AIfootball-Portable-v3.0\
installer\build_installer.bat      # 需要 Inno Setup 6
```

`build_release.bat` 依赖：`src\AIfootball.App\AIfootball.App.csproj`（发布）、工程内的
`runtime\Myproject.exe`（Unity 播放器）、`data\goalkeepers`、`models`、`samples\sample1..3`，
并会校验发布输出里存在 `python_engine\aifootball\__main__.py`。

---

## 9. 本轮"故意为之"的取舍（不要轻易推翻）

| 决策 | 理由 |
| --- | --- |
| 保留浅色专业配色，只做深化 | 用户明确否决深色方案，要求"保持原来的配色"。 |
| Unity 用 `HwndHost` 内嵌 | 用户明确要求"和原来一样内嵌"；性能优于 `D3DImage`/WebGL 方案。 |
| 不用第三方图表库 | 自绘 `DrawingContext` 更轻、更快、样式完全可控（启动无额外依赖）。 |
| Python 单进程、多子命令 | 避免每个功能各自一套脚本；进度只解析一套日志格式。 |
| 路径只在 `AifootballWorkspace` 出现 | 消灭 "services 各自拼路径 + 依赖 cwd" 的老毛病。 |
| 页面常驻、切页改可见性 | 保住 Unity 句柄与采集状态；切页零重建。 |
| 去掉 `System.Drawing.Common` / `Microsoft.Xaml.Behaviors.Wpf` / MSIX 工程 / 重复图表控件 | 冗余依赖与死代码，替换为纯 WPF + 原生实现。 |
| `ArcGauge` 等自定义依赖属性默认 OneWay | 只读 VM 属性绑定 `Slider/ProgressBar.Value` 会抛 "无法进行 TwoWay 绑定"。 |
| 卡片不描边，靠底色差 + 大圆角 + 柔投影分层 | 1px 描边会把卡片"框死"；浅灰画布（`#EEF1F4`）与白卡本身对比够，描边是多余的视觉噪声。 |
| **选中态一律用主色文字 + 加粗，不铺底色** | 铺灰底会和 hover 态撞色，用户分不清"选中"还是"鼠标在上面"，且灰色填充读起来像"禁用"。导航额外加左侧 3px 主色标记条（位置指示，不是填充）；只有下拉浮层这类需要逐行扫读的列表才用浅色块 + 对勾。 |

---

## 10. 验证方法（改完代码怎么自证）

1. `dotnet build AIfootball.sln` → 必须 0 警告 0 错误；
2. `dotnet test AIfootball.sln` + Python unittest → 必须全绿；
3. `AIfootball.exe --diagnose` → 退出码 0，`report.txt` 里每页 4 项检查全 ✓、绑定告警 0；
4. 需要看图时打开 `output\ui_diagnose\page-*.png`；
5. 界面改动建议同时跑一次真实启动（`AIfootball.exe`），确认没有 `App.xaml.cs` 里那套
   "启动即崩"兜底日志（崩溃会把完整异常写入 `logs\` 并弹窗）。

---

## 11. 踩过的坑（排障手册）

| 现象 | 原因 / 处理 |
| --- | --- |
| `MSB3027/MSB3021` 文件被占用 | 先 `Stop-Process -Name AIfootball -Force`。 |
| `无法对"…"类型的只读属性"…"进行 TwoWay 绑定` | `ProgressBar.Value`/`Slider.Value` 默认 TwoWay，只读属性必须显式 `Mode=OneWay`。 |
| 离屏自检/初始化卡死 | **绝不要**在 UI 线程 `task.GetAwaiter().GetResult()`（`ConfigureAwait(true)` 续体死锁），要用 `DispatcherFrame` 泵消息（见 `UiDiagnostics.Pump`）。 |
| 实心按钮上的字是灰的 | 隐式 `TextBlock` 样式若设了 `Foreground`，会压过按钮模板的继承。 |
| Unity 画面盖住日志抽屉/弹窗 | 空域问题：浮层出现时必须 `SetSuspended(true)`，靠 `IsOverlayOpen` 驱动。 |
| 切页后 Unity 变黑/不跟随 | 页面 `IsVisibleChanged` 未恢复；`UnityHostView` 需要在可见时重新 `ResizeUnity()`。 |
| XAML 里 `StringFormat=… {0} …` 报错 | 以 `{}` 开头转义：`StringFormat={}{0} 次射门`。 |
| `-- 注释` 在 XAML 中非法 | XAML 注释是 `<!-- -->`；`--` 只能出现在注释内部。 |
| 构建报 `MC3089 ScrollViewer 只能接受一个子级` | 改 XAML 时把兄弟元素塞进了 ScrollViewer，注意闭合位置。 |
| `--diagnose` 退出码非 0 | 看 `report.txt` 的分类计数；退出码 = 出问题的检查项数量。 |
| Python 侧 `ModuleNotFoundError: aifootball` | `PYTHONPATH` 未指向 `EngineDir`；确认 `src\AIfootball.Engine\aifootball` 或 `python_engine\aifootball` 存在。 |
| 日志在哪里 | `logs\aifootball-YYYYMMDD.log`；界面内 Ctrl+L 打开日志抽屉。 |

---

## 12. 已知缺口 / 下一步（按优先级）

1. **Unity 内嵌画面的人工确认**（唯一未"跑到底"的环节）。
   代码路径：`ReplayPage` → `LaunchViewerCommand` → `UnityViewerService` 启动
   `runtime\Myproject.exe` → `UnityHostView.AttachAsync` 子窗口挂接。
   需要人眼确认：画面是否铺满画布、窗口缩放是否跟随、切页/开抽屉是否正确隐藏、播放控制是否生效。
   若挂接失败，看 `ReplayViewModel.Surface.LastAttachError` 与日志里的 HWND 探测记录。
2. **安装包回归**：`installer\AIfootball.iss` 已按新布局修正（`python_engine\`、命令文件、样例目录），
   版本号已与 `AIfootball.App.csproj` 统一为 `3.0.0`（`build_release.bat` 的 portable 目录同步为 `v3.0`），
   但尚未真正跑一次 Inno Setup 打包 + 全新机器安装验证（本机未装 Inno Setup 6）。
3. **门将/射手建模增强**：当前把"反应时间 → 可用时间 → 覆盖包络"作为物理层，区域概率作统计层。
   可继续做：射手脚型（内脚背/正脚背）对侧旋的修正；**门将初始站位偏移**（`SidePreference`
   已参与扑救目标偏置，见 `InterceptShotMainState`，但开球前的站位尚未按它偏移）；二次反应（补射）场景。
4. **在线采集的真实相机联调**：`online`/`online-live` 与 `mobile-camera-bridge` 的端到端联调
   依赖现场设备；代码与 CLI 已验证，画面质量需实测。
5. **性能**：CPU 推理仍是瓶颈（`--imgsz` 可调）。若要提速可考虑 ONNX Runtime / OpenVINO 后端，
   但需保持 `selfcheck` 契约不变。
6. **可观测性**：可把 `--diagnose` 接入 CI（离屏渲染无需显卡，退出码即门禁）。
7. **观测模型参数已完成端到端标定（C1 落地，见 §15）**：`reactionDelay = 0.18 s`、`confidence = 0.66`、
   `noise = 0.80 m` 是唯一一组"扑救率与方向准确率同时落在验收区间"的参数（162 发实测
   扑救率 24.1%、方向 72.8%）。它们的作用是让相对比较成立（同一套参数下对比不同门将/不同球速），
   **不是**"绝对还原某位真实门将"；拿到实测比赛数据后应回填这三个数（方法与口径见 §15）。
8. **射手姿态方向预测（C2 已落地，见 §16）**：合成域可行（82.2%），真实 3DSP 上
   与标签置换不可区分（AUC 0.499 vs 0.468±0.041）。下一步不是调模型，而是补信息源：
   脚部关键点、球相对脚的位置、连续落点标签、少量真实方向标注。**不要**用组 1 的 82%
   对外宣称"方向预测准确率 82%"。目标与口径见 §16.9。

---

## 13. 快速索引（我要改 X，去哪个文件）

| 我要改… | 去… |
| --- | --- |
| 配色 / 字号 / 圆角 | `src\AIfootball.App\Design\Tokens.xaml` |
| 按钮、输入框、卡片等控件外观 | `src\AIfootball.App\Design\Controls.xaml` |
| 新增图标 | `src\AIfootball.App\Design\Icons.xaml` |
| 标题栏 / 导航 / 状态栏 / 抽屉 | `src\AIfootball.App\Views\MainWindow.xaml(.cs)` |
| 某个页面的布局 | `src\AIfootball.App\Views\Pages\*Page.xaml` |
| 页面的业务逻辑 | `src\AIfootball.App\ViewModels\*ViewModel.cs` |
| 跨页共享状态（就绪度/样本数/浮层） | `src\AIfootball.App\WorkspaceState.cs` |
| Unity 内嵌与窗口挂接 | `src\AIfootball.App\Views\Controls\UnityHostView.cs` |
| 轨迹投影 / 热区图 | `...\Views\Controls\TrajectoryPlot.cs` / `GoalZoneMap.cs` |
| KPI 趋势环/雷达等小图 | `...\Views\Controls\Primitives.cs` |
| 门将扑救与对位算法 | `src\AIfootball.Core\Models\SaveModel.cs` |
| 射手画像统计 | `src\AIfootball.Core\Services\ShooterProfileService.cs` |
| 磁盘路径 | `src\AIfootball.Core\Workspace\AifootballWorkspace.cs` |
| Python 子进程启动 | `src\AIfootball.Core\Engine\PythonEngine.cs` |
| 引擎子命令 | `src\AIfootball.Engine\aifootball\cli.py` |
| 三维重建 / 弹道拟合 | `...\aifootball\pipeline\reconstruction.py` / `ballistics.py` |
| 双摄取流 | `...\aifootball\capture\dual_camera.py` |
| 相机标定 | `...\aifootball\calibration\intrinsics.py` / `extrinsics.py` |
| 自检规则（新增检查项） | `src\AIfootball.App\Diagnostics\UiDiagnostics.cs` |
| 门将观测降级（延迟/量化/噪声/置信度） | `Myproject\Assets\SuperGoalie\Scripts\Simulation\ShotObservationModel.cs` |
| 起扑延迟与决策点接线 | `...\States\GoalKeeperStates\TendGoal\MainState\TendGoalMainState.cs`、`InterceptShot\MainState\InterceptShotMainState.cs` |
| 端到端扑救率验收（生成射门集/清单/汇总/门禁） | `src\AIfootball.Engine\aifootball\evaluation.py` + `cli.py` 的 `evaluate` |
| 批量回放与逐球结果记录 | `Myproject\Assets\SuperGoalie\Scripts\Simulation\ShotEvaluationRunner.cs` |
| 3DSP 读取 / 关节常量 / 触球帧 | `...\aifootball\pose\dataset.py` |
| 身体坐标系与 112 维特征 | `...\aifootball\pose\body_frame.py` |
| 合成方向数据（变形幅度在这） | `...\aifootball\pose\synthetic.py` |
| 姿态网络（GRU 编码器 / 三分类 / 二分类） | `...\aifootball\pose\model.py` |
| 训练与温度标定 | `...\aifootball\pose\train.py` |
| 指标（AUC / ECE / Brier） | `...\aifootball\pose\evaluate.py` |
| 三组实验编排 / 判定阈值 / 报告渲染 | `...\aifootball\pose\experiment.py` |

---

## 14. 一条命令的健康检查

```powershell
cd D:\AIfootball
Get-Process -Name AIfootball -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build AIfootball.sln -v q --nologo
dotnet test AIfootball.sln -v q --nologo
python_env\python.exe -m unittest discover -s tests/engine -t .
src\AIfootball.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\AIfootball.exe --diagnose
python_env\python.exe -m aifootball evaluate --shots 27 --observation on --expect-save-rate 0.18 0.30
# 期望：构建 0 警告 0 错误；44 + 103 个测试通过；诊断退出码 0；扑救率落在 18%–30%
```

`evaluate` 需要 `runtime\Myproject.exe`（Unity 播放器）。若播放器缺失，先重建：

```powershell
D:\Unity2021.3.x\2021.3.45f2c1\Editor\Unity.exe -batchmode -quit -nographics `
  -projectPath D:\AIfootball\Myproject -executeMethod BuildWindowsPlayer.Build `
  -logFile D:\AIfootball\output\unity-build.log
```

构建成功的判据是 **`runtime\Myproject_Data\Managed\Assembly-CSharp.dll` 的时间戳更新**，
而不是 `Myproject.exe`（后者只是启动器，脚本变化不会重建它）。

---

## 15. 观测降级（可行性报告修正 C1）——已落地并完成标定

> 背景：改动前，门将在球被踢出的**同一帧**就拿到球门平面上的**精确落点**（`BallHitTarget`），
> 于是"扑救"不是判断，而是执行（可行性报告 §1.2 称之为作弊）。本节记录把这层作弊替换成
> "门将只可能知道的观测"之后的设计、参数与验收口径。

### 15.1 门将现在"看到"什么

一次射门会被 `ShotObservationModel.Observe` 折成四件事：

| # | 降级项 | 做法 | 代码位置 |
| --- | --- | --- | --- |
| ① | **反应延迟** | 起扑发生在触球后 `reactionDelay × (该门将的方向修正 / 基准修正)`，延迟期间保持站位看球 | `TendGoalMainState.Instance_OnBallLaunched` |
| ② | **量化** | 落点只能分辨到 `GridColumns × GridRows` 格，扑救目标是**格心**而不是落点 | `ShotObservationModel.CellCenter/ColumnOf/RowOf` |
| ③ | **置信度收缩** | 不确定时决策点从格心向球门中心收缩（`confidence=1` 不收缩，`=0` 收缩一半路程） | `ShotObservationModel.Observe` 第 ③ 步 |
| ④ | **观测噪声** | 零均值高斯：`σ = noise × lerp(1.6, 0.4, confidence)`，纵向噪声取横向的 0.45（门将读高度更准） | `ShotObservationModel.Observe` 第 ① 步 |

**接线（谁用观测、谁用真值）**：

```text
Ball.PlayTrajectory 之前
  └─ TendGoalMainState.Instance_OnBallLaunched
       ├─ Observe(真值落点) → CurrentObservation / HasShotObservation
       ├─ PlannedSavePoint = 决策点（量化格心 + 置信度收缩）→ 世界坐标
       └─ reactionDelay > 0 ⇒ 延迟再切 InterceptShotMainState

InterceptShotMainState.Enter
  ├─ 决策目标      = PlannedSavePoint        ← 观测（把方向判断错误的代价留给"跑错位置"）
  ├─ 概率判定      = RollSaveAttempt(真值)   ← 真值（回答"这名门将面对这脚球的固有扑救能力"）
  └─ 手臂 IK 目标  = PlannedSavePoint        ← 观测（否则真值从手臂通道回流）
```

三条不能推翻的取舍（每条都有实测依据）：

1. **概率判定用真值**：若 `RollSaveAttempt` 也用决策点，判断错误会被惩罚两次
   （概率下降 + 可达范围按 `successScale` 缩小），扑救率直接跌破验收区间。
2. **手臂 IK 用观测**：`Ball.FuturePosition` 在回放时读 CSV 真值轨迹，继续用它等于把刚堵住的
   真值从手臂放回来——门将会朝正确位置伸手，判断错误没有代价。
3. **零均值噪声 + 收缩只走半程**：噪声若有恒定偏置，方向准确率会被一个偏置污染；
   收缩若线性到 `confidence` 倍（=0.66 时只扑到格心 2/3 处），边角球必失、扑救率塌陷。

关闭开关（`ObservationSettings.Enabled = false`）时模型是**恒等变换**，播放器行为与改动前逐帧一致，
这条路径就是下面验收里的对照臂。

### 15.2 参数（端到端标定出来的唯一一组）

| 参数 | 值 | 含义 | 边界依据（实测） |
| --- | --- | --- | --- |
| `reactionDelay` | **0.18 s** | 起扑延迟基准，再乘门将自身的方向修正（低球快、高球慢） | 真实守门员 0.15–0.28 s；0 时退化为"同帧起扑"（`--reaction-delay 0` 可做单变量诊断） |
| `gridColumns × gridRows` | **5 × 3**（最终）/ 3 × 3（快速门禁） | 门将的分辨力 | 5 列时每列 1.46 m，量化才真正产生代价；3 列每列 2.44 m，方向准确率虚高 |
| `noise` | **0.80 m** | 观测噪声基准 σ | 0.72 实测方向 77.2%（越界）→ 0.80 实测 72.8%（通过）；再增大则扑救率单调下降（早期 3 列实验曾降到 7.4%） |
| `confidence` | **0.66** | 先验置信度 | 越低越向中路收缩、噪声越大；向上调等于让门将更信自己 |

C# 的 `ShotObservationSettings` 默认值与 CLI 的 `--noise/--reaction-delay/--confidence` 默认值
**必须一致**（都是上表的值）：两处不一致时，"用默认命令跑一遍"和"场景里直接跑"会得到不同手感。

> 唯一的例外是网格列数：场景内默认 5 列；`--shots 27` 的快速门禁显式传 3 列
> （评估清单里的 `observation.gridColumns` 覆盖场景默认）。这是两条不同用途的口径，
> 不是"不一致"——但改默认值时必须知道这一点，否则会误以为 27 发的结果能代表最终手感。

### 15.3 验收：怎么算通过

第一指标是**端到端扑救率**（可行性报告 §2.3）：球门 7.32 × 2.44 m、点球点 11 m 的几何下，
门将拿到真值就几乎必扑（对照臂 75.3%），落在 **18%–30%** 才算"像个人"。
第二指标是**方向判断正确率**（横向左/中/右，**55%–75%**）：高于 75% 说明量化没生效。

> **方向门禁只在 5 列网格上成立。** 3 列时每列宽 2.44 m，量化几乎不损失信息——
> 27 发快速门禁实测方向准确率 96.3%（必然越界，结果留档在 `output\evaluation\quick-27\`）。
> 因此 `--shots 27` 只设扑救率门禁，方向准确率只在 `--columns 5` 的最终验收里看。

162 发 5×3（`--seed 20261019`，门将 `GK_Elite`，球速 18–26 m/s，落点按真实点球分布加权）：

| 指标 | 观测开（最终） | 观测关（对照臂） |
| --- | --- | --- |
| 扑救 / 进球 / 未触 | 39 / 120 / 3 | 122 / 40 / 0 |
| **扑救率** | **24.1%** ✅ | 75.3%（对照，不作门禁） |
| 方向判断正确率 | **72.8%** ✅ | n/a |
| 高度判断正确率 | 87.0%（不计入方向指标） | n/a |
| 横向 + 高度全对 | 62.3% | n/a |
| 横向观测误差 | 平均 −0.006 m / 绝对 0.486 m | n/a |
| 解析失败行 / error | 0 / 0 | 0 / 0 |

> 复现性：同一命令跑两轮（`final-on2` 与 `verify-final`，后者在重建播放器之后），
> `save` 恒为 39、方向/高度/同格率与误差**逐位相同**；只有 1 球在 goal/miss 之间抖动
> （回放时序导致的"未到门线"）。因为 miss 也计入分母，扑救率不受影响。
> 这说明 0.80 / 5×3 / 固定种子的验收是稳定的，可以直接作为发布门禁。

**观测开与关的差值 51.2 个百分点，就是"作弊被移除"的量化证据。**

按真值分区看，扑救只出现在中间列（列 2 的扑救率 73%–86%），最外两列为 0%：
最外列格心离门将站位 2.93 m，已超过 `JumpDistance × probabilityScale ≈ 2.09 m` 的身体位移上限，
那些球本来就必进——**这是几何结论，不是模型缺陷**。也正因如此，射门集的多余样本按真实点球
分布（低球多、中路多、死角少）加权，而不是逐格均匀铺开：均匀铺开会让最外两列占掉 40% 样本，
扑救率就由射门集设计决定，而不是由观测模型决定。

复现（结果落在 `output\evaluation\<名字>\`：`report.json` + `results.jsonl` + 逐球 CSV）：

```powershell
# 最终验收（162 发 5×3，约 20 分钟）
python_env\python.exe -m aifootball evaluate --shots 162 --columns 5 --observation on --noise 0.80 `
  --expect-save-rate 0.18 0.30 --expect-direction 0.55 0.75
# 快速门禁（27 发 3×3，约 1 分钟）
python_env\python.exe -m aifootball evaluate --shots 27 --observation on --expect-save-rate 0.18 0.30
# 对照臂（关闭观测降级）
python_env\python.exe -m aifootball evaluate --shots 162 --columns 5 --observation off --out output\evaluation\control
# 复算已有结果（不跑 Unity，秒级）
python_env\python.exe -m aifootball evaluate --summarize-only output\evaluation\verify-final\results.jsonl
```

### 15.4 调参时踩过的坑

| 现象 | 原因 |
| --- | --- |
| 方向准确率 90%+，指标形同虚设 | 分格太粗（3 列 = 每列 2.44 m）；或落点抖动 `--jitter 0`，真值全在格心，量化误差恒为零 |
| 扑救率被射门集"设计"出来 | 多余样本逐格均匀铺开，最外两列占 40%，而那是必进球；应使用 `--shot-distribution penalty`（默认） |
| 观测误差退化成固定偏置 | 每发都复位随机序列：27 发会拿到同一串噪声。随机序列应在**整轮开始时复位一次**（`ResetObservationRandom`） |
| 改了 C# 却发现结果没变 | `runtime\` 里的播放器没重建（判据是 `Assembly-CSharp.dll` 时间戳，不是 `Myproject.exe`） |
| 评估结果与文档对不上 | 先看该次运行的 `shots.json` 里的 `observation` 段（`enabled/gridColumns/positionNoiseMeters/randomSeed`），再比 `results.jsonl` 的 `observationEnabled` |

### 15.5 已知局限（下一步该做什么）

1. 参数（0.18 / 0.66 / 0.80）是"让相对比较成立"的标定值，**不是**从真实比赛数据拟合的；
   拿到实测数据后应回填这三个数。
2. 目前只建模"门将读落点"，**没有**可行性报告修正 B 的"触球后早期球观测 → 贝叶斯融合"：
   门将不会在球飞了一半之后修正判断。
3. 没有二次反应/补射场景：延迟结束后一律扑向决策点，不重新评估。
4. `SidePreference` 只作为目标偏置参与计算，站位偏移尚未建模（见 §12 第 3 项）。

---

## 16. 姿态方向预测基线（可行性报告修正 C2）——已落地，判定「部分可行」

### 16.1 一句话结论

**姿态→方向这条路在原理上通，但在现有真实数据上缺的是「信息」不是「模型」。**

- 组 1（合成→合成）：Δt=0 时三分类准确率 **0.822**，且随提前量单调下降（0.822 → 0.692）。
  这证明「方向由摆腿平面/支撑脚/躯干侧倾决定」这个假设**是可学的**。
- 组 2（合成预训练→真实 3DSP，射正/射偏）：AUC **0.499**（随机初始化 0.497），
  与**标签置换检验**（0.477 / 0.458）不可区分 —— 现有 112 维姿态特征里没有可学的射正/射偏信号。
- 判定：`conditional`。下一步不是调模型，是换信息源（球轨迹、球门几何、脚部关键点）。

**红线**：3DSP **没有方向标签**，任何"方向准确率"都只能来自合成域；把 0.822 写成实战方向
准确率是本项目最容易犯的错。组 2 只报 on/off-target 的 AUC/准确率/Brier/ECE。

### 16.2 3DSP 数据集真相（本机实测，不是文档抄的）

| 事实 | 值 | 怎么确认的 |
| --- | --- | --- |
| shot 数 | train 200（射正 110 / 射偏 90）+ test 10 | `pose inspect` |
| test 分片可用性 | **不可用**（没有 `posture/`） | 目录里只有 `info.ini` |
| 每 shot 帧数 | 20 | `posture/*.txt` 行数 |
| 帧间隔 | 40 ms（**200/200 一致**） | `info.ini` 的 `position_step` |
| 触球时刻 | `annotated_position − start_position = 500 ms`（**200/200 恒定**） | 逐 shot 比对 info 字段 |
| 触球帧 | **12**（0-based），触球前可用 13 帧 | `floor(500/40)` |
| 关节数 / 口径 | 17，H3WB，**无脚/脚趾** | 关节名 |
| 坐标系 | 髋相对；逐帧按身高归一化（脚 z=0、头 z=1）；**z 是竖直向上** | 统计 + 可视化 |

这些常量在 200 个 shot 上完全一致，所以 `Δt` 截断是**精确**的（不用插值）；
代价是 Δt=400 ms 时只剩 3 帧可用。

### 16.3 特征：身体坐标系 + 112 维

```
ẑ = 世界 +z（竖直向上）
x̂ = (左髋 → 右髋) 投影到水平面后归一化
ŷ = ẑ × x̂
原点 = 髋中心；尺度 = |肩 − 髋|（躯干长度）
```

每帧 112 维 = 17 关节×3 坐标(51) + 17 关节×3 速度(51) + 9 个运动学量 + 1 个提前量。
运动学 9 维：左右膝角、左右髋屈、肩线侧倾、踝间距、髋偏航 sin/cos、躯干侧倾。

**两个"看起来更简单、实际是坑"的做法**：

1. **不要靠整体旋转骨架来注入方向**——身体坐标系会把整体旋转归一化掉，
   方向标签当场消失。必须让**相对结构**变化（摆腿平面 vs 支撑脚 vs 躯干）。
2. **不要只动一个关节**——模型会学到"某个关节偏移=某个类"这种捷径，
   合成域准确率虚高，换真实数据立刻崩。这也是设计文档里点名要避免的陷阱。

### 16.4 合成标签的物理假设（组 1 是"上界"的根本原因）

对每个源 shot、每个方向 d ∈ {−1, 0, +1}，在触球前 500 ms 内按时间插值施加：

| 通道 | 幅度 | 方向性 |
| --- | --- | --- |
| 绕竖轴偏航 | 10° ± 3° | d 的符号 |
| 支撑脚横移 | 0.10 ± 0.025 m | d 的符号 |
| 躯干侧倾 | 0.04 ± 0.015 m | d 的符号 |

三者**同号**，模拟"身体转向哪边、球就往哪边去"的常识。每个源 6 个变体（3 方向 × 2 次采样），
160 个源 → 2880 训练样本。

因为标签是我们按这个假设注入的，组 1 的 82% 只能读成"该假设可学"，
**不能**读成"真人也这样踢、模型能预测真人"。要验证后者，必须有人工标注的真实方向数据。

### 16.5 实验设计与实测结果

| 组 | 训练 | 验证 | 目的 |
| --- | --- | --- | --- |
| 1 | 合成（域随机化开） | 合成，**按源划分** | 可行性上界 |
| 1b | 合成（域随机化关） | 合成，按源划分 | 捷径学习对照 |
| 2 | 真实 3DSP，5 折 CV，合成预训练 vs 随机初始化 | 真实 out-of-fold | 迁移可行性 |

**组 1 / 1b（Δt=0）**：0.822 / **0.857**。

组 1b **高于**组 1，说明域随机化把输入弄噪了、任务变难了；但它不构成"组 1 靠随机化撑指标"
的证据 —— 两组都按源划分、互不共享动作，经典捷径拿不到分。两组都停在 80% 上下。

**组 2（Δt=0）**：预训练 AUC 0.499、随机初始化 0.497、增益 +0.002；
5 个 Δt 上 AUC 全部落在 0.444–0.500。

### 16.6 为什么判定是「没信号」而不是「没训好」

这是本次最重要的论证，两个独立证据：

1. **单特征扫描**：把每个样本的窗口按时间平均得到 112 维，逐维与标签求 AUC。
   最强的一维偏离 0.5 仅 0.115，**中位数只有 0.029**（200 样本下抽样波动本身就有 0.02–0.03）。
2. **标签置换检验**：把射正/射偏标签随机打乱，走**完全相同**的 5 折流程
   （同样的划分、增广、温度标定），得到 AUC 0.477 / 0.458，均值 0.468。
   真实标签的 0.499 与它相差 0.031，而 Hanley–McNeil 的 AUC 标准误是 **0.041**，
   即 |差| < 1 个标准误 —— 统计上无法区分。

所以 AUC≈0.5 不是过拟合、不是训练轮数不够、不是正则没调好（30 轮和 8 轮结果一样），
而是**这部分信息不在输入里**。3DSP 的坐标是髋相对 + 身高归一化的，球和球门都不在特征里，
而射正/射偏主要由触球瞬间的脚型、球心相对脚的位置、门将位置决定。

### 16.7 踩过的坑

| 现象 | 原因 / 处理 |
| --- | --- |
| `ImportError: LEFT_SHOULDER` 等 | 关节常量要先在 `pose/dataset.py` 里定义齐（17 个） |
| 肩线侧倾特征恒为 0 | 第一版拿躯干轴当"上"，侧倾被坐标系吸收了；改为世界竖直向上 + 显式侧倾特征 |
| 触球帧估计在 9–17 之间跳 | 用踝速峰值当代理噪声太大；`info.ini` 的时间轴是精确的（500 ms 恒定），只在缺失时才回退到峰值 |
| **合成变形看起来生效、实际在错的坐标系** | `_split_body` 与 `_merge_body` 的 einsum 下标写反且**互相抵消**，round-trip 误差 0.0 掩盖了错误。已修为 `einsum("tnk,tjk->tnj", ...)` / `einsum("tnj,tjk->tnk", ...)`，并加两个回归测试（真实 shot 上旧公式误差 0.70，新公式 1e-16） |
| 组 2 训练 30 轮必过拟合 | 验证 loss 在第 0–3 轮就到底，之后单调上升；真实样本只有 160 个，正则不是主因，**没有信号**才是 |
| `pose run` 前台跑一半就退出 | 200 源整轮约 7 分钟，超过前台工具时限；用 `start /b` 分离进程 + 日志轮询 |
| 判定阈值拍了个 0.02 余量 | 第一次跑出来真实 AUC 0.4993 比置换上界 0.4974 高 0.002，差点误判"有信号"；改用 Hanley–McNeil 标准误（0.041）做 2σ 判定 |
| 组 1b 比组 1 还高，以为写错了 | 正常。随机化是噪声源，关掉后任务更简单；关键看两者都在 80% 上下、且都按源划分 |

### 16.8 复现

```powershell
# 数据勘察（不加载 torch，秒级）
python_env\python.exe -m aifootball pose inspect
# 只训组 1 并存 checkpoint
python_env\python.exe -m aifootball pose train --out output\pose\model
# 完整三组 + 诊断（CPU 约 7 分钟）
python_env\python.exe -m aifootball pose run --out output\pose\final
# 快速冒烟（2 分钟）
python_env\python.exe -m aifootball pose run --max-sources 40 --folds 2 --transfer-epochs 3 --permutation-trials 1
```

产物：`output\pose\final\pose-report.md`（人读）+ `pose-results.json`（机读，含每折 best epoch、
逐 Δt 指标、单特征扫描、置换明细）。

### 16.9 下一步（按性价比排序）

1. **补脚部关键点**：H3WB 17 关节没有脚，而射门方向最直接的信号是脚背朝向。
   用 2D 关键点 + 三角化补出脚踝/脚尖，或者直接用 3DSP 的 2D 标注。
2. **引入球的位置**：3DSP 的 3D 坐标是髋相对的，丢掉了"球在脚的哪一侧"。
   球心相对支撑脚的位置是射正/射偏的强特征。
3. **换目标**：射正/射偏是**结果**标签，噪声大；如果能拿到"落点左右"这种连续量，
   回归任务比二分类更容易学到东西。
4. **补真实方向标注**：哪怕只有 100 条人工标注的"左/中/右"，也能把组 2 从"无监督迁移"
   变成"有监督微调"，这是验证合成域价值的最短路径。


## 17. 门将自定义训练（单机位视频 → 可进化模型）——已落地并端到端验证

### 17.1 一句话

门将不再只有内置的 5 个模板：用户可以自己提供**单机位视频**（手机/转播均可），在应用内逐条标注
射门落点与是否扑出，训练出**独立的模型文件**；之后继续追加标注、重新训练，模型**代际递增并可回看进化史**。

### 17.2 用户怎么用（界面路径）

`输出与交付` → 点门将卡片（`门将：未选择`）→ 浮层里点 **训练** → 弹出 `门将训练` 面板：

1. 左侧选门将，或输入名字点 `+` 新建（名字即文件名，会做安全化处理）。
2. `打开视频…` 选一段单机位点球视频（`.mp4/.mkv/.avi/.mov/.wmv`）。
3. `标定门框` 在画面上依次点**左上、右上、右下、左下**四个门柱角点；少于 4 点会提示。
   **每一脚都要单独标一次**（合集是多机位混剪，见 17.12）；点歪了就点 `清除门框`
   删掉这一脚的标定重来，`Esc` 只退出标定、不删已存盘的标定。
4. 逐脚标注：在画面上点**球最终过门线时的位置**，勾选 `扑出` 或 `射进`，点 `保存标注`。
   标定完成后 clicks 自动转为球门坐标（横向 m / 高度 m），落到哪个 3×5 分区由代码判定。
   保存后会自动跳到下一脚，没标门框的话直接进入标定模式。
5. 全部标完点 `开始训练`：右侧显示热力图、分代摘要、每格 `概率(saves/n)`、5 折交叉验证与告警。
6. 满意就点 `发布到门将库`，写入 `data/goalkeepers/<名字>.json`，Unity 立刻可用。

批量来源：`导入 JSON` 可导入 **StatsBomb 开源事件文件**（见 17.5），自动抽出点球落点并打上
`Saved/Goal` 标签，不用手点。工作区已放好一份 `data/external/statsbomb/events_3869685.json`
（2022 世界杯决赛，11 个点球）可当演示数据。

### 17.3 模型是怎么算出来的（不是拍脑袋的平均）

`AIfootball.Core/Services/GoalkeeperTrainer.cs`：

- **逐格 Beta 后验**：`p = (saves + α·prior) / (n + α)`，`PriorStrength = 2.0`。
- **邻居收缩**：3×5 网格上按高斯核（`σ = 1.1` 格，`NeighbourStrength = 1.0`）把相邻格的样本
  平滑进来，解决"某一格只有 1 脚却给出 100%"的过拟合。
- **超先验**：`BaselineSaveRate = 0.24`，`BaselineStrength = 8.0`（等价于 8 脚虚拟样本）。
  真实数据只有 10 脚时，没有这一层整张表会被 1 次扑救拉到 0.12 以下。
- **夹取** `0.02 – 0.98`：永远不给"必进/必扑"，避免 Unity 里出现不可能的结果。
- **5 折交叉验证**：logloss + Brier，并与"直接预测训练集均值"的基线对比（样本 < 10 时跳过）。
  实测世界杯决赛 10 脚：`logloss 0.409 / 基线 0.500`（优于基线），`Brier 0.194`。
- **置信度**：射正 ≥150 高、≥60 中、≥25 低，否则"不足"。

从 15 格概率反推 Unity 的全局参数：`EstimatedGoalKeeping` 与 `SideIndex`/`HeightIndex`
（`/0.05` 后夹取 ±1）。**身体属性（身高/臂展/速度…）继承原门将 JSON**——单机位视频量不出这些，
`SidePreference`/`HeightPreference` 归零、`GoalKeeping` 置 `0.75`（中性 1.0），避免与概率表重复计权。

### 17.4 "可进化"的确切含义

- 标注是**唯一真值来源**，模型永远可重算：`data/goalkeeper_training/<门将>/clips/<片段>.json`。
- 每次训练生成 `data/goalkeepers/models/<门将>.model.json`，`Version` 自增，
  `History[]` 追加一条（时间、样本数、logloss、置信度），旧模型不覆盖而是演进。
- 发布时只写 `data/goalkeepers/<门将>.json` 的 15 格 `ProbabilityMap` + 描述，
  **Unity 的 JSON 契约完全不变**（`GoalkeeperData.LoadFromJson` 无需改动，也没法在这台机器上重编译 Unity）。
- 追加新视频 → 重新标注 → 再训练，就得到 `v2`、`v3`……这就是"不是训练好就不更新"。

### 17.5 StatsBomb 导入（公开数据集，CC-BY-SA，需署名）

`AIfootball.Core/Services/StatsBombPenaltyImporter.cs`：读 `events/*.json`，筛
`shot.type.name == "Penalty"`，用 `shot.end_location`（`[x, y, z]`，单位码）换算成米：

- 横向：`y - 40`（球门中心线为 0，左负右正），高度：`z`。
- 横梁 2.67 码；超出的判为射偏，不进入样本。
- 结果标签：`Saved` / `Saved to Post` → 扑出，其余 → 进球。
- **门将从哪来**：Shot 事件不带门将字段，从 `Starting XI` 的 `tactics.lineup` 里取**对方**球队的 GK。

`Save` 清单一栏可留空表示"全部门将"。

### 17.6 验证记录（本机实测）

- `dotnet build src\AIfootball.sln -c Release` → 0 警告 0 错误。
- `dotnet test tests\AIfootball.Core.Tests` → **86 个用例通过**（含本次新增的
  homography / 训练语义 / 导入器 / 存储与进化）。
- 真实事件文件（设 `AIFOOTBALL_STATSBOMB_FIXTURE` 指向 17.2 那份 json）能解析出 11 个点球。
- 端到端：标 10 条 → `开始训练` → 面板显示 `第 1 代 · 10 脚射正 / 扑出 1（10%）· 置信度 不足`、
  `logloss 0.409（基线 0.500，优于基线）`、每格 `概率(saves/n)`、进化史
  `v1 · 2026-10-22 09:50:26 · 10 脚 · logloss 0.409`；
  `发布到门将库` 写出 `LowLeft 0.1262 / MidCenter 0.1345 / GoalKeeping 0.75`，日志 `门将模型已发布`。
- 训练/发布后已把演示改动还原（原 `LowLeft 0.68`），并清掉 `data/goalkeeper_training`、
  `data/goalkeepers/models`，工作区不留测试残留。

### 17.7 已知局限（必须向用户说清）

单机位是**透视投影**，不是三角化。实测标定误差约 10–20 cm，而分区只有 1.46 × 0.81 m
——误差已接近半个分区宽。后果：

- 偶尔会把球判到相邻格（随机部分）；
- 更麻烦的是**系统性**部分：四角点标定偏一点，整张图会整体平移/缩放，
  概率表被"抹平"趋向均匀，表现为"这个门将好像没什么偏好"。

缓解手段（按代价排序）：① 用 4 点 DLT 而不是"目测框"（已实现，`Homography.cs` 是精确解）；
② 标定尽量点门柱**内侧**接地点；③ 允许直接**点分区**而不是点画面（代码留了 `_trainerPendingPoint`
直接给球门坐标的口子）；④ 后期接双机位三角化（复用主流程的相机标定）。

另：`训练` 面板只做标注与训练，**不做视频裁剪**——需要片段请先用外部工具剪。

### 17.8 用公开数据真的训出了三个门将模型（2026-10-22）

上面 17.2–17.6 是"流程能跑通"的验证。这一节是"模型真的能用"的证据。

#### 数据从哪来

StatsBomb open-data（CC-BY-SA 4.0，需署名）。因为 GitHub raw / CDN 在这台机器上被封，
走 `https://gh-proxy.com/https://raw.githubusercontent.com/...` 抓，16 线程约 1.2 s/场。

| 赛事 | 场次 | 说明 |
| --- | --- | --- |
| 英超 2015/16 | 380 | **完整全季**，91 个点球 |
| 2018 世界杯 | 64 | 68 个点球 |
| 2022 世界杯 | 64 | 64 个点球 |
| 2020 欧洲杯 | 51 | 55 个点球 |
| 2024 欧洲杯 | 51 | 36 个点球 |
| 2024 美洲杯 | 32 | 49 个点球 |
| 2019 女足世界杯 | 52 | 33 个点球 |
| 2023 女足世界杯 | 64（其中 31 场网络超时未取） | 35 个点球 |
| 西甲 2019/20、2020/21 | 33 + 35 | 各 12 个点球 |

**合计 495 个点球事件 → 461 脚射正、96 次扑救，全局扑救率 20.82%。**
原始提取结果落在 `data/external/statsbomb/penalties_statsbomb.jsonl`（495 行，一条一行），
以后不必重下 events 就能复算。

> 注意：西甲 2008–2018 在 StatsBomb 里是**抽样**（每季只开放 30 余场），
> 所以"某门将整个生涯"这种样本拿不到。这是公开数据的硬边界，不是抓取问题。

#### 先验：区域建模到底有没有用

5 折交叉验证（461 脚，按格建 vs 单一常数）：

| 模型 | logloss |
| --- | --- |
| 单一常数（全局 0.2082） | 0.5177 |
| **15 格真实先验** | **0.4775** |

**提升 7～8%，跨 5 折稳定。** 结论：球飞向哪里确实决定能不能扑到，不是噪声。

15 格先验（`GoalkeeperTrainer.ZonePrior`，已内置进代码）：

| | Left | MidLeft | Center | MidRight | Right |
| --- | --- | --- | --- | --- | --- |
| **Low** | 17.5% | 30.1% | 17.3% | 37.2% | 15.5% |
| **Mid** | 26.7% | **45.3%** | 2.6% | 42.1% | 26.6% |
| **High** | 1.9% | 5.2% | 4.2% | 4.2% | 1.9% |

物理上完全讲得通：中路（门将已倒地）≈ 必进；中高两侧是门将吃饭的地方；
而贴着横梁的高球 **62 脚一次都没扑到**。

#### 训练出的模型

`data/goalkeepers/` 下新增三个真实门将，`data/goalkeepers/models/` 存完整模型文档（含进化史）：

| 文件 | 射正 | 扑出 | 观测率 | 5 折 logloss / 基线 | 期望扑救率 |
| --- | --- | --- | --- | --- | --- |
| `GK_EmiMartinez.json` | 15 | 5 | 33% | **0.616 / 0.694** ✓ | 23.6% |
| `GK_Pickford.json` | 17 | 4 | 24% | **0.590 / 0.649** ✓ | 21.8% |
| `GK_Lloris.json` | 18 | 1 | 5.6% | 0.332 / 0.277 ✗ | 18.8% |

对照现有模板（同样是按真实落点分布加权）：

| 模型 | 期望扑救率 |
| --- | --- |
| `GK_AllRounder` | 67.5% |
| `GK_Elite` | 85.2% |
| `GK_WeakLowRight` | 52.9% |

**现有模板是"理想门将"，真实点球扑救率只有 20% 左右。** 训练出的三个模型分别落在
18.8%–23.6%，正好落进项目既定的 18%–30% 手感验收区间——Unity 侧不需要任何改动，
`SaveProbability` 直接取 `ProbabilityMap` 采样值（见 `GoalKeeper.RollSaveAttempt`）。

#### 必须说清的局限（不要对号入座）

1. **个体模型置信度是"不足"**。15–18 脚远不够刻画一个门将，按现有分级要 ≥25 才"低"、
   ≥60 才"中"。三个模型的 `Confidence` 字段都如实写着 `Insufficient`。
2. **2/3 优于基线，Lloris 例外**。他这 18 脚只扑出 1 个（5.6%），
   模型认为他应该接近平均，于是 logloss 吃亏——这是"用先验对抗运气"的正常代价，
   不是 bug。18 脚 1 扑救根本无法证明他只有 5.6% 的能力。
3. **点球大战占比高**（495 里有 118 条 `period=5`）。点球大战的门将行为与常规点球
   不完全一样（更依赖赛前研究），目前没有分开建模。
4. **公开数据拿不到"某一门将的完整视频"**，只有逐球落点标签。想让模型真正个性化，
   还得回到 17.2 的路径：自己拍、自己标，样本到 50+ 时先验自然让位。

#### 复现

```powershell
# 1. 抓数据（8 个赛事，16 线程，约 40 分钟；走 gh-proxy 镜像）
python_env\python.exe scripts\harvest_statsbomb_penalties.py --work "$env:TEMP\sb"
#    产出一行一个点球的 penalties.jsonl + 可断点续跑的 done.txt

# 2. 统计 15 格先验与交叉验证（脚本逻辑已固化进 §17.3 / §17.8）

# 3. 训练并导出模型——走 AIfootball.Core 的同一套 GoalkeeperTrainer，不另写一份
```

`GoalkeeperTrainer` 的三处改动（本轮）：
- `ZonePrior[]`：15 格真实先验替换原来的常数 0.24；
- `PriorStrength` 2.0 → **6.0**（留一验证扫出来的最优档，压住单格过拟合）；
- 邻域高斯核改为**各向异性**（横向 1.0 格、纵向 0.35 格），
  否则中段的 0.4 会被平滑到高段，凭空造出一个不存在的"高空能力"。

### 17.9 合集视频也能标了 + 库尔图瓦的真实处境（2026-10-22 续）

#### 改了什么（标注端）

一段 5 分钟的"扑救合集"里有几十脚点球，原来的保存逻辑把标注名取成**视频文件名**，
标第二脚就会覆盖第一脚，等于一段视频只能标一脚。本轮改成：

| 行为 | 之前 | 现在 |
| --- | --- | --- |
| 标注命名 | `视频名` | `视频名 @ 42s`（按播放头秒数） |
| 一段视频能存几条 | 1 | 不限 |
| 标完之后的播放头 | 停在原地 | 自动前进 2 秒 |
| 快速定位 | 只有 `±0.1s` | 新增 `-5s` / `+5s` |
| 门框标定 | 只存"当前一套" | 每条标注内联四点快照 |
| 重选同一个视频 | 跳回 0 秒 | 保持播放头 |

单片段的老文件名规则保持兼容（仍去扩展名），已有标注不受影响。
新增 3 个用例覆盖这层行为（`GoalkeeperTrainingStoreClipTests`），C# 用例总数 86 → **89**。

#### 为什么每条标注要存门框快照

合集里几十脚来自不同比赛，**机位各不相同**。`calibration.json` 是"门将级"的当前标定，
一旦换机位重标，之前标好的落点就无法复算了。把四点快照内联进每条标注，才能回答
"这一脚当时是按哪套门框算的"。画面右侧也常驻一句提示：换比赛或机位要重新标定。

#### 库尔图瓦：数据侧的真实边界

- StatsBomb open-data 里他的点球**只有 5 脚**（2022 世界杯扑 Alphonso Davies，加英超 2015/16 四脚）。
  补抓西甲 2011/12 共 37 场，没有他。
- 拿这 5 脚试训了一次：全部落在 `Low` 行，`Mid`/`High` 两行完全是先验被平滑出来的噪声
  （`0.359 / 0.175 / 0.394` 这类数字没有任何依据），5 折交叉验证因每折只有 1 个样本而失效。
  **这个模型没有发布**——发一个噪声产物比没有模型更糟。
- 视频素材已经落地：`data/external/videos/Courtois_85_penalties.mp4`
  （YouTube 公开合集，85 脚，1280×720、5 分 23 秒；仅供本地研究）。裁剪转码可用 ffmpeg 完成，
  但**落点标注必须人眼看**——这正是"自定义门将"功能存在的意义，也是当前唯一的瓶颈。

#### 结论

`Thibaut Courtois` 的训练目录已经建好（`data/goalkeeper_training/Thibaut Courtois/clips/`），
在应用里直接可选。标到 25 脚以上再点「开始训练」，模型才会从"先验占位"变成个体信号；
在此之前门将库里不会出现他的文件——宁缺毋滥。

### 17.10 连续标注：脚次表 + 断点续标（2026-10-24）

#### 用户的原话

> "我这一段视频包含80个点球，总不能才标一个退出就结束了吧"

这不是抱怨功能缺失，而是抱怨**流程断了**。17.9 之后一段合集能存无限多条标注，但实际用起来：
保存 → 前进 2 秒 → 用户自己去找下一脚 → 找不到就退出 → 重开又从头开始。
80 脚 × 每脚手动找位置，等于这个功能不可用。

#### 根因

1. **「下一脚」只跳已标注的点**。第一次通标时一个标注都没有，这个按钮是死的。
   它解决的是"回头核对"，不是"往前标"。
2. **保存后前进 2 秒是瞎猜**。一脚点球平均 4 秒，+2s 大概率落在两脚之间。
3. **没有进度、没有续标**。重开视频回到 0 秒，用户不知道标到第几脚了。

#### 解法：先扫镜头，再导航

`tools/goal_detect/shot_list.py` 只做灰度帧差（160×90），不做门框检测，
8064 帧 **约 30 秒**（`video_scan.py` 要 8 分钟，因为逐帧跑门框检测）。
阈值取帧差分布的 **p99** 并夹在 20~32，再合并 0.4 秒内的连续切点、并掉短于 0.9 秒的闪光转场。

实测 `Courtois_85_penalties.mp4`：**64 段**，中位间隔 3.4 秒。
但 64 < 80——因为两脚之间没有硬切时会连成 10~27 秒的长段。
所以导航前再做一层**长段细分**：超过 8 秒的段按 4 秒切成"虚拟脚次"，
最终 **93 个候选脚次**（322 秒 / 80 脚 ≈ 4 秒，这个步长是对得上的）。

> 注意措辞：界面写的是**「候选 N 脚」**，不是"共 N 脚"。镜头切换不等于射门，
> 回放、庆祝都会多切一段，93 对 80 是正常的过分割，多出来的用「下一脚」跳过即可。
> 宁多勿少——漏掉的脚用户根本不会发现。

| 行为 | 17.9 | 现在 |
| --- | --- | --- |
| 「上一脚 / 下一脚」 | 只在已标注点之间跳 | 按脚次表走，**没标过也能跳** |
| 保存后 | 前进 2 秒 | 跳到**下一脚**的射门时刻（切点前 1.2s） |
| 标注文件名 | `视频名 @ 2s`（取整） | `视频名 @ 2.4s`（一位小数，避免 0.4 秒内互相覆盖） |
| 同一脚重标 | 新增一条重复样本 | **覆盖**（±1.2 秒内视为同一脚） |
| 重开同一段视频 | 回到 0 秒 | **续标**：跳到第一脚没标过的 |
| 标错了 | 只能去 clips 目录删文件 | 「撤销上一脚」：删掉最近一条并跳回那一帧 |
| 进度 | 只有总数 | `第 7/93 脚（候选）· 已标 7` + 时间轴上的淡刻度 |

#### 关键实现点

- **缓存位置** `data/goal_detect/shots/<视频名>.json`。脚次是**视频**的属性，与门将无关，
  所以不放在 `goalkeeper_training/<keeper>/` 下面。每段视频只扫一次。
- **扫描在后台跑**，不挡视频载入。扫描期间「下一脚」退回旧的"已标注点之间跳"逻辑。
- **`AnnotationsForCurrentVideo()` 必须按视频过滤**。合集里几十脚共用一段视频，
  原来的 `RedrawMarkTrack` 把整个门将目录的标注都画到当前时间轴上，秒数是别的视频的，纯误导。
- **标注列表带缓存 + 版本号**（`_annotationVersion`）。播放时每 60ms 刷新一次"第几脚"读数，
  每次都去读 clips 目录会把磁盘打满。保存/撤销/导入时 `InvalidateAnnotationCache()`。
- **`SeekTrainer` 仍然要重压一次 Position**。WPF 的 `MediaElement` 在暂停态 seek 后
  有时不刷新画面，这是 17.9 之前就踩过的坑，新流程沿用（但改成"停手后才补压一次"，见 17.11）。

#### 还没做的

- 脚次表**不可编辑**（不能手动合并/拆分/删除某一段）。过分割靠「下一脚」跳过。
- 没有"一键标完所有剩余脚次"的批量模式——每一脚仍然必须人眼看球过线的那一帧。
- `trusted_segments.json` 里的中位框仍是轴对齐包围盒（`quad` 缺失那批），
  单帧检测失败时会退到它，落点会有约 10~20 cm 的偏差。

### 17.11 连点「+1s / -1帧」导致视频卡死（2026-10-24）

#### 症状

> "如果我连续快速点击 +1s/-1帧这类按键，还是会出现视频卡死的情况"

卡死后的表现固定是三个一起出现：画面停在旧帧、`Position` 读回归零、帧号变 `帧 0 / 8050`。
也就是说不是"卡"，而是**整条 seek 管线被堵死**。

#### 根因：一次点击原来会写两次 Position

1. `StepTrainerVideo` 里先 `StopTrainerVideo()`，而后者无条件执行
   `TrainerVideo.Position = position;`（为了修"暂停后不刷新画面"）。
   **即使本来就是暂停态也会写**——点十下 = 十次额外 seek。
2. `SeekTrainer` 自己写一次 `Position`。
3. 后面还跟着一个 `Dispatcher.BeginInvoke` 再压一次，**又翻一倍**。

所以连点 10 下 ≈ 30 次异步 seek。WPF 的 seek 不是"设置一个变量"，
它要从最近关键帧解到目标帧再渲染；管线来不及消化就永久堵住。

#### 改法：合并（debounce）+ 单次补压 + 看门狗

`SeekTrainer` 不再直接写 `Position`，只记录目标：

| 场景 | 行为 |
| --- | --- |
| 单击 / 正常速度点（间隔 >110ms） | **立刻落地**，零延迟，跟手 |
| 连点（间隔 <110ms） | 只更新目标，手停 110ms 后才下发一次 |
| 按住方向键（自动重复 ~25/s） | 每 450ms 强制下发一次，保证画面还在动 |

实测（模拟同样的规则）：

| 操作 | 改前 seek 次数 | 改后 |
| --- | --- | --- |
| 连点 10 下（80ms 间隔） | 30 | **3** |
| 狂点 20 下（50ms 间隔） | 60 | **3** |
| 按住方向键 30 帧 | 90 | **4** |
| 正常速度点 10 下 | 30 | 10（本来就不该合并） |

配套三处：

- `StopTrainerVideo` 只在 `wasPlaying` 时才回压位置。暂停态不再产生额外 seek。
- "补压一次"改成**停手 260ms 后**，且只在 `Position` 真的和目标不一致时才写
  （以前是每次点击都补压）。
- **看门狗**：下发定位 1.5 秒后，如果 `Position` 被冲回别的值（典型是 0），
  判定管线卡死，自动重载并回到目标帧（5 秒内最多一次，防止"重载→又卡→再重载"死循环）。
  用户不再需要自己去点「重载」。

日志里会留一行可验证的证据：

```
[INFO] 定位合并：10 次点击 → 3 次 seek（840ms）
```

#### 顺带修掉的两个一致性问题

- 读数统一走 `PendingOrCurrentSeconds`（待落地目标 ?? 实际位置）。以前定位还没落地时
  滑块会被旧位置拽回去，看起来像"点了没反应"。
- 保存标注改用 `_ballMark.T`（点球那一刻的帧时间）而不是当前播放头。
  点了球又拖走时间轴的话，位置和秒数会对不上，这一脚会落到错误的脚次上。

### 17.12 门框检测全线下线，改成**逐脚**手标（2026-10-24）

#### 用户的原话

> "算了你门框也别自动检测了，问题太大了，改成手动的吧"
> "谁让你自己检测了，每一脚都要重新标"

#### 现在的唯一路径

**没有任何自动检测**。`detect_at.py` / `video_scan.py` / `trusted_segments.json`
全部不再被应用调用（脚本留在 `tools/goal_detect/` 里做离线分析用，应用侧一个都不碰）。

删掉的代码：`EnsureFrameGoalRectAsync`、`DetectGoalRectAsync`、`ParseDetectedRect`、
`LoadGoalSegmentsIfNeeded`，以及 `_goalSegments` / `_lastDetect` / `_frameGoalPartial` /
`_detectingFrame` / `_trainerHomography` / `_trainerCalibration` 六个字段。
`MarkBallAsync` 变回同步的 `MarkBall`。

#### 为什么是"每一脚"而不是"每一段镜头"

第一版做成了"按时间分段，标一次管到换机位为止"。这是错的：这段合集是**多机位混剪**，
相邻两脚就可能换机位，靠"猜什么时候换"必然有漏。用户直接否掉了这个设计。

现在的规则很硬：**标定键 = 这一脚的起点时间**（脚次表里的 `Start`）。

```json
// data/goalkeeper_training/<keeper>/calibrations/<视频名>.json
{
  "video": "Courtois_85_penalties",
  "shots": [
    { "shotStart": 0.0,  "calibration": {四点} },
    { "shotStart": 5.48, "calibration": {四点} }
  ]
}
```

`VideoCalibrationDocument.ForShot(shotStart)` 是**精确查找**（容差 0.08 秒），
没标过的脚次返回 `null`——**绝不沿用上一脚**。这条是整套设计的核心：
沿用等于给一个看起来正常、其实错几十厘米的数字，比没有更糟。
测试里专门有一条 `没标过的脚次就是没有门框而不是沿用上一脚` 守着它。

#### 操作循环（每脚 5 次点击 + 1 次回车）

```
保存上一脚
  → 自动跳到下一脚（按脚次序号走，不按 Key）
  → 这一脚没标门框就自动进入标定模式
  → 点左上、右上、右下、左下（4 次）
  → 拖到球过线那一帧，点球（1 次）
  → 回车
```

- 标完门框，右侧显示 `第 7/93 脚 门框已标定 · 全片已标 7/93 脚`。
- 标定模式下按 **Esc** 退出标定（不关面板），再按一次才关面板。
- 同一脚里挪几帧重标 = 覆盖这一脚，不会多出一条。
- 脚次表还在扫的时候不允许标定：那时"这一脚从哪开始"还没确定，
  存进去的键会变成孤儿。`ShotListPending` 挡这一条。

#### 顺带修掉的一个导航 bug

`AdvanceToNextShot` 原来按 `Key` 找下一脚。但用户是在切点之前若干帧点球的，
**当前这一脚的 Key 可能还在播放头前面**——保存后"下一脚"会跳回同一脚。
改成按脚次序号走（`NeighbourShot`），`上一脚/下一脚` 同理。

#### 代价（必须承认）

- 80 脚 = 320 次门角点击 + 80 次点球 + 80 次回车。比自动检测累得多，
  但每个数字都能追溯到用户自己点的四个点。
- 手点歪了落点就歪，没有任何兜底。好处是错的时候**看得见**（橙框跟门柱对不上）。
- 脚次表如果重新扫（缓存删了），脚次起点会漂移几帧，已有标定会对不上键。
  `SameShotTolerance` 只吸收 0.08 秒以内的抖动。

#### 测试

`VideoCalibrationPerShotTests`（7 个用例）：逐脚互不串用、没标过返回 null、
同脚重标覆盖、相邻两脚不互相覆盖、存盘往返、单片段用 0 作键、空视频返回空。
C# 用例总数 114 → **121**，全部通过。

### 17.13 门框标记"清不掉"：补上删除入口（2026-10-24）

#### 用户的原话

> "门框的标记清不掉是什么情况"

#### 三个叠加的原因

1. **标定成功后没清掉那四个橙点。** `HandleCalibrationClick` 成功分支只调了
   `RedrawGoalOverlay()`，`ClearCalibrationMarkers()` 只在共线失败分支和 Esc 里调。
   四个橙点变成橙框以后还留在画面上，重标几次就叠几层。
2. **没有任何删除路径。** `VideoCalibrationDocument` 只有 `Set`（同脚覆盖），
   没有 `Remove`。标错的脚次只能"再标一次盖掉"，用户想撤掉门框根本无处可点。
3. **`RedrawGoalOverlay()` 是无条件重画。** 只要 `_frameGoalRect` 非 null 就画橙框，
   而 `_frameGoalRect` 又是从已存盘标定推出来的（`ApplyManualGoalFrame`）。
   所以就算手动把橙框从画布上删掉，下一次 `SeekTrainer` 又会把它画回来——
   这就是"清不掉"的直接观感。

#### 改了什么

- `VideoCalibrationDocument.Remove(shotStart)`：按 0.08 秒容差删掉这一脚，返回是否删到。
- `GoalkeeperTrainingStore.DeleteVideoCalibration(keeper, videoPath, shotStart)`：
  删完如果一脚都不剩，把整个 json 也删掉——留空壳文件会让
  "全片已标 N 脚"和"这一脚还没标"两处判断都跑偏。
- `HandleCalibrationClick` 成功分支补 `ClearCalibrationMarkers()`：
  四点已经变成橙框，留着就是重复噪音。
- 新增 `清除门框` 按钮（`ClearShotCalibration_Click`）：退出标定状态 →
  删文件里的这一脚 → 清内存缓存 → `ApplyManualGoalFrame` 置 null →
  `RedrawGoalOverlay` 撤掉橙框红线 → 连带撤掉这一脚的待保存球点。
  删不到时明确回一句"本来就没有保存过门框标定，画面已刷新"，
  和"真删掉了"区分开，避免用户以为点了没反应。

#### 仍然存在的边界

- 已经**保存过标注**的那一脚，标注里内嵌了自己的 `Calibration` / `GoalRect` 副本。
  清除门框标定不会去改历史标注——那是另一条数据，动了就等于篡改已确认的样本。
  要重做就重新标门框、重新点球、再保存一次（会覆盖同脚标注）。
- `Esc` 只退出标定模式并清掉还没落定的四个点，**不删已存盘的标定**。
  删除是显式动作，必须点按钮。

#### 测试

`VideoCalibrationPerShotTests` 从 7 条加到 11 条，新增：
删一脚不影响别的脚、重复删返回 false、删光不留空壳文件、容差内算同一脚。
C# 用例总数 121 → **125**，全部通过。

### 17.14 标定改成"只针对一帧"（2026-10-24）

#### 用户的原话

> "让你删你也删不掉，我说了每次标定应该只针对一帧，你这倒好，每一帧上面都有，撤销也撤销不了"

#### 三处错

1. **橙框按"脚"画，不按"帧"画。**
   `RedrawGoalOverlay()` 只要 `_frameGoalRect` 非 null 就画，而 `_frameGoalRect` 是按**脚**从标定
   推出来的。合集里一脚 3~5 秒、上百帧，结果就是同一脚每一帧都套一个橙框。
   用户看到的是"满屏都是框"，自然会以为标定串到别的脚上去了。
2. **标定里没记帧号。** `GoalFrameCalibration` 只有四个点的坐标，没有"这是在第几帧点的"，
   所以代码根本无从判断"这一帧是不是标定的那一帧"。
3. **撤销只认标注。** `UndoLastAnnotation` 只删"本视频最后保存的一条标注"。只标了门框、
   还没保存球点的时候，它永远回"本视频还没有可撤销的标注"——门框撤不掉。

#### 改了什么

- `GoalFrameCalibration` 新增 `Frame`（帧号）和 `FrameSeconds`（播放头秒数），默认 -1；
  `HasFrame` 判断是不是新数据。`HandleCalibrationClick` 在点完第四个角时把当前帧写进去。
- 新增 `ShouldShowGoalOverlay()`：**只有播放头停在标定的那一帧（±半帧）才画橙框**。
  正在标这一脚时例外——把已有的框显示出来当参照，好判断这次点得准不准。
- 老数据（`Frame < 0`）没有帧号，不敢猜它属于哪一帧，**除了标定中一律不画**。
  右下角状态改成 `门框已标定（第 N 帧）` / `门框已标定（老数据，无帧号）`，明确告诉用户
  "标定还在，只是不在这一帧"。
- 新增 `DropCalibrationClicksIfFrameChanged()`：标定过程中换了帧，已经点的门角作废、橙点撤掉。
  否则四个点分属两帧，橙框看着像对的，换算出来的落点却是错的。
- `撤销上一脚` → **`撤销这一脚`**：先删这一脚已保存的标注；没有标注就删这一脚的门框标定。
  两条路都不通才说"没有可撤销的"。新增 `AnnotationForCurrentShot()` 负责按脚次区间找标注。
- `MarkBall` 改用 `PendingOrCurrentSeconds` 而不是 `TrainerVideo.Position`：
  seek 是异步的，刚点完 `+1帧` 就点球的话 `Position` 还停在前一帧，标注会落到上一脚。
- 脚次读数拆成 `标注 N · 门框 M 脚`：只标门框没点球时，"已标 0"会让人以为那四针白点了。

#### 一个刻意保留的行为

**落点换算仍然按"脚"取门框**（`CalibrationAt` 按这一脚的起点查），不是按帧。
理由：标定是对一帧做的，但同一脚里机位不动，四个点对这一脚的任何一帧都成立；
如果按帧取，用户就必须在"点球的那一帧"上重新标一次，白白多四针。
变的只是**显示**：橙框只在标定帧出现，别的帧不画。

#### 测试

`VideoCalibrationPerShotTests` 11 → 13 条，新增：帧号能落盘读回、老数据 `HasFrame` 为假。
C# 用例总数 125 → **127**，全部通过。
