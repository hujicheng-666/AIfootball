# 第三视角腿脚姿态预测点球方向 · 可行性分析

> 议题：在现有双目基础上增加第三路相机拍摄点球手脚部/腿部，训练模型预测射门方向，由预测结果控制守门员起扑方向，后续移动仍由趋近算法调整。
>
> 结论先行：**方向正确、技术可行，但当前真正的瓶颈不是算法，而是「零标注数据 + 三路未同步 + 起扑链路没有反应延迟」。** 直接加相机训模型，会得到一个"看起来很准但门将依然每次扑对"的系统。本报告给出三处必要修正与分阶段路线。

---

## 摘要：六个关键结论

| # | 结论 | 依据 |
|---|---|---|
| 1 | 问题定位准确，但比描述更严重。当前守门员不只是"方向100%正确"，而是**零延迟拿到球门平面上的精确落点真值坐标** | `TendGoalMainState.cs:113-123`、`Ball.cs:128-142` |
| 2 | 学术界已验证同一构想：触球前用姿态预测三分类方向，**准确率 89%、推理 22ms** | [arXiv:2509.26088](https://arxiv.org/abs/2509.26088) |
| 3 | 最大技术风险不是精度而是**跨被试泛化**：同一研究显示个体内预测性能高、跨个体显著退化 | [J-STAGE nolta 17(3):1079](https://www.jstage.jst.go.jp/article/nolta/17/3/17_1079/_article/-char/en) |
| 4 | 算力不是阻塞项。本机实测 CPU 上 YOLO11n-pose **60–70ms/帧**；关键点序列模型（LSTM/TCN）在 CPU 上训练与推理**完全可行** | 本报告 §3.4 实测 |
| 5 | 第三路相机**不是无条件必需**。现有 3264×1440 画面中腿部每段已有 130–150px、关键点置信度 0.96–0.99；第三路的价值在**视角几何**而非分辨率 | 本报告 §3.2 实测 |
| 6 | 有两条几乎零成本的"去作弊"路径可先落地，用于校准产品手感，再决定是否投入相机与数据采集 | 本报告 §5.1 修正 C |

---

## 1. 现状取证

### 1.1 起扑决策链路（代码级）

```
CSV 轨迹回放 ──► Ball.PlayTrajectory(trajectory, goalkeeperTargetCenter)
                      │  Ball.cs:128-142
                      └─► OnBallLaunched(duration, avgSpeed, initial, targetRoot)   ← target 即真实落点
                              │
                              ▼
                        GoalKeeper.Instance_OnBallLaunched()      GoalKeeper.cs:294-299
                              │
                        TendGoalMainState.Instance_OnBallLaunched()  TendGoalMainState.cs:113-123
                              │  Owner.BallHitTarget = target        ← 真值写入
                              │  Machine.ChangeState<InterceptShotMainState>()  ← 同一帧切换，零延迟
                              ▼
                        InterceptShotMainState.Enter()            InterceptShotMainState.cs:48-134
                              │  ballAtGoalLocal = WorldToGoalCoordinates(BallHitTarget)   ← 精确落点
                              │  biasedTargetLateral = clamp(ballAtGoalLocal.x - sidePreference·…)   
                              │  desiredLateral = Lerp(currentLateral, biasedTargetLateral, anticipation)
                              │  _playerSteeringTarget = goalLineCenter + right·desiredLateral + forward·offset
                              ▼
                        RPGMovement.SetMoveTarget(目标)  ──►  趋近/转向运动
```

**趋近算法本身没有问题**：`anticipation`（0.35–0.95，随 `GoalKeeping`/`SaveProbability` 变化）、`maxDiveTravel` 限制、`_turn` 判定、IK 手部目标，这些都是合理的拟人化处理。问题在于**喂给它的输入是真值**。

### 1.2 "作弊"的三个层次

| 层次 | 现状 | 真实世界 |
|---|---|---|
| **信息** | `BallHitTarget` = 球门平面精确落点 (x, y) | 只能看到踢球者身体与球出脚后最初几帧 |
| **时机** | 与球发射同一帧获得，`_timeOfBall…` 甚至反推 0.12–1.20s | 必须在球出脚前后 ~200–350ms 内决策 |
| **准确性** | 100% 正确 | 顶级守门员点球方向判断正确率远低于 100% |

补充证据（本报告核对的死代码清单，全部"已定义、无调用者"）：

| 符号 | 位置 | 说明 |
|---|---|---|
| `GoalKeeper.GetDiveReactionTime()` | `GoalKeeper.cs:587-599` | 低/中/高/中路 4 个反应时间参数（0.10/0.14/0.20/0.08 s）**从未生效** |
| `GoalKeeper.EvaluateSaveQuality()` | `GoalKeeper.cs:563-584` | "确定性射门难度评分"从未被调用 |
| `GoalKeeper.SetDebugInterceptPlan()` | `GoalKeeper.cs:540-546` | 仅被 `OnDrawGizmosSelected` 消费 |
| `Ball.Instance_OnBallLaunch()` | `Ball.cs:123-126` | 无调用者 |
| `CheckIfHasBall` / `IdleWithBall` / `IdleWithNoBall` | `IdleMainState.cs:11-18` 整段被注释 | 三个子状态从未注册 |

即：**"反应延迟"这件事在整个仿真里是缺位的**，参数写了、逻辑写了，但没有接线。

**另一个关键事实：仿真里没有踢球者。** Unity 场景中只有一个动画控制器（门将专用）与一个人形模型（含手套材质），不存在任何射手动画或角色类。也就是说：

- **真人射手只存在于相机拍摄的视频里**，仿真只回放"球的轨迹 + 门将"；
- 因此"从腿脚预测方向"这一步天然属于 **Python 侧离线计算**，Unity 只是一个消费预测结果的播放器；
- 这实际上**降低了 Unity 侧的改动风险**（见 §5.3），但也意味着预测的视觉呈现（例如在界面标注"预判方向/置信度"）需要在 WPF 或 Unity UI 里新做。

另一处"软作弊"：`GoalKeeper.RollSaveAttempt()`（`GoalKeeper.cs:513-538`）用 `SaveProbability` 概率图做了一次随机判定，并用 `successScale = ShouldSaveSucceed ? 1f : 0.58f` 缩放有效触及范围。这**看起来**像不确定性，但它作用在"已经知道落点"之后——是"先知道答案，再决定这次要不要手软"，而非"因为不确定所以扑错方向"。这正是用户所说"没有意义"的根源。

### 1.3 视觉与算力现状（实测）

样本视频实测规格（本报告实测，非文档描述）：

| 样本 | 路 | 分辨率 | 帧率 | 帧数 | 时长 |
|---|---|---|---|---|---|
| sample1 | left | 3264×1440 | 59.63 | 160 | 2.683 s |
| sample1 | right | 3264×1440 | 60.00 | 123 | 2.050 s |
| sample2 | left/right | 3264×1440 | 60.00 | 137 | 2.283 s |
| sample3 | left | 3264×1440 | 58.44 | 337 | 5.767 s |
| sample3 | right | 3264×1440 | 59.79 | 282 | 4.717 s |

**关键观察（对方案影响很大）**：

1. **左右两路连软同步都没有**：sample1 左 160 帧 / 右 123 帧，时长差 0.63 s。更关键的是，**录制路径根本不落盘逐帧时间戳**——`FrameClock`（流内 PTS → 统一秒）已被创建（`camera_capture.py:456,523`），但录制循环从不调用 `clock.stamp()`，因此只有 live 路径带时间戳；`samples/*/` 下除 mp4 外没有任何元数据文件。历史日志还显示两路实测帧率差异极大（左 2448×1080 @ **37.6 fps** / 右 1920×1080 @ **13.9 fps**）。离线重建目前靠事后软对齐（`find_best_time_offset`，`reconstruct_3d_trajectory.py:1994`），日志实测偏移达 **+0.03 – +0.24 s**。三路若不同步，时序对齐与"提前量"就无从谈起。
2. **录制窗口极短**（2–2.7 秒）。管线只在 `kick_frame` 前回看 `LEAD_IN = max(sparse_every, fps*0.3)`（`reconstruct_3d_trajectory.py:997-998`），约 **0.3 秒**。而"助跑 + 支撑脚落地 + 髋部旋转"这段有效信息通常分布在触球前 **0.5–1.5 秒**。不扩预滚动，模型根本看不到关键动作。
3. **现有帧缓冲已有基础**：`camera_capture.py:189` 用 `deque(maxlen=300)` 保存最近 300 帧（60fps ≈ 5 秒）。这是现成的环形缓冲，扩展成"落盘预滚动"改动量很小。**注意 `FrameClock` 同样已经写好，只需在录制循环里接线**——两项基础设施都已经存在，属于"没接线"而非"没实现"。
4. **相机视角是广角全景**：画面里球门与人同框，人物占画面高度 46%–53%，说明相机到踢球者距离较远、以覆盖场景为主。
5. **两路视野重叠有限**：sample3 的 right 画面中踢球者几乎不在视野内（仅左边缘露出一只鞋）。这对"用双目三角化得到 3D 姿态"的路线是硬约束。
6. **样本与当前采集管线对不上**：6 个 mp4 都是 H.264，而 `camera_capture.py:643` 写入的是 `mp4v`；且实际采集日志的分辨率是 2448×1080 / 1920×1080，而非样本的 3264×1440。样本可能来自其他采集路径或经过转码——**现有数据不能视为当前管线的标准产物**。
7. **"边录边分析"从未成功跑完过一次**：日志中 `online-live` 多次启动后数秒内被取消，`samples/sample_live/` 与 `output/trajectory_3d/sample_live/` 至今为空目录。也就是说 live 路径既没有实测帧率数据，也尚未被验证可用。
8. **检测与采集在同一线程串行**：`live_analysis.py:623-686` 的循环里顺序 read 两路并同步检测，没有推理线程或队列（只有预览 JPEG 编码被放到后台线程）。**加第三路会线性增加单轮耗时**，`--analyze-every` 是目前唯一的降载手段。

**算力实测**（本机 `python_env`，torch 2.13.0+**cpu**、Intel UHD Graphics、无独显）：

| 项目 | 实测值 |
|---|---|
| YOLO11m 球检测（640px） | **~520 ms/帧**（`reconstruct_3d_trajectory.py:40-44` 注释，960px 时 ~1.1 s/帧） |
| YOLO11**n-pose** 姿态（640px） | **65.4 / 69.1 / 62.6 ms** ≈ 15 fps |
| YOLO11n-pose 姿态（320px） | **22.2–47.5 ms** ≈ 21–45 fps |
| 密集跟踪 160 帧 | 实测日志 190 s（约 1.2 fps） |
| 日志中 GPU 状态 | 全部为"CPU 推理模式: Intel(R) UHD Graphics" |

> 结论：**姿态估计在 CPU 上可以做到 15–40 fps，不是瓶颈**；瓶颈是 11m 级大分辨率下的球检测。这直接决定了模型的技术选型（见 §5.4）。

### 1.4 数据现状：当前可用训练数据 = 0

| 项目 | 现状 |
|---|---|
| 样本目录 | `samples/` 下 5 个目录，仅 sample1/2/3 有录制；`sample_live/`、`_final_check/` 为空目录 |
| 样本内容 | 实拍为**球场走动/走位测试**，非点球射门动作（本报告抽帧核实，临时文件已清理） |
| 轨迹数据 | `data/*.csv` 共 3 条，步长 1/120 s，且为**合成直线数据**（x 从 11.0 起精确线性递减、z 线性上升，非弹道曲线；首行 z=0.11 与当前球半径 0.145 也不一致） |
| 元数据 | `data/*.meta.json` 只记录 `GoalkeeperName`，无采集参数、无时间戳 |
| 方向标签 | 无 |
| 人体姿态标注 | 无（全工程搜索 `pose/keypoint/skeleton/mediapipe` 在 Python 侧 **0 匹配**） |
| 已装依赖 | torch 2.13.0+cpu、torchvision 0.28.0+cpu、ultralytics 8.4.103、opencv 5.0.0、numpy/scipy/polars；**无 mediapipe、无 onnxruntime、无 GPU 版 torch**；`requirements.txt` 同样未包含，新增依赖需同步 `python_env` |

这是本方案**最大的实际成本项**：一切从零开始。

---

## 2. 需求本质：什么叫"有意义"

### 2.1 真实守门员的时间预算

| 环节 | 典型耗时 |
|---|---|
| 点球点 → 球门线 | 11.0 m |
| 球速（业余 → 职业） | 15–30 m/s |
| 球飞行时间 | **0.37 – 0.73 s** |
| 视觉→肌肉启动反应 | 200–350 ms |
| 完成侧扑并触及球门角 | 500–700 ms |
| **合计需求** | **0.7 – 1.05 s ≫ 飞行时间** |

**这个不等式就是整个方案的全部意义**：守门员在生理上不可能"看完再动"，必须**在触球前或触球瞬间形成方向判断**，并承担判断错误的代价。

因此，"用触球后重建的真实落点"在物理上是不可能的输入；而"用触球前的身体线索 + 触球后最初几帧的球信息"才是真实可用的输入集合。

### 2.2 仿真侧物理约束（决定了预测精度值多少钱）

从门将配置与场景实测得到的硬约束：

| 量 | 取值 | 来源 |
|---|---|---|
| 球门 | 7.32 m × 2.44 m | `Goal.prefab` / `Goal.cs:236,246` |
| 点球点距离 | 11 m | `Goal.cs:9`；首行校验容差 0.35 m（`GameManager.cs:465-478`） |
| 门将身高 | 1.83 – 2.02 m | `data/goalkeepers/*.json` |
| 飞扑最大速度 | 3.55 – 5.65 m/s | 同上（`DiveSpeed`） |
| 水平飞扑最大距离 | 1.06 – 1.72 m | 同上（`JumpDistance`） |
| 手臂触及 | 0.54 – 0.88 m | 同上（`Reach`） |
| 实际有效触及 | 约 0.28 – 0.90 m | `_effectiveReach = Reach × probabilityScale × successScale`（`InterceptShotMainState.cs:72`） |
| 球半径 | 0.145 m | 场景 `SphereCollider` |

**推论**：门将横向可覆盖约 **1–2.5 m**（`JumpDistance + Reach`，再乘概率缩放）。球门半宽 3.66 m，意味着**门将本来就够不到两侧死角**——这与真实足球一致。

于是"预测方向"的价值被精确定义为：**在 1–2.5 m 的可达窗口内，把起扑放到正确的一侧**。预测横向误差超过约 1 m 就会从"扑到"变成"扑空"。这既说明预测精度要求不高（不需要厘米级），也说明了为什么"方向 100% 正确"会让整个扑救判定失去意义——可达窗口太窄，正确答案几乎等于必然扑到。

### 2.3 可量化验收标准（建议写入需求）

当前系统缺一个"什么叫真实"的客观定义。建议采用以下三条：

| 指标 | 目标值 | 说明 |
|---|---|---|
| **端到端扑救率** | **18% – 30%** | "手感真实"的校准锚点。公开统计中顶级联赛守门员的长期扑救率量级就在 20% 上下：有统计称五大联赛领先者面对 90 次点球扑出 20 次（约 22%）[来源](https://m.leisu.com/news/view-282137)。建议最终以**本项目自采数据**重新标定。高于 35% 说明门将仍然过强 |
| **方向判断正确率** | **55% – 75%**（触球前 200ms 决策） | 显著高于三分类随机基线 33%，但远低于 100% |
| **提前量-准确率曲线** | 必须随提前量增加而单调下降 | 若曲线平坦，说明存在标签泄漏（见 §4.3） |

> 建议把"扑救率落在 18%–30% 区间"作为项目验收的**第一指标**，模型准确率只是中间指标。这能避免"模型很准但游戏手感没变"的失败模式。

---

## 3. 技术可行性论证

### 3.1 文献与工业证据

| 研究 | 方法 | 关键结果 | 对本项目的含义 |
|---|---|---|---|
| [Ranasinghe & Ranasinghe 2025, arXiv:2509.26088](https://arxiv.org/abs/2509.26088)（Springer LNCS 15925, Sports Analytics） | MobileNetV2(RGB) + LSTM(2D关键点) 双分支 + 姿态引导注意力；755 个真实比赛点球事件；**触球前**预测左/中/右 | **准确率 89%**，比纯视觉/纯姿态基线高 **14–22%**；**推理 22 ms** | 构想已被完整验证；轻量化可实时 |
| [Amano & Hosaka 2026, NOLTA 17(3):1079](https://www.jstage.jst.go.jp/article/nolta/17/3/17_1079/_article/-char/en) | OpenPose 25 关节 2D 坐标 + Echo State Network，单关节时间序列 | **踢球者特定条件下性能高，跨被试泛化有限**；DAPU 分析定位了决策形成的时间区间 | **必须做个体化模型**，这是本方案最重要的工程结论 |
| [Takahashi 等，支撑脚朝向估计射门方向](https://researchmap.jp/read0110612/published_papers/46717055?lang=en) | 关注支撑脚（非踢球脚）朝向 | 支撑脚指向是有效线索 | 决定第三路相机的**机位**：必须能看清支撑脚指向 |
| [EfSUPIT 2025, 基于身体姿态与运动学指标的预测模型](http://efsupit.ro/images/stories/november2025/Art%20267.pdf) | 躯干朝向与球方向一致性 | 一致率约 **49%** | 单一生物力学指标**远不足以**预测；必须多线索融合，规则阈值方案不可行 |
| [Dicks, Button & Davids 等，时间遮蔽/注视研究](https://www.sportwissenschaft.de/fileadmin/pdf/schriftenreihe/dvs201_asp-2001.pdf) | 时间遮蔽范式 | 精英守门员在触球前即获得大部分信息 | 提供了评估方法学：**必须用时间截断评估**，而非全序列评估 |

**综合判断**：方向预测在技术上是成熟的、被多次复现的。真正的不确定性在数据规模与个体泛化，而非算法本身。

### 3.2 第三路相机：必要性需要先证明

这是本方案最容易被忽略的一点。**直觉是"特写相机能看到更多细节"，但数据说现有相机可能已经够用**：

实测（YOLO11n-pose 在现有 3264×1440 帧上）：

| 画面 | 人体框 | 占画面高 | 大腿 | 小腿 | 关键点置信度 |
|---|---|---|---|---|---|
| sample1 left | 306 × 670 px | 46.5% | 132 px | 142 px | 0.984 – 0.999 |
| sample3 left | 385 × 760 px | 52.8% | 149 px | 151 px | 0.967 – 0.998 |

即：现有广角相机下，小腿段已有约 **140–150 px**，关节定位精度约 ±3–5 px，角度分辨率约 **2–4°**。**这个量级究竟够不够区分支撑脚指向或髋部开合角，必须用实验回答，不能靠直觉判断**——这正是 §6 中 P0 实验要解决的问题。

那么第三路相机的真实增量价值在哪里？三个地方，且都不是分辨率：

1. **观测几何（最关键）**。现有两路都架在球门附近，拍踢球者属于侧后方视角。侧视下：
   - 髋部旋转沿光轴方向被**透视压缩**，图像中几乎测不出开合角；
   - 支撑脚指向同样接近侧视，图像中指向角退化。
   
   而支撑脚指向与髋部朝向恰是文献中最强的两类线索。**一台架在踢球者侧前方 40°–60° 的相机，能把这两个量从"测不出"变成"可测"**——这才是加相机的核心理由。
2. **遮挡鲁棒性**。助跑与摆腿时，现有视角下支撑腿常被摆动腿或身体遮挡。
3. **冗余**。三路中任意一路关键点置信度低时，仍有两路可用。

> **建议：不要先买相机。** 先用现有双目跑一遍姿态预测基线实验（见 §6 P0），用实测增益决定是否投入。若 pose-only 在现有视角已达 65%+，第三路相机的边际收益可能不足以支撑其同步成本。

### 3.3 数据规模估算

参照文献与常见姿态时序建模经验，给出量级估计：

| 路线 | 每人所需有效踢球次数 | 说明 |
|---|---|---|
| 固定落点（1 类） | — | 不可行，模型学不到东西 |
| **3 类方向（左/中/右）** | **≥ 150 次/人** | 每类 ≥50 次，最低可用门槛 |
| **9 宫格（3×3）** | **≥ 300 次/人** | 每格 ≥30 次，推荐目标 |
| 跨个体通用模型 | ≥ 2000 次、≥10 人 | 文献显示泛化困难，不建议作为一期目标 |

**一个决定性的成本优势**：**标注是自动的、免费的**。现有双目管线已经能重建球的真实三维落点（`reconstruct_3d_trajectory.py`），把它作为方向标签即可实现自监督标注。不需要人工标注方向，也不需要额外设备。这使得数据采集的唯一成本就是**场地时间与人力**，而不是标注费用。

**采集效率估算**：每次踢球含捡球、摆球、恢复约 25–40 秒 → 300 次 ≈ **2.5–3.5 小时/人**。加上设备架设与质检，**1 人 1 天可完成一个射手的完整数据集**。这个量级是现实的。

### 3.4 算力可行性与技术选型

实测已经给出答案，这里给出选型推论：

| 方案 | CPU 可行性 | 说明 |
|---|---|---|
| 端到端视频模型（MobileNetV2 + LSTM，即文献方案） | **不可行（实时）** | RGB 分支在 3264×1440 上无法实时；但**离线推理可行**，训练需 GPU |
| **关键点序列模型（推荐）** | **完全可行** | 输入是低维坐标序列（如 17 关节 × 90 帧 × 2 ≈ 3060 维），TCN/LSTM（<1M 参数）CPU 推理 <5ms、CPU 训练分钟级 |
| 规则阈值（支撑脚角度 > x） | 可行但无效 | §3.1 已证明单一线索一致率仅约 49% |

> **推荐技术路线：姿态关键点 → 手工运动学特征 + 轻量时序模型。** 这条路线与当前 CPU-only 环境完全匹配，且绕开了本机最大的性能瓶颈（大分辨率球检测）。

---

## 4. 关键风险与对策

按严重度排序：

| # | 风险 | 严重度 | 说明 | 对策 |
|---|---|---|---|---|
| 1 | **跨被试泛化失败** | 高 | 文献明确：个体内高、跨个体低。不同射手的动作习惯、体型、助跑方式差异大 | 一期做**个体化模型**（每个射手一个模型/一组权重）；用少量样本做微调（fine-tune）而非从零训练；模型输出带置信度，低置信时退化为保守策略 |
| 2 | **三路不同步（既有技术债）** | 高 | 现状连两路都不同步（帧数 160 vs 123）。三路不同步则"提前量 Δt"无意义，特征与标签会错位 | 采集阶段为每帧写入统一时钟时间戳（`time.monotonic`）；用共享事件标记（闪光/拍手/哨声）做互相关对齐；硬件条件允许时上硬件触发 |
| 3 | **标签泄漏导致"虚高的准确率"** | 高 | 若特征窗口包含触球后帧，或用到了球的落点信息，准确率会飙到 95%+，回到"作弊"原点 | 严格时间截断：训练/评估时特征窗口硬性截断在 `t_kick - Δt`；`Δt ∈ {0, 100, 200, 300, 500ms}` 分别训练与评估；把"准确率-提前量曲线单调性"作为验收项 |
| 4 | **模型太准 = 新瓶装旧酒** | 高 | 即使离线指标漂亮，若门将每次仍扑对，体验没有改善 | 输出必须是**概率分布**而非 argmax 方向；用 log loss / ECE（校准误差）而非 accuracy 作为主指标；引入 temperature scaling 校准；端到端验收看 §2.3 的扑救率 |
| 5 | **起扑仍无反应延迟** | 中 | 即使换用预测值，门将仍在球发射同帧起扑，等于"预测得比人还快" | 启用现成的 `GetDiveReactionTime()`（`GoalKeeper.cs:587-599`，当前是死代码），或引入统一 `reactionDelay`（180–280ms）；起扑时刻 = `t_kick + reactionDelay` |
| 6 | **录制窗口过短，看不到助跑** | 中 | 当前 2–2.7s、`LEAD_IN` 仅 0.3s | 把 `deque(maxlen=300)`（`camera_capture.py:189`）扩展为"预滚动落盘"：开始分析时先把之前 3–5 秒写入视频，或录制结束后向前扩展剪辑 |
| 7 | **数据采集成本与时程** | 中 | 每射手 2.5–3.5 小时场地时间，且需重复覆盖 9 宫格 | 设计采集协议（固定球速档位、固定助跑角度、随机化落点顺序以避免射手形成模式）；先跑通 1 个射手的最小闭环再放量 |
| 8 | **本机无 GPU，训练受限** | 中 | torch 为 `+cpu` 版本；只有 Intel UHD 集显 | 轻量路线下 CPU 训练可行（分钟级）；若要复现文献的 RGB 分支，需 GPU（自购约 3500 元级，或云租 2–5 元/小时） |
| 9 | **硬编码 left/right 的改动面** | 低 | `DualCameraRecorder` 为二元结构、`load_camera_configs()`（`reconstruct_3d_trajectory.py:102-130`）与 `resolve_camera_videos()`（`:133-144`）把两路写死 | 已列出逐处清单，见 §5.5；抽象成 `cameras: list[...]` + 目录扫描即可 |
| 10 | **标定流程需重做** | 低 | 新相机需内参标定 + 外参标定；现有流程已完备（`calibrate_intrinsics.py`、`estimate_extrinsics.py`） | 复用现有流程；若第三路只需 2D 姿态特征（不做三角化），甚至**只需内参**，成本大幅降低 |
| 11 | **"实时"路线尚未被验证** | 中 | `online-live` 在日志中**从未完整跑完过一次**，`samples/sample_live/` 为空；且检测与采集同线程，加第三路会线性变慢 | 一期只做**离线**（录完再算），这与当前 CPU-only 环境匹配；实时能力明确列入 P4，不作为一期承诺 |

---

## 5. 建议方案

### 5.1 三处必要修正

**修正 A：输出"球门平面上的概率分布"，而不是方向标签。**

现有门将需要的是连续落点 `(x, y)`。若模型只给"左/中/右"，门将只能扑三个离散点，表现反而不如现在自然。建议：

- 模型输出 **3×3（或 5×3）网格上的 softmax 概率分布**；
- 门将取该分布的**期望点**（或带风险偏好的分位点）作为 `_playerSteeringTarget` 的横向/高度依据；
- **关键**：不确定时分布的期望自然收敛到中路——这恰好复现了真实守门员"不确定就站住中路"的行为，无需额外规则；
- 现有 `anticipation` 插值机制**保留**（它负责"知道方向但身体要花时间过去"），只把输入从 `BallHitTarget` 换成预测期望点。这是一个**非常小的改动**。

**修正 B：与"触球后早期球观测"做贝叶斯融合——而不是二选一。**

真实守门员的决策是两段式：

```
触球前 [t-1.2s .. t-0.1s]  →  身体线索  →  先验分布 P(落点)
触球后 [t+0 .. t+80ms]     →  球的初速度/前 2–3 帧位移  →  似然  →  后验分布
                                   │
                                   ▼
                        起扑决策（在 t + reactionDelay 时刻执行）
```

**这个融合架构可以复用现有双目，不需要任何新硬件**，并且天然给出"早期不确定 → 后期修正"的真实行为。即使第三路相机方案失败，这条路径单独也能显著改善手感。

**修正 C：先做两条零成本基线，再决定是否投入相机与数据。**

| 基线 | 做法 | 成本 | 价值 |
|---|---|---|---|
| **C1 观测降级** | 不训练任何模型，直接给 `BallHitTarget` 加**延迟 + 量化 + 噪声**：延后 200–300ms 再进入起扑、把落点量化到 3×3 网格、按可调难度参数叠加高斯扰动 | **约几十行代码** | 立刻量化"扑救率"这个验收指标，得到可信的体验基线；也能测出"模型至少要达到什么水平才有意义" |
| **C2 现有视角姿态基线** | 用现有左相机（或双目）跑姿态估计 + 时序模型，测方向预测上限 | 只花采集时间 | **直接回答"第三路相机到底需不需要"**——这是本方案唯一的硬件决策依据 |

> 强烈建议先做 C1+C2。C1 能用最小代价证明"这个方向值不值得做"，C2 能省掉一次可能无用的硬件投入。两步合计可在 **1–2 周**内给出决策依据。

### 5.2 目标架构

```
┌─ 采集层（三路，统一时间戳）──────────────────────────────────┐
│  Cam-L ─┐                                                    │
│  Cam-R ─┼─► 时间戳写入 + 事件标记对齐 ─► 预滚动缓冲(≥3s)      │
│  Cam-F ─┘   (侧前方 40-60°，拍髋→脚)                          │
└──────────────────────┬───────────────────────────────────────┘
                       ▼
┌─ 感知层（离线，CPU 可跑）────────────────────────────────────┐
│  球检测(YOLO11m) ─► 双目三角化 ─► 三维弹道 ─► kick_frame      │
│  姿态估计(YOLO11n-pose, 裁剪 ROI) ─► 关节序列                 │
└──────────────────────┬───────────────────────────────────────┘
                       ▼
┌─ 预测层 ─────────────────────────────────────────────────────┐
│  归一化(髋中心/身高) ─► 特征(坐标+速度+角度) ─► TCN/LSTM      │
│  ─► 3×3 概率分布 (含置信度)                                   │
└──────────────────────┬───────────────────────────────────────┘
                       ▼
┌─ 决策层 ─────────────────────────────────────────────────────┐
│  先验(姿态) ⊗ 似然(球出脚后早期观测) ─► 后验 ─► 期望落点      │
│  ─► 起扑时刻 = t_kick + reactionDelay(180-280ms)              │
└──────────────────────┬───────────────────────────────────────┘
                       ▼
┌─ 仿真层（Unity，改动最小）───────────────────────────────────┐
│  InterceptShotMainState 保留趋近算法                           │
│  仅替换输入：BallHitTarget(真值) ─► PredictedTarget(预测期望)  │
│  新增：起扑延迟、置信度驱动的保守策略                          │
└──────────────────────────────────────────────────────────────┘
```

### 5.3 Unity 侧最小侵入注入点（含精确落点）

**时间轴事实**：回放中 `t=0` 就是 CSV 第一行，且首行被强制校验为"靠近点球点"（`GameManager.cs:465-478`，容差 0.35 m）。也就是说 **t=0 ≡ 踢球瞬间**，`OnBallLaunched` 在 `Ball.PlayTrajectory` 内**先于**球开始运动被触发（`Ball.cs:139-141`），门将在同一帧 `SetTrigger("Dive")`（`InterceptShotMainState.cs:130`）。因此：

- 预测结果必须在 `Ball.PlayTrajectory` **被调用之前**就已写入门将；
- "提前量"在离线回放里的物理含义是"模型只允许看到 t<0 的那部分数据"。

**推荐实现（改动 2 个文件、各约 5 行）**

| 步骤 | 位置 | 内容 |
|---|---|---|
| 1 | `GoalKeeper.cs:138` 旁 | 新增契约字段：`HasShotPrediction` / `PredictedLateral`（米，+= 门将右侧）/ `PredictedHeight`（米）/ `PredictionConfidence`（0–1） |
| 2 | `GameManager.cs:283`、`PenaltyKickApp.cs:398` | 在调用 `Ball.PlayTrajectory` **之前**写入上述预测值（这是唯一语义正确的位置） |
| 3 | `InterceptShotMainState.cs:85` | 在 `biasedTargetLateral` 算出后插入混合：`biasedTargetLateral = Lerp(biasedTargetLateral, Owner.PredictedLateral, confidence)` |
| 4 | `InterceptShotMainState.cs:127` | `_height` 同样按置信度混合到 `PredictedHeight` |
| 5 | `TendGoalMainState.cs:51-81` | （可选）让门将在球未踢出时就依预测提前横移，体感更像"提前判断"；该状态已被限制在门线上横移，不会违规前压 |

**关键取舍**：预测值应与真值 `BallHitTarget` **并存**（前者用于决策，后者仅用于对照与评估），这样可以在同一套回放里直接量出"预测版本 vs 真值版本"的扑救率差，而不必维护两条代码路径。

**注意机制分离**：`InterceptShotMainState.cs:67` 的 `RollSaveAttempt()` 是**随机**判定，与几何接触判定（`GoalKeeper.TryGetAnimatedContact`，骨骼胶囊扫掠，唯一权威）是两套机制。若希望"置信度"影响的是"够不够得着"，应作用于有效触及范围（`_effectiveReach`，`:72`）而非只改目标点。

**数据通道建议**：预测结果最适合与轨迹一起落盘，扩展已存在的 `data/<sample>_trajectory.meta.json`（当前只记录绑定的门将），Unity 载入轨迹时一并读取。这样**不需要新增 WPF→Unity 命令、也不受 `WpfCommandBridge` 0.15s 轮询延迟影响**，零协议风险。

**其他可复用的现成能力**：`GoalKeeper.PrepareForShot()`（`:256-260`）已存在；`EvaluateSaveQuality()`（`:563-584`）与 `SetDebugInterceptPlan()`（`:540-546`）当前**均为死代码**，可作为调试/可视化接入点。

改动集中在 2 个文件、约 50–150 行，风险可控。

### 5.4 模型设计建议（匹配当前 CPU-only 环境）

| 项 | 建议 |
|---|---|
| 输入 | 触球前 1.0–1.5s 的骨骼序列（17 关节），坐标以髋中心为原点、按身高归一化 |
| 特征 | 原始坐标 + 一阶差分（速度）+ 派生角度（髋轴朝向、膝关节角、踝关节角、支撑脚指向、躯干-大腿夹角） |
| 模型 | TCN 或单层 LSTM/GRU，参数量 < 1M；不必上 Transformer |
| 输出 | 3×3 网格 softmax（含"射偏"弃权类可选） |
| 损失 | 交叉熵 + label smoothing；类别不平衡时用 focal loss |
| 校准 | 训练后 temperature scaling，用 ECE 评估 |
| 数据增强 | 时间抖动、镜像（注意左右语义！）、关节噪声、尺度扰动 |
| 评估 | **时间截断**（Δt ∈ {0,100,200,300,500}ms）的准确率-提前量曲线 + 混淆矩阵 + 校准曲线 |
| 基线 | 多数类、随机（33%）、单线索规则（支撑脚角度阈值）——必须全部显著超过 |

### 5.5 相机选型与三路同步（落地细节）

**硬件：成本可以接近零。** 现有采集链路是"手机 + MJPEG 推流"，`mobile-camera-bridge`（Android Camera2）明确按"两台手机分别为 left/right 路"使用，Python 侧 `--cam-left/--cam-right` 接受任意 `http://<ip>:8080/video`（`live_analysis.py:751-752`）。因此**第三路直接用第三台 Android 手机 + 同一个 APK**即可，无需采购工业相机。

> 注意 `mobile-camera-bridge/README.md` 的既有约束：采集分辨率一旦用于内参/外参标定，正式录制必须保持同一分辨率与相机朝向。

**机位——这才是第三路相机的全部价值所在**（§3.2）：

| 项 | 建议 | 理由 |
|---|---|---|
| 位置 | 踢球者**侧前方 40°–60°**，距离 3–5 m | 正前方会沿光轴压缩支撑脚指向，正侧面会压缩髋部开合角；45° 兼顾两者 |
| 取景 | 画面高度覆盖"髋 → 脚"，约 1.2–1.4 m | 只需下肢，不必带全身 |
| 镜头 | 手机长焦端（2x/3x） | 避免广角畸变，减少无关区域 |
| 分辨率 | **优先 1080p@60fps，而非 3264×1440@30** | 时序建模需要帧率；姿态特征不需要 3K。实测表明 640px 输入下腿部已有足够像素 |

**同步——本方案最大的工程风险**。MJPEG over WiFi 没有硬件同步，三路各自独立启动，网络抖动可达数十至数百毫秒。分三级：

1. **最低可用（立刻可做）**：每路录制时写入统一 `time.monotonic()` 帧时间戳并存为 sidecar JSON；后处理按时间戳重采样到共同时间轴。**注意这不是从零开发**：`FrameClock`（含 PTS 单位推断）已经实现，只是录制路径没调用它。
2. **推荐**：录制开始时制造一个**共享视觉事件**（手机屏幕闪烁 / LED / 拍手），使其同时进入三路画面，后处理用互相关对齐，可做到 <10 ms。
3. **高精度（仅在确有需要时）**：工业相机 + 硬件触发（成本 3,000–15,000 元）。

> 工作区里已留有可复用的验证脚本：`output/_verify/check_ts.py`（时间戳单位与帧间隔抖动）、`output/_verify/check_fifo.py`（慢消费下的丢包率）、`output/_verify/final_flow.py`（预览→start→stop 全流程）。做 P0 的同步验证时可直接改造它们，不必重写。

**同时必须解决"预滚动"**：当前录制从点击"开始分析"才起算，`LEAD_IN` 仅 0.3 s（`reconstruct_3d_trajectory.py:997-998`），而模型需要触球前 1–1.5 s 的数据。`camera_capture.py:189` 的 `deque(maxlen=300)`（60 fps ≈ 5 s）就是现成的环形缓冲——把"录制开始时先把缓冲内容落盘"做出来即可，改动很小。

**Python 侧改动面清单**（两路 → N 路）。核心结论：**如果第三路不参与球的三角化，改动面是可控的**；一旦它要进 3D 管线，就变成真正的重构。

| 位置 | 现状 | 改法 |
|---|---|---|
| `camera_capture.py:166-168` | 构造只收 `source_left, source_right` | 改为 `sources: list[str]` |
| `camera_capture.py:186-197` | 8 个定长二元状态（`_caps/_writers/_frame_buffers/_frame_counts/_fps/_clocks/_partial_paths/_final_paths`） | 按路数动态生成 |
| `camera_capture.py:543,571,603-604,615,644,671,713` | `range(2)` / `("left","right")` 共 7 处 | 改为遍历 `self.camera_names` |
| `camera_capture.py` 录制循环 | `FrameClock` 已创建但从未 `stamp()` | 接线：录制时逐帧写入时间戳（顺带解决同步） |
| `aifootball/capture/dual_camera.py` | 与 `camera_capture.py` 是**两份并行实现**（视频文件支持、UDP 缓冲 4KB vs 8MB 都不同） | 两处都要改；建议借此收敛为单一实现，避免三路逻辑再度分叉 |
| `live_analysis.py:520-546` | `run()` 只收 `cam_left/cam_right`；`caps=[None,None]` | 增加第三路参数与列表化 |
| `live_analysis.py:265-278,305,327,360` | 预览面板固定 2 画面 | 支持 3 画面布局 |
| `live_analysis.py:587-610,730` | writers / 帧尺寸 / 收尾都是二元 | 列表化 |
| `aifootball/__main__.py:15-21,130-147,160-195,236-250` | `has_camera_videos` 固定 `("left","right")`；CLI 只有 `--cam-left/--cam-right` | 增加第三路 CLI 与校验 |
| `preview_publisher.py` | 已按字符串键（`submit("left"/"right")`） | **无需改结构**，只需 `submit("foot", frame)` |
| `config.py` / `constants.py` | 无相机数量常量 | 新增 `CAMERA_NAMES` / `FOOT_CAMERA` 作为唯一事实源，避免继续散落 `range(2)` |
| **标定**：`estimate_extrinsics.py:1005-1130` | `joint_stereo_bundle_adjustment` **硬编码两相机参数布局** | 这是新增相机的**硬闸门**；若第三路不进 3D，可绕开 |
| **标定**：`estimate_extrinsics.py:1646-1659,1722-1725` | 安装白名单只有 6 个文件，且强制 `{"left","right"}` 与 `len(summary)==2` | 同上；第三路内参单独存放即可绕开 |
| **3D 管线**（仅当第三路要进 3D 时）：`reconstruct_3d_trajectory.py:2410,2417,2444-2446` | `len(video_paths)!=2`、`set(configs) != {"left","right"}` 等二元校验 | 这层要改造 |
| **3D 管线**：`fit_ballistic_trajectory.py:382-388,419-421` | 重投影误差模型**假定两相机平均**、置信度 `sqrt(conf_l*conf_r)` | 需重构为相机列表 |
| WPF：`PipelineViewModel.cs:59-71,110-122` | `CamLeft/CamRight`、`UrlLeft/UrlRight` | 增加第三路属性 |
| WPF：`Views/Pages/PipelinePage.xaml:166-171,204-211` | 左右两路输入框 + "测试左路连接" | 增加第三路输入与测试按钮 |
| WPF：`Services/PipelineService.cs:192-241` | `RunOnlineAsync/RunOnlineLiveAsync` 透传左右 | 透传第三路 |

**重要设计约束**：第三路相机**只服务于姿态特征**，不参与球的三角化。这样它**只需内参标定**，不必做外参标定——而外参标定（`joint_stereo_bundle_adjustment` 的两相机布局、安装白名单的 `len(summary)==2`）是本项目目前最重的流程之一，绕开它能显著降低工作量。若第三路坚持要进 3D 管线，请按"真正的重构"（约 2–3 周）而不是"加个参数"来估算。

---

## 6. 实施路线图

| 阶段 | 工期 | 内容 | 产出 | **决策门** |
|---|---|---|---|---|
| **P0 基线验证（零硬件）** | 1–2 周 | C1 观测降级改造；C2 用现有左/双目做姿态预测基线；扩展预滚动缓冲至 ≥3s；写入统一帧时间戳 | 扑救率基线数字；现有视角下的方向预测准确率；"提前量-准确率"曲线初版 | **第三路相机是否必要**；预测在此场景是否有可用信号 |
| **P1 采集体系** | 2–3 周 | 三路同步采集（或按 P0 结论用双目 + 1 路特写）；标定；自动标注流水线（复用现有重建结果）；采集协议 | 首个射手 300 次数据集；同步误差 < 10ms | 数据质量是否达标（关键点置信度、标签一致性） |
| **P2 模型训练与离线评估** | 2–4 周 | 特征工程、时序模型训练、校准、时间截断评估、消融（单相机 vs 三相机） | 模型 + 评估报告 + 消融结论 | 准确率-提前量曲线是否满足 §2.3 |
| **P3 仿真集成与手感校准** | 1–2 周 | Unity 注入预测 + 反应延迟 + 保守策略；端到端扑救率测量与调参 | 可体验版本；扑救率落在 18%–30% | 手感是否达到"真实" |
| **P4 扩展（可选）** | 持续 | 多射手个体化模型；在线/实时模式；RGB 多模态分支（需 GPU） | 通用化能力 | 是否值得投入算力 |

**关键路径**：P0 的 C2 实验 → 决定是否采购第三路相机 → P1 数据采集。**不要在 P0 完成前采购硬件。**

---

## 7. 成本估算

| 项目 | 估算 | 备注 |
|---|---|---|
| 第三路相机（沿用现有手机方案） | **≈ 0 元** | 项目已有 `mobile-camera-bridge`（Android Camera2 MJPEG），可直接复用 |
| 第三路相机（工业相机 + 硬件同步） | 3,000 – 15,000 元 | 仅在需要 <1ms 同步精度时必要 |
| 三脚架/支架/线材 | 300 – 1,500 元 | |
| 训练 GPU（如需复现 RGB 分支） | 3,500 元级（自购 4060Ti 16G）或 2–5 元/小时（云） | **轻量关键点路线不需要** |
| 数据采集场地与人力 | 2.5–3.5 小时/射手 | 主要成本 |
| 标注 | **≈ 0 元** | 现有重建管线自动生成标签 |
| 软件工程量（P0–P3） | 约 6–10 周（1 人） | 其中 P0 是最高性价比投入 |

**投入优先级建议**：P0（1–2 周人力，0 元硬件）> C1 改造（几十行代码）> 数据采集 > 第三路相机 > GPU。

---

## 8. 需要你确认的决策点

1. **目标定位**：这套系统是"训练分析工具"还是"要让人看得爽的仿真体验"？前者模型只做分析报告，后者才有"扑救率 18%–30%"的验收标准。二者对第三路相机的需求不同。
2. **射手是否固定**？若长期只有 1–3 名固定射手，个体化模型是最优解；若面向任意用户，跨个体泛化问题会显著抬高难度与数据需求。
3. **是否需要实时**？离线分析（录完再算）与实时（边录边判）对模型与算力的要求差一个数量级。当前 CPU-only 环境适合前者，而且**现有的 live 路径在日志里从未成功跑完过一次**——若要做实时，那本身就是一个独立课题。
4. **精度目标**：门将需要"左/中/右"三分类，还是"9 宫格"？这决定数据量与模型复杂度。
5. **是否接受先做 C1（观测降级）**：这是 1 天内可完成的、能立刻验证方向价值的最小实验。

---

## 附录 A：取证清单

本报告全部结论基于以下可复现证据：

> 注：报告写作时 Python 侧还是 `runtime/project/*.py` 单体脚本；重构后已收敛为
> `src/AIfootball.Engine/aifootball/` 包（`pipeline/`、`capture/`、`calibration/`、`live.py`）。
> 下文路径已按模块做了对应替换，语义未变；列出的行号是**重构前**脚本的行号，仅作历史取证。

**代码路径**
- `Myproject/Assets/SuperGoalie/Scripts/States/GoalKeeperStates/TendGoal/MainState/TendGoalMainState.cs:113-123` — 真值注入与零延迟切换
- `Myproject/Assets/SuperGoalie/Scripts/Entities/Ball.cs:128-142` — `PlayTrajectory` 注入真实落点
- `Myproject/Assets/SuperGoalie/Scripts/States/GoalKeeperStates/InterceptShot/MainState/InterceptShotMainState.cs:48-134` — 起扑目标与趋近算法
- `Myproject/Assets/SuperGoalie/Scripts/Entities/GoalKeeper.cs:513-538` — `RollSaveAttempt` 概率判定
- `Myproject/Assets/SuperGoalie/Scripts/Entities/GoalKeeper.cs:587-599` — `GetDiveReactionTime`（未被调用）
- `src/AIfootball.Engine/aifootball/pipeline/reconstruction.py:40-47` — 检测耗时与 imgsz 上限注释
- `src/AIfootball.Engine/aifootball/pipeline/reconstruction.py:102-144` — left/right 硬编码
- `src/AIfootball.Engine/aifootball/pipeline/reconstruction.py:261-283` — `estimate_kick_frame`（球离点球点判断）
- `src/AIfootball.Engine/aifootball/pipeline/reconstruction.py:997-998` — `LEAD_IN = max(sparse_every, fps*0.3)`
- `src/AIfootball.Engine/aifootball/capture/dual_camera.py:189` — `deque(maxlen=300)` 帧缓冲

**实测记录（本报告执行）**
- 样本视频规格：`cv2.VideoCapture` 读取 `samples/*/{left,right}/recording.mp4`
- 姿态推理耗时与腿部像素：YOLO11n-pose 在 `imgsz=640/320` 下对现有帧推理（3 次计时，含预热）
- 日志统计：`logs/aifootball-2026*.log` 中"密集跟踪"阶段耗时（160 帧 ≈ 190s）

**说明**：取证脚本与抽帧图片为临时产物，分析完成后已清理，未留在工作区。

## 附录 B：参考文献

1. Ranasinghe P., Ranasinghe P. *Predicting Penalty Kick Direction Using Multi-Modal Deep Learning with Pose-Guided Attention.* arXiv:2509.26088 / Springer LNCS 15925, Sports Analytics (ISACE 2025), pp. 179–192. https://arxiv.org/abs/2509.26088
2. Amano S., Hosaka R. *Prediction of soccer penalty kick direction from single-joint kinematics using the Echo State Network.* NOLTA, IEICE, 17(3): 1079–1099, 2026. https://doi.org/10.1587/nolta.17.1079
3. Takahashi T. et al. *Shoot Direction Estimation Focused on Orientation of Each Kicker's Support Foot in Soccer Penalty Kick.* https://researchmap.jp/read0110612/published_papers/46717055
4. *A predictive model of penalty-kick direction based on body posture and kinematic indicators.* e-Fulfillment/EFSUPIT, Nov 2025. http://efsupit.ro/images/stories/november2025/Art%20267.pdf
5. Dicks M., Button C., Davids K. *Examination of gaze behaviours under in situ and video simulation task constraints.* dvs-Schriftenreihe. https://www.sportwissenschaft.de/fileadmin/pdf/schriftenreihe/dvs201_asp-2001.pdf
6. *Anticipating the Lateral Direction of Penalty Kicks in Football From PCA-Reduced Point-Light Displays.* https://doi.org/10.1080/10407413.2017.1270148
7. Basler. *White Paper: Multi-Camera Applications / GigE Multi-Camera Synchronization.*（多相机同步工程实践）
