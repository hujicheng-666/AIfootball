// 把混元射手(Mixamo 骨架)接入 Demo.unity 的 Shooter 节点
using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

public static class ShooterSceneInstall
{
    const string FbxPath = "Assets/SuperGoalie/Meshes/Characters/ShooterMixamo/shooter_mixamo_rig_150k.fbx";
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    [MenuItem("Tools/安装射手到场景")]
    public static void Run()
    {
        var L = new StringBuilder();
        Action<string> P = s => { L.AppendLine("[IN] " + s); Debug.Log("[IN] " + s); };
        try
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

            var driver = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()
                .FirstOrDefault(m => m != null && m.GetType().Name == "ShooterDriver");
            if (driver == null) { P("ERROR 未找到 ShooterDriver"); Flush(L); return; }

            Transform shooterRoot = driver.transform;
            P("Shooter 根: " + shooterRoot.name + "  pos=" + shooterRoot.position.ToString("F3"));

            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(FbxPath);
            var clip = AssetDatabase.LoadAllAssetsAtPath(FbxPath).OfType<AnimationClip>()
                .Where(c => !c.name.StartsWith("__preview__")).OrderByDescending(c => c.length).First();
            if (fbx == null || clip == null) { P("ERROR FBX/clip 缺失"); Flush(L); return; }
            P("clip: " + clip.name + "  " + clip.length.ToString("F3") + "s");

            // 1) 禁用旧的显示模型
            var oldChar = shooterRoot.Find("Character");
            if (oldChar != null && oldChar.gameObject.activeSelf)
            {
                oldChar.gameObject.SetActive(false);
                P("已禁用旧模型: Shooter/Character");
            }

            // 2) 清掉上一次安装的实例，避免重复
            var existed = shooterRoot.Find("ShooterMixamo");
            if (existed != null)
            {
                UnityEngine.Object.DestroyImmediate(existed.gameObject);
                P("移除旧安装实例 ShooterMixamo");
            }

            // 3) 实例化新模型
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(fbx, scene);
            inst.name = "ShooterMixamo";
            inst.transform.SetParent(shooterRoot, false);
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;
            inst.transform.localScale = Vector3.one;
            P("已实例化: " + inst.name);

            var animator = inst.GetComponent<Animator>();
            if (animator == null) animator = inst.GetComponentInChildren<Animator>();
            P("新模型 Animator: " + (animator != null ? animator.name + " avatar=" + (animator.avatar != null ? animator.avatar.name : "null") : "null"));

            // 4) 关闭旧的 JSON 重定向器（保留组件，便于一键回退）
            var retargeter = shooterRoot.GetComponents<MonoBehaviour>()
                .FirstOrDefault(m => m != null && m.GetType().Name == "ShooterRetargeter");
            if (retargeter != null) { retargeter.enabled = false; P("已禁用 ShooterRetargeter（保留以便回退）"); }

            // 5) 写 driver 字段
            var so = new SerializedObject(driver);
            SetObj(so, "_animator", animator, P);
            SetObj(so, "_animClip", clip, P);
            SetBool(so, "_recolorKit", false, P);      // 染色会盖掉贴图，必须关
            SetFloat(so, "_clipKickTime", 0.767f, P);
            SetFloat(so, "_clipStandOffset", 0.36f, P);
            SetFloat(so, "_rootYOffset", 0f, P);
            so.ApplyModifiedPropertiesWithoutUndo();
            P("driver 字段已写入");

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            P("场景已保存");
            P("INSTALL_DONE");
        }
        catch (Exception e) { P("EXCEPTION: " + e); }
        finally { Flush(L); }
    }

    static void SetObj(SerializedObject so, string name, UnityEngine.Object v, Action<string> P)
    {
        var p = so.FindProperty(name);
        if (p == null) { P("  字段不存在: " + name); return; }
        p.objectReferenceValue = v;
        P("  " + name + " <- " + (v != null ? v.name : "null"));
    }

    static void SetBool(SerializedObject so, string name, bool v, Action<string> P)
    {
        var p = so.FindProperty(name);
        if (p == null) { P("  字段不存在: " + name); return; }
        p.boolValue = v; P("  " + name + " = " + v);
    }

    static void SetFloat(SerializedObject so, string name, float v, Action<string> P)
    {
        var p = so.FindProperty(name);
        if (p == null) { P("  字段不存在: " + name); return; }
        p.floatValue = v; P("  " + name + " = " + v);
    }

    static void Flush(StringBuilder L)
    {
        try
        {
            var outDir = Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath));
            File.WriteAllText(Path.Combine(outDir, ".tmp_unity_install.txt"), L.ToString());
        }
        catch { }
    }
}
