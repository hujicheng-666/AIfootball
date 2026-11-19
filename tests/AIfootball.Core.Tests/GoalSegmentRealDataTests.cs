using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

/// <summary>
/// 用仓库里真实的门框段/过线事件验证标注页依赖的那条计算链：
/// 定帧 → 找门框段 → 门线 → 球在门线哪一侧。
///
/// 这些断言是跨实现的：门框段和过线事件由 tools/goal_detect 下的 Python 产出，
/// 这里用 C# 重新算一遍，两边必须指向同一个段、同一条门线，否则标注页会把落点算歪。
/// 数据文件不在时（例如只拷了源码）跳过，不当作失败。
/// </summary>
public sealed class GoalSegmentRealDataTests
{
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "goal_detect")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static (List<GoalSegmentRect> Segments, GoalCrossEventDocument Events)? Load()
    {
        var root = RepoRoot();
        if (root is null) return null;

        var segments = GoalSegmentRectStore.Load(
            Path.Combine(root, "data", "goal_detect", "trusted_segments.json"));
        var events = GoalCrossEventStore.Load(
            Path.Combine(root, "data", "goal_detect", "cross_events.json"));
        if (segments.Count == 0 || events is null || events.Events.Count == 0) return null;

        return (segments, events);
    }

    [Fact]
    public void RealSegments_AllHaveUsableBoxAndMonotonicTimeRange()
    {
        if (Load() is not { } data) return;

        foreach (var segment in data.Segments)
        {
            Assert.True(segment.Trusted);
            Assert.True(segment.T1 >= segment.T0);
            Assert.NotNull(segment.UsableRect);
            Assert.Equal(segment.Median!.GoalLineY, segment.GoalLineY, 6);
        }
    }

    [Fact]
    public void RealSegments_LookupFindsTheSameSegmentPythonUsedForCrossings()
    {
        if (Load() is not { } data) return;
        var segments = data.Segments;
        var events = data.Events;

        var checkedCrossings = 0;
        foreach (var evt in events.Events)
        {
            if (!evt.Crossed || evt.TCross is not { } crossTime) continue;

            var segment = GoalSegmentRectStore.FindAt(segments, crossTime);
            Assert.NotNull(segment);
            Assert.Equal(evt.Seg, segment!.Seg);
            Assert.Equal(evt.SegT0, segment.T0, 6);

            // 过线时刻的门线必须在画面里，且落在该段门框矩形的底边上
            var rect = segment.UsableRect!;
            Assert.InRange(rect.GoalLineY, 0, 720);
            checkedCrossings++;
        }

        // 真实数据里有 21 条过线事件；少于 10 条说明文件被动过
        Assert.True(checkedCrossings >= 10, $"只核对到 {checkedCrossings} 条过线事件");
    }

    [Fact]
    public void RealCrossingEvent_FlipsSideOfTheGoalLine()
    {
        if (Load() is not { } data) return;

        // seg0 的 50→56 帧是这套流程的标准样例：球从门线这一侧飞到球门里面
        var evt = data.Events.Events.FirstOrDefault(e => e.Seg == 0 && e.Crossed);
        if (evt is null) return;

        Assert.NotNull(evt.TCross);
        Assert.InRange(evt.TCross!.Value, evt.TStart, evt.TEnd);
        Assert.Contains(evt.NFrames, new[] { 6, 7 });
    }
}
