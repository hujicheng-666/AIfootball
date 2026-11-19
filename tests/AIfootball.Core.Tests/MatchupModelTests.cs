using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

/// <summary>
/// 对位建模：把射手的历史落点逐个喂给门将模型，得到"这个射手打这个门将"的胜率画像。
/// 断言的重点是计数闭合（扑出 + 打穿 = 射正）与结论档位，不是具体小数。
/// </summary>
public sealed class MatchupModelTests
{
    private static GoalkeeperProfile Keeper(float keeping = 0.85f, float diveSpeed = 4.5f, float reach = 0.8f)
    {
        var profile = new GoalkeeperProfile
        {
            Name = "keeper",
            DisplayName = "测试门将",
            Height = 1.92f,
            DiveSpeed = diveSpeed,
            JumpDistance = 1.4f,
            JumpHeight = 0.6f,
            Reach = reach,
            GoalKeeping = keeping,
        };
        foreach (var key in GoalkeeperProfile.ZoneKeys) profile.ProbabilityMap[key] = 0.55f;
        return profile;
    }

    private static ShotPlacement Shot(double lateral, double height, double flight = 0.7) =>
        new("sample1", lateral, height, flight, 26.0);

    [Fact]
    public void Analyze_EmptyPlacementsReturnsEmptyReport()
    {
        var report = MatchupModel.Analyze(Keeper(), []);

        Assert.Equal(0, report.ShotCount);
        Assert.False(report.HasData);
        Assert.Equal("暂无足够轨迹做对位分析", report.Verdict);
        Assert.Equal(MatchupReport.Empty, report);
    }

    [Fact]
    public void Analyze_AllOffTargetKeepsShotCountButHasNoData()
    {
        var shots = new List<ShotPlacement> { Shot(4.5, 1.2), Shot(-5.0, 0.4) };
        var report = MatchupModel.Analyze(Keeper(), shots);

        Assert.Equal(2, report.ShotCount);
        Assert.Equal(0, report.OnTargetCount);
        Assert.False(report.HasData);
    }

    [Fact]
    public void Analyze_CountsAreClosed()
    {
        var shots = new List<ShotPlacement>
        {
            Shot(0.0, 0.5),    // 正中间 → 门将轻松扑
            Shot(0.4, 0.8),    // 中路 → 扑
            Shot(3.5, 2.3),    // 右上死角 → 打穿
            Shot(4.9, 1.0),    // 偏出 → 不计入射正
        };

        var report = MatchupModel.Analyze(Keeper(), shots);

        Assert.Equal(4, report.ShotCount);
        Assert.Equal(3, report.OnTargetCount);
        Assert.Equal(report.SavedCount + report.BeatenCount, report.OnTargetCount);
        Assert.Equal(report.BeatRate, (double)report.BeatenCount / report.OnTargetCount, 9);
        Assert.InRange(report.AverageSaveChance, 0, 1);
        Assert.InRange(report.BeatRate, 0, 1);
    }

    [Fact]
    public void Analyze_FavouriteZoneIsTheMostFrequentOne()
    {
        var shots = new List<ShotPlacement>
        {
            Shot(-3.4, 0.3),   // LowLeft
            Shot(-3.2, 0.4),   // LowLeft
            Shot(0.0, 2.2),    // HighCenter
        };

        var report = MatchupModel.Analyze(Keeper(), shots);

        Assert.Equal("LowLeft", report.ZoneKey);
    }

    [Fact]
    public void Analyze_AllSavedLooksLikeKeeperDominance()
    {
        var shots = Enumerable.Range(0, 4).Select(_ => Shot(0.2, 0.5)).ToList();
        var report = MatchupModel.Analyze(Keeper(keeping: 0.95f), shots);

        Assert.Equal(4, report.SavedCount);
        Assert.Equal(0, report.BeatenCount);
        Assert.Equal(0.0, report.BeatRate, 9);
        Assert.StartsWith("该门将对位压制", report.Verdict);
    }

    [Fact]
    public void Analyze_AllBeatenLooksLikeShooterDominance()
    {
        // 极慢门将 + 三个死角：机械上够不到，且区域概率给得再高也判不成扑出
        var slow = Keeper(keeping: 0.05f, diveSpeed: 2.0f, reach: 0.45f);
        var shots = new List<ShotPlacement>
        {
            Shot(3.6, 2.4, flight: 0.35),
            Shot(-3.6, 2.4, flight: 0.35),
            Shot(3.6, 2.3, flight: 0.32),
        };

        var report = MatchupModel.Analyze(slow, shots);

        Assert.Equal(3, report.BeatenCount);
        Assert.Equal(0, report.SavedCount);
        Assert.Equal(1.0, report.BeatRate, 9);
        Assert.Equal("这个射手明显压制该门将", report.Verdict);
    }

    [Fact]
    public void Analyze_StrongKeeperSavesMoreThanWeakKeeper()
    {
        var shots = Enumerable.Range(0, 6)
            .Select(i => Shot(-2.4 + i * 0.9, 0.6 + (i % 3) * 0.5))
            .ToList();

        var weak = MatchupModel.Analyze(Keeper(keeping: 0.2f, diveSpeed: 2.5f, reach: 0.45f), shots);
        var strong = MatchupModel.Analyze(Keeper(keeping: 0.98f, diveSpeed: 6.5f, reach: 1.05f), shots);

        Assert.True(strong.SavedCount >= weak.SavedCount);
        Assert.True(strong.AverageSaveChance >= weak.AverageSaveChance);
    }

    [Fact]
    public void ShooterProfile_MatchupAgainstDelegatesToMatchupModel()
    {
        var keeper = Keeper();
        var shots = new List<ShotPlacement> { Shot(0.2, 0.4), Shot(2.8, 1.9) };
        var profile = new ShooterProfile(
            SourceFileCount: 2,
            UniqueTrajectoryCount: 2,
            DuplicateTrajectoryCount: 0,
            InvalidFileCount: 0,
            AverageSpeed: 26.0,
            AverageLateralOffset: 1.2,
            Spread: 0.4,
            AverageHeight: 1.1,
            PreferredSide: "偏好右侧",
            PreferredHeight: "半高球",
            PowerTrait: "力量充足",
            ConsistencyTrait: "落点高度稳定")
        {
            Placements = shots,
        };

        var report = profile.MatchupAgainst(keeper);

        Assert.Equal(shots.Count, report.ShotCount);
        Assert.Equal(2, report.OnTargetCount);
    }
}
