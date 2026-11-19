#if UNITY_EDITOR
using System.IO;
using Assets.SuperGoalie.Scripts.Entities;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Shooter.EditorTools
{
    /// <summary>
    /// 一键在当前场景搭建射手：实例化角色、配置 Animator、挂载驱动脚本、连接引用、
    /// 并生成一套区别于门将的材质（去手套 + 换球衣色）。
    ///
    /// 菜单：AIfootball / 搭建射手（Shooter）
    /// </summary>
    public static class ShooterSetup
    {
        private const string ModelPath =
            "Assets/SuperGoalie/Meshes/Characters/Players/Player001/Character.fbx";
        private const string MaterialDir = "Assets/PenaltyKick/Materials";
        private const string ShooterName = "Shooter";

        [MenuItem("AIfootball/搭建射手（Shooter）", priority = 20)]
        private static void CreateShooterMenu()
        {
            CreateShooter(false);
        }

        /// <summary>创建射手。<paramref name="silentIfExists"/>=true 时不弹「已存在」对话框（供自动安装调用）。</summary>
        public static void CreateShooter(bool silentIfExists)
        {
            GameObject modelAsset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            if (modelAsset == null)
            {
                EditorUtility.DisplayDialog("找不到模型",
                    "未能加载角色模型：\n" + ModelPath +
                    "\n\n请确认路径是否正确。", "知道了");
                return;
            }

            GameObject existing = GameObject.Find(ShooterName);
            if (existing != null)
            {
                if (silentIfExists) return;   // 自动安装时不打扰用户

                bool replace = EditorUtility.DisplayDialog("已存在射手",
                    "场景中已经有名为 \"" + ShooterName + "\" 的对象。\n是否删除并重建？", "重建", "取消");
                if (!replace)
                {
                    Selection.activeGameObject = existing;
                    return;
                }
                Undo.DestroyObjectImmediate(existing);
            }

            // ── 找到球与球门作为参考 ──────────────────────────────────
            Ball ball = FindFirst<Ball>();
            Goal goal = FindFirst<Goal>();

            // ── 创建根对象 ────────────────────────────────────────────
            GameObject root = new GameObject(ShooterName);
            Undo.RegisterCreatedObjectUndo(root, "创建射手");

            GameObject model = (GameObject)PrefabUtility.InstantiatePrefab(modelAsset);
            model.name = "Model";
            model.transform.SetParent(root.transform, false);
            Undo.RegisterCreatedObjectUndo(model, "创建射手模型");

            // ── 朝向球门、站位在球的后方 ──────────────────────────────
            Vector3 forward = Vector3.forward;
            if (goal != null)
            {
                Vector3 flat = Vector3.ProjectOnPlane(goal.transform.position - root.transform.position, Vector3.up);
                if (flat.sqrMagnitude > 1e-6f) forward = flat.normalized;
            }
            else
            {
                Debug.LogWarning("[ShooterSetup] 场景中未找到 Goal，朝向默认取 +Z；请手动设置 Facing Target。");
            }

            const float standOffset = 0.62f;
            Vector3 anchor = ball != null ? ball.transform.position : Vector3.zero;
            if (ball == null)
                Debug.LogWarning("[ShooterSetup] 场景中未找到 Ball，站位默认为原点；请手动调整。");

            root.transform.position = anchor - forward * standOffset;
            root.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);

            // ── Animator：Humanoid，但不播放任何 clip ────────────────
            Animator animator = model.GetComponentInChildren<Animator>();
            if (animator == null)
            {
                Debug.LogError("[ShooterSetup] 模型上没有 Animator，无法进行 Humanoid 重定向。");
                return;
            }
            Undo.RecordObject(animator, "配置射手 Animator");
            animator.runtimeAnimatorController = null;   // 关键：不播放动画，交由脚本驱动
            animator.applyRootMotion = false;
            animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                Debug.LogError(
                    "[ShooterSetup] 该模型的 Avatar 不是 Humanoid，重定向无法工作。" +
                    "请在模型导入设置的 Rig 页把 Animation Type 设为 Humanoid 并 Apply。");
            }

            // ── 禁用碰撞体，避免干扰球的物理与轨迹 ────────────────────
            int disabled = 0;
            foreach (Collider col in root.GetComponentsInChildren<Collider>(true))
            {
                col.enabled = false;
                disabled++;
            }

            // ── 材质：与门将区分 ──────────────────────────────────────
            ApplyShooterMaterials(model);

            // ── 挂载脚本并连接引用 ────────────────────────────────────
            ShooterRetargeter retargeter = Undo.AddComponent<ShooterRetargeter>(root);
            ShooterDriver driver = Undo.AddComponent<ShooterDriver>(root);

            SerializedObject soRetarget = new SerializedObject(retargeter);
            soRetarget.FindProperty("_animator").objectReferenceValue = animator;
            soRetarget.ApplyModifiedProperties();

            SerializedObject soDriver = new SerializedObject(driver);
            soDriver.FindProperty("_retargeter").objectReferenceValue = retargeter;
            if (goal != null)
                soDriver.FindProperty("_facingTarget").objectReferenceValue = goal.transform;
            if (ball != null)
                soDriver.FindProperty("_ball").objectReferenceValue = ball.transform;
            soDriver.ApplyModifiedProperties();

            // 播放绑定器：接管球发射闸门，使球在触球帧才飞出（缺了它射手只会自顾自表演）
            ShooterPlaybackBinder binder = Undo.AddComponent<ShooterPlaybackBinder>(root);
            SerializedObject soBinder = new SerializedObject(binder);
            soBinder.FindProperty("_shooter").objectReferenceValue = driver;
            soBinder.ApplyModifiedProperties();

            EditorUtility.SetDirty(root);
            Selection.activeGameObject = root;

            Debug.Log(string.Format(
                "[ShooterSetup] 射手已创建。\n" +
                "  • 模型：{0}（Animator 已清空 controller，禁用 {1} 个 Collider）\n" +
                "  • 站位：球后方 {2:0.##}m，面朝 {3}\n" +
                "  • 姿态数据：{4}\n" +
                "下一步：在 ShooterDriver 上确认 Pose Resource Path（默认 ShooterPoses/00008），" +
                "然后接入播放流程（见 ShooterPlaybackBinder）。",
                ModelPath, disabled,
                standOffset,
                goal != null ? "Goal" : "默认 +Z（请手动设置 Facing Target）",
                "Resources/ShooterPoses/"),
                root);
        }

        /// <summary>复制并改写材质，使射手与门将可区分（不改动原资产）。</summary>
        private static void ApplyShooterMaterials(GameObject model)
        {
            if (!Directory.Exists(MaterialDir))
            {
                Directory.CreateDirectory(MaterialDir);
                AssetDatabase.Refresh();
            }

            Material kitTemplate = null, limbsTemplate = null;
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in renderers)
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    string n = m.name.ToLowerInvariant();
                    if (kitTemplate == null && n.Contains("kit")) kitTemplate = m;
                    if (limbsTemplate == null && (n.Contains("limbs") || n.Contains("limb"))) limbsTemplate = m;
                }
            }

            Material kitShooter = null;
            if (kitTemplate != null)
            {
                string path = Path.Combine(MaterialDir, "ShooterKit.mat").Replace("\\", "/");

                // AssetDatabase.CreateAsset 在目标已存在时会静默失败，
                // 结果就是新材质的颜色修改全部丢失（表现为球衣一直是灰白）。
                // 所以先删掉旧资产再重建。
                if (AssetDatabase.LoadAssetAtPath<Material>(path) != null)
                    AssetDatabase.DeleteAsset(path);

                kitShooter = new Material(kitTemplate) { name = "ShooterKit" };
                Color c = new Color(0.85f, 0.20f, 0.18f);   // 红色球衣，与门将区分
                if (kitShooter.HasProperty("_Color")) kitShooter.SetColor("_Color", c);
                if (kitShooter.HasProperty("_BaseColor")) kitShooter.SetColor("_BaseColor", c);
                AssetDatabase.CreateAsset(kitShooter, path);
            }

            int swapped = 0;
            foreach (Renderer r in renderers)
            {
                Material[] mats = r.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < mats.Length; i++)
                {
                    Material m = mats[i];
                    if (m == null) continue;
                    string n = m.name.ToLowerInvariant();

                    if (n.Contains("glove") && limbsTemplate != null)
                    {
                        // 门将手套 -> 皮肤材质，去掉守门员特征
                        mats[i] = limbsTemplate;
                        changed = true;
                    }
                    else if (n.Contains("kit") && kitShooter != null)
                    {
                        mats[i] = kitShooter;
                        changed = true;
                    }
                }
                if (changed)
                {
                    r.sharedMaterials = mats;
                    swapped++;
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log(string.Format("[ShooterSetup] 材质处理完成：{0} 个 Renderer 被替换" +
                                    "（球衣->{1}，手套->皮肤）。",
                swapped, kitShooter != null ? "ShooterKit" : "未找到 kit 材质"));
        }

        private static T FindFirst<T>() where T : Object
        {
#if UNITY_2023_1_OR_NEWER
            return Object.FindFirstObjectByType<T>(FindObjectsInactive.Include);
#else
            return Object.FindObjectOfType<T>(true);
#endif
        }

        [MenuItem("AIfootball/校验射手配置", priority = 21)]
        public static void ValidateShooter()
        {
            GameObject root = GameObject.Find(ShooterName);
            if (root == null)
            {
                EditorUtility.DisplayDialog("校验射手", "场景中没有找到 \"" + ShooterName + "\"。", "知道了");
                return;
            }

            ShooterRetargeter retargeter = root.GetComponent<ShooterRetargeter>();
            ShooterDriver driver = root.GetComponent<ShooterDriver>();
            ShooterPlaybackBinder binder = root.GetComponent<ShooterPlaybackBinder>();
            Animator animator = root.GetComponentInChildren<Animator>();

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine("射手配置校验：");
            sb.AppendLine("  ShooterRetargeter:   " + (retargeter != null ? "✓" : "✗ 缺失"));
            sb.AppendLine("  ShooterDriver:       " + (driver != null ? "✓" : "✗ 缺失"));
            sb.AppendLine("  ShooterPlaybackBinder: " + (binder != null ? "✓" : "✗ 缺失（球不会等触球）"));
            sb.AppendLine("  Animator:            " + (animator != null ? "✓" : "✗ 缺失"));
            if (animator != null)
            {
                sb.AppendLine("    Avatar 是 Humanoid: " + (animator.avatar != null && animator.avatar.isHuman ? "✓" : "✗"));
                sb.AppendLine("    Controller 已清空: " + (animator.runtimeAnimatorController == null ? "✓" : "✗ 仍会覆盖脚本姿态"));
                if (animator.avatar != null && animator.avatar.isHuman)
                {
                    string[] needed = { "Hips", "Spine", "Chest", "Head",
                                        "LeftUpperArm", "LeftLowerArm", "RightUpperArm", "RightLowerArm",
                                        "LeftUpperLeg", "LeftLowerLeg", "RightUpperLeg", "RightLowerLeg" };
                    HumanBodyBones[] bones = { HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
                                               HumanBodyBones.Head, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
                                               HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm,
                                               HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg,
                                               HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg };
                    int missing = 0;
                    for (int i = 0; i < bones.Length; i++)
                        if (animator.GetBoneTransform(bones[i]) == null) { sb.AppendLine("    缺失骨骼: " + needed[i]); missing++; }
                    sb.AppendLine("    关键骨骼完整: " + (missing == 0 ? "✓" : "✗ 缺 " + missing + " 根"));
                }
            }
            TextAsset clip = Resources.Load<TextAsset>("ShooterPoses/00008");
            sb.AppendLine("  姿态数据 00001:    " + (clip != null ? "✓" : "✗ 未找到，请先运行导出脚本"));

            Debug.Log(sb.ToString(), root);
            EditorUtility.DisplayDialog("校验射手", sb.ToString(), "知道了");
        }
    }
}
#endif
