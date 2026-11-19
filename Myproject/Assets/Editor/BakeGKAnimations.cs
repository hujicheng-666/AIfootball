using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// 守门员原版动画烘焙（终极方案）：
/// Humanoid muscle 动画在新模型上 retarget 崩坏（手工 avatar 与 importer 自动 avatar 都陷地+扭曲），
/// 改为 FK 烘焙：逐帧采样原版动画 → 把 human 骨骼的世界旋转拷到 GKCustom 骨架（骨骼长度用 GKCustom 自己的）
/// → 生成 Generic AnimationClip → 克隆原版 controller 并把 clip 引用替换为烘焙 clip。
/// 场景 GKCustom Animator 切回 Generic（Generic 管线此前已验证播放正常不陷地）。
/// </summary>
public static class BakeGKAnimations
{
    const string AnimDir = "Assets/SuperGoalie/Animations/GoalKeeperAnimations";
    const string OutDir = "Assets/SuperGoalie/Animations/GKBaked";
    const string DstFBX = "Assets/SuperGoalie/Meshes/Characters/GKCustom_GK2.fbx";
    const string SrcCtrl = "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller";
    const string NewCtrl = "Assets/SuperGoalie/Animators/GKBakedController.controller";
    const float Fps = 30f;

    static readonly Dictionary<string, string> Baked = new Dictionary<string, string>();   // clipName -> baked clip name

    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            if (!AssetDatabase.IsValidFolder(OutDir))
                AssetDatabase.CreateFolder("Assets/SuperGoalie/Animations", "GKBaked");

            // dst = 场景活跃 GK Character 的克隆（层级 scale 与播放实例完全一致，
            // 否则 hips localPosition 曲线会因父级 scale 差异放大/缩小成百上千倍）
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", UnityEditor.SceneManagement.OpenSceneMode.Single);
            GameObject activeChar = null;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t.name != "Character" || !t.gameObject.activeInHierarchy) continue;
                if (t.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) continue;
                bool inGk = false;
                for (var p = t.parent; p != null; p = p.parent)
                    if (p.name.StartsWith("GoalKeeper") || p.name.Contains("GoalKeeper")) { inGk = true; break; }
                if (inGk) { activeChar = t.gameObject; break; }
            }
            if (activeChar == null) throw new System.Exception("场景中未找到活跃的 GK Character");
            var dst = Object.Instantiate(activeChar);
            dst.name = "GKBakeDst";
            log.AppendLine("dst 克隆自场景实例，根 scale=" + dst.transform.localScale);

            // FBX 已切 Generic（avatar=null），现建 humanoid avatar 供 GetBoneTransform
            var dstAnim = dst.GetComponent<Animator>();
            if (dstAnim == null) dstAnim = dst.AddComponent<Animator>();
            dstAnim.runtimeAnimatorController = null;
            dstAnim.avatar = BuildHumanAvatarFor(dst);
            if (dstAnim.avatar == null || !dstAnim.avatar.isHuman)
                throw new System.Exception("临时 humanoid avatar 构建失败");

            string[] fbxGuids = AssetDatabase.FindAssets("t:Model", new[] { AnimDir });
            var fbxPaths = fbxGuids.Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            log.AppendLine("待烘焙 FBX 数: " + fbxPaths.Count);

            foreach (var fbxPath in fbxPaths)
            {
                var clips = AssetDatabase.LoadAllAssetsAtPath(fbxPath)
                    .OfType<AnimationClip>()
                    .Where(c => !c.name.StartsWith("__preview") && (c.legacy == false))
                    .ToArray();
                if (clips.Length == 0) { log.AppendLine("跳过(无clip): " + fbxPath); continue; }

                var srcGo = (GameObject)PrefabUtility.InstantiatePrefab(
                    AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath));
                var srcAnim = srcGo.GetComponent<Animator>();
                if (srcAnim == null || srcAnim.avatar == null)
                {
                    log.AppendLine("!! 源无 Animator/avatar: " + fbxPath);
                    Object.DestroyImmediate(srcGo); continue;
                }

                // 记录默认 hips 世界高度（比例基准），并对齐 dst 根旋转与 src 一致
                var srcHips = srcAnim.GetBoneTransform(HumanBodyBones.Hips);
                var dstHips = dstAnim.GetBoneTransform(HumanBodyBones.Hips);
                float srcRestHipY = srcHips != null ? srcHips.position.y : 1f;
                float dstRestHipY = dstHips != null ? dstHips.position.y : 1f;
                float hipScale = dstRestHipY / Mathf.Max(0.01f, srcRestHipY);

                dst.transform.rotation = srcGo.transform.rotation;

                foreach (var clip in clips)
                {
                    string outName = clip.name;
                    string safeName = Sanitize(outName);
                    if (Baked.ContainsKey(outName)) safeName = Sanitize(fbxPath.Substring(fbxPath.LastIndexOf('/') + 1).Replace(".fbx", "")) + "_" + safeName;
                    var baked = BakeOne(srcGo, srcAnim, dst, dstAnim, clip, safeName, hipScale, log);
                    if (baked != null) Baked[outName] = baked.name;
                }
                Object.DestroyImmediate(srcGo);
            }

            log.AppendLine("烘焙 clip 总数: " + Baked.Count);
            Object.DestroyImmediate(dst);

            // ── 完整复制原版 controller 并替换 motion 引用 ──
            // 注意：Object.Instantiate(controller) 不克隆内部子资产（状态机/状态），
            // 会得到空壳；必须用 CopyAsset 完整复制后再替换。
            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(SrcCtrl) == null)
                throw new System.Exception("原版 controller 不存在: " + SrcCtrl);
            AssetDatabase.DeleteAsset(NewCtrl);
            if (!AssetDatabase.CopyAsset(SrcCtrl, NewCtrl))
                throw new System.Exception("CopyAsset 失败");
            AssetDatabase.SaveAssets();
            var newCtrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(NewCtrl);
            if (newCtrl == null) throw new System.Exception("复制后的 controller 加载失败");

            var subAssets = new List<Object>();
            // 注意：AnimatorController.layers 的 getter 返回深拷贝（Unity API 陷阱），
            // 修改克隆树后必须把 layers 整体写回才会持久化！
            var layers = newCtrl.layers;
            foreach (var layer in layers)
                WalkStateMachine(layer.stateMachine, subAssets, log);
            newCtrl.layers = layers;
            EditorUtility.SetDirty(newCtrl);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            log.AppendLine("GKBakedController 已复制并替换 motion，状态数: " + subAssets.Count);

            // ── FBX 切回 Generic ──
            var mi = AssetImporter.GetAtPath(DstFBX) as ModelImporter;
            mi.animationType = (ModelImporterAnimationType)2;   // Generic
            mi.sourceAvatar = null;
            mi.SaveAndReimport();
            log.AppendLine("GKCustom FBX 已切回 Generic");

            // ── 场景确认：Animator 应已挂 GKBakedController（前一轮已设）──
            var bakedCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(NewCtrl);
            int okCount = 0;
            foreach (var ani in Object.FindObjectsOfType<Animator>(true))
            {
                if (ani.runtimeAnimatorController == bakedCtrl) okCount++;
            }
            log.AppendLine("场景中引用 GKBakedController 的 Animator 数: " + okCount);
            if (okCount == 0) log.AppendLine("!! 警告: 场景无 Animator 引用新 controller");
            log.AppendLine("场景未修改（无需保存）");
        }
        catch (System.Exception e)
        {
            log.AppendLine("!! 异常: " + e);
        }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_bake_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }

    /// <summary>采样一个 clip，FK 拷贝到 dst，输出烘焙 AnimationClip。</summary>
    static AnimationClip BakeOne(GameObject srcGo, Animator srcAnim, GameObject dstGo, Animator dstAnim,
        AnimationClip srcClip, string outName, float hipScale, System.Text.StringBuilder log)
    {
        var dstRoot = dstGo.transform;
        var outClip = new AnimationClip { frameRate = Fps };
        var posCurves = new Dictionary<string, AnimationCurve>[3];
        for (int k = 0; k < 3; k++) posCurves[k] = new Dictionary<string, AnimationCurve>();
        var rotCurves = new Dictionary<string, AnimationCurve>[4];
        for (int k = 0; k < 4; k++) rotCurves[k] = new Dictionary<string, AnimationCurve>();

        // dst human 骨骼清单（按层级深度从浅到深处理，保证父先于子）
        var boneList = new List<(HumanBodyBones bone, Transform dt, int depth)>();
        for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
        {
            var st = srcAnim.GetBoneTransform((HumanBodyBones)i);
            var dt = dstAnim.GetBoneTransform((HumanBodyBones)i);
            if (st == null || dt == null) continue;
            int depth = 0;
            for (var p = dt.parent; p != null && p != dstRoot; p = p.parent) depth++;
            boneList.Add(((HumanBodyBones)i, dt, depth));
        }
        boneList.Sort((a, b) => a.depth.CompareTo(b.depth));
        if (boneList.Count < 15)
        {
            log.AppendLine("!! 骨骼匹配过少(" + boneList.Count + ")，跳过 " + srcClip.name);
            return null;
        }

        int frames = Mathf.Max(2, Mathf.CeilToInt(srcClip.length * Fps) + 1);
        var srcRoot = srcGo.transform;
        AnimationMode.StartAnimationMode();
        try
        {
            for (int f = 0; f < frames; f++)
            {
                float t = Mathf.Min(srcClip.length, f / Fps);
                AnimationMode.SampleAnimationClip(srcGo, srcClip, t);

                foreach (var (bone, dt, _) in boneList)
                {
                    var st = srcAnim.GetBoneTransform(bone);
                    if (bone == HumanBodyBones.Hips)
                    {
                        // hips：位置按身高比例迁移 + 旋转直拷
                        Vector3 offset = st.position - srcRoot.position;
                        Vector3 target = dstRoot.position + offset * hipScale;
                        dt.SetPositionAndRotation(target, st.rotation);
                        string path = RelativePath(dstGo, dt);
                        Vector3 lp = dt.localPosition;
                        for (int k = 0; k < 3; k++)
                            AddKey(posCurves[k], path, f, frames, lp[k]);
                    }
                    else
                    {
                        dt.rotation = st.rotation;   // 世界旋转直拷，local 自动换算
                    }
                    string rp = RelativePath(dstGo, dt);
                    Quaternion lq = dt.localRotation;
                    for (int k = 0; k < 4; k++)
                        AddKey(rotCurves[k], rp, f, frames, lq[k]);
                }
            }
        }
        finally { AnimationMode.StopAnimationMode(); }

        for (int k = 0; k < 3; k++)
        {
            char axis = "xyz"[k];
            foreach (var p in posCurves[k])
                outClip.SetCurve(p.Key, typeof(Transform), "m_LocalPosition." + axis, p.Value);
        }
        for (int k = 0; k < 4; k++)
        {
            string comp = new[] { "x", "y", "z", "w" }[k];
            foreach (var p in rotCurves[k])
                outClip.SetCurve(p.Key, typeof(Transform), "m_LocalRotation." + comp, p.Value);
        }

        string outPath = OutDir + "/" + outName + ".anim";
        AssetDatabase.DeleteAsset(outPath);
        AssetDatabase.CreateAsset(outClip, outPath);
        log.AppendLine("烘焙: " + srcClip.name + " (" + frames + "帧) -> " + outPath);
        return outClip;
    }

    /// <summary>用标准骨名构建临时 humanoid avatar（FBX 已是 Generic，无法自带）。</summary>
    static Avatar BuildHumanAvatarFor(GameObject root)
    {
        var maps = new (string bone, HumanBodyBones hb)[]
        {
            ("Hips", HumanBodyBones.Hips), ("Spine", HumanBodyBones.Spine),
            ("Chest", HumanBodyBones.Chest), ("Neck", HumanBodyBones.Neck),
            ("Head", HumanBodyBones.Head),
            ("LeftShoulder", HumanBodyBones.LeftShoulder), ("LeftUpperArm", HumanBodyBones.LeftUpperArm),
            ("LeftLowerArm", HumanBodyBones.LeftLowerArm), ("LeftHand", HumanBodyBones.LeftHand),
            ("RightShoulder", HumanBodyBones.RightShoulder), ("RightUpperArm", HumanBodyBones.RightUpperArm),
            ("RightLowerArm", HumanBodyBones.RightLowerArm), ("RightHand", HumanBodyBones.RightHand),
            ("LeftUpperLeg", HumanBodyBones.LeftUpperLeg), ("LeftLowerLeg", HumanBodyBones.LeftLowerLeg),
            ("LeftFoot", HumanBodyBones.LeftFoot), ("LeftToes", HumanBodyBones.LeftToes),
            ("RightUpperLeg", HumanBodyBones.RightUpperLeg), ("RightLowerLeg", HumanBodyBones.RightLowerLeg),
            ("RightFoot", HumanBodyBones.RightFoot), ("RightToes", HumanBodyBones.RightToes),
        };
        var all = new HashSet<string>(root.GetComponentsInChildren<Transform>(true).Select(t => t.name));
        var human = maps.Where(kv => all.Contains(kv.bone)).Select(kv => new HumanBone
        {
            boneName = kv.bone,
            humanName = HumanTrait.BoneName[(int)kv.hb],
            limit = new HumanLimit { useDefaultValues = true },
        }).ToArray();
        var skeleton = root.GetComponentsInChildren<Transform>(true)
            .Select(t => new SkeletonBone
            {
                name = t.name,
                position = t.localPosition,
                rotation = t.localRotation,
                scale = t.localScale,
            }).ToArray();
        var avatar = AvatarBuilder.BuildHumanAvatar(root, new HumanDescription
        {
            human = human,
            skeleton = skeleton,
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f,
            upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f,
        });
        avatar.name = "GKBakeTempAvatar";
        return avatar;
    }

    static string Sanitize(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        return sb.ToString();
    }

    static void AddKey(Dictionary<string, AnimationCurve> map, string path, int frame, int total, float v)
    {
        if (!map.TryGetValue(path, out var c)) { c = new AnimationCurve(); map[path] = c; }
        c.AddKey(frame / 30f, v);
    }

    static string RelativePath(GameObject root, Transform t)
    {
        var parts = new List<string>();
        for (var p = t; p != null && p.gameObject != root; p = p.parent)
            parts.Insert(0, p.name);
        return string.Join("/", parts);
    }

    static void WalkStateMachine(AnimatorStateMachine sm, List<Object> subAssets, System.Text.StringBuilder log)
    {
        if (sm == null) return;
        foreach (var st in sm.states)
        {
            if (st.state == null) continue;
            st.state.motion = ReplaceMotion(st.state.motion, log, subAssets);
            EditorUtility.SetDirty(st.state);   // 子资产必须单独 SetDirty 才会持久化
        }
        foreach (var bts in sm.stateMachines)
            WalkStateMachine(bts.stateMachine, subAssets, log);
    }

    static Motion ReplaceMotion(Motion m, System.Text.StringBuilder log, List<Object> subAssets)
    {
        if (m is BlendTree bt)
        {
            var children = bt.children;
            bool changed = false;
            for (int i = 0; i < children.Length; i++)
            {
                var r = ReplaceMotion(children[i].motion, log, subAssets);
                if (r != children[i].motion) { children[i].motion = r; changed = true; }
            }
            if (changed)
            {
                bt.children = children;
                EditorUtility.SetDirty(bt);   // BlendTree 子资产同样需要
            }
            return bt;
        }
        var clip = m as AnimationClip;
        if (clip != null && Baked.TryGetValue(clip.name, out var bakedName))
        {
            var baked = AssetDatabase.LoadAssetAtPath<AnimationClip>(OutDir + "/" + bakedName + ".anim");
            if (baked != null)
            {
                log.AppendLine("替换 motion: " + clip.name + " -> " + bakedName);
                return baked;
            }
        }
        return m;
    }
}
