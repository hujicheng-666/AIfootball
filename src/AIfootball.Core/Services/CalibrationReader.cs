using System.Text.Json;
using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

/// <summary>
/// 读取标定产物。旧实现只看"文件在不在"，这里进一步读出分辨率、焦距、重投影误差，
/// 让界面能显示"标定质量"而不是一个布尔值。
/// </summary>
public sealed class CalibrationReader(AifootballWorkspace workspace)
{
    private readonly AifootballWorkspace _workspace = workspace;

    public CalibrationSnapshot Read()
    {
        var dir = _workspace.CalibrationDir;
        var left = ReadCamera("left", dir);
        var right = ReadCamera("right", dir);
        var (baseline, stereoRms) = ReadStereo(dir);

        DateTimeOffset? updated = null;
        try
        {
            var files = new[] { left.PosePath, right.PosePath, left.IntrinsicsPath, right.IntrinsicsPath }
                .Where(File.Exists).Select(File.GetLastWriteTimeUtc).ToArray();
            if (files.Length > 0) updated = files.Max();
        }
        catch (IOException) { }

        return new CalibrationSnapshot(left, right, baseline, stereoRms, updated);
    }

    private static CameraCalibration ReadCamera(string side, string dir)
    {
        var intrinsicsPath = Path.Combine(dir, $"intrinsics_{side}.npz");
        var posePath = Path.Combine(dir, $"{side}_pose.npz");
        var summaryPath = Path.Combine(dir, $"intrinsics_{side}_summary.json");
        var extrinsicsPath = Path.Combine(dir, $"{side}_extrinsics.json");

        var intrinsicsReady = File.Exists(intrinsicsPath);
        var extrinsicsReady = File.Exists(posePath);

        int width = 0, height = 0, views = 0;
        double? rms = null, focal = null;

        if (TryReadJson(summaryPath, out var summary))
        {
            width = ReadInt(summary, "image_width") ?? ReadInt(summary, "width") ?? 0;
            height = ReadInt(summary, "image_height") ?? ReadInt(summary, "height") ?? 0;
            views = ReadInt(summary, "selected_view_count") ?? ReadInt(summary, "view_count") ?? 0;
            rms = ReadDouble(summary, "rms_reprojection_error") ?? ReadDouble(summary, "mean_reprojection_error");
            focal = ReadDouble(summary, "fx") ?? ReadDouble(summary, "focal_length_px");
        }

        if (TryReadJson(extrinsicsPath, out var extrinsics))
        {
            rms ??= ReadDouble(extrinsics, "mean_reprojection_error_px") ?? ReadDouble(extrinsics, "rms_reprojection_error_px");
            width = width == 0 ? ReadInt(extrinsics, "image_width") ?? 0 : width;
            height = height == 0 ? ReadInt(extrinsics, "image_height") ?? 0 : height;
        }

        return new CameraCalibration(side, intrinsicsReady, extrinsicsReady,
            intrinsicsPath, posePath, width, height, rms, focal, views == 0 ? null : views);
    }

    private static (double? Baseline, double? StereoRms) ReadStereo(string dir)
    {
        var path = Path.Combine(dir, "stereo_summary.json");
        if (!TryReadJson(path, out var root)) return (null, null);
        return (ReadDouble(root, "baseline_m"), ReadDouble(root, "stereo_rms_px"));
    }

    internal static bool TryReadJson(string path, out JsonElement element)
    {
        element = default;
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
    }

    internal static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : null;

    internal static double? ReadDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}

/// <summary>门将 JSON 读取（容错：字段缺失时使用默认值）。</summary>
public static class GoalkeeperReader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static GoalkeeperProfile? Read(string path)
    {
        try
        {
            var profile = JsonSerializer.Deserialize<GoalkeeperProfile>(File.ReadAllText(path), Options);
            if (profile is null) return null;
            return new GoalkeeperProfile
            {
                Name = Path.GetFileNameWithoutExtension(path),
                FilePath = path,
                DisplayName = profile.DisplayName,
                Description = profile.Description,
                Height = profile.Height,
                DiveSpeed = profile.DiveSpeed,
                JumpDistance = profile.JumpDistance,
                JumpHeight = profile.JumpHeight,
                Reach = profile.Reach,
                TendGoalSpeed = profile.TendGoalSpeed,
                TendGoalDistance = profile.TendGoalDistance,
                GoalKeeping = profile.GoalKeeping,
                SidePreference = profile.SidePreference,
                HeightPreference = profile.HeightPreference,
                ProbabilityMap = profile.ProbabilityMap,
            };
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
