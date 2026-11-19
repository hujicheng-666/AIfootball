using System.IO;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 渲染混元角色的 T-pose 与一个极端姿态，用来目视检查蒙皮质量：
    /// 关节是否撕裂、腋下/胯部是否粘连、脚是否踩在地面。
    /// </summary>
    public static class HunyuanVerify
    {
        private const string OutDir = ".tmp_hunyuan_verify";
        private const int W = 760;
        private const int H = 900;

        [MenuItem("AIfootball/混元/3. 渲染验证")]
        public static void VerifyAll()
        {
            RenderOne("Assets/SuperGoalie/Meshes/Characters/Hunyuan/ShooterRig.prefab", "shooter");
            RenderOne("Assets/SuperGoalie/Meshes/Characters/Hunyuan/GoalkeeperRig.prefab", "goalkeeper");
        }

        private static void RenderOne(string prefabPath, string tag)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                Debug.LogError("[HunyuanVerify] 找不到 " + prefabPath);
                return;
            }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.position = Vector3.zero;

            var animator = inst.GetComponent<Animator>();
            if (animator != null) animator.enabled = false;   // 不让 Animator 覆盖手动姿态

            // 相机与光
            var camGo = new GameObject("VCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.86f, 0.88f, 0.91f);
            cam.fieldOfView = 32f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 30f;
            camGo.transform.position = new Vector3(1.9f, 1.15f, 3.3f);
            camGo.transform.LookAt(new Vector3(0f, 0.92f, 0f));

            var lightGo = new GameObject("VLight");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            lightGo.transform.rotation = Quaternion.Euler(38f, -34f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.46f, 0.49f, 0.54f);

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;
            cam.Render();
            var texA = ReadTex(rt);            // T-pose 正面
            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr != null)
                Debug.Log("[HunyuanVerify] " + tag + " T-pose bounds=" + smr.bounds.size +
                          " bones=" + smr.bones.Length);

            // 诊断开关：HN_NO_NORMAL=1 时去掉法线贴图，用于判断黑斑是不是法线方向问题
            if (System.Environment.GetEnvironmentVariable("HN_NO_NORMAL") == "1" && smr != null)
            {
                var mat = smr.sharedMaterial;
                if (mat != null)
                {
                    mat.SetTexture("_BumpMap", null);
                    mat.DisableKeyword("_NORMALMAP");
                    Debug.Log("[HunyuanVerify] 已禁用法线贴图");
                }
            }

            // ── 用真实扑救动画采样，顺便验证 Humanoid 兼容性 ──
            // 手动改 Transform 在 batchmode 下不会刷新 GPU 骨骼矩阵缓存（实测 md5 不变），
            // 必须走 AnimationMode 采样，这也是实际运行时的路径。
            var clip = LoadClip("Assets/SuperGoalie/Animations/GoalKeeperAnimations/PunchHighLeft.fbx");
            if (clip != null)
            {
                AnimationMode.StartAnimationMode();
                AnimationMode.BeginSampling();
                AnimationMode.SampleAnimationClip(inst, clip, Mathf.Min(0.34f, clip.length * 0.55f));
                AnimationMode.EndSampling();
                Debug.Log("[HunyuanVerify] " + tag + " 采样 " + clip.name + " len=" + clip.length.ToString("F2") +
                          " 骨骼(Hips)=" + FindBone(inst, "Hips").position.ToString("F3"));
            }
            else
            {
                Debug.LogWarning("[HunyuanVerify] 找不到扑救动画剪辑");
            }

            cam.Render();
            var texB = ReadTex(rt);            // 动画帧 正面
            if (smr != null) DumpSkinnedExtremes(smr, tag + "-动画后");

            camGo.transform.position = new Vector3(3.5f, 1.2f, 0.6f);
            camGo.transform.LookAt(new Vector3(0f, 0.92f, 0f));
            cam.Render();
            var texC = ReadTex(rt);            // 动画帧 侧面

            if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();

            // ── 拼图 ──
            var combined = new Texture2D(W * 3, H, TextureFormat.RGBA32, false);
            var px = new Color32[W * 3 * H];
            Copy(texA, px, 0);
            Copy(texB, px, 1);
            Copy(texC, px, 2);
            combined.SetPixels32(px);
            combined.Apply();

            Directory.CreateDirectory(OutDir);
            var outPath = Path.Combine(OutDir, tag + ".png");
            File.WriteAllBytes(outPath, combined.EncodeToPNG());
            Debug.Log("[HunyuanVerify] 已写出 " + Path.GetFullPath(outPath));

            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lightGo);
            Object.DestroyImmediate(combined);
            Object.DestroyImmediate(inst);
        }

        private static void Pose(GameObject root, string boneName, Vector3 euler)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == boneName)
                {
                    t.localRotation = Quaternion.Euler(euler);
                    return;
                }
            }
            Debug.LogWarning("[HunyuanVerify] 找不到骨骼 " + boneName);
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

        private static Transform FindBone(GameObject root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == name) return t;
            return null;
        }

        private static void DumpSkinnedExtremes(SkinnedMeshRenderer smr, string tag)
        {
            var m = new Mesh();
            smr.BakeMesh(m, true);
            var v = m.vertices;
            float xMin = float.MaxValue, xMax = float.MinValue, yMax = float.MinValue;
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i].x < xMin) xMin = v[i].x;
                if (v[i].x > xMax) xMax = v[i].x;
                if (v[i].y > yMax) yMax = v[i].y;
            }
            Debug.Log(string.Format("[HunyuanVerify] {0} CPU蒙皮 x=[{1:F3},{2:F3}] yMax={3:F3} 顶点={4:N0}",
                tag, xMin, xMax, yMax, v.Length));
            Object.DestroyImmediate(m);
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

        private static void Copy(Texture2D src, Color32[] dst, int slot)
        {
            var px = src.GetPixels32();
            for (int y = 0; y < H; y++)
            {
                int srcRow = y * W;
                int dstRow = y * (W * 3) + slot * W;
                for (int x = 0; x < W; x++)
                    dst[dstRow + x] = px[srcRow + x];
            }
            Object.DestroyImmediate(src);
        }
    }
}
