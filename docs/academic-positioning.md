# 学术定位与投稿策略

> **状态**：分析稿（基于 2026-09-30 的文献检索）
> **问题**：这个项目值得发 CCF-B / CCF-C 的点在哪里？
> **结论先行**：**不在"预测方向"，在"不确定性如何转化为决策"。** 预测赛道已被占满且天花板很低；决策层的骨架被部分占据但留下四个干净空位，其中"不确定性 → 保守行为的边界条件"是最硬的新颖点。

---

## 1. 赛道格局：三块地已被占据

| 层 | 谁占了 | 占了什么 | venue |
|---|---|---|---|
| **预测层** | [MambaKick](https://arxiv.org/abs/2604.16588)（2026-04） | HAR 嵌入 + Mamba，**三分类 53.1%**、二分类 64.5% | ICICV（非 CCF） |
| | [Freire-Obregón et al.](https://ar5iv.labs.arxiv.org/html/2507.12617)（2025） | 22 个 HAR 骨干，三分类 51.9%、二分类 63.9%；数据集 1010→640 样本（**网络爬取**） | **ICIAP 2025（CCF-C）** |
| | [Pose-Guided Attention](https://arxiv.org/abs/2509.26088)（ISACE 2025） | MobileNetV2 + LSTM 双分支 | Springer LNCS |
| | [ESN](https://www.jstage.jst.go.jp/article/nolta/17/3/17_1079/_article/-char/en)（NOLTA 2026） | OpenPose + Echo State Network | NOLTA |
| **决策层（离散）** | [Bransen, Janssen & Davis 2025](https://arxiv.org/abs/2505.24629)（KU Leuven / KNVB） | 7,872 个专家标注点球；**首次把门将 action capacities 参数化**（早扑 3.0–3.2 m / 晚扑 2.6–2.9 m）；`p(save)=p(扑对方向)×p(够到)`，数学上等价于最小化期望失球 | arXiv（cs.LG + cs.GT） |
| **时机层** | [MOS 2026-09-21](https://arxiv.org/abs/2609.23976)（CMU 等，**9 天前**） | 把"何时启动扑救"建模为**单调最优停止**（Bellman/Snell），带近似误差界；仿真扑救率 67.7%→74.4% | arXiv（cs.RO） |

### 三个必须记住的数字

1. **真实守门员基线 = 46.0%（三分类）/ 54.2%（二分类）**；SOTA 模型 53.1% / 64.5%——**只高 6–7 个百分点**。
2. **中间类几乎无法预测**：MambaKick 的 center recall 仅 **10.7%**。
3. **Bransen 的方向模型 logloss 0.954 vs 基线 0.969**——预测层的提升本身就很微弱。

> **推论**：把"方向预测更准"作为贡献点**没有发表价值**——空间只剩几个百分点，且已被占位。而且这些工作的 venue 最高到 CCF-C。

---

## 2. 四个仍然干净的空位（逐条核实）

### 空位 1 ★★★：从"不确定性"优化推导出"保守行为"的边界条件

**这是最明确、最硬的新颖点。** 现有"保守"分三类，都不是我们要的东西：

| 现有做法 | 代表 | 为什么不是我们想要的 |
|---|---|---|
| 频率算出的基准 + 心理学解释 | [Bar-Eli et al. 2007](https://doi.org/10.1016/j.joep.2006.12.001) 的 action bias | **不是从不确定性的优化推导出来的**；且 [Singh & Mukherjee 2026](https://doi.org/10.1016/j.joep.2026.102886) **未能复现该现象** |
| 风险敏感控制的一般理论 | [Nagengast et al. 2010](https://doi.org/10.1371/journal.pcbi.1000857) | 不确定性来自**自体执行噪声**，不是对目标的**预测分布** |
| 最优停止的"早启动" | MOS 2026 | 方向**相反**（不确定性高 + 紧迫 → 早启动），且不决定"扑向哪侧" |
| 手工阈值规则 | Bransen 2025 的 mixed educated policy | 是 `if 超出晚扑范围 then 早扑` 的规则，**不是优化解**；且**"早扑中路"被排除在动作空间之外**（原文脚注 3） |

**检索结论（明确未找到）**：没有任何工作把"落点预测的后验不确定性"通过优化映射为"保留中路 / 延迟承诺 / 缩小动作幅度"这类保守行为，并给出**保守程度的边界条件**。

**这直接支撑我们设计里的那句话**——"不确定就守中路"必须是**期望失球最小化的必然结果**，而不是一条 `if` 语句。Bransen 的模型里根本没这个动作可选，这本身就是一条有力的论证。

### 空位 2 ★★：视觉后验 → 连续动作空间

- Bransen 用**手工 XGBoost 特征**（47 个上下文特征）+ **离散策略枚举**（早扑/晚扑 × 3 个方向区）。
- **未检索到**任何"姿态/视频预测 → 球门平面落点概率分布"直接接入扑救决策的工作。
- **未检索到**把"最小化期望失球"作为显式目标、在**连续动作空间**上求解扑救方向的工作。

**我们的做法**：动作 = 连续位置 `g`，目标 = `argmin_g Σ_cell P(cell)·[cell ∉ A(g)]`（用可达集对预测分布积分），而不是在几个预设策略里挑最好的。

### 空位 3 ★★："分布 vs 点预测"的决策层消融

**未检索到任何论文做过**这个对照实验：在同一能力约束下，用完整落点分布做期望失分最小化 vs 用点预测（argmax / 均值）。

这是**最直接的证明方式**，而且容易做、结论清晰。MOS 用 "parameter-matched learned gate"（只用置信度门控）作对照，思路相同但做的是"何时"。

### 空位 4 ★★："何时启动"与"扑向何处"的联合优化

MOS 做"何时"、Bransen 做"哪侧"，**没有人把两者放在同一个期望失分目标下联合求解**。

### 空位 5 ★★：提前量（lead time）条件化——体育领域空白，但有方法论先例

**检索结论**：体育动作预测领域**未检索到**任何"把提前量作为条件输入、用单一模型覆盖整条 accuracy–lead-time 曲线"的工作。所有已检索到的点球预测工作都用**固定时间窗**并只报**单一准确率**：

| 工作 | 输入窗口 | 报告指标 |
|---|---|---|
| Freire-Obregón, ICIAP'25 | 触球前动作（固定） | 63.9% / 52% |
| Pose-Guided Attention, ISACE'25 | 距离阈值分割（**目的正是为固定窗长**） | 89% |
| MambaKick, 2026 | 固定 48 帧（32+8+8） | 53.1% / 64.5% |
| ESN, NOLTA'26 | 逐关节时序（固定） | kicker-specific 高、跨被试有限 |

**最有价值的一条证据**：该领域最前沿团队（Freire-Obregón）在公开报道中把"提前量"列为**未来工作**——

> "接下来，我们还将研究能否仅从罚球者射门前的动作进行预测，如果可行，在保持有意义的准确率的前提下，**能够提前多久做出这样的预测**。"（[中国科学报报道](https://news.sciencenet.cn/sbhtmlnews/2025/7/385684.shtm)）

**这是领域权威公开承认的开放问题**，引用价值极高——它把我们的方向从"自说自话"变成"回应领域公认的 open problem"。

#### 但必须诚实处理方法论先例

[Ke, Fritz & Schiele, *Time-Conditioned Action Anticipation in One Shot*, CVPR 2019](https://openaccess.thecvf.com/content_CVPR_2019/papers/Ke_Time-Conditioned_Action_Anticipation_in_One_Shot_CVPR_2019_paper.pdf) 已经实现了"单模型 + 时间作为显式条件 + 一次覆盖多个时间视野"。**不能说"首次提出时间条件化"**，差异空间如下（可直接改写进论文）：

| 维度 | Ke et al. CVPR 2019 | 本工作 |
|---|---|---|
| 条件语义 | future time horizon（动作将在多久后**发生**） | **lead time τ**（距**同一个已确定事件**——触球——还剩多少 ms） |
| 领域 | 日常活动 / 烹饪（Epic-Kitchens、50Salads） | 高速对抗性体育（点球），毫秒级决策 |
| 时间锚点 | 视频起始的相对比例 β | **以触球瞬间为绝对锚点**，需帧级精确对齐 |
| 输出 | 未来动作序列 + 时长 | 球门平面落点**概率分布** |
| 评估轴 | α（观测比例）× β（预测比例）网格 | **单轴 τ 扫描** → accuracy-vs-lead-time 曲线 |
| 动机 | 效率（避免递归预测的误差累积） | **决策可用性**（守门员在不同时间预算下的可行动信息量） |

> **正确定位**："首次把提前量作为条件变量用于**体育意图预测**，并把它接到决策层"，而非"首次提出时间条件化"。

#### 迁移需要新增的论证（缺一不可）

1. **条件变量的语义有效性**：τ 大 → 模型应依赖**近端线索**（躯干、髋部）；τ 小 → 依赖**远端线索**（踢球腿、球）。有行为科学支撑：[Savelsbergh et al. 2002](https://www.bisp-surf.de/Record/PU200911005886) 发现专家**随着接近触球**才把踢球腿/非踢球腿/球视为信息源。
2. **帧级对齐鲁棒性**：τ 条件只有在触球标注足够精确时才有意义（MambaKick 特意用"触球前后各 8 帧"来降低对齐误差敏感性）。必须报告 **±1/±2 帧抖动**下的曲线稳定性。
3. **单模型 vs per-τ oracle 对照**：证明共享表征没有显著精度损失，否则条件化只是廉价近似，缺乏说服力。
4. **曲线形态的科学解释**：给出"信息悬崖"（曲线拐点）出现的位置及其与运动学线索可用性的对应关系——注意 Ke 2019 的数字并非严格单调，所以必须解释而非假设。
5. **领域必要性**：守门员在不同时间点的**可执行动作集不同**，因此需要"时间预算感知"的预测组件。

---

## 3. 可引用的理论骨架（现成的，直接站上去）

这三篇是认知科学/运动控制的成熟范式，**但从未被用于体育预判**——这是我们的理论定位：

| 文献 | 核心 | 与本项目的关系 |
|---|---|---|
| [Battaglia & Schrater 2007, *J Neuroscience* 27(26):6984](https://pubmed.ncbi.nlm.nih.gov/17596447/) | 把"看多久（信息获取）"与"有多少时间运动"当作两个**可权衡的决策变量**，决策论推导 | 与门将"早扑（时间长信息少）vs 晚扑（信息多时间短）"**结构同构** |
| [Trommershäuser, Maloney & Landy 2008, *TiCS* 12(8):291](https://pubmed.ncbi.nlm.nih.gov/18614390/) | 运动计划 = **分布 + 执行约束 + 期望损失最小化**的经典骨架 | 可直接改写为"落点分布 + 门将可达集 + 期望失球"。**差别**：原作的不确定性来自自体执行噪声，我们来自对对手的预测分布 |
| [Nagengast, Braun & Wolpert 2010, *PLoS Comp Biol* 6(7)](https://doi.org/10.1371/journal.pcbi.1000857) | 风险敏感最优控制 → 不确定性下偏好**低方差策略**（优化推导） | 为"不确定性 → 保守"提供范式，但语境不同（见空位 1） |

**外加两篇方法论武器：**

- [Forecast Skill Is Not Decision Skill](https://arxiv.org/abs/2512.14779)（2025-12，v2 2026-09）：**预测层性能不能可靠转化为下游决策性能，模型排名甚至会变**。
- [Calibrating predictions to decisions](https://dl.acm.org/doi/abs/10.5555/3540261.3541970)（**NeurIPS 2021**）：decision calibration 框架，在**决策层**而非预测层评估概率预测。

---

## 4. 建议的论文形态

**标题方向**：《从预测分布到扑救决策：不确定性如何决定门将的保守程度》
（From Forecast Distribution to Dive Decision: How Posterior Uncertainty Determines Goalkeeper Conservatism）

| # | 贡献 | 新颖性状态 |
|---|---|---|
| 1 | 姿态预测的**后验分布** → **连续动作空间**的扑救决策 | **首次**（空位 2） |
| 2 | 推导"不确定性 → 保守"的**边界条件**（命题 + 证明 + 仿真验证） | **最硬的新颖点**（空位 1） |
| 3 | 决策层消融：**分布 vs 点预测** | **首次做的对照实验**（空位 3） |
| 4 | **联合**优化"何时启动"与"扑向何处" | **首次**（空位 4） |
| 5 | **提前量条件化**：单模型覆盖整条 accuracy–lead-time 曲线 | 体育领域**首次**（空位 5）；方法论有 [Ke et al. CVPR 2019](https://openaccess.thecvf.com/content_CVPR_2019/papers/Ke_Time-Conditioned_Action_Anticipation_in_One_Shot_CVPR_2019_paper.pdf) 先例，**必须明确切割，否则会被打回** |
| 6 | 闭环物理仿真平台（可开源） | **别人没有**——预测论文只能报准确率（无门将无物理），Bransen 有仿真但无视觉 |

**行动空间的设计要求**（受 MOS 启发，见 §5 的修正）：

动作不能是一次性选点，而应是**策略** `(启动时刻, 初始方向, 保留的修正能力)`。MOS 已证明"早启动 + 保持闭环 + 被假动作揭穿后修正"优于"先锁定方向"。

---

## 5. 必须做的核心实验

| # | 实验 | 目的 | 对标 |
|---|---|---|---|
| E1 | **分布 vs 点预测的决策层消融**（同能力约束下比期望失球差、regret） | 证明"用分布"本身就有价值 | 空白；MOS 的门控基线思路 |
| E2 | **保守行为的边界条件**：扫描后验熵 × 门将能力，画出"何时应留中路"的相图 | 证明保守行为可从优化推导 | 空白 |
| E3 | **能力参数敏感性扫描**（门将速度/触及/反应时间步长扫描） | 证明"最优策略随能力变化" | 对齐 Bransen 2025 |
| E4 | **预测层 vs 决策层指标排名不一致** | 支撑 Forecast≠Decision | 对齐 [2512.14779](https://arxiv.org/abs/2512.14779) |
| E5 | **高置信度错误预测的危害量化** | 反直觉发现：错误但自信的预测比错误且犹豫更危险 | 空白 |
| E6 | 端到端扑救率（闭环仿真） + 与"只用置信度门控"基线对比 | 最终说服力 | 对齐 MOS 2026 |
| E7 | **单模型 vs per-τ oracle 对照** | 证明条件化没有精度损失 | 空位 5 论证 3 |
| E8 | **跨 τ 泛化**（在训练未见的 τ 上测试，含插值与外推） | 证明学到的是连续条件而非离散记忆 | 空位 5 论证 3 |
| E9 | **时间对齐鲁棒性**：触球标注 ±1/±2 帧抖动后重绘曲线 | 证明 τ 条件在真实标注噪声下仍成立 | 空位 5 论证 2 |
| E10 | **与行为科学人类曲线并列**（temporal occlusion 的 T1/T2/T3 三点设计） | 让模型曲线有"人类基线"作参照 | [Wu et al. 2026, Front Psychol](https://doi.org/10.3389/fpsyg.2026.1828824) |
| E11 | **逐 τ 混淆矩阵 + 分组曲线**（按踢球脚 / 罚球侧） | MambaKick 显示左右严重不对称（左 72.1% vs 右 54.7%），必须解释 | MambaKick 2026 |

**两层指标必须分开报**（Bransen 的做法值得学）：预测层报 log loss + 校准曲线；决策层报期望失球/扑救率 + regret。

---

## 6. Related Work 怎么写（切割话术）

必须主动、清晰地切割，否则会被审稿人认为重复：

| 对标工作 | 一句话切割 |
|---|---|
| Bransen 2025 | "他们用**手工统计特征**构建方向分布并在**离散策略**中枚举；我们用**视觉后验分布**，在**连续动作空间**上求解，且**不排除"留中路"**这一动作。" |
| MOS 2026 | "它决定**何时**启动（最优停止），方向交给下游控制器；我们求解的是**何时 × 何地**的联合最优。" |
| MambaKick / Freire-Obregón | "它们止步于方向分类准确率，**没有决策层**；我们展示准确率排名与决策质量排名的**不一致**。" |
| Nagengast 2010 / Trommershäuser 2008 | "这些工作把不确定性归因于**自体执行噪声**；我们的不确定性来自**对对手意图的后验分布**，且随观测时间收缩。" |
| Bar-Eli 2007 | **谨慎引用**：该 action bias 未能被 [Singh & Mukherjee 2026](https://doi.org/10.1016/j.joep.2026.102886) 复现，只能作为"存在争议的实证背景"，不能作为立论基础。 |

---

## 7. 目标 venue（依据 CCF 2022 推荐目录核实）

| venue | 等级（CCF 2022） | 所属方向 | 适配理由 |
|---|---|---|---|
| **ICME** | **CCF-B** | 图形学与多媒体 | 多媒体 & Expo，接受体育分析 + 决策系统 |
| **ICASSP** | **CCF-B** | 图形学与多媒体 | 信号处理视角，接受姿态/时序预测类工作 |
| **ECCV** | **CCF-B** | 人工智能 | 顶会但按 CCF 属 B；门槛高，适合方法最硬时冲 |
| **CVIU** | **CCF-B** | 人工智能（期刊） | 视觉 + 应用，容得下"方法 + 平台"完整工作 |
| **TCSVT / TMM** | **CCF-B** | 图形学与多媒体（期刊） | 视频理解 / 多媒体，接受体育视频分析 |
| **Pattern Recognition** | **CCF-B** | 人工智能（期刊） | 方法与实验都友好，覆盖面广 |
| **PRL / IVC / JVCIR / MVA** | **CCF-C** | 期刊 | 精简版、扎实实验型工作 |
| **ICPR / ICIP / PCM / ACCV / BMVC / FG / PRCV** | **CCF-C** | 人工智能 / 图形学与多媒体 | 保底，接受应用视觉 |
| **IEEE SMC** | **CCF-C** | 交叉/综合/新兴 | 若强调"人–机系统"视角 |

### ⚠️ 两个必须更正的认识

1. **WACV 不在 CCF 推荐目录中**（既非 B 也非 C）。我早前的建议里把它列为候选，**这是错的，不能作为 CCF 成果**。
2. **CVsports（CVPR Workshop）、CHI PLAY、ACM MIG / IMX、IEEE Transactions on Games 均不在目录中**。

> **对"个体化自训练闭环"路线的影响**：原本设想的 HCI/游戏类 venue 大多不算 CCF。可考虑 **CogSci（CCF-B，交叉/综合）**——考虑到我们的理论骨架本就来自认知科学（Trommershäuser、Battaglia）；或者**把系统部分降为 CV venue 论文的一个 contribution**，而不是单独成篇。

> **核实说明**：以上依据第三方整理的 CCF 2022 列表（[会议](https://www.cnblogs.com/xyz/p/17115088.html)、[期刊](https://www.cnblogs.com/yangykaifa/p/19615140)），**投稿前请以 [CCF 官网目录](https://www.ccf.org.cn/Academic_Evaluation/By_category/) 为准**。

---

## 8. 风险与止损

| 风险 | 说明 | 对策 |
|---|---|---|
| **强队在场** | Bransen 是 KU Leuven 的 Jesse Davis 组（体育分析顶组）；MOS 是 CMU 机器人组 | 不要正面碰撞，走"视觉后验 + 连续动作 + 保守边界"这条他们没有的线 |
| **"没有新视觉方法"的质疑** | 纯决策论可能被认为不属 CV | 保留一个视觉侧贡献（Δt 条件化预测 / 物理化姿态特征） |
| **贪多** | "预测 + 决策 + 平台"三合一显得散 | 聚焦 1–2 个核心贡献，其余作为支撑 |
| **无真实数据** | 闭环是纸面的 | 至少自采 100–200 次；公开数据只能用于预训练 |
| **action bias 误用** | 已被复现失败 | 只作背景，不作立论 |
| **决策校准不算全新** | NeurIPS 2021 已有框架 | 诚实定位为"**首次迁移到体育动作预判**"，而非发明 |

---

## 附录：核心文献清单

**点球方向预测**
1. Velesaca et al. *MambaKick: Early Penalty Direction Prediction from HAR Embeddings.* arXiv:2604.16588, 2026. https://arxiv.org/abs/2604.16588
2. Freire-Obregón et al. *Predicting Soccer Penalty Kick Direction Using Human Action Recognition.* ICIAP 2025. https://ar5iv.labs.arxiv.org/html/2507.12617
3. Ranasinghe & Ranasinghe. *Predicting Penalty Kick Direction Using Multi-modal Deep Learning with Pose-Guided Attention.* arXiv:2509.26088. https://arxiv.org/abs/2509.26088
4. Amano & Hosaka. *Prediction of soccer penalty kick direction from single-joint kinematics using the ESN.* NOLTA 17(3):1079, 2026. https://doi.org/10.1587/nolta.17.1079
5. Yeung, Ide & Fujii. *AutoSoccerPose / 3D Shot Posture Dataset.* CVPRW 2024（Apache 2.0）. https://github.com/calvinyeungck/3D-Shot-Posture-Dataset

**决策与最优停止**
6. Bransen, Janssen & Davis. *Stop Guessing: Optimizing Goalkeeper Policies for Soccer Penalty Kicks.* arXiv:2505.24629, 2025. https://arxiv.org/abs/2505.24629
7. Zhang et al. *Anticipatory Robot Goalkeeping via Monotone Optimal Stopping.* arXiv:2609.23976, 2026. https://arxiv.org/abs/2609.23976
8. Chiappori, Levitt & Groseclose. *Testing Mixed-Strategy Equilibria When Players Are Heterogeneous.* AER 2002. https://doi.org/10.1257/00028280260344678
9. Rahimian & Toka. *Inferring optimal actions in soccer with deep RL.* IJSSC 19(1):245, 2023. https://doi.org/10.1177/17479541221149481

**理论与认知科学骨架**
10. Trommershäuser, Maloney & Landy. *Decision making, movement planning and statistical decision theory.* TiCS 12(8):291, 2008. https://pubmed.ncbi.nlm.nih.gov/18614390/
11. Battaglia & Schrater. *Humans trade off viewing time and movement duration…* J Neurosci 27(26):6984, 2007. https://pubmed.ncbi.nlm.nih.gov/17596447/
12. Nagengast, Braun & Wolpert. *Risk-Sensitive Optimal Feedback Control…* PLoS Comp Biol 6(7):e1000857, 2010. https://doi.org/10.1371/journal.pcbi.1000857

**方法论**
13. Raeth & Ludwig. *Forecast Skill Is Not Decision Skill.* arXiv:2512.14779, 2025/2026. https://arxiv.org/abs/2512.14779
14. Zhao et al. *Calibrating predictions to decisions.* NeurIPS 2021. https://dl.acm.org/doi/abs/10.5555/3540261.3541970

**行为科学（谨慎引用）**
15. Bar-Eli et al. *Action bias among elite soccer goalkeepers.* J Econ Psychol 28(5):606, 2007. https://doi.org/10.1016/j.joep.2006.12.001
16. Singh & Mukherjee. *Replication: No action 'Bias' among elite soccer goalkeepers.* J Econ Psychol 113:102886, 2026. https://doi.org/10.1016/j.joep.2026.102886
17. Savelsbergh et al. *Anticipation and visual search behaviour in expert soccer goalkeepers.* J Sports Sci, 2002. https://doi.org/10.1080/026404102317284826
18. Brenner & Smeets. *Continuous use of prediction in interception.* J Neurophysiol 120(6):3257, 2018. https://pubmed.ncbi.nlm.nih.gov/30379633/

**机器人守门**
19. Huang et al. *Creating a Dynamic Quadrupedal Robotic Goalkeeper with RL.* IROS 2023. https://arxiv.org/abs/2210.04435

**动作预测与提前量条件化（空位 5 相关）**
20. Ke, Fritz & Schiele. *Time-Conditioned Action Anticipation in One Shot.* **CVPR 2019**. https://openaccess.thecvf.com/content_CVPR_2019/papers/Ke_Time-Conditioned_Action_Anticipation_in_One_Shot_CVPR_2019_paper.pdf
    > ⚠️ 检索限制：该文 PDF 未被逐字核对，其准确率数字引自 [TCCA (arXiv:2412.19424)](https://ar5iv.labs.arxiv.org/html/2412.19424v1) 的对比表，属二手来源，投稿前请以原文核实。
21. Dalal et al. *Action Anticipation from SoccerNet Football Video Broadcasts (FAANTRA).* CVPRW 2025. https://arxiv.org/abs/2504.12021
22. Wu et al. *Badminton expertise shapes cue extraction and evidence accumulation during perceptual prediction.* Front Psychol 2026. https://doi.org/10.3389/fpsyg.2026.1828824
23. Müller et al. *Accelerating Visual Anticipation in Sport Through Temporal Occlusion Training: A Meta-Analysis.* Sports Medicine 54(10):2597, 2024. https://doi.org/10.1007/s40279-024-02073-6
24. Savelsbergh, Williams, van der Kamp & Ward. *Visual search, anticipation and expertise in soccer goalkeepers.* J Sports Sci 20(3):279, 2002. https://www.bisp-surf.de/Record/PU200911005886
25. **anytime prediction 先例**（概念差异见空位 5）：Ghodrati et al. *FrameExit.* CVPR 2021. https://arxiv.org/abs/2104.13400 ｜ Wu et al. *AdaFrame.* CVPR 2019. https://ar5iv.labs.arxiv.org/html/1811.12432

---

## 附录：修订记录

| 日期 | 变更 |
|---|---|
| 2026-09-30 | 创建。基于 arXiv/Crossref/PubMed/OpenAlex 逐条核实的文献检索；确定四个空位与建议论文形态 |
| 2026-09-30 | 补充**空位 5（提前量条件化）**：确认体育领域无先例、但有 Ke et al. CVPR 2019 方法论先例，附差异对照表与 5 项必须新增的论证；贡献表与实验表（E7–E11）同步更新 |
