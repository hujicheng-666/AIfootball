using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

/// <summary>
/// 门框标定是**逐脚**的：一脚一套，没标过的脚次没有门框。
///
/// 点球合集是多机位混剪，相邻两脚就可能换机位（实测同一段视频里门框中心的 x
/// 在 206~948 之间跳）。任何"沿用上一脚/上一段镜头"的做法都会让落点错几十厘米，
/// 所以这里最核心的一条是：**没标过的脚次必须返回 null，而不是退到别脚的标定**。
/// </summary>
public sealed class VideoCalibrationPerShotTests : IDisposable
{
    private const string Keeper = "GK_Test";
    private const string Video = @"D:\videos\Courtois_85_penalties.mp4";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "aifootball-calib-" + Guid.NewGuid().ToString("N")[..8]);

    public VideoCalibrationPerShotTests() => Directory.CreateDirectory(_root);

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

    /// <summary>造一套"宽度固定、左上角随 offset 平移"的标定，便于比较取到的是哪一脚的。</summary>
    private static GoalFrameCalibration Calibration(double offset) => new()
    {
        VideoWidth = 1280,
        VideoHeight = 720,
        LeftTopX = 400 + offset, LeftTopY = 300,
        RightTopX = 900 + offset, RightTopY = 300,
        RightBottomX = 900 + offset, RightBottomY = 600,
        LeftBottomX = 400 + offset, LeftBottomY = 600,
    };

    [Fact]
    public void 每一脚各自一套标定互不串用()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 0, Calibration(0));
        store.SaveVideoCalibration(Keeper, Video, 5.48, Calibration(300));
        store.SaveVideoCalibration(Keeper, Video, 8.99, Calibration(-150));

        Assert.Equal(400, store.ResolveCalibration(Keeper, Video, 0)!.LeftTopX);
        Assert.Equal(700, store.ResolveCalibration(Keeper, Video, 5.48)!.LeftTopX);
        Assert.Equal(250, store.ResolveCalibration(Keeper, Video, 8.99)!.LeftTopX);
    }

    [Fact]
    public void 没标过的脚次就是没有门框而不是沿用上一脚()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 5.48, Calibration(300));

        // 第 1 脚（0s）没标过 —— 必须返回 null，不能拿 5.48s 那套顶上
        Assert.Null(store.ResolveCalibration(Keeper, Video, 0));
        Assert.Null(store.ResolveCalibration(Keeper, Video, 20.5));
        Assert.NotNull(store.ResolveCalibration(Keeper, Video, 5.48));
    }

    [Fact]
    public void 同一脚重标是覆盖而不是追加()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 5.48, Calibration(0));
        store.SaveVideoCalibration(Keeper, Video, 12.0, Calibration(300));
        // 用户在第 1 脚里挪了几帧，觉得点歪了重标
        store.SaveVideoCalibration(Keeper, Video, 5.5, Calibration(50));

        var document = store.LoadVideoCalibrations(Keeper, Video)!;
        Assert.Equal(2, document.Shots.Count);
        Assert.Equal(450, store.ResolveCalibration(Keeper, Video, 5.48)!.LeftTopX);
        Assert.Equal(700, store.ResolveCalibration(Keeper, Video, 12.0)!.LeftTopX);
    }

    [Fact]
    public void 相邻两脚起点接近时不会互相覆盖()
    {
        var document = new VideoCalibrationDocument();
        // 相差 0.3 秒：两脚都是真实存在的脚次，必须各自保留
        document.Set(10.0, Calibration(0));
        document.Set(10.3, Calibration(300));

        Assert.Equal(2, document.Count);
        Assert.Equal(400, document.ForShot(10.0)!.LeftTopX);
        Assert.Equal(700, document.ForShot(10.3)!.LeftTopX);
    }

    [Fact]
    public void 逐脚标定能存盘并原样读回()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 0, Calibration(0));
        store.SaveVideoCalibration(Keeper, Video, 88.5, Calibration(-150));

        // 换一个 store 实例读，确认不是内存里的假象
        var reloaded = new GoalkeeperTrainingStore(_root);
        var document = reloaded.LoadVideoCalibrations(Keeper, Video)!;

        Assert.Equal(2, document.Count);
        Assert.Equal(88.5, document.Shots[1].ShotStart, 3);
        Assert.Equal(250, reloaded.ResolveCalibration(Keeper, Video, 88.5)!.LeftTopX);
        Assert.Equal("Courtois_85_penalties", document.Video);
    }

    [Fact]
    public void 没有脚次表的单片段视频用零秒作为键()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 0, Calibration(0));

        // 单片段视频整段算一脚，键固定 0，任何时候都能取到
        Assert.NotNull(store.ResolveCalibration(Keeper, Video, 0));
        Assert.Equal(400, store.ResolveCalibration(Keeper, Video, 0)!.LeftTopX);
    }

    [Fact]
    public void 完全没标过的视频返回空()
    {
        var store = Store;
        Assert.Null(store.LoadVideoCalibrations(Keeper, Video));
        Assert.Null(store.ResolveCalibration(Keeper, Video, 0));
    }

    [Fact]
    public void 删掉一脚的标定后这一脚就没有门框而别的脚不受影响()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 0, Calibration(0));
        store.SaveVideoCalibration(Keeper, Video, 5.48, Calibration(300));
        store.SaveVideoCalibration(Keeper, Video, 8.99, Calibration(-150));

        Assert.True(store.DeleteVideoCalibration(Keeper, Video, 5.48));

        // 标错的那一脚清干净了，不能退到相邻脚去顶
        Assert.Null(store.ResolveCalibration(Keeper, Video, 5.48));
        Assert.Equal(2, store.LoadVideoCalibrations(Keeper, Video)!.Count);
        Assert.Equal(400, store.ResolveCalibration(Keeper, Video, 0)!.LeftTopX);
        Assert.Equal(250, store.ResolveCalibration(Keeper, Video, 8.99)!.LeftTopX);
    }

    [Fact]
    public void 重复删同一脚返回false且不动别的脚()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 5.48, Calibration(300));

        Assert.True(store.DeleteVideoCalibration(Keeper, Video, 5.48));
        // 第二次已经没有可删的了 —— 界面上要能区分"真删掉了"和"本来就没有"
        Assert.False(store.DeleteVideoCalibration(Keeper, Video, 5.48));
        Assert.Null(store.LoadVideoCalibrations(Keeper, Video));
    }

    [Fact]
    public void 删光所有脚次后不留下空壳文件()
    {
        var store = Store;
        store.SaveVideoCalibration(Keeper, Video, 0, Calibration(0));
        store.SaveVideoCalibration(Keeper, Video, 5.48, Calibration(300));

        store.DeleteVideoCalibration(Keeper, Video, 0);
        store.DeleteVideoCalibration(Keeper, Video, 5.48);

        // 空壳 json 会让"已标 N 脚"和"这一脚还没标"两处判断都跑偏
        Assert.Null(store.LoadVideoCalibrations(Keeper, Video));
        var path = Path.Combine(store.CalibrationsDirectory(Keeper), "Courtois_85_penalties.json");
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void 删除时同脚容差内的时间点算同一脚()
    {
        var document = new VideoCalibrationDocument();
        document.Set(10.0, Calibration(0));

        // 用户在第 1 脚里挪了几帧再点清除，仍然应该清掉这一脚
        Assert.True(document.Remove(10.05));
        Assert.Equal(0, document.Count);
    }

    [Fact]
    public void 标定会记住是在哪一帧点的()
    {
        var store = Store;
        var calibration = Calibration(0);
        calibration.Frame = 61;
        calibration.FrameSeconds = 2.44;
        store.SaveVideoCalibration(Keeper, Video, 0, calibration);

        // 换实例读，确认帧号真的落盘了——橙框只在这一帧画，靠的就是它
        var reloaded = new GoalkeeperTrainingStore(_root).ResolveCalibration(Keeper, Video, 0)!;
        Assert.True(reloaded.HasFrame);
        Assert.Equal(61, reloaded.Frame);
        Assert.Equal(2.44, reloaded.FrameSeconds, 3);
    }

    [Fact]
    public void 老数据没有帧号时HasFrame为假()
    {
        // 2026-10-24 之前存的标定没有 Frame/FrameSeconds，默认 -1
        var calibration = Calibration(0);
        Assert.Equal(-1, calibration.Frame);
        Assert.Equal(-1, calibration.FrameSeconds);
        Assert.False(calibration.HasFrame);
    }
}
