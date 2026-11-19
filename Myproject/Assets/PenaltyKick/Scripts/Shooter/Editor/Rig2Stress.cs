using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 蒙皮压力测试：把关节掰到极限角度，用 CPU 线性混合蒙皮（LBS）逐顶点重算，
    /// 直接渲染变形后的网格。
    ///
    /// 为什么不用 SkinnedMeshRenderer：batchmode 下改 Transform 不刷新 GPU 骨骼缓存，
    /// 渲染出来的还是 T-pose。只有手算 Σ w·(boneWorld·bindpose)·v 才可靠，
    /// 而且这正好就是运行时 GPU 做的事，等价于真实效果。
    /// </summary>
    public static class Rig2Stress
    {
        private const string Root = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/";
        private const string OutDir = ".tmp_rig2_stress";
        private const int W = 520;
        private const int H = 900;

        [MenuItem("AIfootball/混元FBX/8. 蒙皮压力测试")]
        public static void Run()
        {
            Directory.CreateDirectory(OutDir);
            Stress(Root + "GoalkeeperRig2.prefab", "gk2");
            Stress(Root + "ShooterRig2.prefab", "sh2");
        }

        // 姿态：骨骼名 → 父空间欧拉角。X=前后摆(ZY面)，Y=扭转，Z=左右摆(XY面)
        private struct Pose
        {
            public string name;
            public Vector3 view;      // 相机方向（世界），用来选最暴露缺陷的角度
            public (string bone, float x, float y, float z)[] rots;
        }

        private static readonly Pose[] Poses =
        {
            new Pose { name = "手臂上举", view = new Vector3(0f, 0.1f, 1f), rots = new[]
            {
                ("UpperArmR", 0f, 0f, 92f), ("UpperArmL", 0f, 0f, -92f),
                ("LowerArmR", 0f, 0f, 25f), ("LowerArmL", 0f, 0f, -25f),
            }},
            new Pose { name = "深蹲", view = new Vector3(1f, 0.12f, 0.30f), rots = new[]
            {
                ("UpperLegR", 78f, 0f, 8f), ("UpperLegL", 78f, 0f, -8f),
                ("LowerLegR", -108f, 0f, 0f), ("LowerLegL", -108f, 0f, 0f),
                ("FootR", 34f, 0f, 0f), ("FootL", 34f, 0f, 0f),
                ("Spine", -16f, 0f, 0f),
            }},
            new Pose { name = "躯干前屈", view = new Vector3(1f, 0.05f, 0.22f), rots = new[]
            {
                ("Spine", -42f, 0f, 0f), ("Chest", -30f, 0f, 0f),
                ("UpperArmR", 0f, 0f, -55f), ("UpperArmL", 0f, 0f, 55f),
                ("UpperLegR", 18f, 0f, 0f), ("UpperLegL", 18f, 0f, 0f),
            }},
            new Pose { name = "横扫踢腿", view = new Vector3(0.45f, 0.12f, 1f), rots = new[]
            {
                ("UpperLegR", 0f, 0f, -68f), ("LowerLegR", -22f, 0f, 0f),
                ("Spine", 6f, 0f, 0f), ("Chest", 4f, 18f, 0f),
                ("UpperArmR", 0f, 0f, 40f), ("UpperArmL", 0f, 0f, -20f),
            }},
        };

        private static void Stress(string prefabPath, string tag)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) { Debug.LogError("[Stress] 找不到 " + prefabPath); return; }

            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;

            var smr = inst.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (smr == null) { Debug.LogError("[Stress] 无 SkinnedMeshRenderer"); return; }
            var mesh = smr.sharedMesh;
            var bones = smr.bones;
            var bps = mesh.bindposes;
            var bw = mesh.boneWeights;
            var verts = mesh.vertices;
            int nv = verts.Length;

            // 原始骨骼旋转，逐姿态复位
            var origRot = new Quaternion[bones.Length];
            for (int i = 0; i < bones.Length; i++) origRot[i] = bones[i].localRotation;

            var texs = new Texture2D[Poses.Length];
            var camGo = new GameObject("SCam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.28f, 0.30f, 0.34f);
            cam.fieldOfView = 34f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 40f;

            var lg = new GameObject("SLight");
            var light = lg.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            lg.transform.rotation = Quaternion.Euler(36f, -30f, 0f);
            var lg2 = new GameObject("SLight2");
            var l2 = lg2.AddComponent<Light>();
            l2.type = LightType.Directional;
            l2.intensity = 0.4f;
            lg2.transform.rotation = Quaternion.Euler(10f, 160f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.48f, 0.50f, 0.54f);

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;

            for (int pi = 0; pi < Poses.Length; pi++)
            {
                // 复位
                for (int i = 0; i < bones.Length; i++) bones[i].localRotation = origRot[i];
                // 施加姿态
                foreach (var (bn, rx, ry, rz) in Poses[pi].rots)
                {
                    var t = FindBone(bones, bn);
                    if (t == null) { Debug.LogWarning("[Stress] 缺骨骼 " + bn); continue; }
                    t.localRotation = Quaternion.Euler(rx, ry, rz);
                }

                // 预计算每根骨的 skin 矩阵。
                // 注意：不能直接用 bone.localToWorldMatrix —— batchmode 不跑渲染循环，
                // Transform 的世界矩阵缓存不会因改 localRotation 而刷新（实测深蹲完全没弯）。
                // 这里自己沿层级递归算 TRS，结果与运行时一致。
                var wcache = new System.Collections.Generic.Dictionary<Transform, Matrix4x4>();
                Matrix4x4 GetWorld(Transform t)
                {
                    if (wcache.TryGetValue(t, out var cached)) return cached;
                    var local = Matrix4x4.TRS(t.localPosition, t.localRotation, t.localScale);
                    var m = t.parent == null ? local : GetWorld(t.parent) * local;
                    wcache[t] = m;
                    return m;
                }

                var skin = new Matrix4x4[bones.Length];
                for (int i = 0; i < bones.Length; i++)
                    skin[i] = GetWorld(bones[i]) * bps[i];

                // 校验：旋转确实改变了骨骼世界位置
                var footT = FindBone(bones, "FootR");
                if (footT != null)
                    Debug.Log(string.Format("[Stress] {0}/{1} FootR pos={2} (校验矩阵已更新)",
                        tag, Poses[pi].name, GetWorld(footT).GetColumn(3)));

                // LBS
                var outV = new Vector3[nv];
                for (int v = 0; v < nv; v++)
                {
                    var b = bw[v];
                    Vector3 p = verts[v];
                    Vector3 acc = Vector3.zero;
                    if (b.weight0 > 0f) acc += skin[b.boneIndex0].MultiplyPoint3x4(p) * b.weight0;
                    if (b.weight1 > 0f) acc += skin[b.boneIndex1].MultiplyPoint3x4(p) * b.weight1;
                    if (b.weight2 > 0f) acc += skin[b.boneIndex2].MultiplyPoint3x4(p) * b.weight2;
                    if (b.weight3 > 0f) acc += skin[b.boneIndex3].MultiplyPoint3x4(p) * b.weight3;
                    outV[v] = acc;
                }

                // 临时网格渲染（不含蒙皮，顶点已经是最终位置）
                var tm = new Mesh();
                tm.indexFormat = mesh.indexFormat;
                tm.vertices = outV;
                tm.normals = mesh.normals;
                tm.uv = mesh.uv;
                tm.triangles = mesh.triangles;
                tm.RecalculateNormals();
                tm.RecalculateBounds();

                var go = new GameObject("StressMesh");
                go.AddComponent<MeshFilter>().sharedMesh = tm;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = smr.sharedMaterial;

                var bb = tm.bounds;
                var dir = Poses[pi].view.normalized;
                float dist = bb.extents.magnitude * 2.1f + 1.2f;
                camGo.transform.position = bb.center + dir * dist;
                camGo.transform.LookAt(bb.center);
                cam.Render();
                texs[pi] = ReadTex(rt);

                Debug.Log(string.Format("[Stress] {0} / {1} 包围盒={2} 高度={3:F3}",
                    tag, Poses[pi].name, bb.size.ToString("F2"), bb.size.y));

                Object.DestroyImmediate(tm);
                Object.DestroyImmediate(go);
            }

            // 复位
            for (int i = 0; i < bones.Length; i++) bones[i].localRotation = origRot[i];

            Compose(tag, texs);

            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lg);
            Object.DestroyImmediate(lg2);
            Object.DestroyImmediate(inst);
        }

        private static Transform FindBone(Transform[] bones, string name)
        {
            foreach (var b in bones) if (b.name == name) return b;
            return null;
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
            string p = Path.Combine(OutDir, tag + "_stress.png");
            File.WriteAllBytes(p, combined.EncodeToPNG());
            Debug.Log("[Stress] 写出 " + Path.GetFullPath(p));
            foreach (var t in texs) Object.DestroyImmediate(t);
            Object.DestroyImmediate(combined);
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
