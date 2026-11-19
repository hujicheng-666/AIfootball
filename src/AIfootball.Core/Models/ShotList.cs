using System.Text.Json.Serialization;

namespace AIfootball.Core.Models;

/// <summary>
/// 一段合集视频的脚次表（由 <c>tools/goal_detect/shot_list.py</c> 按镜头切换扫出来）。
/// 一个 span 大致对应一脚点球；标注时用它做"下一脚"导航和断点续标，
/// 不必再靠 ±1s 按钮在三百秒里一帧帧摸。
/// </summary>
public sealed class ShotListDocument
{
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("video")] public string Video { get; set; } = "";
    [JsonPropertyName("fps")] public double Fps { get; set; }
    [JsonPropertyName("frames")] public int Frames { get; set; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("threshold")] public double Threshold { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("shots")] public List<ShotSpan> Shots { get; set; } = [];
}

/// <summary>一脚点球占用的时间区间。Key 是跳过去时落的那一秒（切点前约 1.2s）。</summary>
public sealed class ShotSpan
{
    [JsonPropertyName("index")] public int Index { get; set; }
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
    [JsonPropertyName("key")] public double Key { get; set; }
    [JsonPropertyName("frames")] public int Frames { get; set; }

    [JsonIgnore] public double Length => End - Start;

    public bool Contains(double seconds) => seconds >= Start - 0.05 && seconds <= End;
}
