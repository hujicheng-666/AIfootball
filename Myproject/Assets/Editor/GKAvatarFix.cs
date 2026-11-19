using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 第三轮修复（用户反馈：守门员肢体扭曲+陷入地下、射手原地助跑+两段踢球）：
///   1. GK avatar 改由 ModelImporter 自动骨映射生成（手工 BuildHumanAvatar 的 muscle
///      空间错误 → retarget 扭曲/陷地；importer 自动映射对标准骨名最可靠）；
///   2. 场景中 GKCustom 相关 Animator 换用新自动 avatar；
///   3. ShooterDriver 助跑参数加大（0.85m/0.95s 太短 → 2.2m/1.3s，真实助跑感）；
///   4. 保存 Demo.unity 并验证。
/// 射手"两段踢球"由 JSON 重新裁剪解决（已单独处理）。
/// </summary>
public static class GKAvatarFix
{
    const string FBX = "Assets/SuperGoalie/Meshes/Characters/GKCustom_GK2.fbx";
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            // ── 1. FBX importer 自动映射生成 Humanoid avatar ──
            var mi = AssetImporter.GetAtPath(FBX) as ModelImporter;
            if (mi == null) throw new System.Exception("FBX importer 不存在: " + FBX);
            mi.animationType = (ModelImporterAnimationType)3;   // Humanoid
            mi.sourceAvatar = null;          // 清掉手工 avatar，让 Unity 自动骨映射
            mi.optimizeGameObjects = false;
            mi.SaveAndReimport();
            log.AppendLine("FBX 已以自动映射重新导入 (Humanoid)");

            var autoAvatar = AssetDatabase.LoadAllAssetsAtPath(FBX)
                .OfType<Avatar>().FirstOrDefault(a => a.isHuman);
            if (autoAvatar == null || !autoAvatar.isValid)
                throw new System.Exception("importer 自动 avatar 生成失败");
            log.AppendLine("自动 avatar: " + autoAvatar.name + " isHuman=" + autoAvatar.isHuman);

            // ── 2. 场景：GKCustom 相关 Animator 换 avatar + 射手助跑参数 ──
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            int fixedGk = 0;
            foreach (var ani in Object.FindObjectsOfType<Animator>(true))
            {
                if (ani.avatar == null) continue;
                bool isOldManual = ani.avatar.name.Contains("GKCustomAvatar");
                bool isGkCustomInstance = ani.runtimeAnimatorController != null
                    && ani.runtimeAnimatorController.name.Contains("GoalKeeperAnimator001");
                if (!isOldManual && !isGkCustomInstance) continue;

                ani.avatar = autoAvatar;
                EditorUtility.SetDirty(ani);
                fixedGk++;
                log.AppendLine("GK Animator '" + ani.name + "' -> 自动 avatar");
            }
            log.AppendLine("GK Animator 修改数: " + fixedGk);
            if (fixedGk == 0) log.AppendLine("!! 警告: 未找到需要换 avatar 的 GK Animator");

            int fixedSh = 0;
            foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "ShooterDriver") continue;
                var so = new SerializedObject(mb);
                var dist = so.FindProperty("_runUpDistance");
                var dur = so.FindProperty("_runUpDuration");
                if (dist == null || dur == null) { log.AppendLine("!! ShooterDriver 字段缺失"); continue; }
                dist.floatValue = 2.2f;
                dur.floatValue = 1.3f;
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(mb);
                fixedSh++;
                log.AppendLine("ShooterDriver 助跑参数 -> 2.2m / 1.3s (" + mb.gameObject.name + ")");
            }
            log.AppendLine("ShooterDriver 修改数: " + fixedSh);

            // ── 3. 保存（反射赋值 + SetDirty + 显式 SaveScene，已验证可持久化）──
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            log.AppendLine("SaveScene: " + saved);
        }
        catch (System.Exception e)
        {
            log.AppendLine("!! 异常: " + e);
        }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_avatar_fix_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
