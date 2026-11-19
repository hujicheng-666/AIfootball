using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    /// <summary>渲染门将 Dive state 在不同 (Turn,Height) 下的关键帧，验证动作方向与姿态。</summary>
    public static class DiveRender
    {
        [MenuItem("AIfootball/动画/3. 渲染扑救验证图")]
        public static void Run()
        {
            var scene = EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            var keeper = GameObject.Find("GoalKeeper002");
            if (keeper == null) { Debug.LogError("[DiveRender] 找不到门将"); return; }

            var anim = keeper.GetComponentInChildren<Animator>(false);
            if (anim == null) { Debug.LogError("[DiveRender] 无 Animator"); return; }
            anim.applyRootMotion = false;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // 关掉 FSM，避免它抢动画
            foreach (var mb in keeper.GetComponents<MonoBehaviour>())
                if (mb != null && (mb.GetType().Name.Contains("FSM") || mb.GetType().Name.Contains("GoalKeeper")))
                    mb.enabled = false;

            // 相机：从正前方偏侧，看整体
            var cam = Camera.main;
            if (cam == null) foreach (var c in Object.FindObjectsOfType<Camera>()) { cam = c; break; }
            cam.enabled = true;
            cam.fieldOfView = 40f;

            var root = keeper.transform;
            var head = anim.GetBoneTransform(HumanBodyBones.Head);
            Vector3 focus = head != null ? head.position : root.position + Vector3.up;

            // 用例：(Turn, Height, 归一化时间)
            var cases = new (float turn, float h, string label)[]
            {
                (-1f, 0.85f, "T-1 H.85 高左"),
                ( 0f, 0.85f, "T 0 H.85 高中"),
                ( 1f, 0.85f, "T+1 H.85 高右"),
                (-1f, 0.50f, "T-1 H.50 中左"),
                ( 0f, 0.50f, "T 0 H.50 中中"),
                ( 1f, 0.50f, "T+1 H.50 中右"),
                (-1f, 0.15f, "T-1 H.15 低左"),
                ( 0f, 0.15f, "T 0 H.15 低中"),
                ( 1f, 0.15f, "T+1 H.15 低右"),
            };
            float[] times = { 0.15f, 0.30f, 0.45f };

            int W = 480, H = 520;
            var grid = new Texture2D(W * times.Length, H * cases.Length, TextureFormat.RGBA32, false);
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var sb = new StringBuilder("[DiveRender] 渲染结果\n");

            for (int ci = 0; ci < cases.Length; ci++)
            {
                var (turn, h, label) = cases[ci];
                anim.SetFloat("Turn", turn);
                anim.SetFloat("Height", h);
                for (int ti = 0; ti < times.Length; ti++)
                {
                    anim.Play("Dive", 0, times[ti]);
                    anim.Update(0.0001f);
                    anim.Update(0.0001f);

                    // 相机跟随到当前身体中心
                    Vector3 bodyCenter = root.position + Vector3.up * 1.0f;
                    cam.transform.position = bodyCenter + new Vector3(4.2f, 1.4f, -5.0f);
                    cam.transform.LookAt(bodyCenter);
                    cam.targetTexture = rt;
                    cam.Render();
                    RenderTexture.active = rt;
                    var t = new Texture2D(W, H, TextureFormat.RGBA32, false);
                    t.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                    t.Apply();
                    RenderTexture.active = null;
                    cam.targetTexture = null;

                    grid.SetPixels(ti * W, (cases.Length - 1 - ci) * H, W, H, t.GetPixels());
                    Object.DestroyImmediate(t);
                }
                sb.AppendLine("  " + label + " 已渲染");
            }

            grid.Apply();
            System.IO.Directory.CreateDirectory(".tmp_dive");
            string outPath = ".tmp_dive/dive_grid.png";
            System.IO.File.WriteAllBytes(outPath, grid.EncodeToPNG());
            Debug.Log(sb.ToString() + "[DiveRender] 写出 " + System.IO.Path.GetFullPath(outPath));
        }
    }
}
