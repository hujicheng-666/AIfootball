using AIfootball.Core.Engine;
using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

/// <summary>离线批处理的请求描述。</summary>
public sealed record OfflineJob(
    IReadOnlyList<string> Samples,
    string? GoalkeeperName,
    bool SkipReconstruction,
    bool SkipBallistic,
    int ImageSize = 1280,
    double Confidence = 0.15);

/// <summary>在线采集会话：既能下发 start/stop，也能等待结束。</summary>
public sealed class CaptureSession
{
    private readonly Action<string>? _send;

    internal CaptureSession(Action<string>? send, Task<EngineResult> completion, bool liveAnalysis)
    {
        _send = send;
        Completion = completion;
        LiveAnalysis = liveAnalysis;
    }

    public bool LiveAnalysis { get; }
    public Task<EngineResult> Completion { get; }
    public bool CanSendCommand => _send is not null;

    /// <summary>下发指令（start / stop）。采集进程按行协议读取 stdin。</summary>
    public void Send(string command) => _send?.Invoke(command);

    public void Start() => Send("start");
    public void Stop() => Send("stop");
}

/// <summary>
/// 流水线编排：所有 Python 调用都收敛到这一个入口，UI 只订阅结构化进度。
/// </summary>
public sealed partial class PipelineRunner(PythonEngine engine, AifootballWorkspace workspace)
{
    private readonly PythonEngine _engine = engine;
    private readonly AifootballWorkspace _workspace = workspace;
    private readonly TrajectoryStore _trajectories = new(workspace);

    /// <summary>解析后的进度事件流（UI 线程之外触发，调用方自行切线程）。</summary>
    public event Action<PipelineProgressEvent>? ProgressChanged;

    /// <summary>原始日志行（含 stdout/stderr），用于控制台视图。</summary>
    public event Action<LogEntry>? LogEmitted;

    public async Task<EngineResult> RunOfflineAsync(OfflineJob job, CancellationToken cancellation)
    {
        var arguments = new List<string> { "offline", "--samples" };
        arguments.AddRange(job.Samples);
        arguments.AddRange(["--imgsz", job.ImageSize.ToString(), "--conf", job.Confidence.ToString("0.###")]);
        if (job.SkipReconstruction) arguments.Add("--skip-reconstruct");
        if (job.SkipBallistic) arguments.Add("--skip-ballistic");
        arguments.AddRange(["--workspace", _workspace.Root]);

        var parser = new PipelineProgressParser(job.Samples);
        var result = await _engine.RunAsync(arguments, new EngineRunOptions
        {
            // 视频重建耗时由片段长度决定，必须允许用户取消，但不能被固定墙钟时间掐断
            TimeoutMs = Timeout.Infinite,
            LogSource = "offline",
            OnLog = entry =>
            {
                LogEmitted?.Invoke(entry);
                if (entry.Level == LogLevel.Warning && LooksLikeFatal(entry.Message))
                {
                    ProgressChanged?.Invoke(new PipelineProgressEvent(
                        PipelineStage.Idle, StageState.Failed, entry.Message));
                    return;
                }
                var progress = parser.Consume(entry.Message);
                if (progress is not null) ProgressChanged?.Invoke(progress);
            },
        }, cancellation).ConfigureAwait(false);

        if (result.Ok && !string.IsNullOrWhiteSpace(job.GoalkeeperName))
        {
            foreach (var sample in job.Samples)
                if (File.Exists(_workspace.UnityCsv(sample)))
                    _trajectories.SaveMetadata(sample, job.GoalkeeperName);
        }

        ProgressChanged?.Invoke(new PipelineProgressEvent(
            result.Ok ? PipelineStage.Done : PipelineStage.Idle,
            result.Ok ? StageState.Completed : StageState.Failed,
            result.Ok ? "全部阶段已完成" : $"处理失败：{result.LastLine}",
            null, result.Ok ? 1 : null));

        return result;
    }

    private static bool LooksLikeFatal(string line) =>
        line.Contains("Traceback", StringComparison.Ordinal) ||
        line.Contains("ModuleNotFoundError", StringComparison.Ordinal) ||
        line.Contains("FileNotFoundError", StringComparison.Ordinal);

    /// <summary>启动在线采集。预览与边录制边分析走同一套控制通道。</summary>
    public CaptureSession StartCapture(
        string cameraLeft,
        string cameraRight,
        string sampleName,
        bool liveAnalysis,
        int imageSize = 1280,
        double confidence = 0.15)
    {
        var arguments = new List<string>
        {
            liveAnalysis ? "online-live" : "online",
            "--cam-left", cameraLeft,
            "--cam-right", cameraRight,
            "--sample", sampleName,
            "--workspace", _workspace.Root,
        };
        if (liveAnalysis)
            arguments.AddRange(["--imgsz", imageSize.ToString(), "--conf", confidence.ToString("0.###")]);

        Action<string>? sender = null;
        var completion = _engine.RunAsync(arguments, new EngineRunOptions
        {
            TimeoutMs = Timeout.Infinite,
            LogSource = liveAnalysis ? "online-live" : "online",
            OnControlChannelReady = send => sender = send,
            OnLog = entry =>
            {
                LogEmitted?.Invoke(entry);
                var progress = ConsumeCaptureLine(entry.Message);
                if (progress is not null) ProgressChanged?.Invoke(progress);
            },
        });

        return new CaptureSession(sender, completion, liveAnalysis);
    }

    private static PipelineProgressEvent? ConsumeCaptureLine(string line)
    {
        var text = line.Trim();
        if (text.Length == 0) return null;
        if (text.Contains("等待画面", StringComparison.Ordinal) || text.Contains("取流", StringComparison.Ordinal))
            return new PipelineProgressEvent(PipelineStage.Idle, StageState.Running, "等待相机画面…");
        if (text.Contains("开始录制", StringComparison.Ordinal) || text.Contains("录制中", StringComparison.Ordinal))
            return new PipelineProgressEvent(PipelineStage.Idle, StageState.Running, "正在录制…");
        if (text.Contains("kick_frame", StringComparison.Ordinal))
            return new PipelineProgressEvent(PipelineStage.Reconstruct, StageState.Running, "实时检测中…");
        if (text.Contains("端口已被", StringComparison.Ordinal))
            return new PipelineProgressEvent(PipelineStage.Idle, StageState.Failed, text);
        return null;
    }

    public Task<EngineResult> CalibrateIntrinsicsAsync(string leftVideo, string rightVideo, CancellationToken cancellation)
    {
        var arguments = new List<string>
        {
            "calibrate-intrinsics",
            "--left", leftVideo,
            "--right", rightVideo,
            "--calib", _workspace.CalibrationDir,
            "--workspace", _workspace.Root,
        };
        return RunLoggedAsync(arguments, "calibrate-intrinsics", Timeout.Infinite, cancellation);
    }

    public Task<EngineResult> CalibrateExtrinsicsAsync(string leftImage, string rightImage, CancellationToken cancellation)
    {
        var arguments = new List<string>
        {
            "calibrate-extrinsics",
            "--left-image", leftImage,
            "--right-image", rightImage,
            "--intrinsics-left", Path.Combine(_workspace.CalibrationDir, "intrinsics_left.npz"),
            "--intrinsics-right", Path.Combine(_workspace.CalibrationDir, "intrinsics_right.npz"),
            "--calib", _workspace.CalibrationDir,
            "--workspace", _workspace.Root,
        };
        // 外参标定需要用户在 OpenCV 窗口里点参考点，不能用墙钟时间掐断
        return RunLoggedAsync(arguments, "calibrate-extrinsics", Timeout.Infinite, cancellation);
    }

    public async Task<(bool Ok, string Message)> TestStreamAsync(string url, int timeoutSeconds = 8,
        CancellationToken cancellation = default)
    {
        var arguments = new List<string>
        {
            "teststream", "--source", url, "--timeout", timeoutSeconds.ToString(),
            "--workspace", _workspace.Root,
        };
        var result = await _engine.RunAsync(arguments, new EngineRunOptions
        {
            TimeoutMs = (timeoutSeconds + 30) * 1000,
            LogSource = "teststream",
        }, cancellation).ConfigureAwait(false);

        var message = result.StandardOutput.Trim();
        if (message.Length == 0) message = result.StandardError.Trim();
        if (message.Length == 0) message = $"测试失败（退出码 {result.ExitCode}）";
        return (result.Ok, message);
    }

    public async Task<IReadOnlyList<int>> ListCamerasAsync(CancellationToken cancellation = default)
    {
        var result = await _engine.RunAsync(["list-cameras"], new EngineRunOptions
        {
            TimeoutMs = 60_000,
            LogSource = "list-cameras",
        }, cancellation).ConfigureAwait(false);

        if (!result.Ok) return [];
        return [.. result.StandardOutput
            .Split([',', '\n', '\r', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => int.TryParse(token, out var index) ? index : -1)
            .Where(index => index >= 0)];
    }

    private Task<EngineResult> RunLoggedAsync(List<string> arguments, string source, int timeoutMs,
        CancellationToken cancellation)
    {
        return _engine.RunAsync(arguments, new EngineRunOptions
        {
            TimeoutMs = timeoutMs,
            LogSource = source,
            OnLog = entry =>
            {
                LogEmitted?.Invoke(entry);
                var progress = ParseCalibrationProgress(entry.Message);
                if (progress is not null) ProgressChanged?.Invoke(progress);
            },
        }, cancellation);
    }

    private static PipelineProgressEvent? ParseCalibrationProgress(string line)
    {
        var match = CalibrationPercentPattern().Match(line);
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups["percent"].Value, out var percent)) return null;
        return new PipelineProgressEvent(PipelineStage.Idle, StageState.Running, line.Trim(),
            null, Math.Clamp(percent / 100.0, 0, 1));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<percent>\d{1,3})\s*%")]
    private static partial System.Text.RegularExpressions.Regex CalibrationPercentPattern();
}
