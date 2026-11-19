using System.Globalization;
using System.Text.Json;
using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

/// <summary>轨迹交付元数据：记录这条轨迹交付给哪个门将，重新打开时保持一致。</summary>
public sealed record DeliveryMetadata(string? GoalkeeperName, DateTimeOffset? DeliveredAt);

/// <summary>
/// Unity 回放轨迹的读写。CSV 列固定为 <c>time,x,y,z</c>（x=向前、y=门将右侧、z=向上）。
/// </summary>
public sealed class TrajectoryStore(AifootballWorkspace workspace)
{
    private static readonly JsonSerializerOptions MetadataOptions = new() { WriteIndented = true };
    private readonly AifootballWorkspace _workspace = workspace;

    public Trajectory? Load(string sampleName)
    {
        var path = _workspace.UnityCsv(sampleName);
        if (!File.Exists(path)) return null;

        var points = new List<TrajectoryPoint>(4096);
        try
        {
            using var reader = new StreamReader(path);
            string? line;
            var first = true;
            while ((line = reader.ReadLine()) is not null)
            {
                if (first) { first = false; if (IsHeader(line)) continue; }
                if (line.Length == 0) continue;
                if (TryParse(line, out var point)) points.Add(point);
            }
        }
        catch (IOException) { return null; }

        return points.Count == 0 ? null : new Trajectory
        {
            SampleName = sampleName,
            Points = points,
            SourcePath = path,
        };
    }

    public DeliveryMetadata? LoadMetadata(string sampleName)
    {
        var path = _workspace.UnityMeta(sampleName);
        if (!File.Exists(path)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            string? goalkeeper = null;
            foreach (var name in new[] { "GoalkeeperName", "goalkeeperName", "goalkeeper" })
            {
                if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                {
                    goalkeeper = value.GetString();
                    break;
                }
            }
            DateTimeOffset? deliveredAt = null;
            if (root.TryGetProperty("DeliveredAt", out var stamp) && stamp.TryGetDateTimeOffset(out var parsed))
                deliveredAt = parsed;
            return new DeliveryMetadata(goalkeeper, deliveredAt);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public void SaveMetadata(string sampleName, string? goalkeeperName)
    {
        var path = _workspace.UnityMeta(sampleName);
        Directory.CreateDirectory(_workspace.DataDir);
        var payload = new Dictionary<string, object?>
        {
            ["GoalkeeperName"] = goalkeeperName,
            ["DeliveredAt"] = DateTimeOffset.Now.ToString("O"),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, MetadataOptions));
    }

    /// <summary>写出一份 CSV（结果页"另存为"使用）。</summary>
    public void Save(string path, IReadOnlyList<TrajectoryPoint> points)
    {
        using var writer = new StreamWriter(path);
        writer.WriteLine("time,x,y,z");
        foreach (var point in points)
        {
            writer.WriteLine(string.Join(',',
                point.Time.ToString("0.######", CultureInfo.InvariantCulture),
                point.Forward.ToString("0.######", CultureInfo.InvariantCulture),
                point.Lateral.ToString("0.######", CultureInfo.InvariantCulture),
                point.Height.ToString("0.######", CultureInfo.InvariantCulture)));
        }
    }

    private static bool IsHeader(string line) =>
        line.StartsWith("time", StringComparison.OrdinalIgnoreCase);

    private static bool TryParse(string line, out TrajectoryPoint point)
    {
        point = default;
        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 4) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var forward)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var lateral)) return false;
        if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return false;
        point = new TrajectoryPoint(time, forward, lateral, height);
        return true;
    }
}
