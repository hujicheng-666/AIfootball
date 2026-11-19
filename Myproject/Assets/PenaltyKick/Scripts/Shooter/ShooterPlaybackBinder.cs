using System;
using Assets.SuperGoalie.Scripts.Managers;
using UnityEngine;

namespace PenaltyKickPlatform.Shooter
{
    /// <summary>
    /// 把射手前奏接到球发射流程上：接管 <see cref="ShotPlaybackGate"/>，
    /// 先播射手助跑与摆腿，**在触球帧那一刻**才真正让球飞出。
    ///
    /// 放行 / 取消 是两种不同语义，不能混用：
    ///   • 放行（Release）：调用 playBall，球飞出——触球、超时兜底、组件被禁用时使用；
    ///   • 取消（Cancel）：丢弃 playBall，球不出——复位、停止、被新的播放请求取代时使用。
    ///
    /// 若射手缺失、数据未导出或被禁用，会立即回退为原来的行为（球立刻飞出），
    /// 因此这个组件可以安全地留在场景里而不影响既有功能。
    /// </summary>
    [DisallowMultipleComponent]
    public class ShooterPlaybackBinder : MonoBehaviour
    {
        public static ShooterPlaybackBinder Instance { get; private set; }

        [Tooltip("射手驱动；留空则自动在场景中查找")]
        [SerializeField] private ShooterDriver _shooter;

        [Tooltip("关闭后：球立即飞出，跳过射手前奏")]
        [SerializeField] private bool _enableIntro = true;

        [Tooltip("等待触球的超时（秒）；超时后强制放行，避免球卡住")]
        [SerializeField] private float _kickTimeoutSeconds = 6f;

        [Tooltip("打印接管、放行与取消日志")]
        [SerializeField] private bool _logEvents = true;

        private Action _pendingPlayBall;
        private float _pendingSince;
        private bool _pending;

        public bool IsWaitingForKick { get { return _pending; } }

        private void Awake()
        {
            if (_shooter == null) _shooter = FindShooter();
            Instance = this;
        }

        private void OnEnable()
        {
            Instance = this;
            ShotPlaybackGate.Interceptor = Intercept;
            ShotPlaybackGate.Canceller = CancelPending;
            ShotPlaybackGate.ResetHandler = ResetShooter;
        }

        private void OnDisable()
        {
            if (Instance == this) Instance = null;
            ShotPlaybackGate.Release(Intercept);
            ShotPlaybackGate.ReleaseCanceller(CancelPending);
            ShotPlaybackGate.ReleaseResetHandler(ResetShooter);

            // 被禁用时若仍在等待触球，必须放行，否则球永远不会飞出
            ReleasePending("绑定器被禁用");
        }

        /// <summary>把射手恢复到待机站立。由项目里的「复位」触发。</summary>
        public void ResetShooter()
        {
            CancelPending();
            if (_shooter != null) _shooter.StopAndReset();
            if (_logEvents) Debug.Log("[ShooterPlaybackBinder] 射手已复位到待机站立。", this);
        }

        private static ShooterDriver FindShooter()
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindFirstObjectByType<ShooterDriver>(FindObjectsInactive.Include);
#else
            return UnityEngine.Object.FindObjectOfType<ShooterDriver>(true);
#endif
        }

        /// <summary>由 ShotPlaybackGate 调用。返回 true 表示已接管。</summary>
        private bool Intercept(Action playBall)
        {
            if (!_enableIntro || _shooter == null || !_shooter.isActiveAndEnabled)
                return false;

            // 上一次请求尚未释放：丢弃它。
            // 这里必须用「取消」而不是「放行」——否则旧的一次射门会凭空飞出一颗球。
            CancelPending();

            _pendingPlayBall = playBall;
            _pendingSince = Time.realtimeSinceStartup;
            _pending = true;

            _shooter.Kick += OnKick;

            if (!_shooter.Play())
            {
                // 姿态数据缺失：立即回退为直接播放
                _shooter.Kick -= OnKick;
                _pending = false;
                _pendingPlayBall = null;
                if (_logEvents)
                    Debug.LogWarning("[ShooterPlaybackBinder] 射手无法播放（姿态数据缺失？），回退为立即发射。", this);
                return false;
            }

            if (_logEvents)
            {
                Debug.Log(string.Format(
                    "[ShooterPlaybackBinder] 接管球发射：先播放射手前奏，预计 {0:0.##}s 后触球。",
                    _shooter.KickTimeInIntro), this);
            }
            return true;
        }

        private void OnKick()
        {
            if (_logEvents) Debug.Log("[ShooterPlaybackBinder] 触球 —— 释放球。", this);
            ReleasePending("触球");
        }

        /// <summary>放行：调用 pending 的 playBall，让球飞出。</summary>
        private void ReleasePending(string reason)
        {
            if (!_pending) return;

            Action playBall = DetachPending();
            if (playBall == null) return;

            if (_logEvents) Debug.Log("[ShooterPlaybackBinder] 放行球（" + reason + "）。", this);
            playBall.Invoke();
        }

        /// <summary>取消：丢弃 pending 的 playBall，球不飞出。</summary>
        public void CancelPending()
        {
            if (!_pending) return;
            DetachPending();
            if (_logEvents) Debug.Log("[ShooterPlaybackBinder] 取消待发射的球。", this);
        }

        /// <summary>摘除 pending 状态并返回回调（不调用），同时让射手复位。</summary>
        private Action DetachPending()
        {
            _pending = false;
            if (_shooter != null) _shooter.Kick -= OnKick;

            Action playBall = _pendingPlayBall;
            _pendingPlayBall = null;
            return playBall;
        }

        private void Update()
        {
            if (!_pending) return;

            // 兜底：触球事件未到达（动画被中断、数据异常）时强制放行，避免球卡住
            if (Time.realtimeSinceStartup - _pendingSince > Mathf.Max(0.5f, _kickTimeoutSeconds))
            {
                Debug.LogWarning(
                    "[ShooterPlaybackBinder] 等待触球超时（" + _kickTimeoutSeconds + "s），强制放行球。", this);
                ReleasePending("超时");
            }
        }

        /// <summary>立即结束前奏并放行球（例如外部希望跳过前奏）。</summary>
        public void ForceRelease()
        {
            ReleasePending("外部强制放行");
        }

        public void SetIntroEnabled(bool value) { _enableIntro = value; }
    }
}
