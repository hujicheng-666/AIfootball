# AIfootball · 点球三维轨迹训练台

Windows 桌面应用：左右双目视频 → YOLO11 检测 → 三维三角化 → 弹道拟合 → **内嵌 Unity 回放**门将扑救，
同时给出射门热区、门将覆盖与"射手 × 门将"对位胜率。

> **接手开发请先读 [docs/HANDOVER.md](docs/HANDOVER.md)**（架构、契约、排障、已知缺口、健康检查命令）。
> 设计与可行性分析在 [docs/](docs/)。

---

## ⚠️ 2026-10-20 已回退 UI 与 Unity

- **UI**：恢复为重构前的单体 WPF 版（`src\AIfootball.App` + 只含 App 的 `src\AIfootball.sln`）。设计系统、`Views\Pages\*`、`--diagnose` 离屏自检都已移除。
- **Unity**：`Myproject\` 与 `runtime\` 播放器按备份快照完整恢复，门将恢复"全知"；C1 观测降级与 `ShotEvaluationRunner` 评估链路一并移除。
- **保留**：`src\AIfootball.Engine`（含 C2 `pose`）、`src\AIfootball.Core`、`tests\`、`output\pose\final\`。UI 不再引用 `AIfootball.Core`。
- 细节与备份位置见 [docs/HANDOVER.md](docs/HANDOVER.md) 第 0 节。

---

## 快速开始

支持 Windows 10 x64（内部版本 19041 及以上）和 Windows 11 x64。运行需要
[WebView2 Evergreen Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download)
和 [Visual C++ x64 运行库](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)。

### 1. 克隆并下载资源

使用带 Git LFS 的 [Git for Windows](https://gitforwindows.org/)。建议使用如 `D:\AIfootball` 的短路径。

```powershell
git lfs install
git clone -c core.longpaths=true --branch Striker_Model https://github.com/hujicheng-666/AIfootball.git D:\AIfootball
Set-Location D:\AIfootball
```

Unity 播放器和部分资源通过 LFS 分发。若因网络中断留下了指针文件，在仓库根目录运行
`git lfs pull`。需要切换一个已经正常检出的仓库时，先 `git fetch origin`，再运行
`git switch --track origin/Striker_Model`（已有本地同名分支时用 `git switch Striker_Model`）。

旧提交曾跟踪 `python_env\`。如果检出旧提交时出现路径过长，短路径和
`core.longpaths=true` 可以让 Git 完成检出。若 `git status` 将几乎所有文件列为已暂存删除，
说明检出曾中断。保留原目录中的本地文件，然后在另一短路径重新克隆，再迁移需要的本地改动。

### 2. 编译与开发运行

安装 x64 .NET 8 SDK 和 .NET 8 Desktop Runtime。先还原 NuGet 依赖并运行应用。

```powershell
dotnet build src\AIfootball.sln -c Release
dotnet run --project src\AIfootball.App\AIfootball.App.csproj -c Release --no-build
```

WebView2 使用已入库的 `src\AIfootball.Web\dist`。修改 React 源码后，在
`src\AIfootball.Web` 下执行 `npm ci` 和 `npm run build`，并提交更新后的 dist。

首次启动时，可在「工作台」「分析」或「设置 → 环境诊断」点击「一键安装环境」。状态显示为
「Python 环境就绪」后即可分析样例。应用联网创建
`%LOCALAPPDATA%\AIfootball\Python310`，验证 PyTorch、YOLO、OpenCV 和 SciPy。
检测到可用的 NVIDIA 驱动时会尝试 CUDA，初始化失败时自动使用 CPU。
如果要单独测试 CPU 安装，设置 `AIFOOTBALL_CPU_ONLY=1` 并用 `AIFOOTBALL_PYTHON_ENV`
指定一个新的可写目录，再从同一个终端启动应用。

### 3. 给另一台电脑使用

```powershell
.\scripts\build_release.bat
```

复制整个 `dist\AIfootball-Portable-v3.0` 目录到目标电脑的可写位置，再运行其中的
`AIfootball.exe`。便携包已包含 .NET、Unity 播放器、YOLO 模型和三组带配套标定的双目样例。
首次安装 Python 依赖需要联网。自己的相机和场地需要重新标定。

需要安装向导时，在构建电脑装好 [Inno Setup](https://jrsoftware.org/inno-setup/downloads) 6.3 或以上，
运行 `.\installer\build_installer.bat`，产物在 `dist\installer`。
安装向导默认安装到当前用户的可写目录，并检测 WebView2 和 Visual C++ 运行库。
安装缺失的 Visual C++ 运行库时需要管理员批准。

### 目录角色

| 目录 | 是否入库 | 说明 |
| --- | --- | --- |
| `%LOCALAPPDATA%\AIfootball\Python310` | 否 | 当前用户首次运行时安装的 Python 推理环境 |
| `runtime\` | 是（LFS） | Unity 播放器（`Myproject.exe` + `Myproject_Data`），`dotnet build` 不生成 |
| `calib\` | 是 | 双目标定四件套，检测链路必需；换场地需重新标定 |
| `data\goalkeepers\` | 是 | 门将属性 JSON，UI 列表与 `evaluate` 依赖 |
| `models\` | 是 | `yolo11m.pt` / `yolo11n.pt` |
| `samples\` | 是 | sample1/2/3 双目演示视频 |
| `Myproject\` | 是 | Unity 工程（`Assets` / `ProjectSettings` / `Packages`），改模型用 |
| `data\external\`、`data\goal_detect\` | 否 | 外部数据集与 YOLO 训练集，体积大，按需自备 |
| `data\PenaltyKickHistory\`、`output\`（除 `blender`） | 否 | 运行期产物 |


## 自检与测试

```powershell
$python = Join-Path $env:LOCALAPPDATA 'AIfootball\Python310\python.exe'
# Python 引擎
& $python -c "import sys, unittest; sys.path.insert(0, r'src\AIfootball.Engine'); unittest.main(module=None, argv=['unittest','discover','-s','tests/engine','-t','.'])"

# C# 领域层测试
dotnet test tests\AIfootball.Core.Tests\AIfootball.Core.Tests.csproj
```

> ⚠️ `evaluate` 验收依赖 **Unity 批量回放协议**（`--wpf-command` / `ShotEvaluationRunner`），该协议已随 Unity 回退移除；命令仍能生成数据并做 Python 侧统计，但不能驱动门将实测。恢复新版 Unity 后可复用下面的命令。

```powershell
# 快速门禁：27 脚射门，扑救率必须落在 18%–30%
& $python src\AIfootball.Engine\aifootball\__main__.py evaluate --shots 27 --observation on --expect-save-rate 0.18 0.30

# 最终验收：162 脚 5×3 网格，同时校验方向准确率 55%–75%（约 20 分钟）
& $python src\AIfootball.Engine\aifootball\__main__.py evaluate --shots 162 --columns 5 --observation on --noise 0.80 --expect-save-rate 0.18 0.30 --expect-direction 0.55 0.75
```

`evaluate` 是**唯一客观的"手感"口径**：`--observation off` 还原改动前的全知门将（对照臂），
`--observation on` 是观测降级后的真实门将。162 发实测：观测开 24.1% vs 观测关 75.3%——
这 51.2 个百分点的差距就是"作弊"被移除的量化证据。
报告落在 `output\evaluation\<名字>\`（`report.json` + `results.jsonl` + 逐球诊断字段），
`--expect-save-rate` / `--expect-direction` 越界时返回非 0，可直接当门禁用；
`--summarize-only <results.jsonl>` 可秒级复算已有结果。
注意**方向准确率门禁只在 5 列网格上成立**：3 列时每列 2.44 m，量化几乎不损失信息（快速门禁会读到 96%）。
完整口径、参数与调参踩坑见 [docs/HANDOVER.md](docs/HANDOVER.md) §15。

## 射手姿态 → 方向预测基线（C2）

用 3DSP 的 3D 骨骼序列做"现有视角能不能提前看出射门方向"的可行性验证（纯 CPU，不需要相机/Unity）：

```powershell
& $python src\AIfootball.Engine\aifootball\__main__.py pose inspect          # 数据勘察（秒级，不加载 torch）
& $python src\AIfootball.Engine\aifootball\__main__.py pose run --out output\pose\final   # 三组实验 + 诊断（约 7 分钟）
```

实测结论（详见 [docs/HANDOVER.md](docs/HANDOVER.md) §16，报告在 `output\pose\final\pose-report.md`）：

- **组 1（合成→合成）**：Δt=0 时三分类准确率 **82.2%**，随提前量单调下降 → 物理假设"可学"；
- **组 2（合成预训练→真实 3DSP）**：射正/射偏 AUC **0.499**，与**标签置换检验**（0.468 ± 0.041）
  不可区分 → 现有 112 维姿态特征里没有这部分信号；
- 判定 `conditional`：下一步要补的是**信息源**（脚部关键点、球相对脚的位置、连续落点标签），
  不是模型容量。

> 3DSP **没有方向标签**，任何方向准确率都只能来自合成域。组 1 的 82% 是可行性上界，
> **不能**对外当作实战方向准确率。

## 自定义门将训练（单机位视频 → 可进化模型）

门将不再只有内置模板：喂一段**单机位**点球视频（手机/转播都行），在应用内逐脚标注落点与扑救，
就能训练出**属于自己**的门将，并且可以一直追加标注、持续进化。

界面路径：`输出与交付` → 点门将卡片 → 浮层点 **训练**。

1. 选/新建门将 → `打开视频…`；
2. `标定门框`，依次点左上、右上、右下、左下四个门柱角点（按门将+机位保存，可复用）；
3. 逐脚点球的过线位置 + 勾 `扑出`/`射进` → `保存标注`；
4. `开始训练` → 得到 15 格概率表 + 5 折交叉验证 + 告警；
5. `发布到门将库` → 写入 `data/goalkeepers/<名字>.json`，Unity 直接可用。

一整段**合集视频**（几十脚点球剪在一起）也能标：每保存一脚，标注名会自动带上秒数
（形如 `Courtois_85_penalties @ 42s`），不会互相覆盖；保存后播放头前进 2 秒，
配 `-5s` / `+5s` 快速定位。合集里换了比赛或机位就要**重新标定门框**——
每条标注都会存下当时的四点快照，事后能追出哪一脚用的是哪套标定。

已经备好一份待标素材：`data/external/videos/Courtois_85_penalties.mp4`
（YouTube 公开合集，85 脚对库尔图瓦的点球，1280×720、5 分 23 秒；仅供本地研究使用）。
用它新建门将 `Thibaut Courtois`、按上面的流程标到 25 脚以上，`Confidence` 才会脱离"先验占位"。

懒得手标就点 `导入 JSON` 导入 **StatsBomb 开源事件**（CC-BY-SA）自动抽标签；
工作区已备一份演示数据 `data/external/statsbomb/events_3869685.json`（2022 世界杯决赛，11 个点球）。

模型不是简单平均：逐格 Beta 后验 + 各向异性邻居收缩 + 15 格**真实先验**，夹在 `0.02–0.98`；
每训练一次 `Version+1`、`History` 追加一条，标注只增不丢，模型永远可重算。
**Unity 的 15 格 JSON 契约没有变化**。

### 已经训出来的模型

`data/goalkeepers/` 里已经有三个用公开数据（StatsBomb open-data，CC-BY-SA）训练的真实门将：

| 文件 | 射正 | 扑出 | 5 折 logloss / 基线 | 期望扑救率 |
| --- | --- | --- | --- | --- |
| `GK_EmiMartinez.json` | 15 | 5 | **0.616 / 0.694** ✓ | 23.6% |
| `GK_Pickford.json` | 17 | 4 | **0.590 / 0.649** ✓ | 21.8% |
| `GK_Lloris.json` | 18 | 1 | 0.332 / 0.277 ✗ | 18.8% |

样本来自 **495 个真实点球（461 脚射正、96 次扑救）**，覆盖英超 2015/16 全季、
2018 & 2022 世界杯、2020 & 2024 欧洲杯、2024 美洲杯、2019 & 2023 女足世界杯。
原始提取结果在 `data/external/statsbomb/penalties_statsbomb.jsonl`。

作为对照，仓库里原有的模板是"理想门将"：`GK_AllRounder` 期望扑救率 **67.5%**、
`GK_Elite` **85.2%**，而真实点球扑救率只有 **20.8%**。训练出的三个模型落在 18.8%–23.6%，
正好是项目 18%–30% 手感验收区间——Unity 侧一行都不用改。

> 三个模型的 `Confidence` 都如实写着 `Insufficient`（15–18 脚远不够刻画一个门将）。
> 这是数据的物理边界，不是实现问题：想要真正个性化的模型，得按上面「自定义门将训练」
> 自己拍、自己标，样本到 50+ 时先验才会让位给个体信号。

数学细节、交叉验证（按格建模比单一常数好 7%）、15 格先验表与单机位投影误差的已知局限
见 [docs/HANDOVER.md](docs/HANDOVER.md) §17。

## 工作流

1. **相机标定** — 用棋盘格视频做内参，用场地照片做外参；产物落在 `calib/`。
2. **准备样本** — 每个样本固定结构，不再靠文件名猜相机：

   ```text
   samples/<name>/
     ├─ left/recording.mp4
     └─ right/recording.mp4
   ```

3. **采集** — "仅预览"只看画面（不落盘、不加载模型）；"开始分析"才录屏并逐帧检测；
   停止后自动重建、拟合、导出。
4. **回放** — 选轨迹与门将 →"启动"把 Unity 播放器**嵌进页面**画布（兜底可用独立窗口）；
   右下角面板显示该轨迹的落点热区与对位结论。

## 产物

```text
output/trajectory_3d/<sample>/         三维重建结果
output/trajectory_ballistic/<sample>/  弹道拟合结果
output/live_preview/                   采集实时预览帧（临时）
output/ui_diagnose/                    界面自检报告与截图（临时）
data/<sample>_trajectory.csv           Unity 回放轨迹（time,x,y,z）
data/<sample>_trajectory.meta.json     该轨迹绑定的门将
data/goalkeepers/*.json                门将模型（Unity 直接读取）
data/goalkeepers/models/<名字>.model.json         训练产出的完整模型文档（含进化史）
data/goalkeeper_training/<名字>/clips/*.json       逐片段标注（唯一真值来源）
data/goalkeeper_training/<名字>/calibration.json   该门将的机位标定（4 点单应）
data/external/statsbomb/events_*.json              StatsBomb 开源事件（CC-BY-SA，导入用）
data/external/statsbomb/penalties_statsbomb.jsonl  495 个真实点球的提取结果（一行一个，可复算）
scripts/harvest_statsbomb_penalties.py             抓取上述数据的脚本（gh-proxy 镜像）
```

`output/`、`logs/`、`bin/`、`obj/`、`__pycache__/` 均为运行期产物，不入版本库。

## 目录

```text
src/AIfootball.Core/     领域层：路径真相、引擎宿主、扫描/解析/编排、门将与扑救模型
src/AIfootball.App/      WPF：Design 设计系统 / Views 页面 / ViewModels / Diagnostics 自检
src/AIfootball.Engine/   Python 引擎（aifootball 包：cli / capture / calibration / pipeline / pose）
tests/                   Core 单元测试（xUnit）+ 引擎单元测试（unittest）
runtime/                 Unity 播放器（Myproject.exe 等运行库）
Myproject/               Unity 工程源码
samples/ calib/ models/ python_env/   样本、标定产物、检测模型、内嵌解释器
data/                    门将配置（goalkeepers/）、门将训练标注（goalkeeper_training/）、回放轨迹 CSV、external/（3DSP 姿态数据集、StatsBomb 事件）
report_assets/           汇报插图素材（弹道 / 流程 / 热区 / 雷达）
docs/ scripts/ installer/ mobile-camera-bridge/ assets/
```

### 不要删除的资产

| 路径 | 为什么不能删 |
| --- | --- |
| `data/external/3dsp/` | 射手骨骼姿态的唯一来源：`scripts\export_shooter_pose.py` 与 `aifootball pose`（C2 基线）都依赖它（见 `docs/shooter-setup-guide.md`、`docs/HANDOVER.md` §16） |
| `src/AIfootball.Engine/aifootball/calibration/assets/*.jpg` | 外参标定的预设参考照片，引擎按**包内相对路径**读取，缺失则外参标定没有预设任务 |
| `report_assets/` | 汇报/论文插图原始素材，不是缓存 |
| `assets/brand/source/` | 早期品牌（SpotIQ）设计源图，仅作留档 |

## 手机当相机

若 IP Webcam 限制了分辨率/帧率，用 [mobile-camera-bridge](mobile-camera-bridge/README.md)：
安卓端读取相机硬件支持的规格并提供 MJPEG 地址（如 `http://192.168.1.101:8080/video`），
直接填进采集页的左/右路输入框即可。
