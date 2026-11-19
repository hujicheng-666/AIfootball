namespace AIfootball.Core.Models;

public enum LogLevel { Trace, Info, Success, Warning, Error }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message, string Source = "app")
{
    public string TimeLabel => Timestamp.ToString("HH:mm:ss");

    public static LogEntry Info(string message, string source = "app") =>
        new(DateTimeOffset.Now, LogLevel.Info, message, source);
}

/// <summary>流水线的四个阶段，与引擎实际执行顺序一致。</summary>
public enum PipelineStage
{
    Idle = 0,
    Reconstruct = 1,
    Ballistic = 2,
    Export = 3,
    Done = 4,
}

public enum StageState { Pending, Running, Completed, Failed, Skipped }

/// <summary>
/// 某个样本在某个阶段的状态。
/// 实现 INotifyPropertyChanged 是刻意的：进度由后台线程高频刷新，
/// 若靠"整个集合重置"来驱动 UI，会连带重建控件、丢掉动画与滚动位置。
/// </summary>
public sealed class StageProgress : System.ComponentModel.INotifyPropertyChanged
{
    private StageState _state = StageState.Pending;
    private int _completedUnits;
    private int _totalUnits;

    public required PipelineStage Stage { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }

    public StageState State
    {
        get => _state;
        set
        {
            if (_state == value) return;
            _state = value;
            Raise(nameof(State));
            Raise(nameof(StateLabel));
        }
    }

    public int CompletedUnits
    {
        get => _completedUnits;
        set
        {
            if (_completedUnits == value) return;
            _completedUnits = value;
            Raise(nameof(CompletedUnits));
            Raise(nameof(Ratio));
        }
    }

    public int TotalUnits
    {
        get => _totalUnits;
        set
        {
            if (_totalUnits == value) return;
            _totalUnits = value;
            Raise(nameof(TotalUnits));
            Raise(nameof(Ratio));
        }
    }

    public double Ratio => TotalUnits <= 0 ? 0 : Math.Clamp((double)CompletedUnits / TotalUnits, 0, 1);

    public string StateLabel => State switch
    {
        StageState.Running => "进行中",
        StageState.Completed => "已完成",
        StageState.Failed => "失败",
        StageState.Skipped => "已跳过",
        _ => "等待",
    };

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>一次流水线运行的完整进度快照，供 UI 绑定。</summary>
public sealed class PipelineRunState
{
    public string Title { get; set; } = "空闲";
    public string Detail { get; set; } = "等待任务";
    public bool IsRunning { get; set; }
    public double OverallRatio { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public IReadOnlyList<StageProgress> Stages { get; init; } = [];

    public TimeSpan Elapsed => StartedAt is { } start ? DateTimeOffset.Now - start : TimeSpan.Zero;
}
