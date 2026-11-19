using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

/// <summary>
/// 样本目录扫描。约定：<c>samples/&lt;name&gt;/{left,right}/*.mp4</c>，每侧恰好一个可用视频。
/// 扫描全部在后台线程执行，UI 只接收不可变结果。
/// </summary>
public sealed class SampleCatalog(AifootballWorkspace workspace)
{
    private readonly AifootballWorkspace _workspace = workspace;

    public IReadOnlyList<Sample> Scan()
    {
        var root = _workspace.SamplesDir;
        if (!Directory.Exists(root)) return [];

        var samples = new List<Sample>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var sample = Read(directory);
            if (sample is not null) samples.Add(sample);
        }
        return [.. samples.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public Sample? Read(string directory)
    {
        var name = Path.GetFileName(directory);
        if (string.IsNullOrEmpty(name)) return null;

        var left = SingleVideo(Path.Combine(directory, "left"));
        var right = SingleVideo(Path.Combine(directory, "right"));
        if (left is null && right is null) return null;

        var reconstructed = File.Exists(Path.Combine(_workspace.Trajectory3DDir, name, "trajectory_3d_points.csv"));
        var ballistic = File.Exists(Path.Combine(_workspace.BallisticDir, name, "ballistic_fit_curve.csv"));
        var deliverable = File.Exists(_workspace.UnityCsv(name));

        DateTimeOffset? lastWrite = null;
        try { lastWrite = Directory.GetLastWriteTimeUtc(directory); }
        catch (IOException) { }

        return new Sample(
            name,
            directory,
            left?.FullName,
            right?.FullName,
            left?.Length ?? 0,
            right?.Length ?? 0,
            reconstructed,
            ballistic,
            deliverable,
            lastWrite);
    }

    /// <summary>每侧只接受最新写入的那个 mp4；坏文件（&lt;1KB）视为不存在。</summary>
    private static FileInfo? SingleVideo(string cameraDirectory)
    {
        if (!Directory.Exists(cameraDirectory)) return null;
        return new DirectoryInfo(cameraDirectory).EnumerateFiles("*.mp4", SearchOption.TopDirectoryOnly)
            .Where(f => f.Length >= Sample.MinimumUsableVideoBytes)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();
    }

    public IReadOnlyList<GoalkeeperProfile> ScanGoalkeepers()
    {
        var directory = _workspace.GoalkeepersDir;
        if (!Directory.Exists(directory)) return [];

        var profiles = new List<GoalkeeperProfile>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            var profile = GoalkeeperReader.Read(file);
            if (profile is not null) profiles.Add(profile);
        }
        return [.. profiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>已导出可回放轨迹的样本名（结果页/交付页使用）。</summary>
    public IReadOnlyList<string> ScanDeliverables()
    {
        var directory = _workspace.DataDir;
        if (!Directory.Exists(directory)) return [];
        return [.. Directory.EnumerateFiles(directory, "*_trajectory.csv")
            .Select(f => Path.GetFileName(f).Replace("_trajectory.csv", "", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>实时预览帧路径（引擎每 50ms 覆写一次）。</summary>
    public string PreviewFramePath(string side) => Path.Combine(_workspace.LivePreviewDir, $"live_preview_{side}.jpg");
}
