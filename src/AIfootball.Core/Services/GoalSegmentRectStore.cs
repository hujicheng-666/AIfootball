using System.Text.Json;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>读取可信门框段，供播放时叠加门框、自动建立落点映射。</summary>
public static class GoalSegmentRectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>默认文件位置（相对工作区根）。</summary>
    public static string DefaultPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "data", "goal_detect", "trusted_segments.json");

    /// <summary>读取全部段。文件缺失或无效时返回空列表，不抛异常。</summary>
    public static List<GoalSegmentRect> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new List<GoalSegmentRect>();
        try
        {
            var json = File.ReadAllText(path);
            var segs = JsonSerializer.Deserialize<List<GoalSegmentRect>>(json, Options);
            if (segs is null) return new List<GoalSegmentRect>();
            segs.Sort((a, b) => a.T0.CompareTo(b.T0));
            return segs;
        }
        catch (JsonException)
        {
            return new List<GoalSegmentRect>();
        }
        catch (IOException)
        {
            return new List<GoalSegmentRect>();
        }
    }

    /// <summary>
    /// 找出时间 t 所在的可信段。优先取区间内命中；t 落在段间空隙时，
    /// 若与相邻段距离在 tolerance 秒内则沿用，避免拖动时门框闪烁。
    /// </summary>
    public static GoalSegmentRect? FindAt(IReadOnlyList<GoalSegmentRect> segs, double t,
                                          double tolerance = 0.35)
    {
        if (segs.Count == 0) return null;

        GoalSegmentRect? best = null;
        var bestGap = double.MaxValue;
        foreach (var s in segs)
        {
            if (!s.Trusted || s.Median is null) continue;
            if (s.Contains(t)) return s;
            var gap = t < s.T0 ? s.T0 - t : t - s.T1;
            if (gap < bestGap)
            {
                bestGap = gap;
                best = s;
            }
        }
        return bestGap <= tolerance ? best : null;
    }
}
