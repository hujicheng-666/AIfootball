using AIfootball.Core.Models;

namespace AIfootball.Core.Tests;

/// <summary>
/// 球门几何：区域划分必须与 Unity 侧 ProbabilityMap 的 3×5 键完全对应，
/// 否则热区图与门将模型会对不上号。
/// </summary>
public sealed class GoalGeometryTests
{
    [Theory]
    [InlineData(0.0, 1.22, "MidCenter")]      // 球门正中
    [InlineData(-3.60, 0.10, "LowLeft")]      // 左下死角
    [InlineData(3.60, 2.40, "HighRight")]     // 右上死角
    [InlineData(-1.5, 0.4, "LowMidLeft")]
    [InlineData(1.5, 0.4, "LowMidRight")]
    [InlineData(0.0, 0.4, "LowCenter")]
    [InlineData(-3.0, 2.0, "HighLeft")]
    public void ZoneKey_MapsGoalLinePointToExpectedZone(double lateral, double height, string expected) =>
        Assert.Equal(expected, GoalGeometry.ZoneKey(lateral, height));

    [Theory]
    [InlineData(-99.0, -5.0, "LowLeft")]      // 越界要夹取，不能抛
    [InlineData(99.0, 5.0, "HighRight")]
    [InlineData(0.0, -0.1, "LowCenter")]
    public void ZoneKey_ClampsOutOfRangeInput(double lateral, double height, string expected) =>
        Assert.Equal(expected, GoalGeometry.ZoneKey(lateral, height));

    [Fact]
    public void ZoneCenter_RoundTripsThroughZoneKey()
    {
        for (var row = 0; row < GoalGeometry.Rows.Length; row++)
        {
            for (var column = 0; column < GoalGeometry.Columns.Length; column++)
            {
                var (lateral, height) = GoalGeometry.ZoneCenter(row, column);
                Assert.Equal(GoalGeometry.Rows[row] + GoalGeometry.Columns[column],
                             GoalGeometry.ZoneKey(lateral, height));
            }
        }
    }

    [Fact]
    public void ZoneCenter_SpansTheGoalMouth()
    {
        var first = GoalGeometry.ZoneCenter(0, 0);
        var second = GoalGeometry.ZoneCenter(0, 1);
        var last = GoalGeometry.ZoneCenter(0, GoalGeometry.Columns.Length - 1);

        Assert.True(first.Lateral > -GoalGeometry.HalfWidth);
        Assert.True(last.Lateral < GoalGeometry.HalfWidth);
        Assert.True(first.Lateral < last.Lateral);
        // 相邻区域中心的间距 = 球门宽度的 1/5
        Assert.Equal(GoalGeometry.HalfWidth * 2 / 5, second.Lateral - first.Lateral, 6);
    }

    [Fact]
    public void Constants_MatchRealGoal() 
    {
        Assert.Equal(3.66, GoalGeometry.HalfWidth, 6);
        Assert.Equal(2.44, GoalGeometry.Height, 6);
        Assert.Equal(11.0, GoalGeometry.PenaltyDistance, 6);
    }

    [Theory]
    [InlineData(0.0, 1.0, true)]
    [InlineData(-3.66, 0.0, true)]       // 门柱内侧算射正
    [InlineData(3.66, 2.44, true)]
    [InlineData(-3.67, 1.0, false)]      // 偏出左侧
    [InlineData(3.67, 1.0, false)]
    [InlineData(0.0, 2.45, false)]       // 高出横梁
    [InlineData(0.0, -0.01, false)]      // 打在地上
    public void ShotPlacement_OnTargetMatchesGoalMouth(double lateral, double height, bool expected)
    {
        var shot = new ShotPlacement("sample1", lateral, height, 0.5, 25.0);
        Assert.Equal(expected, shot.OnTarget);
    }

    [Fact]
    public void ShotPlacement_ZoneKeyDelegatesToGoalGeometry()
    {
        var shot = new ShotPlacement("sample2", -3.0, 2.2, 0.62, 27.5);

        Assert.Equal(GoalGeometry.ZoneKey(-3.0, 2.2), shot.ZoneKey);
        Assert.Equal("HighLeft", shot.ZoneKey);
    }
}
