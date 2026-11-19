using System.Text.Json.Serialization;

namespace AIfootball.Core.Models;

/// <summary>
/// 门将模型。<see cref="ProbabilityMap"/> 是 3×5 的区域扑救概率
/// （列 = 左/中左/中/中右/右，行 = 低/中/高），与 Unity 侧 <c>GoalkeeperData</c> 一致。
/// </summary>
public sealed class GoalkeeperProfile
{
    public string Name { get; init; } = "";

    /// <summary>来源文件路径，只在程序内部使用，不写进 Unity 契约文件。</summary>
    [JsonIgnore] public string FilePath { get; init; } = "";

    [JsonPropertyName("DisplayName")] public string DisplayName { get; set; } = "";
    [JsonPropertyName("Description")] public string Description { get; set; } = "";
    [JsonPropertyName("Height")] public float Height { get; set; } = 1.90f;
    [JsonPropertyName("DiveSpeed")] public float DiveSpeed { get; set; } = 4.0f;
    [JsonPropertyName("JumpDistance")] public float JumpDistance { get; set; } = 1.0f;
    [JsonPropertyName("JumpHeight")] public float JumpHeight { get; set; } = 0.5f;
    [JsonPropertyName("Reach")] public float Reach { get; set; } = 0.5f;
    [JsonPropertyName("TendGoalSpeed")] public float TendGoalSpeed { get; set; } = 3.0f;
    [JsonPropertyName("TendGoalDistance")] public float TendGoalDistance { get; set; } = 3.0f;
    [JsonPropertyName("GoalKeeping")] public float GoalKeeping { get; set; } = 0.85f;
    [JsonPropertyName("SidePreference")] public float SidePreference { get; set; }
    [JsonPropertyName("HeightPreference")] public float HeightPreference { get; set; }

    [JsonPropertyName("ProbabilityMap")]
    public Dictionary<string, float> ProbabilityMap { get; set; } = new();

    /// <summary>展示名优先用 JSON 里的 DisplayName，缺失时退回文件名。</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;

    /// <summary>区域键顺序，供雷达图/热区图稳定渲染。</summary>
    public static readonly string[] ZoneKeys =
    [
        "LowLeft", "LowMidLeft", "LowCenter", "LowMidRight", "LowRight",
        "MidLeft", "MidMidLeft", "MidCenter", "MidMidRight", "MidRight",
        "HighLeft", "HighMidLeft", "HighCenter", "HighMidRight", "HighRight",
    ];

    public float ZoneProbability(string key) => ProbabilityMap.TryGetValue(key, out var value) ? value : 0f;

    /// <summary>综合能力（0-1），用于列表排序与雷达中心值。</summary>
    public float OverallRating =>
        Math.Clamp(0.55f * GoalKeeping + 0.25f * Normalize(DiveSpeed, 3f, 6.5f) + 0.20f * Normalize(Reach, 0.4f, 1.2f), 0f, 1f);

    /// <summary>雷达图六轴：把模型字段映射到 0-1 的可比刻度。</summary>
    public IReadOnlyList<(string Axis, float Value)> RadarAxes()
    {
        return
        [
            ("身高", Normalize(Height, 1.75f, 2.05f)),
            ("下地", Normalize(DiveSpeed, 3.0f, 6.5f)),
            ("覆盖", Normalize(JumpDistance, 0.8f, 2.2f)),
            ("弹跳", Normalize(JumpHeight, 0.3f, 1.0f)),
            ("臂展", Normalize(Reach, 0.4f, 1.2f)),
            ("门线", Normalize(TendGoalSpeed, 2.0f, 5.5f)),
        ];
    }

    /// <summary>左/右两侧平均扑救率，用于横向偏好可视化。</summary>
    public (float Left, float Center, float Right) SideAverages()
    {
        var left = AverageZones("Left");
        var right = AverageZones("Right");
        var center = AverageZones("Center");
        return (left, center, right);

        float AverageZones(string suffix)
        {
            var values = ZoneKeys.Where(k => k.EndsWith(suffix, StringComparison.Ordinal))
                                 .Select(ZoneProbability).ToArray();
            return values.Length == 0 ? 0f : values.Average();
        }
    }

    private static float Normalize(float value, float min, float max) =>
        Math.Clamp((value - min) / (max - min), 0f, 1f);
}
