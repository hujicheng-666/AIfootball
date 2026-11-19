using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>盘点门将动画 clip 的实际内容：时长、根位移、肌肉曲线、首末帧姿态差异。</summary>
    public static class ClipAudit
    {
        [MenuItem("AIfootball/动画/盘点门将动作")]
        public static void Run()
        {
            string dir = "Assets/SuperGoalie/Animations/GoalKeeperAnimations";
            var files = System.IO.Directory.GetFiles(dir, "*.fbx");
            var sb = new StringBuilder("[ClipAudit] 门将动作盘点\n");

            foreach (var f in files)
            {
                string p = f.Replace('\\', '/');
                var clips = AssetDatabase.LoadAllAssetsAtPath(p);
                foreach (var o in clips)
                {
                    var clip = o as AnimationClip;
                    if (clip == null || clip.name.StartsWith("__preview")) continue;
                    sb.AppendLine("── " + System.IO.Path.GetFileNameWithoutExtension(p) + " / " + clip.name
                        + "  时长=" + clip.length.ToString("F2") + "s  帧率=" + clip.frameRate.ToString("F0")
                        + "  loop=" + clip.isLooping).AppendLine();

                    // 根位移范围
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    int nRot = 0, nPos = 0, nMuscle = 0;
                    Vector3 rootMin = Vector3.one * 999, rootMax = Vector3.one * -999;
                    foreach (var b in bindings)
                    {
                        if (b.type == typeof(Animator)) nMuscle++;
                        else if (b.propertyName == "m_LocalRotation") nRot++;
                        else if (b.propertyName == "m_LocalPosition") nPos++;

                        if (b.propertyName == "m_LocalPosition" &&
                            (b.path == "" || b.path.Equals("Hips", System.StringComparison.OrdinalIgnoreCase)))
                        {
                            var c = AnimationUtility.GetEditorCurve(clip, b);
                            if (c != null)
                                for (int i = 0; i < c.length; i++) { }
                        }
                    }
                    sb.AppendLine("   曲线 肌肉=" + nMuscle + " 旋转=" + nRot + " 位置=" + nPos + " 总=" + bindings.Length);

                    // 采样根节点位置（Hips 或有 path 的第一个位置曲线）
                    foreach (var b in bindings)
                    {
                        if (b.propertyName != "m_LocalPosition") continue;
                        if (!(b.path == "" || b.path.EndsWith("Hips"))) continue;
                        var c = AnimationUtility.GetEditorCurve(clip, b);
                        if (c == null) continue;
                        sb.AppendLine("   根曲线 " + (b.path == "" ? "(root)" : b.path) + "." + b.propertyName
                            + " 关键帧=" + c.length);
                        break;
                    }

                    // 首末帧姿态差（用肌肉曲线近似）
                    float diff = 0f; int cnt = 0;
                    foreach (var b in bindings)
                    {
                        if (b.type != typeof(Animator)) continue;
                        var c = AnimationUtility.GetEditorCurve(clip, b);
                        if (c == null || c.length < 2) continue;
                        diff += Mathf.Abs(c.Evaluate(0f) - c.Evaluate(clip.length));
                        cnt++;
                    }
                    if (cnt > 0) sb.AppendLine("   首末帧平均肌肉差=" + (diff / cnt).ToString("F4"));
                }
            }
            Debug.Log(sb.ToString());
        }
    }
}
