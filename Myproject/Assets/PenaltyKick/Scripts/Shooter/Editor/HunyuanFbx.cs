using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 混元 FBX（纯雕塑、无骨骼）的导入探测与正交视图渲染。
    ///
    /// 目的：绑骨前先把模型"看清楚"——正交五视图 + 关键身高刻度，
    /// 用来人工判读肩宽、裆高、膝高、踝高这些真实几何，而不是套解剖学比例。
    /// </summary>
    public static class HunyuanFbx
    {
        private const string Dir = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/FbxIn";
        private const string OutDir = ".tmp_fbx_probe";
        private const int W = 560;
        private const int H = 820;

        private static readonly string[] Models = { "ModelA", "ModelB" };

        [MenuItem("AIfootball/混元FBX/1. 导入并探测")]
        public static void ImportAndProbe()
        {
            foreach (var n in Models) Probe(Dir + "/" + n + ".fbx");
            AssetDatabase.SaveAssets();
        }

        // ─────────────────────────── 探测 ───────────────────────────

        private static void Probe(string path)
        {
            AssetDatabase.ImportAsset(path,
                ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                Debug.LogError("[Fbx] 导入失败：" + path);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[Fbx] ===== " + Path.GetFileName(path) + " =====");
            sb.AppendLine(string.Format("  根旋转={0}  缩放={1}  子物体数={2}",
                go.transform.localEulerAngles, go.transform.localScale,
                go.GetComponentsInChildren<Transform>(true).Length));

            foreach (var t in go.GetComponentsInChildren<Transform>(true))
            {
                if (t.childCount == 0 && t.GetComponent<MeshFilter>() == null) continue;
                sb.AppendLine(string.Format("  ├ {0}  pos={1} rot={2} scale={3}",
                    t.name, t.localPosition.ToString("F4"),
                    t.localEulerAngles.ToString("F2"), t.localScale.ToString("F3")));
            }

            int totalV = 0, totalT = 0;
            var bb = new Bounds();
            bool hasBB = false;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null) continue;
                totalV += m.vertexCount;
                totalT += m.triangles.Length / 3;
                var wb = mf.transform.TransformBounds(m.bounds);
                if (!hasBB) { bb = wb; hasBB = true; } else bb.Encapsulate(wb);
                sb.AppendLine(string.Format(
                    "  网格 {0}: 顶点={1:N0} 三角={2:N0} 子网格={3} uv={4} 法线={5} 切线={6}",
                    m.name, m.vertexCount, m.triangles.Length / 3, m.subMeshCount,
                    m.uv != null && m.uv.Length > 0 ? "有" : "无",
                    m.normals != null && m.normals.Length > 0 ? "有" : "无",
                    m.tangents != null && m.tangents.Length > 0 ? "有" : "无"));
            }
            sb.AppendLine(string.Format("  合计 顶点={0:N0} 三角={1:N0}", totalV, totalT));
            if (hasBB)
            {
                sb.AppendLine(string.Format("  世界包围盒 center={0} size={1}  高={2:F4} 肩宽={3:F4} 厚={4:F4}",
                    bb.center.ToString("F4"), bb.size.ToString("F4"), bb.size.y, bb.size.x, bb.size.z));
                sb.AppendLine(string.Format("  贴地检查 yMin={0:F4}  水平居中 xCenter={1:F4} zCenter={2:F4}",
                    bb.min.y, bb.center.x, bb.center.z));
            }

            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                var mat = r.sharedMaterial;
                sb.AppendLine(string.Format("  渲染 {0} 材质={1} mainTex={2} bump={3}",
                    r.GetType().Name,
                    mat != null ? mat.name : "null",
                    mat != null && mat.HasProperty("_MainTex") && mat.GetTexture("_MainTex") != null
                        ? mat.GetTexture("_MainTex").name : "null",
                    mat != null && mat.HasProperty("_BumpMap") && mat.GetTexture("_BumpMap") != null
                        ? mat.GetTexture("_BumpMap").name : "null"));
            }

            Debug.Log(sb.ToString());
        }

        // ─────────────────────────── 正交五视图 ───────────────────────────

        [MenuItem("AIfootball/混元FBX/2. 渲染正交视图")]
        public static void RenderAll()
        {
            Directory.CreateDirectory(OutDir);
            foreach (var n in Models) RenderOne(Dir + "/" + n + ".fbx", n);
        }

        private static void RenderOne(string path, string tag)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null) { Debug.LogError("[Fbx] 找不到 " + path); return; }

            var inst = (GameObject)Object.Instantiate(go);
            inst.transform.position = Vector3.zero;
            inst.transform.rotation = Quaternion.identity;

            // 统一量取世界包围盒
            var bb = new Bounds();
            bool has = false;
            foreach (var r in inst.GetComponentsInChildren<Renderer>(true))
            {
                if (!has) { bb = r.bounds; has = true; } else bb.Encapsulate(r.bounds);
            }
            if (!has) { Debug.LogError("[Fbx] 无渲染器 " + tag); return; }

            float hgt = bb.size.y;
            Debug.Log(string.Format("[Fbx] {0} bounds center={1} size={2} 高={3:F4}",
                tag, bb.center.ToString("F4"), bb.size.ToString("F4"), hgt));

            // 材质兜底：没有材质的物体给个白模
            EnsureMaterial(inst);

            var camGo = new GameObject("OCam");
            var cam = camGo.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = hgt * 0.56f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.22f, 0.24f, 0.27f);
            cam.nearClipPlane = 0.01f;
            cam.farClipPlane = 100f;

            var keyGo = new GameObject("KLight");
            var key = keyGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.intensity = 1.05f;
            keyGo.transform.rotation = Quaternion.Euler(35f, -40f, 0f);

            var fillGo = new GameObject("FLight");
            var fill = fillGo.AddComponent<Light>();
            fill.type = LightType.Directional;
            fill.intensity = 0.55f;
            fillGo.transform.rotation = Quaternion.Euler(20f, 145f, 0f);

            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.42f, 0.44f, 0.48f);

            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
            cam.targetTexture = rt;

            var c = new Vector3(bb.center.x, bb.center.y, bb.center.z);
            float d = Mathf.Max(bb.size.x, bb.size.z) * 3f + 2f;

            var views = new (string name, Vector3 pos, Vector3 up, float dist)[]
            {
                ("front", new Vector3(c.x, c.y, c.z - d), Vector3.up, d),
                ("back",  new Vector3(c.x, c.y, c.z + d), Vector3.up, d),
                ("left",  new Vector3(c.x - d, c.y, c.z), Vector3.up, d),
                ("right", new Vector3(c.x + d, c.y, c.z), Vector3.up, d),
                ("top",   new Vector3(c.x, c.y + d, c.z + 0.0001f), Vector3.forward, d),
            };

            foreach (var v in views)
            {
                camGo.transform.position = v.pos;
                camGo.transform.LookAt(c, v.up);
                cam.orthographicSize = v.name == "top" ? Mathf.Max(bb.size.x, bb.size.z) * 0.56f : hgt * 0.56f;
                cam.Render();
                var tex = ReadTex(rt);
                File.WriteAllBytes(Path.Combine(OutDir, tag + "_" + v.name + ".png"), tex.EncodeToPNG());
                Object.DestroyImmediate(tex);
            }

            // 输出投影参数，供 Python 叠加刻度线
            string meta = string.Format(
                "{{\"tag\":\"{0}\",\"center\":[{1},{2},{3}],\"size\":[{4},{5},{6}],\"w\":{7},\"h\":{8}}}",
                tag, c.x, c.y, c.z, bb.size.x, bb.size.y, bb.size.z, W, H);
            File.WriteAllText(Path.Combine(OutDir, tag + "_meta.json"), meta);
            Debug.Log("[Fbx] 已渲染 " + tag + " 五视图 -> " + Path.GetFullPath(OutDir));

            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(keyGo);
            Object.DestroyImmediate(fillGo);
            Object.DestroyImmediate(inst);
        }

        private static void EnsureMaterial(GameObject root)
        {
            var white = new Material(Shader.Find("Standard"));
            white.color = new Color(0.78f, 0.78f, 0.80f);
            white.SetFloat("_Glossiness", 0.25f);
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r.sharedMaterial == null) r.sharedMaterial = white;
            }
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

    internal static class BoundsX
    {
        public static Bounds TransformBounds(this Transform t, Bounds local)
        {
            var c = t.TransformPoint(local.center);
            var e = local.extents;
            var ax = t.TransformVector(new Vector3(e.x, 0, 0));
            var ay = t.TransformVector(new Vector3(0, e.y, 0));
            var az = t.TransformVector(new Vector3(0, 0, e.z));
            var ext = new Vector3(
                Mathf.Abs(ax.x) + Mathf.Abs(ay.x) + Mathf.Abs(az.x),
                Mathf.Abs(ax.y) + Mathf.Abs(ay.y) + Mathf.Abs(az.y),
                Mathf.Abs(ax.z) + Mathf.Abs(ay.z) + Mathf.Abs(az.z));
            return new Bounds(c, ext * 2f);
        }
    }
}
