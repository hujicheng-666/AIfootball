# 射手（Shooter）搭建与使用说明

> **目标**：点击"播放"后，射手从球后方助跑接近、摆腿，**在触球那一刻球才飞出**。
> **动作来源**：3DSP 数据集中**真实职业球员**的射门骨骼（Apache 2.0），不是通用动画。

---

## 1. 快速开始（三步）

### 第 1 步：导出姿态数据

```powershell
python scripts\export_shooter_pose.py --shot 00001 00002 00005 00010
```

产物：`Myproject/Assets/PenaltyKick/Resources/ShooterPoses/<shot>.json`

> 只有 `data/external/3dsp/` 存在时才有效（见 `docs/academic-positioning.md` 附录）。
> 需要更多 shot：`--shot 00003 00007 ...`，数据全部在 `3dsp/train/` 下（共 200 个）。

### 第 2 步：在 Unity 里一键搭建

打开场景 → 菜单 **AIfootball → 搭建射手（Shooter）**

脚本会自动完成：
- 实例化 `Player001` 作为射手，站在球后方 0.62 m、面朝球门
- 清空 Animator 的 Controller（**关键**：否则会覆盖脚本驱动的姿态）
- 禁用全部 Collider（避免干扰球的物理）
- 生成并换上 `ShooterKit`（红色球衣）+ 把手套换成皮肤材质（与门将区分）
- 挂载并连好 `ShooterRetargeter` / `ShooterDriver` / `ShooterPlaybackBinder`

### 第 3 步：校验并播放

菜单 **AIfootball → 校验射手配置**，应全部显示 ✓。然后点播放。

预期表现：静立 0.3 s → 助跑接近 0.95 s → 摆腿 → **触球瞬间球按 CSV 轨迹飞出**，同时门将起扑。

---

## 2. 关键参数（ShooterDriver）

| 参数 | 默认 | 说明 |
|---|---|---|
| `Pose Resource Path` | `ShooterPoses/00001` | 换用别的射门动作 |
| `Facing Target` | 球门 | 射手始终面朝它 |
| `Ball` | 场景中的球 | 用于推算站位 |
| `Kick Frame Override` | −1 | ≥0 时强制指定触球帧；−1 用数据自带值 |
| `Use Kinematic Kick Frame` | false | true 则改用**踝速度峰值**帧（通常比标注早 2–4 帧） |
| `Hold Before Run Up` | 0.30 s | 助跑前静立时长（设为 0 可立即起跑） |
| `Run Up Duration` | 0.95 s | 助跑时长 |
| `Stand Offset` | 0.62 m | 踢球站位到球的距离 |
| `Run Up Distance` | 0.85 m | 助跑起点相对站位的后退距离 |
| `Enable Intro`（Binder） | true | 关掉则球立即飞出，行为回到改动前 |

### 关于触球帧的两个取值

数据里带了两个触球帧，差 2–4 帧，各有依据：

- **`kickFrame`（默认）**：SoccerNet 人工动作标注（所有样本恒为 `start+500 ms`）。
- **`kickFrameKinematic`**：踝关节速度峰值，通常更早——物理上摆腿在触球前达到最大角速度。

**如果看球"出脚比脚早/晚"，就切这个开关**，或直接用 `Kick Frame Override` 微调。

---

## 3. 原理

### 重定向：只用方向，不用长度

3DSP 的骨骼段长度（大腿 0.33 m）明显小于角色模型（约 0.42 m）。但重定向**只用「起点关节 → 末端关节」的方向**：

```
目标朝向 = normalize(endJoint - startJoint)      // 与长度无关
delta    = Quaternion.FromToRotation(当前朝向, 目标朝向)
bone.rotation = delta * bone.rotation            // 增量修正，无需 rest pose 校准
```

因此**骨骼长度差异自动抵消**，源数据与角色模型可以任意不匹配。

骨骼链**必须按「父先子后」**处理（Hips → Spine → Chest → …），否则父级旋转会破坏子级已对齐的朝向。这一约束写在 `ShooterRetargeter.Chain` 的注释里。

根骨骼（Hips）需要完整旋转而非单一朝向，用髋轴 + 脊柱方向构造：

```
up = 脊柱方向,  left = 右髋→左髋,  forward = Cross(up, left)
```

（Unity 是左手系，`Cross(up, left)` 的朝向已按 T-pose 验证。）

### 时序：触球事件才放球

```
t=0                                  Hold
t=hold                               RunUp 开始（位置 SmoothStep 插值）
t=hold+runUp                         Strike 开始（数据段 t=0）
t=hold+runUp+kickTime                **Kick 事件 → 球飞出**
t=hold+runUp+duration                结束
```

**这段前奏不计入轨迹时间轴**——球仍然在 `t=0` 被 `PlayTrajectory` 驱动，所以
`t=0 ≡ 触球` 的语义、门将逻辑与评估口径**完全不受影响**。

### 解耦：球发射闸门

```
GameManager / PenaltyKickApp  →  ShotPlaybackGate.Request(() => ball.PlayTrajectory(...))
                                            ↓ （有射手则被接管）
                                   ShooterPlaybackBinder → ShooterDriver.Play()
                                            ↓ （触球时）
                                         playBall()  ← 球真正飞出
```

SuperGoalie 层**不认识射手**，依赖方向保持正确（PenaltyKick → SuperGoalie）。
**没有射手时 `Request` 直接调用 `playBall`，行为与改动前完全一致。**

---

## 4. 故障排查

| 现象 | 原因与处理 |
|---|---|
| 球一直不飞出 | 触球事件没到达。Binder 有 6 s 超时兜底；同时查 Console 是否有"等待触球超时"。也可在 Inspector 上取消 `Enable Intro` 应急 |
| 射手 T-pose 或不动 | ① Animator 的 Controller 没清空；② `Pose Resource Path` 不存在；③ Avatar 不是 Humanoid（跑"校验射手配置"看） |
| 姿态扭曲 / 肢体乱转 | 模型 Avatar 的骨骼映射不全。校验菜单会列出**缺失的具体骨骼名** |
| 球比脚早出 | 把 `Use Kinematic Kick Frame` 打开，或用 `Kick Frame Override` 减 1–2 帧 |
| 助跑时看起来像"滑行" | 助跑段用的是数据首帧姿态（3DSP 不含助跑）。可缩短 `Run Up Duration`/`Run Up Distance`，或见下节 |
| 射手位置不对 | 检查 `Ball` 与 `Facing Target` 是否连上；或手动调 `Stand Offset` |
| 点了停止但球还是飞出去 | 不应发生——`ResetAll()` / `StopAndResetTrajectory()` 会调用 `ShotPlaybackGate.Cancel()` 丢弃待发射的球。若仍出现，检查是否有别的入口绕过了闸门直接调用 `Ball.PlayTrajectory` |

---

## 5. 已知限制与可增强项

1. **3DSP 不含助跑**（只有触球前后共 0.6 s）。当前助跑是程序化位移 + 首帧姿态。
   要更真实可做：程序化跑步循环（对两大腿施加正弦摆动）叠加在重定向结果上。
2. **17 关节信息不全**：缺脚趾、脊柱细分、手指。脚部方向用踝关节外推，手指保持 rest。
3. **根运动缺失**：3DSP 是以髋为原点的局部坐标，所以位移只能程序化生成。
4. **每帧只有 20 帧**（31.67 fps，约 0.63 s）。已做 3 帧时间平滑抑制抖动；
   若嫌不够平滑，可对 `SampleJoint` 改用 Catmull-Rom 插值。
5. **只用了一个 shot 的动作**。理想情况下应让射手的动作与**该样本的球轨迹**同源
   （数据里每个 shot 都有对应姿态），这一步等播放流程能传递样本名后再接。

---

## 6. 相关文件

| 文件 | 作用 |
|---|---|
| `scripts/export_shooter_pose.py` | 3DSP → Unity JSON（坐标系转换 + 触球帧推断） |
| `Assets/PenaltyKick/Scripts/Shooter/ShooterPoseClip.cs` | 姿态数据容器 + 按时间插值 |
| `Assets/PenaltyKick/Scripts/Shooter/ShooterRetargeter.cs` | **核心**：Humanoid 重定向 |
| `Assets/PenaltyKick/Scripts/Shooter/ShooterDriver.cs` | 时序：助跑 + 触球段 + Kick 事件 |
| `Assets/PenaltyKick/Scripts/Shooter/ShooterPlaybackBinder.cs` | 接管球发射闸门 |
| `Assets/PenaltyKick/Scripts/Shooter/Editor/ShooterSetup.cs` | 一键搭建 + 配置校验 |
| `Assets/SuperGoalie/Scripts/Managers/ShotPlaybackGate.cs` | 球发射闸门（无外部依赖的中转） |
