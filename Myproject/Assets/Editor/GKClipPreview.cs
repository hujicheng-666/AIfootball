using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using System.Linq;

/// <summary>
/// Unity 内采样新守门员动作并渲染 PNG 抽查（Avatar 映射/朝向/贴图验证）。
/// CLI: Unity.exe -projectPath ... -executeMethod GKClipPreview.RunCli -batchmode -quit
/// </summary>
public static class GKClipPreview
{
    static readonly string[] ClipKeys =
    {
        "GK_10_IdleNoBall", "GK_12_CatchLow", "GK_11_CatchMedium",
        "GK_13_CatchJumpHigh", "GK_15_DivingSaveRight", "GK_18_DivingSaveLeft",
        "GK_03_Miss", "GK_19_Scoop",
    };

    public static void RunCli()
    {
        try
        {
            Run();
            EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[GKClipPreview] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Football/GK Clip Preview")]
    public static void Run()
    {
        EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity");

        // 新 Character（GKCustomRig 实例，含 Animator+GKCustom_Mat）
        Animator animator = null;
        foreach (var a in UnityEngine.Object.FindObjectsOfType<Animator>(true))
            if (a.runtimeAnimatorController != null &&
                a.runtimeAnimatorController.name.Contains("GKCustomController"))
                animator = a;
        if (animator == null) throw new System.Exception("GKCustom animator not found in scene");
        var root = animator.transform;
        Debug.Log("[GKClipPreview] animator on: " + root.name + " scale=" + root.localScale +
                  " worldPos=" + root.position + " isHuman=" + animator.avatar.isHuman);

        // 诊断: 打印 controller 各 state 的 motion
        var ctrlAsset = animator.runtimeAnimatorController as UnityEditor.Animations.AnimatorController;
        if (ctrlAsset != null)
            foreach (var st in ctrlAsset.layers[0].stateMachine.states)
                Debug.Log("[GKClipPreview] STATE " + st.state.name + " motion=" +
                          (st.state.motion != null ? st.state.motion.name : "NULL"));
        string outDir = "D:/AIfootball/output/unity_preview";
        Directory.CreateDirectory(outDir);

        var clips = AssetDatabase.LoadAllAssetsAtPath(
                "Assets/SuperGoalie/Meshes/Characters/GKCustom_GK2.fbx")
            .Where(x => x is AnimationClip && !(x is GameObject))
            .Cast<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__"))
            .ToList();

        // 临时相机（看向守门员正面）
        var camGo = new GameObject("PreviewCam");
        var cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 45;
        var rt = new RenderTexture(640, 760, 24);
        cam.targetTexture = rt;
        cam.backgroundColor = new Color(0.25f, 0.32f, 0.25f);
        cam.clearFlags = CameraClearFlags.SolidColor;

        foreach (var key in ClipKeys)
        {
            var clip = clips.FirstOrDefault(c => c.name.Contains(key));
            if (clip == null) { Debug.LogWarning("[GKClipPreview] missing " + key); continue; }

            float time = clip.length * 0.55f;
            var stateName = clip.name.Contains("|") ? clip.name.Split('|')[1] : clip.name;
            animator.enabled = true;
            animator.Play(stateName, 0, 0.55f);
            animator.Update(0.02f);
            var st = animator.GetCurrentAnimatorStateInfo(0);
            int nb = AnimationUtility.GetCurveBindings(clip).Length;
            var hipsB = animator.GetBoneTransform(HumanBodyBones.Hips);
            Debug.Log("[GKClipPreview] " + key + " state=" + (st.IsName(stateName) ? stateName : st.shortNameHash.ToString()) +
                      " hipsY=" + (hipsB != null ? hipsB.position.y.ToString("F2") : "null") +
                      " curveBindings=" + nb + " len=" + clip.length.ToString("F2"));

            // 采样后刷新
            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            Vector3 focus = hips != null ? hips.position : root.position;
            // 守门员面向球场，相机从面向方向前方 3m 拍
            Vector3 forward = root.forward;
            cam.transform.position = focus + forward * 3.2f + Vector3.up * 0.5f;
            cam.transform.LookAt(focus + Vector3.up * 0.2f);
            cam.Render();

            RenderTexture.active = rt;
            var tex = new Texture2D(640, 760, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 640, 760), 0, 0);
            tex.Apply();
            File.WriteAllBytes(outDir + "/" + key + ".png", tex.EncodeToPNG());
            Debug.Log("[GKClipPreview] rendered " + key + " t=" + time.ToString("F2") +
                      " hips=" + focus);
        }
        cam.targetTexture = null;
        rt.Release();
        UnityEngine.Object.DestroyImmediate(camGo);
        Debug.Log("[GKClipPreview] OK");
    }
}
