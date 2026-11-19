namespace AIfootball.Core.Models;

/// <summary>样本在流水线中的完成度。</summary>
public enum SampleReadiness
{
    /// <summary>只有原始左右视频。</summary>
    Raw = 0,
    /// <summary>已完成三维重建。</summary>
    Reconstructed = 1,
    /// <summary>已完成弹道拟合。</summary>
    Ballistic = 2,
    /// <summary>已导出可被 Unity 回放的轨迹。</summary>
    Deliverable = 3,
}

/// <summary>
/// 一个双摄样本：<c>samples/&lt;name&gt;/{left,right}/recording.mp4</c>。
/// 相机归属由目录决定，绝不从文件名或画面内容猜测。
/// </summary>
public sealed record Sample(
    string Name,
    string DirectoryPath,
    string? LeftVideoPath,
    string? RightVideoPath,
    long LeftVideoBytes,
    long RightVideoBytes,
    bool HasReconstruction,
    bool HasBallistic,
    bool HasDeliverable,
    DateTimeOffset? LastWriteTime)
{
    /// <summary>单段录制小于 1KB 一定是中断留下的坏文件（读不出 moov atom）。</summary>
    public const long MinimumUsableVideoBytes = 1024;

    public bool HasLeftVideo => IsUsable(LeftVideoPath, LeftVideoBytes);
    public bool HasRightVideo => IsUsable(RightVideoPath, RightVideoBytes);
    public bool IsPairComplete => HasLeftVideo && HasRightVideo;

    public SampleReadiness Readiness => HasDeliverable ? SampleReadiness.Deliverable
        : HasBallistic ? SampleReadiness.Ballistic
        : HasReconstruction ? SampleReadiness.Reconstructed
        : SampleReadiness.Raw;

    public string ReadinessLabel => Readiness switch
    {
        SampleReadiness.Deliverable => "可回放",
        SampleReadiness.Ballistic => "已拟合",
        SampleReadiness.Reconstructed => "已重建",
        _ => "待处理",
    };

    public string VideoSummary => $"{FormatSize(LeftVideoBytes)} + {FormatSize(RightVideoBytes)}";

    private static bool IsUsable(string? path, long bytes) =>
        !string.IsNullOrEmpty(path) && bytes >= MinimumUsableVideoBytes;

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
