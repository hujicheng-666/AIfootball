using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using PenaltyKickPlatform.Shooter;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>渲染射手原姿态与镜像姿态的触球帧，验证镜像后无反向关节。</summary>
    public static class ShooterMirrorCheck
    {
        [MenuItem("AIfootball/动画/6. 验证射手镜像姿态")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var shooter = GameObject.Find("Shooter");
            if (shooter == null) { Debug.LogError("[MirrorCheck] 找不到 Shooter"); return; }

            var rt = shooter.GetComponent("ShooterRetargeter") as ShooterRetargeter;
            if (rt == null) { Debug.LogError("[MirrorCheck] 缺 ShooterRetargeter"); return; }
            rt.SetAutoUpdate(false);
            // batchmode 不会执行 Awake，手动完成初始化
            rt.CaptureRestPose();

            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }

            string[] ids = { "ShooterPoses/00008", "ShooterPoses/00008_m" };
            int W = 520, H = 640;
            var grid = new Texture2D(W * 4, H * ids.Length, TextureFormat.RGBA32, false);
            var sb = new StringBuilder("[MirrorCheck] 渲染\n");

            for (int i = 0; i < ids.Length; i++)
            {
                var ta = Resources.Load<TextAsset>(ids[i]);
                if (ta == null) { sb.AppendLine("  缺失 " + ids[i]); continue; }
                var clip = ShooterPoseClip.FromJson(ta.text);
                if (clip == null) { sb.AppendLine("  解析失败 " + ids[i]); continue; }
                rt.SetClip(clip);
                int kf = Mathf.Clamp(clip.kickFrame, 0, clip.FrameCount - 1);

                float[] tset = {
                    clip.frames[Mathf.Max(0, kf - 4)].t,
                    clip.frames[Mathf.Max(0, kf - 2)].t,
                    clip.frames[kf].t,
                    clip.frames[Mathf.Min(clip.FrameCount - 1, kf + 3)].t,
                };

                for (int j = 0; j < 4; j++)
                {
                    rt.SetTime(tset[j]);
                    rt.ApplyPose();

                    Vector3 c = shooter.transform.position + Vector3.up * 0.95f;
                    cam.transform.position = c + new Vector3(3.6f, 0.9f, -4.2f);
                    cam.transform.LookAt(c);

                    var rtt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rtt;
                    cam.Render();
                    RenderTexture.active = rtt;
                    var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
                    t.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    t.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    grid.SetPixels(j * W, (ids.Length - 1 - i) * H, W, H, t.GetPixels());
                    Object.DestroyImmediate(t);
                    Object.DestroyImmediate(rtt);
                }
                sb.AppendLine("  " + ids[i] + " 触球帧=" + kf);
            }

            grid.Apply();
            System.IO.Directory.CreateDirectory(".tmp_mirror");
            System.IO.File.WriteAllBytes(".tmp_mirror/shooter_mirror.png", grid.EncodeToPNG());
            rt.ApplyRestPose();
            Debug.Log(sb + "[MirrorCheck] 写出 " + System.IO.Path.GetFullPath(".tmp_mirror/shooter_mirror.png"));
        }
    }
}
