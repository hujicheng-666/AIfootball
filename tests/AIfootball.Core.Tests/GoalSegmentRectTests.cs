using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

public sealed class GoalSegmentRectTests
{
    private static GoalRect Box(double cx = 640, double cy = 360, double w = 300, double h = 150) =>
        new() { Cx = cx, Cy = cy, W = w, H = h };

    [Fact]
    public void Corners_FromBox_AreClockwiseFromTopLeft()
    {
        var corners = Box().Corners();

        Assert.Equal(4, corners.Length);
        Assert.Equal((490.0, 285.0), corners[0]);
        Assert.Equal((790.0, 285.0), corners[1]);
        Assert.Equal((790.0, 435.0), corners[2]);
        Assert.Equal((490.0, 435.0), corners[3]);
    }

    [Fact]
    public void Corners_PreferQuadWhenPresent()
    {
        // 机位带倾斜时四点并不正交，包围盒会把门线压平，必须用原始四点
        var rect = Box();
        rect.Quad = [[100, 200], [400, 240], [400, 400], [100, 360]];

        var corners = rect.Corners();

        Assert.Equal((100.0, 200.0), corners[0]);
        Assert.Equal((400.0, 240.0), corners[1]);
        Assert.Equal((400.0, 400.0), corners[2]);
        Assert.Equal((100.0, 360.0), corners[3]);
    }

    [Fact]
    public void Corners_FallBackToBoxWhenQuadIsMalformed()
    {
        var rect = Box();
        rect.Quad = [[1, 2], [3, 4]];

        Assert.Equal((490.0, 285.0), rect.Corners()[0]);
    }

    [Fact]
    public void GoalLineY_IsMeanOfBottomCorners()
    {
        Assert.Equal(435.0, Box().GoalLineY);

        var tilted = Box();
        tilted.Quad = [[100, 200], [400, 240], [400, 400], [100, 360]];
        Assert.Equal(380.0, tilted.GoalLineY);
    }

    [Fact]
    public void BelowLinePx_PositiveWhenBallIsOnThisSideOfTheLine()
    {
        var rect = Box();

        // 相机在射手身后：球往画面上方走才是飞向球门，y 变小
        Assert.Equal(65.0, rect.BelowLinePx(0, 500));
        Assert.Equal(-35.0, rect.BelowLinePx(0, 400));
        Assert.Equal(0.0, rect.BelowLinePx(0, 435));
    }

    [Fact]
    public void IsUsable_RejectsTinyBoxes()
    {
        Assert.True(Box().IsUsable);
        Assert.False(Box(w: 20, h: 20).IsUsable);
        Assert.Null(GoalSegmentRect.FromRect(Box(w: 10, h: 10), 0, 1).UsableRect);
    }

    [Fact]
    public void ToCalibration_MapsRectCornersOntoGoalFrame()
    {
        var segment = GoalSegmentRect.FromRect(Box(), 10, 12);

        var calibration = segment.ToCalibration(1280, 720);

        Assert.NotNull(calibration);
        Assert.Equal(1280, calibration!.VideoWidth);
        Assert.Equal(720, calibration.VideoHeight);
        Assert.Equal((490.0, 285.0), calibration.ImagePoints()[0]);
        Assert.Equal((790.0, 285.0), calibration.ImagePoints()[1]);
        Assert.Equal((790.0, 435.0), calibration.ImagePoints()[2]);
        Assert.Equal((490.0, 435.0), calibration.ImagePoints()[3]);
        Assert.True(calibration.IsComplete);
    }

    [Fact]
    public void ToCalibration_RejectsMissingVideoSizeOrTinyRect()
    {
        var segment = GoalSegmentRect.FromRect(Box(), 0, 1);

        Assert.Null(segment.ToCalibration(0, 720));
        Assert.Null(segment.ToCalibration(1280, 0));
        Assert.Null(GoalSegmentRect.FromRect(Box(w: 8, h: 8), 0, 1).ToCalibration(1280, 720));
    }

    [Fact]
    public void ToCalibration_RoundTripsThroughHomography()
    {
        // 门框四角应该正好落在球门平面的四个角上，这是自动算落点的前提
        var segment = GoalSegmentRect.FromRect(Box(), 0, 1);
        var calibration = segment.ToCalibration(1280, 720)!;
        var homography = Homography.Solve(calibration.ImagePoints(), GoalFrameCalibration.GoalPoints());

        Assert.NotNull(homography);

        var topLeft = homography!.Map(calibration.LeftTopX, calibration.LeftTopY);
        var bottomRight = homography.Map(calibration.RightBottomX, calibration.RightBottomY);

        Assert.NotNull(topLeft);
        Assert.NotNull(bottomRight);
        Assert.Equal(-GoalGeometry.HalfWidth, topLeft!.Value.X, 2);
        Assert.Equal(GoalGeometry.Height, topLeft.Value.Y, 2);
        Assert.Equal(GoalGeometry.HalfWidth, bottomRight!.Value.X, 2);
        Assert.Equal(0.0, bottomRight.Value.Y, 2);
    }

    [Fact]
    public void ToCalibration_BallAboveTheLineMapsAboveTheGround()
    {
        // 球越过门线越深，映射出来的高度应该越高——这是"过没过线"能算出来的原因
        var segment = GoalSegmentRect.FromRect(Box(), 0, 1);
        var calibration = segment.ToCalibration(1280, 720)!;
        var homography = Homography.Solve(calibration.ImagePoints(), GoalFrameCalibration.GoalPoints())!;

        var onLine = homography.Map(640, 435);
        var inside = homography.Map(640, 400);

        Assert.NotNull(onLine);
        Assert.NotNull(inside);
        Assert.Equal(0.0, onLine!.Value.Y, 2);
        Assert.True(inside!.Value.Y > onLine.Value.Y);
    }

    [Fact]
    public void FromRect_MarksSingleFrameSegmentAsTrusted()
    {
        var segment = GoalSegmentRect.FromRect(Box(), 5, 5);

        Assert.Equal(-1, segment.Seg);
        Assert.Equal(1, segment.Frames);
        Assert.Equal(1, segment.Found);
        Assert.True(segment.Trusted);
        Assert.True(segment.Contains(5));
    }

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gsr_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Store_Load_ParsesRealShapeAndSortsByStart()
    {
        const string json = """
        [
          {"seg":7,"t0":30.0,"t1":33.2,"frames":81,"found":80,
           "median":{"cx":666.0,"cy":279.0,"w":672.0,"h":220.0},"trusted":true},
          {"seg":0,"t0":0.0,"t1":4.8,"frames":120,"found":118,
           "median":{"cx":845.0,"cy":330.0,"w":298.0,"h":144.0},"trusted":true}
        ]
        """;
        var path = WriteTemp(json);
        try
        {
            var segments = GoalSegmentRectStore.Load(path);

            Assert.Equal(2, segments.Count);
            Assert.Equal(0, segments[0].Seg);
            Assert.Equal(7, segments[1].Seg);
            Assert.Equal(298.0, segments[0].Median!.W);
            Assert.Equal(402.0, segments[0].GoalLineY);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Store_Load_MissingOrBrokenFileReturnsEmpty()
    {
        Assert.Empty(GoalSegmentRectStore.Load(Path.Combine(Path.GetTempPath(), "nope_gsr.json")));
        Assert.Empty(GoalSegmentRectStore.Load(""));

        var path = WriteTemp("{ not json");
        try
        {
            Assert.Empty(GoalSegmentRectStore.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<GoalSegmentRect> Segments() =>
    [
        GoalSegmentRect.FromRect(Box(cx: 100), 0, 4),
        GoalSegmentRect.FromRect(Box(cx: 200), 10, 14),
    ];

    [Fact]
    public void Store_FindAt_ReturnsSegmentCoveringTime()
    {
        var segments = Segments();

        Assert.Equal(100.0, GoalSegmentRectStore.FindAt(segments, 2.0)!.Median!.Cx);
        Assert.Equal(200.0, GoalSegmentRectStore.FindAt(segments, 14.0)!.Median!.Cx);
    }

    [Fact]
    public void Store_FindAt_ToleratesSmallGapSoTheBoxDoesNotFlicker()
    {
        var segments = Segments();

        // 4.0~10.0 之间是空隙；近距离沿用相邻段，远距离就当作没有
        Assert.Equal(100.0, GoalSegmentRectStore.FindAt(segments, 4.2)!.Median!.Cx);
        Assert.NotNull(GoalSegmentRectStore.FindAt(segments, 10.2));
        Assert.Null(GoalSegmentRectStore.FindAt(segments, 7.0));
        Assert.Null(GoalSegmentRectStore.FindAt(segments, 20.0));
    }

    [Fact]
    public void Store_FindAt_SkipsUntrustedAndBareSegments()
    {
        var segments = Segments();
        segments[0].Trusted = false;
        segments[1].Median = null;

        Assert.Null(GoalSegmentRectStore.FindAt(segments, 2.0));
        Assert.Null(GoalSegmentRectStore.FindAt(segments, 12.0));
        Assert.Null(GoalSegmentRectStore.FindAt(new List<GoalSegmentRect>(), 1.0));
    }
}
