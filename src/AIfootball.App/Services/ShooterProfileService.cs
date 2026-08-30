using System.Globalization;
using System.Text;
using System.Text.Json;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>
/// Analyzes the original trajectory CSV files produced by the training pipeline.
/// It deliberately does not access Unity history or simulation outcomes.
/// </summary>
public sealed class ShooterProfileService : IShooterProfileService
{
    private const float CenterLaneHalfWidth = 0.55f;
    private readonly IPythonEngine _engine;

    public ShooterProfileService(IPythonEngine engine)
    {
        _engine = engine;
    }

    public ShooterProfileResult AnalyzeTrainingData()
    {
        var dataDirectory = Path.Combine(_engine.WorkspaceDir, "data");
        var files = Directory.Exists(dataDirectory)
            ? Directory.GetFiles(dataDirectory, "*_trajectory.csv", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();

        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var trajectories = new List<(string SampleName, List<TrajectoryPoint> Points)>();
        var duplicateCount = 0;
        var invalidCount = 0;

        foreach (var file in files)
        {
            if (!TryReadTrajectory(file, out var trajectory))
            {
                invalidCount++;
                continue;
            }

            if (!signatures.Add(CreateSignature(trajectory)))
            {
                duplicateCount++;
                continue;
            }
            trajectories.Add((Path.GetFileNameWithoutExtension(file), trajectory));
        }

        if (trajectories.Count == 0)
            return new ShooterProfileResult(files.Length, 0, duplicateCount, invalidCount,
                0f, 0f, 0f, 0f, "", "", "", "");

        float totalSpeed = 0f;
        float totalOffset = 0f;
        float totalOffsetSquared = 0f;
        float totalHeight = 0f;
        var left = 0;
        var center = 0;
        var right = 0;

        foreach (var (sampleName, trajectory) in trajectories)
        {
            var (targetLateral, targetHeight) = ResolveTargetPoint(sampleName, trajectory);

            totalSpeed += CalculateAverageSpeed(trajectory);
            totalOffset += targetLateral;
            totalOffsetSquared += targetLateral * targetLateral;
            totalHeight += targetHeight;
            if (targetLateral > CenterLaneHalfWidth) left++;
            else if (targetLateral < -CenterLaneHalfWidth) right++;
            else center++;
        }

        var count = trajectories.Count;
        var averageOffset = totalOffset / count;
        var spread = MathF.Sqrt(MathF.Max(0f, totalOffsetSquared / count - averageOffset * averageOffset));
        return new ShooterProfileResult(
            files.Length,
            count,
            duplicateCount,
            invalidCount,
            totalSpeed / count,
            averageOffset,
            spread,
            totalHeight / count,
            DescribeSide(left, center, right),
            DescribeHeight(totalHeight / count),
            DescribePower(totalSpeed / count),
            DescribeConsistency(spread));
    }

    /// <summary>
    /// 解析一次射门的落点：优先用弹道拟合输出的门线交点（解析解），
    /// 其次对 CSV 做跨线插值，最后回退到最贴近门线的采样点。
    /// </summary>
    private (float Lateral, float Height) ResolveTargetPoint(string sampleName, List<TrajectoryPoint> trajectory)
    {
        var crossing = TryReadBallisticCrossing(sampleName);
        if (crossing.HasValue) return crossing.Value;
        return InterpolateGoalLineCrossing(trajectory);
    }

    /// <summary>
    /// 读取 Python 流水线弹道拟合的门线交点。data/<sample>_trajectory.csv 由
    /// export_unity_trajectory.py 从 output/trajectory_ballistic/<sample>/ 导出，
    /// summary 中的 goal_line_crossing 是弹道模型与门线平面的解析解，不受
    /// 采样间隔影响（25 m/s 时相邻采样点间隔约 0.2 m）。
    /// </summary>
    private (float Lateral, float Height)? TryReadBallisticCrossing(string sampleName)
    {
        try
        {
            var summaryPath = Path.Combine(_engine.WorkspaceDir, "output",
                "trajectory_ballistic", sampleName, "ballistic_fit_summary.json");
            if (!File.Exists(summaryPath)) return null;

            using var stream = File.OpenRead(summaryPath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("goal_line_crossing", out var crossing))
                return null;
            if (!crossing.TryGetProperty("detected", out var detected)
                || detected.ValueKind != JsonValueKind.True)
                return null;
            if (!crossing.TryGetProperty("point_xyz_m", out var point)
                || point.ValueKind != JsonValueKind.Array)
                return null;

            var values = point.EnumerateArray().Select(v => v.GetSingle()).ToArray();
            if (values.Length < 3 || !values.All(float.IsFinite)) return null;
            // summary 是 Python 坐标：X=横向（Unity y，门将右侧为正）、Y=纵深、Z=高度
            return (values[0], values[2]);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// 在门线附近对相邻采样点做线性插值，替代"最近单点"：曲线上两个采样点
    /// 跨越 x=0 时，交点处的高度/横向由连续插值给出，消除采样量化误差。
    /// </summary>
    private static (float Lateral, float Height) InterpolateGoalLineCrossing(List<TrajectoryPoint> trajectory)
    {
        for (var index = 1; index < trajectory.Count; index++)
        {
            var a = trajectory[index - 1];
            var b = trajectory[index];
            if ((a.Forward > 0f) == (b.Forward > 0f)) continue;

            var span = b.Forward - a.Forward;
            if (MathF.Abs(span) < float.Epsilon) continue;
            var alpha = (0f - a.Forward) / span;
            if (alpha < 0f || alpha > 1f) continue;
            return (a.Lateral + alpha * (b.Lateral - a.Lateral),
                    a.Height + alpha * (b.Height - a.Height));
        }

        var target = trajectory[0];
        foreach (var point in trajectory)
        {
            if (MathF.Abs(point.Forward) < MathF.Abs(target.Forward))
                target = point;
        }
        return (target.Lateral, target.Height);
    }

    private static bool TryReadTrajectory(string path, out List<TrajectoryPoint> trajectory)
    {
        trajectory = new List<TrajectoryPoint>();
        try
        {
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;

                var columns = line.Split(',');
                if (columns.Length != 4) return false;
                if (!TryParsePoint(columns, out var point))
                {
                    if (trajectory.Count == 0 && IsHeader(columns)) continue;
                    return false;
                }
                if (trajectory.Count > 0 && point.Time <= trajectory[^1].Time) return false;
                trajectory.Add(point);
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }

        return trajectory.Count >= 2 && trajectory[^1].Time > trajectory[0].Time;
    }

    private static bool TryParsePoint(string[] columns, out TrajectoryPoint point)
    {
        const NumberStyles Style = NumberStyles.Float;
        var culture = CultureInfo.InvariantCulture;
        var time = 0f;
        var forward = 0f;
        var lateral = 0f;
        var height = 0f;
        var ok = float.TryParse(columns[0].Trim().TrimStart('\ufeff'), Style, culture, out time)
            && float.TryParse(columns[1].Trim(), Style, culture, out forward)
            && float.TryParse(columns[2].Trim(), Style, culture, out lateral)
            && float.TryParse(columns[3].Trim(), Style, culture, out height)
            && float.IsFinite(time) && float.IsFinite(forward)
            && float.IsFinite(lateral) && float.IsFinite(height);
        point = ok ? new TrajectoryPoint(time, forward, lateral, height) : default;
        return ok;
    }

    private static bool IsHeader(string[] columns) =>
        string.Equals(columns[0].Trim().TrimStart('\ufeff'), "time", StringComparison.OrdinalIgnoreCase)
        && string.Equals(columns[1].Trim(), "x", StringComparison.OrdinalIgnoreCase)
        && string.Equals(columns[2].Trim(), "y", StringComparison.OrdinalIgnoreCase)
        && string.Equals(columns[3].Trim(), "z", StringComparison.OrdinalIgnoreCase);

    private static string CreateSignature(List<TrajectoryPoint> trajectory)
    {
        var signature = new StringBuilder(trajectory.Count * 36);
        foreach (var point in trajectory)
        {
            AppendRounded(signature, point.Time);
            AppendRounded(signature, point.Forward);
            AppendRounded(signature, point.Lateral);
            AppendRounded(signature, point.Height);
        }
        return signature.ToString();
    }

    private static void AppendRounded(StringBuilder builder, float value)
    {
        builder.Append((MathF.Round(value * 1000f) / 1000f).ToString("0.000", CultureInfo.InvariantCulture));
        builder.Append('|');
    }

    private static float CalculateAverageSpeed(List<TrajectoryPoint> trajectory)
    {
        var distance = 0f;
        for (var index = 1; index < trajectory.Count; index++)
        {
            var a = trajectory[index - 1];
            var b = trajectory[index];
            distance += MathF.Sqrt((b.Forward - a.Forward) * (b.Forward - a.Forward)
                + (b.Lateral - a.Lateral) * (b.Lateral - a.Lateral)
                + (b.Height - a.Height) * (b.Height - a.Height));
        }
        var duration = trajectory[^1].Time - trajectory[0].Time;
        return duration > float.Epsilon ? distance / duration : 0f;
    }

    private static string DescribeSide(int left, int center, int right)
    {
        var maximum = Math.Max(left, Math.Max(center, right));
        if (maximum == center) return "中路倾向";
        if (left == right) return "左右均衡";
        // Positive y is the goalkeeper's right and therefore the shooter's left.
        return maximum == left ? "偏射手左侧" : "偏射手右侧";
    }

    private static string DescribeHeight(float height) => height switch
    {
        < 0.75f => "低平球",
        > 1.65f => "高点冲击",
        _ => "中路半高球"
    };

    private static string DescribePower(float speed) => speed switch
    {
        >= 19f => "力量型射门",
        <= 12f => "节奏型射门",
        _ => "均衡发力"
    };

    private static string DescribeConsistency(float spread) => spread switch
    {
        <= 0.60f => "落点稳定",
        >= 1.35f => "落点多变",
        _ => "落点均衡"
    };

    private readonly record struct TrajectoryPoint(float Time, float Forward, float Lateral, float Height);
}
