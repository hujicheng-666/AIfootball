namespace AIfootball.Core.Models;

/// <summary>标准球门几何（米）：7.32 × 2.44，点球点 11 m。</summary>
public static class GoalGeometry
{
    public const double HalfWidth = 3.66;
    public const double Height = 2.44;
    public const double PenaltyDistance = 11.0;

    /// <summary>区域概率图的列键（左→右五列）。</summary>
    public static readonly string[] Columns = ["Left", "MidLeft", "Center", "MidRight", "Right"];

    /// <summary>区域概率图的行键（低→高三行）。</summary>
    public static readonly string[] Rows = ["Low", "Mid", "High"];

    /// <summary>把门线上的落点映射到 3×5 区域键（与 Unity 侧 ProbabilityMap 一致）。</summary>
    public static string ZoneKey(double lateral, double height)
    {
        var column = (int)Math.Floor((lateral + HalfWidth) / (HalfWidth * 2 / 5));
        column = Math.Clamp(column, 0, 4);
        var row = (int)Math.Floor(Math.Max(0, height) / (Height / 3));
        row = Math.Clamp(row, 0, 2);
        return Rows[row] + Columns[column];
    }

    /// <summary>区域中心的门线坐标，供热图落点用。</summary>
    public static (double Lateral, double Height) ZoneCenter(int row, int column) =>
        (-HalfWidth + (column + 0.5) * (HalfWidth * 2 / 5), (row + 0.5) * (Height / 3));
}

/// <summary>一次射门的门线落点（射手画像与对位分析的最小单元）。</summary>
public sealed record ShotPlacement(string Sample, double Lateral, double Height, double FlightTime, double Speed)
{
    public string ZoneKey => GoalGeometry.ZoneKey(Lateral, Height);
    public bool OnTarget => Math.Abs(Lateral) <= GoalGeometry.HalfWidth && Height <= GoalGeometry.Height && Height >= 0;
}

/// <summary>门将在这脚球飞行时间内的可覆盖区域（门平面内的椭圆包络）。</summary>
public sealed record KeeperCoverage(
    double LateralRadius,
    double StandingReach,
    double MaxHeight,
    double AvailableTime,
    double DiveSpeed)
{
    public static KeeperCoverage? From(SaveAssessment? assessment, GoalkeeperProfile goalkeeper)
    {
        _ = assessment;
        return new KeeperCoverage(
            SaveModel.LateralEnvelope(goalkeeper, SaveModel.AvailableTime(goalkeeper, 0.45)),
            SaveModel.StandingReach(goalkeeper),
            SaveModel.VerticalEnvelope(goalkeeper, SaveModel.AvailableTime(goalkeeper, 0.45)),
            SaveModel.AvailableTime(goalkeeper, 0.45),
            goalkeeper.DiveSpeed);
    }
}

/// <summary>
/// 守门员扑救模型的计算结果。
/// 所有时间单位为秒、距离为米；<see cref="TimeMargin"/> 为正表示门将来得及。
/// </summary>
public sealed record SaveAssessment(
    bool OnTarget,
    bool Reachable,
    double FlightTime,
    double ReactionTime,
    double AvailableTime,
    double RequiredTime,
    double LateralDistance,
    double RequiredSpeed,
    double CoverageLateral,
    double CoverageHeight,
    double ZoneProbability,
    double SaveChance,
    string ZoneKey,
    string Verdict)
{
    /// <summary>时间余量：可用时间 - 到位所需时间。</summary>
    public double TimeMargin => AvailableTime - RequiredTime;

    /// <summary>距离余量：门将横向覆盖半径 - 实际需要的横向位移。</summary>
    public double ReachMargin => CoverageLateral - LateralDistance;

    /// <summary>0-1 难度，供界面配色使用。</summary>
    public double Difficulty => Math.Clamp(1 - SaveChance, 0, 1);
}

/// <summary>
/// 守门员扑救建模。
///
/// 物理链路：球的飞行时间（来自轨迹本身）→ 扣掉神经反应时间 → 得到可用时间；
/// 可用时间 × 移动速度给出横向可达距离（再叠加飞身与臂展的额外覆盖），
/// 纵向则由站立触及高度 + 起跳高度按时间折算。两者都够得着，才算"扑得到"。
///
/// 统计链路：命中区域在 <see cref="GoalkeeperProfile.ProbabilityMap"/> 里的扑救率，
/// 与门将综合能力加权，得到最终扑救概率——机械可达但概率低的情况（贴地死角）不会被判成必扑。
/// </summary>
public static class SaveModel
{
    /// <summary>起跳到最高点所需时间，用于把弹跳高度折算成平均起跳速度。</summary>
    private const double JumpRiseTime = 0.35;

    /// <summary>纯反应时间：0.32s（反应慢）→ 0.16s（顶级），由综合能力插值。</summary>
    public static double ReactionTime(GoalkeeperProfile goalkeeper) =>
        Math.Clamp(0.32 - 0.16 * goalkeeper.GoalKeeping, 0.16, 0.34);

    /// <summary>可用时间 = 飞行时间 - 反应时间（下限 0）。</summary>
    public static double AvailableTime(GoalkeeperProfile goalkeeper, double flightTime) =>
        Math.Max(0, flightTime - ReactionTime(goalkeeper));

    /// <summary>站立状态的指尖触及高度（臂展约等于身高，举手过头再加一点）。</summary>
    public static double StandingReach(GoalkeeperProfile goalkeeper) => goalkeeper.Height * 1.15;

    /// <summary>横向覆盖半径：移动距离（受飞身距离上限约束）+ 臂展。</summary>
    public static double LateralEnvelope(GoalkeeperProfile goalkeeper, double availableTime)
    {
        var travel = Math.Min(goalkeeper.DiveSpeed * availableTime, goalkeeper.JumpDistance);
        return travel + goalkeeper.Reach;
    }

    /// <summary>纵向触及高度：站立触及 + 按可用时间折算的起跳高度。</summary>
    public static double VerticalEnvelope(GoalkeeperProfile goalkeeper, double availableTime)
    {
        var rise = goalkeeper.JumpHeight * Math.Clamp(availableTime / JumpRiseTime, 0, 1);
        return StandingReach(goalkeeper) + rise;
    }

    /// <summary>对一条轨迹做评估（轨迹需已经过门线）。</summary>
    public static SaveAssessment? Assess(GoalkeeperProfile goalkeeper, Trajectory trajectory)
    {
        var crossing = trajectory.GoalLineCrossing;
        if (crossing is not { } hit) return null;
        return Assess(goalkeeper, hit.Lateral, hit.Height, hit.Time);
    }

    /// <summary>对门线上的一个落点 + 飞行时间做评估。</summary>
    public static SaveAssessment Assess(
        GoalkeeperProfile goalkeeper, double lateral, double height, double flightTime)
    {
        var reaction = ReactionTime(goalkeeper);
        var available = Math.Max(0, flightTime - reaction);

        var lateralDistance = Math.Abs(lateral);
        // 伸手臂可以省掉一部分横向位移，但绝不是全部——门将仍要移动身体
        var requiredLateralTime = Math.Max(0, lateralDistance - goalkeeper.Reach * 0.6) /
                                  Math.Max(0.5, goalkeeper.DiveSpeed);
        var requiredVerticalTime = height <= StandingReach(goalkeeper)
            ? 0
            : (height - StandingReach(goalkeeper)) / Math.Max(0.5, goalkeeper.JumpHeight / JumpRiseTime);

        // 横向与纵向的移动可以同时进行，所以瓶颈取两者较大值
        var required = Math.Max(requiredLateralTime, requiredVerticalTime);

        var coverageLateral = LateralEnvelope(goalkeeper, available);
        var coverageHeight = VerticalEnvelope(goalkeeper, available);
        var zoneKey = GoalGeometry.ZoneKey(lateral, height);
        var zoneProbability = goalkeeper.ZoneProbability(zoneKey);

        var onTarget = Math.Abs(lateral) <= GoalGeometry.HalfWidth
                       && height >= 0 && height <= GoalGeometry.Height;

        var reachable = onTarget
                        && required <= available
                        && lateralDistance <= coverageLateral
                        && height <= coverageHeight;

        // 统计层：机械可达时给足权重，够不到时只保留极小的"神扑"概率
        var skill = 0.6 + 0.45 * goalkeeper.GoalKeeping;
        var timeBonus = Math.Clamp(available - required, -0.2, 0.4) * 0.25;
        var saveChance = !onTarget
            ? 0
            : reachable
                ? Math.Clamp(zoneProbability * skill + timeBonus, 0.02, 0.97)
                : Math.Clamp(zoneProbability * 0.35, 0.0, 0.3);

        return new SaveAssessment(
            OnTarget: onTarget,
            Reachable: reachable,
            FlightTime: flightTime,
            ReactionTime: reaction,
            AvailableTime: available,
            RequiredTime: required,
            LateralDistance: lateralDistance,
            RequiredSpeed: requiredLateralTime <= 1e-6 ? 0 : lateralDistance / Math.Max(1e-3, requiredLateralTime),
            CoverageLateral: coverageLateral,
            CoverageHeight: coverageHeight,
            ZoneProbability: zoneProbability,
            SaveChance: saveChance,
            ZoneKey: zoneKey,
            Verdict: Describe(onTarget, reachable, saveChance));
    }

    private static string Describe(bool onTarget, bool reachable, double saveChance)
    {
        if (!onTarget) return "偏出球门";
        if (!reachable) return saveChance > 0.12 ? "门将勉强够到" : "门将无解";
        return saveChance switch
        {
            >= 0.72 => "门将轻松化解",
            >= 0.5 => "门将占优",
            >= 0.32 => "五五开",
            _ => "门将险扑",
        };
    }
}

/// <summary>射手 × 门将的对位报告。</summary>
public sealed record MatchupReport(
    int ShotCount,
    int OnTargetCount,
    int BeatenCount,
    int SavedCount,
    double BeatRate,
    double AverageSaveChance,
    double FavouriteLateral,
    string ZoneKey,
    string Verdict)
{
    public static MatchupReport Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, "", "暂无足够轨迹做对位分析");

    public bool HasData => ShotCount > 0 && OnTargetCount > 0;
}

public static class MatchupModel
{
    /// <summary>把历史落点逐个喂给门将模型，得到"这个射手打这个门将"的胜率画像。</summary>
    public static MatchupReport Analyze(GoalkeeperProfile goalkeeper, IReadOnlyList<ShotPlacement> placements)
    {
        if (placements.Count == 0) return MatchupReport.Empty;

        var onTarget = 0;
        var beaten = 0;
        var saved = 0;
        var chanceSum = 0.0;
        var lateralSum = 0.0;
        var zones = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var shot in placements)
        {
            if (!shot.OnTarget) continue;
            onTarget++;
            lateralSum += shot.Lateral;

            var assessment = SaveModel.Assess(goalkeeper, shot.Lateral, shot.Height, shot.FlightTime);
            chanceSum += assessment.SaveChance;
            if (assessment.SaveChance >= 0.5) saved++;
            else beaten++;

            zones[shot.ZoneKey] = zones.TryGetValue(shot.ZoneKey, out var count) ? count + 1 : 1;
        }

        if (onTarget == 0) return MatchupReport.Empty with { ShotCount = placements.Count };

        var favourite = zones.OrderByDescending(pair => pair.Value).First().Key;
        var beatRate = (double)beaten / onTarget;
        var averageChance = chanceSum / onTarget;

        return new MatchupReport(
            placements.Count,
            onTarget,
            beaten,
            saved,
            beatRate,
            averageChance,
            lateralSum / onTarget,
            favourite,
            Describe(beatRate, averageChance));
    }

    private static string Describe(double beatRate, double averageSaveChance) => beatRate switch
    {
        >= 0.7 => "这个射手明显压制该门将",
        >= 0.5 => "射手略占上风",
        >= 0.3 => "门将略占上风",
        _ => $"该门将对位压制（平均扑救率 {averageSaveChance:P0}）",
    };
}
