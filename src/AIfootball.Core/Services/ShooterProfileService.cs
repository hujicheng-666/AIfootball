using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

/// <summary>射手脚法画像：只由训练轨迹本身推导，不使用门将仿真结果。</summary>
public sealed record ShooterProfile(
    int SourceFileCount,
    int UniqueTrajectoryCount,
    int DuplicateTrajectoryCount,
    int InvalidFileCount,
    double AverageSpeed,
    double AverageLateralOffset,
    double Spread,
    double AverageHeight,
    string PreferredSide,
    string PreferredHeight,
    string PowerTrait,
    string ConsistencyTrait)
{
    /// <summary>逐脚的门线落点，用于落点散点图与"射手 × 门将"对位建模。</summary>
    public IReadOnlyList<ShotPlacement> Placements { get; init; } = [];

    public bool HasData => UniqueTrajectoryCount > 0;

    public static ShooterProfile Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, "—", "—", "—", "—");

    /// <summary>把这个射手的全部落点喂给某位门将的扑救模型，得到对位胜率。</summary>
    public MatchupReport MatchupAgainst(GoalkeeperProfile goalkeeper) =>
        MatchupModel.Analyze(goalkeeper, Placements);
}

/// <summary>
/// 训练数据画像。同一轨迹可能在 data/ 下存在多份拷贝，先按几何签名去重再统计，
/// 避免"练了 3 次却显示 30 次"的虚高。
/// </summary>
public sealed class ShooterProfileService(AifootballWorkspace workspace)
{
    private readonly AifootballWorkspace _workspace = workspace;
    private readonly TrajectoryStore _store = new(workspace);

    public ShooterProfile Analyze()
    {
        var samples = new SampleCatalog(_workspace).ScanDeliverables();
        if (samples.Count == 0) return ShooterProfile.Empty;

        var speeds = new List<double>();
        var offsets = new List<double>();
        var heights = new List<double>();
        var placements = new List<ShotPlacement>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var invalid = 0;

        foreach (var name in samples)
        {
            var trajectory = _store.Load(name);
            if (trajectory is null || trajectory.Points.Count < 3) { invalid++; continue; }

            var signature = BuildSignature(trajectory);
            if (!signatures.Add(signature)) { duplicates++; continue; }

            var crossing = trajectory.GoalLineCrossing;
            var lateral = crossing?.Lateral ?? trajectory.Points[^1].Lateral;
            var height = crossing?.Height ?? trajectory.ApexHeight;

            speeds.Add(trajectory.LaunchSpeed);
            offsets.Add(lateral);
            heights.Add(height);
            placements.Add(new ShotPlacement(
                name, lateral, height, crossing?.Time ?? trajectory.Duration, trajectory.LaunchSpeed));
        }

        if (speeds.Count == 0) return ShooterProfile.Empty;

        var spread = StandardDeviation(offsets);
        var averageSpeed = speeds.Average();
        var averageOffset = offsets.Average();
        var averageHeight = heights.Average();

        return new ShooterProfile(
            samples.Count,
            speeds.Count,
            duplicates,
            invalid,
            averageSpeed,
            averageOffset,
            spread,
            averageHeight,
            DescribeSide(offsets),
            DescribeHeight(averageHeight),
            DescribePower(averageSpeed),
            DescribeConsistency(spread))
        {
            Placements = placements,
        };
    }

    private static string DescribeSide(List<double> offsets)
    {
        var left = offsets.Count(o => o < -0.4);
        var right = offsets.Count(o => o > 0.4);
        var center = offsets.Count - left - right;
        if (left > right * 1.5) return "偏好左侧";
        if (right > left * 1.5) return "偏好右侧";
        if (center >= left && center >= right) return "偏好中路";
        return "左右均衡";
    }

    private static string DescribeHeight(double height) => height switch
    {
        < 0.7 => "低平球",
        < 1.5 => "半高球",
        < 2.2 => "上角球",
        _ => "高吊球",
    };

    private static string DescribePower(double speed) => speed switch
    {
        < 14 => "控球为主",
        < 20 => "中等力量",
        < 26 => "力量充足",
        _ => "重炮",
    };

    private static string DescribeConsistency(double spread) => spread switch
    {
        < 0.6 => "落点高度稳定",
        < 1.4 => "落点略有波动",
        _ => "落点分散",
    };

    private static double StandardDeviation(List<double> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    private static string BuildSignature(Trajectory trajectory)
    {
        var builder = new System.Text.StringBuilder();
        var step = Math.Max(1, trajectory.Points.Count / 12);
        for (var i = 0; i < trajectory.Points.Count; i += step)
        {
            var point = trajectory.Points[i];
            builder.Append(Math.Round(point.Forward, 2)).Append('|')
                   .Append(Math.Round(point.Lateral, 2)).Append('|')
                   .Append(Math.Round(point.Height, 2)).Append(';');
        }
        return builder.ToString();
    }
}
