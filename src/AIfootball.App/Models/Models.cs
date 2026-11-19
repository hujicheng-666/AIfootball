using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AIfootball.Core.Models;

namespace AIfootball.App.Models;

/// <summary>处理样本信息</summary>
/// <remarks>
/// 落点三项由弹道拟合结果带出，未跑完弹道拟合时为空——界面据此显示"未分析"，
/// 不能用 0 假装成"打在门中间"。
/// </remarks>
public record SampleInfo(
    string Name,
    string DirectoryPath,
    int VideoCount,
    bool HasResult3D,
    bool HasResultBallistic,
    bool HasUnityCsv,
    double? ShotLateral = null,
    double? ShotHeight = null,
    double? ShotSpeed = null
)
{
    public bool HasShotPlacement => ShotLateral.HasValue && ShotHeight.HasValue;

    /// <summary>门线落点的一句话摘要，样本列表直接显示。</summary>
    public string ShotSummary => HasShotPlacement
        ? $"{DirectionLabel(ShotLateral!.Value)}{HeightLabel(ShotHeight!.Value)}"
          + (ShotSpeed is { } speed ? $" · {speed:0.0} m/s" : "")
        : "未分析";

    public string ShotVerdict => HasShotPlacement
        ? Math.Abs(ShotLateral!.Value) <= 3.66 && ShotHeight!.Value is >= 0 and <= 2.44 ? "射正" : "射偏"
        : "";

    static string DirectionLabel(double lateral) =>
        lateral < -0.9 ? "左侧" : lateral > 0.9 ? "右侧" : "中路";

    static string HeightLabel(double height) =>
        height < 0.8 ? "低" : height < 1.6 ? "中" : "高";
}

/// <summary>流水线步骤状态</summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}

/// <summary>流水线步骤</summary>
public record PipelineStep(
    string Name,
    string Description,
    StepStatus Status = StepStatus.Pending
)
{
    public string StatusIcon => Status switch
    {
        StepStatus.Pending => "○",
        StepStatus.Running => "◉",
        StepStatus.Completed => "✓",
        StepStatus.Failed => "✗",
        StepStatus.Skipped => "→",
        _ => "○",
    };
}

/// <summary>环境状态信息</summary>
public record EnvironmentStatus(
    bool IsReady,
    string PythonVersion,
    string TorchVersion,
    string InferenceProfile, // "cuda" or "cpu"
    bool CudaAvailable,
    string GpuName,
    string GpuAdapterNames
)
{
    public static EnvironmentStatus Unknown => new(false, "", "", "", false, "", "");
}

/// <summary>
/// `aifootball selfcheck` 打印的那一行 JSON。缺哪个库，对应字段就是 null，
/// 所以这里一律用可空类型，不能拿空字符串冒充"已安装"。
/// </summary>
public sealed class EngineSelfCheck
{
    [JsonPropertyName("python")]
    public string? Python { get; set; }

    [JsonPropertyName("torch")]
    public string? Torch { get; set; }

    [JsonPropertyName("opencv")]
    public string? OpenCv { get; set; }

    [JsonPropertyName("cuda")]
    public bool Cuda { get; set; }

    [JsonPropertyName("ultralytics")]
    public string? Ultralytics { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("model_present")]
    public bool ModelPresent { get; set; }

    /// <summary>推理必需的三件套是否齐全；模型文件缺失单独提示，不影响这一步。</summary>
    [JsonIgnore]
    public bool HasCoreDependencies => Torch is not null && OpenCv is not null && Ultralytics is not null;

    /// <summary>缺了哪些依赖，直接拼进日志。</summary>
    public string MissingSummary()
    {
        var missing = new List<string>();
        if (Torch is null) missing.Add("torch");
        if (OpenCv is null) missing.Add("opencv-python");
        if (Ultralytics is null) missing.Add("ultralytics");
        return missing.Count == 0 ? "无" : string.Join(", ", missing);
    }
}

/// <summary>GPU 信息</summary>
public record GpuInfo(
    bool HasNvidiaGpu,
    bool CudaAvailable,
    string GpuName,
    string CudaVersion,
    bool HasDedicatedGpu,
    string AdapterNames
);

/// <summary>校准状态</summary>
public record CalibrationStatus(
    bool IntrinsicsReady,
    bool ExtrinsicsReady,
    string LeftIntrinsicsPath,
    string RightIntrinsicsPath,
    string LeftPosePath,
    string RightPosePath
)
{
    public bool FullyReady => IntrinsicsReady && ExtrinsicsReady;
}

/// <summary>门将数据</summary>
public record GoalkeeperInfo(
    string Name,
    string FilePath
);

/// <summary>
/// 由训练 CSV 原始轨迹生成的点球手画像。不会使用 Unity 回放或门将仿真结果。
/// </summary>
public record ShooterProfileResult(
    int SourceFileCount,
    int UniqueTrajectoryCount,
    int DuplicateTrajectoryCount,
    int InvalidFileCount,
    float AverageSpeed,
    float AverageTargetOffset,
    float TargetSpread,
    float AverageTargetHeight,
    string PreferredSide,
    string PreferredHeight,
    string PowerTrait,
    string ConsistencyTrait
)
{
    public bool HasData => UniqueTrajectoryCount > 0;
}

/// <summary>门将属性（从 data/goalkeepers/*.json 解析，雷达图 + 区域热图用）</summary>
public class GoalkeeperStats
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public float Height { get; set; } = 1.9f;
    public float DiveSpeed { get; set; } = 4f;
    public float JumpDistance { get; set; } = 1f;
    public float JumpHeight { get; set; } = 0.5f;
    public float Reach { get; set; } = 0.5f;
    public float TendGoalSpeed { get; set; } = 3f;
    public float TendGoalDistance { get; set; } = 3f;
    public float GoalKeeping { get; set; } = 0.85f;
    public float SidePreference { get; set; } = 0f;
    public float HeightPreference { get; set; } = 0f;

    /// <summary>
    /// 3×5 区域扑救概率（键为 Low/Mid/High + Left/MidLeft/Center/MidRight/Right）。
    /// 这是 Unity 播放器读同一份 JSON 时使用的字段，界面上显示的就是它实际生效的值。
    /// </summary>
    public Dictionary<string, float> ProbabilityMap { get; set; } = new();

    /// <summary>按行优先（低→高）把概率摊平成 15 个格子；缺失键按 0 处理。</summary>
    public double[] ToZoneGrid()
    {
        var grid = new double[15];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 5; column++)
            {
                var key = AIfootball.Core.Models.GoalGeometry.Rows[row]
                          + AIfootball.Core.Models.GoalGeometry.Columns[column];
                grid[row * 5 + column] = ProbabilityMap.TryGetValue(key, out var value) ? value : 0;
            }
        }
        return grid;
    }

    /// <summary>地图是否为空；不参与序列化，避免把计算属性写回门将 JSON。</summary>
    [JsonIgnore]
    public bool HasProbabilityMap => ProbabilityMap.Count > 0;

    /// <summary>编辑用的深拷贝：编辑器的"取消"不能污染界面里正在显示的那份数据。</summary>
    public GoalkeeperStats Clone() => new()
    {
        DisplayName = DisplayName,
        Description = Description,
        Height = Height,
        DiveSpeed = DiveSpeed,
        JumpDistance = JumpDistance,
        JumpHeight = JumpHeight,
        Reach = Reach,
        TendGoalSpeed = TendGoalSpeed,
        TendGoalDistance = TendGoalDistance,
        GoalKeeping = GoalKeeping,
        SidePreference = SidePreference,
        HeightPreference = HeightPreference,
        ProbabilityMap = new Dictionary<string, float>(ProbabilityMap),
    };
}

/// <summary>
/// 射门落点的界面文案。落点本身复用 <c>AIfootball.Core.Models.ShotPlacement</c>，
/// 这里只补 UI 需要的措辞，不重复定义数据模型。
/// </summary>
public static class ShotPlacementDisplay
{
    public static string Summary(this ShotPlacement shot) =>
        $"{(shot.Lateral < -0.9 ? "左侧" : shot.Lateral > 0.9 ? "右侧" : "中路")}"
        + $"{(shot.Height < 0.8 ? "低" : shot.Height < 1.6 ? "中" : "高")}"
        + $" · {shot.Speed:0.0} m/s";

    public static string Verdict(this ShotPlacement shot) => shot.OnTarget ? "射正" : "射偏";
}

/// <summary>一批射门落点的统计结果，供门面热图与散点图使用。</summary>
public sealed record ShotPlacementSummary(
    IReadOnlyList<ShotPlacement> Shots
)
{
    public int Count => Shots.Count;
    public int OnTargetCount => Shots.Count(s => s.OnTarget);
    public bool HasData => Shots.Count > 0;

    /// <summary>15 格落点计数（行优先，与 <c>GoalGeometry.ZoneKeys</c> 同序）。</summary>
    public int[] ZoneCounts()
    {
        var counts = new int[15];
        foreach (var shot in Shots)
        {
            var key = shot.ZoneKey;
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 5; column++)
                {
                    if (key != AIfootball.Core.Models.GoalGeometry.Rows[row]
                              + AIfootball.Core.Models.GoalGeometry.Columns[column]) continue;
                    counts[row * 5 + column]++;
                }
            }
        }
        return counts;
    }

    /// <summary>
    /// 归一化到门框坐标的散点（0→1，原点在球门左下角）。
    /// 不做裁剪：射偏的球必须是负值或大于 1，界面上才能看出在门框之外。
    /// </summary>
    public (double X, double Y)[] ScatterPoints() =>
        Shots.Select(shot => (
            (shot.Lateral + AIfootball.Core.Models.GoalGeometry.HalfWidth)
                / (AIfootball.Core.Models.GoalGeometry.HalfWidth * 2),
            shot.Height / AIfootball.Core.Models.GoalGeometry.Height)).ToArray();
}

/// <summary>样本选择包装（用于 Pipeline 复选框绑定）</summary>
public class SampleItem : INotifyPropertyChanged
{
    public SampleInfo Info { get; }
    public string Name => Info.Name;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(); }
    }

    public SampleItem(SampleInfo info) { Info = info; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>应用日志条目</summary>
public record LogEntry(
    DateTime Timestamp,
    string Level,
    string Message
);
