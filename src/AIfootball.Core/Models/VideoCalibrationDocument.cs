using System.Text.Json.Serialization;

namespace AIfootball.Core.Models;

/// <summary>
/// 一段视频里**每一脚各自**的门框标定。
///
/// 为什么不是"一个视频一套"或"一段镜头一套"：点球合集是多个机位混着剪的，
/// 相邻两脚就可能换机位。用检测器抽样看过 9 个时间点，门框中心的 x 在 206~948 之间跳，
/// 宽度 146~296——任何"沿用上一脚"的做法都会让落点错几十厘米。
/// 所以键就是**这一脚在时间轴上的起点**，一脚一套，没标过的脚次就是没有门框。
/// </summary>
public sealed class VideoCalibrationDocument
{
    /// <summary>同一脚重标时认定"是同一脚"的起点容差（秒）。</summary>
    private const double SameShotTolerance = 0.08;

    [JsonPropertyName("video")] public string Video { get; set; } = "";

    [JsonPropertyName("shots")] public List<ShotCalibration> Shots { get; set; } = [];

    /// <summary>已标过的脚次数。</summary>
    [JsonIgnore] public int Count => Shots.Count;

    /// <summary>取某一脚的门框。没标过就是 null——绝不沿用别的脚。</summary>
    public GoalFrameCalibration? ForShot(double shotStart)
    {
        foreach (var entry in Shots)
        {
            if (Math.Abs(entry.ShotStart - shotStart) > SameShotTolerance) continue;
            if (entry.Calibration is { IsComplete: true } calibration) return calibration;
        }
        return null;
    }

    public bool Has(double shotStart) => ForShot(shotStart) is not null;

    /// <summary>写入某一脚的标定；同一脚重标则覆盖。</summary>
    public void Set(double shotStart, GoalFrameCalibration calibration)
    {
        Shots.RemoveAll(entry => Math.Abs(entry.ShotStart - shotStart) <= SameShotTolerance);
        Shots.Add(new ShotCalibration { ShotStart = shotStart, Calibration = calibration });
        Shots.Sort((a, b) => a.ShotStart.CompareTo(b.ShotStart));
    }

    /// <summary>
    /// 删掉某一脚的标定，返回是否真的删到了。
    ///
    /// 之前只有 Set 没有 Remove，标错的脚次只能靠"再标一次"覆盖，
    /// 于是画面上那圈橙框永远消不掉——用户以为清不掉，其实是压根没有删除入口。
    /// </summary>
    public bool Remove(double shotStart) =>
        Shots.RemoveAll(entry => Math.Abs(entry.ShotStart - shotStart) <= SameShotTolerance) > 0;
}

/// <summary>一脚点球的门框标定，按这一脚的起点时间归档。</summary>
public sealed class ShotCalibration
{
    /// <summary>这一脚在时间轴上的起点（秒），与脚次表里的 start 一致。</summary>
    [JsonPropertyName("shotStart")] public double ShotStart { get; set; }

    [JsonPropertyName("calibration")] public GoalFrameCalibration Calibration { get; set; } = new();
}
