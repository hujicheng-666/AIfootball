using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>
    /// 修正混元角色贴图的导入设置。默认导入把 4096 降到 2048、用 DXT 压缩、
    /// aniso=1（等于关闭各向异性过滤），斜视角下球衣细节必然发糊；
    /// 密集纹理经 DXT1 还会压出块状噪点（看起来像"脏污黑斑"）。
    /// </summary>
    public static class TextureQualityFix
    {
        private const string TexRoot =
            "Assets/SuperGoalie/Meshes/Characters/Hunyuan/FbxIn/Textures";

        [MenuItem("AIfootball/混元FBX/8. 修复贴图画质")]
        public static void Run()
        {
            int n = 0;
            foreach (var dir in new[] { "ModelA", "ModelB" })
            {
                string path = TexRoot + "/" + dir;
                if (!Directory.Exists(path)) continue;

                foreach (var f in Directory.GetFiles(path, "*.png"))
                {
                    string p = f.Replace('\\', '/');
                    var ti = AssetImporter.GetAtPath(p) as TextureImporter;
                    if (ti == null)
                    {
                        Debug.LogWarning("[TexFix] 无法读取导入器: " + p);
                        continue;
                    }

                    bool isNormal = p.ToLowerInvariant().Contains("_normal");
                    bool isMask = p.ToLowerInvariant().Contains("_metallic")
                               || p.ToLowerInvariant().Contains("_roughness");

                    // 1) 不再降采样
                    ti.maxTextureSize = 4096;

                    // 2) 关闭压缩（DXT 会把渐变和法线的精度毁掉）
                    ti.textureCompression = TextureImporterCompression.Uncompressed;

                    // 3) 三线性 + 16x 各向异性：看台斜视时纹理不再糊成一片
                    ti.filterMode = FilterMode.Trilinear;
                    ti.anisoLevel = 16;

                    // 4) 类型与色彩空间
                    if (isNormal)
                    {
                        ti.textureType = TextureImporterType.NormalMap;
                        ti.sRGBTexture = false;
                    }
                    else if (isMask)
                    {
                        ti.textureType = TextureImporterType.Default;
                        ti.sRGBTexture = false;      // 金属度/粗糙度是线性数据
                    }
                    else
                    {
                        ti.textureType = TextureImporterType.Default;
                        ti.sRGBTexture = true;
                    }

                    ti.mipmapEnabled = true;
                    ti.isReadable = false;
                    ti.npotScale = TextureImporterNPOTScale.None;

                    // 平台覆盖：Build 时也按同样的规格来，避免编辑器好看、打包后糊
                    var st = ti.GetPlatformTextureSettings("Standalone");
                    st.overridden = true;
                    st.maxTextureSize = 4096;
                    st.format = TextureImporterFormat.RGBA32;
                    st.textureCompression = TextureImporterCompression.Uncompressed;
                    ti.SetPlatformTextureSettings(st);

                    EditorUtility.SetDirty(ti);
                    ti.SaveAndReimport();
                    n++;
                    Debug.Log(string.Format("[TexFix] {0}  4096/未压缩/Trilinear/aniso16{1}",
                        p.Substring(p.LastIndexOf('/') + 1), isNormal ? "/NormalMap" : ""));
                }
            }
            AssetDatabase.Refresh();
            Debug.Log("[TexFix] 完成，共处理 " + n + " 张贴图");
        }

        /// <summary>把材质的高光/法线强度调到能看清明暗层次的范围。</summary>
        [MenuItem("AIfootball/混元FBX/9. 调整材质表现")]
        public static void TuneMaterials()
        {
            string root = "Assets/SuperGoalie/Meshes/Characters/Hunyuan";
            foreach (var name in new[] { "GoalkeeperRig2_Mat", "ShooterRig2_Mat" })
            {
                string mp = root + "/" + name + ".mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(mp);
                if (mat == null) { Debug.LogWarning("[TexFix] 缺材质 " + mp); continue; }

                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Glossiness", 0.22f);      // 布料不该高光
                if (mat.HasProperty("_BumpScale")) mat.SetFloat("_BumpScale", 0.7f);
                if (mat.HasProperty("_GlossyReflections")) mat.SetFloat("_GlossyReflections", 0f);
                EditorUtility.SetDirty(mat);

                var main = mat.GetTexture("_MainTex");
                var bump = mat.GetTexture("_BumpMap");
                Debug.Log(string.Format("[TexFix] {0}  MainTex={1}  BumpMap={2}",
                    name,
                    main != null ? main.name + " " + main.width + "x" + main.height : "null",
                    bump != null ? bump.name : "null"));
            }
            AssetDatabase.SaveAssets();
        }
    }
}
