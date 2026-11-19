using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 把 FBX 内的 23 个 MeshPart 顶点降采样导出为 CSV，
    /// 供外部（Python）做骨架几何拟合。
    ///
    /// 为什么要导出：混元模型的身体比例不标准（实测臂展/身高仅 0.88，
    /// 手臂短粗），套解剖学硬比例会系统偏差。关节必须从真实点云拟合。
    /// </summary>
    public static class HunyuanCloud
    {
        private const string Dir = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/FbxIn";
        private const string OutDir = ".tmp_fbx_probe";
        private static readonly string[] Models = { "ModelA", "ModelB" };

        /// <summary>采样步长：85 万顶点 → 约 6.5 万点，统计量已足够且文件只有几 MB。</summary>
        private const int Step = 13;

        [MenuItem("AIfootball/混元FBX/3. 导出点云")]
        public static void ExportAll()
        {
            Directory.CreateDirectory(OutDir);
            foreach (var n in Models) ExportOne(n);
            AssetDatabase.Refresh();
        }

        private static void ExportOne(string tag)
        {
            string fbx = Dir + "/" + tag + ".fbx";
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            if (go == null) { Debug.LogError("[Cloud] 找不到 " + fbx); return; }

            var sb = new StringBuilder();
            sb.AppendLine("x,y,z");
            int kept = 0, total = 0;
            var bb = new Bounds();
            bool hasBB = false;

            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null) continue;
                var v = m.vertices;
                var M = mf.transform.localToWorldMatrix;
                total += v.Length;
                for (int i = 0; i < v.Length; i += Step)
                {
                    var p = M.MultiplyPoint3x4(v[i]);
                    sb.Append(p.x.ToString("F5")).Append(',')
                      .Append(p.y.ToString("F5")).Append(',')
                      .Append(p.z.ToString("F5")).Append('\n');
                    kept++;
                    if (!hasBB) { bb = new Bounds(p, Vector3.zero); hasBB = true; }
                    else bb.Encapsulate(p);
                }
            }

            string outPath = Path.Combine(OutDir, tag + "_cloud.csv");
            File.WriteAllText(outPath, sb.ToString());

            string stat = string.Format(
                "{{\"tag\":\"{0}\",\"totalVerts\":{1},\"sampled\":{2},\"step\":{3}," +
                "\"bbMin\":[{4},{5},{6}],\"bbMax\":[{7},{8},{9}]}}",
                tag, total, kept, Step,
                bb.min.x, bb.min.y, bb.min.z, bb.max.x, bb.max.y, bb.max.z);
            File.WriteAllText(Path.Combine(OutDir, tag + "_cloud.json"), stat);

            Debug.Log("[Cloud] " + tag + " 总顶点=" + total.ToString("N0") +
                      " 采样=" + kept.ToString("N0") + " bounds=" + bb.size.ToString("F4") +
                      " -> " + Path.GetFullPath(outPath));
        }
    }
}
