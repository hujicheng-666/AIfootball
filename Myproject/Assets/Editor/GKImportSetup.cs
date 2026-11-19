using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// 守门员新模型(FBX)导入配置：Humanoid Avatar 显式映射 + 材质 + 动作控制器。
/// CLI: Unity.exe -projectPath ... -executeMethod GKImportSetup.RunCli -batchmode -quit
/// </summary>
public static class GKImportSetup
{
    /// <summary>对照实验开关: true=clipAnimations 置 null(Unity 自动)</summary>
    public static bool ENV_TEST_NULL = false;

    const string FBX = "Assets/SuperGoalie/Meshes/Characters/GKCustom_GK2.fbx";
    const string Dir = "Assets/SuperGoalie/Meshes/Characters/GKCustom";
    const string TexPath = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/Goalkeeper/texture_pbr_20250901.png";

    // 模型骨名 -> HumanBodyBones（唯一缺失映射的留空由自动回退处理）
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

    public static void EnvTestNull() { ENV_TEST_NULL = true; RunCli(); }

    public static void RunCli()
    {
        try
        {
            Run();
            Debug.Log("[GKImportSetup] OK");
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogError("[GKImportSetup] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Football/GK Import Setup")]
    public static void Run()
    {
        if (!AssetDatabase.LoadAssetAtPath<GameObject>(FBX))
            throw new Exception("FBX not found at " + FBX + " — Unity 需先完成一次资产刷新");

        Directory.CreateDirectory(Dir);

        // ── 1. FBX 导入设置: Generic + clip 循环 ──
        var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FBX);
        var mi = AssetImporter.GetAtPath(FBX) as ModelImporter;
        mi.animationType = (ModelImporterAnimationType)2;   // Generic: 动画按 transform 曲线忠实播放
        mi.optimizeGameObjects = false;   // 需要真实骨骼 Transform (IK/碰撞扫掠)
        mi.motionNodeName = "ShooterRig";   // 动画根节点
        mi.SaveAndReimport();

        var clips = AssetDatabase.LoadAllAssetsAtPath(FBX)
            .Where(x => x is AnimationClip && !(x is GameObject))
            .Cast<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__"))
            .ToList();
        Debug.Log("[GKImportSetup] clips=" + clips.Count + " : " + string.Join(", ", clips.Select(c => c.name)));

        var clipAnims = mi.clipAnimations;
        if (clipAnims == null || clipAnims.Length == 0)
        {
            clipAnims = AssetDatabase.LoadAllAssetsAtPath(FBX)
                .OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__"))
                .Select(c => new ModelImporterClipAnimation
                {
                    name = c.name,
                    takeName = c.name,          // FBX 内部 take 名(带 ShooterRig| 前缀)
                    firstFrame = 0, lastFrame = 100,   // 百分比, 缺省 0/0 会得到空动画
                    loopTime = c.name.Contains("Idle"),
                }).ToArray();
        }
        else
        {
            foreach (var ca in clipAnims) ca.loopTime = ca.name.Contains("Idle");
        }
        mi.clipAnimations = clipAnims;
        mi.SaveAndReimport();

        // ── 3. 材质 ──
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(TexPath);
        if (tex == null) throw new Exception("keeper texture missing: " + TexPath);
        var mat = new Material(Shader.Find("Standard"));
        mat.name = "GKCustom_Mat";
        mat.SetTexture("_MainTex", tex);
        mat.SetFloat("_Glossiness", 0.15f);
        AssetDatabase.CreateAsset(mat, Dir + "/GKCustom_Mat.asset");

        // ── 4. AnimatorController: Idle 默认态 + MotionIndex/Play 直选动作 ──
        AssetDatabase.DeleteAsset(Dir + "/GKCustomController.controller");
        var ctrl = AnimatorController.CreateAnimatorControllerAtPath(Dir + "/GKCustomController.controller");
        ctrl.AddParameter("MotionIndex", AnimatorControllerParameterType.Int);
        ctrl.AddParameter("Play", AnimatorControllerParameterType.Trigger);
        var sm = ctrl.layers[0].stateMachine;

        var idleClip = clips.FirstOrDefault(c => c.name.Contains("GK_10_IdleNoBall"))
                       ?? clips.FirstOrDefault(c => c.name.Contains("Idle"));
        if (idleClip == null) throw new Exception("idle clip not found");
        var idle = sm.AddState("Idle");
        idle.motion = idleClip;
        sm.defaultState = idle;

        // 按区域排布的动作顺序（GKMotionSelector 的 MotionIndex 与此一致）
        string[] motionOrder =
        {
            "GK_12_CatchLow",        // 0 中央低球
            "GK_11_CatchMedium",     // 1 中央中球
            "GK_13_CatchJumpHigh",   // 2 中央高球
            "GK_18_DivingSaveLeft",  // 3 左侧低/中扑救
            "GK_04_BodyBlockLeft",   // 4 左侧身体阻挡
            "GK_20_CatchRunJumpHigh",// 5 左侧高球
            "GK_15_DivingSaveRight", // 6 右侧低/中扑救
            "GK_07_BodyBlockRight",  // 7 右侧身体阻挡
            "GK_03_Miss",            // 8 扑救失败(脱手/失误)
            "GK_19_Scoop",           // 9 贴地捞球
        };
        int index = 0;
        foreach (var clipName in motionOrder)
        {
            var clip = clips.FirstOrDefault(c => c.name.Contains(clipName));
            if (clip == null)
            {
                Debug.LogWarning("[GKImportSetup] clip missing: " + clipName);
                index++;
                continue;
            }
            var st = sm.AddState(clip.name.Contains("|") ? clip.name.Split('|')[1] : clip.name);
            st.motion = clip;

            // Any State -> (Play + MotionIndex==i) -> 状态
            var trIn = sm.AddAnyStateTransition(st);
            trIn.duration = 0.05f;
            trIn.hasExitTime = false;
            trIn.exitTime = 0f;
            trIn.AddCondition(AnimatorConditionMode.Equals, index, "MotionIndex");
            trIn.AddCondition(AnimatorConditionMode.If, 0, "Play");

            // 动作播完回 Idle
            var trOut = st.AddTransition(idle);
            trOut.hasExitTime = true;
            trOut.exitTime = 0.98f;
            trOut.duration = 0.15f;
            index++;
        }

        // ── 5. Prefab: 模型 + Animator(controller) + 材质 ──
        var prefab = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
        var animator = prefab.GetComponent<Animator>();
        if (animator == null) animator = prefab.AddComponent<Animator>();
        animator.runtimeAnimatorController = ctrl;
        foreach (var rend in prefab.GetComponentsInChildren<Renderer>())
            rend.sharedMaterial = mat;
        PrefabUtility.SaveAsPrefabAsset(prefab, Dir + "/GKCustomRig.prefab");
        UnityEngine.Object.DestroyImmediate(prefab);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[GKImportSetup] prefab + controller + material done");
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
            if (!modelBones.Contains(kv.Key))
            {
                Debug.LogWarning("[GKImportSetup] bone not in model: " + kv.Key);
                continue;
            }
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
            upperArmTwist = 0.5f,
            lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f,
            lowerLegTwist = 0.5f,
            armStretch = 0.05f,
            legStretch = 0.05f,
        };
    }
}
