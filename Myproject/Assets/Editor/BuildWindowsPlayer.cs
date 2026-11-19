using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class SceneCompatibilityRepair
{
    public const string DemoScenePath = "Assets/SuperGoalie/Scenes/Demo.unity";

    const string SessionRepairKey = "FootballPlatform.DemoSceneRepaired";
    static bool _waitingForEditMode;

    static SceneCompatibilityRepair()
    {
        EditorApplication.delayCall += RepairAfterReload;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
    }

    static void RepairAfterReload()
    {
        if (SessionState.GetBool(SessionRepairKey, false))
            return;

        if (EditorApplication.isPlayingOrWillChangePlaymode)
        {
            _waitingForEditMode = true;
            return;
        }

        RepairAndOpenDemo();
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredEditMode || !_waitingForEditMode)
            return;

        _waitingForEditMode = false;
        EditorApplication.delayCall += RepairAfterReload;
    }

    [MenuItem("Tools/Football Platform/Repair and Open Demo Scene")]
    public static void RepairAndOpenDemo()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Exit Play Mode before repairing the Demo scene.");

        SessionState.SetBool(SessionRepairKey, true);
        ForceImportDemoScene();

        Scene scene = EditorSceneManager.OpenScene(DemoScenePath, OpenSceneMode.Single);
        EditorSceneManager.MarkSceneDirty(scene);
        if (!EditorSceneManager.SaveScene(scene, DemoScenePath))
            throw new InvalidOperationException("Unity could not save the repaired Demo scene.");

        ForceImportDemoScene();
        Debug.Log("Demo scene repaired, saved, and reimported: " + DemoScenePath);
    }

    public static void ForceImportDemoScene()
    {
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(
            DemoScenePath,
            ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
    }
}

public sealed class FootballSceneBuildPreprocessor : IPreprocessBuildWithReport
{
    public int callbackOrder { get { return -1000; } }

    public void OnPreprocessBuild(BuildReport report)
    {
        SceneCompatibilityRepair.ForceImportDemoScene();
    }
}

public static class BuildWindowsPlayer
{
    [MenuItem("Build/Build Windows EXE")]
    public static void Build()
    {
        BuildInternal(false);
    }

    [MenuItem("Build/Repair, Build and Run Windows EXE")]
    public static void BuildAndRun()
    {
        BuildInternal(true);
    }

    static void BuildInternal(bool runAfterBuild)
    {
        SceneCompatibilityRepair.RepairAndOpenDemo();

        // 直接构建到 AIfootball 仓库的 runtime/（桌面程序与发布脚本均从
        // runtime/Myproject.exe + Myproject_Data 加载），免去二次复制部署。
        string projectRoot = Directory.GetParent(Application.dataPath).FullName;   // ...\Myproject
        string repoRoot = Directory.GetParent(projectRoot).FullName;               // ...\AIfootball
        string outputDirectory = Path.Combine(repoRoot, "runtime");
        string executablePath = Path.Combine(outputDirectory, "Myproject.exe");
        Directory.CreateDirectory(outputDirectory);

        string[] scenes = EditorBuildSettings.scenes
            .Where(scene => scene.enabled)
            .Select(scene => scene.path)
            .ToArray();
        if (scenes.Length == 0)
            scenes = new[] { SceneCompatibilityRepair.DemoScenePath };

        BuildPlayerOptions options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = executablePath,
            target = BuildTarget.StandaloneWindows64,
            options = runAfterBuild ? BuildOptions.AutoRunPlayer : BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException("Windows build failed. Check the first error in the Unity Console and Build Report.");

        Debug.Log("Windows executable generated: " + executablePath);
    }
}
