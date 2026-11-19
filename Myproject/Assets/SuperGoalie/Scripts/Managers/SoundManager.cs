using System;
using Patterns.Singleton;
using UnityEngine;

namespace Assets.SuperGoalie.Scripts.Managers
{
    public class SoundManager : Singleton<SoundManager>
    {
        public AudioSource _ballKickAS;

        public AudioSource _goalAS;

        public AudioSource _matchAmbience;

        /// <summary>
        /// 音效总开关。
        ///
        /// 这个演示端最终是嵌在 WPF 主界面里跑的：观众环境声、踢球声、进球欢呼
        /// 会和标注时的操作反馈混在一起，用户分不清声音是"程序放的"还是"视频里的"。
        /// 默认关闭；需要声音时把这里改成 true 即可。
        /// </summary>
        private static readonly bool SoundEnabled = false;

        public override void Awake()
        {
            base.Awake();

            if (SoundEnabled) return;

            // AudioListener 是送往声卡前的最后一道闸：即便某个 AudioSource 在别处被唤醒
            // （prefab 上 FootballAmbienceAS 就是 playOnAwake = 1），也不会真的出声。
            AudioListener.volume = 0f;

            if (_matchAmbience != null)
            {
                _matchAmbience.playOnAwake = false;
                _matchAmbience.Stop();
            }
        }

        private void Start()
        {
            // Awake 早于 AudioSource 的 playOnAwake 自动播放，这里再停一次兜底
            if (SoundEnabled || _matchAmbience == null) return;
            _matchAmbience.Stop();
        }

        public void PlayBallKickedSound()
        {
            if (!SoundEnabled) return;
            if (_ballKickAS != null)
                _ballKickAS.Play();
        }

        public void PlayBallKickedSound(float flightTime, float velocity, Vector3 initial, Vector3 target)
        {
            if (!SoundEnabled) return;
            if (_ballKickAS != null)
                _ballKickAS.Play();
        }

        public void PlayGoalScoredSound()
        {
            if (!SoundEnabled) return;
            if (_goalAS != null)
                _goalAS.Play();
        }
    }
}
