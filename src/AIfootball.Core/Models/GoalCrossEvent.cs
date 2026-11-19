using System.Text.Json.Serialization;

namespace AIfootball.Core.Models;

/// <summary>
/// 一次"球出现在球门附近"的候选事件，由 tools/goal_detect/ball_cross.py 逐帧检测、
/// cross_events.py 聚簇得到。
///
/// 单帧检测结果不足以判断进球：门框附近任何误检都会产生候选。真正有意义的信号是
/// 一条连续轨迹——球从门框外侧（below_line_px &gt; 0）移动到门框内部（below_line_px &lt; 0）。
/// 本类型描述这样一簇事件，由人眼最终定性。
/// </summary>
public sealed class GoalCrossEvent
{
    /// <summary>所属门框检测段号。</summary>
    [JsonPropertyName("seg")] public int Seg { get; set; }

    /// <summary>事件起始时间（秒）。</summary>
    [JsonPropertyName("t_start")] public double TStart { get; set; }

    /// <summary>事件结束时间（秒）。</summary>
    [JsonPropertyName("t_end")] public double TEnd { get; set; }

    /// <summary>过线瞬间的时间；未跨线时为 null。</summary>
    [JsonPropertyName("t_cross")] public double? TCross { get; set; }

    /// <summary>本事件包含的候选帧数。帧数越多，轨迹越完整。</summary>
    [JsonPropertyName("n_frames")] public int NFrames { get; set; }

    /// <summary>本事件内最高的球检测置信度。</summary>
    [JsonPropertyName("max_conf")] public double MaxConf { get; set; }

    /// <summary>是否检测到球从门线外侧跨到内侧。</summary>
    [JsonPropertyName("crossed")] public bool Crossed { get; set; }

    /// <summary>是否至少有一帧球心落在门框四边形内部。</summary>
    [JsonPropertyName("inside_any")] public bool InsideAny { get; set; }

    /// <summary>所属门框段的起始时间（秒）。</summary>
    [JsonPropertyName("seg_t0")] public double SegT0 { get; set; }

    /// <summary>所属门框段的结束时间（秒）。</summary>
    [JsonPropertyName("seg_t1")] public double SegT1 { get; set; }

    /// <summary>供列表显示的简短描述。</summary>
    [JsonIgnore]
    public string Display
    {
        get
        {
            var flag = Crossed ? "过线" : (InsideAny ? "框内" : "邻近");
            var cross = TCross.HasValue ? $" 过线{TCross.Value:0.00}s" : string.Empty;
            return $"段{Seg} {TStart:0.0}-{TEnd:0.0}s {flag}{cross}";
        }
    }

    /// <summary>让 ListBox 直接显示可读摘要。</summary>
    public override string ToString() => Display;
}
