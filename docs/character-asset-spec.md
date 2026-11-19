# 角色资产规范（Character Creator 5 路线）

> 适用：射手脚色 `Shooter`、守门员 `Entities/GoalKeeper002`
> 目标：用 CC5 产出射手与门将模型，骨骼与蒙皮原生适配 Unity Humanoid，接入现有
> 姿态重定向链路（`ShooterRetargeter`）与 Animator 链路（`GoalKeeperAnimator001`）**零代码改动**。

---

## 1. 为什么换：现有混元模型的实测差距

对 `Assets/SuperGoalie/Meshes/Characters/Hunyuan/` 下的 OBJ 做包围盒与顶点统计：

| 指标 | 射手 | 门将 | 标准值 | 判断 |
|---|---|---|---|---|
| 顶点数 | 749,992 | 750,002 | ≤ 20,000 | **超 37 倍**，核显无法承受 |
| 臂展 / 身高 | 0.941 | **0.881** | 1.00 | 门将手臂短 12% |
| 身高（模型单位） | 1.078 | 1.060 | 由场景定 | 需缩放 |
| 骨骼来源 | 自研启发式补骨架（`HunyuanRigger` / `HunyuanRigger2`） | 同左 | 工业级 | 权重靠距离衰减 + Laplacian 平滑，极限动作会失真 |

`HunyuanRigger2.cs` 头部注释已经记录了这个根因：

> 「这两具模型臂展/身高只有 0.88，套标准比例会系统偏移」

也就是说，之前"腿短 22%"的观感，本质是 **AI 生成的网格本身比例失真**，再叠加自研骨架的关节位置估算误差。
换 CC 的核心收益是**比例可控 + AccuRIG 工业级蒙皮**，不是"模型更好看"。

---

## 2. 硬性约束（由代码决定，不可协商）

`ShooterRetargeter.cs` 与 `GoalKeeper.cs` 全部通过 `Animator.GetBoneTransform(HumanBodyBones.XXX)`
取骨骼，**没有任何写死的骨名**。因此只要 Avatar 映射正确，两个脚本都能直接跑。

### 2.1 必须存在且映射正确的 19 根骨骼

```
Hips  Spine  Chest  Neck  Head
LeftShoulder   LeftUpperArm   LeftLowerArm   LeftHand
RightShoulder  RightUpperArm  RightLowerArm  RightHand
LeftUpperLeg   LeftLowerLeg   LeftFoot
RightUpperLeg  RightLowerLeg  RightFoot
```

依据：
- `ShooterRetargeter.Chain`（12 条重定向链 + 肩线扭转参考）
- `GoalKeeper` 的 11 个碰撞胶囊体（`BoneCapsule`）

**手指骨、脚趾骨、眼球骨非必需**，可以保留但不参与驱动。

### 2.2 Avatar 必须 `isValid == true` 且无缺失槽位

导入后 Unity 控制台不应出现：

```
[ShooterRetargeter] 有 N 根骨骼在 Humanoid Avatar 中缺失
[ShooterRetargeter] 有 N 根骨骼的 rest 基退化
```

### 2.3 必须是 T-Pose（或标准 A-Pose）

`CaptureRest()` 在首次 `ApplyPose` 前读取一次 rest 姿态，用来构造每根骨的基向量。
如果模型导出时是蹲姿/跑姿，rest 基会错，整套扭转都会歪。

### 2.4 原点在脚底、面朝 +Z

- `ShooterDriver._groundY = transform.position.y`，脚底必须落在该平面上
- `GoalKeeper` 的扑救方向以自身 forward 为基准

---

## 3. 比例基准（从动作数据实测）

对 `Resources/ShooterPoses/SH_Jog.json`（79 帧）逐帧求骨段长度的中位数。
数据为刚性骨架，长度恒定无噪声：

| 骨段 | 长度（数据单位） |
|---|---|
| 大腿 | 0.2512 |
| 小腿 | 0.2589 |
| 下躯干（Hips→Center of Body） | 0.1075 |
| 上躯干（→Center of Shoulder） | 0.2577 |
| 颈 | 0.0131 |
| 头 | 0.0593 |
| 上臂 | 0.2245 |
| 前臂 | 0.1613 |

### 3.1 关键比值（这才是 CC 里要调的）

| 比值 | 数据源 | 标准成人 | 结论 |
|---|---|---|---|
| 腿长 / 全高 | **0.538** | 0.52–0.55 | 标准，**CC 默认已接近** |
| 大腿 / 小腿 | **0.970** | 0.95–1.05 | 标准，不用调 |
| 上臂 / 前臂 | **1.369** | 1.35–1.40 | 标准，不用调 |
| 腿长 / 躯干+颈+头 | 1.166 | 1.15–1.20 | 标准 |

**结论：数据源就是标准成人体型。CC 默认人体不做任何调整即可对齐，唯需把手脚比例锁死在默认值，不要用夸张的体型 morph。**

> 备注：数据里"肩宽 0.0951"与髋宽 0.1337 相比明显偏窄，判断是关节定义取了肩关节球心
> 而非肩峰外缘。**肩宽不作为验收指标**，以臂展（默认即可）为准。

### 3.2 身高

| 角色 | 目标身高 | 依据 |
|---|---|---|
| 射手 | 1.80 m | `HunyuanRigger.ShooterHeight` |
| 门将 | 1.90 m | `GoalkeeperData.Height` 基准 |

> 若替换后出现脚悬空或陷地，调 `ShooterDriver._rootYOffset`（默认 0），
> **不要改模型 scale**，否则会连带影响 `_standOffset` 的站位计算。

---

## 4. CC5 操作清单

### 4.1 准备

1. 下载 Character Creator 5（30 天免费试用；Standard $299 / Deluxe $479）
2. 装好后先改两处默认值：
   - `Preference > Unit` 设为 **Meter**
   - 面部/身体细分保持默认，**不要开 HD 模式**（HD 是 70K 面，核显带不动）

### 4.2 建角色（每个角色单独建，不要共用工程）

1. `File > New Project`，选 **CC5 Game Base**（10K 面），不选 HD Base
2. `Modify > Body > Proportion`：
   - `Leg Length` **+2%**（对齐 0.538 的腿长比）
   - 其余滑块**保持默认**
3. `Modify > Body > Shape`：按需要调体型，但避免极端值（会破坏 3.1 的比值）
4. 头部/服装：`Content Store` 里选，或用 `Headshot AI` 照片生成
5. **角色命名**：射手 → `Shooter_CC`，门将 → `Keeper_CC`

> 关键：**每个角色导出到独立文件夹**。CC 的 Auto Setup 按文件夹配贴图，
> 两个角色混在一个目录会互相覆盖材质。

### 4.3 绑骨（AccuRIG）

1. 选角色 → `Modify > AccuRIG > Auto Rig`
2. 检查关节落点，重点确认：髋、膝、踝、肩、肘、腕
3. 蒙皮检查：`Modify > Skin Weight`，用 `Pose` 面板做极限姿态拉伸测试
   - 抬腿 90°：胯部不能塌陷
   - 举臂过头：腋下不能撕裂
   - 屈膝 120°：膝窝不能破面
4. 有问题用 `Skin Weight > Smooth` 或手绘权重修

### 4.4 导出

`File > Export > FBX > Clothed Character`，设置如下：

| 选项 | 值 | 原因 |
|---|---|---|
| Target Tool Preset | **Unity 3D** | 会自动带 T-Pose 作为第 0 帧，并输出 `.json` 供 Auto Setup 读取 |
| Embed Textures | **不勾** | Auto Setup 会自动连贴图通道；勾了会让 FBX 体积翻倍且贴图重复 |
| Delete Hidden Faces | **勾** | 删掉衣服下的皮肤，省面数 |
| Motion | **Mesh only** | 动作另做，避免带入多余 clip |
| Include Motion | 不勾 | 同上 |

导出到：`Myproject/Assets/SuperGoalie/Meshes/Characters/CC/<角色名>/`

---

## 5. Unity 侧接入

### 5.1 装 Auto Setup

1. 从 Reallusion 下载 `Character Creator & iClone Auto Setup`
2. 解压得到 `Auto Setup/Unity/` 下按 Unity 版本分的三个目录
3. 选 **2021.3 对应的 unitypackage**，拖进 `Assets/`
4. 会自动生成 `Assets/Editor`（若已存在则合并）与 `Assets/CC_Assets`

### 5.2 导入角色

1. 把导出的整个文件夹（`.fbx` + `.json` + `textures/` + `.fbm/`）拖进 `Assets/CC_Assets/`
2. 等待 Auto Setup 自动执行（会生成 Prefabs / Materials / AnimatorController）
3. `Edit > Project Settings > Player > Color Space` 改为 **Linear**
   （不改的话 CC 的金属/粗糙度会明显发灰）

### 5.3 骨架校验（必须做）

选中 `.fbx` → Inspector → `Rig`：

- `Animation Type` = **Humanoid**
- `Avatar Definition` = `Create From This Model`
- 点 `Configure...` 打开 Avatar 面板，逐项确认第 2.1 节的 19 根骨全部**绿色且无 Required 缺失**

若 `Hips` 被映射成 `Spine`（当前 Rigify 骨架就有这个毛病），说明层级不规范，
需要在 `Configure` 里手动拖正确，或回 CC 检查 AccuRIG 是否多插了一根根骨。

### 5.4 替换场景

场景根对象只有 27 个，角色都是 **PrefabInstance**（`Shooter` 与 `Entities` 两个根下各挂一个），
prefab 内部的 `Character` 子物体不在场景 YAML 里。

1. 打开 `Assets/SuperGoalie/Scenes/Demo.unity`
2. 展开 `Shooter`，把下面挂的旧模型实例**整体禁用**（不要删、不要 Unpack，方便回退）
3. 把 `CC_Assets/Prefabs/Shooter_CC.prefab` 拖成 `Shooter` 的子物体
4. `LocalPosition` = `(0,0,0)`、`LocalRotation` = `identity`、`LocalScale` = `(1,1,1)`
5. 检查 `ShooterDriver` 的 `_animator` 引用指向新模型
6. 门将同理：展开 `Entities`，禁用旧的 `GoalKeeper002` 实例，挂 `Keeper_CC.prefab`
7. `GoalKeeper` 的 `_animator` 引用与 `GoalKeeperAnimator001.controller` 保持一致

> 注意：上一轮修过的 `m_Avatar` override 问题——**不要再给新模型加 avatar override**。
> 用模型自带的 Avatar，否则骨骼错位会重演。

---

## 6. 验收清单

跑一次 `Demo.unity`，逐项确认：

- [ ] 控制台无 `[ShooterRetargeter] 骨骼缺失 / rest 基退化` 警告
- [ ] 射手脚底贴合草皮，无悬空、无陷地（误差 < 2cm）
- [ ] 射手跑动无跳动闪烁（对比修复前的 0.526m 跳变）
- [ ] 射手随挥时小腿不反折（这是模型比例问题，CC 默认比例应已解决）
- [ ] 门将站立时双脚着地，模型无扭曲
- [ ] 门将扑救时肩、胯无撕裂
- [ ] 单角色三角面 ≤ 20,000
- [ ] 帧率不低于替换前的 90%

---

## 7. 已知风险

| 风险 | 表现 | 处置 |
|---|---|---|
| CC5 HD 角色面数过高 | 核显掉帧到个位数 | 只用 Game Base；导出前 `Optimize > Decimate` |
| Auto Setup 对 CC5 支持滞后 | 材质发紫/发白 | 改用 Soupday `cc_unity_tools`（开源，支持 Built-in） |
| Humanoid 自动映射错位 | `Hips` 映射成 `Spine` | 手动 Configure 修正；根因是 AccuRIG 多插根骨 |
| 换模型后动作幅度变化 | 步幅变大/变小 | 比例已在第 3 节锁定；若仍异常，检查 Avatar 的 `Scale` 设置 |
| 门将动画重定向失真 | 扑救姿态变形 | Humanoid clip 可跨模型复用，但骨长差异会改变观感，需逐 clip 目视确认 |

---

## 8. 动作来源建议

模型与动作分开处理：

- **射手**：已有 `SH_Jog.json` 等 JSON 驱动，换模型后重跑同一份数据即是天然回归测试
- **门将**：优先复用现有 `GoalKeeperAnimator001` 的 4 个状态（Humanoid clip 可跨模型重定向）；
  不足的部分从 Mixamo 取 `Goalkeeper Dive` 类动作，或 iClone / ActorCore 补

---

_最后更新：2026-11-19（dev 分支）_
