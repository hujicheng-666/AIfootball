using System.Text.Json;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>
/// 脚次表的磁盘缓存。脚次是视频自身的属性（跟哪个门将无关），
/// 所以按视频名缓存在 data/goal_detect/shots/&lt;视频名&gt;.json，
/// 同一段视频只扫一次，之后打开面板直接读。
/// </summary>
public static class ShotListStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string CacheDirectory(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "data", "goal_detect", "shots");

    public static string CachePath(string workspaceRoot, string videoPath) =>
        Path.Combine(CacheDirectory(workspaceRoot), Path.GetFileNameWithoutExtension(videoPath) + ".json");

    /// <summary>读缓存。没有、损坏、或 Python 报了 ok=false 都当"还没扫过"。</summary>
    public static ShotListDocument? Load(string workspaceRoot, string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return null;
        var path = CachePath(workspaceRoot, videoPath);
        if (!File.Exists(path)) return null;
        try
        {
            var document = JsonSerializer.Deserialize<ShotListDocument>(File.ReadAllText(path), Options);
            if (document is null || !document.Ok || document.Shots.Count == 0) return null;
            document.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
            return document;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>找出 t 所在的那一脚；落在空隙里返回 null。</summary>
    public static ShotSpan? FindAt(IReadOnlyList<ShotSpan> shots, double t)
    {
        foreach (var shot in shots)
        {
            if (shot.Contains(t)) return shot;
        }
        return null;
    }

    /// <summary>t 之后最近的一脚（严格大于 t + 容差）。</summary>
    public static ShotSpan? Next(IReadOnlyList<ShotSpan> shots, double t, double tolerance = 0.2)
    {
        foreach (var shot in shots)
        {
            if (shot.Key > t + tolerance) return shot;
        }
        return null;
    }

    /// <summary>t 之前最近的一脚（严格小于 t - 容差）。</summary>
    public static ShotSpan? Previous(IReadOnlyList<ShotSpan> shots, double t, double tolerance = 0.2)
    {
        ShotSpan? found = null;
        foreach (var shot in shots)
        {
            if (shot.Key < t - tolerance) found = shot;
            else break;
        }
        return found;
    }
}
