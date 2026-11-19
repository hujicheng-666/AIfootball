using UnityEngine;

namespace PenaltyKickPlatform.Shooter
{
    /// <summary>
    /// 把 <see cref="ShooterPoseClip"/> 里的真实射门姿态重定向到 Unity Humanoid 角色上。
    ///
    /// 为什么需要「双向量」而不是「单向量」
    /// ------------------------------------
    /// 早先的实现是：<c>delta = FromToRotation(restDir, targetDir)</c>，只约束骨骼**指向**。
    /// 实测每根骨的方向误差都是 0.0°，但观感依然是「肢体乱」——因为一个向量只能定住
    /// 骨头指向哪，定不住它**绕自身轴的扭转（roll）**。膝盖朝哪、肘窝朝哪、脚背朝哪，
    /// 全部由 rest 姿态的残余扭转随机继承，12 根骨各扭一个角度，拼起来就是一团乱麻。
    ///
    /// 本实现为每根骨建立**完整坐标系**：
    ///   forward = 骨向量（关节 A → 关节 B）
    ///   rollRef = 第二参考向量，用来钉死扭转：
    ///       • 四肢：铰链轴 —— cross(近端骨, 远端骨)，也就是膝/肘的弯曲平面法线；
    ///       • 躯干/髋：左右关节连线（Left→Right），即角色的「右」方向。
    /// 两个向量正交化后构成正交基，再用 <c>delta = qTarget * qRest⁻¹</c> 求出世界旋转。
    ///
    /// 为什么用世界旋转而不是父空间增量
    /// --------------------------------
    /// 每根骨的世界朝向都只由「数据」和「自己的 rest 朝向」决定，与父级当前姿态无关，
    /// 因此不会逐级累积误差，也不必关心骨骼的局部轴怎么定义。父级不需要先算好。
    ///
    /// 使用约束：Animator 不应播放任何动画片段，否则姿态会被覆盖。
    /// </summary>
    [DisallowMultipleComponent]
    public class ShooterRetargeter : MonoBehaviour
    {
        /// <summary>扭转参考向量的来源。</summary>
        private enum RollSource
        {
            /// <summary>左右关节连线，用于髋与躯干。</summary>
            JointPair,
            /// <summary>铰链轴 = cross(近端骨方向, 远端骨方向)，用于四肢。</summary>
            Hinge,
        }

        private struct BoneLink
        {
            public HumanBodyBones bone;
            /// <summary>用于确定「骨向量」的参考子骨骼。</summary>
            public HumanBodyBones referenceChild;
            public string startJoint;
            public string endJoint;

            public RollSource rollSource;

            // ── JointPair：模型侧用骨骼，数据侧用关节名 ──
            public HumanBodyBones modelRefLeft;
            public HumanBodyBones modelRefRight;
            public string dataRefLeft;
            public string dataRefRight;

            // ── Hinge：铰链轴由这两条骨的方向叉乘得到 ──
            public bool isLeg;
            public int hingeProximal;
            public int hingeDistal;
        }

        /// <summary>HumanBodyBones 没有 None 值，用它表示「无需参考子骨骼」。</summary>
        private const HumanBodyBones NoBone = (HumanBodyBones)(-1);

        private static BoneLink Pair(HumanBodyBones bone, HumanBodyBones child,
                                     string startJoint, string endJoint,
                                     HumanBodyBones modelLeft, HumanBodyBones modelRight,
                                     string dataLeft, string dataRight)
        {
            var link = new BoneLink
            {
                bone = bone,
                referenceChild = child,
                startJoint = startJoint,
                endJoint = endJoint,
                rollSource = RollSource.JointPair,
                modelRefLeft = modelLeft,
                modelRefRight = modelRight,
                dataRefLeft = dataLeft,
                dataRefRight = dataRight,
                hingeProximal = -1,
                hingeDistal = -1,
            };
            return link;
        }

        private static BoneLink Hinge(HumanBodyBones bone, HumanBodyBones child,
                                      string startJoint, string endJoint,
                                      bool isLeg, int proximal, int distal)
        {
            var link = new BoneLink
            {
                bone = bone,
                referenceChild = child,
                startJoint = startJoint,
                endJoint = endJoint,
                rollSource = RollSource.Hinge,
                modelRefLeft = NoBone,
                modelRefRight = NoBone,
                dataRefLeft = null,
                dataRefRight = null,
                isLeg = isLeg,
                hingeProximal = proximal,
                hingeDistal = distal,
            };
            return link;
        }

        /// <summary>
        /// 重定向链。顺序只影响可读性，不影响结果（每根骨独立求解世界朝向）。
        /// 注意 Unity 对这套 Rigify 骨架的自动映射是：Hips→spine、Spine→spine.001、
        /// Chest→spine.002、Neck→spine.004、Head→spine.005，中间的 spine.003 没有对应
        /// Humanoid 骨骼，因此不参与驱动（由相邻骨补偿，不影响姿态正确性）。
        /// </summary>
        private static readonly BoneLink[] Chain =
        {
            // 髋：骨向量沿脊柱向上，扭转参考取髋轴（左→右）
            Pair(HumanBodyBones.Hips, HumanBodyBones.Spine,
                 "Center of Hips", "Center of Body",
                 HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
                 "Left Hip", "Right Hip"),

            // 躯干：三段都沿脊柱向上，扭转参考取肩线
            Pair(HumanBodyBones.Spine, HumanBodyBones.Chest,
                 "Center of Body", "Center of Shoulder",
                 HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
                 "Left Shoulder", "Right Shoulder"),
            Pair(HumanBodyBones.Chest, HumanBodyBones.Neck,
                 "Center of Shoulder", "Neck",
                 HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
                 "Left Shoulder", "Right Shoulder"),
            Pair(HumanBodyBones.Neck, HumanBodyBones.Head,
                 "Neck", "Head",
                 HumanBodyBones.LeftShoulder, HumanBodyBones.RightShoulder,
                 "Left Shoulder", "Right Shoulder"),

            // 手臂：铰链轴 = cross(上臂, 前臂)，即肘的弯曲平面法线
            Hinge(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
                  "Left Shoulder", "Left Elbow", false, 4, 5),
            Hinge(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
                  "Left Elbow", "Left Wrist", false, 4, 5),
            Hinge(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                  "Right Shoulder", "Right Elbow", false, 6, 7),
            Hinge(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                  "Right Elbow", "Right Wrist", false, 6, 7),

            // 腿：铰链轴 = cross(大腿, 小腿)，即膝的弯曲平面法线
            Hinge(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
                  "Left Hip", "Left Knee", true, 8, 9),
            Hinge(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                  "Left Knee", "Left Ankle", true, 8, 9),
            Hinge(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
                  "Right Hip", "Right Knee", true, 10, 11),
            Hinge(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
                  "Right Knee", "Right Ankle", true, 10, 11),
        };

        [Header("引用")]
        [Tooltip("角色 Animator（必须是 Humanoid，且不要播放任何 clip）")]
        [SerializeField] private Animator _animator;

        [Header("运行状态")]
        [SerializeField] private bool _autoUpdate = true;

        [Header("手臂自然化")]
        [Tooltip("上臂与躯干向下方向的最大夹角（度）。动捕里踢球后甩臂常举到肩以上，" +
                 "瘦长手臂的模型上会像举手投降，超出部分向自然垂臂收回。180 = 不限制。")]
        [SerializeField] private float _armMaxElevation = 62f;
        [Tooltip("收回的柔和度：0 = 硬压到上限，1 = 完全不干预")]
        [SerializeField] private float _armClampSoftness = 0.02f;

        // ── rest 基准（CaptureRestPose 采集）──
        private Transform[] _bones;
        private Quaternion[] _restWorldRotation;
        private Quaternion[] _restBasis;

        // ── 每帧解算缓冲 ──
        private Vector3[] _targetDir;
        private int[] _startJoint;
        private int[] _endJoint;

        // ── 铰链轴的时间连续性：近端/远端骨架在伸直时叉乘会退化成零向量，
        //    这时沿用上一帧的轴，避免 roll 突然翻 180°。──
        private readonly Vector3[] _lastHinge =
        {
            Vector3.zero, Vector3.zero, Vector3.zero, Vector3.zero,
        };

        private ShooterPoseClip _clip;
        private float _clipTime;
        private bool _restCaptured;
        private bool _initialized;
        private bool _hasPose;
        private float _restBlend;

        // ── 阶段切换的姿势过渡 ────────────────────────────────────────
        // Hold(rest) → RunUp(Jog 首帧) → Strike(Kick 首帧) 之间姿态差异很大，
        // 直接换片段会在相邻两帧出现几十度的突变，观感就是"卡顿/闪烁"。
        // 切换前快照当前骨骼朝向，新片段的前 N 秒从快照平滑收敛过去。
        private Quaternion[] _transFrom;
        private float _transDur;
        private float _transLeft;

        public ShooterPoseClip Clip { get { return _clip; } }
        public bool IsReady { get { return _initialized && _clip != null; } }
        public float ClipTime { get { return _clipTime; } }
        public bool HasPose { get { return _hasPose; } }

        private void Reset()
        {
            _animator = GetComponentInChildren<Animator>();
        }

        private void Awake()
        {
            if (_animator == null) _animator = GetComponentInChildren<Animator>();
            // 此时模型应处于 bind pose，是采集 rest 的正确时机
            CaptureRestPose();
        }

        /// <summary>
        /// 采集每根骨在 rest 姿态下的世界朝向与正交基。
        /// 必须在模型处于 bind pose 时调用；若之后模型被摆过姿势，可手动重新调用。
        /// </summary>
        [ContextMenu("重新采集 Rest Pose")]
        public void CaptureRestPose()
        {
            int n = Chain.Length;
            _bones = new Transform[n];
            _restWorldRotation = new Quaternion[n];
            _restBasis = new Quaternion[n];
            _targetDir = new Vector3[n];
            _restCaptured = false;

            int missing = 0, degenerate = 0;
            for (int i = 0; i < Chain.Length; i++)
            {
                Transform bone = _animator != null ? _animator.GetBoneTransform(Chain[i].bone) : null;
                _bones[i] = bone;
                if (bone == null) { missing++; continue; }

                _restWorldRotation[i] = bone.rotation;

                Transform child = Chain[i].referenceChild != NoBone
                    ? _animator.GetBoneTransform(Chain[i].referenceChild)
                    : null;

                Vector3 forward = child != null
                    ? child.position - bone.position
                    : Vector3.zero;
                if (forward.sqrMagnitude < 1e-10f)
                {
                    degenerate++;
                    forward = bone.up;
                }

                Vector3 roll = RestRollHint(Chain[i]);
                if (roll.sqrMagnitude < 1e-8f) degenerate++;

                _restBasis[i] = BuildBasis(forward, roll);
            }

            for (int i = 0; i < _lastHinge.Length; i++) _lastHinge[i] = Vector3.zero;

            if (missing > 0)
                Debug.LogWarning("[ShooterRetargeter] 有 " + missing + " 根骨骼在 Humanoid Avatar 中缺失，将被跳过。" +
                                 "请检查角色模型的 Avatar 配置。", this);
            if (degenerate > 0)
                Debug.LogWarning("[ShooterRetargeter] 有 " + degenerate + " 根骨骼的 rest 基退化" +
                                 "（参考子骨骼缺失或长度为零），这些骨骼的扭转不可靠。", this);

            _restCaptured = true;
        }

        /// <summary>
        /// rest 姿态下用于钉死扭转的第二参考向量。
        ///
        /// 躯干/髋：左右关节连线（指向角色右侧），语义与数据侧完全一致。
        /// 四肢：直接取「角色右轴」。原因是 T/A-pose 下四肢基本伸直，
        /// cross(近端, 远端) 会退化成零向量，算不出铰链轴；而解剖学上
        /// 肘向前弯时 cross(上臂, 前臂) 指向角色左侧、膝向后弯时指向右侧，
        /// 所以手臂取 -right、腿取 +right，与数据侧叉乘结果的符号一致。
        /// </summary>
        private Vector3 RestRollHint(BoneLink link)
        {
            if (link.rollSource == RollSource.JointPair)
            {
                Transform l = _animator.GetBoneTransform(link.modelRefLeft);
                Transform r = _animator.GetBoneTransform(link.modelRefRight);
                if (l == null || r == null) return Vector3.zero;
                return r.position - l.position;   // 指向角色右侧
            }

            return transform.right * (link.isLeg ? 1f : -1f);
        }

        /// <summary>
        /// 用 forward + 一个（不必垂直的）roll 参考构造正交基。
        /// 约定：z = forward，x = roll 在垂直于 forward 平面上的投影，y = cross(z, x)。
        /// </summary>
        private static Quaternion BuildBasis(Vector3 forward, Vector3 rollHint)
        {
            if (forward.sqrMagnitude < 1e-10f) return Quaternion.identity;
            forward.Normalize();

            Vector3 right = rollHint - forward * Vector3.Dot(rollHint, forward);
            if (right.sqrMagnitude < 1e-8f)
            {
                // 参考向量与骨向量共线：任取一个垂直方向，保证基仍然正交
                right = Vector3.Cross(forward, Vector3.up);
                if (right.sqrMagnitude < 1e-8f) right = Vector3.Cross(forward, Vector3.forward);
            }
            right.Normalize();

            Vector3 up = Vector3.Cross(forward, right);
            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>
        /// 按轴分解四元数为 swing × twist（Unity 没有内建）。
        /// twist 是绕 axis 的纯旋转分量，swing 是把 axis 转到 q·axis 的最短弧。
        /// </summary>
        private static void SwingTwist(Quaternion q, Vector3 axis, out Quaternion swing, out Quaternion twist)
        {
            axis.Normalize();
            float proj = q.x * axis.x + q.y * axis.y + q.z * axis.z;
            twist = new Quaternion(axis.x * proj, axis.y * proj, axis.z * proj, q.w);
            float mag = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (mag < 1e-6f)
            {
                twist = Quaternion.identity;
            }
            else
            {
                twist = new Quaternion(twist.x / mag, twist.y / mag, twist.z / mag, twist.w / mag);
            }
            swing = q * Quaternion.Inverse(twist);
        }

        /// <summary>载入姿态片段并解析关节索引。</summary>
        public void SetClip(ShooterPoseClip clip)
        {
            // 同一个片段重复设置时什么都不做。
            // RunUp 阶段每帧都会 SetClip(_runClip)，如果每次都清空 _lastHinge，
            // 「铰链轴沿用上一帧」的兜底就永远是空的，四肢 roll 会逐帧重算并跳变。
            if (_initialized && ReferenceEquals(_clip, clip)) return;

            _clip = clip;
            _initialized = false;
            _hasPose = false;
            _restBlend = 0f;
            for (int i = 0; i < _lastHinge.Length; i++) _lastHinge[i] = Vector3.zero;

            if (clip == null || clip.FrameCount == 0) return;

            int n = Chain.Length;
            _startJoint = new int[n];
            _endJoint = new int[n];
            for (int i = 0; i < n; i++)
            {
                _startJoint[i] = clip.IndexOfJoint(Chain[i].startJoint);
                _endJoint[i] = clip.IndexOfJoint(Chain[i].endJoint);
            }

            _initialized = true;
        }

        public void SetTime(float seconds) { _clipTime = seconds; }

        /// <summary>用当前片段时间应用一次姿态。</summary>
        public void ApplyPose()
        {
            if (!_initialized || !_restCaptured || _clip == null) return;

            // 先算全部目标骨向量：铰链轴要用到相邻两条骨，不能边算边取
            for (int i = 0; i < Chain.Length; i++)
                _targetDir[i] = TargetDirection(i);

            NaturalizeArms();

            for (int i = 0; i < Chain.Length; i++)
            {
                Transform bone = _bones[i];
                if (bone == null || _targetDir[i].sqrMagnitude < 1e-8f) continue;

                Quaternion target = BuildBasis(_targetDir[i], TargetRollHint(i));
                Quaternion delta = target * Quaternion.Inverse(_restBasis[i]);

                // 四肢只取 swing，绕骨轴的 twist 沿用 bind pose。
                // 原因：肘/膝的扭转参考是 cross(近端, 远端)，而踢球摆臂、蹬地伸腿时两段骨
                // 接近共线，叉乘退化到零，roll 只能靠上一帧兜底 —— 一旦翻向就是 180° 突变，
                // 网格表现为"手臂像木板扭一下""膝盖绕轴转半圈"。
                // CMU 的 ASF/AMC 里腕关节也没有 twist DOF，本来就不携带这个信息，
                // 与其用不可靠的估计，不如稳稳保留模型的自然扭转。
                if (Chain[i].rollSource == RollSource.Hinge)
                {
                    // 分解轴必须取「rest 的骨方向」，不是目标方向：
                    // delta 作用在 rest 姿态上，绕 rest 轴剥掉 twist 后，
                    // 剩下的 swing 才能把 rest 的骨向量转到目标骨向量上。
                    // 绕目标轴分解会让 swing 之后再对不上，整条肢体偏 30°~100°。
                    Vector3 restFwd = _restBasis[i] * Vector3.forward;
                    Quaternion swing, twist;
                    SwingTwist(delta, restFwd, out swing, out twist);
                    bone.rotation = swing * _restWorldRotation[i];
                }
                else
                {
                    bone.rotation = delta * _restWorldRotation[i];
                }
            }

            BlendToRest();
            BlendTransition();
            _hasPose = true;
        }

        /// <summary>
        /// 记录当前骨骼朝向作为过渡起点。必须在切换到新片段（并重新 ApplyPose）之前调用，
        /// 这样新片段的第一帧仍然保持旧姿势，随后几帧平滑收敛到新片段。
        /// </summary>
        public void BeginTransition(float duration)
        {
            if (!_restCaptured || _bones == null) return;

            if (_transFrom == null || _transFrom.Length != _bones.Length)
                _transFrom = new Quaternion[_bones.Length];

            for (int i = 0; i < _bones.Length; i++)
                _transFrom[i] = _bones[i] != null ? _bones[i].rotation : Quaternion.identity;

            _transDur = Mathf.Max(0.02f, duration);
            _transLeft = _transDur;
        }

        /// <summary>立刻结束过渡（复位时用，避免残留把姿态拉回旧姿势）。</summary>
        public void ClearTransition()
        {
            _transLeft = 0f;
            _transDur = 0f;
        }

        /// <summary>把刚解算出的姿势与过渡起点做 Slerp；权重随剩余时间递减到 0。</summary>
        private void BlendTransition()
        {
            if (_transLeft <= 0f || _transFrom == null) return;

            _transLeft -= Time.deltaTime;
            float k = 1f - Mathf.Clamp01(_transLeft / _transDur);
            k = k * k * (3f - 2f * k);   // SmoothStep，起收都柔和

            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null) continue;
                _bones[i].rotation = Quaternion.Slerp(_transFrom[i], _bones[i].rotation, k);
            }

            if (_transLeft <= 0f) _transFrom = null;
        }

        /// <summary>
        /// 姿态向 rest（自然站立）收敛的权重，0 = 完全用动捕姿态，1 = 回到站立。
        /// 随挥播完后动作片段就结束了，如果直接停在末帧，角色会举着双臂定格在那儿，
        /// 观感比动作本身还怪。这里让末帧在 0.5 秒内平滑退回站姿。
        /// </summary>
        public void SetRestBlend(float weight)
        {
            _restBlend = Mathf.Clamp01(weight);
        }

        public float RestBlend { get { return _restBlend; } }

        private void BlendToRest()
        {
            if (_restBlend <= 0f) return;
            for (int i = 0; i < _bones.Length; i++)
            {
                if (_bones[i] == null) continue;
                _bones[i].rotation = Quaternion.Slerp(_bones[i].rotation, _restWorldRotation[i], _restBlend);
            }
        }

        /// <summary>
        /// 把过度上举的手臂拉回自然范围。
        ///
        /// CMU 这几段踢球里，受试者触球瞬间会有一条手臂甩到肩以上（10_06 的触球帧
        /// 左肘比左肩还高 7cm）。真人身上这是正常的配重摆臂，但换到这个瘦长手臂、
        /// 五指张开的模型上，就是「举手投降」——用户看到的第一眼就是这里不对。
        /// 这里按「上臂与躯干向下方向的夹角」设上限，超出部分朝着自然垂臂方向收回，
        /// 前臂同步旋转以保持肘部弯曲关系。
        /// </summary>
        private void NaturalizeArms()
        {
            if (_targetDir == null || _targetDir.Length == 0) return;

            Vector3 up = _targetDir.Length > 1 ? _targetDir[1] : transform.up;
            if (up.sqrMagnitude < 1e-8f) up = transform.up;
            Vector3 down = -up.normalized;

            ApplyArmClamp(4, 5, down);   // 左：上臂 → 前臂
            ApplyArmClamp(6, 7, down);   // 右
        }

        private void ApplyArmClamp(int upper, int lower, Vector3 down)
        {
            if (upper >= _targetDir.Length) return;
            Vector3 d = _targetDir[upper];
            if (d.sqrMagnitude < 1e-8f) return;

            float ang = Vector3.Angle(d, down);
            if (ang <= _armMaxElevation) return;

            Vector3 axis = Vector3.Cross(d, down);
            if (axis.sqrMagnitude < 1e-6f) return;
            axis.Normalize();

            float excess = (ang - _armMaxElevation) * (1f - Mathf.Clamp01(_armClampSoftness));
            Quaternion fix = Quaternion.AngleAxis(excess, axis);

            _targetDir[upper] = fix * d;
            if (lower < _targetDir.Length && _targetDir[lower].sqrMagnitude > 1e-8f)
                _targetDir[lower] = fix * _targetDir[lower];
        }

        /// <summary>数据里这段骨的方向（世界空间）。</summary>
        private Vector3 TargetDirection(int i)
        {
            if (_startJoint[i] < 0 || _endJoint[i] < 0) return Vector3.zero;
            Vector3 start = _clip.SampleJoint(_clipTime, _startJoint[i]);
            Vector3 end = _clip.SampleJoint(_clipTime, _endJoint[i]);
            return transform.TransformDirection(end - start);
        }

        /// <summary>数据里这段骨的扭转参考向量（世界空间）。</summary>
        private Vector3 TargetRollHint(int i)
        {
            BoneLink link = Chain[i];

            if (link.rollSource == RollSource.JointPair)
            {
                int left = _clip.IndexOfJoint(link.dataRefLeft);
                int right = _clip.IndexOfJoint(link.dataRefRight);
                if (left < 0 || right < 0) return Vector3.zero;
                Vector3 l = _clip.SampleJoint(_clipTime, left);
                Vector3 r = _clip.SampleJoint(_clipTime, right);
                return transform.TransformDirection(r - l);   // 指向角色右侧
            }

            // 铰链轴：cross(近端骨, 远端骨)
            // _lastHinge 按四条肢体分组：链条索引 4/5=左臂、6/7=右臂、8/9=左腿、10/11=右腿
            int group = (link.hingeProximal - 4) / 2;
            int p = link.hingeProximal;
            int d = link.hingeDistal;

            if (_targetDir[p].sqrMagnitude < 1e-8f || _targetDir[d].sqrMagnitude < 1e-8f)
                return FallbackHinge(group, link.isLeg);

            Vector3 axis = Vector3.Cross(_targetDir[p], _targetDir[d]);
            if (axis.sqrMagnitude < 1e-6f)
            {
                // 肢体几乎伸直，弯曲平面退化 —— 沿用上一帧的轴
                return FallbackHinge(group, link.isLeg);
            }

            axis.Normalize();

            // 符号连续性：叉乘结果在肢体反向摆动时可能翻向，翻向会让 roll 瞬转 180°
            if (_lastHinge[group].sqrMagnitude > 1e-6f && Vector3.Dot(axis, _lastHinge[group]) < 0f)
                axis = -axis;

            _lastHinge[group] = axis;
            return axis;
        }

        /// <summary>铰链轴不可用时的兜底：上一帧的轴，否则用「角色右轴」按解剖方向取号。</summary>
        private Vector3 FallbackHinge(int group, bool isLeg)
        {
            if (group >= 0 && group < _lastHinge.Length && _lastHinge[group].sqrMagnitude > 1e-6f)
                return _lastHinge[group];
            return transform.right * (isLeg ? 1f : -1f);
        }

        /// <summary>回到 rest 姿态（自然站立）。用于待机与复位。</summary>
        public void ApplyRestPose()
        {
            if (!_restCaptured) return;

            // 倒序放，避免子骨先摆好又被父骨带偏（世界旋转法其实无所谓，这里只是保守）
            for (int i = Chain.Length - 1; i >= 0; i--)
            {
                if (_bones[i] == null) continue;
                _bones[i].rotation = _restWorldRotation[i];
            }

            for (int i = 0; i < _lastHinge.Length; i++) _lastHinge[i] = Vector3.zero;
            _restBlend = 0f;
            _hasPose = false;
            ClearTransition();
        }

        // ── 程序化助跑参数 ──────────────────────────────────────────
        [Header("程序化助跑")]
        [Tooltip("大腿前后摆幅（度）")]
        [SerializeField] private float _thighSwing = 34f;
        [Tooltip("小腿弯曲幅度（度）")]
        [SerializeField] private float _kneeBend = 48f;
        [Tooltip("手臂摆幅（度）")]
        [SerializeField] private float _armSwing = 26f;
        [Tooltip("躯干前倾（度）")]
        [SerializeField] private float _runLean = 7f;

        /// <summary>
        /// 程序化跑步循环：从 rest 姿态出发，对四肢施加正弦摆动。
        ///
        /// 门将的做法是 Animator 播动画、代码做位移（根运动关闭）。射手没有踢球/跑步动画
        /// 资源，这里用同样的分工——腿部动作由本方法生成，位移仍由 ShooterDriver 负责。
        ///
        /// 旋转轴取「角色左右轴」再围绕 rest 世界朝向施加，因此与骨骼局部轴定义无关。
        /// </summary>
        /// <param name="phase01">循环相位，0~1 之间循环</param>
        public void ApplyRunCycle(float phase01)
        {
            if (!_restCaptured) return;

            ApplyRestPose();

            float phase = phase01 * Mathf.PI * 2f;
            float sinL = Mathf.Sin(phase);
            float sinR = Mathf.Sin(phase + Mathf.PI);
            Vector3 axis = transform.right;

            // 躯干略微前倾
            AddAxisOffset(HumanBodyBones.Spine, _runLean, axis);

            // 大腿：前后交替摆动
            AddAxisOffset(HumanBodyBones.LeftUpperLeg, sinL * _thighSwing, axis);
            AddAxisOffset(HumanBodyBones.RightUpperLeg, sinR * _thighSwing, axis);

            // 小腿：只在腿向后摆时弯曲，模拟抬腿
            AddAxisOffset(HumanBodyBones.LeftLowerLeg, Mathf.Max(0f, -sinL) * _kneeBend, axis);
            AddAxisOffset(HumanBodyBones.RightLowerLeg, Mathf.Max(0f, -sinR) * _kneeBend, axis);

            // 手臂：与同侧腿反相摆动
            AddAxisOffset(HumanBodyBones.LeftUpperArm, sinR * _armSwing, axis);
            AddAxisOffset(HumanBodyBones.RightUpperArm, sinL * _armSwing, axis);

            _hasPose = false;
        }

        /// <summary>在 rest 姿态基础上，把某根骨骼绕指定世界轴额外旋转若干度。</summary>
        private void AddAxisOffset(HumanBodyBones which, float degrees, Vector3 worldAxis)
        {
            if (Mathf.Abs(degrees) < 0.01f) return;

            int i = IndexInChain(which);
            if (i < 0 || _bones[i] == null) return;

            _bones[i].rotation = Quaternion.AngleAxis(degrees, worldAxis) * _restWorldRotation[i];
        }

        private static int IndexInChain(HumanBodyBones which)
        {
            for (int i = 0; i < Chain.Length; i++)
                if (Chain[i].bone == which) return i;
            return -1;
        }

        private void LateUpdate()
        {
            if (_autoUpdate && IsReady) ApplyPose();
        }

        public void SetAutoUpdate(bool value) { _autoUpdate = value; }
    }
}
