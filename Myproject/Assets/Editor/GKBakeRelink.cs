using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>把场景 GK Animator 重新挂上重建后的 GKBakedController 并保存。</summary>
public static class GKBakeRelink
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(
                "Assets/SuperGoalie/Animators/GKBakedController.controller");
            if (ctrl == null) throw new System.Exception("GKBakedController 不存在");

            int n = 0;
            foreach (var ani in Object.FindObjectsOfType<Animator>(true))
            {
                // GK 实例（GoalKeeper 祖先）全部重挂
                bool inGk = false;
                for (var p = ani.transform.parent; p != null; p = p.parent)
                    if (p.name.Contains("GoalKeeper") || p.name.StartsWith("GKCustom")) { inGk = true; break; }
                if (!inGk) continue;

                ani.runtimeAnimatorController = ctrl;
                ani.avatar = null;   // Generic rig 无需 avatar
                EditorUtility.SetDirty(ani);
                n++;
                log.AppendLine(ani.gameObject.name + " -> GKBakedController");
            }
            log.AppendLine("重挂数量: " + n);
            if (n == 0) log.AppendLine("!! 警告: 未找到 GK Animator");

            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene,
                "Assets/SuperGoalie/Scenes/Demo.unity");
            log.AppendLine("SaveScene: " + saved);
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_relink_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
