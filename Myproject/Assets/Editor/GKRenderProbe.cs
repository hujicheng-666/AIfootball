using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>渲染 Demo 场景一帧保存 PNG，看 GK 是否可见/在哪。</summary>
public static class GKRenderProbe
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);

            // 找 GK 场景实例
            Transform gkRoot = null;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
                if (t.name == "GoalKeeper002") { gkRoot = t; break; }
            if (gkRoot == null) log.AppendLine("!! 未找到 GoalKeeper002");
            else
            {
                log.AppendLine("GoalKeeper002 pos=" + gkRoot.position + " scale=" + gkRoot.localScale
                    + " activeInHierarchy=" + gkRoot.gameObject.activeInHierarchy);
                var hips = gkRoot.GetComponentInChildren<Transform>(true);
                foreach (var t in gkRoot.GetComponentsInChildren<Transform>(true))
                {
                    if (t.name == "Hips" || t.name == "ShooterRig" || t.name == "Head")
                        log.AppendLine("  " + t.name + " path=" + PathOf(gkRoot, t)
                            + " world=" + t.position + " parentScale=" + (t.parent != null ? t.parent.localScale.ToString() : "-")
                            + " localPos=" + t.localPosition);
                }
                foreach (var smr in gkRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                    log.AppendLine("  SMR " + smr.name + " visible=" + smr.isVisible
                        + " boundsWorld=" + smr.bounds.center + "/" + smr.bounds.size
                        + " updateWhenOffscreen=" + smr.updateWhenOffscreen
                        + " mat=" + (smr.sharedMaterial == null ? "NULL" : smr.sharedMaterial.name));
            }

            // 渲染一帧
            Camera cam = null;
            foreach (var c in Object.FindObjectsOfType<Camera>())
                if (c.enabled && c.gameObject.activeInHierarchy) { cam = c; break; }
            if (cam == null) log.AppendLine("!! 无可用相机");
            else
            {
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(@"D:\AIfootball\gk_render_probe.png", tex.EncodeToPNG());
                cam.targetTexture = null;
                RenderTexture.active = null;
                log.AppendLine("已渲染 gk_render_probe.png (cam=" + cam.name + ")");
            }
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_probe_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }

    static string PathOf(Transform root, Transform t)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var p = t; p != null && p != root; p = p.parent) parts.Insert(0, p.name);
        return string.Join("/", parts);
    }
}
