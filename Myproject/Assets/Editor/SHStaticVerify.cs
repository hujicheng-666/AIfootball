using System;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Edit 模式静态验证：手动执行 ShooterDriver 的完整驱动链
/// （LoadClip → SetClip → CaptureRestPose → SetRestBlend(0) → SetTime → ApplyPose），
/// 采样骨骼世界位置变化，并打印 Retargeter/Animator 的全部状态。
/// 结果写 D:/AIfootball/sh_static_diag.txt。
/// </summary>
public static class SHStaticVerify
{
    public static void Run()
    {
        var sb = new StringBuilder();
        try
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);

            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true).ToList();
            var driver = all.FirstOrDefault(x => x.GetType().Name == "ShooterDriver");
            var ret = all.FirstOrDefault(x => x.GetType().Name == "ShooterRetargeter");
            if (driver == null || ret == null) { sb.AppendLine("!! 组件缺失"); return; }

            sb.AppendLine("Shooter 对象: " + driver.gameObject.name
                + " activeSelf=" + driver.gameObject.activeSelf
                + " activeInHierarchy=" + driver.gameObject.activeInHierarchy);

            // Retargeter 序列化状态
            var tRet = ret.GetType();
            var aniSer = new SerializedObject(ret).FindProperty("_animator").objectReferenceValue as Animator;
            sb.AppendLine("Retargeter._animator(序列化): " + (aniSer == null ? "NULL" : aniSer.name));
            if (aniSer != null)
            {
                sb.AppendLine("  enabled=" + aniSer.enabled
                    + " activeInHierarchy=" + aniSer.gameObject.activeInHierarchy
                    + " avatar=" + (aniSer.avatar == null ? "NULL" : aniSer.avatar.name)
                    + " ctrl=" + (aniSer.runtimeAnimatorController == null ? "null" : aniSer.runtimeAnimatorController.name));
            }

            // 采集 rest 并检查骨骼
            tRet.GetMethod("CaptureRestPose").Invoke(ret, null);
            var bones = tRet.GetField("_bones", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(ret) as Transform[];
            int missing = 0;
            if (bones != null) foreach (var b in bones) if (b == null) missing++;
            sb.AppendLine("骨骼链缺失: " + missing + "/" + (bones == null ? 0 : bones.Length));
            if (bones != null)
                for (int i = 0; i < bones.Length; i++)
                    sb.AppendLine("  chain[" + i + "] " + tRet.GetField("_bones", BindingFlags.NonPublic | BindingFlags.Instance) + " = " + (bones[i] == null ? "NULL" : bones[i].name));

            // 加载姿态数据（走 ShooterDriver 私有 LoadClipIfNeeded）
            var tDrv = driver.GetType();
            tDrv.GetMethod("LoadClipIfNeeded", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(driver, null);
            var clip = tDrv.GetProperty("Clip")?.GetValue(driver, null);
            sb.AppendLine("Driver.Clip: " + (clip == null ? "NULL(数据加载失败)" : clip.GetType().Name));

            // 手动驱动：restBlend=0, t=kickTime, ApplyPose
            var hand = aniSer != null ? aniSer.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            var foot = aniSer != null ? aniSer.GetBoneTransform(HumanBodyBones.RightFoot) : null;
            Vector3 hand0 = hand != null ? hand.position : Vector3.zero;
            Vector3 foot0 = foot != null ? foot.position : Vector3.zero;

            if (clip != null)
            {
                tRet.GetMethod("SetClip").Invoke(ret, new object[] { clip });
                tRet.GetMethod("SetRestBlend").Invoke(ret, new object[] { 0f });
                // kickTime
                var kickTimeInIntro = tDrv.GetProperty("KickTimeInIntro");
                float kt = (float)kickTimeInIntro.GetValue(driver, null) - 0.95f - 0.3f; // 减去 hold+runUp → 触球片段时刻
                tRet.GetMethod("SetTime").Invoke(ret, new object[] { Mathf.Max(0f, kt) });
                tRet.GetMethod("ApplyPose").Invoke(ret, null);
                sb.AppendLine("ApplyPose(t=" + kt.ToString("0.##") + ") 已执行");
            }

            Vector3 hand1 = hand != null ? hand.position : Vector3.zero;
            Vector3 foot1 = foot != null ? foot.position : Vector3.zero;
            sb.AppendLine("Hand 移动: " + Vector3.Distance(hand0, hand1).ToString("F3") + " m  (" + hand0.ToString("F2") + " -> " + hand1.ToString("F2") + ")");
            sb.AppendLine("Foot 移动: " + Vector3.Distance(foot0, foot1).ToString("F3") + " m");
            sb.AppendLine("结论: " + (Vector3.Distance(hand0, hand1) > 0.05f || Vector3.Distance(foot0, foot1) > 0.05f
                ? "Edit 模式下 ApplyPose 有效果（问题在运行时覆盖源）"
                : "Edit 模式下 ApplyPose 无效果（骨骼映射/初始化问题）"));
        }
        catch (Exception e)
        {
            sb.AppendLine("!! 异常: " + e);
        }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\sh_static_diag.txt", sb.ToString());
            EditorApplication.Exit(0);
        }
    }
}
