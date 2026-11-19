using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 守门员最终方案（对照原版实现还原）：
/// 原版 controller 的 Dive/TendGoal/Idle 状态 motion 全为空——原版"扑救"是程序化移动+Humanoid IK，
/// 不是动画 clip。唯一有动画的 WakeupNoBall（爬起）正是 muscle 动画在 GKCustom 上扭曲陷地的元凶，
/// 已在 GKHumanController 中清空。
/// 本脚本：1) GKCustom FBX 切 Humanoid（importer 自动 avatar，IK 需要）；
///         2) 场景 GK Animator 挂 GKHumanController + 自动 avatar。
/// 不播放任何 muscle 动画 → 不陷地不扭曲；扑救表现与原版一致（程序化+IK）。
/// </summary>
public static class GKFinalSetup
{
    const string FBX = "Assets/SuperGoalie/Meshes/Characters/GKCustom_GK2.fbx";
    const string Ctrl = "Assets/SuperGoalie/Animators/GKHumanController.controller";
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            // 1. FBX → Humanoid 自动 avatar
            var mi = AssetImporter.GetAtPath(FBX) as ModelImporter;
            mi.animationType = (ModelImporterAnimationType)3;   // Humanoid
            mi.sourceAvatar = null;                              // importer 自动骨映射
            mi.optimizeGameObjects = false;
            mi.SaveAndReimport();
            var avatar = AssetDatabase.LoadAllAssetsAtPath(FBX)
                .OfType<Avatar>().FirstOrDefault(a => a.isHuman);
            if (avatar == null || !avatar.isValid) throw new System.Exception("自动 avatar 失败");
            log.AppendLine("FBX Humanoid 自动 avatar: " + avatar.name);

            // 2. 场景
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Ctrl);
            if (ctrl == null) throw new System.Exception("GKHumanController 不存在");

            int n = 0;
            foreach (var ani in Object.FindObjectsOfType<Animator>(true))
            {
                bool inGk = false;
                for (var p = ani.transform.parent; p != null; p = p.parent)
                    if (p.name.Contains("GoalKeeper") || p.name.StartsWith("GKCustom")) { inGk = true; break; }
                if (!inGk) continue;

                ani.runtimeAnimatorController = ctrl;
                ani.avatar = avatar;
                EditorUtility.SetDirty(ani);
                n++;
                log.AppendLine(ani.gameObject.name + " -> GKHumanController + " + avatar.name);
            }
            log.AppendLine("修改数: " + n);
            if (n == 0) log.AppendLine("!! 未找到 GK Animator");

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            log.AppendLine("SaveScene: " + saved);
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_final_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
