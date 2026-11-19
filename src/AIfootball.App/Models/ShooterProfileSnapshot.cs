namespace AIfootball.App.Models;

/// <summary>
/// 点球手画像的界面快照。
/// 画像由训练 CSV 归纳得出，属于分析产出而不是回放动作，因此放在分析页展示；
/// 这里只做一次计算并把结论交给前端，避免在 TypeScript 侧重复实现归一化。
/// </summary>
public sealed record ShooterProfileSnapshot(
    bool HasData,
    string CountText,
    string[] Lines,
    string[] RadarLabels,
    double[] RadarValues,
    string EmptyText)
{
    public static ShooterProfileSnapshot From(ShooterProfileResult? profile)
    {
        if (profile is null)
            return new ShooterProfileSnapshot(
                false, "数据未就绪", [], [], [], "无法读取训练数据");

        if (!profile.HasData)
            return new ShooterProfileSnapshot(
                false,
                $"训练样本 0 条（文件 {profile.SourceFileCount} 个）",
                [], [], [],
                profile.InvalidFileCount > 0
                    ? $"没有可用轨迹，{profile.InvalidFileCount} 个文件解析失败"
                    : "暂无可用训练轨迹 CSV");

        var lines = new[]
        {
            $"射门力量  {profile.AverageSpeed:0.0} m/s",
            $"横向落点  {profile.AverageTargetOffset:+0.00;-0.00;0.00} m",
            $"落点高度  {profile.AverageTargetHeight:0.00} m",
            $"落点离散  {profile.TargetSpread:0.00} m",
            $"方向偏好  {profile.PreferredSide}",
            $"高度偏好  {profile.PreferredHeight}",
            $"稳定性    {profile.ConsistencyTrait}",
        };

        var labels = new[] { "力量", "稳定", "左路", "高度", "变化" };
        var values = new[]
        {
            Normalize(profile.AverageSpeed, 8f, 24f),
            1d - Normalize(profile.TargetSpread, 0.2f, 2f),
            Normalize(profile.AverageTargetOffset, -3f, 3f),
            Normalize(profile.AverageTargetHeight, 0.2f, 2.2f),
            Normalize(profile.TargetSpread, 0.2f, 2f),
        };

        return new ShooterProfileSnapshot(
            true,
            $"有效轨迹 {profile.UniqueTrajectoryCount} 条 · 重复 {profile.DuplicateTrajectoryCount} 条",
            lines, labels, values, "");
    }

    private static double Normalize(float value, float min, float max) =>
        Math.Clamp((value - min) / (max - min), 0f, 1f);
}
