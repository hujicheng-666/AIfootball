namespace AIfootball.Core.Models;

/// <summary>单台相机的标定摘要（来自 calib/*_summary.json 与 *_extrinsics.json）。</summary>
public sealed record CameraCalibration(
    string Side,
    bool IntrinsicsReady,
    bool ExtrinsicsReady,
    string IntrinsicsPath,
    string PosePath,
    int ImageWidth,
    int ImageHeight,
    double? RmsReprojectionPx,
    double? FocalLengthPx,
    int? ViewsUsed)
{
    public static CameraCalibration Missing(string side, string intrinsicsPath, string posePath) =>
        new(side, false, false, intrinsicsPath, posePath, 0, 0, null, null, null);

    public string ResolutionLabel => ImageWidth > 0 ? $"{ImageWidth}×{ImageHeight}" : "—";
    public string RmsLabel => RmsReprojectionPx is { } rms ? $"{rms:0.000} px" : "—";
    public string FocalLabel => FocalLengthPx is { } focal ? $"{focal:0.0} px" : "—";
}

/// <summary>双目标定整体状态（内参 + 外参 + 立体基线）。</summary>
public sealed record CalibrationSnapshot(
    CameraCalibration Left,
    CameraCalibration Right,
    double? BaselineMeters,
    double? StereoRmsPx,
    DateTimeOffset? UpdatedAt)
{
    public bool IntrinsicsReady => Left.IntrinsicsReady && Right.IntrinsicsReady;
    public bool ExtrinsicsReady => Left.ExtrinsicsReady && Right.ExtrinsicsReady;
    public bool FullyReady => IntrinsicsReady && ExtrinsicsReady;

    public string ReadinessLabel => FullyReady ? "标定完成" : IntrinsicsReady ? "缺少外参" : "未标定";

    public double CompletionRatio => (Left.IntrinsicsReady ? 0.25 : 0) + (Right.IntrinsicsReady ? 0.25 : 0)
                                   + (Left.ExtrinsicsReady ? 0.25 : 0) + (Right.ExtrinsicsReady ? 0.25 : 0);

    public static CalibrationSnapshot Empty(string calibDir) => new(
        CameraCalibration.Missing("left", Path.Combine(calibDir, "intrinsics_left.npz"), Path.Combine(calibDir, "left_pose.npz")),
        CameraCalibration.Missing("right", Path.Combine(calibDir, "intrinsics_right.npz"), Path.Combine(calibDir, "right_pose.npz")),
        null, null, null);
}
