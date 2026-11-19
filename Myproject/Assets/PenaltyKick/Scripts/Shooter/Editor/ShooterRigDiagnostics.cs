#if UNITY_EDITOR
using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Shooter.EditorTools
{
    /// <summary>
    /// 打印射手模型的 Humanoid 骨骼映射，用于排查"肢体乱、动作对不上"的问题。
    ///
    /// 关键经验：**不能在资源态（AssetDatabase.LoadAssetAtPath）直接调 GetBoneTransform**。
    /// 资源态下 Animator 没有初始化，任何 Humanoid 模型都会返回全 null —— 那是一个假阴性，
    /// 很容易让人误判为"映射全丢"。必须实例化并 Rebind 之后再读。
    ///
    /// 用法：菜单 AIfootball / 诊断 / 射手骨骼映射
    ///      或命令行 -executeMethod PenaltyKickPlatform.Shooter.EditorTools.ShooterRigDiagnostics.Dump
    /// </summary>
    public static class ShooterRigDiagnostics
    {
        private const string ModelPath =
            "Assets/SuperGoalie/Meshes/Characters/Players/Player001/Character.fbx";

        private static readonly HumanBodyBones[] ProbeBones =
        {
            HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
            HumanBodyBones.Neck, HumanBodyBones.Head,
            HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm,
            HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand,
            HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
            HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
            HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
            HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot,
        };

        [MenuItem("AIfootball/诊断/射手骨骼映射")]
        public static void DumpFromMenu()
        {
            DumpModel(ModelPath);
        }

        public static void Dump()
        {
            DumpModel(ModelPath);
        }

        /// <summary>
        /// 端到端校验：真的把 ShooterRetargeter 跑起来，逐骨比较
        /// 「解算出的骨向」与「姿态数据要求的方向」，输出角误差。
        ///
        /// 这是唯一能区分「数据错」和「算法错」的办法 —— 只看映射表看不出
        /// FromToRotation 在近反向时会翻轴。
        /// </summary>
        [MenuItem("AIfootball/诊断/校验重定向结果")]
        public static void VerifyFromMenu()
        {
            VerifyPose();
        }

        public static void VerifyPose()
        {
            const string clipResource = "ShooterPoses/00001";

            TextAsset json = Resources.Load<TextAsset>(clipResource);
            if (json == null)
            {
                Debug.LogError("[RigDiag] 找不到姿态资源: " + clipResource);
                return;
            }

            ShooterPoseClip clip = ShooterPoseClip.FromJson(json.text);
            if (clip == null || clip.FrameCount == 0)
            {
                Debug.LogError("[RigDiag] 姿态解析失败");
                return;
            }

            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            GameObject instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null)
            {
                Debug.LogError("[RigDiag] 实例化失败");
                return;
            }

            // 复刻 ShooterSetup 的层级：Root(负责朝向) -> Model
            var root = new GameObject("RigDiagRoot");
            try
            {
                instance.transform.SetParent(root.transform, false);

                ShooterRetargeter retargeter = root.AddComponent<ShooterRetargeter>();
                var so = new SerializedObject(retargeter);
                so.FindProperty("_animator").objectReferenceValue =
                    instance.GetComponentInChildren<Animator>();
                so.ApplyModifiedPropertiesWithoutUndo();

                retargeter.CaptureRestPose();
                retargeter.SetClip(clip);

                Animator animator = instance.GetComponentInChildren<Animator>();

                // 与 ShooterRetargeter.Chain 一一对应的探针表
                var probes = new[]
                {
                    new Probe(HumanBodyBones.Hips, HumanBodyBones.Spine, "Center of Hips", "Center of Body"),
                    new Probe(HumanBodyBones.Spine, HumanBodyBones.Chest, "Center of Body", "Center of Shoulder"),
                    new Probe(HumanBodyBones.Chest, HumanBodyBones.Neck, "Center of Shoulder", "Neck"),
                    new Probe(HumanBodyBones.Neck, HumanBodyBones.Head, "Neck", "Head"),
                    new Probe(HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, "Left Shoulder", "Left Elbow"),
                    new Probe(HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, "Left Elbow", "Left Wrist"),
                    new Probe(HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, "Right Shoulder", "Right Elbow"),
                    new Probe(HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, "Right Elbow", "Right Wrist"),
                    new Probe(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, "Left Hip", "Left Knee"),
                    new Probe(HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot, "Left Knee", "Left Ankle"),
                    new Probe(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, "Right Hip", "Right Knee"),
                    new Probe(HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot, "Right Knee", "Right Ankle"),
                };

                float[] times = { 0f, clip.Duration * 0.25f, clip.Duration * 0.5f, clip.Duration };
                foreach (float t in times)
                {
                    retargeter.SetTime(t);
                    retargeter.ApplyPose();

                    var sb = new StringBuilder();
                    sb.Append("[RigDiag] t=").Append(t.ToString("0.000")).Append("s\n");

                    foreach (Probe p in probes)
                    {
                        Transform bone = animator.GetBoneTransform(p.Bone);
                        Transform child = animator.GetBoneTransform(p.Child);
                        int si = clip.IndexOfJoint(p.StartJoint);
                        int ei = clip.IndexOfJoint(p.EndJoint);
                        if (bone == null || child == null || si < 0 || ei < 0)
                        {
                            sb.Append("    ").Append(p.Bone.ToString().PadRight(14)).Append("跳过\n");
                            continue;
                        }

                        Vector3 actual = (child.position - bone.position).normalized;
                        Vector3 target = (clip.SampleJoint(t, ei) - clip.SampleJoint(t, si)).normalized;
                        float err = Vector3.Angle(actual, target);

                        string flag = err > 25f ? "  ★" : "";
                        sb.Append("    ").Append(p.Bone.ToString().PadRight(14))
                          .Append(" 误差=").Append(err.ToString("0.0").PadLeft(5)).Append("°")
                          .Append("  实际=").Append(actual.ToString("0.00").PadRight(20))
                          .Append(" 目标=").Append(target.ToString("0.00"))
                          .Append(flag).Append('\n');
                    }

                    Debug.Log(sb.ToString());
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        /// <summary>
        /// 把姿态渲染成一张胶片图，用于肉眼核对「肢体是否还乱」。
        ///
        /// 数值只能证明骨向量和铰链轴对上了，证明不了网格看起来正常——扭转错了
        /// 方向时方向误差依然是 0°（roll 绕的就是骨向量本身）。所以这里直接出图。
        /// </summary>
        [MenuItem("AIfootball/诊断/渲染姿态图")]
        public static void RenderFromMenu()
        {
            RenderPose();
        }

        public static void RenderPose()
        {
            // 可用环境变量切换要渲染的片段，便于横向对比候选素材
            string clipResource = Environment.GetEnvironmentVariable("RIGDIAG_CLIP");
            if (string.IsNullOrEmpty(clipResource)) clipResource = "ShooterPoses/00001";

            const int cellW = 420;
            const int cellH = 620;

            Debug.Log("[RigDiag] graphicsDevice=" + SystemInfo.graphicsDeviceType);
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null)
            {
                Debug.LogError("[RigDiag] 无图形设备（batchmode -nographics），无法渲染");
                return;
            }

            TextAsset json = Resources.Load<TextAsset>(clipResource);
            if (json == null)
            {
                Debug.LogError("[RigDiag] 找不到姿态资源: " + clipResource);
                return;
            }

            ShooterPoseClip clip = ShooterPoseClip.FromJson(json.text);
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            GameObject instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            var root = new GameObject("RigDiagRoot");
            GameObject camGo = null;
            GameObject lightGo = null;
            RenderTexture rt = null;
            var scratch = new System.Collections.Generic.List<GameObject>();

            try
            {
                instance.transform.SetParent(root.transform, false);

                ShooterRetargeter retargeter = root.AddComponent<ShooterRetargeter>();
                var so = new SerializedObject(retargeter);
                so.FindProperty("_animator").objectReferenceValue =
                    instance.GetComponentInChildren<Animator>();
                so.ApplyModifiedPropertiesWithoutUndo();
                retargeter.CaptureRestPose();
                retargeter.SetClip(clip);

                // ── 相机：从角色左前方看（角色朝 +z）──
                camGo = new GameObject("RigDiagCam");
                Camera cam = camGo.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.16f, 0.18f, 0.21f);
                cam.fieldOfView = 32f;
                cam.nearClipPlane = 0.05f;
                cam.farClipPlane = 60f;
                camGo.transform.position = new Vector3(2.4f, 1.45f, 3.9f);
                camGo.transform.LookAt(new Vector3(0f, 1.02f, 0f));

                // 地面：用来判断角色是否悬空或陷进草皮
                var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
                ground.name = "RigDiagGround";
                ground.transform.localScale = new Vector3(4f, 1f, 4f);
                ground.transform.position = new Vector3(0f, -0.128f, 0f);
                var groundMat = new Material(Shader.Find("Standard"))
                {
                    color = new Color(0.22f, 0.32f, 0.20f)
                };
                ground.GetComponent<Renderer>().sharedMaterial = groundMat;
                scratch.Add(ground);

                lightGo = new GameObject("RigDiagLight");
                Light light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.15f;
                lightGo.transform.rotation = Quaternion.Euler(42f, 138f, 0f);

                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(0.42f, 0.45f, 0.5f);

                // ── 逐时刻渲染：首帧 / 助跑中 / 触球前一帧 / 触球 / 收尾 ──
                float d = clip.Duration;
                float kickT = clip.fps > 0f ? clip.kickFrame / clip.fps : d * 0.5f;
                float[] times = { 0f, d * 0.3f, Mathf.Max(0f, kickT - 1f / 31f), kickT, d };
                int n = times.Length;
                var strip = new Texture2D(cellW * n, cellH, TextureFormat.RGB24, false);

                rt = new RenderTexture(cellW, cellH, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;

                var cell = new Texture2D(cellW, cellH, TextureFormat.RGB24, false);
                for (int i = 0; i < n; i++)
                {
                    retargeter.SetTime(times[i]);
                    retargeter.ApplyPose();

                    cam.Render();
                    RenderTexture.active = rt;
                    cell.ReadPixels(new Rect(0, 0, cellW, cellH), 0, 0);
                    cell.Apply();
                    RenderTexture.active = null;

                    strip.SetPixels(i * cellW, 0, cellW, cellH, cell.GetPixels());
                    Debug.Log("[RigDiag] 渲染 t=" + times[i].ToString("0.000") + "s");
                }
                strip.Apply();

                string dir = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(Application.dataPath, "..", "..", ".tmp_rigdiag"));
                System.IO.Directory.CreateDirectory(dir);
                string outPath = System.IO.Path.Combine(
                    dir, clipResource.Replace('/', '-') + ".png");
                System.IO.File.WriteAllBytes(outPath, strip.EncodeToPNG());
                Debug.Log("[RigDiag] 已写出 " + outPath);

                UnityEngine.Object.DestroyImmediate(strip);
                UnityEngine.Object.DestroyImmediate(cell);
            }
            finally
            {
                foreach (GameObject o in scratch)
                    if (o != null) UnityEngine.Object.DestroyImmediate(o);
                if (camGo != null) UnityEngine.Object.DestroyImmediate(camGo);
                if (lightGo != null) UnityEngine.Object.DestroyImmediate(lightGo);
                if (rt != null)
                {
                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private struct Probe
        {
            public readonly HumanBodyBones Bone;
            public readonly HumanBodyBones Child;
            public readonly string StartJoint;
            public readonly string EndJoint;

            public Probe(HumanBodyBones bone, HumanBodyBones child, string startJoint, string endJoint)
            {
                Bone = bone;
                Child = child;
                StartJoint = startJoint;
                EndJoint = endJoint;
            }
        }

        public static void DumpModel(string path)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (asset == null)
            {
                Debug.LogError("[RigDiag] 找不到模型: " + path);
                return;
            }

            // ── 0. 资源态（预期全 null，仅作为对照，不作结论）───────────
            Animator assetAnimator = asset.GetComponentInChildren<Animator>();
            if (assetAnimator != null)
                Debug.Log("[RigDiag] 资源态 isInitialized=" + assetAnimator.isInitialized
                          + "  Hips=" + Name(assetAnimator.GetBoneTransform(HumanBodyBones.Hips))
                          + "   ← 资源态读不到是正常的，不要据此判断");

            // ── 1. 实例化 + 初始化，这才是运行时的真实状态 ───────────────
            GameObject instance = PrefabUtility.InstantiatePrefab(asset) as GameObject;
            if (instance == null)
            {
                Debug.LogError("[RigDiag] 实例化失败");
                return;
            }

            try
            {
                Animator animator = instance.GetComponentInChildren<Animator>();
                if (animator == null)
                {
                    Debug.LogError("[RigDiag] 实例上没有 Animator");
                    return;
                }

                animator.Rebind();
                animator.Update(0f);

                Avatar avatar = animator.avatar;
                Debug.Log("[RigDiag] ===== Avatar 概况 =====");
                Debug.Log("[RigDiag] avatar=" + Name(avatar)
                          + "  isValid=" + (avatar != null && avatar.isValid)
                          + "  isHuman=" + (avatar != null && avatar.isHuman)
                          + "  isInitialized=" + animator.isInitialized
                          + "  humanScale=" + (animator.humanScale.ToString("0.000")));

                // ── 2. 生效的 humanDescription（自动映射的结果会体现在这里）──
                if (avatar != null)
                {
                    HumanDescription desc = avatar.humanDescription;
                    HumanBone[] humans = desc.human ?? new HumanBone[0];
                    SkeletonBone[] skel = desc.skeleton ?? new SkeletonBone[0];
                    Debug.Log("[RigDiag] humanDescription: human=" + humans.Length
                              + "  skeleton=" + skel.Length);

                    if (humans.Length > 0)
                    {
                        var sbh = new StringBuilder();
                        foreach (HumanBone hb in humans)
                            sbh.Append("    ").Append(hb.humanName.PadRight(20))
                               .Append(" -> ").Append(hb.boneName).Append('\n');
                        Debug.Log("[RigDiag] 生效映射：\n" + sbh);
                    }
                }

                // ── 3. 运行时真实骨骼解析 ────────────────────────────────
                Debug.Log("[RigDiag] ===== 运行时 GetBoneTransform =====");
                int mapped = 0, missing = 0;
                var missingNames = new StringBuilder();

                foreach (HumanBodyBones bone in ProbeBones)
                {
                    Transform t = animator.GetBoneTransform(bone);
                    if (t == null)
                    {
                        missing++;
                        missingNames.Append(bone).Append(' ');
                        Debug.Log("[RigDiag]   " + bone.ToString().PadRight(16) + " -> <null>");
                    }
                    else
                    {
                        mapped++;
                        Debug.Log("[RigDiag]   " + bone.ToString().PadRight(16) + " -> " + t.name
                                  + "   localPos=" + t.localPosition.ToString("0.000")
                                  + "  localRot=" + t.localEulerAngles.ToString("0.0")
                                  + "  parent=" + Name(t.parent));
                    }
                }

                Debug.Log("[RigDiag] 已映射 " + mapped + " 根，缺失 " + missing + " 根");
                if (missing > 0)
                    Debug.LogError("[RigDiag] 缺失骨骼: " + missingNames
                                   + " —— 这些骨骼不会被重定向驱动");

                // ── 4. 重定向器真正用到的 12 根，逐根给出 rest 指向 ──────
                Debug.Log("[RigDiag] ===== 重定向链 rest 指向（父空间）=====");
                ReportRest(animator, HumanBodyBones.Hips, HumanBodyBones.Spine);
                ReportRest(animator, HumanBodyBones.Spine, HumanBodyBones.Chest);
                ReportRest(animator, HumanBodyBones.Chest, HumanBodyBones.Neck);
                ReportRest(animator, HumanBodyBones.Neck, HumanBodyBones.Head);
                ReportRest(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm);
                ReportRest(animator, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand);
                ReportRest(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm);
                ReportRest(animator, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand);
                ReportRest(animator, HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg);
                ReportRest(animator, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
                ReportRest(animator, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg);
                ReportRest(animator, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);

                // ── 5. 左右是否颠倒 ──────────────────────────────────────
                CheckSide(animator, HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg, "UpperLeg");
                CheckSide(animator, HumanBodyBones.LeftFoot, HumanBodyBones.RightFoot, "Foot");
                CheckSide(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm, "UpperArm");
                CheckSide(animator, HumanBodyBones.LeftHand, HumanBodyBones.RightHand, "Hand");

                // ── 6. 尺度与接地 ────────────────────────────────────────
                ReportBounds(instance);

                // ── 7. 骨骼命名全表（用于建立名字映射的兜底路径）──────────
                Debug.Log("[RigDiag] ===== 全部骨骼名 =====");
                var sb = new StringBuilder();
                foreach (Transform t in instance.GetComponentsInChildren<Transform>())
                    if (t.GetComponent<SkinnedMeshRenderer>() == null) sb.Append(t.name).Append(' ');
                Debug.Log("[RigDiag] " + sb);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static string Name(UnityEngine.Object o)
        {
            return o == null ? "<null>" : o.name;
        }

        /// <summary>打印某段骨的 rest 方向（在父空间下），以及骨长是否为零。</summary>
        private static void ReportRest(Animator animator, HumanBodyBones bone, HumanBodyBones child)
        {
            Transform b = animator.GetBoneTransform(bone);
            Transform c = animator.GetBoneTransform(child);
            if (b == null || c == null || b.parent == null)
            {
                Debug.Log("[RigDiag]   " + bone.ToString().PadRight(16) + " 跳过（骨骼缺失）");
                return;
            }

            Vector3 a = b.parent.InverseTransformPoint(b.position);
            Vector3 d = b.parent.InverseTransformPoint(c.position);
            Vector3 dir = d - a;
            Debug.Log("[RigDiag]   " + bone.ToString().PadRight(16)
                      + " restDir=" + dir.normalized.ToString("0.000").PadRight(22)
                      + " 长度=" + dir.magnitude.ToString("0.000"));
        }

        /// <summary>左右骨骼应分居角色中线两侧；同侧或重合说明映射反了/错了。</summary>
        private static void CheckSide(Animator animator, HumanBodyBones left, HumanBodyBones right, string label)
        {
            Transform l = animator.GetBoneTransform(left);
            Transform r = animator.GetBoneTransform(right);
            if (l == null || r == null)
            {
                Debug.LogWarning("[RigDiag] 左右校验跳过（" + label + " 有缺失）");
                return;
            }

            Vector3 lp = animator.transform.InverseTransformPoint(l.position);
            Vector3 rp = animator.transform.InverseTransformPoint(r.position);

            bool ok = lp.x < rp.x;
            string msg = "[RigDiag] 左右校验 " + label.PadRight(9)
                         + " L.x=" + lp.x.ToString("0.000").PadLeft(7)
                         + "  R.x=" + rp.x.ToString("0.000").PadLeft(7)
                         + "  -> " + (ok ? "正常" : "★ 左右疑似颠倒");

            if (ok) Debug.Log(msg);
            else Debug.LogError(msg);
        }

        private static void ReportBounds(GameObject instance)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;

            Bounds b = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);

            Debug.Log("[RigDiag] 包围盒 中心=" + b.center.ToString("0.000")
                      + " 尺寸=" + b.size.ToString("0.000")
                      + "（身高 " + b.size.y.ToString("0.00") + " m，脚底 y=" + b.min.y.ToString("0.000") + "）");
        }
    }
}
#endif
