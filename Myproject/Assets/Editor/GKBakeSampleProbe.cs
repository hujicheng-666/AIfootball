using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>把烘焙 Idle clip 采样到场景 GK 实例上，验证播放时 hips 世界位置是否正常。</summary>
public static class GKBakeSampleProbe
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(
                "Assets/SuperGoalie/Animations/GKBaked/metarig_metarigAction.anim");
            if (clip == null) throw new System.Exception("烘焙 Idle clip 不存在");

            // 找活跃 GK Character
            GameObject gkChar = null;
            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                if (t.name != "Character" || !t.gameObject.activeInHierarchy) continue;
                if (t.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) continue;
                bool inGk = false;
                for (var p = t.parent; p != null; p = p.parent)
                    if (p.name.Contains("GoalKeeper")) { inGk = true; break; }
                if (inGk) { gkChar = t.gameObject; break; }
            }
            if (gkChar == null) throw new System.Exception("未找到活跃 GK Character");

            var hips = gkChar.GetComponentInChildren<Transform>(true);
            foreach (var t in gkChar.GetComponentsInChildren<Transform>(true))
                if (t.name == "Hips" && t.parent != null && t.parent.name == "ShooterRig") { hips = t; break; }
            log.AppendLine("采样前 hips world=" + hips.position);

            // 逐帧采样
            float[] times = { 0f, 0.3f, 0.6f, 1.0f };
            foreach (float t in times)
            {
                AnimationMode.StartAnimationMode();
                try { AnimationMode.SampleAnimationClip(gkChar, clip, t); }
                finally { AnimationMode.StopAnimationMode(); }
                log.AppendLine("t=" + t + "s 采样后 hips world=" + hips.position
                    + " localPos=" + hips.localPosition
                    + " parent=" + hips.parent.name + " parentScale=" + hips.parent.localScale);
            }

            // 渲染
            Camera cam = null;
            foreach (var c in Object.FindObjectsOfType<Camera>())
                if (c.enabled && c.gameObject.activeInHierarchy) { cam = c; break; }
            if (cam != null)
            {
                // 采到 t=1.0 的姿态下渲染
                AnimationMode.StartAnimationMode();
                try { AnimationMode.SampleAnimationClip(gkChar, clip, 1.0f); } finally { }
                var rt = new RenderTexture(1280, 720, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                System.IO.File.WriteAllBytes(@"D:\AIfootball\gk_sample_probe.png", tex.EncodeToPNG());
                cam.targetTexture = null;
                RenderTexture.active = null;
                log.AppendLine("已渲染 gk_sample_probe.png");
            }
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_sample_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
