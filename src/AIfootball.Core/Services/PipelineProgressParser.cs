using System.Text.RegularExpressions;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>从引擎日志推导出的结构化进度事件。</summary>
public sealed record PipelineProgressEvent(
    PipelineStage Stage,
    StageState State,
    string Message,
    string? SampleName = null,
    double? OverallRatio = null,
    double? SampleFraction = null);

/// <summary>
/// 引擎日志 → 结构化进度。
///
/// 旧实现把一堆正则散落在 ViewModel 里（`UpdateStepStatusFromLog`），既难测试也不好维护。
/// 这里做成纯函数式解析器：输入一行日志，输出一个进度事件（或 null），可被单元测试完整覆盖。
/// 引擎输出格式为 <c>[阶段序号/总数] 阶段名</c> 与 <c>[样本名] 说明</c>。
/// </summary>
public sealed partial class PipelineProgressParser(IReadOnlyList<string> samples)
{
    private readonly List<string> _samples = [.. samples];
    private readonly Dictionary<PipelineStage, HashSet<string>> _completed = new()
    {
        [PipelineStage.Reconstruct] = [],
        [PipelineStage.Ballistic] = [],
        [PipelineStage.Export] = [],
    };

    private PipelineStage _current = PipelineStage.Idle;

    /// <summary>当前已知的单样本阶段完成数（用于估算总进度）。</summary>
    public int CompletedUnits => _completed.Values.Sum(set => set.Count);

    public int TotalUnits => Math.Max(1, _samples.Count * 3);

    public double Ratio => Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);

    public PipelineProgressEvent? Consume(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var text = line.Trim();

        if (StageBannerPattern().Match(text) is { Success: true } banner)
        {
            var stage = banner.Groups["name"].Value switch
            {
                var n when n.Contains("重建", StringComparison.Ordinal) => PipelineStage.Reconstruct,
                var n when n.Contains("拟合", StringComparison.Ordinal) => PipelineStage.Ballistic,
                var n when n.Contains("导出", StringComparison.Ordinal) => PipelineStage.Export,
                _ => PipelineStage.Idle,
            };
            if (stage == PipelineStage.Idle) return null;
            AdvanceTo(stage);
            return new PipelineProgressEvent(stage, StageState.Running, text, null, Ratio);
        }

        if (text.Contains("全部完成", StringComparison.Ordinal))
        {
            foreach (var stage in _completed.Keys) _completed[stage].UnionWith(_samples);
            return new PipelineProgressEvent(PipelineStage.Done, StageState.Completed, "全部阶段已完成", null, 1);
        }

        if (SampleTagPattern().Match(text) is not { Success: true } tag)
        {
            // 重建阶段内部的分段进度（"起脚定位扫描：120/900 帧 (13%)"）没有样本前缀，
            // 但进度条能否平滑走全靠它，单独认一下。
            var segment = ParseSampleFraction(text);
            if (segment is null) return null;
            return new PipelineProgressEvent(_current, StageState.Running, text, null, Ratio, segment);
        }
        var sample = tag.Groups["sample"].Value;
        if (!_samples.Contains(sample)) return null;
        var body = tag.Groups["body"].Value;

        if (body.Contains("检测足球", StringComparison.Ordinal) || body.Contains("相机分配", StringComparison.Ordinal))
            return Emit(PipelineStage.Reconstruct, StageState.Running, $"正在重建 {sample}", sample);

        if (body.Contains("Raw 3D point count", StringComparison.Ordinal) || body.Contains("Filtered 3D point count", StringComparison.Ordinal))
            return Complete(PipelineStage.Reconstruct, sample, $"{sample} 三维重建完成");

        if (body.Contains("拟合 RMSE", StringComparison.Ordinal) || body.Contains("拟合点数", StringComparison.Ordinal))
            return Complete(PipelineStage.Ballistic, sample, $"{sample} 弹道拟合完成");

        if (body.Contains("已保存", StringComparison.Ordinal) && body.Contains("summary", StringComparison.OrdinalIgnoreCase))
            return Complete(PipelineStage.Ballistic, sample, $"{sample} 拟合结果已落盘");

        if (body.Contains("_trajectory.csv", StringComparison.Ordinal) || body.StartsWith("->", StringComparison.Ordinal))
            return Complete(PipelineStage.Export, sample, $"{sample} 已导出回放轨迹");

        return new PipelineProgressEvent(_current, StageState.Running, text, sample, Ratio,
            ParseSampleFraction(body));
    }

    /// <summary>
    /// 把「样本内百分比」换算成 0–1 的阶段内位置。
    /// 重建阶段引擎会分两段各自从 0 打印到 100%（起脚定位、密集跟踪），
    /// 直接取百分比会让进度条在中途回退，所以按 20% / 80% 分配。
    /// </summary>
    private static double? ParseSampleFraction(string body)
    {
        if (PercentPattern().Match(body) is not { Success: true } match
            || !int.TryParse(match.Groups["percent"].Value, out var percent))
            return null;

        var raw = Math.Clamp(percent / 100.0, 0, 1);
        if (body.Contains("起脚定位", StringComparison.Ordinal)) return raw * 0.2;
        if (body.Contains("密集跟踪", StringComparison.Ordinal)) return 0.2 + raw * 0.8;
        return raw;
    }

    private PipelineProgressEvent Emit(PipelineStage stage, StageState state, string message, string? sample) =>
        new(stage, state, message, sample, Ratio);

    private PipelineProgressEvent Complete(PipelineStage stage, string sample, string message)
    {
        _completed[stage].Add(sample);
        return new PipelineProgressEvent(stage, StageState.Completed, message, sample, Ratio);
    }

    /// <summary>进入新阶段时，把之前所有阶段标记为完成（顺序执行的前提）。</summary>
    private void AdvanceTo(PipelineStage stage)
    {
        foreach (var previous in _completed.Keys.Where(s => s < stage).ToArray())
            _completed[previous].UnionWith(_samples);
        _current = stage;
    }

    [GeneratedRegex(@"^\[\d+/\d+\]\s*(?<name>.+)$")]
    private static partial Regex StageBannerPattern();

    [GeneratedRegex(@"^\[(?<sample>[^\]\s]+)\]\s*(?<body>.+)$")]
    private static partial Regex SampleTagPattern();

    [GeneratedRegex(@"\((?<percent>\d{1,3})%\)")]
    private static partial Regex PercentPattern();
}
