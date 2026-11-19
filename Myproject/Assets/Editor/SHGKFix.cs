using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 一次性修复脚本：
/// 1. ShooterRetargeter._animator 改绑激活的新模型 Shooter/Character（SHCustomAvatar），
///    旧值指向禁用的 Shooter/Model（ShooterRig2Avatar），导致 12 根骨骼全找不到、ApplyPose 空转；
/// 2. 读取 GKCustom_GK2.fbx 中 10 个扑救动作 clip 的真实时长，写入场景 GKMotionSelector.ClipLengths，
///    供运行时把扑救动作的触手瞬间对齐球的到达时间。
/// </summary>
public static class SHGKFix
{
    private static readonly string[] MotionOrder =
    {
        "GK_12_CatchLow",         // 0
        "GK_11_CatchMedium",      // 1
        "GK_13_CatchJumpHigh",    // 2
        "GK_18_DivingSaveLeft",   // 3
        "GK_04_BodyBlockLeft",    // 4
        "GK_20_CatchRunJumpHigh", // 5
        "GK_15_DivingSaveRight",  // 6
        "GK_07_BodyBlockRight",   // 7
        "GK_03_Miss",             // 8
        "GK_19_Scoop",            // 9
    };

    [MenuItem("Tools/修复射手引用与GK动作时长")]
    public static void Run()
    {
        var scene = EditorSceneManager.OpenScene(
            "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
        var log = new System.Text.StringBuilder();

        // ── 1. 修射手 Retargeter 的 _animator（反射赋值，绕过 SerializedObject 在 batchmode 下不持久化的坑）──
        foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetType().Name != "ShooterRetargeter") continue;

            // 激活的新模型：父级 Shooter 下 activeSelf 的 Character（SHCustomAvatar）
            Animator target = null;
            foreach (Transform child in mb.transform)
            {
                if (!child.gameObject.activeSelf) continue;
                var ani = child.GetComponent<Animator>();
                if (ani != null && ani.avatar != null && ani.avatar.name.Contains("SHCustom"))
                    target = ani;
            }

            if (target == null)
            {
                log.AppendLine("!! 未找到激活的 SHCustomAvatar Animator，保持原值");
            }
            else
            {
                var field = mb.GetType().GetField("_animator",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                field.SetValue(mb, target);
                EditorUtility.SetDirty(mb);
                log.AppendLine("ShooterRetargeter._animator 反射赋值 -> " + target.name
                    + " (" + target.avatar.name + ")，读回=" + (field.GetValue(mb) as Animator)?.name);
            }
        }

        // ── 2. GK 动作时长 → 场景 GKMotionSelector.ClipLengths ──
        var fbx = AssetDatabase.LoadAssetAtPath<Object>("Assets/SuperGoalie/Meshes/Characters/GKCustom/GKCustom_GK2.fbx");
        if (fbx == null)
        {
            // 尝试其他路径
            var guids = AssetDatabase.FindAssets("GKCustom_GK2 t:Model");
            if (guids.Length > 0)
                fbx = AssetDatabase.LoadAssetAtPath<Object>(AssetDatabase.GUIDToAssetPath(guids[0]));
        }

        float[] lengths = new float[0];
        if (fbx != null)
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(fbx));
            if (importer != null)
            {
                lengths = new float[MotionOrder.Length];
                for (int i = 0; i < MotionOrder.Length; i++)
                {
                    var clip = importer.clipAnimations.FirstOrDefault(c => c.name.Contains(MotionOrder[i]));
                    // clipAnimations 没有直接时长；改用 AnimationClip 资源
                    var animClips = AssetDatabase.LoadAllAssetsAtPath(AssetDatabase.GetAssetPath(fbx))
                        .OfType<AnimationClip>()
                        .Where(c => !c.name.StartsWith("__preview__"))
                        .ToArray();
                    var match = animClips.FirstOrDefault(c => c.name.Contains(MotionOrder[i]));
                    lengths[i] = match != null ? match.length : 0f;
                    log.AppendLine((match != null
                        ? "GK clip " + MotionOrder[i] + ": " + lengths[i].ToString("0.###") + "s (" + match.name + ")"
                        : "GK clip " + MotionOrder[i] + ": 未找到"));
                }
            }
        }
        else
        {
            log.AppendLine("!! 未找到 GKCustom_GK2.fbx");
        }

        foreach (var mb in Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetType().Name != "GKMotionSelector") continue;
            var so = new SerializedObject(mb);
            var prop = so.FindProperty("ClipLengths");
            if (prop == null)
            {
                log.AppendLine("!! GKMotionSelector 没有 ClipLengths 字段（先更新运行时代码）");
                continue;
            }
            prop.arraySize = lengths.Length;
            for (int i = 0; i < lengths.Length; i++)
                prop.GetArrayElementAtIndex(i).floatValue = lengths[i];
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(mb);
            log.AppendLine("GKMotionSelector.ClipLengths 已写入场景对象 " + mb.gameObject.name);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene, "Assets/SuperGoalie/Scenes/Demo.unity");
        log.AppendLine("SaveScene 返回: " + saved);
        System.IO.File.WriteAllText(@"D:\AIfootball\gk_fix_log.txt", log.ToString());
        EditorApplication.Exit(0);
    }
}
