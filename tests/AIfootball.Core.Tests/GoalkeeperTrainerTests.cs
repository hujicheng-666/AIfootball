using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

public class HomographyTests
{
    // 一块 1280×720 的画面里，球门四角大致落在这里：
    // 左上 (420,300) 右上 (860,300) 右下 (860,560) 左下 (420,560)
    private static readonly (double X, double Y)[] Screen =
    [
        (420, 300), (860, 300), (860, 560), (420, 560),
    ];

    private static Homography Create() =>
        Homography.Solve(Screen, GoalFrameCalibration.GoalPoints())
        ?? throw new InvalidOperationException("标定点应当可解");

    [Fact]
    public void 四个角点映射到球门四角()
    {
        var homography = Create();
        var expected = GoalFrameCalibration.GoalPoints();

        for (var i = 0; i < 4; i++)
        {
            var mapped = homography.Map(Screen[i].X, Screen[i].Y);
            Assert.NotNull(mapped);
            Assert.Equal(expected[i].X, mapped!.Value.X, 3);
            Assert.Equal(expected[i].Y, mapped.Value.Y, 3);
        }
    }

    [Fact]
    public void 画面中心映射到球门中心()
    {
        var mapped = Create().Map(640, 430);
        Assert.NotNull(mapped);
        Assert.Equal(0, mapped!.Value.X, 2);
        Assert.Equal(GoalGeometry.Height / 2, mapped.Value.Y, 2);
    }

    [Fact]
    public void 左半边映射到负横向()
    {
        var mapped = Create().Map(480, 500);
        Assert.NotNull(mapped);
        Assert.True(mapped!.Value.X < 0, "画面左侧应映射到射手视角左（负值）");
    }

    [Fact]
    public void 画面越靠上映射的高度越高()
    {
        var high = Create().Map(640, 320)!.Value.Y;
        var low = Create().Map(640, 550)!.Value.Y;
        Assert.True(high > low);
    }

    [Fact]
    public void 共线的四个点无法解出单应()
    {
        (double X, double Y)[] collinear = [(100, 100), (200, 100), (300, 100), (400, 100)];
        Assert.Null(Homography.Solve(collinear, GoalFrameCalibration.GoalPoints()));
    }
}

public class GoalkeeperTrainerTests
{
    private static KeeperShotSample Shot(double lateral, double height, bool saved, string clip = "clip")
        => new(clip, lateral, height, saved);

    private static GoalkeeperProfile Template() => new()
    {
        Height = 1.92f,
        DiveSpeed = 4.4f,
        ProbabilityMap = GoalkeeperProfile.ZoneKeys.ToDictionary(key => key, _ => 0.5f),
    };

    [Fact]
    public void 偏出球门的标注不计入统计()
    {
        var samples = new[]
        {
            Shot(-3.0, 0.4, true),
            Shot(4.2, 0.4, true),   // 偏出右门柱
            Shot(0.0, 2.8, false),  // 高出横梁
        };

        var result = GoalkeeperTrainer.Train("t", samples, Template());

        Assert.Equal(1, result.ShotCount);
        Assert.Equal(2, result.OffTargetCount);
        Assert.Contains(result.Warnings, warning => warning.Contains("偏出球门"));
    }

    [Fact]
    public void 没有有效样本时保留模板概率表()
    {
        var result = GoalkeeperTrainer.Train("t", [Shot(5.0, 0.5, true)], Template());

        Assert.Equal(TrainingConfidence.Insufficient, result.Confidence);
        Assert.Equal(0, result.ShotCount);
        Assert.All(result.Zones, zone => Assert.Equal(0.5, zone.Probability, 3));
        Assert.Contains(result.Warnings, warning => warning.Contains("没有落在门框内"));
    }

    [Fact]
    public void 概率始终落在开区间内避免必然扑救或必然进球()
    {
        // 全部射向同一格且全部被扑出，最容易把估计推成 1.0
        var samples = Enumerable.Range(0, 30)
            .Select(i => Shot(-3.4, 0.2, true, $"c{i}"))
            .ToArray();

        var result = GoalkeeperTrainer.Train("t", samples, Template());

        Assert.All(result.Zones, zone => Assert.InRange(zone.Probability, 0.02, 0.98));
    }

    [Fact]
    public void 单格样本足够多时明显压过先验()
    {
        // 左低：30 条里扑出 27 → 观测率 0.9。先验给 0.17，真实贝叶斯不会一步跳到 0.9，
        // 但样本足够多时必须显著高于先验、且接近观测率。
        var samples = new List<KeeperShotSample>();
        for (var i = 0; i < 30; i++) samples.Add(Shot(-3.4, 0.2, i < 27, $"lowleft{i}"));
        for (var i = 0; i < 2; i++) samples.Add(Shot(0.0, 1.2, false, "center"));
        for (var i = 0; i < 2; i++) samples.Add(Shot(3.4, 2.2, false, "highright"));

        var result = GoalkeeperTrainer.Train("t", samples, Template());
        var left = result.Zones.Single(zone => zone.Key == "LowLeft");

        Assert.Equal(30, left.Samples);
        Assert.True(left.Probability > 0.7, $"左低应明显高于先验 0.17，实际 {left.Probability:F3}");
        Assert.True(left.Probability < 0.9, $"30 个样本仍不足以认下 0.9，实际 {left.Probability:F3}");
    }

    [Fact]
    public void 空区域来自真实先验而不是零_纵向不互相污染()
    {
        var samples = Enumerable.Range(0, 20)
            .Select(i => Shot(-3.4, 0.2, true, $"c{i}"))
            .ToArray();

        var result = GoalkeeperTrainer.Train("t", samples, Template());
        var empty = result.Zones.Where(zone => zone.Samples == 0).ToArray();

        Assert.NotEmpty(empty);
        // 没有样本的区域不该是 0，而应落在真实先验上。
        Assert.All(empty, zone => Assert.True(zone.Probability > 0.01,
            $"{zone.Key} 不该被填成接近 0，实际 {zone.Probability:F3}"));

        // 纵向是物理台阶：贴着横梁的球真实数据 62 脚 0 扑救，
        // 绝不能因为中段（门将吃饭区）概率高就被平滑上来。
        var high = result.Zones.Where(zone => zone.Row == 2).Average(zone => zone.Probability);
        var mid = result.Zones.Where(zone => zone.Row == 1).Average(zone => zone.Probability);
        Assert.True(high < 0.1, $"高球区域应低于 0.1，实际均值 {high:F3}");
        Assert.True(mid > high * 2, $"中段 {mid:F3} 应显著高于高球 {high:F3}");
    }

    [Fact]
    public void 左右倾向符号跟随数据()
    {
        var rightStrong = new List<KeeperShotSample>();
        for (var i = 0; i < 20; i++) rightStrong.Add(Shot(3.0, 1.0, i < 16, "r"));
        for (var i = 0; i < 20; i++) rightStrong.Add(Shot(-3.0, 1.0, i < 4, "l"));

        var result = GoalkeeperTrainer.Train("t", rightStrong, Template());

        Assert.True(result.SideIndex > 0, $"右侧更强时应为正，实际 {result.SideIndex:F2}");
    }

    [Fact]
    public void 高低倾向符号跟随数据()
    {
        var highStrong = new List<KeeperShotSample>();
        for (var i = 0; i < 20; i++) highStrong.Add(Shot(0.0, 2.2, i < 16, "h"));
        for (var i = 0; i < 20; i++) highStrong.Add(Shot(0.0, 0.2, i < 4, "l"));

        var result = GoalkeeperTrainer.Train("t", highStrong, Template());

        Assert.True(result.HeightIndex > 0, $"高球更强时应为正，实际 {result.HeightIndex:F2}");
    }

    [Fact]
    public void 身体参数继承模板而偏好归零()
    {
        var result = GoalkeeperTrainer.Train("新建", [Shot(0.0, 1.0, true)], Template());

        Assert.Equal(1.92f, result.Profile.Height);
        Assert.Equal(4.4f, result.Profile.DiveSpeed);
        Assert.Equal(0f, result.Profile.SidePreference);
        Assert.Equal(0f, result.Profile.HeightPreference);
        Assert.Equal("新建", result.Profile.Name);
    }

    [Fact]
    public void 综合能力随扑救率上升()
    {
        var weak = Enumerable.Range(0, 40).Select(i => Shot(0.0, 1.0, i < 4, $"w{i}")).ToArray();
        var strong = Enumerable.Range(0, 40).Select(i => Shot(0.0, 1.0, i < 32, $"s{i}")).ToArray();

        Assert.True(
            GoalkeeperTrainer.Train("w", weak, Template()).EstimatedGoalKeeping <
            GoalkeeperTrainer.Train("s", strong, Template()).EstimatedGoalKeeping);
    }

    [Fact]
    public void 样本数决定置信度分级()
    {
        KeeperShotSample[] Many(int count) =>
            Enumerable.Range(0, count).Select(i => Shot(-2.0 + i % 5, 0.4 * (i % 3), i % 2 == 0, $"c{i}")).ToArray();

        Assert.Equal(TrainingConfidence.High, GoalkeeperTrainer.Train("a", Many(200), Template()).Confidence);
        Assert.Equal(TrainingConfidence.Medium, GoalkeeperTrainer.Train("b", Many(80), Template()).Confidence);
        Assert.Equal(TrainingConfidence.Low, GoalkeeperTrainer.Train("c", Many(30), Template()).Confidence);
        Assert.Equal(TrainingConfidence.Insufficient, GoalkeeperTrainer.Train("d", Many(10), Template()).Confidence);
    }

    [Fact]
    public void 训练结果写出的模型文件可序列化并读回()
    {
        var result = GoalkeeperTrainer.Train("GK_测试",
            [Shot(-3.0, 0.3, true), Shot(2.0, 1.8, false)], Template());

        var document = GoalkeeperTrainer.ToDocument(result, "clips/*.mp4");
        var json = System.Text.Json.JsonSerializer.Serialize(document);
        var restored = System.Text.Json.JsonSerializer.Deserialize<GoalkeeperModelDocument>(json)!;

        Assert.Equal(GoalkeeperModelDocument.CurrentVersion, restored.SchemaVersion);
        Assert.Equal(2, restored.ShotCount);
        Assert.Equal(15, restored.ZoneSamples.Length);
        Assert.Equal(15, restored.Profile.ProbabilityMap.Count);
        Assert.Equal("GK_测试", restored.Profile.Name);
    }

    [Fact]
    public void 区域索引覆盖整块球门()
    {
        Assert.Equal((0, 0), GoalkeeperTrainer.ZoneIndex(-3.66, 0));
        Assert.Equal((0, 4), GoalkeeperTrainer.ZoneIndex(3.66, 0));
        Assert.Equal((2, 0), GoalkeeperTrainer.ZoneIndex(-3.66, 2.44));
        Assert.Equal((2, 4), GoalkeeperTrainer.ZoneIndex(3.66, 2.44));
        Assert.Equal((1, 2), GoalkeeperTrainer.ZoneIndex(0, 1.22));
    }

    [Fact]
    public void 区域索引与几何模型一致()
    {
        foreach (var (row, column) in new[] { (0, 0), (0, 4), (1, 2), (2, 0), (2, 4) })
        {
            var center = GoalGeometry.ZoneCenter(row, column);
            Assert.Equal((row, column), GoalkeeperTrainer.ZoneIndex(center.Lateral, center.Height));
        }
    }
}
