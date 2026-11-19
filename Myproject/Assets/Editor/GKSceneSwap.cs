using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Linq;

/// <summary>
/// Demo 场景守门员模型替换：禁用旧 Character 子物体，挂 GKCustomRig，重定向引用。
/// CLI: Unity.exe -projectPath ... -executeMethod GKSceneSwap.RunCli -batchmode -quit
/// </summary>
public static class GKSceneSwap
{
    public static void RunCli()
    {
        try
        {
            Run();
            EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[GKSceneSwap] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Football/GK Scene Swap")]
    public static void Run()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/SuperGoalie/Meshes/Characters/GKCustom/GKCustomRig.prefab");
        if (prefab == null) throw new System.Exception("GKCustomRig.prefab missing — 先跑 GKImportSetup");

        var scene = EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity");

        // 找挂 GoalKeeper 的实体
        MonoBehaviour gkComp = null;
        foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            if (mb.GetType().Name == "GoalKeeper") { gkComp = mb; break; }
        if (gkComp == null) throw new System.Exception("GoalKeeper component not found in scene");
        var gkGo = gkComp.gameObject;
        Debug.Log("[GKSceneSwap] keeper entity: " + gkGo.name);

        // 读取 Height 属性（用于缩放校正）
        float keeperHeight = 1.9f;
        var soGk = new SerializedObject(gkComp);
        var heightProp = soGk.FindProperty("_height");
        if (heightProp != null) keeperHeight = heightProp.floatValue;

        // 禁用所有旧 Character 子模型，记录激活的那个的本地变换作为基准
        Transform reference = null;
        foreach (Transform child in gkGo.transform)
        {
            if (child.name != "Character") continue;
            if (child.gameObject.activeSelf) reference = child;
            child.gameObject.SetActive(false);
        }
        Vector3 refPos = reference != null ? reference.localPosition : Vector3.zero;
        Quaternion refRot = reference != null ? reference.localRotation : Quaternion.identity;
        float refScale = reference != null ? reference.localScale.x : 1f;
        Debug.Log("[GKSceneSwap] reference old model: pos=" + refPos + " scale=" + refScale);

        // 旧模型当前实际身高（由包围盒估算）以换算缩放
        float oldHeight = 1.9f;
        if (reference != null)
        {
            var rends = reference.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var bounds = rends[0].bounds;
                foreach (var r in rends) bounds.Encapsulate(r.bounds);
                oldHeight = bounds.size.y;
            }
        }
        float scale = refScale * (keeperHeight / oldHeight);
        Debug.Log("[GKSceneSwap] oldHeight=" + oldHeight + " keeperHeight=" + keeperHeight + " => scale=" + scale);

        // 挂入新模型(防重: 已有 GKCustom 实例就只更新变换)
        Transform existing = null;
        foreach (Transform child in gkGo.transform)
        {
            if (child.name != "Character") continue;
            var anim = child.GetComponentInChildren<Animator>(true);
            if (anim != null && anim.runtimeAnimatorController != null &&
                anim.runtimeAnimatorController.name.Contains("GKCustomController"))
            { existing = child; break; }
        }
        GameObject newModel;
        if (existing != null)
        {
            newModel = existing.gameObject;
            newModel.transform.localPosition = refPos;
            newModel.transform.localRotation = refRot;
            newModel.transform.localScale = Vector3.one * scale;
            newModel.gameObject.SetActive(true);
            Debug.Log("[GKSceneSwap] existing GKCustom instance updated");
        }
        else
        {
            newModel = (GameObject)PrefabUtility.InstantiatePrefab(prefab, gkGo.transform);
            newModel.name = "Character";
            newModel.transform.localPosition = refPos;
            newModel.transform.localRotation = refRot;
            newModel.transform.localScale = Vector3.one * scale;
            newModel.gameObject.SetActive(true);
        }

        // 重定向 GoalKeeper 的 _animator/_modelRoot
        var newAnimator = newModel.GetComponent<Animator>();
        if (newAnimator == null) throw new System.Exception("new model has no Animator");
        soGk.FindProperty("_animator").objectReferenceValue = newAnimator;
        soGk.FindProperty("_modelRoot").objectReferenceValue = newModel.transform;
        soGk.ApplyModifiedProperties();

        // 挂 GKMotionSelector（没有才加）
        if (gkGo.GetComponent(gkComp.GetType().Assembly.GetType(
                "Assets.SuperGoalie.Scripts.Entities.GKMotionSelector")) == null)
        {
            var t = gkComp.GetType().Assembly.GetType("Assets.SuperGoalie.Scripts.Entities.GKMotionSelector");
            gkGo.AddComponent(t);
            Debug.Log("[GKSceneSwap] GKMotionSelector added");
        }

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[GKSceneSwap] scene saved");
    }
}
