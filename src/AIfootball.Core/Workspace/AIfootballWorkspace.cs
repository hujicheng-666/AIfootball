using System.Diagnostics.CodeAnalysis;

namespace AIfootball.Core.Workspace;

/// <summary>
/// 工作区路径上下文 —— 全应用唯一的"磁盘布局真相"。
///
/// 旧实现把路径散落在各个服务里（services 各自 Path.Combine(baseDir, "samples")），
/// 并且要求调用方必须把进程工作目录设为项目根，否则 Python 侧会算错路径。
/// 这里把布局集中描述一次，并显式传给 Python 引擎（--workspace / AIFOOTBALL_WORKSPACE），
/// 从此不再依赖进程 cwd。
/// </summary>
public sealed class AifootballWorkspace
{
    /// <summary>工作区根目录（绝对路径）。</summary>
    public string Root { get; }

    private AifootballWorkspace(string root) => Root = root;

    public string CalibrationDir => Combine("calib");
    public string SamplesDir => Combine("samples");
    public string OutputDir => Combine("output");
    public string Trajectory3DDir => Combine("output", "trajectory_3d");
    public string BallisticDir => Combine("output", "trajectory_ballistic");
    public string TrackCacheDir => Combine("output", "track_cache");
    public string LivePreviewDir => Combine("output", "live_preview");
    public string DataDir => Combine("data");
    public string GoalkeepersDir => Combine("data", "goalkeepers");
    public string ModelsDir => Combine("models");
    public string RuntimeDir => Combine("runtime");
    public string UnityCommandFile => Combine("runtime", "data", "wpf-unity-command.txt");
    public string PythonEnvDir => Combine("python_env");
    public string PythonExe => Combine("python_env", "python.exe");
    /// <summary>
    /// Python 引擎所在目录（内含 <c>aifootball</c> 包，作为 PYTHONPATH 传给解释器）。
    /// 开发态在 <c>src\AIfootball.Engine</c>；构建/发布输出里则由 csproj 复制到
    /// <c>python_engine\</c>（也接受手工摆到 <c>engine\</c>）。三个位置依次探测。
    /// </summary>
    public string EngineDir
    {
        get
        {
            string[] candidates = [Combine("src", "AIfootball.Engine"), Combine("python_engine"), Combine("engine")];
            foreach (var candidate in candidates)
            {
                if (Directory.Exists(Path.Combine(candidate, "aifootball"))) return candidate;
            }
            return candidates[0];
        }
    }

    public string ModelFile => Path.Combine(ModelsDir, "yolo11m.pt");

    public string SampleDir(string sampleName) => Path.Combine(SamplesDir, sampleName);
    public string UnityCsv(string sampleName) => Path.Combine(DataDir, $"{sampleName}_trajectory.csv");
    public string UnityMeta(string sampleName) => Path.Combine(DataDir, $"{sampleName}_trajectory.meta.json");

    public string Combine(params string[] parts) => Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>环境变量：让子进程无需命令行参数也能定位工作区。</summary>
    public const string WorkspaceEnvironmentVariable = "AIFOOTBALL_WORKSPACE";

    /// <summary>
    /// 解析工作区：显式参数 → 环境变量 → 从锚点目录向上查找布局特征 → 当前目录。
    /// </summary>
    public static AifootballWorkspace Resolve(string? explicitRoot = null, string? anchorDirectory = null)
    {
        foreach (var candidate in EnumerateCandidates(explicitRoot, anchorDirectory))
        {
            if (candidate is null) continue;
            var full = Path.GetFullPath(candidate);
            if (LooksLikeWorkspace(full)) return new AifootballWorkspace(full);
        }

        // 兜底：即使目录还没建全，也不让应用启动失败。
        var fallback = explicitRoot is { Length: > 0 }
            ? Path.GetFullPath(explicitRoot)
            : Directory.GetCurrentDirectory();
        return new AifootballWorkspace(fallback);
    }

    private static IEnumerable<string?> EnumerateCandidates(string? explicitRoot, string? anchorDirectory)
    {
        yield return explicitRoot;

        var fromEnvironment = Environment.GetEnvironmentVariable(WorkspaceEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) yield return fromEnvironment;

        var anchor = anchorDirectory ?? AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(anchor); dir is not null; dir = dir.Parent)
        {
            yield return dir.FullName;
        }

        yield return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// 布局特征：有 Python 引擎包 + 至少一份运行期数据，才算项目根/发布根。
    /// 开发态认 <c>src\AIfootball.Engine</c>，发布包认 <c>python_engine\</c> 或 <c>engine\</c>。
    /// </summary>
    public static bool LooksLikeWorkspace(string directory)
    {
        var hasEngine =
            Directory.Exists(Path.Combine(directory, "src", "AIfootball.Engine", "aifootball")) ||
            Directory.Exists(Path.Combine(directory, "python_engine", "aifootball")) ||
            Directory.Exists(Path.Combine(directory, "engine", "aifootball"));
        if (!hasEngine) return false;

        return Directory.Exists(Path.Combine(directory, "python_env")) ||
               Directory.Exists(Path.Combine(directory, "samples")) ||
               Directory.Exists(Path.Combine(directory, "calib")) ||
               Directory.Exists(Path.Combine(directory, "models"));
    }

    /// <summary>创建运行期需要的目录（幂等）。</summary>
    public void EnsureRuntimeDirectories()
    {
        foreach (var dir in new[] { OutputDir, LivePreviewDir, DataDir, GoalkeepersDir })
        {
            try { Directory.CreateDirectory(dir); }
            catch (IOException) { /* 目录不可写时不影响启动 */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [return: NotNullIfNotNull(nameof(path))]
    public static string? Display(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        try
        {
            var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }
}
