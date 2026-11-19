using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

/// <summary>
/// 合集视频的标注落盘规则：一整段视频里有多脚点球，必须一脚一条地存下来，
/// 而不能后一脚覆盖前一脚；每一条还要带上当时的门框快照，换机位后仍可追溯。
/// </summary>
public sealed class GoalkeeperTrainingStoreClipTests : IDisposable
{
    private const string Keeper = "Thibaut Courtois";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aifootball-clip-" + Guid.NewGuid().ToString("N")[..8]);

    public GoalkeeperTrainingStoreClipTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论
        }
    }

    private GoalkeeperTrainingStore Store => new(_root);

    [Fact]
    public void 合集视频的每一脚都以秒数区分并全部保留()
    {
        var store = Store;
        var shots = new[] { (Seconds: 12.0, Lateral: -3.1, Saved: true),
                            (Seconds: 48.0, Lateral: 2.4, Saved: false),
                            (Seconds: 95.0, Lateral: 0.3, Saved: true) };

        foreach (var shot in shots)
        {
            store.SaveAnnotation(Keeper, new KeeperClipAnnotation
            {
                Clip = $"Courtois_85 @ {shot.Seconds:0}s",
                Lateral = shot.Lateral,
                Height = 0.42,
                Saved = shot.Saved,
                VideoSeconds = shot.Seconds,
            });
        }

        var clips = store.ListAnnotatedClips(Keeper);
        Assert.Equal(3, clips.Count);
        Assert.Contains("Courtois_85 @ 12s", clips);
        Assert.Contains("Courtois_85 @ 48s", clips);
        Assert.Contains("Courtois_85 @ 95s", clips);

        var samples = store.LoadSamples(Keeper);
        Assert.Equal(3, samples.Count);
        Assert.Equal(2, samples.Count(sample => sample.Saved));
        Assert.All(samples, sample => Assert.True(sample.OnTarget));
    }

    [Fact]
    public void 每一条标注都带着当时的门框快照()
    {
        var store = Store;
        var calibration = new GoalFrameCalibration
        {
            VideoWidth = 1280,
            VideoHeight = 720,
            LeftTopX = 420, LeftTopY = 300,
            RightTopX = 860, RightTopY = 300,
            RightBottomX = 860, RightBottomY = 560,
            LeftBottomX = 420, LeftBottomY = 560,
        };

        store.SaveAnnotation(Keeper, new KeeperClipAnnotation
        {
            Clip = "Courtois_85 @ 7s",
            Lateral = -1.8,
            Height = 0.2,
            Saved = true,
            VideoSeconds = 7,
            Calibration = calibration,
        });

        var loaded = store.LoadAnnotation(Keeper, "Courtois_85 @ 7s");
        Assert.NotNull(loaded);
        Assert.NotNull(loaded!.Calibration);
        Assert.True(loaded.Calibration!.IsComplete);
        Assert.Equal(1280, loaded.Calibration.VideoWidth);
        Assert.Equal(420, loaded.Calibration.LeftTopX);
        Assert.Equal(560, loaded.Calibration.LeftBottomY);
    }

    [Fact]
    public void 单片段的老文件名仍然去掉扩展名()
    {
        var store = Store;
        store.SaveAnnotation(Keeper, new KeeperClipAnnotation
        {
            Clip = "clip1.mp4",
            Lateral = 1.0,
            Height = 0.5,
            Saved = true,
        });

        var clips = store.ListAnnotatedClips(Keeper);
        Assert.Equal(["clip1"], clips);
    }
}
