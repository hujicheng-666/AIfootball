using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 为混元导出的静态网格生成标准 Humanoid 骨架并自动蒙皮。
    ///
    /// 背景：混元导出的 OBJ/GLB 都是纯雕塑（skins=0, animations=0），没有骨骼。
    /// 项目的 9 个扑救动画与射手姿态重定向全部依赖 Unity Humanoid，
    /// 因此必须补一套标准骨架，否则模型只能站着不动。
    ///
    /// 做法：
    ///   1. 用水平切片的宽度突变定位髋、膝、踝、颈、肩的高度；
    ///   2. 按标准人体比例补全其余关节（模型本身是标准 T-pose，比例可靠）；
    ///   3. 建 19 根 Humanoid 骨骼，用"到骨段距离的四次方反比"算蒙皮，
    ///      并按左右侧对异侧骨骼做降权，避免胯部两条腿粘在一起；
    ///   4. 用 AvatarBuilder 生成 Avatar，校验 isValid。
    /// </summary>
    public static class HunyuanRigger
    {
        private const string ShooterObj = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/Shooter/Shooter.obj";
        private const string KeeperObj = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/Goalkeeper/Goalkeeper.obj";
        private const string OutRoot = "Assets/SuperGoalie/Meshes/Characters/Hunyuan";

        /// <summary>射手目标身高 1.80 m，门将 1.90 m（GoalkeeperData.Height 基准）。</summary>
        private const float ShooterHeight = 1.80f;
        private const float KeeperHeight = 1.90f;

        [MenuItem("AIfootball/混元/2. 生成骨架与蒙皮")]
        public static void BuildAll()
        {
            BuildOne(ShooterObj, "ShooterRig", ShooterHeight);
            BuildOne(KeeperObj, "GoalkeeperRig", KeeperHeight);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // ─────────────────────────── 关节分析 ───────────────────────────

        private class Joints
        {
            public float H;          // 网格身高（模型单位）
            public float CenterZ;    // 躯干前后中心
            public float DebugArmY, DebugCrotchY;
            public Vector3 Hips, Spine, Chest, Neck, Head, HeadTop;
            public Vector3 LShoulder, LUpperArm, LLowerArm, LHand;
            public Vector3 RShoulder, RUpperArm, RLowerArm, RHand;
            public Vector3 LUpperLeg, LLowerLeg, LFoot, LToes;
            public Vector3 RUpperLeg, RLowerLeg, RFoot, RToes;
        }

        private static Joints Analyse(Vector3[] verts)
        {
            var j = new Joints();
            float yMax = float.MinValue, yMin = float.MaxValue;
            float xMax = 0f;
            var zs = new List<float>(verts.Length / 8);
            for (int i = 0; i < verts.Length; i += 8)   // 降采样，够用
            {
                var v = verts[i];
                if (v.y > yMax) yMax = v.y;
                if (v.y < yMin) yMin = v.y;
                float ax = Mathf.Abs(v.x);
                if (ax > xMax) xMax = ax;
                zs.Add(v.z);
            }
            float H = yMax - yMin;
            j.H = H;
            float y0 = yMin;

            // 手臂高度：T-pose 手臂水平，取外段顶点的 y 中位数
            var armYs = new List<float>();
            for (int i = 0; i < verts.Length; i += 8)
            {
                if (Mathf.Abs(verts[i].x) > xMax * 0.34f) armYs.Add(verts[i].y);
            }
            float armY = armYs.Count > 32 ? Median(armYs) : y0 + 0.82f * H;

            // 逐层宽度：找颈（最窄）、髋（最宽）、膝（腿最细）、踝
            const int Bands = 80;
            var width = new float[Bands];
            var zmed = new float[Bands];
            var count = new int[Bands];
            for (int b = 0; b < Bands; b++) { width[b] = 0f; count[b] = 0; }
            for (int i = 0; i < verts.Length; i += 6)
            {
                int b = Mathf.Clamp((int)((verts[i].y - y0) / H * Bands), 0, Bands - 1);
                count[b]++;
                float ax = Mathf.Abs(verts[i].x);
                if (ax > width[b]) width[b] = ax;
                zmed[b] += verts[i].z;
            }
            for (int b = 0; b < Bands; b++)
                if (count[b] > 0) zmed[b] /= count[b];

            int BandAt(float f) => Mathf.Clamp((int)(f * Bands), 0, Bands - 1);

            // 髋 / 膝 / 踝高度：用解剖学比例，不用几何检测。
            // 实测这套模型的短裤下摆把裆部填平（y<0.36 中线最窄只有 0.05H，
            // 到 0.36~0.40 突然被填满），膝部 Z 最厚处也被短裤蓬起干扰。
            // 只有手臂高度可靠——手臂裸露，水平伸展，取外段 y 的均值为准。
            float hipsY = y0 + 0.530f * H;
            float kneeY = y0 + 0.285f * H;
            float neckY = armY + 0.030f * H;
            if (neckY > y0 + 0.915f * H) neckY = y0 + 0.905f * H;
            float ankleY = y0 + 0.045f * H;

            float cZ = zmed[BandAt(0.50f)];
            j.CenterZ = cZ;
            j.DebugArmY = armY;
            j.DebugCrotchY = 0f;

            // 躯干中线
            j.Hips = new Vector3(0f, hipsY, cZ);
            j.Spine = new Vector3(0f, Mathf.Lerp(hipsY, neckY, 0.28f), cZ - 0.002f * H);
            j.Chest = new Vector3(0f, Mathf.Lerp(hipsY, neckY, 0.62f), cZ - 0.004f * H);
            j.Neck = new Vector3(0f, neckY, cZ - 0.006f * H);
            j.Head = new Vector3(0f, neckY + 0.055f * H, cZ - 0.004f * H);
            j.HeadTop = new Vector3(0f, y0 + H, cZ);

            // 肩与手臂：肩关节在躯干边缘，腕在手指内侧
            float shoulderX = 0.098f * H;
            float wristX = xMax - 0.062f * H;
            float armZ = cZ - 0.004f * H;
            float armYJ = armY - 0.008f * H;

            j.LShoulder = new Vector3(-shoulderX * 0.45f, armYJ + 0.018f * H, armZ);
            j.LUpperArm = new Vector3(-shoulderX, armYJ, armZ);
            j.LLowerArm = new Vector3(Mathf.Lerp(-shoulderX, -wristX, 0.52f), armYJ - 0.004f * H, armZ + 0.004f * H);
            j.LHand = new Vector3(-wristX, armYJ - 0.004f * H, armZ + 0.002f * H);

            j.RShoulder = new Vector3(shoulderX * 0.45f, armYJ + 0.018f * H, armZ);
            j.RUpperArm = new Vector3(shoulderX, armYJ, armZ);
            j.RLowerArm = new Vector3(Mathf.Lerp(shoulderX, wristX, 0.52f), armYJ - 0.004f * H, armZ + 0.004f * H);
            j.RHand = new Vector3(wristX, armYJ - 0.004f * H, armZ + 0.002f * H);

            // 腿
            float legX = 0.062f * H;
            float footY = y0 + 0.028f * H;
            float toeZ = cZ + 0.075f * H;

            j.LUpperLeg = new Vector3(-legX, hipsY - 0.012f * H, cZ);
            j.LLowerLeg = new Vector3(-legX, kneeY, cZ + 0.004f * H);
            j.LFoot = new Vector3(-legX, ankleY, cZ - 0.004f * H);
            j.LToes = new Vector3(-legX, footY, toeZ);

            j.RUpperLeg = new Vector3(legX, hipsY - 0.012f * H, cZ);
            j.RLowerLeg = new Vector3(legX, kneeY, cZ + 0.004f * H);
            j.RFoot = new Vector3(legX, ankleY, cZ - 0.004f * H);
            j.RToes = new Vector3(legX, footY, toeZ);

            return j;
        }

        private static float Median(List<float> xs)
        {
            xs.Sort();
            return xs[xs.Count / 2];
        }

        // ─────────────────────────── 骨架与蒙皮 ───────────────────────────

        private static void BuildOne(string objPath, string rigName, float targetHeight)
        {
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(objPath);
            if (src == null)
            {
                Debug.LogError("[Hunyuan] 模型未导入：" + objPath);
                return;
            }
            var srcMf = src.GetComponentInChildren<MeshFilter>(true);
            if (srcMf == null || srcMf.sharedMesh == null)
            {
                Debug.LogError("[Hunyuan] 找不到网格：" + objPath);
                return;
            }

            // 复制一份网格：蒙皮要写 boneWeights / bindposes，直接改会污染导入资产
            var srcMesh = srcMf.sharedMesh;
            var mesh = UnityEngine.Object.Instantiate(srcMesh);
            mesh.name = rigName + "_Mesh";
            var verts = srcMesh.vertices;
            var j = Analyse(verts);
            float H = j.H;
            Debug.Log(string.Format("[Hunyuan] {0} 身高={1:F3} 顶点={2:N0}\n  髋={3:F3}({4:P0})  膝={5:F3}({6:P0})  颈={7:F3}({8:P0})  臂={9:F3}  裆={10:F3}",
                rigName, H, verts.Length,
                j.Hips.y, j.Hips.y / H, j.LLowerLeg.y, j.LLowerLeg.y / H, j.Neck.y, j.Neck.y / H,
                j.DebugArmY, j.DebugCrotchY));

            // ── 建骨骼层级 ──
            var root = new GameObject(rigName);
            var bones = new Dictionary<string, Transform>();

            Transform Make(string name, Vector3 pos, Transform parent)
            {
                var t = new GameObject(name).transform;
                t.SetParent(parent, false);
                // 父节点已经定位，用世界位置差算 localPosition（root 此时仍在原点）
                t.localPosition = pos - parent.position;
                t.localRotation = Quaternion.identity;
                t.localScale = Vector3.one;
                bones[name] = t;
                return t;
            }

            // 注意：localPosition 必须相对父节点，父节点位置也是模型空间坐标
            var hips = Make("Hips", j.Hips, root.transform);
            var spine = Make("Spine", j.Spine, hips);
            var chest = Make("Chest", j.Chest, spine);
            var neck = Make("Neck", j.Neck, chest);
            var head = Make("Head", j.Head, neck);

            var lSho = Make("LeftShoulder", j.LShoulder, chest);
            var lUpA = Make("LeftUpperArm", j.LUpperArm, lSho);
            var lLoA = Make("LeftLowerArm", j.LLowerArm, lUpA);
            var lHand = Make("LeftHand", j.LHand, lLoA);

            var rSho = Make("RightShoulder", j.RShoulder, chest);
            var rUpA = Make("RightUpperArm", j.RUpperArm, rSho);
            var rLoA = Make("RightLowerArm", j.RLowerArm, rUpA);
            var rHand = Make("RightHand", j.RHand, rLoA);

            var lUpL = Make("LeftUpperLeg", j.LUpperLeg, hips);
            var lLoL = Make("LeftLowerLeg", j.LLowerLeg, lUpL);
            var lFoot = Make("LeftFoot", j.LFoot, lLoL);
            var lToes = Make("LeftToes", j.LToes, lFoot);

            var rUpL = Make("RightUpperLeg", j.RUpperLeg, hips);
            var rLoL = Make("RightLowerLeg", j.RLowerLeg, rUpL);
            var rFoot = Make("RightFoot", j.RFoot, rLoL);
            var rToes = Make("RightToes", j.RToes, rFoot);

            // ── 蒙皮 ──
            var boneList = new List<Transform>();
            var segA = new List<Vector3>();
            var segB = new List<Vector3>();
            var segSide = new List<int>();   // -1 左 / +1 右 / 0 中线

            void Seg(Transform b, Transform parent, int side)
            {
                boneList.Add(b);
                segA.Add(parent.position == Vector3.zero && parent == root.transform ? Vector3.zero : parent.position);
                segB.Add(b.position);
                segSide.Add(side);
            }

            // 用局部坐标推世界（root 尚未移动，位置即模型坐标）
            Vector3 P(Transform t) => t.position;

            Seg(hips, root.transform, 0);
            Seg(spine, hips, 0);
            Seg(chest, spine, 0);
            Seg(neck, chest, 0);
            Seg(head, neck, 0);
            Seg(lSho, chest, -1); Seg(lUpA, lSho, -1); Seg(lLoA, lUpA, -1); Seg(lHand, lLoA, -1);
            Seg(rSho, chest, +1); Seg(rUpA, rSho, +1); Seg(rLoA, rUpA, +1); Seg(rHand, rLoA, +1);
            Seg(lUpL, hips, -1); Seg(lLoL, lUpL, -1); Seg(lFoot, lLoL, -1); Seg(lToes, lFoot, -1);
            Seg(rUpL, hips, +1); Seg(rLoL, rUpL, +1); Seg(rFoot, rLoL, +1); Seg(rToes, rFoot, +1);

            int nSeg = boneList.Count;
            var weights = new BoneWeight[verts.Length];
            var dist = new float[nSeg];
            var idxBuf = new int[nSeg];

            for (int i = 0; i < verts.Length; i++)
            {
                var v = verts[i];
                float side = v.x;
                for (int s = 0; s < nSeg; s++)
                {
                    float d2 = DistSqToSegment(v, segA[s], segB[s]);
                    float w = 1f / (d2 * d2 / (H * H * H * H) + 1e-7f);
                    // 异侧降权：胯部两条腿不至于互相吸附
                    if (segSide[s] == -1 && side > 0.012f * H) w *= 0.02f;
                    else if (segSide[s] == +1 && side < -0.012f * H) w *= 0.02f;
                    dist[s] = w;
                    idxBuf[s] = s;
                }

                // 取前 4 名
                GetTop4(dist, idxBuf, out int i0, out int i1, out int i2, out int i3,
                        out float w0, out float w1, out float w2, out float w3);

                weights[i] = new BoneWeight
                {
                    boneIndex0 = i0, weight0 = w0,
                    boneIndex1 = i1, weight1 = w1,
                    boneIndex2 = i2, weight2 = w2,
                    boneIndex3 = i3, weight3 = w3,
                };
            }

            mesh.boneWeights = weights;

            // bindposes：T-pose 下每根骨的 worldToLocal
            var bindposes = new Matrix4x4[boneList.Count];
            for (int s = 0; s < boneList.Count; s++)
                bindposes[s] = boneList[s].worldToLocalMatrix * root.transform.localToWorldMatrix;
            mesh.bindposes = bindposes;

            // 关键：写入 boneWeights 后必须重新上传，否则渲染器仍用旧的（全 0 = 全绑 Hips）
            mesh.UploadMeshData(false);

            // ── 网格资产（独立一份，不污染导入原资产）──
            var meshPath = Path.Combine(OutRoot, rigName + "_Mesh.asset").Replace('\\', '/');
            AssetDatabase.DeleteAsset(meshPath);
            AssetDatabase.CreateAsset(mesh, meshPath);
            AssetDatabase.SaveAssets();

            // ── 渲染物体 ──
            var meshGo = new GameObject(rigName + "_Mesh");
            meshGo.transform.SetParent(root.transform, false);
            var smr = meshGo.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = mesh;
            smr.bones = boneList.ToArray();
            smr.rootBone = hips;
            smr.updateWhenOffscreen = true;
            smr.sharedMaterial = BuildMaterial(rigName + "_Mat", Path.GetDirectoryName(objPath).Replace('\\', '/'));

            // ── Avatar ──
            var avatar = BuildAvatar(root, bones);
            if (avatar == null || !avatar.isValid)
            {
                Debug.LogError("[Hunyuan] Avatar 生成失败：" + rigName);
            }
            else
            {
                avatar.name = rigName + "Avatar";
                var avatarPath = Path.Combine(OutRoot, rigName + "Avatar.asset").Replace('\\', '/');
                AssetDatabase.CreateAsset(avatar, avatarPath);
                var animator = root.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.applyRootMotion = false;
                Debug.Log("[Hunyuan] Avatar 有效：" + avatarPath);
            }

            // ── 缩放与落地 ──
            float scale = targetHeight / H;
            root.transform.localScale = Vector3.one * scale;
            root.transform.position = Vector3.zero;

            var prefabPath = Path.Combine(OutRoot, rigName + ".prefab").Replace('\\', '/');
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            UnityEngine.Object.DestroyImmediate(root);
            Debug.Log(string.Format("[Hunyuan] 已生成 {0}  缩放={1:F4}  目标身高={2:F2}m", prefabPath, scale, targetHeight));
        }

        private static Material BuildMaterial(string matName, string dir)
        {
            string basePath = dir + "/texture_pbr_20250901.png";
            string normPath = dir + "/texture_pbr_20250901_normal.png";
            string msPath = dir + "/texture_metallicSmoothness.png";

            ConfigureTexture(basePath, linear: false, isNormal: false);
            ConfigureTexture(normPath, linear: true, isNormal: true);
            ConfigureTexture(msPath, linear: true, isNormal: false);

            var baseTex = AssetDatabase.LoadAssetAtPath<Texture2D>(basePath);
            var normTex = AssetDatabase.LoadAssetAtPath<Texture2D>(normPath);
            var msTex = AssetDatabase.LoadAssetAtPath<Texture2D>(msPath);

            var matPath = Path.Combine(OutRoot, matName + ".mat").Replace('\\', '/');
            AssetDatabase.DeleteAsset(matPath);

            var mat = new Material(Shader.Find("Standard")) { name = matName };
            if (baseTex != null) mat.SetTexture("_MainTex", baseTex);
            if (normTex != null)
            {
                mat.SetTexture("_BumpMap", normTex);
                mat.EnableKeyword("_NORMALMAP");
            }
            if (msTex != null)
            {
                mat.SetTexture("_MetallicGlossMap", msTex);
                // 用 MetallicGlossMap 的 A 通道当 smoothness
                mat.SetFloat("_SmoothnessTextureChannel", 0f);
                mat.EnableKeyword("_METALLICGLOSSMAP");
            }
            mat.SetFloat("_Metallic", 1f);
            mat.SetFloat("_Glossiness", 1f);
            mat.SetFloat("_GlossMapScale", 1f);
            mat.SetFloat("_Mode", 0f);
            mat.renderQueue = -1;
            mat.enableInstancing = true;

            AssetDatabase.CreateAsset(mat, matPath);
            Debug.Log("[Hunyuan] 材质已生成：" + matPath);
            return mat;
        }

        private static void ConfigureTexture(string path, bool linear, bool isNormal)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ti == null)
            {
                Debug.LogWarning("[Hunyuan] 找不到贴图导入器：" + path);
                return;
            }

            bool dirty = false;
            var wantType = isNormal ? TextureImporterType.NormalMap : TextureImporterType.Default;
            if (ti.textureType != wantType) { ti.textureType = wantType; dirty = true; }
            if (ti.sRGBTexture == linear) { ti.sRGBTexture = !linear; dirty = true; }
            if (ti.maxTextureSize < 4096) { ti.maxTextureSize = 4096; dirty = true; }
            if (ti.textureCompression != TextureImporterCompression.CompressedHQ)
            {
                ti.textureCompression = TextureImporterCompression.CompressedHQ;
                dirty = true;
            }
            if (dirty) ti.SaveAndReimport();
        }

        private static Avatar BuildAvatar(GameObject root, Dictionary<string, Transform> bones)
        {
            var human = new List<HumanBone>();
            void H(HumanBodyBones id, string boneName)
            {
                if (!bones.ContainsKey(boneName)) return;
                human.Add(new HumanBone
                {
                    humanName = HumanTrait.BoneName[(int)id],
                    boneName = boneName,
                    limit = new HumanLimit { useDefaultValues = true },
                });
            }

            H(HumanBodyBones.Hips, "Hips");
            H(HumanBodyBones.Spine, "Spine");
            H(HumanBodyBones.Chest, "Chest");
            H(HumanBodyBones.Neck, "Neck");
            H(HumanBodyBones.Head, "Head");
            H(HumanBodyBones.LeftShoulder, "LeftShoulder");
            H(HumanBodyBones.LeftUpperArm, "LeftUpperArm");
            H(HumanBodyBones.LeftLowerArm, "LeftLowerArm");
            H(HumanBodyBones.LeftHand, "LeftHand");
            H(HumanBodyBones.RightShoulder, "RightShoulder");
            H(HumanBodyBones.RightUpperArm, "RightUpperArm");
            H(HumanBodyBones.RightLowerArm, "RightLowerArm");
            H(HumanBodyBones.RightHand, "RightHand");
            H(HumanBodyBones.LeftUpperLeg, "LeftUpperLeg");
            H(HumanBodyBones.LeftLowerLeg, "LeftLowerLeg");
            H(HumanBodyBones.LeftFoot, "LeftFoot");
            H(HumanBodyBones.LeftToes, "LeftToes");
            H(HumanBodyBones.RightUpperLeg, "RightUpperLeg");
            H(HumanBodyBones.RightLowerLeg, "RightLowerLeg");
            H(HumanBodyBones.RightFoot, "RightFoot");
            H(HumanBodyBones.RightToes, "RightToes");

            var skeleton = new List<SkeletonBone>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                skeleton.Add(new SkeletonBone
                {
                    name = t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });
            }

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
            Debug.Log(string.Format("[Hunyuan] Avatar 生成：human={0} 骨骼={1} valid={2} humanScale={3:F3}",
                human.Count, skeleton.Count,
                avatar != null && avatar.isValid,
                avatar != null ? avatar.humanDescription.human.Length : 0));
            return avatar;
        }

        // ─────────────────────────── 工具 ───────────────────────────

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

        private static void GetTop4(float[] w, int[] idx, out int i0, out int i1, out int i2, out int i3,
                                    out float w0, out float w1, out float w2, out float w3)
        {
            i0 = i1 = i2 = i3 = 0;
            w0 = w1 = w2 = w3 = 0f;
            for (int s = 0; s < w.Length; s++)
            {
                float x = w[s];
                if (x > w0) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = i0; w1 = w0; i0 = s; w0 = x; }
                else if (x > w1) { i3 = i2; w3 = w2; i2 = i1; w2 = w1; i1 = s; w1 = x; }
                else if (x > w2) { i3 = i2; w3 = w2; i2 = s; w2 = x; }
                else if (x > w3) { i3 = s; w3 = x; }
            }
            float sum = w0 + w1 + w2 + w3;
            if (sum <= 0f) { w0 = 1f; return; }
            w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum;
        }
    }
}
