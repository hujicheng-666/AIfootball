using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AIfootball.App.Models;

/// <summary>处理样本信息</summary>
public record SampleInfo(
    string Name,
    string DirectoryPath,
    int VideoCount,
    bool HasResult3D,
    bool HasResultBallistic,
    bool HasUnityCsv
);

/// <summary>流水线步骤状态</summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}

/// <summary>流水线步骤</summary>
public record PipelineStep(
    string Name,
    string Description,
    StepStatus Status = StepStatus.Pending
)
{
    public string StatusIcon => Status switch
    {
        StepStatus.Pending => "○",
        StepStatus.Running => "◉",
        StepStatus.Completed => "✓",
        StepStatus.Failed => "✗",
        StepStatus.Skipped => "→",
        _ => "○",
    };
}

/// <summary>环境状态信息</summary>
public record EnvironmentStatus(
    bool IsReady,
    string PythonVersion,
    string TorchVersion,
    string InferenceProfile, // "cuda-cu124" or "cpu"
    bool CudaAvailable,
    string GpuName,
    string GpuAdapterNames
)
{
    public static EnvironmentStatus Unknown => new(false, "", "", "", false, "", "");
}

/// <summary>GPU 信息</summary>
public record GpuInfo(
    bool HasNvidiaGpu,
    bool CudaAvailable,
    string GpuName,
    string CudaVersion,
    bool HasDedicatedGpu,
    string AdapterNames
);

/// <summary>校准状态</summary>
public record CalibrationStatus(
    bool IntrinsicsReady,
    bool ExtrinsicsReady,
    string LeftIntrinsicsPath,
    string RightIntrinsicsPath,
    string LeftPosePath,
    string RightPosePath
)
{
    public bool FullyReady => IntrinsicsReady && ExtrinsicsReady;
}

/// <summary>门将数据</summary>
public record GoalkeeperInfo(
    string Name,
    string FilePath
);

/// <summary>门将属性（从 data/goalkeepers/*.json 解析，雷达图用）</summary>
public class GoalkeeperStats
{
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public float Height { get; set; } = 1.9f;
    public float DiveSpeed { get; set; } = 4f;
    public float JumpDistance { get; set; } = 1f;
    public float JumpHeight { get; set; } = 0.5f;
    public float Reach { get; set; } = 0.5f;
    public float TendGoalSpeed { get; set; } = 3f;
    public float TendGoalDistance { get; set; } = 3f;
    public float GoalKeeping { get; set; } = 0.85f;
    public float SidePreference { get; set; } = 0f;
    public float HeightPreference { get; set; } = 0f;
}

/// <summary>样本选择包装（用于 Pipeline 复选框绑定）</summary>
public class SampleItem : INotifyPropertyChanged
{
    public SampleInfo Info { get; }
    public string Name => Info.Name;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; Notify(); }
    }

    public SampleItem(SampleInfo info) { Info = info; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>应用日志条目</summary>
public record LogEntry(
    DateTime Timestamp,
    string Level,
    string Message
);
