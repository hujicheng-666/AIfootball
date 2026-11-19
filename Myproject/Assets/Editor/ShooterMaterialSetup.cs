// 为混元射手建立正确的 Standard 材质并映射到 FBX 内部材质
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEditor;

public static class ShooterMaterialSetup
{
    const string FbxPath = "Assets/SuperGoalie/Meshes/Characters/ShooterMixamo/shooter_mixamo_rig_150k.fbx";
    const string MatPath = "Assets/SuperGoalie/Meshes/Characters/ShooterMixamo/ShooterKit.mat";
    // 注意：Blender 导出的 *.fbm 里 base 贴图内容有误（平均 RGB 30,106,109 偏青绿），
    // 因此改用工程内原始 ModelB 贴图（中性灰 107,113,111），与 Blender 渲染一致
    const string TexDir = "Assets/SuperGoalie/Meshes/Characters/Hunyuan/FbxIn/Textures/ModelB";

    [MenuItem("Tools/修复射手材质")]
    public static void Run()
    {
        var L = new System.Text.StringBuilder();
        Action<string> P = s => { L.AppendLine("[MAT] " + s); Debug.Log("[MAT] " + s); };
        try
        {
            string baseP = TexDir + "/texture_pbr_20250901.png";
            string normP = TexDir + "/texture_pbr_20250901_normal.png";
            string metalP = TexDir + "/texture_pbr_20250901_metallic.png";
            string roughP = TexDir + "/texture_pbr_20250901_roughness.png";

            // 贴图导入设置：normal 必须是 NormalMap（否则会像颜色一样被当成 albedo）
            foreach (var pair in new[] { new[] { normP, "normal" }, new[] { metalP, "linear" }, new[] { roughP, "linear" } })
            {
                var ti = AssetImporter.GetAtPath(pair[0]) as TextureImporter;
                if (ti == null) { P("贴图缺失: " + pair[0]); continue; }
                bool dirty = false;
                if (pair[1] == "normal" && ti.textureType != TextureImporterType.NormalMap)
                { ti.textureType = TextureImporterType.NormalMap; dirty = true; }
                if (pair[1] == "linear" && ti.sRGBTexture)
                { ti.sRGBTexture = false; dirty = true; }
                if (dirty) { ti.SaveAndReimport(); P("已修正贴图类型: " + Path.GetFileName(pair[0])); }
            }

            var texBase = AssetDatabase.LoadAssetAtPath<Texture2D>(baseP);
            var texNorm = AssetDatabase.LoadAssetAtPath<Texture2D>(normP);
            var texMetal = AssetDatabase.LoadAssetAtPath<Texture2D>(metalP);
            P("贴图加载: base=" + (texBase != null) + " normal=" + (texNorm != null) + " metallic=" + (texMetal != null));
            if (texBase == null) { P("ERROR base 贴图加载失败"); Flush(L); return; }

            // 建/更新材质
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null)
            {
                mat = new Material(Shader.Find("Standard"));
                mat.name = "ShooterKit";
                AssetDatabase.CreateAsset(mat, MatPath);
                P("新建材质 " + MatPath);
            }
            mat.shader = Shader.Find("Standard");
            mat.SetTexture("_MainTex", texBase);
            if (texNorm != null) { mat.SetTexture("_BumpMap", texNorm); mat.EnableKeyword("_NORMALMAP"); }
            if (texMetal != null) { mat.SetTexture("_MetallicGlossMap", texMetal); mat.EnableKeyword("_METALMAP"); }
            mat.SetFloat("_Metallic", 0.0f);
            mat.SetFloat("_Glossiness", 0.28f);
            mat.SetFloat("_BumpScale", 1.0f);
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            P("材质已配置: _MainTex=" + texBase.name + " _BumpMap=" + (texNorm != null ? texNorm.name : "null"));

            // 映射到 FBX 内部材质
            var mi = AssetImporter.GetAtPath(FbxPath) as ModelImporter;
            if (mi == null) { P("ERROR 无 ModelImporter"); Flush(L); return; }

            var names = new System.Collections.Generic.List<string>();
            var all = AssetDatabase.LoadAllAssetsAtPath(FbxPath);
            foreach (var o in all)
                if (o is Material) names.Add(o.name);
            P("FBX 内部材质: " + (names.Count > 0 ? string.Join(", ", names) : "(无)"));

            var target = names.Count > 0 ? names[0] : "Material";
            mi.AddRemap(new AssetImporter.SourceAssetIdentifier(typeof(Material), target), mat);
            mi.SaveAndReimport();
            P("已映射 FBX 材质 '" + target + "' -> ShooterKit.mat");
            P("MAT_DONE");
        }
        catch (Exception e) { P("EXCEPTION: " + e); }
        Flush(L);
    }

    static void Flush(System.Text.StringBuilder L)
    {
        try
        {
            var outDir = Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath));
            File.WriteAllText(Path.Combine(outDir, ".tmp_unity_material.txt"), L.ToString());
        }
        catch { }
    }
}
