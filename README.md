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

要求：Windows 10/11 x64、.NET 8 SDK。**不需要**另装 Python、PyTorch、YOLO 或 Unity——
运行环境与 Unity 播放器都已随仓库分发。

### 1. 克隆（必须带 LFS）

```powershell
git lfs install          # 只需一次
git clone https://github.com/hujicheng-666/AIfootball.git
cd AIfootball
```

仓库含 `python_env\`（内嵌 Python + torch + ultralytics，约 1.6GB）与 `runtime\`
（Unity 播放器，约 486MB），都走 Git LFS。**没有 `git lfs` 会拿到一堆指针文件，
启动时会报找不到 `python_env\python.exe`。**

### 2. 编译

```powershell
dotnet build src\AIfootball.sln -c Release   # 只构建 WPF App（单体版）
```

> 前端产物 `src\AIfootball.Web\dist` 已入库（主界面由 WebView2 加载它）。
> **改了 `src\AIfootball.Web` 的 React 源码后必须重建并提交**，否则跑的还是旧的 dist：
>
> ```powershell
> cd src\AIfootball.Web; npm install; npm run build   # 需要 Node 18+
> ```
>
> 只改 C# 则无需 Node，dist 现成可用。

### 3. 运行

```text
src\AIfootball.App\bin\Release\net8.0-windows10.0.19041.0\win-x64\AIfootball.exe
```

> 路径注意**没有** `x64` 这一段。若用 `-p:Platform=x64` 构建，产物会落在
> `bin\x64\Release\...`。两个目录会同时存在，混用很容易启动到旧的一份——
> 改完代码后按构建时间确认一下 exe 的时间戳。

首次启动会自动定位工作区根目录（从 exe 向上找 `python_env\`），无需配置。
`runtime\` 里的 Unity 播放器由 WPF 拉起，用于三维回放；它不能由 `dotnet build`
生成，所以必须来自仓库（见 `runtime\`）。

### 目录角色

| 目录 | 是否入库 | 说明 |
| --- | --- | --- |
| `python_env\` | 是（LFS） | 内嵌 Python 3.10 + torch/cv2/ultralytics，开箱即用 |
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
# Python 引擎（137 个用例）
python_env\python.exe -m unittest discover -s tests/engine -t .

# C# 领域层（89 个用例；已不在上面的 sln 里，单独构建）
dotnet test tests\AIfootball.Core.Tests\AIfootball.Core.Tests.csproj
```

> ⚠️ `evaluate` 验收依赖 **Unity 批量回放协议**（`--wpf-command` / `ShotEvaluationRunner`），该协议已随 Unity 回退移除；命令仍能生成数据并做 Python 侧统计，但不能驱动门将实测。恢复新版 Unity 后可复用下面的命令。

```powershell
# 快速门禁：27 脚射门，扑救率必须落在 18%–30%
python_env\python.exe -m aifootball evaluate --shots 27 --observation on --expect-save-rate 0.18 0.30

# 最终验收：162 脚 5×3 网格，同时校验方向准确率 55%–75%（约 20 分钟）
python_env\python.exe -m aifootball evaluate --shots 162 --columns 5 --observation on --noise 0.80 --expect-save-rate 0.18 0.30 --expect-direction 0.55 0.75
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
python_env\python.exe -m aifootball pose inspect          # 数据勘察（秒级，不加载 torch）
python_env\python.exe -m aifootball pose run --out output\pose\final   # 三组实验 + 诊断（约 7 分钟）
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
