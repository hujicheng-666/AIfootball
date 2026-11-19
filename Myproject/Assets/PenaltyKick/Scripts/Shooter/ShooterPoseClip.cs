using System;
using UnityEngine;

namespace PenaltyKickPlatform.Shooter
{
    /// <summary>单帧姿态：关节坐标已扁平化为 17*3 的数组（Unity 坐标系，以髋为原点）。</summary>
    [Serializable]
    public class ShooterPoseFrame
    {
        public float t;
        public float[] p;
        /// <summary>髋关节相对触球点的水平位移（米）。动捕自带的助跑轨迹，供 ShooterDriver 驱动位移。</summary>
        public float hipX;
        public float hipZ;
    }

    /// <summary>
    /// 从 3DSP 导出的真实射门姿态片段（由 scripts/export_shooter_pose.py 生成）。
    ///
    /// 坐标系约定：与 Unity 一致（x=右, y=上, z=前），以髋关节为原点，
    /// 因此"面朝 +z"即为该球员的原始朝向。
    /// </summary>
    [Serializable]
    public class ShooterPoseClip
    {
        public string shotId;
        public float fps;
        /// <summary>触球帧（0-based），取自 SoccerNet 动作标注。</summary>
        public int kickFrame;
        /// <summary>触球帧的另一个估计：踝关节速度峰值（通常比标注早 2~4 帧）。</summary>
        public int kickFrameKinematic;
        public string[] jointNames;
        public ShooterPoseFrame[] frames;
        /// <summary>
        /// 触球瞬间「髋到球」的距离（米）。动捕数据里脚在髋前方约 0.75m 击球，
        /// 用各段实测值比场景里写死一个数更准 —— 不同 trial 的站位本来就不一样。
        /// </summary>
        public float standOffset;

        public int FrameCount { get { return frames != null ? frames.Length : 0; } }

        public float Duration
        {
            get { return FrameCount > 1 ? frames[FrameCount - 1].t : 0f; }
        }

        public static ShooterPoseClip FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return JsonUtility.FromJson<ShooterPoseClip>(json);
        }

        /// <summary>按名字查找关节索引；找不到返回 -1。</summary>
        public int IndexOfJoint(string jointName)
        {
            if (jointNames == null) return -1;
            for (int i = 0; i < jointNames.Length; i++)
                if (jointNames[i] == jointName) return i;
            return -1;
        }

        /// <summary>取某帧某关节的位置（角色局部空间）。越界自动钳制。</summary>
        public Vector3 GetJoint(int frameIndex, int jointIndex)
        {
            if (FrameCount == 0 || jointIndex < 0) return Vector3.zero;
            ShooterPoseFrame f = frames[Mathf.Clamp(frameIndex, 0, FrameCount - 1)];
            int o = jointIndex * 3;
            if (f.p == null || o + 2 >= f.p.Length) return Vector3.zero;
            return new Vector3(f.p[o], f.p[o + 1], f.p[o + 2]);
        }

        /// <summary>按时间（秒，相对片段起点）线性插值取关节位置。</summary>
        public Vector3 SampleJoint(float time, int jointIndex)
        {
            if (FrameCount == 0 || jointIndex < 0) return Vector3.zero;
            if (FrameCount == 1) return GetJoint(0, jointIndex);

            float clamped = Mathf.Clamp(time, frames[0].t, frames[FrameCount - 1].t);

            // 帧数很少（20 帧），线性扫描足够快且避免索引分支错误
            for (int i = 0; i < FrameCount - 1; i++)
            {
                if (clamped > frames[i + 1].t) continue;
                float span = frames[i + 1].t - frames[i].t;
                float k = span > 1e-6f ? (clamped - frames[i].t) / span : 0f;
                return Vector3.Lerp(GetJoint(i, jointIndex), GetJoint(i + 1, jointIndex), k);
            }
            return GetJoint(FrameCount - 1, jointIndex);
        }

        /// <summary>片段是否带根位移（髋关节水平轨迹）。老片段没有该数据，走程序化助跑。</summary>
        public bool HasRootMotion
        {
            get
            {
                if (frames == null || frames.Length < 2) return false;
                for (int i = 0; i < frames.Length; i++)
                    if (frames[i].hipX != 0f || frames[i].hipZ != 0f) return true;
                return false;
            }
        }

        /// <summary>按时间取髋关节水平位移（x, z，米；原点在触球帧）。</summary>
        public Vector2 SampleHip(float time)
        {
            if (FrameCount == 0) return Vector2.zero;
            if (FrameCount == 1) return new Vector2(frames[0].hipX, frames[0].hipZ);

            float clamped = Mathf.Clamp(time, frames[0].t, frames[FrameCount - 1].t);
            for (int i = 0; i < FrameCount - 1; i++)
            {
                if (clamped > frames[i + 1].t) continue;
                float span = frames[i + 1].t - frames[i].t;
                float k = span > 1e-6f ? (clamped - frames[i].t) / span : 0f;
                return Vector2.Lerp(
                    new Vector2(frames[i].hipX, frames[i].hipZ),
                    new Vector2(frames[i + 1].hipX, frames[i + 1].hipZ), k);
            }
            return new Vector2(frames[FrameCount - 1].hipX, frames[FrameCount - 1].hipZ);
        }
    }
}
