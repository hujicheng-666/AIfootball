namespace AIfootball.Core.Models;

/// <summary>
/// Unity 回放轨迹中的一个采样点。
/// 坐标已按 Unity 约定：<c>X=向前(离门方向)</c>、<c>Y=门将右手侧</c>、<c>Z=向上</c>，单位米。
/// </summary>
public readonly record struct TrajectoryPoint(double Time, double Forward, double Lateral, double Height);

/// <summary>一条可回放轨迹及其派生指标。</summary>
public sealed class Trajectory
{
    public required string SampleName { get; init; }
    public required IReadOnlyList<TrajectoryPoint> Points { get; init; }
    public string SourcePath { get; init; } = "";

    public bool IsEmpty => Points.Count == 0;

    public double Duration => Points.Count == 0 ? 0 : Points[^1].Time - Points[0].Time;

    /// <summary>出手速度：前若干帧的平均速度（避开终点附近的数值抖动）。</summary>
    public double LaunchSpeed
    {
        get
        {
            if (Points.Count < 3) return 0;
            var span = Math.Min(Points.Count - 1, 10);
            return SegmentSpeed(0, span);
        }
    }

    public double PeakSpeed
    {
        get
        {
            double peak = 0;
            for (var i = 1; i < Points.Count; i++)
                peak = Math.Max(peak, SegmentSpeed(i - 1, i));
            return peak;
        }
    }

    public double ApexHeight => Points.Count == 0 ? 0 : Points.Max(p => p.Height);

    /// <summary>球门线（Forward = 0）穿越点；未穿越返回 null。</summary>
    public (double Lateral, double Height, double Time)? GoalLineCrossing
    {
        get
        {
            for (var i = 1; i < Points.Count; i++)
            {
                var a = Points[i - 1];
                var b = Points[i];
                if (a.Forward > 0 && b.Forward <= 0)
                {
                    var t = a.Forward / (a.Forward - b.Forward);
                    return (a.Lateral + (b.Lateral - a.Lateral) * t,
                            a.Height + (b.Height - a.Height) * t,
                            a.Time + (b.Time - a.Time) * t);
                }
            }
            return null;
        }
    }

    /// <summary>以点球点（前进 11 m）为原点的水平偏移。</summary>
    public double LaunchLateral => Points.Count == 0 ? 0 : Points[0].Lateral;

    public double LaunchHeight => Points.Count == 0 ? 0 : Points[0].Height;

    /// <summary>轨迹投影到 XZ 平面（俯视）的包围盒，供迷你球场图使用。</summary>
    public (double MinLateral, double MaxLateral, double MinForward, double MaxForward) PlanBounds()
    {
        if (Points.Count == 0) return (0, 0, 0, 0);
        return (Points.Min(p => p.Lateral), Points.Max(p => p.Lateral),
                Points.Min(p => p.Forward), Points.Max(p => p.Forward));
    }

    private double SegmentSpeed(int from, int to)
    {
        var a = Points[from];
        var b = Points[to];
        var dt = b.Time - a.Time;
        if (dt <= 1e-6) return 0;
        var dx = b.Forward - a.Forward;
        var dy = b.Lateral - a.Lateral;
        var dz = b.Height - a.Height;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) / dt;
    }
}
