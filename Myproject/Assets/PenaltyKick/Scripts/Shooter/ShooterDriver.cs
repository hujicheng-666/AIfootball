using System;
using UnityEngine;

namespace PenaltyKickPlatform.Shooter
{
    public enum ShooterPhase
    {
        Idle,
        Hold,
        RunUp,
        Strike,
        Done,
        Recover
    }

    /// <summary>
    /// 射手表现驱动：助跑前奏（程序化位移）+ 触球段（3DSP 真实姿态）。
    ///
    /// 时间轴
    /// ------
    ///   t = 0                                 Hold 开始（原地静立）
    ///   t = holdBeforeRunUp                   RunUp 开始（位置插值接近球）
    ///   t = hold + runUpDuration              切入 Strike（数据段 t=0）
    ///   t = hold + runUpDuration + kickTime   **触球 → 触发 <see cref="Kick"/> 事件**
    ///   t = hold + runUpDuration + duration   结束
    ///
    /// 外部在收到 <see cref="Kick"/> 时才让球飞出，从而保证「脚触到球的瞬间球才离开」。
    /// 这段前奏**不计入轨迹时间轴**——球被 PlayTrajectory 驱动时仍然是 t=0 触球，
    /// 因此门将逻辑与评估口径完全不受影响。
    /// </summary>
    [DisallowMultipleComponent]
    public class ShooterDriver : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("射手重定向器；留空则自动在本对象上查找")]
        [SerializeField] private ShooterRetargeter _retargeter;
        [Tooltip("模型 Animator；留空则自动在子物体中查找")]
        [SerializeField] private Animator _animator;
        [Tooltip("朝向目标（通常是球门）；射手会始终面朝它")]
        [SerializeField] private Transform _facingTarget;
        [Tooltip("球，用于推算站位；留空则使用本对象的初始位置")]
        [SerializeField] private Transform _ball;

        [Header("动画片段驱动（优先于 JSON）")]
        [Tooltip("非空时用 AnimationClip 直驱 Animator：保留全部骨骼与手指数据，动作与源 FBX 逐帧一致。\n此时下面的 JSON 姿态全部忽略；把它清空即可一键回退到 JSON 驱动。")]
        [SerializeField] private AnimationClip _animClip;
        [Tooltip("该 clip 的触球时刻（秒）。源动画 60fps 共 91 帧，右脚触球在第 46 帧 → 0.767s")]
        [SerializeField] private float _clipKickTime = 0.767f;
        [Tooltip("触球瞬间「髋到球」的水平距离（米）；脚在该帧位于髋前约 0.36m")]
        [SerializeField] private float _clipStandOffset = 0.36f;

        [Header("姿态数据")]
        [Tooltip("直接指定 JSON；留空则从 Resources 按下面的路径加载")]
        [SerializeField] private TextAsset _poseJson;
        [SerializeField] private string _poseResourcePath = "ShooterPoses/00008";
        [Tooltip("姿态池：非空时每次开球从中随机挑一个，用于丰富射门动作；留空则只用上面的单一路径")]
        [SerializeField] private string[] _poseResourcePaths = new string[0];
        [Tooltip("助跑跑步循环姿态（如 Mixamo 慢跑）；留空则用程序化正弦摆腿")]
        [SerializeField] private string _runPoseResourcePath = "ShooterPoses/SH_Jog";
        [Tooltip("跑步循环一个步态周期的时长（秒）")]
        [SerializeField] private float _runCycleSeconds = 0.867f;
        [Tooltip("指定使用的触球帧；<0 表示用数据自带的 kickFrame")]
        [SerializeField] private int _kickFrameOverride = -1;

        [Header("时序（秒）")]
        [SerializeField] private float _holdBeforeRunUp = 0.30f;
        [SerializeField] private float _runUpDuration = 0.95f;
        [Tooltip("助跑期间的步数（程序化跑步循环次数）")]
        [SerializeField] private float _runUpSteps = 3f;
        [SerializeField] private bool _useKinematicKickFrame = false;

        [Header("站位")]
        [Tooltip("踢球时射手到球的距离（沿朝向前方）")]
        [SerializeField] private float _standOffset = 0.62f;
        [Tooltip("助跑起点相对踢球站位的后退距离")]
        [SerializeField] private float _runUpDistance = 0.85f;
        [Tooltip("相对球门的偏航角（度）：让射手侧身站立、侧身助跑，而不是正对球门")]
        [SerializeField] private float _yawOffset = 35f;
        [Tooltip("把根节点抬高到球的高度之上，避免脚穿进球里")]
        [SerializeField] private float _rootYOffset = 0f;

        [Header("外观")]
        [Tooltip("运行时把球衣材质改成下面的颜色，便于与门将区分")]
        [SerializeField] private bool _recolorKit = true;
        [Tooltip("射手球衣颜色")]
        [SerializeField] private Color _kitColor = new Color(0.85f, 0.20f, 0.18f);

        [Header("调试")]
        [SerializeField] private bool _logEvents = true;

        /// <summary>触球瞬间触发；外部应在此刻调用 Ball.PlayTrajectory。</summary>
        public event Action Kick;

        private ShooterPoseClip _clip;
        private ShooterPoseClip _runClip;
        private string _lastPosePath;
        private string _loadedPosePath;
        private ShooterPhase _phase = ShooterPhase.Idle;
        private float _phaseTime;
        private bool _kickFired;
        private Vector3 _strikePosition;
        private Vector3 _startPosition;
        private float _kickTime;
        private float _groundY;
        private Vector3 _sceneAnchor;
        private Rigidbody _ballBody;
        private const float RecoveryDuration = 0.55f;

        // ---- clip 驱动模式运行时状态 ----
        private GameObject _clipRoot;      // SampleAnimation 的作用根节点（模型根）
        private Vector2[] _hipSamples;     // 相对触球帧的髋水平偏移（x,z）
        private Vector2 _hipAtKick;        // 触球帧髋在模型局部空间的绝对偏移
        private float _hipSampleDt;
        private bool _graphReady;          // clip 模式是否已就绪
        private Transform _clipHips;
        private Transform[] _poseTransforms;
        private ClipPose[] _readyPose;
        private ClipPose[] _followThroughPose;

        private struct ClipPose
        {
            public Vector3 Position;
            public Quaternion Rotation;

            public ClipPose(Transform bone)
            {
                Position = bone.localPosition;
                Rotation = bone.localRotation;
            }
        }

        public ShooterPhase Phase { get { return _phase; } }
        public bool IsPlaying { get { return _phase != ShooterPhase.Idle && _phase != ShooterPhase.Done; } }
        public ShooterPoseClip Clip { get { return _clip; } }
        /// <summary>触球时刻在整段前奏中的位置（秒），便于外部做进度条或预判。</summary>
        public float KickTimeInIntro
        {
            get
            {
                // 有根位移的片段（CMU 动捕）自带助跑，不再叠加程序化助跑时长
                float runUp = UsesRootMotion ? 0f : _runUpDuration;
                return _holdBeforeRunUp + runUp + _kickTime;
            }
        }

        /// <summary>是否处于动画片段驱动模式（忽略 JSON 数据）。</summary>
        public bool UsesAnimatorClip { get { return _animClip != null; } }
        private bool HasAnyClip { get { return _animClip != null || _clip != null; } }
        private float ClipDuration
        {
            get { return _animClip != null ? _animClip.length : (_clip != null ? _clip.Duration : 0f); }
        }

        /// <summary>片段是否自带助跑位移。自带时位移由数据驱动，跳过程序化助跑阶段。</summary>
        public bool UsesRootMotion { get { return _animClip != null || (_clip != null && _clip.HasRootMotion); } }
        /// <summary>整段前奏总时长（秒）。</summary>
        public float IntroDuration { get { return HasAnyClip ? KickTimeInIntro + (ClipDuration - _kickTime) : 0f; } }

        private void Awake()
        {
            if (_retargeter == null) _retargeter = GetComponent<ShooterRetargeter>();
            if (_retargeter == null) _retargeter = GetComponentInChildren<ShooterRetargeter>();
            if (_animator == null) _animator = GetComponentInChildren<Animator>();

            // 姿态由本组件统一驱动，避免 Retargeter 在 LateUpdate 里重复应用
            if (_retargeter != null) _retargeter.SetAutoUpdate(false);

            // 记录初始高度作为地面基准，避免反复播放时位置累积漂移
            _groundY = transform.position.y;
            _sceneAnchor = transform.position;
            if (_ball != null) _ballBody = _ball.GetComponentInParent<Rigidbody>();
        }

        private void Start()
        {
            ApplyKitColor();
            if (_animClip != null) InitClipPlayable();
            if (_animClip == null) LoadClipIfNeeded();
            StopAndReset();
        }

        // ==================== clip 驱动模式 ====================

        /// <summary>
        /// 进入 clip 驱动模式。这里刻意不用 PlayableGraph：
        /// 编辑模式与 batchmode 下 PlayableGraph.Evaluate 不推进动画（实测髋轨迹 91 个采样点全同值），
        /// 而 AnimationClip.SampleAnimation 在两种模式下都稳定生效，且时间轴完全由本组件掌控。
        /// 代价是需要关掉 Animator，否则它每帧会把姿态覆盖回去。
        /// </summary>
        private void InitClipPlayable()
        {
            if (_animator == null)
            {
                Debug.LogError("[ShooterDriver] clip 驱动模式需要 Animator，未在子物体中找到，退回 JSON 模式。", this);
                _animClip = null;
                return;
            }

            _animator.applyRootMotion = false;
            _animator.Rebind();        // 实例化/编辑模式下骨骼绑定可能尚未建立
            _animator.enabled = false; // 关掉，避免它覆盖 SampleAnimation 写入的骨骼

            _clipRoot = _animator.gameObject;
            _graphReady = true;

            _kickTime = Mathf.Clamp(_clipKickTime, 0f, _animClip.length);
            PreSampleHips();
            ApplyPoseAt(0f);
            _poseTransforms = Array.FindAll(_clipRoot.GetComponentsInChildren<Transform>(true),
                bone => bone != transform);
            _readyPose = Array.ConvertAll(_poseTransforms, bone => new ClipPose(bone));
            _followThroughPose = new ClipPose[_poseTransforms.Length];

            if (_logEvents)
                Debug.Log(string.Format(
                    "[ShooterDriver] clip 模式：{0} 长 {1:0.###}s，触球 {2:0.###}s，髋轨迹 {3} 点，前进 {4:0.##}m",
                    _animClip.name, _animClip.length, _kickTime,
                    _hipSamples != null ? _hipSamples.Length : 0,
                    HipTravelDistance()), this);
        }

        /// <summary>预采样髋水平轨迹，并换算成「相对触球帧」的偏移。</summary>
        private void PreSampleHips()
        {
            if (!_graphReady || _clipRoot == null || _animClip == null) return;

            Transform hips = _animator != null ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            if (hips == null) hips = FindDeep(_clipRoot.transform, "Hips");
            _clipHips = hips;
            if (hips == null)
            {
                Debug.LogError("[ShooterDriver] 取不到 Hips 骨骼，髋轨迹不可用，角色将原地播放。", this);
                return;
            }

            float dur = _animClip.length;
            int n = Mathf.Max(2, Mathf.RoundToInt(dur * 60f) + 1);
            _hipSampleDt = dur / (n - 1);

            Vector2[] raw = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                _animClip.SampleAnimation(_clipRoot, i * _hipSampleDt);
                Vector3 p = hips.localPosition;
                raw[i] = new Vector2(p.x, p.z);
            }

            // 以触球帧为原点：这样 PlaceByRootMotion 能让触球瞬间正好站在站位点上
            int ki = Mathf.Clamp(Mathf.RoundToInt(_kickTime / Mathf.Max(1e-6f, _hipSampleDt)), 0, n - 1);
            Vector2 atKick = raw[ki];
            _hipAtKick = atKick;
            _hipSamples = new Vector2[n];
            for (int i = 0; i < n; i++) _hipSamples[i] = raw[i] - atKick;

            if (_logEvents)
                Debug.Log(string.Format(
                    "[ShooterDriver] 髋轨迹采样: 首=({0:F3},{1:F3}) 触球=({2:F3},{3:F3}) 末=({4:F3},{5:F3}) 前进 {6:F3}m",
                    raw[0].x, raw[0].y, atKick.x, atKick.y, raw[n - 1].x, raw[n - 1].y,
                    Vector2.Distance(raw[0], raw[n - 1])), this);

            _animClip.SampleAnimation(_clipRoot, 0f);
        }

        private static Transform FindDeep(Transform root, string keyword)
        {
            if (root.name.ToLowerInvariant().Contains(keyword.ToLowerInvariant())) return root;
            foreach (Transform c in root)
            {
                var r = FindDeep(c, keyword);
                if (r != null) return r;
            }
            return null;
        }

        private float HipTravelDistance()
        {
            if (_hipSamples == null || _hipSamples.Length < 2) return 0f;
            return Vector2.Distance(_hipSamples[0], _hipSamples[_hipSamples.Length - 1]);
        }

        /// <summary>统一取髋水平偏移（相对触球帧）：clip 模式查预采样表，JSON 模式查帧数据。</summary>
        private Vector2 SampleHipUnified(float t)
        {
            if (_animClip != null)
            {
                if (_hipSamples == null || _hipSamples.Length < 2) return Vector2.zero;
                float f = Mathf.Clamp(t / Mathf.Max(1e-6f, _hipSampleDt), 0f, _hipSamples.Length - 1);
                int i0 = Mathf.FloorToInt(f);
                int i1 = Mathf.Min(i0 + 1, _hipSamples.Length - 1);
                float k = f - i0;
                return Vector2.Lerp(_hipSamples[i0], _hipSamples[i1], k);
            }
            return _clip != null ? _clip.SampleHip(t) : Vector2.zero;
        }

        /// <summary>统一按时间应用姿态：clip 模式采样动画片段，JSON 模式走重定向器。</summary>
        private void ApplyPoseAt(float t)
        {
            if (_animClip != null)
            {
                if (!_graphReady || _clipRoot == null) return;
                _animClip.SampleAnimation(_clipRoot, Mathf.Clamp(t, 0f, _animClip.length));
                return;
            }
            if (_retargeter != null)
            {
                _retargeter.SetTime(t);
                _retargeter.ApplyPose();
            }
        }

        private void OnDestroy()
        {
            _graphReady = false;
        }

        /// <summary>
        /// 运行时把球衣材质染成醒目颜色，避免射手看起来和门将一模一样。
        ///
        /// 这里用 <c>Renderer.materials</c>（而非 sharedMaterials）以取得材质实例副本，
        /// 所以不会污染磁盘上的原始材质资源。也正因为放在运行时，它不依赖编辑器
        /// 创建材质资产是否成功——那条路在资产已存在时会静默失败。
        /// </summary>
        private void ApplyKitColor()
        {
            if (!_recolorKit) return;

            int changedRenderers = 0;
            foreach (Renderer renderer in GetComponentsInChildren<Renderer>(true))
            {
                Material[] materials = renderer.materials;
                bool changed = false;
                for (int i = 0; i < materials.Length; i++)
                {
                    Material material = materials[i];
                    if (material == null) continue;
                    if (!material.name.ToLowerInvariant().Contains("kit")) continue;

                    if (material.HasProperty("_Color")) material.SetColor("_Color", _kitColor);
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", _kitColor);
                    changed = true;
                }
                if (changed)
                {
                    renderer.materials = materials;
                    changedRenderers++;
                }
            }

            if (_logEvents)
                Debug.Log("[ShooterDriver] 球衣换色完成，影响 " + changedRenderers + " 个 Renderer。", this);
        }

        /// <summary>从姿态池里挑一个资源路径；池为空时退回单一路径。</summary>
        private string PickPosePath()
        {
            if (_poseResourcePaths != null && _poseResourcePaths.Length > 0)
            {
                // 避免连续两次抽到同一个，观感上更"随机"
                if (_poseResourcePaths.Length > 1 && _lastPosePath != null)
                {
                    for (int guard = 0; guard < 4; guard++)
                    {
                        var c = _poseResourcePaths[UnityEngine.Random.Range(0, _poseResourcePaths.Length)];
                        if (!string.IsNullOrEmpty(c) && c != _lastPosePath) { _lastPosePath = c; return c; }
                    }
                }
                var pick = _poseResourcePaths[UnityEngine.Random.Range(0, _poseResourcePaths.Length)];
                if (!string.IsNullOrEmpty(pick)) { _lastPosePath = pick; return pick; }
            }
            return _poseResourcePath;
        }

        private void LoadClipIfNeeded() { LoadClip(false); }

        private void LoadClip(bool forceRepick)
        {
            if (_animClip != null) return;   // clip 驱动模式不使用 JSON 姿态
            if (_clip != null && !forceRepick) return;

            string path = forceRepick ? PickPosePath()
                : (!string.IsNullOrEmpty(_lastPosePath) ? _lastPosePath : PickPosePath());

            if (_poseJson == null || forceRepick || path != _loadedPosePath)
            {
                var loaded = !string.IsNullOrEmpty(path) ? Resources.Load<TextAsset>(path) : null;
                if (loaded == null && path != _poseResourcePath)
                    loaded = Resources.Load<TextAsset>(_poseResourcePath);
                _poseJson = loaded;
                _loadedPosePath = path;
            }

            if (_poseJson == null)
            {
                Debug.LogWarning(
                    "[ShooterDriver] 未找到姿态数据（Resources 路径: " + path + "）。" +
                    "请先运行 scripts/export_shooter_pose.py 导出。", this);
                return;
            }

            _runClip = null;
            if (!string.IsNullOrEmpty(_runPoseResourcePath))
            {
                var runJson = Resources.Load<TextAsset>(_runPoseResourcePath);
                if (runJson != null)
                    _runClip = ShooterPoseClip.FromJson(runJson.text);
                if (_runClip == null || _runClip.FrameCount == 0)
                {
                    Debug.LogWarning("[ShooterDriver] 跑步循环姿态加载失败: " + _runPoseResourcePath
                        + "，RunUp 回退程序化摆腿。", this);
                    _runClip = null;
                }
            }

            _clip = ShooterPoseClip.FromJson(_poseJson.text);
            if (_clip == null || _clip.FrameCount == 0)
            {
                Debug.LogWarning("[ShooterDriver] 姿态数据解析失败或为空: " + path, this);
                _clip = null;
                return;
            }

            int kickFrame = _kickFrameOverride >= 0
                ? Mathf.Clamp(_kickFrameOverride, 0, _clip.FrameCount - 1)
                : (_useKinematicKickFrame ? _clip.kickFrameKinematic : _clip.kickFrame);
            kickFrame = Mathf.Clamp(kickFrame, 0, _clip.FrameCount - 1);
            _kickTime = _clip.frames[kickFrame].t;

            if (_retargeter != null) _retargeter.SetClip(_clip);

            if (_logEvents)
            {
                Debug.Log(string.Format(
                    "[ShooterDriver] 载入 {0}：{1} 帧 @ {2:0.#}fps，触球帧 {3}（t={4:0.###}s），片段长 {5:0.###}s",
                    _clip.shotId, _clip.FrameCount, _clip.fps, kickFrame, _kickTime, _clip.Duration), this);
            }
        }

        /// <summary>开始播放整段前奏。返回 false 表示数据缺失，调用方应直接播放球轨迹。</summary>
        public bool Play()
        {
            if (_animClip == null) LoadClip(true);   // 每次开球重新抽一个姿态，避免千篇一律
            if (!HasAnyClip) return false;
            if (_animClip != null && !_graphReady) InitClipPlayable();
            if (!HasAnyClip) return false;

            ComputeStandPositions();
            transform.position = _startPosition;
            _phase = ShooterPhase.Hold;
            _phaseTime = 0f;
            _kickFired = false;

            if (_animClip != null)
            {
                // clip 首帧是动捕里真实的准备姿势
                ApplyPoseAt(0f);
            }
            else if (_retargeter != null)
            {
                // Hold 阶段用 rest 自然站姿等待起跑(姿态片段首帧是摆腿准备, 不适合站立)
                _retargeter.SetClip(_clip);
                _retargeter.SetRestBlend(1f);
                _retargeter.ApplyPose();
            }
            return true;
        }

        /// <summary>
        /// 中止并回到待机：位置复位到**助跑起点**（不是踢球站位），姿态回到自然站立。
        /// 回到起点而非站位，是为了让待机位置与下一次 Play() 的起始位置一致，
        /// 避免开播瞬间从球边瞬移到身后。
        /// </summary>
        public void StopAndReset()
        {
            _phase = ShooterPhase.Idle;
            _phaseTime = 0f;
            _kickFired = false;
            ComputeStandPositions();
            transform.position = _startPosition;
            if (_animClip != null)
            {
                ApplyPoseAt(0f);       // 停在首帧（准备姿势）
            }
            else if (_retargeter != null)
            {
                _retargeter.ApplyRestPose();
            }
        }

        private void ComputeStandPositions()
        {
            // Rigidbody.position 在复位当帧已经更新，插值中的 Transform 可能仍在球门附近。
            Vector3 anchor = _ballBody != null ? _ballBody.position
                : _ball != null ? _ball.position : _sceneAnchor;

            // 以球心为参考而不是射手当前位置：位置与朝向互为输入时，
            // 每次复位都会基于上一次的结果重算，导致站位反复迭代漂移。
            Vector3 forward = Vector3.forward;
            if (_facingTarget != null)
            {
                Vector3 flat = Vector3.ProjectOnPlane(_facingTarget.position - anchor, Vector3.up);
                if (flat.sqrMagnitude > 1e-6f)
                {
                    forward = flat.normalized;
                    transform.rotation = Quaternion.LookRotation(forward, Vector3.up) *
                        Quaternion.Euler(0f, _yawOffset, 0f);
                }
            }
            // 触球帧的脚要刚好落在球上：clip 模式用实测的髋-脚水平距离，
            // JSON 模式优先用片段自带的 standOffset
            float stand = _animClip != null
                ? _clipStandOffset
                : ((_clip != null && _clip.standOffset > 0.05f) ? _clip.standOffset : _standOffset);
            _strikePosition = anchor - forward * stand;
            _strikePosition.y = _groundY + _rootYOffset;

            if (UsesRootMotion)
            {
                if (_animClip != null)
                {
                    // clip 模式：前方位移已经烘在动画的 Hips 里，外层根节点必须固定不动，
                    // 只负责把「触球帧的髋」摆到站位点，否则会双重位移（实测角色飞出画面）
                    _startPosition = _strikePosition
                        - transform.rotation * new Vector3(_hipAtKick.x, 0f, _hipAtKick.y);
                }
                else
                {
                    // 动捕自带助跑轨迹：起点直接取数据第一帧，和后面 Strike 阶段的位置连续
                    Vector2 h = SampleHipUnified(0f);
                    _startPosition = _strikePosition + transform.rotation * new Vector3(h.x, 0f, h.y);
                }
                _startPosition.y = _strikePosition.y;
            }
            else
            {
                _startPosition = _strikePosition - forward * _runUpDistance;
                _startPosition.y = _strikePosition.y;
            }
        }

        private void Update()
        {
            if (!HasAnyClip || !IsPlaying) return;

            _phaseTime += Time.deltaTime;

            switch (_phase)
            {
                case ShooterPhase.Hold:
                    if (UsesRootMotion) PlaceByRootMotion(0f);
                    if (_phaseTime >= _holdBeforeRunUp)
                    {
                        // 动捕片段自带助跑，跳过程序化跑步，直接进入数据段
                        // 从站姿切入助跑/射门：JSON 模式先快照站姿避免第一帧硬切；
                        // clip 模式首帧就是准备姿势，直接切换即可
                        if (_animClip == null && _retargeter != null) _retargeter.BeginTransition(0.12f);
                        _phase = UsesRootMotion ? ShooterPhase.Strike : ShooterPhase.RunUp;
                        if (UsesRootMotion)
                        {
                            if (_animClip != null) ApplyPoseAt(0f);
                            else if (_retargeter != null)
                            {
                                // 直接进 Strike 时必须解除 rest 混合，否则整段都是站立姿势
                                _retargeter.SetRestBlend(0f);
                                _retargeter.SetTime(0f);
                            }
                        }
                        _phaseTime = 0f;
                    }
                    break;

                case ShooterPhase.RunUp:
                {
                    float k = _runUpDuration > 1e-4f ? Mathf.Clamp01(_phaseTime / _runUpDuration) : 1f;
                    // SmoothStep：起步与制动都更自然
                    float eased = k * k * (3f - 2f * k);
                    transform.position = Vector3.Lerp(_startPosition, _strikePosition, eased);

                    // 跑步姿态：优先 Mixamo 跑步循环(真实步态)，缺失时回退程序化正弦摆腿。
                    // 位移仍由本组件负责，与门将「动画管腿、代码管位移」的分工一致。
                    if (_retargeter != null)
                    {
                        if (_runClip != null)
                        {
                            _retargeter.SetClip(_runClip);
                            _retargeter.SetRestBlend(0f);

                            // 关键：必须按「片段完整周期」取模，再用时间缩放调步频。
                            // 早先直接用 (_phaseTime % _runCycleSeconds) 采样，等于把 2.6 秒的
                            // 跑步片段截成前 0.867 秒来循环 —— 回绕点落在右腿最快的位置，
                            // 每 0.867 秒姿势突跳 0.5 米，视觉上就是"卡顿式重复闪烁"。
                            // 片段首末帧是闭合的（差 0.004m），按整周期循环才不会跳。
                            float cycle = _runClip.Duration;
                            float speed = _runCycleSeconds > 0.01f ? cycle / _runCycleSeconds : 1f;
                            float tt = cycle > 0.01f
                                ? Mathf.Repeat(_phaseTime * speed, cycle)
                                : _phaseTime;

                            _retargeter.SetTime(tt);
                            _retargeter.ApplyPose();
                        }
                        else
                        {
                            float steps = Mathf.Max(0.5f, _runUpSteps);
                            float phase = Mathf.Lerp(0f, steps, eased) % 1f;
                            _retargeter.ApplyRunCycle(phase);
                        }
                    }

                    if (k >= 1f)
                    {
                        _phase = ShooterPhase.Strike;
                        _phaseTime = 0f;
                        // 切回射门姿态片段：先快照跑步末姿势，再让 Kick 首帧从它平滑收敛，
                        // 否则"迈步中"直接跳到"站定准备"会是一帧内的巨幅突变。
                        if (_retargeter != null && _clip != null)
                        {
                            _retargeter.BeginTransition(0.14f);
                            _retargeter.SetClip(_clip);
                            _retargeter.SetRestBlend(0f);
                            _retargeter.SetTime(0f);
                        }
                    }
                    break;
                }

                case ShooterPhase.Strike:
                {
                    if (UsesRootMotion) PlaceByRootMotion(_phaseTime);

                    ApplyPoseAt(_phaseTime);

                    if (!_kickFired && _phaseTime >= _kickTime)
                    {
                        _kickFired = true;
                        if (_logEvents) Debug.Log("[ShooterDriver] 触球", this);
                        Action handler = Kick;
                        if (handler != null) handler.Invoke();
                    }

                    // 触球后仍继续播放随挥，直到片段结束
                    if (_phaseTime >= ClipDuration)
                    {
                        _phase = ShooterPhase.Recover;
                        _phaseTime = 0f;
                        if (_poseTransforms != null)
                            for (int i = 0; i < _poseTransforms.Length; i++)
                                _followThroughPose[i] = new ClipPose(_poseTransforms[i]);
                    }
                    break;
                }

                case ShooterPhase.Recover:
                    RecoverInPlace();
                    break;
            }
        }

        private void RecoverInPlace()
        {
            float t = Mathf.Clamp01(_phaseTime / RecoveryDuration);
            float blend = Mathf.SmoothStep(0f, 1f, t);
            if (_poseTransforms != null)
            {
                for (int i = 0; i < _poseTransforms.Length; i++)
                {
                    Transform bone = _poseTransforms[i];
                    ClipPose pose = _followThroughPose[i];
                    Vector3 target = _readyPose[i].Position;
                    // 根位移烘在髋部；就地收势时保留触球之后已经迈出的步子。
                    if (bone == _clipHips || bone == _clipRoot.transform)
                    {
                        target.x = pose.Position.x;
                        target.z = pose.Position.z;
                    }
                    bone.localPosition = Vector3.Lerp(pose.Position, target, blend);
                    bone.localRotation = Quaternion.Slerp(pose.Rotation, _readyPose[i].Rotation, blend);
                }
            }
            else if (_retargeter != null)
            {
                _retargeter.SetRestBlend(blend);
                _retargeter.ApplyPose();
            }
            if (t >= 1f) _phase = ShooterPhase.Done;
        }

        /// <summary>
        /// 用动捕自带的髋水平轨迹摆放角色：触球帧对齐站位点，助跑与随挥的前后位移由数据推进。
        /// 轨迹存在角色的局部空间里（+z 为射门方向），所以要乘 transform.rotation 转世界。
        /// </summary>
        private void PlaceByRootMotion(float t)
        {
            if (!HasAnyClip) return;
            // clip 模式的前方位移在动画内部（Hips 曲线），外层根节点必须保持固定
            if (_animClip != null) return;
            Vector2 h = SampleHipUnified(t);
            Vector3 world = _strikePosition + transform.rotation * new Vector3(h.x, 0f, h.y);
            world.y = _groundY + _rootYOffset;
            transform.position = world;
        }

        /// <summary>把当前片段时间归零并停在首帧（用于预览）。</summary>
        [ContextMenu("预览：停在首帧")]
        private void PreviewFirstFrame()
        {
            if (_animClip != null)
            {
                if (!_graphReady) InitClipPlayable();
                ComputeStandPositions();
                transform.position = _strikePosition;
                ApplyPoseAt(0f);
                return;
            }
            LoadClipIfNeeded();
            ComputeStandPositions();
            transform.position = _strikePosition;
            if (_retargeter != null && _clip != null)
            {
                _retargeter.SetClip(_clip);
                _retargeter.SetTime(0f);
                _retargeter.ApplyPose();
            }
        }

        /// <summary>把当前片段时间设到触球帧（用于预览触球姿态）。</summary>
        [ContextMenu("预览：停在触球帧")]
        private void PreviewKickFrame()
        {
            if (_animClip != null)
            {
                if (!_graphReady) InitClipPlayable();
                ComputeStandPositions();
                transform.position = _strikePosition;
                ApplyPoseAt(_kickTime);
                return;
            }
            LoadClipIfNeeded();
            if (_clip == null) return;
            ComputeStandPositions();
            transform.position = _strikePosition;
            if (_retargeter != null)
            {
                _retargeter.SetClip(_clip);
                _retargeter.SetTime(_kickTime);
                _retargeter.ApplyPose();
            }
        }
    }
}
