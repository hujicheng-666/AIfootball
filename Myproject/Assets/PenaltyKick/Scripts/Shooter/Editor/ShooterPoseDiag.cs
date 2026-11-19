using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PenaltyKickPlatform.Shooter;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 射手动作用于「游戏视角」的复现与诊断。
    ///
    /// CmuShotRender 用电影机位看姿态本身，看不出运行时怪在哪。这支按 Demo.unity 的真实
    /// 站位、真实第三人称机位渲染，并且分两版出图：
    ///   · *_mesh.png —— 只画蒙皮网格，复现玩家看到的画面
    ///   · *_skel.png —— 隐藏网格，只在每根骨的位置画红球
    /// 红点连线歪 = 重定向错；红点正而网格歪 = 蒙皮/骨长错。
    ///
    /// 同时输出「模型骨向量」与「数据骨向量」的夹角表（angles.txt），作为数字证据。
    /// </summary>
    public static class ShooterPoseDiag
    {
        private const int Cols = 10;
        private const int W = 300, H = 480;

        private struct Link
        {
            public HumanBodyBones bone, child;
            public string dataA, dataB;
        }

        private static readonly Link[] Links =
        {
            new Link { bone = HumanBodyBones.Hips,          child = HumanBodyBones.Spine,         dataA = "Center of Hips",     dataB = "Center of Body" },
            new Link { bone = HumanBodyBones.Spine,         child = HumanBodyBones.Chest,         dataA = "Center of Body",     dataB = "Center of Shoulder" },
            new Link { bone = HumanBodyBones.Chest,         child = HumanBodyBones.Neck,          dataA = "Center of Shoulder", dataB = "Neck" },
            new Link { bone = HumanBodyBones.LeftUpperArm,  child = HumanBodyBones.LeftLowerArm,  dataA = "Left Shoulder",      dataB = "Left Elbow" },
            new Link { bone = HumanBodyBones.LeftLowerArm,  child = HumanBodyBones.LeftHand,      dataA = "Left Elbow",         dataB = "Left Wrist" },
            new Link { bone = HumanBodyBones.RightUpperArm, child = HumanBodyBones.RightLowerArm, dataA = "Right Shoulder",     dataB = "Right Elbow" },
            new Link { bone = HumanBodyBones.RightLowerArm, child = HumanBodyBones.RightHand,     dataA = "Right Elbow",        dataB = "Right Wrist" },
            new Link { bone = HumanBodyBones.LeftUpperLeg,  child = HumanBodyBones.LeftLowerLeg,  dataA = "Left Hip",           dataB = "Left Knee" },
            new Link { bone = HumanBodyBones.LeftLowerLeg,  child = HumanBodyBones.LeftFoot,      dataA = "Left Knee",          dataB = "Left Ankle" },
            new Link { bone = HumanBodyBones.RightUpperLeg, child = HumanBodyBones.RightLowerLeg, dataA = "Right Hip",          dataB = "Right Knee" },
            new Link { bone = HumanBodyBones.RightLowerLeg, child = HumanBodyBones.RightFoot,     dataA = "Right Knee",         dataB = "Right Ankle" },
        };

        [MenuItem("AIfootball/动画/10. 诊断射手姿态（游戏视角）")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[Diag] 找不到 Shooter"); return; }

            var rt = shooter.GetComponent<ShooterRetargeter>();
            var anim = shooter.GetComponentInChildren<Animator>();
            var smr = shooter.GetComponentInChildren<SkinnedMeshRenderer>();
            if (rt == null || anim == null || smr == null) { Debug.LogError("[Diag] 组件缺失"); return; }

            var ball = GameObject.Find("Ball");
            Vector3 ballPos = ball != null ? ball.transform.position : shooter.transform.position + Vector3.forward * 2f;
            float groundY = shooter.transform.position.y;

            rt.SetAutoUpdate(false);
            rt.CaptureRestPose();

            string outDir = System.IO.Path.GetFullPath(Application.dataPath + "/../../.tmp_diag");
            System.IO.Directory.CreateDirectory(outDir);

            // 红点（模型骨架）。用大一点的球，并在骨架图里隐藏网格，避免被包在肢体内部。
            var matRed = MakeUnlit(new Color(1f, 0.12f, 0.06f));
            var probes = new List<Transform>();
            int probeCount = Links.Length * 2 + 8;
            for (int i = 0; i < probeCount; i++) probes.Add(Sphere("__p" + i, matRed, 0.055f));

            // 代理网格
            var baked = new Mesh();
            var proxy = new GameObject("__diag_proxy");
            proxy.hideFlags = HideFlags.HideAndDontSave;
            proxy.AddComponent<MeshFilter>();
            var proxyMr = proxy.AddComponent<MeshRenderer>();
            proxyMr.sharedMaterials = smr.sharedMaterials;
            proxyMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.enabled = false;

            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }
            cam.fieldOfView = 40f;

            var rtex = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            rtex.Create();
            var shot = new Texture2D(W, H, TextureFormat.RGBA32, false);
            cam.targetTexture = rtex;

            var sb = new StringBuilder();
            sb.AppendLine("[SMR] pos=" + smr.transform.position + " rot=" + smr.transform.rotation.eulerAngles
                          + " lossyScale=" + smr.transform.lossyScale);
            string[] clips = { "CMU_10_01", "CMU_10_02", "CMU_10_03", "CMU_10_05", "CMU_10_06", "CMU_11_01" };

            foreach (string cname in clips)
            {
                var ta = Resources.Load<TextAsset>("ShooterPoses/" + cname);
                if (ta == null) { sb.AppendLine("缺 " + cname); continue; }
                var clip = ShooterPoseClip.FromJson(ta.text);
                rt.SetClip(clip);

                Vector3 toBall = ballPos - shooter.transform.position; toBall.y = 0f;
                Vector3 fwd = toBall.sqrMagnitude > 1e-4f ? toBall.normalized : shooter.transform.forward;
                float stand = clip.standOffset > 0.05f ? clip.standOffset : 0.9f;
                Vector3 strikeBase = ballPos - fwd * stand; strikeBase.y = groundY;
                Quaternion facing = Quaternion.LookRotation(fwd, Vector3.up);

                var gridMesh = new Texture2D(W * Cols, H, TextureFormat.RGBA32, false);
                var gridSkel = new Texture2D(W * Cols, H, TextureFormat.RGBA32, false);
                sb.AppendLine("=== " + cname + "  " + clip.FrameCount + "f  kick=" + clip.kickFrame
                              + "  stand=" + stand.ToString("0.00"));

                for (int j = 0; j < Cols; j++)
                {
                    float t = clip.Duration * j / (Cols - 1);
                    if (j == Cols - 1) t = clip.Duration - 0.001f;

                    Vector2 h = clip.SampleHip(t);
                    shooter.transform.rotation = facing;
                    shooter.transform.position = strikeBase + facing * new Vector3(h.x, 0f, h.y);

                    rt.SetTime(t);
                    rt.ApplyPose();

                    Vector3 hip = shooter.transform.position + Vector3.up * 0.95f;
                    cam.transform.position = hip - fwd * 3.30f + Vector3.up * 1.85f;
                    cam.transform.LookAt(hip + Vector3.up * 0.05f);
                    cam.clearFlags = CameraClearFlags.SolidColor;
                    cam.backgroundColor = new Color(0.42f, 0.55f, 0.35f);

                    // ── 第 1 版：蒙皮网格
                    smr.BakeMesh(baked);
                    proxy.GetComponent<MeshFilter>().sharedMesh = baked;
                    proxy.transform.position = smr.transform.position;
                    proxy.transform.rotation = smr.transform.rotation;
                    proxy.transform.localScale = Vector3.one;   // BakeMesh 顶点已含骨骼的世界尺度
                    for (int i = 0; i < probes.Count; i++) probes[i].position = Vector3.one * -9999f;
                    Blit(cam, rtex, shot, gridMesh, j);

                    // ── 第 2 版：骨架红点
                    proxyMr.enabled = false;
                    int k = 0;
                    foreach (var lk in Links)
                    {
                        var b = anim.GetBoneTransform(lk.bone);
                        var c = anim.GetBoneTransform(lk.child);
                        probes[k].position = b != null ? b.position : Vector3.one * -9999f;
                        probes[k + 1].position = c != null ? c.position : Vector3.one * -9999f;
                        k += 2;
                    }
                    var headB = anim.GetBoneTransform(HumanBodyBones.Head);
                    var neckB = anim.GetBoneTransform(HumanBodyBones.Neck);
                    var ballB = ball != null ? ball.transform : null;
                    if (k < probes.Count) probes[k++].position = headB != null ? headB.position + Vector3.up * 0.10f : Vector3.one * -9999f;
                    if (k < probes.Count) probes[k++].position = neckB != null ? neckB.position : Vector3.one * -9999f;
                    if (k < probes.Count) probes[k++].position = ballB != null ? ballB.position : Vector3.one * -9999f;
                    for (; k < probes.Count; k++) probes[k].position = Vector3.one * -9999f;
                    proxyMr.enabled = false;      // 骨架版：只留红点
                    Blit(cam, rtex, shot, gridSkel, j);
                    proxyMr.enabled = true;

                    {
                        var hipsT = anim.GetBoneTransform(HumanBodyBones.Hips);
                        var kneeT = anim.GetBoneTransform(HumanBodyBones.RightLowerLeg);
                        var ankleT = anim.GetBoneTransform(HumanBodyBones.RightFoot);
                        if (hipsT != null && kneeT != null && ankleT != null)
                            sb.AppendLine(string.Format("  j={0} t={1:0.000}  膝-髋 ({2,6:0.00},{3,6:0.00},{4,6:0.00})  踝-髋 ({5,6:0.00},{6,6:0.00},{7,6:0.00})",
                                j, t,
                                kneeT.position.x - hipsT.position.x, kneeT.position.y - hipsT.position.y, kneeT.position.z - hipsT.position.z,
                                ankleT.position.x - hipsT.position.x, ankleT.position.y - hipsT.position.y, ankleT.position.z - hipsT.position.z));
                    }

                    if (j == Cols / 2 || j == 6)
                    {
                        if (j == 5)
                        {
                            sb.AppendLine("  -- j=" + j + " t=" + t.ToString("0.000") + " --");
                            foreach (var lk in Links) AppendAngle(sb, anim, clip, t, facing);
                            AppendPoints(sb, anim, clip, t);
                        }
                    }
                }

                gridMesh.Apply(); gridSkel.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "diag_" + cname + "_mesh.png"), gridMesh.EncodeToPNG());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(outDir, "diag_" + cname + "_skel.png"), gridSkel.EncodeToPNG());
                Object.DestroyImmediate(gridMesh); Object.DestroyImmediate(gridSkel);
            }

            cam.targetTexture = null;
            smr.enabled = true;
            Object.DestroyImmediate(proxy);
            System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "angles.txt"), sb.ToString());
            Debug.Log("[Diag] 完成，见 " + outDir);
        }

        private static void Blit(Camera cam, RenderTexture rtex, Texture2D shot, Texture2D grid, int col)
        {
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rtex;
            shot.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            shot.Apply();
            RenderTexture.active = prev;
            grid.SetPixels(col * W, 0, W, H, shot.GetPixels());
        }

        /// <summary>模型骨向量 vs 数据骨向量的夹角（度）。接近 0 说明重定向本身没歪。</summary>
        private static void AppendAngle(StringBuilder sb, Animator anim, ShooterPoseClip clip, float t, Quaternion facing)
        {
            foreach (var lk in Links)
            {
                var b = anim.GetBoneTransform(lk.bone);
                var c = anim.GetBoneTransform(lk.child);
                int ia = clip.IndexOfJoint(lk.dataA);
                int ib = clip.IndexOfJoint(lk.dataB);
                if (b == null || c == null || ia < 0 || ib < 0) continue;

                Vector3 mv = c.position - b.position;
                Vector3 dv = facing * (clip.SampleJoint(t, ib) - clip.SampleJoint(t, ia));
                float a = Vector3.Angle(mv, dv);
                sb.AppendLine(string.Format("  {0,-26} 夹角 {1,6:0.0}°   模型长 {2:0.000}m  数据长 {3:0.000}m",
                    lk.bone + "→" + lk.child, a, mv.magnitude, dv.magnitude));
            }
            sb.AppendLine();
        }

        /// <summary>直接打印腿与手臂关键骨骼的世界坐标，和数据的关节坐标对照。</summary>
        private static void AppendPoints(StringBuilder sb, Animator anim, ShooterPoseClip clip, float t)
        {
            var pairs = new[]
            {
                new { bone = HumanBodyBones.Hips,        name = "Center of Hips" },
                new { bone = HumanBodyBones.RightUpperLeg, name = "Right Hip" },
                new { bone = HumanBodyBones.RightLowerLeg, name = "Right Knee" },
                new { bone = HumanBodyBones.RightFoot,   name = "Right Ankle" },
                new { bone = HumanBodyBones.RightUpperArm, name = "Right Shoulder" },
                new { bone = HumanBodyBones.RightHand,   name = "Right Wrist" },
            };

            Vector3 hip = anim.GetBoneTransform(HumanBodyBones.Hips).position;
            sb.AppendLine("    骨骼世界坐标（相对 Hips）：");
            foreach (var p in pairs)
            {
                var tr = anim.GetBoneTransform(p.bone);
                if (tr == null) continue;
                Vector3 w = tr.position - hip;
                Vector3 d = clip.SampleJoint(t, clip.IndexOfJoint(p.name));
                sb.AppendLine(string.Format("      {0,-14} 模型 ({1,6:0.00},{2,6:0.00},{3,6:0.00})   数据 ({4,6:0.00},{5,6:0.00},{6,6:0.00})",
                    p.bone, w.x, w.y, w.z, d.x, d.y, d.z));
            }
            sb.AppendLine();
        }

        private static Transform Sphere(string name, Material mat, float r)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = name;
            go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.localScale = Vector3.one * r;
            var collider = go.GetComponent<Collider>();
            if (collider != null) Object.DestroyImmediate(collider);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go.transform;
        }

        private static Material MakeUnlit(Color c)
        {
            var sh = Shader.Find("Unlit/Color");
            if (sh == null) sh = Shader.Find("Standard");
            var m = new Material(sh);
            m.color = c;
            return m;
        }
    }
}
