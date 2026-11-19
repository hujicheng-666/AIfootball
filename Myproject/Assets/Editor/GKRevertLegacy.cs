using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 守门员回退到原版扑救动作系统（用户要求）：
///   模型保留 GKCustom（新建模），动画回原版 GoalKeeperAnimator001 + 原版动画集。
/// 步骤：
///   1. GKCustom_GK2.fbx 切 Humanoid rig（原版动画是 muscle 数据，Humanoid 下自动重定向），
///      用与 SHImportSetup 相同的标准骨名映射构建 GKCustomAvatar；
///   2. 场景中 GKCustomRig 实例的 Animator 挂原版 controller + 新 avatar；
///   3. 移除场景中的 GKMotionSelector 组件 → GoalKeeper.PlayDiveMotion 走原版
///      Animator.SetTrigger("Dive") 路径（Height/Turn 参数驱动原版 Dive 状态机）；
///   4. 保存 Demo.unity。
/// </summary>
public static class GKRevertLegacy
{
    const string FBX = "Assets/SuperGoalie/Meshes/Characters/GKCustom_GK2.fbx";
    const string Dir = "Assets/SuperGoalie/Meshes/Characters/GKCustom";
    const string LegacyController = "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller";
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    // 与 SHImportSetup 相同的标准骨名映射（GKCustom 模型与 SHCustom 同骨架）
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

    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var fbxRoot = AssetDatabase.LoadAssetAtPath<GameObject>(FBX);
            if (fbxRoot == null) throw new System.Exception("FBX not found: " + FBX);
            var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(LegacyController);
            if (ctrl == null) throw new System.Exception("原版 controller 不存在: " + LegacyController);

            // ── 1. 构建 Humanoid Avatar ──
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(fbxRoot);
            var avatar = AvatarBuilder.BuildHumanAvatar(instance, BuildDescription(instance));
            if (avatar == null || !avatar.isValid)
                throw new System.Exception("GKCustomAvatar 构建失败/无效");
            avatar.name = "GKCustomAvatar";
            var avatarPath = Dir + "/GKCustomAvatar.asset";
            var existing = AssetDatabase.LoadAssetAtPath<Avatar>(avatarPath);
            if (existing != null) AssetDatabase.DeleteAsset(avatarPath);
            AssetDatabase.CreateAsset(avatar, avatarPath);
            log.AppendLine("GKCustomAvatar 构建成功 isHuman=" + avatar.isHuman);
            UnityEngine.Object.DestroyImmediate(instance);

            // ── 2. FBX 切 Humanoid ──
            var mi = AssetImporter.GetAtPath(FBX) as ModelImporter;
            mi.animationType = (ModelImporterAnimationType)3;   // Humanoid
            mi.sourceAvatar = avatar;
            mi.optimizeGameObjects = false;
            mi.SaveAndReimport();
            log.AppendLine("FBX 已切 Humanoid rig");

            // ── 3. 场景修改 ──
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            // 3a. GKCustomRig 实例的 Animator：挂原版 controller + 新 avatar
            // 识别方式：当前 controller 是 GKCustomController（GKImportSetup 设置的）
            int fixedAnimators = 0;
            foreach (var ani in UnityEngine.Object.FindObjectsOfType<Animator>(true))
            {
                if (ani.gameObject.scene.IsValid() == false) continue;
                if (ani.runtimeAnimatorController == null
                    || !ani.runtimeAnimatorController.name.Contains("GKCustomController")) continue;

                ani.runtimeAnimatorController = ctrl;
                ani.avatar = avatar;
                EditorUtility.SetDirty(ani);
                fixedAnimators++;
                log.AppendLine("GKCustom Animator '" + ani.name + "' -> 原版 controller + GKCustomAvatar");
            }
            log.AppendLine("已修改 Animator 数量: " + fixedAnimators);
            if (fixedAnimators == 0) log.AppendLine("!! 警告: 未找到 GKCustomRig 实例的 Animator");

            // 3b. 移除 GKMotionSelector 组件 → 回退原版 Dive trigger 路径
            int removed = 0;
            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>(true))
            {
                if (mb == null || mb.GetType().Name != "GKMotionSelector") continue;
                string host = mb.gameObject.name;
                UnityEngine.Object.DestroyImmediate(mb, true);
                removed++;
                log.AppendLine("已移除 GKMotionSelector on " + host);
            }
            log.AppendLine("移除 GKMotionSelector 数量: " + removed);

            // ── 4. 保存 ──
            EditorSceneManager.MarkSceneDirty(scene);
            bool saved = EditorSceneManager.SaveScene(scene, ScenePath);
            log.AppendLine("SaveScene 返回: " + saved);
        }
        catch (System.Exception e)
        {
            log.AppendLine("!! 异常: " + e);
        }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_revert_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }

    static string GetPrefabSourceGuid(GameObject go)
    {
        return "";   // 已改用 controller 名识别，保留空实现
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

        var modelBones = new HashSet<string>(
            root.GetComponentsInChildren<Transform>(true).Select(t => t.name));
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
            upperArmTwist = 0.5f,
            lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f,
            lowerLegTwist = 0.5f,
            armStretch = 0.05f,
            legStretch = 0.05f,
        };
    }
}
