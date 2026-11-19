using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 把守门员的 AnimatorController 换回最初那版 GoalKeeperAnimator001（原版扑救动作集：
/// PunchLow/Centre、PunchHigh、SideStep、IdleAlert 等 10 个 Humanoid 动作 + 程序化趋近位移）。
///
/// 这些 fbx 的 animationType = 3(Humanoid)，可跨骨架重定向到 GKCustom 的自动 avatar，
/// 因此「原版动作 + 新模型」可以同时成立。GKHumanController 那份把根状态 motion 清空了，
/// 运行时只能停在 bind pose（A-pose），所以必须换回来。
/// </summary>
public static class GKUseOriginalAnimator
{
    const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";
    const string Ctrl = "Assets/SuperGoalie/Animators/GoalKeeperAnimator001.controller";

    public static void Run()
    {
        var log = new StringBuilder();
        var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        var ctrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(Ctrl);
        if (ctrl == null) { log.AppendLine("!! 找不到 " + Ctrl); Flush(log); return; }

        var gk = GameObject.Find("GoalKeeper002");
        if (gk == null) { log.AppendLine("!! 找不到 GoalKeeper002"); Flush(log); return; }

        int n = 0;
        foreach (var ani in gk.GetComponentsInChildren<Animator>(true))
        {
            log.AppendLine(string.Format("{0}: 旧 ctrl={1} avatar={2}",
                ani.name,
                ani.runtimeAnimatorController == null ? "NULL" : ani.runtimeAnimatorController.name,
                ani.avatar == null ? "NULL" : ani.avatar.name));

            ani.runtimeAnimatorController = ctrl;
            ani.applyRootMotion = false;             // 位移由 GoalKeeper/RPGMovement 脚本负责
            ani.cullingMode = AnimatorCullingMode.AlwaysAnimate;  // 扑救时会飞出视锥，别被剔除
            EditorUtility.SetDirty(ani);
            n++;
        }

        log.AppendLine("已切换 Animator 数量: " + n);
        if (n == 0) log.AppendLine("!! 没有 Animator 被切换");

        EditorSceneManager.MarkSceneDirty(scene);
        log.AppendLine("SaveScene: " + EditorSceneManager.SaveScene(scene, ScenePath));

        // 回读
        var s2 = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        var g2 = GameObject.Find("GoalKeeper002");
        foreach (var a in g2.GetComponentsInChildren<Animator>(true))
            log.AppendLine("回读: " + a.name + " ctrl=" +
                (a.runtimeAnimatorController == null ? "NULL" : a.runtimeAnimatorController.name) +
                " avatar=" + (a.avatar == null ? "NULL" : a.avatar.name));

        Flush(log);
    }

    static void Flush(StringBuilder log)
    {
        System.IO.File.WriteAllText(@"D:\AIfootball\gk_ctrl_swap.txt", log.ToString());
        EditorApplication.Exit(0);
    }
}
