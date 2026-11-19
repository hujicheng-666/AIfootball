using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 混元导出的 OBJ 角色导入配置。
    /// 关键点：OBJ 无骨骼、无动画，且面数达 150 万，需要显式关掉网格优化与顶点焊接，
    /// 否则 Unity 默认流程会在导入阶段耗费极长时间，且焊接会破坏 UV 接缝。
    /// </summary>
    public static class HunyuanImporter
    {
        private const string ShooterObj = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/Shooter/Shooter.obj";
        private const string KeeperObj = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/Goalkeeper/Goalkeeper.obj";

        [MenuItem("AIfootball/混元/1. 配置并导入模型")]
        public static void ConfigureAndImport()
        {
            Configure(ShooterObj);
            Configure(KeeperObj);
            Report(ShooterObj);
            Report(KeeperObj);
        }

        private static void Configure(string path)
        {
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi == null)
            {
                Debug.LogError("[Hunyuan] 找不到导入器：" + path);
                return;
            }

            mi.globalScale = 1f;
            mi.useFileScale = false;
            mi.importNormals = ModelImporterNormals.Calculate;
            mi.importTangents = ModelImporterTangents.None;
            mi.materialImportMode = ModelImporterMaterialImportMode.None; // 材质自己建
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.optimizeMeshPolygons = false;     // 150 万面，优化阶段会卡死
            mi.optimizeMeshVertices = false;
            mi.weldVertices = false;             // 焊接会破坏 UV 接缝
            mi.importBlendShapes = false;
            mi.importVisibility = false;
            mi.importCameras = false;
            mi.importLights = false;
            mi.isReadable = true;                // 蒙皮计算需要读顶点
            mi.generateSecondaryUV = false;
            mi.addCollider = false;

            EditorUtility.SetDirty(mi);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Debug.Log("[Hunyuan] 已配置并导入：" + path);
        }

        private static void Report(string path)
        {
            var go = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (go == null)
            {
                Debug.LogError("[Hunyuan] 导入失败或为空：" + path);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("[Hunyuan] ===== " + Path.GetFileName(path) + " =====");
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var m = mf.sharedMesh;
                if (m == null) continue;
                sb.AppendLine(string.Format(
                    "  网格 {0}: 顶点={1:N0} 三角={2:N0} 子网格={3} bounds={4}",
                    m.name, m.vertexCount, m.triangles.Length / 3, m.subMeshCount, m.bounds.size));
                sb.AppendLine(string.Format("  indexFormat={0}  uv={1}  normals={2}",
                    m.indexFormat,
                    m.uv != null && m.uv.Length > 0 ? "有" : "无",
                    m.normals != null && m.normals.Length > 0 ? "有" : "无"));
            }
            foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            {
                sb.AppendLine("  渲染器: " + r.GetType().Name + "  材质=" +
                    (r.sharedMaterial != null ? r.sharedMaterial.name : "null"));
            }
            Debug.Log(sb.ToString());
        }
    }
}
