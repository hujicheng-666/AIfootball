// 混元射手(Mixamo 骨架) 导入校验
// 用法: Unity.exe -batchmode -quit -projectPath <proj> -executeMethod ShooterMixamoValidator.Run
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;

public static class ShooterMixamoValidator
{
    const string FbxPath = "Assets/SuperGoalie/Meshes/Characters/ShooterMixamo/shooter_mixamo_rig_150k.fbx";

    static readonly string[] Required =
    {
        "Hips", "Spine", "Chest", "Neck", "Head",
        "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
        "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
        "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
        "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
    };

    [MenuItem("Tools/校验 Mixamo 射手导入")]
    public static void Run()
    {
        var log = new StringBuilder();
        Action<string> L = s => { log.AppendLine("[VAL] " + s); Debug.Log("[VAL] " + s); };

        try
        {
            // ---------- 1. 确保已导入并配置 ----------
            AssetDatabase.Refresh();
            var mi = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (mi == null)
            {
                AssetDatabase.ImportAsset(FbxPath, ImportAssetOptions.ForceSynchronousImport);
                mi = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            }
            if (mi == null) { L("ERROR: 无法获取 ModelImporter，FBX 未导入: " + FbxPath); Flush(log); return; }

            L("原始设置: animationType=" + mi.animationType + " avatarSetup=" + mi.avatarSetup
              + " importAnimation=" + mi.importAnimation);

            mi.animationType = ModelImporterAnimationType.Human;
            mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            mi.importAnimation = true;
            mi.animationCompression = ModelImporterAnimationCompression.KeyframeReduction;
            mi.importBlendShapes = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importVisibility = false;
            mi.useFileScale = true;
            mi.globalScale = 1f;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.isReadable = false;
            mi.SaveAndReimport();
            L("已应用 Humanoid 导入设置并重导入");

            // ---------- 2. Avatar ----------
            var all = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
            Avatar avatar = all.OfType<Avatar>().FirstOrDefault();
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            if (avatar == null) { L("ERROR: 未生成 Avatar"); Flush(log); return; }
            if (go == null) { L("ERROR: 未加载模型 GameObject"); Flush(log); return; }

            L("Avatar: name=" + avatar.name + " isValid=" + avatar.isValid + " isHuman=" + avatar.isHuman);

            // ---------- 3. 骨骼映射 ----------
            var hd = avatar.humanDescription;
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var h in hd.human)
                if (!string.IsNullOrEmpty(h.humanName) && !string.IsNullOrEmpty(h.boneName))
                    map[h.humanName] = h.boneName;

            L("映射到 Humanoid 的骨骼数: " + map.Count + " / " + HumanTrait.BoneCount);

            int missRequired = 0;
            var missList = new List<string>();
            foreach (var r in Required)
            {
                if (map.ContainsKey(r)) continue;
                missRequired++; missList.Add(r);
            }
            L(missRequired == 0
                ? "必要骨骼(21 项) 全部映射成功"
                : "缺失必要骨骼 " + missRequired + " 项: " + string.Join(", ", missList));

            // 全部 Humanoid 槽位状态
            var lines = new List<string>();
            foreach (var bn in HumanTrait.BoneName)
            {
                string bone;
                bool ok = map.TryGetValue(bn, out bone);
                lines.Add((ok ? "OK  " : "MISS") + "  " + bn.PadRight(18) + " -> " + (ok ? bone : "-"));
            }
            L("--- Humanoid 槽位明细 ---");
            foreach (var l in lines) L(l);

            // 未被任何 Humanoid 槽位使用的骨骼（诊断：是否有关键骨被漏掉）
            var used = new HashSet<string>(map.Values, StringComparer.Ordinal);
            var unused = new List<string>();
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
                if (!used.Contains(t.name) && t != go.transform) unused.Add(t.name);
            L("未被映射的骨骼数: " + unused.Count + " -> " + string.Join(", ", unused.Take(20)));

            // ---------- 3b. 补手指映射（Unity 自动映射只认了 Index） ----------
            var fingerFix = new Dictionary<string, string>
            {
                {"Left Thumb Proximal","mixamorig:LeftHandThumb1"},
                {"Left Thumb Intermediate","mixamorig:LeftHandThumb2"},
                {"Left Thumb Distal","mixamorig:LeftHandThumb3"},
                {"Left Index Proximal","mixamorig:LeftHandIndex1"},
                {"Left Index Intermediate","mixamorig:LeftHandIndex2"},
                {"Left Index Distal","mixamorig:LeftHandIndex3"},
                {"Left Middle Proximal","mixamorig:LeftHandMiddle1"},
                {"Left Middle Intermediate","mixamorig:LeftHandMiddle2"},
                {"Left Middle Distal","mixamorig:LeftHandMiddle3"},
                {"Left Ring Proximal","mixamorig:LeftHandRing1"},
                {"Left Ring Intermediate","mixamorig:LeftHandRing2"},
                {"Left Ring Distal","mixamorig:LeftHandRing3"},
                {"Left Little Proximal","mixamorig:LeftHandPinky1"},
                {"Left Little Intermediate","mixamorig:LeftHandPinky2"},
                {"Left Little Distal","mixamorig:LeftHandPinky3"},
                {"Right Thumb Proximal","mixamorig:RightHandThumb1"},
                {"Right Thumb Intermediate","mixamorig:RightHandThumb2"},
                {"Right Thumb Distal","mixamorig:RightHandThumb3"},
                {"Right Index Proximal","mixamorig:RightHandIndex1"},
                {"Right Index Intermediate","mixamorig:RightHandIndex2"},
                {"Right Index Distal","mixamorig:RightHandIndex3"},
                {"Right Middle Proximal","mixamorig:RightHandMiddle1"},
                {"Right Middle Intermediate","mixamorig:RightHandMiddle2"},
                {"Right Middle Distal","mixamorig:RightHandMiddle3"},
                {"Right Ring Proximal","mixamorig:RightHandRing1"},
                {"Right Ring Intermediate","mixamorig:RightHandRing2"},
                {"Right Ring Distal","mixamorig:RightHandRing3"},
                {"Right Little Proximal","mixamorig:RightHandPinky1"},
                {"Right Little Intermediate","mixamorig:RightHandPinky2"},
                {"Right Little Distal","mixamorig:RightHandPinky3"},
            };

            var hdFix = mi.humanDescription;
            var existing = new HashSet<string>(hdFix.human.Select(h => h.humanName));
            var addList = new List<HumanBone>(hdFix.human);
            int added = 0;
            foreach (var kv in fingerFix)
            {
                if (existing.Contains(kv.Key)) continue;
                addList.Add(new HumanBone
                {
                    humanName = kv.Key,
                    boneName = kv.Value,
                    limit = new HumanLimit { useDefaultValues = true }
                });
                added++;
            }
            if (added > 0)
            {
                hdFix.human = addList.ToArray();
                mi.humanDescription = hdFix;
                mi.SaveAndReimport();
                L("已补手指映射 " + added + " 项，重导入后复查：");

                avatar = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<Avatar>().FirstOrDefault();
                go = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
                var map2 = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var h in avatar.humanDescription.human)
                    if (!string.IsNullOrEmpty(h.humanName) && !string.IsNullOrEmpty(h.boneName))
                        map2[h.humanName] = h.boneName;
                L("复查: isValid=" + avatar.isValid + " 映射骨骼数=" + map2.Count);
                int stillMiss = Required.Count(r => !map2.ContainsKey(r));
                L("复查: 必要骨骼缺失 " + stillMiss + " 项");
                int fingerMiss = fingerFix.Keys.Count(k => !map2.ContainsKey(k));
                L("复查: 手指槽位仍缺 " + fingerMiss + " / " + fingerFix.Count);
                map = map2;
            }
            else L("手指映射已完整，无需补");

            // ---------- 3c. 变换与包围盒诊断 ----------
            var allT = go.GetComponentsInChildren<Transform>(true);
            var armT = allT.FirstOrDefault(x => x.name == "Armature") ?? allT.FirstOrDefault(x => x.name.ToLower().Contains("armature"));
            var hipsT = allT.FirstOrDefault(x => x.name.ToLower().Contains("hips"));
            if (armT != null) L(string.Format("Armature pos={0} rot={1} lossyScale={2}",
                armT.position.ToString("F3"), armT.rotation.eulerAngles.ToString("F1"), armT.lossyScale.ToString("F4")));
            if (hipsT != null) L(string.Format("Hips pos={0} localPos={1} lossyScale={2}",
                hipsT.position.ToString("F3"), hipsT.localPosition.ToString("F4"), hipsT.lossyScale.ToString("F4")));

            // ---------- 4. 层级与网格 ----------
            int transformCount = go.GetComponentsInChildren<Transform>(true).Length;
            var smrs = go.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            L("Transform 数: " + transformCount + "  SkinnedMeshRenderer 数: " + smrs.Length);
            int totalVerts = 0, totalTris = 0;
            foreach (var s in smrs)
            {
                if (s.sharedMesh == null) continue;
                totalVerts += s.sharedMesh.vertexCount;
                totalTris += s.sharedMesh.triangles.Length / 3;
            }
            L("网格合计: 顶点 " + totalVerts + "  三角面 " + totalTris);
            if (smrs.Length > 0 && smrs[0].sharedMesh != null)
            {
                var s0 = smrs[0];
                L("rootBone=" + (s0.rootBone != null ? s0.rootBone.name : "null")
                  + "  localBounds=" + s0.localBounds.center.ToString("F3") + "/" + s0.localBounds.size.ToString("F3"));
                L("mesh.bounds=" + s0.sharedMesh.bounds.center.ToString("F3") + "/" + s0.sharedMesh.bounds.size.ToString("F3"));
                L("smr.bounds=" + s0.bounds.center.ToString("F3") + "/" + s0.bounds.size.ToString("F3"));
            }

            // ---------- 5. 包围盒 ----------
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends.Length > 0)
            {
                var b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                L(string.Format("包围盒 尺寸 W={0:F4} H={1:F4} D={2:F4}  最低点Y={3:F4}",
                    b.size.x, b.size.y, b.size.z, b.min.y));
            }

            // ---------- 6. 动画 Clip ----------
            var clips = all.OfType<AnimationClip>().Where(c => !c.name.StartsWith("__preview__")).ToArray();
            L("Clip 数: " + clips.Length);
            foreach (var c in clips)
            {
                L(string.Format("CLIP  name={0}  length={1:F4}s  frameRate={2:F1}  isHumanMotion={3}  loop={4}",
                    c.name, c.length, c.frameRate, c.isHumanMotion, c.isLooping));
                var binds = AnimationUtility.GetCurveBindings(c);
                L("      曲线数=" + binds.Length
                  + " 位置曲线=" + binds.Count(x => x.propertyName == "m_LocalPosition")
                  + " 旋转曲线=" + binds.Count(x => x.propertyName == "m_LocalRotation"));
                // 判断是否含位移（root motion）
                var hipsPos = binds.FirstOrDefault(x => x.propertyName == "m_LocalPosition"
                    && x.path.ToLower().Contains("hips"));
                if (!string.IsNullOrEmpty(hipsPos.path))
                    L("      Hips 位置曲线 path=" + hipsPos.path + "  (含 root motion)");
            }

            // ---------- 7. 采样验证（动画是否真的驱动模型） ----------
            try
            {
                var clip = clips.OrderByDescending(c => c.length).FirstOrDefault();
                if (clip != null)
                {
                    var inst = UnityEngine.Object.Instantiate(go);
                    inst.hideFlags = HideFlags.HideAndDontSave;
                    var anim = inst.GetComponent<Animator>();
                    if (anim == null) anim = inst.AddComponent<Animator>();
                    anim.avatar = avatar;
                    anim.applyRootMotion = false;
                    anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

                    float[] ts = { 0f, 0.40f, 0.70f, 0.767f, 0.85f, 1.20f, 1.50f };
                    foreach (var t in ts)
                    {
                        float nt = clip.length > 0 ? Mathf.Clamp01(t / clip.length) : 0f;
                        clip.SampleAnimation(inst, t);
                        var all2 = inst.GetComponentsInChildren<Transform>(true);
                        Transform hips = all2.FirstOrDefault(x => x.name.ToLower().Contains("hips"));
                        Transform lf = all2.FirstOrDefault(x => x.name.ToLower().EndsWith("leftfoot"));
                        Transform rf = all2.FirstOrDefault(x => x.name.ToLower().EndsWith("rightfoot"));
                        string s = string.Format("SAMPLE t={0:F2}s", t);
                        if (hips != null) s += string.Format("  Hips=({0:F3},{1:F3},{2:F3})", hips.position.x, hips.position.y, hips.position.z);
                        if (lf != null) s += string.Format("  LFoot.y={0:F3}", lf.position.y);
                        if (rf != null) s += string.Format("  RFoot.y={0:F3}", rf.position.y);
                        L(s);
                    }
                    UnityEngine.Object.DestroyImmediate(inst);
                }
            }
            catch (Exception e)
            {
                L("采样验证跳过: " + e.GetType().Name + " " + e.Message);
            }

            L("RESULT_DONE");
        }
        catch (Exception e)
        {
            L("EXCEPTION: " + e);
        }
        Flush(log);
    }

    static void Flush(StringBuilder log)
    {
        try
        {
            var p = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(Application.dataPath)),
                ".tmp_unity_validate.txt");
            System.IO.File.WriteAllText(p, log.ToString(), new UTF8Encoding(false));
            Debug.Log("[VAL] 结果写入 " + p);
        }
        catch (Exception e) { Debug.LogError("[VAL] 写文件失败 " + e.Message); }
        AssetDatabase.SaveAssets();
    }
}
