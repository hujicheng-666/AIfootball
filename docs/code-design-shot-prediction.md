# 射门方向预测 · 代码设计（差异化版本）

> **状态**：设计稿，未实现
> **前提**：按**人工采集的理想情况**设计——每个射手有 300+ 次高质量踢球数据，标注自动生成。
> **关联**：[可行性分析](feasibility-leg-pose-penalty-direction.md) · [个体化训练闭环设计](design-individual-training-loop.md) · [学术定位与投稿策略](academic-positioning.md)

---

## 0. 设计原则：本设计与常规 pipeline 的本质差异

常规做法是"**姿态分类器 → 用分类结果控制门将**"。本设计在四个地方与它本质不同，每个差异都有明确的工程理由：

### 差异 1：预测的是分布，不是标签

常规：`argmax` 出"左边"→ 门将扑左边。
本设计：输出**球门平面上的概率分布** `P(cell | pose)`。

**理由**：门将需要的不是"哪个方向"，而是"每个位置有多可能"，因为它还要结合自身的可达范围做取舍。把 argmax 交给决策层是信息损失——**分布包含不确定性，标签不包含**。

### 差异 2：提前量是模型的条件输入，不是固定窗口

常规：切一个固定窗口（如触球前 200ms）训练一个模型。
本设计：**Δt 作为条件变量注入模型**，一个模型回答"如果在触球前 Δt 决策，落点分布是什么"。

**理由**：不同门将的反应时间不同（`GetDiveReactionTime` 定义了 0.08–0.20s 的差异），它们能消费的提前量天然不同。训练 K 个模型既浪费又难维护；**条件化让一条曲线变成一个模型**。这也是训练时的免费增强：同一条序列可以随机采 Δt 生成多个训练例。

### 差异 3：决策层输出的是策略，不是方向

常规：预测 → `argmax` → 直接作为扑救目标。
本设计：在 **(何时启动 τ, 初始方向 g, 修正能力保留 c)** 的策略空间上**最小化期望失球**，约束是门将当前位置、反应时间、移动速度与触及范围。

**理由**：这是整个设计里最关键的转变，有两层：

1. **预测 ≠ 决策**。预测提供信念，决策在物理约束下求最优。预测错了，决策仍可能合理（预测偏左但门将本来就在左，不必大动）。
2. **一次性选点是次优的**。[MOS (arXiv:2609.23976)](https://arxiv.org/abs/2609.23976) 证明"早启动 + 保持闭环 + 被假动作揭穿后修正"优于"先锁定方向"，并指出 *confidence alone does not capture the motion opportunity lost by waiting*——等待会损失运动机会，所以"何时"必须与"何处"联合求解。

副产品是——**"不确定就守中路"会自动涌现**：当后验接近均匀时，中路因左右都近而使可达集最大，最优解自然落在那里。这比写一条 `if confidence < 0.3 then stay center` 优雅且正确得多。

> **注意 Bransen et al. 2025 的动作空间排除了"早扑中路"**（原文脚注 3），所以这个行为在他们的框架里**根本不是可选项**——这本身就是一条有力的论证。

### 差异 4：门将能力与视觉预测在数学上耦合

常规：视觉模型和门将参数互不相干。
本设计：门将的反应时间**反推出它能用的提前量**，进而决定它消费分布族里的哪一个：

```
Δt_keeper = t_flight − reaction_time − move_time(target)
```

**理由**：这解决了系统里一个真实矛盾——快门将能用更晚（更准）的预测，慢门将只能用更早（更糊）的预测。这是**物理约束下的自适应**，也是原本割裂的"视觉"与"仿真"两块真正的接口。

---

## 1. 模块结构

复用现有 `src/AIfootball.Engine/aifootball/` 的包布局，新增以下子包：

```
aifootball/
├── pose/                        # 【新】姿态感知
│   ├── roi_tracker.py           #   踢球者 ROI 跟踪（避免 3264×1440 全帧推理）
│   ├── keypoint_extractor.py    #   YOLO11n-pose 封装 + 时序平滑
│   └── body_frame.py            # ★ 身体坐标系归一化 + 物理量特征
├── kinematics/
│   ├── phase.py                 # ★ 动作相位对齐（替代固定时间截断）
│   └── features.py              #   坐标 / 速度 / 角度 / 摆腿平面
├── dataset/
│   ├── shooter_profile.py       #   射手档案（数据集与模型都挂在这里）
│   ├── auto_labeler.py          # ★ 自动标注 + 标签不确定性
│   └── quality_gate.py          #   质检门（四道）
├── synth/                       #   合成数据（预训练基座用）
│   ├── deform.py                #   骨骼参数化变形
│   └── domain_rand.py           #   域随机化
├── model/
│   ├── dt_conditioned.py        # ★ Δt 条件化时序模型
│   ├── calibration.py           #   个体温度标定
│   └── registry.py              #   基座 / 射手 两级版本管理
├── train/
│   ├── pipeline.py              #   统一训练管线（本地 / 云端规模参数化）
│   └── losses.py                # ★ 标签不确定性加权损失
├── predict/
│   ├── predictor.py             #   预测服务（加载模型 → 分布）
│   ├── fusion.py                # ★ 贝叶斯融合（姿态先验 ⊗ 球观测）
│   └── decision.py              # ★ 风险敏感起扑规划
└── capture/
    ├── pre_roll.py              #   分段录制 + 预滚动（方案 A）
    └── time_sync.py             #   FrameClock 接线 + 事件对齐
```

Unity 侧：

```
Myproject/Assets/PenaltyKick/Scripts/Prediction/
├── ShotPrediction.cs            # 预测数据结构（含网格分布）
├── IShotPredictor.cs            # 预测接口
├── KeeperReactionBudget.cs      # ★ 可用提前量反推
└── PredictionInjector.cs        # 注入门将（替代 BallHitTarget 真值）
```

---

## 2. 核心数据契约

```python
# aifootball/predict/types.py
from dataclasses import dataclass
from typing import Literal
import numpy as np

# ── 姿态 ──────────────────────────────────────────────────────────
@dataclass(frozen=True)
class PoseSequence:
    """一段触球前的骨骼序列。关节顺序固定为 H3WB/COCO 17 点。"""
    xyz: np.ndarray            # (T, J, 3) 米，身体坐标系
    confidence: np.ndarray     # (T, J) 关键点置信度
    t_rel: np.ndarray          # (T,) 相对触球的秒数，全部 <= 0
    fps: float

    @property
    def n_frames(self) -> int: ...

# ── 标签（带不确定性，这是差异点之一）────────────────────────────
@dataclass(frozen=True)
class ShotLabel:
    grid_cell: int             # 球门平面网格索引
    confidence: float          # 0-1，由球的三角化精度与 ray_gap 决定
    source: Literal["auto", "manual"]
    lateral_m: float           # 原始落点横向（米，门将视角）
    height_m: float

# ── 预测输出 ──────────────────────────────────────────────────────
@dataclass(frozen=True)
class GoalPlaneDistribution:
    """球门平面上的离散概率分布。这是本设计的核心数据契约。"""
    probs: np.ndarray          # (N_x, N_y) 归一化概率
    edges_x: np.ndarray        # 横向分格边界（米，门将视角）
    edges_y: np.ndarray        # 高度分格边界（米）
    lead_time_ms: float        # 该分布对应的提前量

    @property
    def entropy(self) -> float: ...          # 不确定性
    @property
    def expected_lateral(self) -> float: ... # 期望横向落点
    @property
    def confidence(self) -> float:           # 1 - 归一化熵
        ...
```

**为什么标签要带 `confidence`**：落点标签是从双目重建的球轨迹推出来的，重建精度本身有高低（`ray_gap` 大、重投影误差大的样本，落点就不可信）。把这个不确定性**传播进损失函数**，比假装所有标签都同等可靠要诚实，效果也更好。

---

## 3. 关键模块设计

### 3.1 身体坐标系与物理量特征

**常规做法**：把 17 个关节的坐标直接展平喂网络。
**问题**：不同身高、不同相机距离、不同站位都会分布漂移，模型要浪费容量去学这些无关变化。

```python
# aifootball/pose/body_frame.py

# 身体坐标系：以骨盆中心为原点
#   x̂ = 骨盆→肩中心 的投影（躯干轴，竖直为主）
#   ŷ = 左髋→右髋（髋轴）
#   ẑ = x̂ × ŷ（身体前后轴）
# 所有关节坐标除以"骨骼尺度"（髋宽 + 肩宽 + 大腿长 的中位数）做无量纲化

def to_body_frame(xyz: np.ndarray, conf: np.ndarray) -> np.ndarray:
    """(T,J,3) -> (T,J,3)，平移+旋转+缩放全部消掉。"""
    ...

def physical_features(xyz_bf: np.ndarray) -> dict[str, np.ndarray]:
    """
    从归一化骨骼导出的物理量。这些是文献里被反复验证的线索，
    让模型不必从零学会"什么角度重要"。
    """
    return {
        "hip_axis_yaw":   ...,   # 髋轴相对出球方向的偏角
        "swing_plane_n":  ...,   # ★ 踢球腿摆动的平面法向量
        "support_offset": ...,   # ★ 支撑脚相对骨盆的横向偏移（无脚关节，用踝代理）
        "knee_flex_swing": ...,  # 踢球腿膝角时间序列
        "knee_flex_supp":  ...,  # 支撑腿膝角时间序列
        "trunk_lean":     ...,   # 躯干前倾
        "ankle_vel_ratio": ...,  # ★ 两踝速度比（判定哪条腿在踢）
    }
```

**这里的差异**：`swing_plane_n`（摆腿平面法向量）和 `support_offset` 是**有生物力学含义的量**，不是黑箱特征。文献表明支撑脚指向与髋部朝向是两类最强线索；把它们显式构造出来，等于给模型装了先验。

> ⚠️ **数据限制提醒**：3DSP 只有 `Ankle` 没有脚部关节，"支撑脚指向"只能用踝关节位置代理。自采数据时**建议标注脚部关键点**（RTMPose 等模型支持），这是采集协议要专门确认的一点。

### 3.2 动作相位对齐

**常规做法**：固定时间窗（触球前 200ms）。
**问题**：不同球员助跑节奏差异很大，同一时间窗对应的动作阶段完全不同——有人在支撑，有人已摆腿。模型看到的是错位的动作。

```python
# aifootball/kinematics/phase.py

def estimate_phase(pose: PoseSequence) -> np.ndarray:
    """
    为每帧估计动作相位 φ ∈ [0,1]：
      0.0 = 支撑脚落地
      0.5 = 摆腿加速开始
      1.0 = 触球
    用踢球腿膝关节角速度的累积归一化来估计（角速度对相位单调）。
    """
    ...

def resample_by_phase(pose: PoseSequence, n_phase_bins: int = 24) -> PoseSequence:
    """
    按相位重采样到固定格数，而不是按时间。
    这样不同节奏的球员在同一"动作阶段"上对齐。
    """
    ...
```

**这里的差异**：相位对齐让模型学"动作状态"而不是"经过了多久"，是跨球员泛化的关键——**这正是文献里"跨被试泛化差"的一个可能成因**（时间对齐掩盖了动作错位）。

### 3.3 ★ Δt 条件化时序模型

这是本设计的核心创新。目标：**一个模型生成整条"准确率-提前量"曲线**。

```python
# aifootball/model/dt_conditioned.py
import torch, torch.nn as nn

class FiLM(nn.Module):
    """用 Δt 生成逐通道的缩放/偏移，注入到时序特征中。"""
    def __init__(self, cond_dim: int, feat_dim: int):
        super().__init__()
        self.mlp = nn.Sequential(
            nn.Linear(cond_dim, feat_dim), nn.GELU(),
            nn.Linear(feat_dim, feat_dim * 2),          # gamma + beta
        )

    def forward(self, h: torch.Tensor, dt_norm: torch.Tensor) -> torch.Tensor:
        gamma, beta = self.mlp(dt_norm).chunk(2, dim=-1)
        return gamma.unsqueeze(1) * h + beta.unsqueeze(1)


class DtConditionedPredictor(nn.Module):
    """
    输入：触球前的骨骼序列 + 允许使用的提前量 Δt
    输出：球门平面概率分布

    ★ 关键设计：同一段序列，mask 掉 t > t_kick − Δt 的帧，就能回答任意 Δt。
      因此一个模型覆盖整条提前量曲线，而不是训练 K 个模型。
    """
    def __init__(self, n_joints=17, n_feat=8, hidden=192, n_cells=9,
                 dilations=(1, 2, 4, 8)):
        super().__init__()
        # 时序编码器：膨胀 TCN，感受野覆盖 ~0.6s@60fps，参数量 < 1M
        self.inp = nn.Conv1d(n_joints * n_feat, hidden, 1)
        self.blocks = nn.ModuleList(
            [TCNBlock(hidden, dilation=d) for d in dilations])
        self.film = FiLM(cond_dim=2, feat_dim=hidden)
        self.head = nn.Sequential(
            nn.LayerNorm(hidden), nn.Linear(hidden, hidden), nn.GELU(),
            nn.Linear(hidden, n_cells),
        )

    def forward(self, feats, mask, dt_norm):
        """feats:(B,T,J*F) mask:(B,T) 1=有效 dt_norm:(B,1)"""
        x = self.inp(feats.transpose(1, 2))               # (B,H,T)
        for blk in self.blocks:
            x = blk(x, mask)
        h = masked_mean(x, mask)                          # 时序聚合
        h = self.film(h, dt_norm)                         # ★ Δt 调制
        return self.head(h)                               # (B, n_cells) logits

    def make_mask(self, t_rel: torch.Tensor, dt_sec: float) -> torch.Tensor:
        """★ 核心：Δt 决定哪些帧可见。t_rel 为相对触球的负秒数。"""
        return (t_rel >= -dt_sec).float()
```

**训练时的免费增强**：每读一条样本，随机采一个 `dt_sec ~ U(0, 0.5)`，生成一个训练例。300 次采集 ≈ 数千个有效训练例，且天然覆盖整条曲线。

**推理时的用法**：

```python
# 门将反应慢 → 必须早决策 → 取 Δt=400ms 的分布（更糊但更早）
dist_slow = model.predict(pose, dt_sec=0.40)
# 门将反应快 → 可以晚决策 → 取 Δt=150ms 的分布（更准）
dist_fast = model.predict(pose, dt_sec=0.15)
```

### 3.4 ★ 标签不确定性加权损失

```python
# aifootball/train/losses.py

def uncertainty_weighted_ce(logits, labels: ShotLabel, n_cells: int):
    """
    标准交叉熵把每个标签当同等可信，但自动标注的标签可信度差异很大：
    球的三角化 ray_gap 大、重投影误差大 → 落点本身就可能是错的。

    做法：按标签置信度加权 + 对低置信样本做标签平滑（软化目标）。
    """
    w = labels.confidence.clamp(min=0.2)                  # 不彻底丢弃，只降权
    soft = one_hot(labels.grid_cell, n_cells)
    soft = soft * w[:, None] + (1 - w[:, None]) / n_cells # 置信度越低越接近均匀
    loss = -(soft * log_softmax(logits, -1)).sum(-1)
    return loss.mean()
```

### 3.5 ★ 贝叶斯融合

真实守门员的决策是两段式：**触球前用身体线索形成先验，触球后用球的早期飞行修正**。现有双目已经能在触球后极短时间内给出球的方向，这段信息不利用是浪费。

```python
# aifootball/predict/fusion.py

class BayesianFusion:
    """
    prior:   来自姿态模型  P(cell | pose, Δt)
    likelihood: 来自球的早期观测 P(ball_obs | cell)
    后验 = 归一化(prior × likelihood)
    """
    def update(self, prior: GoalPlaneDistribution,
               obs: BallEarlyObservation) -> GoalPlaneDistribution:
        # 观测模型：由球的前 2–3 帧位移反推落点，带各向异性噪声
        like = self.observation_likelihood(obs)      # (N_x, N_y)
        post = prior.probs * like
        post /= post.sum()
        return replace(prior, probs=post)

    def observation_likelihood(self, obs) -> np.ndarray:
        """
        用球的初速度矢量 + 重力外推落点，误差随观测帧数减少而增大。
        观测帧数 n 越多，似然越尖锐（信息越多）。
        """
        ...
```

**信息价值**：这条路径让"早期不确定 → 后期修正"成为系统的自然行为，而不是靠调参硬凑。

### 3.6 ★ 风险敏感起扑规划

```python
# aifootball/predict/decision.py

@dataclass(frozen=True)
class KeeperState:
    position_lateral: float      # 当前横向站位（米，门将视角）
    reaction_time_s: float       # 来自 GetDiveReactionTime（现在是死代码）
    dive_speed: float            # 米/秒
    reach: float                 # 触及半径（米）
    jump_distance: float         # 最大水平移动（米）

@dataclass(frozen=True)
class DivePolicy:
    """
    ★ 输出的是【策略】，不是单点方向。

    依据 MOS (arXiv:2609.23976)："The stopping decision selects when to act,
    not which target to follow" —— 把动作压缩成一次性选点，会丢掉
    "启动后修正"的自由度；而"早启动 + 保持闭环 + 被假动作揭穿后修正"
    优于"先锁定方向"。因此本设计的动作空间是三维的：

        (何时启动 τ, 初始方向 g, 修正能力保留 c)
    """
    commit_time_s: float          # τ：何时启动（越早信息越少，但可覆盖范围越大）
    initial_lateral: float        # g：初始扑向的横向位置
    initial_height: float
    correction_reserve: float     # c ∈ [0,1]：0=完全承诺，1=最大保留修正能力
    expected_concede: float       # 该策略的期望失球（可解释性）
    reason: str                   # "confident_commit" / "uncertain_center_hold" / "late_read"

class RiskSensitiveDivePlanner:
    """
    ★ 在【策略空间】上最小化期望失球，而不是在候选点里挑一个。

        min_{τ,g,c}  Σ_cell  P_τ(cell) · [ cell ∉ A(g, τ, c) ]

    三个关键耦合——这是与既有工作的分界线：

      1. **P_τ 随 τ 变化**：越晚启动，后验越尖锐（观测越多）。
         这复现了"早扑 = 范围大但信息少 / 晚扑 = 信息多但范围小"的真实权衡，
         与 Battaglia & Schrater (2007, J Neurosci) 的"观察时间 vs 运动时间"
         决策论权衡**结构同构**。
      2. **A 随 τ 收缩**：启动越晚，剩余运动时间越少，可达集越小。
      3. **c 与 A 反向**：保留越多修正能力，初始承诺能覆盖的范围就越小。
         这正是 MOS 指出的 "confidence alone does not capture the motion
         opportunity lost by waiting"。

    ★ 副产品：「不确定就守中路」会自动涌现——
      当 P_τ 接近均匀时，任何 g 的期望失球都趋近 1，而中路因左右都近
      使 A 最大，于是 g* 落在中路。**不需要任何 if 语句或魔法阈值。**
      注：Bransen et al. 2025 的动作空间**排除了"早扑中路"**（原文脚注 3），
      所以这个行为在他们的框架里根本不是可选项——这本身就是一条有力论证。
    """
    def __init__(self, grid: GoalGrid,
                 commit_grid_ms=(200, 250, 300, 350, 400),
                 correction_levels=(0.0, 0.25, 0.5, 0.75, 1.0)): ...

    def plan(self,
             posterior_at: Callable[[float], GoalPlaneDistribution],
             keeper: KeeperState,
             flight_time_s: float) -> DivePolicy:
        """
        posterior_at(τ) 返回"在触球前 τ 秒决策时"可用的落点后验。
        由 §3.3 的 Δt 条件化模型直接给出，**无需重训**——这正是把
        Δt 做成条件输入而非固定窗口的额外回报。
        """
        best = None
        for tau_ms in self.commit_grid_ms:
            posterior = posterior_at(tau_ms / 1000.0)      # 后验随 τ 收缩
            for g in self.candidate_targets():
                for c in self.correction_levels:
                    reachable = self.reachable_mask(g, keeper, tau_ms, c)
                    e = float((posterior.probs * (~reachable)).sum())
                    if best is None or e < best.expected_concede:
                        best = DivePolicy(
                            commit_time_s=tau_ms / 1000.0,
                            initial_lateral=g.lateral,
                            initial_height=g.height,
                            correction_reserve=c,
                            expected_concede=e,
                            reason=self.explain(g, c, tau_ms, posterior),
                        )
        return best

    def reachable_mask(self, g, keeper, commit_ms, correction_reserve) -> np.ndarray:
        """
        在 τ 启动、保留修正能力 c 时，门将能覆盖的区域 A。

        - 剩余运动时间  t = flight_time − τ − reaction_time
        - 有效位移半径  r = min(dive_speed · t, jump_distance) · (1 − λ·c)
          λ 是"保留修正能力"对初始承诺幅度的惩罚系数，需用仿真标定
        - 再叠加 reach 半径
        - 横向与高度的可覆盖范围**分别计算**（跳高能力 ≠ 横移能力）
        """
        ...
```

**为什么这是差异化**：常规实现是 `keeper.diveTo(prediction.argmax())`。本设计的区别有两层：

- **预测与决策分离**：预测提供信念，决策在物理约束下求最优。好处是预测错了决策仍可能合理；置信度被天然利用（均匀分布 → 自动守中）；`expected_concede` 可评估可解释（"这一扑的期望失球是 0.62"）。
- **策略 vs 单点**（相对现有工作的关键分界）：Bransen et al. 2025 在**离散策略集**里枚举（早扑/晚扑 × 3 个方向区），MOS 2026 只解"何时"。本设计在 **(τ, g, c) 连续策略空间**上求期望失球最小，把"何时"与"何处"放在**同一个目标**下联合优化——这是文献检索确认的空白（见 [学术定位](academic-positioning.md) §2 空位 4）。

### 3.7 ★ 门将反应时间 → 提前量反推

这条把"视觉预测"和"门将能力"在数学上耦合起来，是整个系统的关键接口。

```python
# aifootball/predict/predictor.py

class ShotPredictor:
    def predict_for_keeper(self,
                           pose: PoseSequence,
                           keeper: KeeperState,
                           flight_time_s: float) -> GoalPlaneDistribution:
        """
        ★ 门将的能力决定它能消费哪个提前量的预测。

        Δt_available = flight_time − reaction_time − estimated_move_time
          - 反应快的门将 → Δt 小 → 用更晚、更准的预测
          - 反应慢的门将 → Δt 大 → 只能用更早、更糊的预测

        这不是"给模型加个参数"，而是让门将的生理限制
        真正参与视觉决策——两个原本割裂的模块在此处合流。
        """
        move_time = self.estimate_move_time(keeper, flight_time_s)
        dt = clamp(flight_time_s - keeper.reaction_time_s - move_time,
                   self.min_dt, self.max_dt)
        return self.model.infer(pose, dt_sec=dt)
```

---

## 4. 两级模型注册（基座 + 射手）

个体化要求模型可追溯、可回滚、可共存。

```python
# aifootball/model/registry.py

@dataclass(frozen=True)
class BaseModelRef:
    version: str            # 全局预训练基座（合成 + 公开数据）
    trained_at: str
    dataset_digest: str     # 可复现
    config_hash: str

@dataclass(frozen=True)
class ShooterModelRef:
    shooter_id: str
    base: BaseModelRef      # ★ 记录它微调自哪个基座
    adapter_path: str       # 射手私有的微调权重
    calibration_path: str   # ★ 个体温度标定参数（独立于权重）
    n_shots: int            # 训练时用了多少次踢球
    metrics: dict           # 留出集指标（log loss / ECE / 各 Δt 准确率）
```

**设计要点**：

- **微调只存 adapter**（如 LoRA 或仅最后一层），不存整个模型 → 射手模型几百 KB，便于备份与共享；
- **校准参数独立存储** → 换门将/换场景时可以只重标定不重训；
- **`base` 反向引用** → 基座升级后可批量重训所有射手模型；
- **`metrics` 按 Δt 分档记录** → 上线门槛可以按"门将实际能用的 Δt"来判定，而不是看平均值。

---

## 5. Unity 侧接口

```csharp
// Myproject/Assets/PenaltyKick/Scripts/Prediction/ShotPrediction.cs
namespace PenaltyKickPlatform.Prediction
{
    /// <summary>球门平面上的离散概率分布（由 Python 侧离线计算并落盘）。</summary>
    public sealed class ShotPrediction
    {
        public float[] Probs;            // 行优先，N_x * N_y
        public int CellsX, CellsY;
        public float GoalWidth = 7.32f;
        public float GoalHeight = 2.44f;
        public float LeadTimeMs;         // 该分布对应的提前量
        public float Confidence;         // 1 - 归一化熵

        public float ExpectedLateral();  // 期望横向落点
        public float Entropy();
    }

    public interface IShotPredictor
    {
        /// <param name="keeper">门将能力，用于反推可用提前量</param>
        /// <param name="flightTime">球到门线的飞行时间</param>
        ShotPrediction Predict(KeeperReactionBudget keeper, float flightTime);
    }
}
```

```csharp
// Myproject/Assets/PenaltyKick/Scripts/Prediction/KeeperReactionBudget.cs
namespace PenaltyKickPlatform.Prediction
{
    /// <summary>
    /// 把门将的生理参数换算成"它还能用多晚的预测"。
    /// 这解决了现有代码里 GetDiveReactionTime() 是死代码的问题。
    /// </summary>
    public sealed class KeeperReactionBudget
    {
        public float ReactionTime;   // GoalKeeper.GetDiveReactionTime(...)
        public float DiveSpeed;
        public float Reach;
        public float JumpDistance;

        public float AvailableLeadTime(float flightTime, float lateralDistance)
        {
            float moveTime = Mathf.Min(
                lateralDistance / Mathf.Max(DiveSpeed, 0.1f),
                JumpDistance / Mathf.Max(DiveSpeed, 0.1f));
            return Mathf.Max(0f, flightTime - ReactionTime - moveTime);
        }
    }
}
```

**与现有代码的接法**（改动极小，符合先前确认的方案）：

| 现有位置 | 改法 |
|---|---|
| `TendGoalMainState.cs:113-123` | 保留 `BallHitTarget` 真值（仅供评估对照），新增写入门将的 `ShotPrediction` |
| `InterceptShotMainState.cs:76-93` | 起扑目标不再由 `ballAtGoalLocal` 直接决定，改为调用 `RiskSensitiveDivePlanner`（C# 侧轻量重实现或读取 Python 预计算结果） |
| `GoalKeeper.GetDiveReactionTime()` | **正式启用**（当前是死代码），作为 `KeeperReactionBudget.ReactionTime` 的来源 |
| `GameManager.cs:283` / `PenaltyKickApp.cs:398` | 在 `Ball.PlayTrajectory` **之前**载入预测并注入 |

---

## 6. 与现有代码的复用关系

| 现有资产 | 复用方式 |
|---|---|
| `reconstruct_3d_trajectory.py` | 作为**自动标注器**：落点 → 网格标签 + 置信度 |
| `fit_ballistic_trajectory.py` | 提供球的初速度与外推，用于 `observation_likelihood` |
| `camera_capture.py` 的 `FrameClock` | 接线即可提供逐帧时间戳 |
| `deque(maxlen=300)` | 改为分段录制（方案 A），不再用于全分辨率预滚动 |
| `GoalKeeper.GetDiveReactionTime` | **启用**，成为预测选择的关键输入 |
| `InterceptShotMainState` 的 anticipation 插值 | **保留**——它负责"知道方向但身体要过去"，与决策层职责不重叠 |
| `GoalkeeperData`（含 5×3 概率图） | 保留为**门将先验/能力画像**，与模型后验正交 |

---

## 7. 创新点汇总（诚实标注性质）

| # | 设计 | 性质 | 价值 |
|---|---|---|---|
| 1 | **Δt 条件化预测**（FiLM 注入提前量） | 已有技术（FiLM）在新问题上的组合应用 | 一个模型生成整条提前量曲线；训练免费增强 |
| 2 | **风险敏感起扑策略规划**（策略空间而非单点） | 决策论框架 + 联合优化 | 把"何时 × 何处 × 修正能力"统一在一个期望失球目标下；"不确定守中路"从第一性原理涌现；**文献检索确认的空白** |
| 3 | **门将反应时间 → 提前量反推** | 本系统的原创耦合 | 让生理限制真正参与视觉决策；启用现有死代码 |
| 4 | **标签不确定性加权** | 标准技术（噪声标签学习）的针对性应用 | 诚实对待重建精度差异，避免被坏标签带偏 |
| 5 | **贝叶斯融合姿态先验与球观测** | 标准融合框架 | 复现真实守门员的两段式决策 |
| 6 | **动作相位对齐替代时间截断** | 生物力学思路的引入 | 直接针对"跨被试泛化差"这个文献痛点 |
| 7 | **物理量特征显式构造**（摆腿平面法向量等） | 特征工程 | 把文献验证过的线索作为先验注入 |
| 8 | **基座 + adapter + 独立校准 三级版本** | 工程架构 | 射手模型几百 KB；基座升级可批量重训 |

> **诚实说明**：#1、#4、#5 使用的是成熟的公开技术（FiLM、噪声标签学习、贝叶斯融合），本设计的贡献在于**把它们组合到"点球方向预测 + 门将决策"这个具体问题上，并让各层职责清晰分离**。#2、#3、#6 相对本项目的现有实现是真正的结构性改变。

---

## 8. 实施顺序（依赖关系）

```
① body_frame + phase            ── 表示层，其他一切的基础
        │
② auto_labeler + quality_gate   ── 有数据就能跑，与 ① 并行
        │
③ dt_conditioned + losses       ── 需要 ①②
        │
④ calibration + registry        ── 需要 ③
        │
⑤ fusion + decision             ── 需要 ③（可与 ④ 并行）
        │
⑥ Unity 集成（含反应时间反推）  ── 需要 ④⑤
        │
⑦ synth（预训练基座）           ── 可与 ①–⑥ 并行，服务于冷启动
```

**最小可验证闭环**（不与数据采集冲突）：① → ② → ③ → ⑤ → ⑥，用任意一批哪怕少量真实数据即可端到端跑通。

---

## 附录：修订记录

| 日期 | 变更 |
|---|---|
| 2026-09-30 | 创建；确定差异化设计（Δt 条件化、风险规划、反应时间耦合、不确定性加权） |
| 2026-09-30 | **决策层修正**：动作空间由"单点方向"改为"(何时启动 τ, 初始方向 g, 修正能力保留 c) 策略"——据 MOS (arXiv:2609.23976) 的结论；同步更新 §0 差异 3、§3.6 与 §7 创新点表 |
