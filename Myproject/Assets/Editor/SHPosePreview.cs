#if UNITY_EDITOR
using System.IO;
using PenaltyKickPlatform.Shooter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class SHPosePreview
{
    public static void RunCli()
    {
        try
        {
            EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            Directory.CreateDirectory("D:/AIfootball/output/unity_preview");
            var driver = Object.FindObjectOfType<ShooterDriver>(true);
            if (driver == null) throw new System.Exception("no ShooterDriver");
            driver.gameObject.SetActive(true);
            var retargeter = driver.GetComponentInChildren<ShooterRetargeter>(true);
            // Edit mode 下 Awake 不执行, 手动初始化 Animator 引用并采集 rest pose
            var soR = new SerializedObject(retargeter);
            var animProp = soR.FindProperty("_animator");
            var newAnimator = System.Array.Find(
                driver.GetComponentsInChildren<Animator>(true),
                a => a.avatar != null && a.avatar.name == "SHCustomAvatar");
            animProp.objectReferenceValue = newAnimator;
            soR.ApplyModifiedProperties();
            retargeter.CaptureRestPose();
            Debug.Log("[SHPosePreview] rest captured, animator=" + newAnimator.name);

            var camGo = new GameObject("PreviewCam");
            var cam = camGo.AddComponent<Camera>();

            var clips = new (string name, string posePath, float time)[]
            {
                ("SH_rest", "ShooterPoses/SH_Jog", 0f),
                ("SH_run", "ShooterPoses/SH_Jog", 0.35f),
                ("SH_run2", "ShooterPoses/SH_Jog", 0.65f),
                ("SH_windup", "ShooterPoses/SH_PenaltyKick", 0.25f),
                ("SH_kick", "ShooterPoses/SH_PenaltyKick", 0.467f),
                ("SH_follow", "ShooterPoses/SH_PenaltyKick", 0.95f),
            };

            foreach (var (name, posePath, time) in clips)
            {
                var json = AssetDatabase.LoadAssetAtPath<TextAsset>(
                    "Assets/PenaltyKick/Resources/" + posePath + ".json");
                if (json == null) { Debug.LogError("[SHPosePreview] missing " + posePath); continue; }
                var clip = ShooterPoseClip.FromJson(json.text);
                retargeter.SetAutoUpdate(false);
                retargeter.SetClip(clip);
                retargeter.SetRestBlend(0f);
                retargeter.SetTime(time);
                retargeter.ApplyPose();

                var ar = retargeter.GetComponentInChildren<Animator>(true);
                var hp = (ar != null ? ar.transform.position : driver.transform.position) + Vector3.up * 0.9f;
                var loc = hp + new Vector3(2.0f, 0.7f, -2.4f);
                cam.transform.position = loc;
                cam.transform.LookAt(hp);
                cam.fieldOfView = 42f;
                var rt = new RenderTexture(560, 720, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(560, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 560, 720), 0, 0);
                tex.Apply();
                cam.targetTexture = null;
                RenderTexture.active = null;
                rt.Release();
                File.WriteAllBytes("D:/AIfootball/output/unity_preview/" + name + ".png", tex.EncodeToPNG());
                Debug.Log("[SHPosePreview] rendered " + name);
            }
            Debug.Log("[SHPosePreview] OK");
            EditorApplication.Exit(0);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[SHPosePreview] FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }
}
#endif
