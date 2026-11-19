using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PenaltyKickPlatform.Hunyuan.EditorTools
{
    public static class SceneDiag
    {
        [MenuItem("AIfootball/混元/诊断场景")]
        public static void Run()
        {
            var sb = new StringBuilder("[SceneDiag] 开始\n");
            var cur = SceneManager.GetActiveScene();
            sb.AppendLine("  当前场景=" + cur.path + " rootCount=" + cur.rootCount + " loaded=" + cur.isLoaded);

            var scene = EditorSceneManager.OpenScene("Assets/SuperGoalie/Scenes/Demo.unity", OpenSceneMode.Single);
            sb.AppendLine("  OpenScene 后 path=" + scene.path + " rootCount=" + scene.rootCount + " loaded=" + scene.isLoaded + " valid=" + scene.IsValid());

            var roots = scene.GetRootGameObjects();
            sb.AppendLine("  GetRootGameObjects 数量=" + roots.Length);
            foreach (var r in roots) Dump(r.transform, 0, sb);

            // 再试一次全局查找
            foreach (var n in new[] { "GoalKeeper002", "GoalKeeper001", "Shooter", "Main Camera", "Ball" })
            {
                var go = GameObject.Find(n);
                sb.AppendLine("  Find(\"" + n + "\") = " + (go == null ? "null" : go.name + " @" + go.transform.position));
            }

            var all = Object.FindObjectsOfType<Transform>(true);
            sb.AppendLine("  全场景 Transform 数量=" + all.Length);
            foreach (var t in all)
                if (t.parent == null) sb.AppendLine("    根节点: " + t.name);

            Debug.Log(sb.ToString());
        }

        private static void Dump(Transform t, int d, StringBuilder sb)
        {
            if (d > 5) return;
            sb.Append(' ', d * 2 + 4).Append("- ").Append(t.name).Append(" active=").Append(t.gameObject.activeSelf);
            var a = t.GetComponent<Animator>();
            if (a != null) sb.Append(" [Animator avatar=").Append(a.avatar != null ? a.avatar.name : "null")
                              .Append(" ctrl=").Append(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "null").Append(']');
            foreach (var c in t.GetComponents<Component>())
                if (c != null && !(c is Transform)) sb.Append(' ').Append(c.GetType().Name);
            sb.AppendLine();
            foreach (Transform ch in t) Dump(ch, d + 1, sb);
        }
    }
}
