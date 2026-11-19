using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 模型朝向审计：判断射手网格到底面朝 +Z 还是 -Z，以及 Humanoid 左右骨是否映射正确。
    ///
    /// 为什么必须有这一步
    /// ------------------
    /// 动捕数据的转换保证「角色面朝 +Z」。如果模型网格实际面朝 -Z（或 Humanoid 的
    /// Left/Right 映射颠倒），骨骼驱动再准，看上去也是「手和腿都是反的」——
    /// 手臂交叉、膝盖朝后。这类问题在数值上完全看不出来（骨向量夹角可以全是 0°），
    /// 只能用几何事实判断，所以单独做成一次审计。
    ///
    /// 输出
    /// ----
    /// · bind pose 下每根 Humanoid 骨相对 Hips 的坐标；
    /// · 脚趾相对脚踝的朝向（人脚趾朝前，这是最硬的「前后」判据）；
    /// · 从 +Z / -Z / +X / -X 四个方向各渲染一张，哪边是脸一眼就能看出来。
    /// </summary>
    public static class ModelAudit
    {
        private const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";
        private const string OutDir = "D:/AIfootball/.tmp_audit";

        private const int W = 420;
        private const int H = 560;

        public static void Run()
        {
            System.IO.Directory.CreateDirectory(OutDir);
            var sb = new StringBuilder();

            UnityEngine.SceneManagement.Scene scene = SceneManager.GetActiveScene();
            if (!scene.isLoaded || scene.name != "Demo")
            {
                scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                    ScenePath, UnityEditor.SceneManagement.OpenSceneMode.Single);
                sb.AppendLine("打开场景 " + ScenePath);
            }

            // 找到射手：优先按名字，其次按 Humanoid Animator
            GameObject shooter = GameObject.Find("Shooter");
            if (shooter == null)
            {
                foreach (var a in Object.FindObjectsOfType<Animator>())
                {
                    if (a.avatar != null && a.avatar.isHuman) { shooter = a.gameObject; break; }
                }
            }
            if (shooter == null) { Debug.LogError("[Audit] 找不到射手"); return; }

            var anim = shooter.GetComponentInChildren<Animator>();
            if (anim == null) { Debug.LogError("[Audit] 射手没有 Animator"); return; }

            sb.AppendLine("=== 射手对象 ===");
            sb.AppendLine("  name      " + shooter.name);
            sb.AppendLine("  position  " + shooter.transform.position.ToString("F3"));
            sb.AppendLine("  rotation  " + shooter.transform.rotation.eulerAngles.ToString("F2"));
            sb.AppendLine("  localScale " + shooter.transform.localScale.ToString("F3"));
            sb.AppendLine("  lossyScale " + shooter.transform.lossyScale.ToString("F3"));

            sb.AppendLine("=== Avatar ===");
            sb.AppendLine("  isValid " + anim.avatar.isValid + "  isHuman " + anim.avatar.isHuman);
            var hd = anim.avatar.humanDescription;
            sb.AppendLine("  human 骨骼映射条数 " + hd.human.Length);
            foreach (var h in hd.human)
            {
                if (h.boneName == null || h.boneName.Length == 0) continue;
                sb.AppendLine(string.Format("    {0,-16} → {1}", h.humanName, h.boneName));
            }

            // 停掉动画，回到 bind pose
            anim.enabled = false;
            var smr = shooter.GetComponentInChildren<SkinnedMeshRenderer>();
            if (smr == null) { Debug.LogError("[Audit] 没有 SkinnedMeshRenderer"); return; }

            sb.AppendLine("=== 网格 ===");
            sb.AppendLine("  mesh " + smr.sharedMesh.name + "  顶点 " + smr.sharedMesh.vertexCount);
            sb.AppendLine("  bounds center " + smr.bounds.center.ToString("F3") + " size " + smr.bounds.size.ToString("F3"));
            sb.AppendLine("  renderer lossyScale " + smr.transform.lossyScale.ToString("F3"));

            // 重置到 bind pose
            smr.sharedMesh = smr.sharedMesh; // 不变，但确保已被求值
            anim.Rebind();
            var bones = new List<Transform>();
            foreach (var b in smr.bones) bones.Add(b);
            ApplyBindPose(bones, smr.sharedMesh);

            Transform hips = anim.GetBoneTransform(HumanBodyBones.Hips);
            Vector3 hipPos = hips != null ? hips.position : Vector3.zero;

            sb.AppendLine("=== bind pose 骨骼世界坐标（相对 Hips）===");
            var probe = new[]
            {
                HumanBodyBones.Hips, HumanBodyBones.Spine, HumanBodyBones.Chest,
                HumanBodyBones.Neck, HumanBodyBones.Head,
                HumanBodyBones.LeftShoulder, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand, HumanBodyBones.RightShoulder, HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand,
                HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot,
                HumanBodyBones.LeftToes, HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg,
                HumanBodyBones.RightFoot, HumanBodyBones.RightToes,
            };
            foreach (var b in probe)
            {
                var t = anim.GetBoneTransform(b);
                if (t == null) { sb.AppendLine(string.Format("    {0,-16} 缺失", b)); continue; }
                Vector3 d = t.position - hipPos;
                sb.AppendLine(string.Format("    {0,-16} ({1,7:F3},{2,7:F3},{3,7:F3})", b, d.x, d.y, d.z));
            }

            // 脚趾朝向：人脚趾一律朝前，与身体朝向同向
            sb.AppendLine("=== 脚趾朝向（判断前后）===");
            AppendToeDirection(sb, anim, HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, "左");
            AppendToeDirection(sb, anim, HumanBodyBones.RightFoot, HumanBodyBones.RightToes, "右");

            // 肩线 / 髋线方向（判断左右）
            sb.AppendLine("=== 左右连线 ===");
            Transform ls = anim.GetBoneTransform(HumanBodyBones.LeftShoulder);
            Transform rs = anim.GetBoneTransform(HumanBodyBones.RightShoulder);
            Transform lh = anim.GetBoneTransform(HumanBodyBones.LeftUpperLeg);
            Transform rh = anim.GetBoneTransform(HumanBodyBones.RightUpperLeg);
            if (ls != null && rs != null)
                sb.AppendLine("  RightShoulder - LeftShoulder = " + (rs.position - ls.position).ToString("F3") +
                              "   （+X 表示「左→右」指向 +X，符合面朝 +Z 的约定）");
            if (lh != null && rh != null)
                sb.AppendLine("  RightHip - LeftHip = " + (rh.position - lh.position).ToString("F3"));

            // 面部顶点：头骨以上顶点在 Z 上的平均位置，用于判断脸的朝向
            AppendFaceHint(sb, smr, hipPos);

            System.IO.File.WriteAllText(OutDir + "/audit.txt", sb.ToString(), Encoding.UTF8);

            // 四向渲染
            RenderViews(shooter, anim, smr);

            Debug.Log("[Audit] 完成，输出到 " + OutDir);
        }

        private static void ApplyBindPose(List<Transform> bones, Mesh mesh)
        {
            if (mesh == null || bones.Count != mesh.bindposes.Length) return;
            for (int i = 0; i < bones.Count; i++)
            {
                if (bones[i] == null) continue;
                // bindpose 是「骨骼空间 → 网格空间」，反推骨骼的世界矩阵
                Matrix4x4 bind = mesh.bindposes[i].inverse;
                bones[i].position = bind.GetColumn(3);
                bones[i].rotation = bind.rotation;
            }
        }

        private static void AppendToeDirection(StringBuilder sb, Animator anim,
                                               HumanBodyBones foot, HumanBodyBones toe, string side)
        {
            var f = anim.GetBoneTransform(foot);
            var t = anim.GetBoneTransform(toe);
            if (f == null || t == null) { sb.AppendLine("  " + side + "：脚趾骨缺失"); return; }
            Vector3 d = t.position - f.position;
            sb.AppendLine(string.Format("  {0}脚：趾-踝 = ({1,6:F3},{2,6:F3},{3,6:F3})  |水平|={4:F3}  前后分量 {5}",
                side, d.x, d.y, d.z, new Vector2(d.x, d.z).magnitude,
                Mathf.Abs(d.z) < 0.01f ? "≈0（无参考）" : (d.z > 0 ? "+Z 前" : "-Z 后")));
        }

        private static void AppendFaceHint(StringBuilder sb, SkinnedMeshRenderer smr, Vector3 hipPos)
        {
            Mesh m = smr.sharedMesh;
            var verts = m.vertices;
            var weights = m.boneWeights;
            if (verts.Length == 0 || weights.Length != verts.Length) return;

            // 头顶以上 10% 的顶点，其 Z 重心偏向哪边基本就是脸的方向
            float maxY = float.MinValue, minY = float.MaxValue;
            for (int i = 0; i < verts.Length; i++)
            {
                if (verts[i].y > maxY) maxY = verts[i].y;
                if (verts[i].y < minY) minY = verts[i].y;
            }
            float cut = Mathf.Lerp(minY, maxY, 0.86f);
            double sz = 0; int n = 0; double sx = 0;
            for (int i = 0; i < verts.Length; i++)
            {
                if (verts[i].y < cut) continue;
                sz += verts[i].z; sx += verts[i].x; n++;
            }
            if (n == 0) return;
            sb.AppendLine("=== 头部顶点重心（脸通常在这一侧）===");
            sb.AppendLine(string.Format("  头顶区 {0} 个顶点，局部重心 x={1:F3} z={2:F3}", n, sx / n, sz / n));
            sb.AppendLine("  （局部空间；若模型面朝 +Z，脸的鼻尖应使 z 重心偏 +Z）");
        }

        private static void RenderViews(GameObject shooter, Animator anim, SkinnedMeshRenderer smr)
        {
            var camGo = new GameObject("__audit_cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.96f, 0.97f, 0.98f);
            cam.fieldOfView = 32f;
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = 100f;

            var lightGo = new GameObject("__audit_light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.15f;
            lightGo.transform.rotation = Quaternion.Euler(42f, -35f, 0f);

            var rtex = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rtex;

            Bounds b = smr.bounds;
            Vector3 center = b.center;
            float height = Mathf.Max(b.size.y, 0.5f);

            // 手动重算一次包围盒，Rebind 后 bounds 未必刷新
            Vector3 hipPos = anim.GetBoneTransform(HumanBodyBones.Hips).position;
            height = Mathf.Max(height, Vector3.Distance(hipPos, anim.GetBoneTransform(HumanBodyBones.Head).position) * 2.1f);
            center = new Vector3(hipPos.x, hipPos.y + height * 0.05f, hipPos.z);

            float dist = height * 1.55f;
            var dirs = new[]
            {
                new { name = "view_from_+Z", off = new Vector3(0, 0, dist) },
                new { name = "view_from_-Z", off = new Vector3(0, 0, -dist) },
                new { name = "view_from_+X", off = new Vector3(dist, 0, 0) },
                new { name = "view_from_-X", off = new Vector3(-dist, 0, 0) },
            };

            var grid = new Texture2D(W * 4, H, TextureFormat.RGBA32, false);
            for (int i = 0; i < dirs.Length; i++)
            {
                cam.transform.position = center + dirs[i].off + Vector3.up * (height * 0.10f);
                cam.transform.LookAt(center);
                cam.Render();
                RenderTexture.active = rtex;

                var shot = new Texture2D(W, H, TextureFormat.RGBA32, false);
                shot.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                shot.Apply();
                RenderTexture.active = null;

                // 相机在 -Z 侧时，画面左右会天然镜像；统一按「观察者视角」保留，看图判断即可
                grid.SetPixels(i * W, 0, W, H, shot.GetPixels());
                Object.DestroyImmediate(shot);
            }
            grid.Apply();
            System.IO.File.WriteAllBytes(OutDir + "/views.png", grid.EncodeToPNG());

            cam.targetTexture = null;
            Object.DestroyImmediate(rtex);
            Object.DestroyImmediate(camGo);
            Object.DestroyImmediate(lightGo);
        }
    }
}
