using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 清理 GoalKeeper002 下重叠的多个 Character，只保留真正的 GKCustom 模型（含 metarig 骨架）。
///
/// 背景：诊断发现 GoalKeeper002 下压了 4 个同名 Character，全部叠在同一位置 (0,0,55.09)：
///   #1 GoalkeeperRig2_Mesh（原版模型，关闭）
///   #2 GKCustom 7 个 SMR + metarig  ← 唯一正确的守门员（关闭状态，从未显示过）
///   #3 GoalkeeperRig_Mesh（原版模型，关闭）
///   #4 ShooterBody（射手网格副本，**唯一激活**）
/// 运行时球门里站着的其实是射手模型的副本，且被 100 倍缩放压塌 → 「守门员大部分在地下」。
/// </summary>
public static class GKFixFinal
{
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    public static void Run()
    {
        var log = new StringBuilder();
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        Transform gkRoot = null;
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            if (t.name == "GoalKeeper002") { gkRoot = t; break; }

        if (gkRoot == null) { log.AppendLine("!! 未找到 GoalKeeper002"); Flush(log); return; }

        var chars = new List<Transform>();
        foreach (Transform c in gkRoot) if (c.name == "Character") chars.Add(c);
        log.AppendLine("GoalKeeper002 下 Character 数量: " + chars.Count);

        // 判定：含 metarig 子节点 且 SMR 数 >= 5 → 正确的 GKCustom
        Transform keep = null;
        int bestScore = -1;
        foreach (var c in chars)
        {
            int smr = c.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            int meta = c.GetComponentsInChildren<Transform>(true).Count(t => t.name == "metarig");
            bool hasHips = c.GetComponentsInChildren<Transform>(true).Any(t => t.name.ToLower() == "hips");
            int score = smr * 10 + meta * 100 + (hasHips ? 0 : 50);
            log.AppendLine(string.Format("  {0}: SMR={1} metarig={2} Hips={3} active={4} -> score {5}",
                c.GetSiblingIndex() + ":" + c.name, smr, meta, hasHips, c.gameObject.activeSelf, score));
            if (score > bestScore) { bestScore = score; keep = c; }
        }

        if (keep == null) { log.AppendLine("!! 没找到可保留的 Character"); Flush(log); return; }
        log.AppendLine(">> 保留: siblingIndex=" + keep.GetSiblingIndex() + " SMR=" +
            keep.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length);

        // 先记录下来要删的（改名前标记，避免重复找到）
        var toDelete = chars.Where(c => c != keep).ToList();
        var keepAnimator = keep.GetComponent<Animator>();

        // 修正 GoalKeeper 组件的 _animator 引用
        foreach (var mb in gkRoot.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            var so = new SerializedObject(mb);
            var prop = so.FindProperty("_animator");
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) continue;
            var old = prop.objectReferenceValue;
            log.AppendLine("  修正 " + mb.GetType().Name + "._animator: 旧=" +
                (old == null ? "NULL" : (old is Animator a ? (a.transform == keep ? "keep" : "其他Character") : "?")));
            prop.objectReferenceValue = keepAnimator;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mb);
        }

        // 修正 _modelRoot（若有）
        foreach (var mb in gkRoot.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            var so = new SerializedObject(mb);
            var prop = so.FindProperty("_modelRoot");
            if (prop == null || prop.propertyType != SerializedPropertyType.ObjectReference) continue;
            log.AppendLine("  " + mb.GetType().Name + "._modelRoot -> " + keep.name);
            prop.objectReferenceValue = keep;
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mb);
        }

        // 删除多余 Character（先记录名字，销毁后不能访问）
        foreach (var c in toDelete)
        {
            int smr = c.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            string info = " 删除 siblingIndex=" + c.GetSiblingIndex() + " SMR=" + smr;
            Object.DestroyImmediate(c.gameObject, true);
            log.AppendLine(info);
        }

        keep.gameObject.SetActive(true);
        EditorUtility.SetDirty(keep.gameObject);

        // 保留的 Character 上若有 Animator，确保 anchor/avatar 正确
        if (keepAnimator != null)
        {
            log.AppendLine("  保留 Character Animator: ctrl=" +
                (keepAnimator.runtimeAnimatorController == null ? "NULL" : keepAnimator.runtimeAnimatorController.name) +
                " avatar=" + (keepAnimator.avatar == null ? "NULL" : keepAnimator.avatar.name) +
                " rootMotion=" + keepAnimator.applyRootMotion);
            // 根运动必须关闭：位移由脚本负责，否则会被 avatar 的根运动再推一次
            keepAnimator.applyRootMotion = false;
            EditorUtility.SetDirty(keepAnimator);
        }

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
        log.AppendLine("SaveScene: " + saved);

        // 立即回读验证
        var scene2 = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        int remain = 0;
        foreach (var t in Object.FindObjectsOfType<Transform>(true))
            if (t.name == "Character") remain++;
        log.AppendLine("回读验证：场景内 Character 总数 = " + remain);

        Flush(log);
    }

    static void Flush(StringBuilder log)
    {
        System.IO.File.WriteAllText(@"D:\AIfootball\gk_fix_final.txt", log.ToString());
        EditorApplication.Exit(0);
    }
}
