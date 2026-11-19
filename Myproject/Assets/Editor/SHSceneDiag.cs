using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 诊断 Demo 场景里射手/守门员多套模型的驱动关系：
/// ShooterDriver 挂在哪、ShooterRetargeter._animator 指向谁、骨骼是否找得到。
/// batchmode 运行后把结果写到 diag_shooter.txt 并退出。
/// </summary>
public static class SHSceneDiag
{
    public static void Run()
    {
        var sb = new StringBuilder();
        var scene = EditorSceneManager.OpenScene(
            "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);

        sb.AppendLine("=== 场景中所有 Renderer/Animator 相关对象树（含禁用） ===");
        foreach (var root in scene.GetRootGameObjects())
            DumpTree(root.transform, 0, sb);

        sb.AppendLine();
        sb.AppendLine("=== ShooterDriver ===");
        foreach (var driver in Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (driver == null || driver.GetType().Name != "ShooterDriver") continue;
            var so = new SerializedObject(driver);
            var ret = so.FindProperty("_retargeter").objectReferenceValue;
            sb.AppendLine($"Driver on '{GetPath(driver.transform)}' active={driver.gameObject.activeInHierarchy}");
            sb.AppendLine($"  _retargeter={(ret == null ? "NULL" : GetPath(((Component)ret).transform))}");
        }

        sb.AppendLine();
        sb.AppendLine("=== ShooterRetargeter ===");
        foreach (var ret in Object.FindObjectsOfType<MonoBehaviour>(true))
        {
            if (ret == null || ret.GetType().Name != "ShooterRetargeter") continue;
            var so = new SerializedObject(ret);
            var ani = so.FindProperty("_animator").objectReferenceValue as Animator;
            sb.AppendLine($"Retargeter on '{GetPath(ret.transform)}' active={ret.gameObject.activeInHierarchy}");
            sb.AppendLine($"  _animator={(ani == null ? "NULL" : GetPath(ani.transform) + " (enabled=" + ani.enabled + ", active=" + ani.gameObject.activeInHierarchy + ")")}");

            // 运行反射调用 CaptureRestPose 后统计骨骼可用性（不保存场景）
            var mi = ret.GetType().GetMethod("CaptureRestPose");
            if (mi != null) mi.Invoke(ret, null);
            var bonesField = ret.GetType().GetField("_bones", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var bones = bonesField?.GetValue(ret) as Transform[];
            int missing = 0;
            if (bones != null) foreach (var b in bones) if (b == null) missing++;
            sb.AppendLine($"  骨骼链缺失数: {missing}/{(bones == null ? 0 : bones.Length)}");
        }

        System.IO.File.WriteAllText(@"D:\AIfootball\diag_shooter.txt", sb.ToString());
        EditorApplication.Exit(0);
    }

    private static void DumpTree(Transform t, int depth, StringBuilder sb)
    {
        bool interesting = false;
        foreach (var c in t.GetComponentsInChildren<Transform>(true))
        {
            string n = c.name.ToLowerInvariant();
            if (n.Contains("shooter") || n.Contains("shcustom") || n.Contains("goalkeeper")
                || n.Contains("gkcustom") || n.Contains("keeper") || n.Contains("animator"))
            { interesting = true; break; }
        }
        if (!interesting && depth > 0) return;

        var comps = t.GetComponents<Component>();
        var names = new StringBuilder();
        foreach (var c in comps)
        {
            if (c == null) { names.Append("[Missing] "); continue; }
            string cn = c.GetType().Name;
            if (cn == "Transform" || cn == "MeshFilter") continue;
            names.Append(cn).Append(' ');
        }
        var ani = t.GetComponent<Animator>();
        string aniInfo = "";
        if (ani != null)
        {
            var av = ani.avatar;
            aniInfo = " avatar=" + (av == null ? "NULL" : av.name);
        }
        sb.AppendLine($"{new string(' ', depth * 2)}{t.name} active={t.gameObject.activeSelf} [{names.ToString().TrimEnd()}]{aniInfo}");
        foreach (Transform child in t)
            DumpTree(child, depth + 1, sb);
    }

    private static string GetPath(Transform t)
    {
        var parts = new System.Collections.Generic.List<string>();
        while (t != null) { parts.Insert(0, t.name); t = t.parent; }
        return string.Join("/", parts);
    }
}
