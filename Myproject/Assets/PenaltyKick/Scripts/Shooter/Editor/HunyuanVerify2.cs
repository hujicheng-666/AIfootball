using System.IO;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 第二代绑骨的渲染验证：T-pose 正/侧 + 真实动画帧。
    /// 判定标准：静止姿势不变形、关节弯曲处不撕裂、脚踩地。
    /// </summary>
    public static class HunyuanVerify2
    {
        private const string OutDir = ".tmp_rig2_verify";
        private const int W = 620;
        private const int H = 860;
        private const string Root = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/";

        [MenuItem("AIfootball/混元FBX/5. 渲染验证")]
        public static void VerifyAll()
        {
            Directory.CreateDirectory(OutDir);
            RenderOne(Root + "GoalkeeperRig2.prefab", "gk2");
            RenderOne(Root + "ShooterRig2.prefab", "sh2");
        }

        private static void RenderOne(string prefabPath, string tag)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { Debug.LogError("[Verify2] 找不到 " + prefabPath); return; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;

            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            Debug.Log(string.Format("[Verify2] {0} bounds={1} bones={2}",
                tag, smr != null ? smr.bounds.size.ToString("F3") : "null",
                smr != null ? smr.bones.Length : 0));

            var camGo = new GameObject("VCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.30f, 0.32f, 0.35f);
            cam.fieldOfView = 30f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 40f;

            var lg = new GameObject("VLight");
            var light = lg.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            lg.transform.rotation = Quaternion.Euler(38f, -34f, 0f);
            var lg2 = new GameObject("VLight2");
            var light2 = lg2.AddComponent<Light>();
            light2.type = LightType.Directional;
            light2.intensity = 0.45f;
            lg2.transform.rotation = Quaternion.Euler(15f, 150f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.44f, 0.46f, 0.50f);

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;

            Vector3 look = new Vector3(0f, 0.95f, 0f);

            // 1) T-pose 正面
            camGo.transform.position = new Vector3(0f, 1.05f, 4.6f);
            camGo.transform.LookAt(look);
            cam.Render();
            var texA = ReadTex(rt);

            // 2) T-pose 侧面
            camGo.transform.position = new Vector3(4.6f, 1.05f, 0.30f);
            camGo.transform.LookAt(look);
            cam.Render();
            var texB = ReadTex(rt);

            // 3) 真实动画帧 —— 门将扑救，用来检验蒙皮与 Humanoid 重定向
            var animator = inst.GetComponent<Animator>();
            var clip = LoadClip("Assets/SuperGoalie/Animations/GoalKeeperAnimations/PunchHighLeft.fbx");
            if (clip != null && animator != null && animator.avatar != null)
            {
                AnimationMode.StartAnimationMode();
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(inst, clip, Mathf.Min(0.34f, clip.length * 0.55f));
                AnimationMode.EndSampling();

                camGo.transform.position = new Vector3(0f, 1.10f, 4.8f);
                camGo.transform.LookAt(new Vector3(0f, 1.0f, 0f));
                cam.Render();
                var texC = ReadTex(rt);

                // 侧面动画帧
                camGo.transform.position = new Vector3(4.8f, 1.10f, 0.4f);
                camGo.transform.LookAt(new Vector3(0f, 1.0f, 0f));
                cam.Render();
                var texD = ReadTex(rt);

                if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
                Compose(tag, new[] { texA, texB, texC, texD });
                DumpExtremes(smr, tag + "-动画后");
            }
            else
            {
                Debug.LogWarning("[Verify2] 无动画剪辑或 Avatar，跳过动画帧");
                Compose(tag, new[] { texA, texB });
            }

            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lg);
            Object.DestroyImmediate(lg2);
            Object.DestroyImmediate(inst);
        }

        private static void Compose(string tag, Texture2D[] texs)
        {
            int n = texs.Length;
            var combined = new Texture2D(W * n, H, TextureFormat.RGBA32, false);
            var px = new Color32[W * n * H];
            for (int s = 0; s < n; s++)
            {
                var src = texs[s].GetPixels32();
                for (int y = 0; y < H; y++)
                {
                    int so = y * W, d0 = y * (W * n) + s * W;
                    for (int x = 0; x < W; x++) px[d0 + x] = src[so + x];
                }
            }
            combined.SetPixels32(px);
            combined.Apply();
            string p = Path.Combine(OutDir, tag + ".png");
            File.WriteAllBytes(p, combined.EncodeToPNG());
            Debug.Log("[Verify2] 写出 " + Path.GetFullPath(p));
            foreach (var t in texs) Object.DestroyImmediate(t);
            Object.DestroyImmediate(combined);
        }

        private static void DumpExtremes(SkinnedMeshRenderer smr, string tag)
        {
            if (smr == null) return;
            var m = new Mesh();
            smr.BakeMesh(m, true);
            var v = m.vertices;
            float xmin = float.MaxValue, xmax = float.MinValue, ymax = float.MinValue, ymin = float.MaxValue;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i].x < xmin) xmin = v[i].x;
                if (v[i].x > xmax) xmax = v[i].x;
                if (v[i].y > ymax) ymax = v[i].y;
                if (v[i].y < ymin) ymin = v[i].y;
            }
            Debug.Log(string.Format("[Verify2] {0} CPU蒙皮 x=[{1:F2},{2:F2}] y=[{3:F2},{4:F2}] 顶点={5:N0}",
                tag, xmin, xmax, ymin, ymax, v.Length));
            Object.DestroyImmediate(m);
        }

        private static AnimationClip LoadClip(string fbxPath)
        {
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
            {
                var c = o as AnimationClip;
                if (c != null && !c.name.StartsWith("__preview")) return c;
            }
            return null;
        }

        private static Texture2D ReadTex(RenderTexture rt)
        {
            RenderTexture.active = rt;
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            return tex;
        }
    }
}
