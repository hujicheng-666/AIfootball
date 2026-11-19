using AIfootball.Core.Models;

namespace AIfootball.Core.Tests;

/// <summary>
/// 门将扑救模型。这些断言锁住的是"物理链路"的单调性——没有它们，
/// 调一个系数就可能让界面上出现"门将反应比球飞得还快"这种反物理结论。
/// </summary>
public sealed class SaveModelTests
{
    private static GoalkeeperProfile Keeper(
        float height = 1.90f,
        float diveSpeed = 4.0f,
        float jumpDistance = 1.0f,
        float jumpHeight = 0.5f,
        float reach = 0.5f,
        float keeping = 0.85f)
    {
        var profile = new GoalkeeperProfile
        {
            Name = "keeper",
            Height = height,
            DiveSpeed = diveSpeed,
            JumpDistance = jumpDistance,
            JumpHeight = jumpHeight,
            Reach = reach,
            GoalKeeping = keeping,
        };
        foreach (var key in GoalkeeperProfile.ZoneKeys) profile.ProbabilityMap[key] = 0.5f;
        return profile;
    }

    [Fact]
    public void ReactionTime_InterpolatesWithKeepingAndStaysInBounds()
    {
        Assert.Equal(0.32, SaveModel.ReactionTime(Keeper(keeping: 0f)), 6);
        Assert.Equal(0.16, SaveModel.ReactionTime(Keeper(keeping: 1f)), 6);

        var weak = SaveModel.ReactionTime(Keeper(keeping: 0.2f));
        var strong = SaveModel.ReactionTime(Keeper(keeping: 0.8f));
        Assert.True(strong < weak);
    }

    [Fact]
    public void AvailableTime_SubtractsReactionAndNeverGoesNegative()
    {
        var keeper = Keeper(keeping: 0.5f);                 // 反应时间 0.24
        Assert.Equal(0.56, SaveModel.AvailableTime(keeper, 0.8), 6);
        Assert.Equal(0.0, SaveModel.AvailableTime(keeper, 0.1), 6);
    }

    [Fact]
    public void StandingReach_ScalesWithHeight()
    {
        Assert.Equal(1.90 * 1.15, SaveModel.StandingReach(Keeper()), 6);
        Assert.True(SaveModel.StandingReach(Keeper(height: 2.0f)) > SaveModel.StandingReach(Keeper(height: 1.8f)));
    }

    [Fact]
    public void LateralEnvelope_GrowsWithTimeThenSaturatesAtJumpDistance()
    {
        var keeper = Keeper(reach: 0.5f, diveSpeed: 4f, jumpDistance: 1.0f);

        Assert.Equal(0.5, SaveModel.LateralEnvelope(keeper, 0.0), 6);      // 只有臂展
        Assert.Equal(1.3, SaveModel.LateralEnvelope(keeper, 0.2), 6);      // 0.8 + 0.5
        Assert.Equal(1.5, SaveModel.LateralEnvelope(keeper, 10.0), 6);     // 打满飞身距离 + 臂展
    }

    [Fact]
    public void VerticalEnvelope_GrowsWithTimeThenSaturatesAtJumpHeight()
    {
        var keeper = Keeper();
        var standing = SaveModel.StandingReach(keeper);

        Assert.Equal(standing, SaveModel.VerticalEnvelope(keeper, 0.0), 6);
        Assert.Equal(standing + keeper.JumpHeight, SaveModel.VerticalEnvelope(keeper, 0.35), 6);
        Assert.Equal(standing + keeper.JumpHeight, SaveModel.VerticalEnvelope(keeper, 5.0), 6);
    }

    [Fact]
    public void Assess_CentreLowShotWithEnoughTimeIsReachable()
    {
        var assessment = SaveModel.Assess(Keeper(), lateral: 0.4, height: 0.6, flightTime: 0.9);

        Assert.True(assessment.OnTarget);
        Assert.True(assessment.Reachable);
        Assert.True(assessment.SaveChance >= 0.5);
        Assert.True(assessment.TimeMargin > 0);
        Assert.Contains(assessment.Verdict, new[] { "门将轻松化解", "门将占优", "五五开" });
    }

    [Fact]
    public void Assess_WideShotIsOffTargetWithZeroChance()
    {
        var assessment = SaveModel.Assess(Keeper(), lateral: 4.2, height: 1.2, flightTime: 0.9);

        Assert.False(assessment.OnTarget);
        Assert.False(assessment.Reachable);
        Assert.Equal(0.0, assessment.SaveChance, 6);
        Assert.Equal("偏出球门", assessment.Verdict);
    }

    [Fact]
    public void Assess_OverTheBarIsOffTarget()
    {
        var assessment = SaveModel.Assess(Keeper(), lateral: 0.0, height: 2.9, flightTime: 0.9);

        Assert.False(assessment.OnTarget);
        Assert.Equal("偏出球门", assessment.Verdict);
    }

    [Fact]
    public void Assess_ExtremeCornerWithAlmostNoTimeIsNotReachable()
    {
        // 0.18s 飞行时间 < 反应时间 0.184s，可用时间为 0 → 机械上够不到
        var assessment = SaveModel.Assess(Keeper(), lateral: 3.4, height: 2.2, flightTime: 0.18);

        Assert.True(assessment.OnTarget);
        Assert.False(assessment.Reachable);
        Assert.Equal(0.0, assessment.AvailableTime, 6);
        Assert.True(assessment.SaveChance <= 0.30);
        Assert.Contains(assessment.Verdict, new[] { "门将勉强够到", "门将无解" });
    }

    [Fact]
    public void Assess_MoreFlightTimeNeverLowersSaveChance()
    {
        var keeper = Keeper();
        var previous = -1.0;

        foreach (var flight in new[] { 0.30, 0.45, 0.60, 0.75, 0.90, 1.20 })
        {
            var chance = SaveModel.Assess(keeper, lateral: 1.2, height: 1.0, flightTime: flight).SaveChance;
            Assert.True(chance >= previous - 1e-9, $"飞行时间 {flight}s 的扑救率不应低于更短时间：{chance} < {previous}");
            previous = chance;
        }
    }

    [Fact]
    public void Assess_BetterKeeperSavesAtLeastAsOften()
    {
        var weak = Keeper(diveSpeed: 2.5f, jumpDistance: 1.0f, reach: 0.4f, jumpHeight: 0.3f, keeping: 0.3f);
        var strong = Keeper(diveSpeed: 6.0f, jumpDistance: 2.2f, reach: 0.9f, jumpHeight: 0.9f, keeping: 0.98f);

        var weakChance = SaveModel.Assess(weak, lateral: 2.6, height: 1.6, flightTime: 0.55).SaveChance;
        var strongChance = SaveModel.Assess(strong, lateral: 2.6, height: 1.6, flightTime: 0.55).SaveChance;

        Assert.True(strongChance >= weakChance);
    }

    [Fact]
    public void Assessment_DerivedMarginsAreConsistent()
    {
        var assessment = SaveModel.Assess(Keeper(), lateral: 2.0, height: 1.4, flightTime: 0.55);

        Assert.Equal(assessment.AvailableTime - assessment.RequiredTime, assessment.TimeMargin, 9);
        Assert.Equal(assessment.CoverageLateral - assessment.LateralDistance, assessment.ReachMargin, 9);
        Assert.Equal(1 - assessment.SaveChance, assessment.Difficulty, 9);
        Assert.InRange(assessment.Difficulty, 0, 1);
        Assert.Equal(Math.Abs(2.0), assessment.LateralDistance, 9);
        Assert.Equal(GoalGeometry.ZoneKey(2.0, 1.4), assessment.ZoneKey);
    }

    [Fact]
    public void Assess_UsesTrajectoryGoalLineCrossing()
    {
        var keeper = Keeper();
        var trajectory = new Trajectory
        {
            SampleName = "sample1",
            Points =
            [
                new TrajectoryPoint(0.00, 11.0, 0.0, 0.11),
                new TrajectoryPoint(0.50, 0.50, 1.0, 1.00),
                new TrajectoryPoint(1.00, -0.50, 1.2, 1.05),
            ],
        };

        var crossing = trajectory.GoalLineCrossing;
        Assert.NotNull(crossing);
        Assert.Equal(1.1, crossing!.Value.Lateral, 6);
        Assert.Equal(1.025, crossing.Value.Height, 6);
        Assert.Equal(0.75, crossing.Value.Time, 6);

        var assessment = SaveModel.Assess(keeper, trajectory);
        Assert.NotNull(assessment);
        Assert.Equal(crossing.Value.Lateral, assessment!.LateralDistance, 6);
        Assert.Equal(crossing.Value.Time, assessment.FlightTime, 6);
    }

    [Fact]
    public void Assess_ReturnsNullWhenTrajectoryNeverReachesGoalLine()
    {
        var trajectory = new Trajectory
        {
            SampleName = "sample2",
            Points =
            [
                new TrajectoryPoint(0.0, 11.0, 0.0, 0.11),
                new TrajectoryPoint(0.2, 8.0, 0.1, 0.40),
            ],
        };

        Assert.Null(trajectory.GoalLineCrossing);
        Assert.Null(SaveModel.Assess(Keeper(), trajectory));
    }

    [Fact]
    public void KeeperCoverage_IsDerivedFromKeeperProfile()
    {
        var keeper = Keeper(reach: 0.5f, diveSpeed: 4f, jumpDistance: 1.0f);
        var coverage = KeeperCoverage.From(null, keeper);

        Assert.NotNull(coverage);
        Assert.Equal(SaveModel.StandingReach(keeper), coverage!.StandingReach, 6);
        Assert.Equal(keeper.DiveSpeed, coverage.DiveSpeed, 6);
        Assert.Equal(SaveModel.AvailableTime(keeper, 0.45), coverage.AvailableTime, 6);
        // 横向半径与纵向触及高度分别来自两条公式，都随可用时间增长
        Assert.Equal(SaveModel.LateralEnvelope(keeper, coverage.AvailableTime), coverage.LateralRadius, 6);
        Assert.Equal(SaveModel.VerticalEnvelope(keeper, coverage.AvailableTime), coverage.MaxHeight, 6);
        Assert.True(coverage.LateralRadius > 0);
        Assert.True(coverage.MaxHeight > coverage.StandingReach);
    }
}
