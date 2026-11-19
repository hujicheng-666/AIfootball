using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// 命令行构建 Demo 验证场景的 Windows Player。
/// 用法（batchmode）：
///   Unity.exe -quit -batchmode -projectPath D:/AIfootball/Myproject
///             -executeMethod BuildDemoPlayer.Build -logFile build_demo.log
/// 产物输出到 D:/AIfootball/_build_demo/Myproject.exe，随后由外部脚本覆盖 runtime/。
/// </summary>
public static class BuildDemoPlayer
{
    private const string ScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";
    private const string OutputExe = @"D:/AIfootball/_build_demo/Myproject.exe";

    public static void Build()
    {
        // 先显式打开并保存场景，确保磁盘上的接线是最新的
        if (!File.Exists(Path.GetFullPath(ScenePath)))
        {
            Debug.LogError("场景不存在: " + ScenePath);
            EditorApplication.Exit(2);
            return;
        }
        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        EditorSceneManager.SaveOpenScenes();

        if (Directory.Exists(@"D:/AIfootball/_build_demo"))
            Directory.Delete(@"D:/AIfootball/_build_demo", true);

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = OutputExe,
            target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError("构建失败: result=" + report.summary.result
                + " errors=" + report.summary.totalErrors);
            EditorApplication.Exit(2);
            return;
        }

        Debug.Log("构建完成: " + report.summary.outputPath
            + " 大小=" + (report.summary.totalSize / 1024 / 1024) + "MB");
        EditorApplication.Exit(0);
    }
}
