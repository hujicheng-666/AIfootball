using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>用 Animator.Play + Update 模拟运行时播放烘焙动画，验证 GK 播放姿态与位置。</summary>
public static class GKBakePlayProbe
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var cam = Camera.main;
            if (cam == null)
                foreach (var c in Object.FindObjectsOfType<Camera>())
                    if (c.enabled && c.gameObject.activeInHierarchy) { cam = c; break; }

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
            var ani = gkChar.GetComponent<Animator>();
            log.AppendLine("controller=" + (ani.runtimeAnimatorController != null ? ani.runtimeAnimatorController.name : "NULL"));

            Transform hips = null;
            foreach (var t in gkChar.GetComponentsInChildren<Transform>(true))
                if (t.name == "Hips" && t.parent != null && t.parent.name == "ShooterRig") { hips = t; break; }
            log.AppendLine("播放前 hips world=" + hips.position);

            float[] times = { 0.05f, 0.4f, 0.8f, 1.2f };
            foreach (float tt in times)
            {
                ani.Play(0, 0, tt);
                ani.Update(0.02f);
                log.AppendLine("norm=" + tt + " 播放后 hips world=" + hips.position
                    + " localPos=" + hips.localPosition);
                if (cam != null && tt == 0.4f)
                {
                    var rt = new RenderTexture(1280, 720, 24);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                    tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                    tex.Apply();
                    System.IO.File.WriteAllBytes(@"D:\AIfootball\gk_play_probe.png", tex.EncodeToPNG());
                    cam.targetTexture = null;
                    RenderTexture.active = null;
                    log.AppendLine("已渲染 gk_play_probe.png (norm=0.4)");
                }
            }
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_play_log.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
