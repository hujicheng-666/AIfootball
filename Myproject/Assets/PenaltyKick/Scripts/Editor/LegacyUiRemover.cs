#if UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PenaltyKickPlatform.EditorTools
{
    /// <summary>
    /// 删除场景中旧版自建 UI（HUD 与旧玩法接收器）。
    ///
    /// 为什么保留 EventSystem
    /// ----------------------
    /// 它不是「界面」，而是 UGUI 的事件基础设施：项目运行时创建的
    /// PenaltyKickRuntimeUI / CsvTrajectoryUI 都依赖它才能响应点击。
    /// 删掉它按钮会静默失效，且不易排查。其余对象全部删除。
    ///
    /// 删除后，WpfCommandBridge.Start() 与 PenaltyKickApp.HideLegacyUI() 里
    /// 那两段「遍历 Canvas 并 SetActive(false)」会自然变成空操作，
    /// 无需改动——作为防御性逻辑保留即可。
    /// </summary>
    public static class LegacyUiRemover
    {
        /// <summary>旧版自建 UI 的对象名。子对象会随父对象一并删除。</summary>
        private static readonly string[] LegacyNames =
        {
            "Canvas", "Panel", "Score", "Label", "Instructions", "RaycastReceiver",
        };

        /// <summary>明确保留：UGUI 事件系统是基础设施，不是界面。</summary>
        private static readonly string[] KeepNames = { "EventSystem" };

        [MenuItem("AIfootball/删除旧版 UI（HUD）", priority = 30)]
        public static void RemoveLegacyUi()
        {
            Scene scene = SceneManager.GetActiveScene();
            var targets = new List<GameObject>();
            var kept = new List<string>();

            foreach (GameObject root in scene.GetRootGameObjects())
                Collect(root.transform, targets, kept);

            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("删除旧版 UI",
                    "当前场景（" + scene.name + "）中没有找到旧版 UI 对象。", "知道了");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("场景：" + scene.name);
            sb.AppendLine("将删除以下 " + targets.Count + " 个对象（含其全部子对象）：");
            foreach (GameObject go in targets)
                sb.AppendLine("   • " + GetPath(go));
            if (kept.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("保留：" + string.Join("、", kept.ToArray())
                              + "\n（UGUI 事件系统——运行时 UI 依赖它处理点击）");
            }
            sb.AppendLine();
            sb.AppendLine("可用 Ctrl+Z 撤销。");

            if (!EditorUtility.DisplayDialog("删除旧版 UI", sb.ToString(), "删除", "取消"))
                return;

            foreach (GameObject go in targets)
                Undo.DestroyObjectImmediate(go);

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("[LegacyUiRemover] 已删除 " + targets.Count
                      + " 个旧版 UI 对象。请按 Ctrl+S 保存场景。", targets.Count > 0 ? targets[0] : null);
        }

        /// <summary>递归收集；父对象已入选时不再重复收集其子对象。</summary>
        private static void Collect(Transform current, List<GameObject> targets, List<string> kept)
        {
            string name = current.name;

            if (IsKept(name))
            {
                if (!kept.Contains(name)) kept.Add(name);
                return;   // 保留对象及其子树
            }

            if (IsLegacy(name))
            {
                targets.Add(current.gameObject);
                return;   // 整棵子树随之删除
            }

            for (int i = 0; i < current.childCount; i++)
                Collect(current.GetChild(i), targets, kept);
        }

        private static bool IsLegacy(string name)
        {
            for (int i = 0; i < LegacyNames.Length; i++)
                if (string.Equals(name, LegacyNames[i], System.StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsKept(string name)
        {
            for (int i = 0; i < KeepNames.Length; i++)
                if (string.Equals(name, KeepNames[i], System.StringComparison.Ordinal)) return true;
            return false;
        }

        private static string GetPath(GameObject go)
        {
            var parts = new List<string>();
            Transform t = go.transform;
            while (t != null && t.parent != null)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts.ToArray());
        }
    }
}
#endif
