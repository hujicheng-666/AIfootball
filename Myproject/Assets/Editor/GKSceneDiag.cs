using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>诊断 GK 场景实例：网格引用、骨骼链、层级路径，判断为何不渲染。</summary>
public static class GKSceneDiag
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
                bool inGk = false;
                for (var p = t; p != null; p = p.parent)
                    if (p.name.StartsWith("GKCustom") || p.name.Contains("GoalKeeper")) { inGk = true; break; }
                if (!inGk) continue;

                var path = new System.Collections.Generic.List<string>();
                bool hierActive = true;
                for (var p = t; p != null; p = p.parent)
                {
                    path.Insert(0, p.name);
                    if (!p.gameObject.activeSelf) hierActive = false;
                }
                string prefix = hierActive ? "★" : " ";

                var smr = t.GetComponent<SkinnedMeshRenderer>();
                var ani = t.GetComponent<Animator>();
                string extra = "";
                if (smr != null)
                {
                    extra = " [SMR bones=" + (smr.bones == null ? -1 : smr.bones.Length)
                        + " mesh=" + (smr.sharedMesh == null ? "NULL" : smr.sharedMesh.name)
                        + " rootBone=" + (smr.rootBone == null ? "NULL" : smr.rootBone.name)
                        + " enabled=" + smr.enabled + " bounds=" + smr.localBounds.center + "]";
                }
                if (ani != null)
                {
                    extra += " [Animator ctrl=" + (ani.runtimeAnimatorController == null ? "NULL" : ani.runtimeAnimatorController.name)
                        + " avatar=" + (ani.avatar == null ? "null" : ani.avatar.name)
                        + " cullingMode=" + ani.cullingMode + "]";
                }
                var comps = t.GetComponents<Component>();
                int missing = 0;
                foreach (var c in comps) if (c == null) missing++;
                if (missing > 0) extra += " [!!缺失组件 " + missing + "]";

                log.AppendLine(prefix + string.Join("/", path) + extra);
            }
        }
        catch (System.Exception e) { log.AppendLine("!! 异常: " + e); }
        finally
        {
            System.IO.File.WriteAllText(@"D:\AIfootball\gk_scene_diag.txt", log.ToString());
            EditorApplication.Exit(0);
        }
    }
}
