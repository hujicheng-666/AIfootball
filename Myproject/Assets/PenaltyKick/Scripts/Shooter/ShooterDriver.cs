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
        Done
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
        [Tooltip("朝向目标（通常是球门）；射手会始终面朝它")]
        [SerializeField] private Transform _facingTarget;
        [Tooltip("球，用于推算站位；留空则使用本对象的初始位置")]
        [SerializeField] private Transform _ball;

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

        /// <summary>片段是否自带助跑位移。自带时位移由数据驱动，跳过程序化助跑阶段。</summary>
        public bool UsesRootMotion { get { return _clip != null && _clip.HasRootMotion; } }
        /// <summary>整段前奏总时长（秒）。</summary>
        public float IntroDuration { get { return _clip != null ? KickTimeInIntro + (_clip.Duration - _kickTime) : 0f; } }

        private void Awake()
        {
            if (_retargeter == null) _retargeter = GetComponent<ShooterRetargeter>();
            if (_retargeter == null) _retargeter = GetComponentInChildren<ShooterRetargeter>();

            // 姿态由本组件统一驱动，避免 Retargeter 在 LateUpdate 里重复应用
            if (_retargeter != null) _retargeter.SetAutoUpdate(false);

            // 记录初始高度作为地面基准，避免反复播放时位置累积漂移
            _groundY = transform.position.y;
        }

        private void Start()
        {
            ApplyKitColor();
            LoadClipIfNeeded();
            StopAndReset();
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
            LoadClip(true);   // 每次开球重新抽一个姿态，避免千篇一律
            if (_clip == null) return false;

            // 先定朝向：根位移要乘 transform.rotation，朝向没定就会算错起点
            KeepFacingTarget();
            ComputeStandPositions();
            transform.position = _startPosition;
            _phase = ShooterPhase.Hold;
            _phaseTime = 0f;
            _kickFired = false;

            // 起跑前站定：用片段首帧的自然站姿，而不是 bind pose。
            // bind pose 是 A-pose（两臂张成 20°~40°），在跑动开始前会先在原地
            // 摆出一个像"举手"的站姿，比动作本身还显眼。首帧是动捕里真实的准备姿势。
            if (_retargeter != null)
            {
                // Hold 阶段用 rest 自然站姿等待起跑(姿态片段首帧是摆腿准备, 不适合站立)
                _retargeter.SetClip(_clip);
                _retargeter.SetRestBlend(1f);
                _retargeter.ApplyPose();
            }
            return true;
        }

        /// <summary>中止并回到待机：位置复位到踢球站位，姿态回到自然站立。</summary>
        public void StopAndReset()
        {
            _phase = ShooterPhase.Idle;
            _phaseTime = 0f;
            _kickFired = false;
            ComputeStandPositions();
            transform.position = _strikePosition;
            KeepFacingTarget();
            if (_retargeter != null)
            {
                // 待机用 rest 自然站姿
                _retargeter.SetRestBlend(1f);
                _retargeter.ApplyPose();
            }
        }

        private void ComputeStandPositions()
        {
            Vector3 forward = Vector3.forward;
            if (_facingTarget != null)
            {
                Vector3 flat = Vector3.ProjectOnPlane(_facingTarget.position - transform.position, Vector3.up);
                if (flat.sqrMagnitude > 1e-6f) forward = flat.normalized;
            }

            Vector3 anchor = _ball != null ? _ball.position : transform.position;
            // 片段自带实测站位时优先用它，保证触球帧的脚刚好落在球上
            float stand = (_clip != null && _clip.standOffset > 0.05f) ? _clip.standOffset : _standOffset;
            _strikePosition = anchor - forward * stand;
            _strikePosition.y = _groundY + _rootYOffset;

            if (_clip != null && _clip.HasRootMotion)
            {
                // 动捕自带助跑轨迹：起点直接取数据第一帧，和后面 Strike 阶段的位置连续
                Vector2 h = _clip.SampleHip(0f);
                _startPosition = _strikePosition + transform.rotation * new Vector3(h.x, 0f, h.y);
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
            if (_clip == null || _phase == ShooterPhase.Idle) return;

            // 随挥播完后不要硬停在末帧（末帧是两臂张开的收势，定格几秒非常难看），
            // 用 0.5 秒平滑退回自然站姿。
            if (_phase == ShooterPhase.Done)
            {
                if (_retargeter != null && _retargeter.RestBlend < 1f)
                {
                    _retargeter.SetRestBlend(_retargeter.RestBlend + Time.deltaTime / 0.5f);
                    _retargeter.SetTime(_clip.Duration);
                    _retargeter.ApplyPose();
                }
                return;
            }

            KeepFacingTarget();
            _phaseTime += Time.deltaTime;

            switch (_phase)
            {
                case ShooterPhase.Hold:
                    if (UsesRootMotion) PlaceByRootMotion(0f);
                    if (_phaseTime >= _holdBeforeRunUp)
                    {
                        // 动捕片段自带助跑，跳过程序化跑步，直接进入数据段
                        // 从站姿切入助跑/射门：先快照站姿，避免第一帧硬切
                        if (_retargeter != null) _retargeter.BeginTransition(0.12f);
                        _phase = UsesRootMotion ? ShooterPhase.Strike : ShooterPhase.RunUp;
                        if (UsesRootMotion && _retargeter != null)
                        {
                            // 直接进 Strike 时必须解除 rest 混合，否则整段都是站立姿势
                            _retargeter.SetRestBlend(0f);
                            _retargeter.SetTime(0f);
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

                    if (_retargeter != null)
                    {
                        _retargeter.SetTime(_phaseTime);
                        _retargeter.ApplyPose();
                    }

                    if (!_kickFired && _phaseTime >= _kickTime)
                    {
                        _kickFired = true;
                        if (_logEvents) Debug.Log("[ShooterDriver] 触球", this);
                        Action handler = Kick;
                        if (handler != null) handler.Invoke();
                    }

                    // 触球后仍继续播放随挥，直到片段结束
                    if (_phaseTime >= _clip.Duration)
                    {
                        _phase = ShooterPhase.Done;
                        _phaseTime = _clip.Duration;
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 用动捕自带的髋水平轨迹摆放角色：触球帧对齐站位点，助跑与随挥的前后位移由数据推进。
        /// 轨迹存在角色的局部空间里（+z 为射门方向），所以要乘 transform.rotation 转世界。
        /// </summary>
        private void PlaceByRootMotion(float t)
        {
            if (_clip == null) return;
            Vector2 h = _clip.SampleHip(t);
            Vector3 world = _strikePosition + transform.rotation * new Vector3(h.x, 0f, h.y);
            world.y = _groundY + _rootYOffset;
            transform.position = world;
        }

        private void KeepFacingTarget(){
            if (_facingTarget == null) return;
            Vector3 flat = Vector3.ProjectOnPlane(_facingTarget.position - transform.position, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f) return;
            // 叠一个偏航角：让射手侧身对球门（正对看起来像站军姿，也不像真实助跑）
            Quaternion look = Quaternion.LookRotation(flat.normalized, Vector3.up);
            transform.rotation = look * Quaternion.Euler(0f, _yawOffset, 0f);
        }

        /// <summary>把当前片段时间归零并停在首帧（用于预览）。</summary>
        [ContextMenu("预览：停在首帧")]
        private void PreviewFirstFrame()
        {
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
