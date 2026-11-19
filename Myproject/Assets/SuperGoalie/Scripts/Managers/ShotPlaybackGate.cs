using System;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Managers
{
    /// <summary>
    /// 球发射的「闸门」：允许外部系统在球真正飞出之前先播放一段表现（例如射手助跑与摆腿）。
    ///
    /// 为什么不直接调用 Ball.PlayTrajectory
    /// ------------------------------------
    /// 射手属于 PenaltyKick 层，而球与门将属于 SuperGoalie 层。若让 GameManager 直接认识射手，
    /// 依赖方向就反了。用这个不含任何外部依赖的静态闸门做中转，两边互不认识：
    ///
    ///   • 调用方（GameManager / PenaltyKickApp）：
    ///       ShotPlaybackGate.Request(() => _ball.PlayTrajectory(traj, target));
    ///
    ///   • 接管方（射手绑定器）：
    ///       ShotPlaybackGate.Interceptor = playBall => { 播放前奏; 触球时 playBall(); return true; };
    ///
    /// 没有接管方时，行为与改动前完全一致。
    /// </summary>
    public static class ShotPlaybackGate
    {
        /// <summary>
        /// 拦截器。返回 true 表示已接管——接管者**必须**自行在合适时机调用传入的 playBall，
        /// 否则球永远不会飞出。
        /// </summary>
        public static Func<Action, bool> Interceptor;

        /// <summary>
        /// 由接管者注册：请求**丢弃**尚未释放的播放（例如用户在前奏期间点了停止）。
        /// 与「放行」不同，取消不应触发 playBall。
        /// </summary>
        public static Action Canceller;

        /// <summary>
        /// 由接管者注册：请求把射手一类的外部对象复位到待机状态
        /// （例如用户点了项目里的「复位」）。
        /// </summary>
        public static Action ResetHandler;

        public static bool HasInterceptor { get { return Interceptor != null; } }

        /// <summary>请求播放球。若被接管则交由接管者决定何时真正播放。</summary>
        public static void Request(Action playBall)
        {
            if (playBall == null) return;

            Func<Action, bool> handler = Interceptor;
            if (handler != null)
            {
                bool handled = false;
                try
                {
                    handled = handler(playBall);
                }
                catch (Exception exception)
                {
                    // 接管失败不应吞掉播放：回退到立即播放
                    Debug.LogException(exception);
                    handled = false;
                }
                if (handled) return;
            }

            playBall();
        }

        /// <summary>请求取消尚未释放的球播放（例如复位、停止）。无接管者时无副作用。</summary>
        public static void Cancel()
        {
            Action handler = Canceller;
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>由接管者调用，安全地注销（避免误清掉别的接管者）。</summary>
        public static void Release(Func<Action, bool> handler)
        {
            if (Interceptor == handler) Interceptor = null;
        }

        /// <summary>由接管者调用，注销取消回调。</summary>
        public static void ReleaseCanceller(Action handler)
        {
            if (Canceller == handler) Canceller = null;
        }

        /// <summary>
        /// 复位：先丢弃尚未释放的播放，再让接管者把外部对象（射手）恢复待机。
        /// 项目里的「复位」按钮应当调用它。
        /// </summary>
        public static void ResetAll()
        {
            Cancel();

            Action handler = ResetHandler;
            if (handler == null) return;
            try
            {
                handler();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }
        }

        /// <summary>由接管者调用，注销复位回调。</summary>
        public static void ReleaseResetHandler(Action handler)
        {
            if (ResetHandler == handler) ResetHandler = null;
        }
    }
}
