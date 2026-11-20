using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.Core.Tests;

/// <summary>
/// 进度解析器：把引擎日志变成界面能用的结构化事件。
/// 用例里的日志行都取自引擎真实输出格式（cli.py / reconstruction.py），
/// 改了引擎打印格式却忘了改这里，测试会先炸。
/// </summary>
public sealed class PipelineProgressParserTests
{
    private static PipelineProgressParser CreateParser(params string[] samples) =>
        new(samples.Length == 0 ? ["sample1", "sample2"] : samples);

    [Fact]
    public void Consume_StageBannerDrivesStage()
    {
        var parser = CreateParser();

        var reconstruct = parser.Consume("[1/3] 3D 重建: 2 个样本");
        Assert.NotNull(reconstruct);
        Assert.Equal(PipelineStage.Reconstruct, reconstruct.Stage);
        Assert.Equal(StageState.Running, reconstruct.State);

        var ballistic = parser.Consume("[2/3] 弹道拟合: 2 个样本");
        Assert.Equal(PipelineStage.Ballistic, ballistic!.Stage);

        var export = parser.Consume("[3/3] Unity 导出: 2 个样本");
        Assert.Equal(PipelineStage.Export, export!.Stage);
    }

    [Fact]
    public void Consume_SampleLineCarriesSampleName()
    {
        var parser = CreateParser();
        parser.Consume("[1/3] 3D 重建: 2 个样本");

        var progress = parser.Consume("[sample2] 检测足球: 120 帧");

        Assert.NotNull(progress);
        Assert.Equal("sample2", progress.SampleName);
        Assert.Equal(PipelineStage.Reconstruct, progress.Stage);
    }

    [Fact]
    public void Consume_UnknownSampleIsIgnored()
    {
        var parser = CreateParser();
        parser.Consume("[1/3] 3D 重建: 2 个样本");

        Assert.Null(parser.Consume("[not_a_sample] 检测足球: 120 帧"));
    }

    [Fact]
    public void Consume_BlankLineReturnsNull()
    {
        var parser = CreateParser();

        Assert.Null(parser.Consume("   "));
        Assert.Null(parser.Consume(""));
    }

    [Fact]
    public void Consume_SegmentPercentIsSplitAcrossSparseAndDensePhases()
    {
        var parser = CreateParser();
        parser.Consume("[1/3] 3D 重建: 2 个样本");

        // 稀疏扫描占样本内前 20%
        var sparse = parser.Consume("  起脚定位扫描：450/900 帧 (50%)，耗时 12s");
        Assert.NotNull(sparse);
        Assert.Equal(0.1, sparse.SampleFraction!.Value, 6);

        // 密集跟踪占后 80%，两段各自 0-100% 也不会让进度回退
        var denseStart = parser.Consume("  密集跟踪：0/900 帧 (0%)");
        Assert.Equal(0.2, denseStart!.SampleFraction!.Value, 6);

        var denseHalf = parser.Consume("  密集跟踪：450/900 帧 (50%)，耗时 30s");
        Assert.Equal(0.6, denseHalf!.SampleFraction!.Value, 6);

        var denseEnd = parser.Consume("  密集跟踪：900/900 帧 (100%)");
        Assert.Equal(1.0, denseEnd!.SampleFraction!.Value, 6);
    }

    [Fact]
    public void Consume_CompletionAdvancesRatio()
    {
        var parser = CreateParser("sample1");
        Assert.Equal(0, parser.CompletedUnits);

        parser.Consume("[1/3] 3D 重建: 1 个样本");
        var done = parser.Consume("[sample1] Raw 3D point count: 420");

        Assert.Equal(PipelineStage.Reconstruct, done!.Stage);
        Assert.Equal(StageState.Completed, done.State);
        Assert.Equal(1, parser.CompletedUnits);
        Assert.True(parser.Ratio > 0);
    }

    [Fact]
    public void Consume_TriangulationKeepsReconstructionRunning()
    {
        var parser = CreateParser("sample1");
        parser.Consume("[1/3] 3D 重建: 1 个样本");

        var progress = parser.Consume("[sample1] 正在进行双目时间对齐与三角化…");

        Assert.NotNull(progress);
        Assert.Equal(PipelineStage.Reconstruct, progress.Stage);
        Assert.Equal(StageState.Running, progress.State);
        Assert.Equal(1.0, progress.SampleFraction);
        Assert.Equal(0, parser.CompletedUnits);
    }

    [Fact]
    public void Consume_BallisticCompletionIsIdempotent()
    {
        var parser = CreateParser("sample1");
        parser.Consume("[2/3] 弹道拟合: 1 个样本");
        // 进入第二阶段时，前一阶段即视为完成
        Assert.Equal(1, parser.CompletedUnits);

        parser.Consume("[sample1] 拟合 RMSE: 0.021 m，拟合点数: 22");
        var afterFirst = parser.CompletedUnits;

        parser.Consume("[sample1] 拟合 RMSE: 0.021 m，拟合点数: 22");

        // 重复行不能把进度算成 200%
        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, parser.CompletedUnits);
        Assert.InRange(parser.Ratio, 0, 1);
    }

    [Fact]
    public void Consume_AllDoneReportsFullRatio()
    {
        var parser = CreateParser("sample1", "sample2");

        var done = parser.Consume("======== 全部完成 ========");

        Assert.NotNull(done);
        Assert.Equal(PipelineStage.Done, done.Stage);
        Assert.Equal(1, done.OverallRatio);
        Assert.Equal(parser.TotalUnits, parser.CompletedUnits);
    }

    [Fact]
    public void Ratio_AdvancesWhenNewStageStarts()
    {
        var parser = CreateParser("sample1", "sample2");

        parser.Consume("[1/3] 3D 重建: 2 个样本");
        var before = parser.Ratio;

        // 进入下一阶段时，上一阶段视为全部完成（引擎是顺序执行的）
        parser.Consume("[2/3] 弹道拟合: 2 个样本");

        Assert.True(parser.Ratio > before);
    }

    [Fact]
    public void TotalUnits_ScalesWithSampleCount()
    {
        Assert.Equal(3, CreateParser("sample1").TotalUnits);
        Assert.Equal(6, CreateParser("sample1", "sample2").TotalUnits);
        // 一个样本都没有时也不能除以零
        Assert.Equal(1, new PipelineProgressParser([]).TotalUnits);
    }
}
