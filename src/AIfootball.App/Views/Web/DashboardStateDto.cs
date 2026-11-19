using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIfootball.App.Models;
using AIfootball.App.ViewModels;

namespace AIfootball.App.Views.Web;

/// <summary>
/// 前端只拿它真正要显示的东西。这里的字段全部来自真实扫描结果，
/// 拿不到的（例如 Python 版本）宁可不给，也不要在界面里编一个。
/// </summary>
public sealed class DashboardStateDto
{
    public EnvDto Env { get; init; } = new();
    public GpuDto Gpu { get; init; } = new();
    public CalibDto Calibration { get; init; } = new();
    public PipelineDto Pipeline { get; init; } = new();
    public RunDto Run { get; init; } = new();
    public FormDto Form { get; init; } = new();
    public List<SampleDto> Samples { get; init; } = new();
    public List<GoalkeeperDto> Goalkeepers { get; init; } = new();
    public ShooterDto Shooter { get; init; } = new();
    public string StatusBar { get; init; } = "";
    public string Version { get; init; } = "2.0.0";
}

public sealed class EnvDto
{
    public bool Ready { get; init; }
    public string StatusText { get; init; } = "";
    public bool SettingUp { get; init; }
    public int SetupProgress { get; init; }
}

/// <summary>点球手画像：分析页用它渲染指标与雷达，不在前端做二次计算。</summary>
public sealed class ShooterDto
{
    public bool HasData { get; init; }
    public string CountText { get; init; } = "";
    public string[] Lines { get; init; } = [];
    public string[] RadarLabels { get; init; } = [];
    public double[] RadarValues { get; init; } = [];
    public string EmptyText { get; init; } = "";
}

public sealed class GpuDto
{
    public string Name { get; init; } = "";
    public bool CudaAvailable { get; init; }
    public bool Dedicated { get; init; }
    public string CudaVersion { get; init; } = "";
}

public sealed class CalibDto
{
    public bool Intrinsics { get; init; }
    public bool Extrinsics { get; init; }
    public bool Ready => Intrinsics && Extrinsics;

    /// <summary>标定任务是否在跑（内参/外参共用一条进度）。</summary>
    public bool Running { get; init; }
    public int Progress { get; init; }
    public string ProgressText { get; init; } = "";

    // ── 表单：Web 侧是受控组件，这些值由宿主保存并回推 ──
    public string LeftChess { get; init; } = "";
    public string RightChess { get; init; } = "";
    public string LeftField { get; init; } = "";
    public string RightField { get; init; } = "";
    public string IntrinsicsStatus { get; init; } = "";
    public string ExtrinsicsStatus { get; init; } = "";
    public bool ShowWorkflow { get; init; }
}

public sealed class PipelineDto
{
    public int Stage { get; init; }
    public string StatusText { get; init; } = "";
}

/// <summary>处理队列的执行状态：进度条、步骤、在线预览是否在跑。</summary>
public sealed class RunDto
{
    public bool Running { get; init; }
    public bool Previewing { get; init; }
    public bool Analyzing { get; init; }
    public bool TestingStream { get; init; }
    public int Progress { get; init; }
    public string ProgressText { get; init; } = "";
    public bool PreviewVisible { get; init; }

    /// <summary>预览图的最后写入时间戳；前端拿它做缓存击穿，文件没变就不重新拉。</summary>
    public long PreviewLeftStamp { get; init; }
    public long PreviewRightStamp { get; init; }

    public List<StepDto> Steps { get; init; } = new();
}

public sealed class StepDto
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Status { get; init; } = "pending";
}

/// <summary>处理队列表单的当前值。前端是受控组件，改动都要回传宿主再确认。</summary>
public sealed class FormDto
{
    public List<FormSampleDto> Samples { get; init; } = new();
    public bool SkipReconstruct { get; init; }
    public bool SkipBallistic { get; init; }
    public string SelectedGoalkeeper { get; init; } = "";

    public bool UsbMode { get; init; }
    public bool NetworkMode { get; init; }
    public string CamLeft { get; init; } = "";
    public string CamRight { get; init; } = "";
    public string CamerasText { get; init; } = "";
    public string UrlLeft { get; init; } = "";
    public string UrlRight { get; init; } = "";
    public string NetworkTestText { get; init; } = "";
    public string SampleName { get; init; } = "";
    public bool LiveAnalysis { get; init; }
}

public sealed class FormSampleDto
{
    public string Name { get; init; } = "";
    public bool Selected { get; init; }
}

public sealed class SampleDto
{
    public string Name { get; init; } = "";
    public int Videos { get; init; }
    public bool Has3D { get; init; }
    public bool HasBallistic { get; init; }
    public bool HasCsv { get; init; }
    public string ShotSummary { get; init; } = "";
    public string Verdict { get; init; } = "";
    public string Updated { get; init; } = "";
}

public sealed class GoalkeeperDto
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public KeeperAttrsDto Attrs { get; init; } = new();

    /// <summary>按低→高、左→右摊平的 15 格扑救概率，0 表示这份 JSON 里没有该键。</summary>
    public List<double> Zones { get; init; } = new();
    public bool HasZones { get; init; }
}

public sealed class KeeperAttrsDto
{
    public float Height { get; init; }
    public float DiveSpeed { get; init; }
    public float JumpDistance { get; init; }
    public float JumpHeight { get; init; }
    public float Reach { get; init; }
    public float TendGoalSpeed { get; init; }
    public float GoalKeeping { get; init; }
    public float SidePreference { get; init; }
    public float HeightPreference { get; init; }
}

/// <summary>把 ViewModel 的当前状态压成一个可序列化快照。</summary>
public static class DashboardStateBuilder
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 门将属性来自磁盘 JSON，一次扫描要读十来个文件。状态推送 120ms 一次，
    // 每次都读盘不值当，按 (路径 + 最后写入时间) 缓存。
    private static readonly Dictionary<string, (DateTime Stamp, GoalkeeperStats? Stats)> KeeperCache = new();

    public static string BuildJson(MainViewModel vm, PipelineViewModel? pipelineVm) =>
        JsonSerializer.Serialize(Build(vm, pipelineVm), Options);

    public static DashboardStateDto Build(MainViewModel vm, PipelineViewModel? pipelineVm)
    {
        var gpu = vm.GpuInfo;
        var calib = vm.CalibrationStatus;

        return new DashboardStateDto
        {
            Env = new EnvDto
            {
                Ready = vm.IsEnvironmentReady,
                StatusText = vm.EnvStatusText,
                SettingUp = vm.IsSettingUp,
                SetupProgress = vm.SetupProgress,
            },
            Gpu = new GpuDto
            {
                Name = gpu?.GpuName ?? "",
                CudaAvailable = gpu?.CudaAvailable ?? false,
                Dedicated = gpu?.HasDedicatedGpu ?? false,
                CudaVersion = gpu?.CudaVersion ?? "",
            },
            Calibration = new CalibDto
            {
                Intrinsics = calib?.IntrinsicsReady ?? false,
                Extrinsics = calib?.ExtrinsicsReady ?? false,
                Running = vm.IsCalibrationRunning,
                Progress = vm.CalibrationProgress,
                ProgressText = vm.CalibrationProgressText,
                LeftChess = vm.CalibLeftChessVideo,
                RightChess = vm.CalibRightChessVideo,
                LeftField = vm.CalibLeftFieldImage,
                RightField = vm.CalibRightFieldImage,
                IntrinsicsStatus = vm.CalibIntrinsicsStatus,
                ExtrinsicsStatus = vm.CalibExtrinsicsStatus,
                ShowWorkflow = vm.CalibShowWorkflow,
            },
            Pipeline = new PipelineDto
            {
                Stage = vm.PipelineStage,
                StatusText = vm.PipelineStatusText,
            },
            Run = ToRun(pipelineVm),
            Form = ToForm(pipelineVm),
            Samples = vm.Samples.Select(ToSample).ToList(),
            Goalkeepers = vm.Goalkeepers.Select(ToGoalkeeper).ToList(),
            Shooter = vm.ShooterProfile is { } shooter
                ? new ShooterDto
                {
                    HasData = shooter.HasData,
                    CountText = shooter.CountText,
                    Lines = shooter.Lines,
                    RadarLabels = shooter.RadarLabels,
                    RadarValues = shooter.RadarValues,
                    EmptyText = shooter.EmptyText,
                }
                : new ShooterDto { EmptyText = "尚未生成，点「生成画像」开始归纳" },
            StatusBar = vm.StatusBarText,
        };
    }

    private static RunDto ToRun(PipelineViewModel? p)
    {
        if (p is null) return new RunDto();

        return new RunDto
        {
            Running = p.IsRunning,
            Previewing = p.IsPreviewing,
            Analyzing = p.IsAnalyzing,
            TestingStream = p.IsTestingStream,
            Progress = p.ProcessingProgress,
            ProgressText = p.ProcessingProgressText,
            PreviewVisible = p.IsPreviewVisible,
            PreviewLeftStamp = FileStamp(Path.Combine(p.LivePreviewDirectory, "live_preview_left.jpg")),
            PreviewRightStamp = FileStamp(Path.Combine(p.LivePreviewDirectory, "live_preview_right.jpg")),
            Steps = p.Steps.Select(s => new StepDto
            {
                Name = s.Name,
                Description = s.Description,
                Status = s.Status.ToString().ToLowerInvariant(),
            }).ToList(),
        };
    }

    private static long FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0 ? info.LastWriteTimeUtc.Ticks : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static FormDto ToForm(PipelineViewModel? p)
    {
        if (p is null) return new FormDto();
        return new FormDto
        {
            Samples = p.SampleItems.Select(s => new FormSampleDto
            {
                Name = s.Name,
                Selected = s.IsSelected,
            }).ToList(),
            SkipReconstruct = p.SkipReconstruct,
            SkipBallistic = p.SkipBallistic,
            SelectedGoalkeeper = p.SelectedGoalkeeper?.Name ?? "",
            UsbMode = p.IsUsbMode,
            NetworkMode = p.IsNetworkMode,
            CamLeft = p.CamLeft,
            CamRight = p.CamRight,
            CamerasText = p.CamerasText,
            UrlLeft = p.UrlLeft,
            UrlRight = p.UrlRight,
            NetworkTestText = p.NetworkTestText,
            SampleName = p.SampleName,
            LiveAnalysis = p.IsLiveAnalysis,
        };
    }

    private static SampleDto ToSample(SampleInfo s) => new()
    {
        Name = s.Name,
        Videos = s.VideoCount,
        Has3D = s.HasResult3D,
        HasBallistic = s.HasResultBallistic,
        HasCsv = s.HasUnityCsv,
        ShotSummary = s.HasShotPlacement ? s.ShotSummary : "",
        Verdict = s.ShotVerdict,
        Updated = TryGetUpdated(s.DirectoryPath),
    };

    private static GoalkeeperDto ToGoalkeeper(GoalkeeperInfo g)
    {
        var stats = LoadStats(g);
        if (stats is null) return new GoalkeeperDto { Name = g.Name };

        return new GoalkeeperDto
        {
            Name = g.Name,
            DisplayName = string.IsNullOrWhiteSpace(stats.DisplayName) ? g.Name : stats.DisplayName,
            Description = stats.Description,
            Attrs = new KeeperAttrsDto
            {
                Height = stats.Height,
                DiveSpeed = stats.DiveSpeed,
                JumpDistance = stats.JumpDistance,
                JumpHeight = stats.JumpHeight,
                Reach = stats.Reach,
                TendGoalSpeed = stats.TendGoalSpeed,
                GoalKeeping = stats.GoalKeeping,
                SidePreference = stats.SidePreference,
                HeightPreference = stats.HeightPreference,
            },
            Zones = stats.ToZoneGrid().ToList(),
            HasZones = stats.HasProbabilityMap,
        };
    }

    private static GoalkeeperStats? LoadStats(GoalkeeperInfo g)
    {
        try
        {
            var info = new FileInfo(g.FilePath);
            if (!info.Exists) return null;

            if (KeeperCache.TryGetValue(g.FilePath, out var hit) && hit.Stamp == info.LastWriteTimeUtc)
                return hit.Stats;

            var stats = JsonSerializer.Deserialize<GoalkeeperStats>(File.ReadAllText(g.FilePath));
            KeeperCache[g.FilePath] = (info.LastWriteTimeUtc, stats);
            return stats;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>样本目录最后写入时间，比"扫描时间"更能说明这份数据有多新。</summary>
    private static string TryGetUpdated(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return "";
            var t = Directory.GetLastWriteTime(directory);
            var today = DateTime.Now.Date;
            var day = t.Date == today ? "今天"
                : t.Date == today.AddDays(-1) ? "昨天"
                : t.ToString("MM-dd");
            return $"{day} {t:HH:mm}";
        }
        catch
        {
            return "";
        }
    }
}
