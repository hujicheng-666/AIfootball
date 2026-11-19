using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PenaltyKickPlatform.Shooter;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 把 CMU 动捕片段套到射手模型上渲染出来，用于和原始 avi 逐帧比对。
    ///
    /// 渲染方式和 <see cref="ShooterSynthCheck"/> 相同：batchmode 下 Transform 改了网格不刷新，
    /// 必须把 localRotation 烘成 AnimationClip、走 AnimationMode 采样，最后用 BakeMesh
    /// 在 CPU 端拿变形网格交给普通 MeshRenderer 画。
    /// </summary>
    public static class CmuShotRender
    {
        private static readonly HumanBodyBones[] AllBones = (HumanBodyBones[])
            System.Enum.GetValues(typeof(HumanBodyBones));

        private static readonly string[] Clips =
        {
            "CMU_10_01", "CMU_10_02", "CMU_10_03",
            "CMU_10_05", "CMU_10_06", "CMU_11_01",
        };

        private const int Cols = 14;
        private const int W = 360, H = 620;

        [MenuItem("AIfootball/动画/9. 渲染 CMU 动捕射门")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[CmuRender] 找不到 Shooter"); return; }

            var rt = shooter.GetComponent("ShooterRetargeter") as ShooterRetargeter;
            if (rt == null) { Debug.LogError("[CmuRender] 缺 ShooterRetargeter"); return; }

            var anim = shooter.GetComponentInChildren<Animator>();
            if (anim == null) { Debug.LogError("[CmuRender] 缺 Animator"); return; }

            var smr = shooter.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) { Debug.LogError("[CmuRender] 缺 SkinnedMeshRenderer"); return; }

            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }

            // 骨骼清单
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
            var proxy = new GameObject("__cmu_proxy");
            proxy.hideFlags = HideFlags.HideAndDontSave;
            var proxyMf = proxy.AddComponent<MeshFilter>();
            var proxyMr = proxy.AddComponent<MeshRenderer>();
            proxyMr.sharedMaterials = smr.sharedMaterials;
            proxyMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.enabled = false;

            rt.SetAutoUpdate(false);
            rt.CaptureRestPose();

            var sb = new StringBuilder("[CmuRender] 骨骼 " + bones.Count + " 根\n");
            // 相对路径会被解析到 projectPath，而 .tmp_synth 这种点开头目录名 Unity 不接受，
            // 直接用仓库根的绝对路径
            string outDir = System.IO.Path.GetFullPath(Application.dataPath + "/../../.tmp_synth");
            System.IO.Directory.CreateDirectory(outDir);

            // 角色在场景里的初始位置：动捕的髋轨迹以触球帧为原点，叠加到这里即可。
            // 触球帧要站在「球后方 standOffset」处，脚才刚好落在球上。
            Vector3 basePos = shooter.transform.position;
            var ball = GameObject.Find("Ball");

            foreach (string name in Clips)
            {
                var ta = Resources.Load<TextAsset>("ShooterPoses/" + name);
                if (ta == null) { sb.AppendLine("  缺 " + name); continue; }

                var clip = ShooterPoseClip.FromJson(ta.text);
                if (clip == null || clip.FrameCount == 0) { sb.AppendLine("  解析失败 " + name); continue; }

                rt.SetClip(clip);
                rt.CaptureRestPose();

                float dur = clip.Duration;
                int n = Cols;
                var ts = new float[n];
                for (int j = 0; j < n; j++) ts[j] = dur * j / (n - 1);

                // 采 localRotation
                var rots = new Quaternion[n][];
                for (int j = 0; j < n; j++)
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

                var baked = new AnimationClip { name = name + "_baked", legacy = false };
                for (int i = 0; i < bones.Count; i++)
                {
                    var cx = new AnimationCurve(); var cy = new AnimationCurve();
                    var cz = new AnimationCurve(); var cw = new AnimationCurve();
                    for (int j = 0; j < n; j++)
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

                var grid = new Texture2D(W * n, H * 2, TextureFormat.RGBA32, false);
                // 模拟运行时的 KeepFacingTarget：先朝球，再叠 35° 侧身，最后按实测站位后退
                Vector3 ballPos = ball != null ? ball.transform.position : basePos + Vector3.forward * 2f;
                Vector3 toBall = ballPos - basePos; toBall.y = 0f;
                Vector3 fwd = toBall.sqrMagnitude > 1e-4f ? toBall.normalized : shooter.transform.forward;
                Quaternion look = Quaternion.LookRotation(fwd, Vector3.up);
                Quaternion yaw = Quaternion.Euler(0f, 0f, 0f);   // 场景里 _yawOffset = 0，数据自带侧身

                Vector3 strikeBase = clip.standOffset > 0.05f
                    ? ballPos - fwd * clip.standOffset
                    : basePos;
                strikeBase.y = basePos.y;
                Vector3 hip = strikeBase + Vector3.up * 0.92f;

                AnimationMode.StartAnimationMode();
                try
                {
                    for (int view = 0; view < 2; view++)
                    {
                        for (int j = 0; j < n; j++)
                        {
                            // 髋水平轨迹以触球帧为原点：直接加上去，人就沿着真实助跑路线跑上来
                            Vector2 h = clip.SampleHip(ts[j]);
                            shooter.transform.rotation = look * yaw;
                            shooter.transform.position = strikeBase +
                                shooter.transform.rotation * new Vector3(h.x, 0f, h.y);

                            AnimationMode.BeginSampling();
                            AnimationMode.SampleAnimationClip(shooter, baked, ts[j]);
                            AnimationMode.EndSampling();

                            smr.BakeMesh(bakedMesh, true);
                            proxyMf.sharedMesh = bakedMesh;
                            proxy.transform.position = smr.transform.position;
                            proxy.transform.rotation = smr.transform.rotation;

                            // 以球为构图中心：球、支撑脚、踢球脚必须同框，否则看不出脚有没有踢到球
                            Vector3 right = Vector3.Cross(Vector3.up, fwd);
                            Vector3 focus = ballPos + Vector3.up * 0.35f;
                            cam.transform.position = view == 0
                                ? ballPos - fwd * 3.0f + right * 2.4f + Vector3.up * 1.15f
                                : ballPos + right * 3.4f + Vector3.up * 0.95f;
                            cam.transform.LookAt(focus);
                            cam.fieldOfView = 34f;

                            var rtt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                            cam.targetTexture = rtt;
                            cam.Render();
                            RenderTexture.active = rtt;
                            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                            tex.Apply();
                            RenderTexture.active = null;
                            cam.targetTexture = null;
                            grid.SetPixels(j * W, (1 - view) * H, W, H, tex.GetPixels());
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
                string outPath = System.IO.Path.Combine(outDir, "cmu_" + name + ".png");
                System.IO.File.WriteAllBytes(outPath, grid.EncodeToPNG());
                Object.DestroyImmediate(grid);
                Object.DestroyImmediate(baked);
                sb.AppendLine("  " + name + " " + clip.FrameCount + "帧 " + dur.ToString("0.00") + "s → " + outPath);
            }

            shooter.transform.position = basePos;
            smr.enabled = true;
            Object.DestroyImmediate(proxy);
            Object.DestroyImmediate(bakedMesh);
            cam.fieldOfView = 60f;
            Debug.Log(sb.ToString());
        }
    }
}
