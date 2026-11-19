using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>列出 Shooter 层级所有组件，识别每帧覆盖骨骼的组件（IK/Animator 等）。</summary>
public static class SHComponentDiag
{
    public static void Run()
    {
        var log = new System.Text.StringBuilder();
        try
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);

            foreach (var t in Object.FindObjectsOfType<Transform>(true))
            {
                bool inShooter = false;
                for (var p = t; p != null; p = p.parent)
                {
                    if (p.name == "Shooter") { inShooter = true; break; }
                }
                if (!inShooter) continue;

                // 完整路径 + 层级有效激活状态
                var path = new System.Collections.Generic.List<string>();
                bool hierActive = true;
                for (var p = t; p != null; p = p.parent)
                {
                    path.Insert(0, p.name);
                    if (!p.gameObject.activeSelf) hierActive = false;
                    if (p.name == "Shooter") break;
                }
                var comps = t.GetComponents<Component>();
                var names = new System.Collections.Generic.List<string>();
                foreach (var c in comps)
                {
                    if (c == null) { names.Add("<missing>"); continue; }
                    var bh = c as Behaviour;
                    names.Add(c.GetType().Name + (bh == null || bh.enabled ? "" : " [disabled]"));
                }
                bool renders = t.GetComponent<SkinnedMeshRenderer>() != null;
                log.AppendLine((hierActive ? "★" : " ") + (renders ? "[网格]" : "") + string.Join("/", path)
                    + " : " + string.Join(", ", names));
            }
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\sh_components.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
