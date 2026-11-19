using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using System;
using System.Collections.Generic;
using System.Linq;
using PenaltyKickPlatform.Shooter;

/// <summary>
/// 射手新模型(FBX)导入配置 + 场景替换，与守门员 GKImportSetup 同一模式：
/// 1. Humanoid Avatar 显式映射(ShooterRetargeter 用 HumanBodyBones 查骨, Animator 本身不播动画)
/// 2. Hunyuan Shooter 贴图材质
/// 3. SHCustomRig prefab
/// 4. Demo.unity: Shooter 下旧 Model 禁用、挂新模型(按旧模型包围盒高度对齐缩放)、
///    ShooterDriver 姿态池指向 SH_PenaltyKick、跑步循环 SH_Jog
/// </summary>
public static class SHImportSetup
{
    const string FBX = "Assets/SuperGoalie/Meshes/Characters/SHCustom_SH.fbx";
    const string Dir = "Assets/SuperGoalie/Meshes/Characters/SHCustom";
    const string TexPath = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/Shooter/texture_pbr_20250901.png";
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    static readonly Dictionary<string, HumanBodyBones> BoneMap = new Dictionary<string, HumanBodyBones>
    {
        { "Hips", HumanBodyBones.Hips },
        { "Spine", HumanBodyBones.Spine },
        { "Chest", HumanBodyBones.Chest },
        { "Neck", HumanBodyBones.Neck },
        { "Head", HumanBodyBones.Head },
        { "LeftShoulder", HumanBodyBones.LeftShoulder },
        { "LeftUpperArm", HumanBodyBones.LeftUpperArm },
        { "LeftLowerArm", HumanBodyBones.LeftLowerArm },
        { "LeftHand", HumanBodyBones.LeftHand },
        { "RightShoulder", HumanBodyBones.RightShoulder },
        { "RightUpperArm", HumanBodyBones.RightUpperArm },
        { "RightLowerArm", HumanBodyBones.RightLowerArm },
        { "RightHand", HumanBodyBones.RightHand },
        { "LeftUpperLeg", HumanBodyBones.LeftUpperLeg },
        { "LeftLowerLeg", HumanBodyBones.LeftLowerLeg },
        { "LeftFoot", HumanBodyBones.LeftFoot },
        { "LeftToes", HumanBodyBones.LeftToes },
        { "RightUpperLeg", HumanBodyBones.RightUpperLeg },
        { "RightLowerLeg", HumanBodyBones.RightLowerLeg },
        { "RightFoot", HumanBodyBones.RightFoot },
        { "RightToes", HumanBodyBones.RightToes },
    };

    public static void RunCli()
    {
        try
        {
            Run();
            Debug.Log("[SHImportSetup] OK");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[SHImportSetup] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Football/SH Import Setup")]
    public static void Run()
    {
        if (!AssetDatabase.LoadAssetAtPath<GameObject>(FBX))
            throw new Exception("FBX not found at " + FBX);
        Directory.CreateDirectory(Dir);

        // ── 1. Humanoid Avatar 显式映射(Retargeter 靠它查骨) ──
        var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FBX);
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        var avatar = AvatarBuilder.BuildHumanAvatar(instance, BuildDescription(instance));
        if (avatar == null || !avatar.isValid)
            throw new Exception("Avatar build failed / invalid");
        avatar.name = "SHCustomAvatar";
        AssetDatabase.CreateAsset(avatar, Dir + "/SHCustomAvatar.asset");
        Debug.Log("[SHImportSetup] avatar built, isHuman=" + avatar.isHuman);
        UnityEngine.Object.DestroyImmediate(instance);

        // ── 2. FBX 导入设置 ──
        var mi = AssetImporter.GetAtPath(FBX) as ModelImporter;
        mi.animationType = (ModelImporterAnimationType)3;   // Humanoid
        mi.sourceAvatar = AssetDatabase.LoadAssetAtPath<Avatar>(Dir + "/SHCustomAvatar.asset");
        mi.optimizeGameObjects = false;
        mi.SaveAndReimport();

        // ── 3. 材质 ──
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
        if (tex == null) throw new Exception("shooter texture missing: " + TexPath);
        var matPath = Dir + "/SHCustom_Mat.asset";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
        if (mat == null)
        {
            mat = new Material(Shader.Find("Standard"));
            mat.name = "SHCustom_Mat";
            AssetDatabase.CreateAsset(mat, matPath);
        }
        mat.SetTexture("_MainTex", tex);
        mat.SetFloat("_Glossiness", 0.15f);
        EditorUtility.SetDirty(mat);

        // ── 4. Prefab: Animator(Avatar, 无 controller) + 材质 ──
        var prefab = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        var animator = prefab.GetComponent<Animator>();
        if (animator == null) animator = prefab.AddComponent<Animator>();
        animator.avatar = AssetDatabase.LoadAssetAtPath<Avatar>(Dir + "/SHCustomAvatar.asset");
        animator.runtimeAnimatorController = null;   // Retargeter 程序化驱动, 不播 clip
        foreach (var rend in prefab.GetComponentsInChildren<Renderer>())
            rend.sharedMaterial = mat;
        PrefabUtility.SaveAsPrefabAsset(prefab, Dir + "/SHCustomRig.prefab");
        UnityEngine.Object.DestroyImmediate(prefab);
        AssetDatabase.SaveAssets();

        // ── 5. 场景替换 + 姿态池 ──
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var driver = UnityEngine.Object.FindObjectOfType<ShooterDriver>(true);
        if (driver == null) throw new Exception("ShooterDriver not found in " + ScenePath);
        var root = driver.gameObject;

        // 旧模型包围盒高度(取激活的那个)
        float refHeight = 1.8f;
        Transform refModel = null;
        foreach (Transform child in root.transform)
        {
            if (child.name != "Model") continue;
            var ar = child.GetComponentInChildren<Animator>(true);
            if (ar == null || !child.gameObject.activeInHierarchy) continue;
            refModel = child;
            var b = new Bounds(child.position, Vector3.zero);
            foreach (var r in child.GetComponentsInChildren<Renderer>(true)) b.Encapsulate(r.bounds);
            refHeight = Mathf.Max(0.5f, b.size.y);
        }
        Debug.Log("[SHImportSetup] refModel=" + (refModel != null ? refModel.name : "null")
                  + " refHeight=" + refHeight.ToString("F3"));

        // 新模型身高校正缩放
        var probe = (GameObject)PrefabUtility.InstantiatePrefab(
            AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/SHCustomRig.prefab"));
        var pb = new Bounds(probe.transform.position, Vector3.zero);
        foreach (var r in probe.GetComponentsInChildren<Renderer>()) pb.Encapsulate(r.bounds);
        float newHeight = Mathf.Max(0.3f, pb.size.y);
        float scale = refHeight / newHeight;
        UnityEngine.Object.DestroyImmediate(probe);
        Debug.Log("[SHImportSetup] newHeight=" + newHeight.ToString("F3") + " scale=" + scale.ToString("F3"));

        // 防重: 已挂过 SHCustom 就只更新变换
        Transform existing = null;
        foreach (Transform child in root.transform)
        {
            var ar = child.GetComponentInChildren<Animator>(true);
            if (child.name == "Character" && ar != null && ar.avatar != null
                && ar.avatar.name == "SHCustomAvatar") { existing = child; break; }
        }
        GameObject newModel;
        if (existing != null)
        {
            newModel = existing.gameObject;
            newModel.transform.localScale = Vector3.one * scale;
            Debug.Log("[SHImportSetup] existing SHCustom instance updated");
        }
        else
        {
            newModel = (GameObject)PrefabUtility.InstantiatePrefab(
                AssetDatabase.LoadAssetAtPath<GameObject>(Dir + "/SHCustomRig.prefab"), root.transform);
            newModel.name = "Character";
            newModel.transform.localPosition = Vector3.zero;
            newModel.transform.localRotation = Quaternion.identity;
            newModel.transform.localScale = Vector3.one * scale;
            // 旧模型禁用(保留回退)
            foreach (Transform child in root.transform)
                if (child.name == "Model") child.gameObject.SetActive(false);
        }
        newModel.gameObject.SetActive(true);

        // 姿态池: 射门 SH_PenaltyKick + 跑步循环 SH_Jog
        var so = new SerializedObject(driver);
        var paths = so.FindProperty("_poseResourcePaths");
        paths.ClearArray();
        paths.InsertArrayElementAtIndex(0);
        paths.GetArrayElementAtIndex(0).stringValue = "ShooterPoses/SH_PenaltyKick";
        so.FindProperty("_poseResourcePath").stringValue = "ShooterPoses/SH_PenaltyKick";
        var runPath = so.FindProperty("_runPoseResourcePath");
        if (runPath != null) runPath.stringValue = "ShooterPoses/SH_Jog";
        so.ApplyModifiedProperties();

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveOpenScenes();
        Debug.Log("[SHImportSetup] scene swapped, pose pool -> SH_PenaltyKick / SH_Jog");
    }

    static HumanDescription BuildDescription(GameObject root)
    {
        var skeleton = root.GetComponentsInChildren<Transform>(true)
            .Select(t => new SkeletonBone
            {
                name = t.name,
                position = t.localPosition,
                rotation = t.localRotation,
                scale = t.localScale
            }).ToArray();

        var modelBones = root.GetComponentsInChildren<Transform>(true)
            .Select(t => t.name).ToHashSet();
        var human = new List<HumanBone>();
        foreach (var kv in BoneMap)
        {
            if (!modelBones.Contains(kv.Key)) continue;
            human.Add(new HumanBone
            {
                boneName = kv.Key,
                humanName = HumanTrait.BoneName[(int)kv.Value],
                limit = new HumanLimit { useDefaultValues = true }
            });
        }
        return new HumanDescription
        {
            human = human.ToArray(),
            skeleton = skeleton,
            feetSpacing = 0f,
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f,
        };
    }
}
