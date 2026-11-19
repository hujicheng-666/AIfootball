using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

public class StatsBombPenaltyImporterTests
{
    // 取自 StatsBomb open-data 的 2022 世界杯决赛字段形态（数值已核对）：
    // end_location 的 y 以 40 码为球门中心，z 为穿过门线的高度（码）。
    private const string EventsJson = """
    [
      {"type":{"name":"Starting XI"},"team":{"name":"Argentina"},
       "tactics":{"lineup":[
         {"player":{"name":"Emiliano Martinez"},"position":{"name":"Goalkeeper"}},
         {"player":{"name":"Lionel Messi"},"position":{"name":"Center Forward"}}]}},
      {"type":{"name":"Starting XI"},"team":{"name":"France"},
       "tactics":{"lineup":[
         {"player":{"name":"Hugo Lloris"},"position":{"name":"Goalkeeper"}}]}},
      {"type":{"name":"Shot"},"period":1,"player":{"name":"Lionel Messi"},"team":{"name":"Argentina"},
       "shot":{"type":{"name":"Penalty"},"outcome":{"name":"Goal"},"end_location":[120.0,41.8,0.2]}},
      {"type":{"name":"Shot"},"period":5,"player":{"name":"Kingsley Coman"},"team":{"name":"France"},
       "shot":{"type":{"name":"Penalty"},"outcome":{"name":"Saved"},"end_location":[118.8,38.2,0.9]}},
      {"type":{"name":"Shot"},"period":5,"player":{"name":"Aurelien Tchouameni"},"team":{"name":"France"},
       "shot":{"type":{"name":"Penalty"},"outcome":{"name":"Off T"},"end_location":[120.0,35.6,0.2]}},
      {"type":{"name":"Shot"},"period":2,"player":{"name":"Someone"},"team":{"name":"Argentina"},
       "shot":{"type":{"name":"Open Play"},"outcome":{"name":"Goal"},"end_location":[120.0,40.0,1.0]}},
      {"type":{"name":"Shot"},"period":4,"player":{"name":"Kylian Mbappe"},"team":{"name":"France"},
       "shot":{"type":{"name":"Penalty"},"outcome":{"name":"Goal"}}}
    ]
    """;

    [Fact]
    public void 只保留点球事件()
    {
        var parsed = StatsBombPenaltyImporter.ParseEvents(EventsJson, 3869685);
        Assert.Equal(3, parsed.Penalties.Count);
    }

    [Fact]
    public void 缺少落点的点球被计数而不是静默丢弃()
    {
        var parsed = StatsBombPenaltyImporter.ParseEvents(EventsJson, 3869685);
        Assert.Equal(1, parsed.MissingLocation);
    }

    [Fact]
    public void 码制落点换算成米()
    {
        var messi = StatsBombPenaltyImporter.ParseEvents(EventsJson)
            .Penalties.Single(penalty => penalty.Shooter == "Lionel Messi");

        Assert.Equal((41.8 - 40.0) * 0.9144, messi.Lateral, 3);
        Assert.Equal(0.2 * 0.9144, messi.Height, 3);
        Assert.True(messi.OnTarget);
        Assert.False(messi.Saved);
    }

    [Fact]
    public void 被扑出的球判为扑救()
    {
        var coman = StatsBombPenaltyImporter.ParseEvents(EventsJson)
            .Penalties.Single(penalty => penalty.Shooter == "Kingsley Coman");

        Assert.True(coman.Saved);
        Assert.True(coman.OnTarget);
        Assert.Equal((38.2 - 40.0) * 0.9144, coman.Lateral, 3);
    }

    [Fact]
    public void 偏出左门柱的球被标为射偏()
    {
        var tchouameni = StatsBombPenaltyImporter.ParseEvents(EventsJson)
            .Penalties.Single(penalty => penalty.Shooter == "Aurelien Tchouameni");

        Assert.False(tchouameni.OnTarget);
        Assert.True(Math.Abs(tchouameni.Lateral) > GoalGeometry.HalfWidth);
    }

    [Fact]
    public void 门将从对手首发阵容反推()
    {
        var penalties = StatsBombPenaltyImporter.ParseEvents(EventsJson).Penalties;

        Assert.Equal("Hugo Lloris", penalties.Single(p => p.Shooter == "Lionel Messi").Keeper);
        Assert.Equal("Emiliano Martinez", penalties.Single(p => p.Shooter == "Kingsley Coman").Keeper);
    }

    [Fact]
    public void 点球大战按场次标记()
    {
        var penalties = StatsBombPenaltyImporter.ParseEvents(EventsJson).Penalties;
        Assert.Equal(2, penalties.Count(penalty => penalty.IsShootout));
    }

    [Fact]
    public void 导入时跳过射偏的球并按门将过滤()
    {
        var summary = StatsBombPenaltyImporter.Import([(3869685, EventsJson)]);

        Assert.Equal(1, summary.FilesRead);
        Assert.Equal(3, summary.PenaltyEvents);
        Assert.Equal(1, summary.OffTarget);
        Assert.Equal(2, summary.UsableSamples);
        Assert.Contains("Hugo Lloris", summary.KeeperNames);
        Assert.Contains("Emiliano Martinez", summary.KeeperNames);

        var lorisSamples = StatsBombPenaltyImporter.Import([(3869685, EventsJson)], "Hugo Lloris");
        Assert.Single(lorisSamples.Samples);
        Assert.Contains("Messi", lorisSamples.Samples[0].Clip);
    }

    [Fact]
    public void 非法JSON不抛异常()
    {
        var parsed = StatsBombPenaltyImporter.ParseEvents("not json at all");
        Assert.Empty(parsed.Penalties);
    }

    /// <summary>
    /// 用一个真实的 StatsBomb 事件文件做端到端校验（2022 世界杯决赛，11 个点球）。
    /// 文件太大不适合入库，通过环境变量 AIFOOTBALL_STATSBOMB_FIXTURE 指过去；没有就跳过。
    /// </summary>
    [Fact]
    public void 真实事件文件能解析出全部点球()
    {
        var path = Environment.GetEnvironmentVariable("AIFOOTBALL_STATSBOMB_FIXTURE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        var json = File.ReadAllText(path);
        var parsed = StatsBombPenaltyImporter.ParseEvents(json, 3869685);

        Assert.Equal(11, parsed.Penalties.Count);
        Assert.Equal(0, parsed.MissingLocation);
        // 常规时间/加时 3 个点球，点球大战 8 个
        Assert.Equal(3, parsed.Penalties.Count(penalty => !penalty.IsShootout));
        Assert.Equal(8, parsed.Penalties.Count(penalty => penalty.IsShootout));
        Assert.Contains(parsed.Penalties, penalty => penalty.Shooter.Contains("Messi"));
        Assert.Contains(parsed.Penalties, penalty => penalty.Saved);

        // 决赛里 10 个点球射正、1 个偏出（楚阿梅尼打左门柱外）
        var onTarget = parsed.Penalties.Count(penalty => penalty.OnTarget);
        Assert.Equal(10, onTarget);
        Assert.All(parsed.Penalties, penalty => Assert.False(string.IsNullOrWhiteSpace(penalty.Keeper)));
    }
}

public class GoalkeeperTrainingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aifootball-gk-" + Guid.NewGuid().ToString("N"));

    public GoalkeeperTrainingStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 临时目录清理失败不影响结论 */ }
    }

    private static KeeperClipAnnotation Annotation(string clip, double lateral, double height, bool saved) => new()
    {
        Clip = clip,
        Lateral = lateral,
        Height = height,
        Saved = saved,
    };

    [Fact]
    public void 保存的标注能原样读回()
    {
        var store = new GoalkeeperTrainingStore(_root);
        store.SaveAnnotation("GK_A", Annotation("clip1", -3.0, 0.4, true));

        var samples = store.LoadSamples("GK_A");

        Assert.Single(samples);
        Assert.Equal(-3.0, samples[0].Lateral, 3);
        Assert.True(samples[0].Saved);
        Assert.Equal("clip1", samples[0].Clip);
        Assert.Equal("LowLeft", samples[0].ZoneKey);
    }

    [Fact]
    public void 重训会续写代数与进化史()
    {
        var store = new GoalkeeperTrainingStore(_root);
        for (var i = 0; i < 12; i++)
        {
            store.SaveAnnotation("GK_A", Annotation($"c{i}", -3.4, 0.2, i % 2 == 0));
        }

        var first = store.TrainAndSave("GK_A");
        Assert.Equal(1, first.Version);
        Assert.Single(first.History);

        for (var i = 12; i < 30; i++)
        {
            store.SaveAnnotation("GK_A", Annotation($"c{i}", 3.4, 2.2, i % 3 == 0));
        }

        var second = store.TrainAndSave("GK_A");
        Assert.Equal(2, second.Version);
        Assert.Equal(2, second.History.Count);
        Assert.Equal(12, second.History[0].ShotCount);
        Assert.Equal(30, second.History[1].ShotCount);
        Assert.Equal(30, second.ZoneSamples.Sum());
    }

    [Fact]
    public void 发布文件落在门将库里且可被几何模型读取()
    {
        var store = new GoalkeeperTrainingStore(_root);
        for (var i = 0; i < 20; i++) store.SaveAnnotation("GK_B", Annotation($"c{i}", 0.0, 1.2, i < 15));

        var document = store.TrainAndSave("GK_B");
        var path = store.Publish(document.Profile);

        Assert.True(File.Exists(path));
        Assert.Equal(Path.Combine(_root, "data", "goalkeepers", "GK_B.json"), path);

        var reloaded = System.Text.Json.JsonSerializer.Deserialize<GoalkeeperProfile>(File.ReadAllText(path))!;
        Assert.Equal(15, reloaded.ProbabilityMap.Count);
        Assert.All(GoalkeeperProfile.ZoneKeys, key => Assert.True(reloaded.ProbabilityMap.ContainsKey(key)));
        Assert.True(reloaded.ProbabilityMap["MidCenter"] > 0.5);
    }

    [Fact]
    public void 模型文件与发布文件分开存放()
    {
        var store = new GoalkeeperTrainingStore(_root);
        store.SaveAnnotation("GK_C", Annotation("c", 0, 1.0, true));
        store.TrainAndSave("GK_C");

        Assert.True(File.Exists(Path.Combine(_root, "data", "goalkeepers", "models", "GK_C.model.json")));
        // 发布前门将库里不应出现该文件，避免 Unity 读到没有概率表的半成品
        Assert.False(File.Exists(Path.Combine(_root, "data", "goalkeepers", "GK_C.json")));
    }
}
