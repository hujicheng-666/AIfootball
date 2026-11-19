using System.Text.Json;
using System.Text.Json.Serialization;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>cross_events.json 的顶层结构。</summary>
public sealed class GoalCrossEventDocument
{
    /// <summary>被分析的视频绝对路径；界面据此定位播放源。</summary>
    [JsonPropertyName("video")] public string? Video { get; set; }

    /// <summary>聚簇前的原始候选帧总数。</summary>
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }

    /// <summary>聚簇后的事件列表。</summary>
    [JsonPropertyName("events")] public List<GoalCrossEvent> Events { get; set; } = new();

    /// <summary>其中确认跨线的条数。</summary>
    [JsonIgnore]
    public int CrossedCount
    {
        get
        {
            var n = 0;
            foreach (var e in Events)
                if (e.Crossed) n++;
            return n;
        }
    }
}

/// <summary>
/// 读取 tools/goal_detect 产出的过线候选事件。只读：事件由 Python 侧生成，
/// 人工判定另存为 cross_verdicts.json。
/// </summary>
public static class GoalCrossEventStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>默认事件文件位置（相对工作区根）。</summary>
    public static string DefaultPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "data", "goal_detect", "cross_events.json");

    /// <summary>默认判定结果文件位置（相对工作区根）。</summary>
    public static string DefaultVerdictPath(string workspaceRoot) =>
        Path.Combine(workspaceRoot, "data", "goal_detect", "cross_verdicts.json");

    /// <summary>读取事件文件。文件不存在或内容无效时返回 null，不抛异常。</summary>
    public static GoalCrossEventDocument? Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonSerializer.Deserialize<GoalCrossEventDocument>(json, Options);
            if (doc?.Events is null) return null;
            // 按时间排序，界面上从上到下就是播放顺序
            doc.Events.Sort((a, b) => a.TStart.CompareTo(b.TStart));
            return doc;
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
}
