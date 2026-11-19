using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 混元 FBX 精细绑骨（第二代）。
    ///
    /// 与第一版的区别：
    ///   1. 关节全部来自点云实测 + 正交视图目视校准，不用解剖学硬比例
    ///      （这两具模型臂展/身高只有 0.88，套标准比例会系统偏移）；
    ///   2. 先把 23 个 MeshPart 合并成一张网格——混元 FBX 按 65534 三角切块，
    ///      分开绑会出现 22 条接缝，权重不连续会撕裂；
    ///   3. 权重在距离衰减之外加网格邻域 Laplacian 平滑，关节过渡带更柔和；
    ///   4. 平滑后按顶点位置焊接分组求平均，保证切块接缝两侧权重一致。
    /// </summary>
    public static class HunyuanRigger2
    {
        private const string Dir = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/FbxIn";
        private const string OutRoot = "Assets/SuperGoalie/Meshes/Characters/Hunyuan";

        private const int SmoothRounds = 3;
        private const float SmoothLambda = 0.38f;

        // ─────────────────────── 关节表（单位：身高分数）───────────────────────
        // 依据：ModelA/ModelB 正交五视图 + 点云切片统计，目视核对后微调。
        // 约定：角色面朝 +Z，右脚/右手在 +X。

        private static readonly Dictionary<string, Vector3> JA = new Dictionary<string, Vector3>
        {
            { "Hips",      new Vector3( 0.000f, 0.480f,  0.000f) },
            { "Spine",     new Vector3( 0.000f, 0.575f, -0.004f) },
            { "Chest",     new Vector3( 0.000f, 0.685f, -0.008f) },
            { "Neck",      new Vector3( 0.000f, 0.855f, -0.004f) },
            { "Head",      new Vector3( 0.000f, 0.905f, -0.002f) },
            { "HeadTop",   new Vector3( 0.000f, 1.000f,  0.000f) },

            { "ShoulderR", new Vector3( 0.050f, 0.798f,  0.000f) },
            { "UpperArmR", new Vector3( 0.112f, 0.792f,  0.000f) },
            { "LowerArmR", new Vector3( 0.272f, 0.789f,  0.002f) },
            { "HandR",     new Vector3( 0.352f, 0.792f,  0.002f) },
            { "HandEndR",  new Vector3( 0.432f, 0.794f,  0.000f) },

            { "UpperLegR", new Vector3( 0.062f, 0.452f,  0.000f) },
            { "LowerLegR", new Vector3( 0.063f, 0.278f,  0.006f) },
            { "FootR",     new Vector3( 0.062f, 0.066f, -0.006f) },
            { "ToesR",     new Vector3( 0.062f, 0.020f,  0.072f) },
        };

        private static readonly Dictionary<string, Vector3> JB = new Dictionary<string, Vector3>
        {
            { "Hips",      new Vector3( 0.000f, 0.470f,  0.000f) },
            { "Spine",     new Vector3( 0.000f, 0.560f, -0.004f) },
            { "Chest",     new Vector3( 0.000f, 0.670f, -0.008f) },
            { "Neck",      new Vector3( 0.000f, 0.848f, -0.004f) },
            { "Head",      new Vector3( 0.000f, 0.898f, -0.002f) },
            { "HeadTop",   new Vector3( 0.000f, 1.000f,  0.000f) },

            { "ShoulderR", new Vector3( 0.050f, 0.798f,  0.000f) },
            { "UpperArmR", new Vector3( 0.104f, 0.792f,  0.000f) },
            { "LowerArmR", new Vector3( 0.252f, 0.786f,  0.002f) },
            { "HandR",     new Vector3( 0.408f, 0.786f,  0.002f) },
            { "HandEndR",  new Vector3( 0.470f, 0.786f,  0.000f) },

            { "UpperLegR", new Vector3( 0.058f, 0.440f,  0.000f) },
            { "LowerLegR", new Vector3( 0.059f, 0.246f,  0.006f) },
            { "FootR",     new Vector3( 0.058f, 0.062f, -0.006f) },
            { "ToesR",     new Vector3( 0.058f, 0.018f,  0.066f) },
        };

        [MenuItem("AIfootball/混元FBX/4. 精细绑骨")]
        public static void BuildAll()
        {
            BuildOne("ModelA", "GoalkeeperRig2", 1.90f, JA, mirror: false);
            BuildOne("ModelB", "ShooterRig2", 1.80f, JB, mirror: false);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // ─────────────────────── 合并网格 ───────────────────────

        private class MergedMesh
        {
            public Mesh mesh;
            public int vertCount;
            public float height;      // 世界（米）身高
        }

        private static MergedMesh Merge(string fbxPath)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (go == null) { Debug.LogError("[Rig2] 找不到 " + fbxPath); return null; }

            var parts = new List<MeshFilter>();
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
                if (mf.sharedMesh != null) parts.Add(mf);
            if (parts.Count == 0) { Debug.LogError("[Rig2] 无网格 " + fbxPath); return null; }

            int nv = 0, nt = 0;
            foreach (var mf in parts) { nv += mf.sharedMesh.vertexCount; nt += mf.sharedMesh.triangles.Length; }

            var verts = new Vector3[nv];
            var norms = new Vector3[nv];
            var uvs = new Vector2[nv];
            var tris = new int[nt];

            int vo = 0, to = 0;
            foreach (var mf in parts)
            {
                var sm = mf.sharedMesh;
                var M = mf.transform.localToWorldMatrix;
                var mv = sm.vertices;
                var mn = sm.normals;
                var mu = sm.uv;
                var mt = sm.triangles;

                for (int i = 0; i < mv.Length; i++)
                {
                    verts[vo + i] = M.MultiplyPoint3x4(mv[i]);
                    norms[vo + i] = mn != null && mn.Length > i
                        ? M.MultiplyVector(mn[i]).normalized : Vector3.up;
                    uvs[vo + i] = mu != null && mu.Length > i ? mu[i] : Vector2.zero;
                }
                for (int i = 0; i < mt.Length; i++) tris[to + i] = mt[i] + vo;
                vo += mv.Length;
                to += mt.Length;
            }

            var mesh = new Mesh { name = Path.GetFileNameWithoutExtension(fbxPath) + "_Merged" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = norms;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            mesh.RecalculateTangents();

            float ymin = float.MaxValue, ymax = float.MinValue;
            for (int i = 0; i < nv; i++)
            {
                if (verts[i].y < ymin) ymin = verts[i].y;
                if (verts[i].y > ymax) ymax = verts[i].y;
            }

            Debug.Log(string.Format("[Rig2] 合并 {0} 块 → 顶点={1:N0} 索引={2:N0} 身高={3:F4}m yMin={4:F4}",
                parts.Count, nv, nt, ymax - ymin, ymin));

            return new MergedMesh { mesh = mesh, vertCount = nv, height = ymax - ymin, };
        }

        // ─────────────────────── 主流程 ───────────────────────

        private static void BuildOne(string model, string rigName, float targetHeight,
                                     Dictionary<string, Vector3> raw, bool mirror)
        {
            var merged = Merge(Dir + "/" + model + ".fbx");
            if (merged == null) return;

            float H = merged.height;
            var mesh = merged.mesh;
            var verts = mesh.vertices;

            // 归一化关节 → 模型空间（米）
            var J = new Dictionary<string, Vector3>();
            foreach (var kv in raw) J[kv.Key] = new Vector3(kv.Value.x * H, kv.Value.y * H, kv.Value.z * H);
            // 补左右镜像（左 = -X）
            foreach (var key in new List<string>(J.Keys))
            {
                if (!key.EndsWith("R")) continue;
                J[key.Substring(0, key.Length - 1) + "L"] =
                    new Vector3(-J[key].x, J[key].y, J[key].z);
            }

            var log = new StringBuilder();
            log.AppendLine("[Rig2] ==== " + rigName + " ====  身高=" + H.ToString("F4"));
            foreach (var k in new[] { "Hips", "Chest", "Neck", "ShoulderR", "UpperArmR", "LowerArmR",
                                      "HandR", "UpperLegR", "LowerLegR", "FootR", "ToesR" })
                log.AppendLine(string.Format("   {0,-10} {1}", k, J[k].ToString("F4")));
            Debug.Log(log.ToString());

            // ── 建骨骼 ──
            var root = new GameObject(rigName);
            var boneOrder = new List<string>();
            var boneT = new Dictionary<string, Transform>();
            var boneParent = new Dictionary<string, string>();

            void Make(string name, string parent)
            {
                var t = new GameObject(name).transform;
                var p = parent == null ? root.transform : boneT[parent];
                t.SetParent(p, false);
                t.position = J[name];                       // root 在原点，世界坐标可直接赋
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
                boneT[name] = t;
                boneParent[name] = parent;
                boneOrder.Add(name);
            }

            Make("Hips", null);
            Make("Spine", "Hips"); Make("Chest", "Spine"); Make("Neck", "Chest"); Make("Head", "Neck");
            foreach (var s in new[] { "R", "L" })
            {
                Make("Shoulder" + s, "Chest");
                Make("UpperArm" + s, "Shoulder" + s);
                Make("LowerArm" + s, "UpperArm" + s);
                Make("Hand" + s, "LowerArm" + s);
            }
            foreach (var s in new[] { "R", "L" })
            {
                Make("UpperLeg" + s, "Hips");
                Make("LowerLeg" + s, "UpperLeg" + s);
                Make("Foot" + s, "LowerLeg" + s);
                Make("Toes" + s, "Foot" + s);
            }

            // ── 骨段（用于蒙皮距离）──
            // 不直接用"关节到关节"：那样 Neck 段会横跨整个上胸，头顶会被 Neck 抢走，
            // 抬手时袖口会被 Chest 吸住。这里按解剖覆盖范围显式给每根骨划段。
            int nB = boneOrder.Count;
            var segA = new Vector3[nB];
            var segB = new Vector3[nB];
            var segSide = new int[nB];      // -1 左 / +1 右 / 0 中线
            var segRad = new float[nB];     // 影响半径（米）

            float hy = J["Hips"].y, sy = J["Spine"].y, ny = J["Neck"].y, ty = J["HeadTop"].y;
            float armY = J["UpperArmR"].y;
            float uaX = J["UpperArmR"].x, elX = J["LowerArmR"].x;
            float wrX = J["HandR"].x, heX = J["HandEndR"].x;
            float lgX = J["UpperLegR"].x, knY = J["LowerLegR"].y;
            float anY = J["FootR"].y, toY = J["ToesR"].y, toZ = J["ToesR"].z;
            float cZ = J["Hips"].z;

            for (int i = 0; i < nB; i++)
            {
                string n = boneOrder[i];
                bool isL = n.EndsWith("L");
                float sx = isL ? -1f : 1f;
                float ax = 0f, ay = 0f, az = cZ, bx = 0f, by = 0f, bz = cZ;

                switch (n)
                {
                    case "Hips":
                        ay = hy - 0.075f * H; by = hy + 0.020f * H; break;
                    case "Spine":
                        ay = hy + 0.020f * H; by = sy + 0.015f * H; break;
                    case "Chest":
                        ay = sy + 0.015f * H; by = ny - 0.070f * H; break;
                    case "Neck":
                        ay = ny - 0.070f * H; by = ny + 0.025f * H; break;
                    case "Head":
                        ay = ny + 0.025f * H; by = ty; break;
                    case "ShoulderR": case "ShoulderL":
                        ax = sx * 0.045f * H; bx = sx * 0.088f * H;
                        ay = by = armY; break;
                    case "UpperArmR": case "UpperArmL":
                        ax = sx * 0.088f * H; bx = sx * elX;
                        ay = by = armY; break;
                    case "LowerArmR": case "LowerArmL":
                        ax = sx * elX; bx = sx * wrX;
                        ay = by = armY; break;
                    case "HandR": case "HandL":
                        ax = sx * wrX; bx = sx * heX;
                        ay = by = armY; break;
                    case "UpperLegR": case "UpperLegL":
                        ax = bx = sx * lgX; ay = hy - 0.050f * H; by = knY; break;
                    case "LowerLegR": case "LowerLegL":
                        ax = bx = sx * lgX; ay = knY; by = anY; break;
                    case "FootR": case "FootL":
                        ax = bx = sx * lgX; ay = anY; by = toY; break;
                    case "ToesR": case "ToesL":
                        ax = bx = sx * lgX; ay = by = toY; az = cZ; bz = toZ; break;
                }

                segA[i] = new Vector3(ax, ay, az);
                segB[i] = new Vector3(bx, by, bz);

                if (isL) segSide[i] = -1;
                else if (n.EndsWith("R")) segSide[i] = 1;
                else segSide[i] = 0;
                segRad[i] = BoneRadius(n) * H;
            }

            // ── 骨邻接图：限制"远骨"抢权重 ──
            // 网格是按 65534 三角切块的，每个 MeshPart 内部连通，但接缝处顶点重复；
            // 直接靠距离会让袖口顶点被 Chest 吸走（大骨半径大），抬手时袖子留在原地。
            var adjGraph = BuildAdjacency(boneOrder, boneParent);

            // ── 初始权重：距离四次方反比 ──
            int nv = verts.Length;
            var w = new float[nv * 4];
            var bi = new int[nv * 4];
            var dist = new float[nB];
            var idx = new int[nB];
            float eps = 1e-5f * H * H;
            var reach = new bool[nB];

            for (int v = 0; v < nv; v++)
            {
                Vector3 p = verts[v];

                // 先找"主骨"（按归一化距离最近），主骨之外只允许图距离 ≤2 的骨参与
                int nearest = 0;
                float bestD = float.MaxValue;
                for (int s = 0; s < nB; s++)
                {
                    float d2 = DistSqToSegment(p, segA[s], segB[s]);
                    float rel = d2 / (segRad[s] * segRad[s]);
                    if (rel < bestD) { bestD = rel; nearest = s; }
                }
                for (int s = 0; s < nB; s++) reach[s] = adjGraph[nearest, s] <= 2;

                for (int s = 0; s < nB; s++)
                {
                    if (!reach[s]) { dist[s] = 0f; idx[s] = s; continue; }
                    float d2 = DistSqToSegment(p, segA[s], segB[s]);
                    float rel = d2 / (segRad[s] * segRad[s]);
                    // d^-2.5：比 d^-4 的过渡带宽一倍，关节处不至于塌成薄片
                    float val = 1f / (Mathf.Pow(rel, 1.25f) + eps);
                    // 图距离每远一级再衰减，让主骨及其直接父/子占绝对主导
                    int g = adjGraph[nearest, s];
                    if (g >= 2) val *= 0.32f;
                    if (segSide[s] == -1 && p.x > 0.012f * H) val *= 0.03f;
                    else if (segSide[s] == 1 && p.x < -0.012f * H) val *= 0.03f;
                    dist[s] = val;
                    idx[s] = s;
                }
                Top4(dist, idx, out int j0, out int j1, out int j2, out int j3,
                     out float u0, out float u1, out float u2, out float u3);
                w[v * 4] = u0; w[v * 4 + 1] = u1; w[v * 4 + 2] = u2; w[v * 4 + 3] = u3;
                bi[v * 4] = j0; bi[v * 4 + 1] = j1; bi[v * 4 + 2] = j2; bi[v * 4 + 3] = j3;
            }

            // ── 邻接（CSR）──
            var tris = mesh.triangles;
            var deg = new int[nv];
            for (int t = 0; t < tris.Length; t += 3)
            {
                deg[tris[t]] += 2; deg[tris[t + 1]] += 2; deg[tris[t + 2]] += 2;
            }
            var off = new int[nv + 1];
            for (int i = 0; i < nv; i++) off[i + 1] = off[i] + deg[i];
            var adj = new int[off[nv]];
            var cur = new int[nv];
            Array.Copy(off, cur, nv);
            for (int t = 0; t < tris.Length; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                adj[cur[a]++] = b; adj[cur[a]++] = c;
                adj[cur[b]++] = a; adj[cur[b]++] = c;
                adj[cur[c]++] = a; adj[cur[c]++] = b;
            }
            Debug.Log("[Rig2] 邻接构建完成 边数=" + off[nv].ToString("N0"));

            // ── Laplacian 平滑权重 ──
            var w2 = new float[nv * 4];
            var b2 = new int[nv * 4];
            var acc = new float[nB];
            for (int round = 0; round < SmoothRounds; round++)
            {
                for (int v = 0; v < nv; v++)
                {
                    Array.Clear(acc, 0, nB);
                    for (int k = 0; k < 4; k++)
                    {
                        int b = bi[v * 4 + k];
                        float ww = w[v * 4 + k];
                        if (b < 0 || b >= nB) continue;
                        acc[b] += ww * (1f - SmoothLambda);
                    }
                    int s0 = off[v], s1 = off[v + 1];
                    int cnt = s1 - s0;
                    if (cnt > 0)
                    {
                        float share = SmoothLambda / cnt;
                        for (int j = s0; j < s1; j++)
                        {
                            int u = adj[j];
                            for (int k = 0; k < 4; k++)
                            {
                                int b = bi[u * 4 + k];
                                float ww = w[u * 4 + k];
                                if (b < 0 || b >= nB) continue;
                                acc[b] += ww * share;
                            }
                        }
                    }
                    for (int s = 0; s < nB; s++) { dist[s] = acc[s]; idx[s] = s; }
                    Top4(dist, idx, out int k0, out int k1, out int k2, out int k3,
                         out float f0, out float f1, out float f2, out float f3);
                    w2[v * 4] = f0; w2[v * 4 + 1] = f1; w2[v * 4 + 2] = f2; w2[v * 4 + 3] = f3;
                    b2[v * 4] = k0; b2[v * 4 + 1] = k1; b2[v * 4 + 2] = k2; b2[v * 4 + 3] = k3;
                }
                Array.Copy(w2, w, w2.Length);
                Array.Copy(b2, bi, b2.Length);
                Debug.Log("[Rig2] 平滑第 " + (round + 1) + " 轮完成");
            }

            // 平滑会把权重扩散到远处骨，按骨邻接再裁剪一次，恢复"关节局部性"
            for (int v = 0; v < nv; v++)
            {
                int main = bi[v * 4];
                float sum = 0f;
                for (int k = 0; k < 4; k++)
                {
                    int b = bi[v * 4 + k];
                    if (adjGraph[main, b] > 3) w[v * 4 + k] = 0f;
                    sum += w[v * 4 + k];
                }
                if (sum > 1e-6f) for (int k = 0; k < 4; k++) w[v * 4 + k] /= sum;
            }

            // ── 接缝焊接：同位置顶点权重取平均 ──
            WeldSeams(verts, w, bi, nv);

            // ── 写回网格 ──
            var bw = new BoneWeight[nv];
            for (int v = 0; v < nv; v++)
            {
                bw[v] = new BoneWeight
                {
                    boneIndex0 = bi[v * 4], weight0 = w[v * 4],
                    boneIndex1 = bi[v * 4 + 1], weight1 = w[v * 4 + 1],
                    boneIndex2 = bi[v * 4 + 2], weight2 = w[v * 4 + 2],
                    boneIndex3 = bi[v * 4 + 3], weight3 = w[v * 4 + 3],
                };
            }
            mesh.boneWeights = bw;

            var boneList = new List<Transform>();
            foreach (var n in boneOrder) boneList.Add(boneT[n]);
            var bindposes = new Matrix4x4[boneList.Count];
            for (int i = 0; i < boneList.Count; i++)
                bindposes[i] = boneList[i].worldToLocalMatrix * root.transform.localToWorldMatrix;
            mesh.bindposes = bindposes;
            mesh.UploadMeshData(false);

            // ── 资产 ──
            string meshPath = OutRoot + "/" + rigName + "_Mesh.asset";
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();

            var rigRoot = new GameObject(rigName + "_Mesh");
            rigRoot.transform.SetParent(root.transform, false);
            var smr = rigRoot.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = boneList.ToArray();
            smr.rootBone = boneT["Hips"];
            smr.updateWhenOffscreen = true;
            smr.sharedMaterial = BuildMaterial(rigName, Dir + "/" + model + ".fbx");

            // ── Avatar ──
            var avatar = BuildAvatar(root, boneT);
            if (avatar != null && avatar.isValid)
            {
                avatar.name = rigName + "Avatar";
                string avPath = OutRoot + "/" + rigName + "Avatar.asset";
                AssetDatabase.DeleteAsset(avPath);
                AssetDatabase.CreateAsset(avatar, avPath);
                var anim = root.AddComponent<Animator>();
                anim.avatar = avatar;
                anim.applyRootMotion = false;
            }
            else Debug.LogError("[Rig2] Avatar 无效：" + rigName);

            // ── 缩放落地 ──
            float scale = targetHeight / H;
            root.transform.localScale = Vector3.one * scale;
            root.transform.position = Vector3.zero;

            string prefabPath = OutRoot + "/" + rigName + ".prefab";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log(string.Format("[Rig2] 已生成 {0}  缩放={1:F4}  骨骼={2}", prefabPath, scale, nB));
        }

        // ─────────────────────── 接缝焊接 ───────────────────────

        private static void WeldSeams(Vector3[] verts, float[] w, int[] bi, int nv)
        {
            const float q = 1e-4f;                       // 0.1mm 量化
            var map = new Dictionary<long, int>(nv);
            var group = new int[nv];
            int groups = 0;
            for (int v = 0; v < nv; v++)
            {
                long kx = (long)Math.Round(verts[v].x / q);
                long ky = (long)Math.Round(verts[v].y / q);
                long kz = (long)Math.Round(verts[v].z / q);
                long key = (kx & 0x1FFFFF) << 42 | (ky & 0x1FFFFF) << 21 | (kz & 0x1FFFFF);
                if (map.TryGetValue(key, out int g)) group[v] = g;
                else { group[v] = groups; map[key] = groups; groups++; }
            }
            if (groups == nv) { Debug.Log("[Rig2] 无需焊接（顶点无重复）"); return; }

            // 组内累加（21 骨足够，用数组）
            int nB = 21;
            var sum = new float[groups * nB];
            var cnt = new int[groups];
            for (int v = 0; v < nv; v++)
            {
                int g = group[v];
                cnt[g]++;
                for (int k = 0; k < 4; k++)
                {
                    int b = bi[v * 4 + k];
                    if (b >= 0 && b < nB) sum[g * nB + b] += w[v * 4 + k];
                }
            }
            // 写回：组内所有顶点用同一套权重
            var dw = new float[nB];
            var dx = new int[nB];
            int welded = 0;
            for (int v = 0; v < nv; v++)
            {
                int g = group[v];
                if (cnt[g] < 2) continue;
                welded++;
                for (int b = 0; b < nB; b++) { dw[b] = sum[g * nB + b] / cnt[g]; dx[b] = b; }
                Top4(dw, dx, out int b0, out int b1, out int b2, out int b3,
                     out float w0, out float w1, out float w2, out float w3);
                w[v * 4] = w0; w[v * 4 + 1] = w1; w[v * 4 + 2] = w2; w[v * 4 + 3] = w3;
                bi[v * 4] = b0; bi[v * 4 + 1] = b1; bi[v * 4 + 2] = b2; bi[v * 4 + 3] = b3;
            }
            Debug.Log("[Rig2] 接缝焊接：组数=" + groups.ToString("N0") +
                      " 落位顶点=" + welded.ToString("N0"));
        }

        // ─────────────────────── 工具 ───────────────────────

        private static float BoneRadius(string name)
        {
            if (name == "Hips") return 0.075f;
            if (name == "Spine") return 0.070f;
            if (name == "Chest") return 0.075f;
            if (name == "Neck") return 0.035f;
            if (name == "Head") return 0.085f;
            if (name.StartsWith("Shoulder")) return 0.055f;
            if (name.StartsWith("UpperArm")) return 0.050f;
            if (name.StartsWith("LowerArm")) return 0.042f;
            if (name.StartsWith("Hand")) return 0.040f;
            if (name.StartsWith("UpperLeg")) return 0.065f;
            if (name.StartsWith("LowerLeg")) return 0.052f;
            if (name.StartsWith("Foot")) return 0.035f;
            if (name.StartsWith("Toes")) return 0.030f;
            return 0.045f;
        }

        /// <summary>骨骼图的跳数矩阵（Floyd，21 节点）。用来限制"远骨"参与蒙皮。</summary>
        private static int[,] BuildAdjacency(List<string> order, Dictionary<string, string> parent)
        {
            int n = order.Count;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) index[order[i]] = i;

            var g = new int[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    g[i, j] = i == j ? 0 : 999;

            for (int i = 0; i < n; i++)
            {
                string p = parent[order[i]];
                if (p != null && index.TryGetValue(p, out int j)) { g[i, j] = 1; g[j, i] = 1; }
            }
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                    for (int j = 0; j < n; j++)
                        if (g[i, k] + g[k, j] < g[i, j]) g[i, j] = g[i, k] + g[k, j];
            return g;
        }

        private static float DistSqToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            float abx = b.x - a.x, aby = b.y - a.y, abz = b.z - a.z;
            float len2 = abx * abx + aby * aby + abz * abz;
            float t = 0f;
            if (len2 > 1e-12f)
            {
                t = ((p.x - a.x) * abx + (p.y - a.y) * aby + (p.z - a.z) * abz) / len2;
                if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            }
            float dx = p.x - (a.x + abx * t);
            float dy = p.y - (a.y + aby * t);
            float dz = p.z - (a.z + abz * t);
            return dx * dx + dy * dy + dz * dz;
        }

        private static void Top4(float[] w, int[] idx, out int i0, out int i1, out int i2, out int i3,
                                 out float w0, out float w1, out float w2, out float w3)
        {
            i0 = i1 = i2 = i3 = 0;
            w0 = w1 = w2 = w3 = 0f;
            for (int s = 0; s < w.Length; s++)
            {
                float x = w[s];
                if (x <= 0f) continue;
                if (x > w0) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = i0; w1 = w0; i0 = s; w0 = x; }
                else if (x > w1) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = s; w1 = x; }
                else if (x > w2) { i3 = i2; w3 = w2; i2 = s; w2 = x; }
                else if (x > w3) { i3 = s; w3 = x; }
            }
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 0f) { i0 = 0; w0 = 1f; return; }
            w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum;
        }

        private static Material BuildMaterial(string rigName, string fbxPath)
        {
            var mat = new Material(Shader.Find("Standard")) { name = rigName + "_Mat" };
            Texture2D baseTex = null, normTex = null;

            // 优先用已抽取到外部的贴图（Textures/<Model>/），避免同名互相覆盖
            string model = Path.GetFileNameWithoutExtension(fbxPath);
            string texDir = Path.GetDirectoryName(fbxPath).Replace('\\', '/') + "/Textures/" + model;
            if (Directory.Exists(texDir))
            {
                foreach (var f in Directory.GetFiles(texDir, "*.png"))
                {
                    string fn = Path.GetFileName(f).ToLowerInvariant();
                    string rel = texDir + "/" + Path.GetFileName(f);
                    var t = AssetDatabase.LoadAssetAtPath<Texture2D>(rel);
                    if (t == null) continue;
                    if (fn.Contains("normal")) { if (normTex == null) normTex = t; }
                    else if (fn.Contains("metallic") || fn.Contains("rough") || fn.Contains("smooth")) { }
                    else if (baseTex == null) baseTex = t;
                }
            }

            // 兜底：从 FBX 内嵌 sub-asset 取
            if (baseTex == null)
            {
                foreach (var o in AssetDatabase.LoadAllAssetsAtPath(fbxPath))
                {
                    var t = o as Texture2D;
                    if (t == null) continue;
                    string n = t.name.ToLowerInvariant();
                    if (n.Contains("normal")) { if (normTex == null) normTex = t; }
                    else if (n.Contains("metallic") || n.Contains("rough") || n.Contains("smooth")) { }
                    else if (baseTex == null) baseTex = t;
                }
            }

            if (baseTex != null) mat.SetTexture("_MainTex", baseTex);
            if (normTex != null)
            {
                mat.SetTexture("_BumpMap", normTex);
                mat.EnableKeyword("_NORMALMAP");
            }
            mat.SetFloat("_Metallic", 0f);          // 混元金属图偏脏，置 0 免得发黑
            mat.SetFloat("_Glossiness", 0.32f);
            mat.enableInstancing = true;

            string matPath = OutRoot + "/" + rigName + "_Mat.mat";
            AssetDatabase.DeleteAsset(matPath);
            AssetDatabase.CreateAsset(mat, matPath);
            Debug.Log(string.Format("[Rig2] 材质 {0}: base={1} normal={2}",
                rigName, baseTex != null ? baseTex.name : "null",
                normTex != null ? normTex.name : "null"));
            return mat;
        }

        /// <summary>把混元 FBX 内嵌的贴图抽成外部文件，再重新绑到材质上。</summary>
        [MenuItem("AIfootball/混元FBX/7. 抽取内嵌贴图并重绑")]
        public static void ExtractTexturesAndRebind()
        {
            // 两个模型的贴图同名（texture_pbr_20250901.png），必须分目录，否则互相覆盖
            var pairs = new[]
            {
                ("ModelA", OutRoot + "/GoalkeeperRig2_Mat.mat"),
                ("ModelB", OutRoot + "/ShooterRig2_Mat.mat"),
            };

            foreach (var (n, matPath) in pairs)
            {
                string texDir = Dir + "/Textures/" + n;
                Directory.CreateDirectory(texDir);
                string p = Dir + "/" + n + ".fbx";

                var mi = AssetImporter.GetAtPath(p) as ModelImporter;
                if (mi == null) { Debug.LogError("[Rig2] 无导入器 " + p); continue; }
                bool ok = mi.ExtractTextures(texDir);
                Debug.Log("[Rig2] 抽取贴图 " + n + " -> " + ok + " 目录=" + texDir);
            }
            AssetDatabase.Refresh();

            foreach (var (n, matPath) in pairs)
            {
                string texDir = Dir + "/Textures/" + n;
                FixMatFromDir(matPath, texDir);
            }
            AssetDatabase.SaveAssets();
        }

        private static void FixMatFromDir(string matPath, string texDir)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null) { Debug.LogError("[Rig2] 找不到材质 " + matPath); return; }

            string basePath = null, normPath = null;
            if (Directory.Exists(texDir))
            {
                foreach (var f in Directory.GetFiles(texDir, "*.png"))
                {
                    string fn = Path.GetFileName(f).ToLowerInvariant();
                    string rel = texDir + "/" + Path.GetFileName(f);
                    if (fn.Contains("normal")) { if (normPath == null) normPath = rel; }
                    else if (fn.Contains("metallic") || fn.Contains("rough") || fn.Contains("smooth")) { }
                    else if (basePath == null) basePath = rel;
                }
            }

            var bt = basePath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(basePath) : null;
            var nt = normPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(normPath) : null;

            // 抽出来的 PNG 默认是 Default/sRGB 贴图，法线当颜色用会让光照全错（实测球衣发白起斑）
            if (basePath != null) ConfigureTex(basePath, TextureImporterType.Default, false, 4096);
            if (normPath != null) ConfigureTex(normPath, TextureImporterType.NormalMap, true, 4096);

            bt = basePath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(basePath) : bt;
            nt = normPath != null ? AssetDatabase.LoadAssetAtPath<Texture2D>(normPath) : nt;

            if (bt != null) mat.SetTexture("_MainTex", bt);
            if (nt != null) { mat.SetTexture("_BumpMap", nt); mat.EnableKeyword("_NORMALMAP"); }
            mat.SetFloat("_Metallic", 0f);
            mat.SetFloat("_Glossiness", 0.32f);
            EditorUtility.SetDirty(mat);
            Debug.Log("[Rig2] 重绑 " + matPath + " base=" + (bt != null ? bt.name : "null") +
                      " normal=" + (nt != null ? nt.name : "null"));
        }

        private static void ConfigureTex(string path, TextureImporterType type, bool linear, int maxSize)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null) return;
            bool dirty = false;
            if (ti.textureType != type) { ti.textureType = type; dirty = true; }
            if (ti.sRGBTexture != !linear) { ti.sRGBTexture = !linear; dirty = true; }
            if (ti.maxTextureSize < maxSize) { ti.maxTextureSize = maxSize; dirty = true; }
            if (ti.textureCompression != TextureImporterCompression.CompressedHQ)
            {
                ti.textureCompression = TextureImporterCompression.CompressedHQ; dirty = true;
            }
            if (dirty) ti.SaveAndReimport();
        }

        /// <summary>只修材质、不重建网格（网格资产 170MB，重建代价高）。</summary>
        [MenuItem("AIfootball/混元FBX/6. 修复材质贴图")]
        public static void FixMaterials()
        {
            FixMatFromDir(OutRoot + "/GoalkeeperRig2_Mat.mat", Dir + "/Textures/ModelA");
            FixMatFromDir(OutRoot + "/ShooterRig2_Mat.mat", Dir + "/Textures/ModelB");
            AssetDatabase.SaveAssets();
        }

        private static Avatar BuildAvatar(GameObject root, Dictionary<string, Transform> bones)
        {
            var human = new List<HumanBone>();
            void H(HumanBodyBones id, string name)
            {
                if (!bones.ContainsKey(name)) return;
                human.Add(new HumanBone
                {
                    humanName = HumanTrait.BoneName[(int)id],
                    boneName = name,
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            H(HumanBodyBones.Hips, "Hips");
            H(HumanBodyBones.Spine, "Spine");
            H(HumanBodyBones.Chest, "Chest");
            H(HumanBodyBones.Neck, "Neck");
            H(HumanBodyBones.Head, "Head");
            foreach (var s in new[] { "R", "L" })
            {
                H(s == "R" ? HumanBodyBones.RightShoulder : HumanBodyBones.LeftShoulder, "Shoulder" + s);
                H(s == "R" ? HumanBodyBones.RightUpperArm : HumanBodyBones.LeftUpperArm, "UpperArm" + s);
                H(s == "R" ? HumanBodyBones.RightLowerArm : HumanBodyBones.LeftLowerArm, "LowerArm" + s);
                H(s == "R" ? HumanBodyBones.RightHand : HumanBodyBones.LeftHand, "Hand" + s);
                H(s == "R" ? HumanBodyBones.RightUpperLeg : HumanBodyBones.LeftUpperLeg, "UpperLeg" + s);
                H(s == "R" ? HumanBodyBones.RightLowerLeg : HumanBodyBones.LeftLowerLeg, "LowerLeg" + s);
                H(s == "R" ? HumanBodyBones.RightFoot : HumanBodyBones.LeftFoot, "Foot" + s);
                H(s == "R" ? HumanBodyBones.RightToes : HumanBodyBones.LeftToes, "Toes" + s);
            }

            var skeleton = new List<SkeletonBone>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                skeleton.Add(new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });

            var desc = new HumanDescription
            {
                human = human.ToArray(),
                skeleton = skeleton.ToArray(),
                upperArmTwist = 0.5f,
                lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f,
                lowerLegTwist = 0.5f,
                armStretch = 0.05f,
                legStretch = 0.05f,
                feetSpacing = 0f,
                hasTranslationDoF = false,
            };

            var avatar = AvatarBuilder.BuildHumanAvatar(root, desc);
            Debug.Log(string.Format("[Rig2] Avatar human={0} skeleton={1} valid={2}",
                human.Count, skeleton.Count, avatar != null && avatar.isValid));
            return avatar;
        }
    }
}
