using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PenaltyKickPlatform.Shooter;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 渲染合成射门动作的时间序列。
    ///
    /// 为什么不用「改 Transform 再 cam.Render()」：batchmode 下 SkinnedMeshRenderer 的
    /// 骨骼矩阵缓存在渲染前不会被 Transform 变更标记刷新，手写的 bone.rotation 读得到
    /// （用子父位置差核对夹角为 0°），但画面上网格纹丝不动。必须走动画系统自己的求值
    /// 路径：把每帧的 localRotation 烘成 AnimationClip，再用 AnimationMode 采样。
    /// </summary>
    public static class ShooterSynthCheck
    {
        private static readonly HumanBodyBones[] AllBones = (HumanBodyBones[])
            System.Enum.GetValues(typeof(HumanBodyBones));

        [MenuItem("AIfootball/动画/7. 验证合成射门动作")]
        public static void Run()
        {
            EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[SynthCheck] 找不到 Shooter"); return; }

            var rt = shooter.GetComponent("ShooterRetargeter") as ShooterRetargeter;
            if (rt == null) { Debug.LogError("[SynthCheck] 缺 ShooterRetargeter"); return; }
            rt.SetAutoUpdate(false);
            rt.CaptureRestPose();

            var anim = shooter.GetComponentInChildren<Animator>();
            if (anim == null) { Debug.LogError("[SynthCheck] 缺 Animator"); return; }

            var ta = Resources.Load<TextAsset>("ShooterPoses/SYN17");
            if (ta == null) { Debug.LogError("[SynthCheck] 缺 ShooterPoses/SYN17.json"); return; }
            var clip = ShooterPoseClip.FromJson(ta.text);
            if (clip == null) { Debug.LogError("[SynthCheck] 解析失败"); return; }
            rt.SetClip(clip);

            var sb = new StringBuilder("[SynthCheck] 帧数=" + clip.FrameCount +
                " 时长=" + clip.Duration + " 触球帧=" + clip.kickFrame + "\n");

            // 12 个时刻：预备 → 后摆 → 加速 → 触球 → 随挥 → 落地 → 站定
            float[] ts = { 0.00f, 0.15f, 0.30f, 0.45f, 0.58f, 0.70f,
                           0.84f, 1.00f, 1.13f, 1.28f, 1.50f, 1.733f };

            // 收集骨骼
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
            sb.AppendLine("[SynthCheck] 参与烘焙的骨骼 " + bones.Count + " 根");

            // 1) 采集每帧的 localRotation（四元数做符号对齐，避免插值翻面）
            int n = ts.Length;
            var rots = new Quaternion[n][];
            for (int j = 0; j < n; j++)
            {
                rt.SetTime(ts[j]);
                rt.ApplyPose();
                rots[j] = new Quaternion[bones.Count];
                for (int i = 0; i < bones.Count; i++)
                {
                    Quaternion q = bones[i].localRotation;
                    if (j > 0)
                    {
                        Quaternion prev = rots[j - 1][i];
                        if (Quaternion.Dot(prev, q) < 0f)
                            q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
                    }
                    rots[j][i] = q;
                }
            }

            // 2) 烘成 AnimationClip
            // 采集是否真的采到了变化，直接对比首帧与触球帧
            int checkIdx = -1;
            for (int i = 0; i < paths.Count; i++)
                if (paths[i].ToUpperInvariant().EndsWith("UPPERLEGR")) { checkIdx = i; break; }
            sb.AppendLine("[SynthCheck] animator宿主=" + anim.gameObject.name +
                          " 采样根=" + shooter.name +
                          " controller=" + (anim.runtimeAnimatorController != null ? "有" : "无") +
                          " anim.enabled=" + anim.enabled);
            if (checkIdx >= 0)
            {
                sb.AppendLine("  UpperLegR localRot[0]=" + rots[0][checkIdx].eulerAngles +
                              "  [5]=" + rots[5][checkIdx].eulerAngles +
                              "  path=" + paths[checkIdx]);
            }
            else sb.AppendLine("  !! 没找到 UpperLegR 路径，骨骼名可能不同");

            var baked = new AnimationClip { name = "SYN17_baked", legacy = false };
            for (int i = 0; i < bones.Count; i++)
            {
                var cx = new AnimationCurve();
                var cy = new AnimationCurve();
                var cz = new AnimationCurve();
                var cw = new AnimationCurve();
                for (int j = 0; j < n; j++)
                {
                    float t = ts[j];
                    Quaternion q = rots[j][i];
                    cx.AddKey(t, q.x); cy.AddKey(t, q.y); cz.AddKey(t, q.z); cw.AddKey(t, q.w);
                }
                // 平滑切线：关键帧之间用贝塞尔，避免线性插值的折角
                foreach (var c in new[] { cx, cy, cz, cw })
                    for (int k = 0; k < c.length; k++)
                        c.SmoothTangents(k, 0f);

                string p = paths[i];
                baked.SetCurve(p, typeof(Transform), "localRotation.x", cx);
                baked.SetCurve(p, typeof(Transform), "localRotation.y", cy);
                baked.SetCurve(p, typeof(Transform), "localRotation.z", cz);
                baked.SetCurve(p, typeof(Transform), "localRotation.w", cw);
            }

            // 3) 采样渲染
            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }

            int W = 340, H = 600;
            var grid = new Texture2D(W * n, H * 2, TextureFormat.RGBA32, false);
            Vector3 hipWorld = shooter.transform.position + Vector3.up * 0.92f;

            // batchmode 下 SkinnedMeshRenderer 的骨骼矩阵不随 Transform 刷新：AnimationMode
            // 采样后 Transform 值确实变了（子父位置差与数据逐帧吻合，夹角 0~13°），但画面上
            // 网格纹丝不动。改用 BakeMesh 在 CPU 端算变形网格，交给普通 MeshRenderer 画。
            var smr = shooter.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) { Debug.LogError("[SynthCheck] 缺 SkinnedMeshRenderer"); return; }
            var bakedMesh = new Mesh();
            var proxy = new GameObject("__synth_proxy");
            proxy.hideFlags = HideFlags.HideAndDontSave;
            var proxyMf = proxy.AddComponent<MeshFilter>();
            var proxyMr = proxy.AddComponent<MeshRenderer>();
            proxyMr.sharedMaterials = smr.sharedMaterials;
            proxyMr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            smr.enabled = false;

            AnimationMode.StartAnimationMode();
            try
            {
                for (int view = 0; view < 2; view++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        AnimationMode.BeginSampling();
                        AnimationMode.SampleAnimationClip(shooter, baked, ts[j]);
                        AnimationMode.EndSampling();

                        smr.BakeMesh(bakedMesh, true);
                        proxyMf.sharedMesh = bakedMesh;
                        proxy.transform.position = smr.transform.position;
                        proxy.transform.rotation = smr.transform.rotation;

                        cam.transform.position = view == 0
                            ? hipWorld + new Vector3(0f, 0.10f, -3.9f)     // 正对球门（看背面）
                            : hipWorld + new Vector3(3.9f, 0.10f, 0f);     // 正侧面（看摆腿弧线）
                        cam.transform.LookAt(hipWorld);
                        cam.fieldOfView = 32f;

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
                smr.enabled = true;
                Object.DestroyImmediate(proxy);
                Object.DestroyImmediate(bakedMesh);
            }

            cam.fieldOfView = 60f;
            grid.Apply();
            System.IO.Directory.CreateDirectory(".tmp_synth");
            System.IO.File.WriteAllBytes(".tmp_synth/synth_shot.png", grid.EncodeToPNG());
            rt.ApplyRestPose();
            Debug.Log(sb + "[SynthCheck] 写出 " + System.IO.Path.GetFullPath(".tmp_synth/synth_shot.png"));
        }
    }
}
