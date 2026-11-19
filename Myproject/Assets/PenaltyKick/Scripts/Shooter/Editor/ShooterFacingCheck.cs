using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PenaltyKickPlatform.Shooter;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 朝向验证：按运行时的真实摆位（站在球后 standOffset 处、transform 朝球）
    /// 应用动捕姿态，然后从「球门方向」「正侧」「射手背后」三个机位渲染。
    ///
    /// 判据很直接：
    ///   · 从球门方向看过去，射手应该是**正脸**对着镜头（他面向球门）；
    ///   · 如果看到的是后脑勺，说明数据与模型的前向差了 180°；
    ///   · 如果看到的是侧脸并且四肢左右交叉，说明左右反向。
    /// 这三张图能一次性区分「反 180°」「左右镜像」「只是扭转」三种情况。
    /// </summary>
    public static class ShooterFacingCheck
    {
        private static readonly HumanBodyBones[] AllBones = (HumanBodyBones[])
            System.Enum.GetValues(typeof(HumanBodyBones));

        private static readonly string[] Clips = { "CMU_10_06", "CMU_11_01" };

        private const int W = 380, H = 620;
        private const int Cols = 5;

        [MenuItem("AIfootball/动画/10. 朝向验证（球门视角）")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[Facing] 找不到 Shooter"); return; }

            var rt = shooter.GetComponent("ShooterRetargeter") as ShooterRetargeter;
            var anim = shooter.GetComponentInChildren<Animator>();
            var smr = shooter.GetComponentInChildren<SkinnedMeshRenderer>();
            if (rt == null || anim == null || smr == null)
            {
                Debug.LogError("[Facing] 缺 ShooterRetargeter / Animator / SkinnedMeshRenderer");
                return;
            }

            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }

            var bones = new List<Transform>();
            var paths = new List<string>();
            foreach (var hb in AllBones)
            {
                if (hb == HumanBodyBones.LastBone) continue;
                var tr = anim.GetBoneTransform(hb);
                if (tr == null) continue;
                bones.Add(tr);
                paths.Add(AnimationUtility.CalculateTransformPath(tr, shooter.transform));
            }

            var bakedMesh = new Mesh();
            var proxy = new GameObject("__facing_proxy");
            proxy.hideFlags = HideFlags.HideAndDontSave;
            var proxyMf = proxy.AddComponent<MeshFilter>();
            var proxyMr = proxy.AddComponent<MeshRenderer>();
            proxyMr.sharedMaterials = smr.sharedMaterials;
            proxyMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.enabled = false;

            rt.SetAutoUpdate(false);
            rt.CaptureRestPose();

            var sb = new StringBuilder("[Facing] 骨骼 " + bones.Count + " 根\n");
            string outDir = System.IO.Path.GetFullPath(Application.dataPath + "/../../.tmp_facing");
            System.IO.Directory.CreateDirectory(outDir);

            Vector3 basePos = shooter.transform.position;
            var ball = GameObject.Find("Ball");
            sb.AppendLine("射手 " + basePos.ToString("F2") + "  球 " + (ball != null ? ball.transform.position.ToString("F2") : "缺"));

            // 球门方向标：在球门一侧放一个亮色方块，渲染时能直接看出哪边是球门
            var goalMark = GameObject.CreatePrimitive(PrimitiveType.Cube);
            goalMark.name = "__goal_mark";
            goalMark.hideFlags = HideFlags.HideAndDontSave;
            Object.DestroyImmediate(goalMark.GetComponent<Collider>());
            goalMark.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);

            foreach (string name in Clips)
            {
                var ta = Resources.Load<TextAsset>("ShooterPoses/" + name);
                if (ta == null) { sb.AppendLine("  缺 " + name); continue; }
                var clip = ShooterPoseClip.FromJson(ta.text);
                if (clip == null || clip.FrameCount == 0) continue;

                rt.SetClip(clip);
                rt.CaptureRestPose();

                float dur = clip.Duration;
                var ts = new float[Cols];
                for (int j = 0; j < Cols; j++) ts[j] = dur * j / (Cols - 1);

                // 采 localRotation 并烘成 clip（batchmode 下直接改 Transform 不刷新网格）
                var rots = new Quaternion[Cols][];
                for (int j = 0; j < Cols; j++)
                {
                    rt.SetTime(ts[j]);
                    rt.ApplyPose();
                    rots[j] = new Quaternion[bones.Count];
                    for (int i = 0; i < bones.Count; i++)
                    {
                        Quaternion q = bones[i].localRotation;
                        if (j > 0 && Quaternion.Dot(rots[j - 1][i], q) < 0f)
                            q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                        rots[j][i] = q;
                    }
                }

                var baked = new AnimationClip { name = name + "_facing", legacy = false };
                for (int i = 0; i < bones.Count; i++)
                {
                    var cx = new AnimationCurve(); var cy = new AnimationCurve();
                    var cz = new AnimationCurve(); var cw = new AnimationCurve();
                    for (int j = 0; j < Cols; j++)
                    {
                        Quaternion q = rots[j][i];
                        cx.AddKey(ts[j], q.x); cy.AddKey(ts[j], q.y);
                        cz.AddKey(ts[j], q.z); cw.AddKey(ts[j], q.w);
                    }
                    foreach (var c in new[] { cx, cy, cz, cw })
                        for (int k = 0; k < c.length; k++) c.SmoothTangents(k, 0f);
                    baked.SetCurve(paths[i], typeof(Transform), "localRotation.x", cx);
                    baked.SetCurve(paths[i], typeof(Transform), "localRotation.y", cy);
                    baked.SetCurve(paths[i], typeof(Transform), "localRotation.z", cz);
                    baked.SetCurve(paths[i], typeof(Transform), "localRotation.w", cw);
                }

                // 运行时摆位：朝球、退到球后 standOffset
                Vector3 ballPos = ball != null ? ball.transform.position : basePos + Vector3.forward * 12f;
                Vector3 toBall = ballPos - basePos; toBall.y = 0f;
                Vector3 fwd = toBall.sqrMagnitude > 1e-4f ? toBall.normalized : shooter.transform.forward;
                Quaternion look = Quaternion.LookRotation(fwd, Vector3.up);

                float stand = clip.standOffset > 0.05f ? clip.standOffset : 0.9f;
                Vector3 strikeBase = ballPos - fwd * stand;
                strikeBase.y = basePos.y;

                // 球门一侧的标志物（球门在球的正前方）
                goalMark.transform.position = ballPos + fwd * 4.5f + Vector3.up * 0.17f;

                Vector3 right = Vector3.Cross(Vector3.up, fwd);
                float dist = 4.6f;
                var views = new[]
                {
                    new { label = "守门员视角（从球门看射手）", eye = ballPos + fwd * dist + Vector3.up * 1.15f },
                    new { label = "正侧", eye = ballPos + right * dist + Vector3.up * 1.05f },
                    new { label = "射手背后", eye = ballPos - fwd * dist + Vector3.up * 1.15f },
                };

                var grid = new Texture2D(W * Cols, H * views.Length, TextureFormat.RGBA32, false);
                Vector3 focus = ballPos + Vector3.up * 0.95f;

                AnimationMode.StartAnimationMode();
                try
                {
                    for (int v = 0; v < views.Length; v++)
                    {
                        for (int j = 0; j < Cols; j++)
                        {
                            Vector2 h = clip.SampleHip(ts[j]);
                            shooter.transform.rotation = look;
                            shooter.transform.position = strikeBase +
                                shooter.transform.rotation * new Vector3(h.x, 0f, h.y);

                            AnimationMode.BeginSampling();
                            AnimationMode.SampleAnimationClip(shooter, baked, ts[j]);
                            AnimationMode.EndSampling();

                            smr.BakeMesh(bakedMesh, true);
                            proxyMf.sharedMesh = bakedMesh;
                            proxy.transform.position = smr.transform.position;
                            proxy.transform.rotation = smr.transform.rotation;

                            cam.transform.position = views[v].eye;
                            cam.transform.LookAt(focus);
                            cam.fieldOfView = 36f;

                            var rtt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                            cam.targetTexture = rtt;
                            cam.Render();
                            RenderTexture.active = rtt;
                            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                            tex.Apply();
                            RenderTexture.active = null;
                            cam.targetTexture = null;
                            grid.SetPixels(j * W, (views.Length - 1 - v) * H, W, H, tex.GetPixels());
                            Object.DestroyImmediate(tex);
                            Object.DestroyImmediate(rtt);
                        }
                    }
                }
                finally
                {
                    AnimationMode.StopAnimationMode();
                }

                grid.Apply();
                string outPath = System.IO.Path.Combine(outDir, "facing_" + name + ".png");
                System.IO.File.WriteAllBytes(outPath, grid.EncodeToPNG());
                Object.DestroyImmediate(grid);
                Object.DestroyImmediate(baked);

                // 数值判据：模型的「左肩相对髋」在角色局部空间的 x，数据是否同号
                rt.SetTime(clip.Duration * 0.5f);
                rt.ApplyPose();
                Transform hipsT = anim.GetBoneTransform(HumanBodyBones.Hips);
                Transform lsh = anim.GetBoneTransform(HumanBodyBones.LeftShoulder);
                Transform rsh = anim.GetBoneTransform(HumanBodyBones.RightShoulder);
                Vector3 localL = shooter.transform.InverseTransformPoint(lsh.position) -
                                 shooter.transform.InverseTransformPoint(hipsT.position);
                Vector3 localR = shooter.transform.InverseTransformPoint(rsh.position) -
                                 shooter.transform.InverseTransformPoint(hipsT.position);
                sb.AppendLine(string.Format("  {0}: 模型局部 左肩x={1:F3} 右肩x={2:F3}（左应 < 右）",
                    name, localL.x, localR.x));
                sb.AppendLine("    球 " + ballPos.ToString("F2") + "  球门方向 " + fwd.ToString("F2") +
                              "  站位 " + strikeBase.ToString("F2") + " → " + outPath);
            }

            shooter.transform.position = basePos;
            smr.enabled = true;
            Object.DestroyImmediate(proxy);
            Object.DestroyImmediate(bakedMesh);
            Object.DestroyImmediate(goalMark);
            cam.fieldOfView = 60f;
            Debug.Log(sb.ToString());
            System.IO.File.WriteAllText(outDir + "/facing.txt", sb.ToString(), Encoding.UTF8);
        }
    }
}
