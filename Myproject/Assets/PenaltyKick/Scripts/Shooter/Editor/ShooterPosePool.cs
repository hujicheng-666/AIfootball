using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>给射手挂上姿态池（原 5 个 + 镜像 5 个），使其每次开球随机换动作。</summary>
    public static class ShooterPosePool
    {
        private const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

        /// <summary>
        /// 姿态池：CMU Graphics Lab 动捕库里 subject 10/11 的六段真实踢球
        /// （mocap.cs.cmu.edu，官方标注 "soccer - kick ball"）。
        ///
        /// 与之前几批数据的区别：
        ///   · 3DSP 的 0000x 只有 600ms 标注窗口，单目估计在肩颈误差大，播到一半就定格；
        ///   · SYN17 是脚本按关节角度合成的，关节角对得上但重心与节奏始终差一口气；
        ///   · CMU 是光学动捕的真人全身姿态，髋、膝、踝的耦合关系天然自洽，
        ///     并且自带助跑位移（见 ShooterPoseFrame.hipX/hipZ），用真实的跑动带出射门。
        /// 每段都已裁剪成「助跑 1.25s + 触球 + 随挥 0.62s」约 1.9 秒。
        /// </summary>
        private static readonly string[] Pool =
        {
            "ShooterPoses/CMU_10_01",
            "ShooterPoses/CMU_10_02",
            "ShooterPoses/CMU_10_03",
            "ShooterPoses/CMU_10_05",
            "ShooterPoses/CMU_10_06",
            "ShooterPoses/CMU_11_01",
        };

        [MenuItem("AIfootball/动画/5. 设置射手姿态池")]
        public static void Apply()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[PosePool] 找不到 Shooter"); return; }

            var driver = shooter.GetComponent("ShooterDriver") as Component;
            if (driver == null) { Debug.LogError("[PosePool] 缺 ShooterDriver"); return; }

            var sb = new StringBuilder("[PosePool] 姿态池\n");
            var so = new SerializedObject(driver);
            var arr = so.FindProperty("_poseResourcePaths");
            if (arr == null) { Debug.LogError("[PosePool] 字段不存在，需先编译新代码"); return; }

            int valid = 0;
            arr.arraySize = Pool.Length;
            for (int i = 0; i < Pool.Length; i++)
            {
                arr.GetArrayElementAtIndex(i).stringValue = Pool[i];
                var ta = Resources.Load<TextAsset>(Pool[i]);
                if (ta != null) valid++;
                sb.AppendLine("   " + Pool[i] + (ta != null ? "  OK " + ta.text.Length + "B" : "  缺失!"));
            }
            so.ApplyModifiedProperties();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();
            sb.AppendLine("   有效 " + valid + "/" + Pool.Length + "，场景已保存");
            Debug.Log(sb.ToString());
        }
    }
}
