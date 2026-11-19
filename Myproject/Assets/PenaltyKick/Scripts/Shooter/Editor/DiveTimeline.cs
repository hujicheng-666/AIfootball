using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>渲染 Dive 全段（0→3.8s）与 WakeupNoBall 衔接，检查 FSM 提前切换处是否穿地/断裂。</summary>
    public static class DiveTimeline
    {
        [MenuItem("AIfootball/动画/7. 渲染扑救时序")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var keeper = GameObject.Find("GoalKeeper002");
            if (keeper == null) { Debug.LogError("[Timeline] 无门将"); return; }

            var anim = keeper.GetComponentInChildren<Animator>(false);
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            foreach (var mb in keeper.GetComponents<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name.Contains("FSM") || mb.GetType().Name.Contains("GoalKeeper")))
                    mb.enabled = false;

            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }

            anim.SetFloat("Turn", -1f);
            anim.SetFloat("Height", 0.85f);

            // Dive 段：归一化 0.02..0.5（因为只会播前 1.2s 左右）
            float[] diveT = { 0.02f, 0.08f, 0.14f, 0.20f, 0.26f, 0.32f, 0.40f, 0.60f };
            float[] wakeT = { 0.0f, 0.15f, 0.30f, 0.5f, 0.7f, 0.9f };

            int W = 420, H = 420;
            int cols = diveT.Length, rows = 2;
            var grid = new Texture2D(W * cols, H * rows, TextureFormat.RGBA32, false);
            var sb = new StringBuilder("[Timeline] 渲染\n");

            for (int r = 0; r < rows; r++)
            {
                float[] ts = r == 0 ? diveT : wakeT;
                string state = r == 0 ? "Dive" : "WakeupNoBall";
                for (int i = 0; i < ts.Length; i++)
                {
                    anim.Play(state, 0, ts[i]);
                    anim.Update(0.0001f);
                    anim.Update(0.0001f);

                    Vector3 c = keeper.transform.position + Vector3.up * 1.0f;
                    cam.transform.position = c + new Vector3(4.0f, 1.2f, -4.4f);
                    cam.transform.LookAt(c);

                    var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
                    t.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    t.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = null;
                    grid.SetPixels(i * W, (rows - 1 - r) * H, W, H, t.GetPixels());
                    Object.DestroyImmediate(t);
                    Object.DestroyImmediate(rt);
                }
                sb.AppendLine("  " + state + " 已渲染 " + ts.Length + " 帧");
            }

            grid.Apply();
            System.IO.Directory.CreateDirectory(".tmp_dive");
            System.IO.File.WriteAllBytes(".tmp_dive/timeline.png", grid.EncodeToPNG());
            Debug.Log(sb + "[Timeline] 写出 " + System.IO.Path.GetFullPath(".tmp_dive/timeline.png"));
        }
    }
}
