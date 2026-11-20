using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;
using AIfootball.Core.Services;
using PipelineStage = AIfootball.Core.Models.PipelineStage;
using StageState = AIfootball.Core.Models.StageState;

namespace AIfootball.App.ViewModels;

/// <summary>流水线 ViewModel — 离线/在线处理</summary>
public class PipelineViewModel : ViewModelBase
{
    private readonly IPipelineService _pipelineService;
    private readonly IPythonEngine _pythonEngine;
    private readonly MainViewModel _mainVm;

    public PipelineViewModel(IPipelineService pipelineService, IPythonEngine pythonEngine,
        MainViewModel mainVm)
    {
        _pipelineService = pipelineService;
        _pythonEngine = pythonEngine;
        _mainVm = mainVm;

        RunOfflineCommand = new RelayCommand(async () => await RunOfflineAsync(),
            () => !IsRunning);
        RunOnlineCommand = new RelayCommand(async () => await RunOnlineAsync(),
            () => !IsRunning);
        DetectCamerasCommand = new RelayCommand(async () => await DetectCamerasAsync());
        TestNetworkCommand = new RelayCommand(async () => await TestNetworkAsync());
        CancelCommand = new RelayCommand(OnCancel, () => IsRunning);
        StartAnalysisCommand = new RelayCommand(OnStartAnalysis,
            () => IsPreviewing && _sendControl != null);
        StopAnalysisCommand = new RelayCommand(OnStopAnalysis,
            () => IsAnalyzing && _sendControl != null);
    }

    // ─── 离线模式 ───
    public ObservableCollection<SampleInfo> Samples => _mainVm.Samples;
    public ObservableCollection<SampleItem> SampleItems { get; } = new();

    private bool _skipReconstruct;
    public bool SkipReconstruct
    {
        get => _skipReconstruct;
        set => SetProperty(ref _skipReconstruct, value);
    }

    private bool _skipBallistic;
    public bool SkipBallistic
    {
        get => _skipBallistic;
        set => SetProperty(ref _skipBallistic, value);
    }

    // ─── 在线模式 ───
    private string _camLeft = "0";
    public string CamLeft
    {
        get => _camLeft;
        set => SetProperty(ref _camLeft, value);
    }

    private string _camRight = "1";
    public string CamRight
    {
        get => _camRight;
        set => SetProperty(ref _camRight, value);
    }

    private string _sampleName = "sample_live";
    public string SampleName
    {
        get => _sampleName;
        set => SetProperty(ref _sampleName, value);
    }

    private string _camerasText = "";
    public string CamerasText
    {
        get => _camerasText;
        set => SetProperty(ref _camerasText, value);
    }

    // ─── 采集方式：USB 相机 / 手机网络流 ───
    private bool _isUsbMode = true;
    public bool IsUsbMode
    {
        get => _isUsbMode;
        set
        {
            if (!SetProperty(ref _isUsbMode, value)) return;
            if (value) IsNetworkMode = false;
        }
    }

    private bool _isNetworkMode;
    public bool IsNetworkMode
    {
        get => _isNetworkMode;
        set
        {
            if (!SetProperty(ref _isNetworkMode, value)) return;
            if (value) IsUsbMode = false;
        }
    }

    private string _urlLeft = "udp://@0.0.0.0:5000?fifo_size=5000000&overrun_nonfatal=1&buffer_size=8388608";
    public string UrlLeft
    {
        get => _urlLeft;
        set => SetProperty(ref _urlLeft, value);
    }

    private string _urlRight = "udp://@0.0.0.0:5001?fifo_size=5000000&overrun_nonfatal=1&buffer_size=8388608";
    public string UrlRight
    {
        get => _urlRight;
        set => SetProperty(ref _urlRight, value);
    }

    private string _networkTestText = "";
    public string NetworkTestText
    {
        get => _networkTestText;
        set => SetProperty(ref _networkTestText, value);
    }

    private bool _isTestingStream;
    public bool IsTestingStream
    {
        get => _isTestingStream;
        set
        {
            if (!SetProperty(ref _isTestingStream, value)) return;
            ((RelayCommand)TestNetworkCommand).CanExecute(null);
        }
    }

    // ─── 边录制边分析 ───
    private bool _isLiveAnalysis;
    public bool IsLiveAnalysis
    {
        get => _isLiveAnalysis;
        set => SetProperty(ref _isLiveAnalysis, value);
    }

    // ─── 在线实时预览（Python 每帧写 output/live_preview/*.jpg，WPF 定时读取显示）───
    private ImageSource? _livePreviewLeft;
    public ImageSource? LivePreviewLeft
    {
        get => _livePreviewLeft;
        set => SetProperty(ref _livePreviewLeft, value);
    }

    private ImageSource? _livePreviewRight;
    public ImageSource? LivePreviewRight
    {
        get => _livePreviewRight;
        set => SetProperty(ref _livePreviewRight, value);
    }

    private bool _isPreviewVisible;
    public bool IsPreviewVisible
    {
        get => _isPreviewVisible;
        set => SetProperty(ref _isPreviewVisible, value);
    }

    // ─── 预览 / 分析两级控制 ───
    // 采集进程先只做预览，收到 "start" 才加载模型并开始录制/分析，
    // 收到 "stop" 停止并出结果。状态以进程回报的 [CONTROL] 标记为准。
    private Action<string>? _sendControl;
    private bool _isAnalyzing;

    /// <summary>采集进程已进入分析（录制 + 实时检测）阶段。</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (!SetProperty(ref _isAnalyzing, value)) return;
            OnPropertyChanged(nameof(IsPreviewing));
            ((RelayCommand)StartAnalysisCommand).CanExecute(null);
            ((RelayCommand)StopAnalysisCommand).CanExecute(null);
        }
    }

    /// <summary>任务已启动但还没开始分析：此刻处于预览，等用户确认画面。</summary>
    public bool IsPreviewing => IsRunning && !IsAnalyzing;

    private void OnStartAnalysis()
    {
        if (_sendControl == null) return;
        _mainVm.AddLog("info", "已请求开始分析，正在加载检测模型...");
        // 先乐观切换按钮；进程确认（analyzing=1）或拒绝时会纠正回来
        IsAnalyzing = true;
        _sendControl("start");
    }

    private void OnStopAnalysis()
    {
        if (_sendControl == null) return;
        _mainVm.AddLog("info", "已请求停止分析，正在收尾...");
        _sendControl("stop");
    }

    /// <summary>
    /// 消费采集进程回报的控制标记。返回 true 表示这行只是内部控制协议，
    /// 不必写进用户日志。
    /// </summary>
    private bool HandleControlMessage(string message)
    {
        if (message.Contains("暂时无法开始分析", StringComparison.Ordinal))
        {
            // 某一路还没画面，进程仍停在预览；把按钮切回「开始分析」
            IsAnalyzing = false;
            return false;
        }
        if (message.Contains("[CONTROL] analyzing=1", StringComparison.Ordinal))
        {
            IsAnalyzing = true;
            return true;
        }
        if (message.Contains("[CONTROL] analyzing=0", StringComparison.Ordinal))
        {
            IsAnalyzing = false;
            return true;
        }
        return false;
    }

    private DispatcherTimer? _previewTimer;
    // 记录上一次已解码的预览文件时间戳：Python 端约 20fps 发布新帧，
    // 定时器（50ms）触发时若文件没变就跳过解码，避免重复解码同一张图。
    private DateTime _previewStampLeft = DateTime.MinValue;
    private DateTime _previewStampRight = DateTime.MinValue;

    private string PreviewDirectory =>
        Path.Combine(_pythonEngine.WorkspaceDir, "output", "live_preview");

    /// <summary>预览帧目录。Web 界面把这一层映射成虚拟主机直接取图，不必逐帧转码。</summary>
    public string LivePreviewDirectory => PreviewDirectory;

    /// <summary>启动定时刷新预览：约 20fps 读取 Python 写的最新 JPEG。</summary>
    private void StartLivePreview()
    {
        if (_previewTimer != null) return;
        _previewStampLeft = DateTime.MinValue;
        _previewStampRight = DateTime.MinValue;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _previewTimer.Tick += (_, _) => RefreshLivePreview();
        _previewTimer.Start();
        IsPreviewVisible = true;
        RefreshLivePreview();
    }

    private void StopLivePreview()
    {
        _previewTimer?.Stop();
        _previewTimer = null;
        IsPreviewVisible = false;
        _previewStampLeft = DateTime.MinValue;
        _previewStampRight = DateTime.MinValue;
        LivePreviewLeft = null;
        LivePreviewRight = null;
    }

    private void RefreshLivePreview()
    {
        LoadPreviewFrame("left");
        LoadPreviewFrame("right");
    }

    private void LoadPreviewFrame(string side)
    {
        var path = Path.Combine(PreviewDirectory, $"live_preview_{side}.jpg");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0) return;

            var stamp = info.LastWriteTimeUtc;
            if (side == "left")
            {
                if (stamp == _previewStampLeft) return;
            }
            else if (stamp == _previewStampRight)
            {
                return;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length == 0) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze(); // 供跨线程 UI 使用
            if (side == "left")
            {
                _previewStampLeft = stamp;
                LivePreviewLeft = bmp;
            }
            else
            {
                _previewStampRight = stamp;
                LivePreviewRight = bmp;
            }
        }
        catch
        {
            // 预览失败忽略，不阻塞录制
        }
    }

    // ─── 步骤进度 ───
    public ObservableCollection<PipelineStep> Steps { get; } = new();

    private int _processingProgress;
    public int ProcessingProgress
    {
        get => _processingProgress;
        set => SetProperty(ref _processingProgress, value);
    }

    private string _processingProgressText = "待启动";
    public string ProcessingProgressText
    {
        get => _processingProgressText;
        set => SetProperty(ref _processingProgressText, value);
    }

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            SetProperty(ref _isRunning, value);
            OnPropertyChanged(nameof(IsPreviewing));
            ((RelayCommand)RunOfflineCommand).CanExecute(null);
            ((RelayCommand)RunOnlineCommand).CanExecute(null);
            ((RelayCommand)CancelCommand).CanExecute(null);
            ((RelayCommand)StartAnalysisCommand).CanExecute(null);
            ((RelayCommand)StopAnalysisCommand).CanExecute(null);
        }
    }

    // ─── 门将选择 ───
    public ObservableCollection<GoalkeeperInfo> Goalkeepers => _mainVm.Goalkeepers;

    private GoalkeeperInfo? _selectedGoalkeeper;
    public GoalkeeperInfo? SelectedGoalkeeper
    {
        get => _selectedGoalkeeper;
        set => SetProperty(ref _selectedGoalkeeper, value);
    }

    // ─── 命令 ───
    public RelayCommand RunOfflineCommand { get; }
    public RelayCommand RunOnlineCommand { get; }
    public RelayCommand DetectCamerasCommand { get; }
    public RelayCommand TestNetworkCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand StartAnalysisCommand { get; }
    public RelayCommand StopAnalysisCommand { get; }

    private CancellationTokenSource? _cts;

    // ─── 批次处理进度状态机（一次跑完所有样本的同一阶段） ───
    private List<string> _processingSamples = new();
    private int _stageBase;
    private int _stageSpan = 100;
    private string _stageDescription = "";
    private int _currentSampleIndex;

    // 日志 → 结构化进度的解析器。每次开始处理都按本批样本重建，
    // 否则上一轮记住的"已完成"会让这一轮的进度条一开局就是满的。
    private PipelineProgressParser? _progressParser;

    private PipelineProgressParser ProgressParser =>
        _progressParser ??= new PipelineProgressParser(_processingSamples);

    private async Task RunOfflineAsync()
    {
        if (!_mainVm.IsEnvironmentReady)
        {
            ProcessingProgressText = "Python 环境未就绪，请先安装或等待自检完成";
            _mainVm.AddLog("warn", ProcessingProgressText);
            return;
        }

        // 同步 SampleItems
        SyncSampleItems();

        var selectedNames = SampleItems.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        var targetSamples = selectedNames.Count > 0
            ? selectedNames
            : Samples.Select(s => s.Name).ToList();

        if (targetSamples.Count == 0)
        {
            ProcessingProgressText = "没有可处理的样本";
            _mainVm.AddLog("warn", ProcessingProgressText);
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        InitSteps(targetSamples);
        ProcessingProgress = 0;
        ProcessingProgressText = "等待推理任务启动";
        _mainVm.SetPipelineState(
            !SkipReconstruct ? 2 : !SkipBallistic ? 3 : 4,
            !SkipReconstruct ? "准备三维重建" : !SkipBallistic ? "准备弹道拟合" : "准备 Unity 导出");

        _mainVm.AddLog("info", $"======== 离线处理 {targetSamples.Count} 个样本 ========");

        string? stageFailure = null;
        var progress = new Progress<(string Message, string Level)>(p =>
        {
            _mainVm.AddLog(p.Level, p.Message);
            var message = p.Message.TrimStart();
            if (message.StartsWith("失败:", StringComparison.Ordinal))
                stageFailure ??= message["失败:".Length..].Trim();
            HandleLogLine(p.Message);
        });

        bool success = false;
        try
        {
            success = await _pipelineService.RunOfflineAsync(
                targetSamples, SkipReconstruct, SkipBallistic,
                SelectedGoalkeeper?.Name,
                progress, _cts.Token);
            CompleteRemainingSteps(success);
            if (success)
            {
                ProcessingProgress = 100;
                ProcessingProgressText = "已完成";
                _mainVm.SetPipelineState(5, "处理完成");
            }
            else
            {
                ProcessingProgressText = _cts.IsCancellationRequested ? "处理已中止"
                    : stageFailure is { Length: > 0 } ? $"处理失败：{stageFailure}"
                    : "处理失败，详情见 logs/";
                _mainVm.SetPipelineState(-1, "处理失败");
            }
            _mainVm.AddLog(success ? "success" : "error",
                success ? "======== 全部完成 ========" : "======== 处理失败 ========");
            await _mainVm.RefreshListsAsync();
        }
        catch (Exception ex)
        {
            CompleteRemainingSteps(false);
            ProcessingProgressText = $"处理失败：{ex.Message}";
            _mainVm.SetPipelineState(-1, "处理异常");
            _mainVm.AddLog("error", "流水线异常: " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunOnlineAsync()
    {
        if (!_mainVm.IsEnvironmentReady)
        {
            _mainVm.AddLog("warn", "Python 环境未就绪，请先在仪表盘点击「一键安装环境」");
            return;
        }

        // 根据采集方式构造左右相机源（本地索引或网络 URL）
        string camLeft, camRight;
        if (IsNetworkMode)
        {
            camLeft = UrlLeft?.Trim() ?? "";
            camRight = UrlRight?.Trim() ?? "";
            if (string.IsNullOrEmpty(camLeft) || string.IsNullOrEmpty(camRight))
            {
                _mainVm.AddLog("warn", "请填写左右两个网络视频流地址");
                return;
            }
            _mainVm.AddLog("info", $"使用手机/网络相机: 左={camLeft} 右={camRight}");
        }
        else
        {
            camLeft = string.IsNullOrWhiteSpace(CamLeft) ? "0" : CamLeft.Trim();
            camRight = string.IsNullOrWhiteSpace(CamRight) ? "1" : CamRight.Trim();
            _mainVm.AddLog("info", $"使用 USB 相机: 左={camLeft} 右={camRight}");
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        InitSteps(new List<string> { SampleName });
        ProcessingProgress = 0;
        ProcessingProgressText = "等待采集任务启动";
        _mainVm.SetPipelineState(1, "双摄采集中");
        // 启动实时预览：Python 录制时每帧写 JPEG，WPF 定时读到 Image 显示
        StartLivePreview();

        _mainVm.AddLog("info", $"======== 在线录制: {SampleName} ========");

        var progress = new Progress<(string Message, string Level)>(p =>
        {
            // [CONTROL] 是进程与界面之间的控制协议，不进用户日志
            if (HandleControlMessage(p.Message)) return;
            _mainVm.AddLog(p.Level, p.Message);
            HandleLogLine(p.Message);
        });
        try
        {
            var success = IsLiveAnalysis
                ? await _pipelineService.RunOnlineLiveAsync(
                    camLeft, camRight, SampleName, progress, _cts.Token,
                    sink => _sendControl = sink)
                : await _pipelineService.RunOnlineAsync(
                    camLeft, camRight, SampleName, progress, _cts.Token,
                    sink => _sendControl = sink);

            CompleteRemainingSteps(success);
            if (success)
            {
                ProcessingProgress = 100;
                ProcessingProgressText = "录制与处理已完成";
                _mainVm.SetPipelineState(5, "录制与处理已完成");
            }
            else
                _mainVm.SetPipelineState(-1, "录制或处理失败");
            _mainVm.AddLog(success ? "success" : "error",
                success ? "录制与处理已完成" : "录制或处理失败");
            await _mainVm.RefreshListsAsync();
        }
        catch (Exception ex)
        {
            CompleteRemainingSteps(false);
            ProcessingProgressText = "任务异常，详情见日志";
            _mainVm.SetPipelineState(-1, "任务异常");
            _mainVm.AddLog("error", "在线任务异常: " + ex.Message);
        }
        finally
        {
            StopLivePreview();
            // 进程已经退出，指令通道随之失效
            _sendControl = null;
            IsAnalyzing = false;
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task DetectCamerasAsync()
    {
        var cameras = await _pipelineService.DetectCamerasAsync();
        if (cameras.Count > 0)
            CamerasText = $"发现 {cameras.Count} 个摄像头: {string.Join(", ", cameras)}";
        else
            CamerasText = "未检测到摄像头";
    }

    private async Task TestNetworkAsync()
    {
        var leftUrl = IsNetworkMode ? UrlLeft?.Trim() ?? "" : "";
        var rightUrl = IsNetworkMode ? UrlRight?.Trim() ?? "" : "";
        if (string.IsNullOrEmpty(leftUrl) || string.IsNullOrEmpty(rightUrl))
        {
            NetworkTestText = "请先填写左右两个网络流地址";
            return;
        }

        IsTestingStream = true;
        NetworkTestText = "正在连接测试...";
        try
        {
            var (leftOk, leftMessage) = await _pipelineService.TestNetworkStreamAsync(leftUrl);
            var (rightOk, rightMessage) = await _pipelineService.TestNetworkStreamAsync(rightUrl);
            NetworkTestText = $"左路 {(leftOk ? "✅" : "❌")} {leftMessage}\n" +
                              $"右路 {(rightOk ? "✅" : "❌")} {rightMessage}";
        }
        catch (Exception ex)
        {
            NetworkTestText = $"❌ 测试异常: {ex.Message}";
        }
        finally
        {
            IsTestingStream = false;
        }
    }

    private void OnCancel()
    {
        _cts?.Cancel();
        _mainVm.SetPipelineState(-1, "任务已取消");
        _mainVm.AddLog("warn", "操作已取消");
    }

    private void InitSteps(List<string> samples)
    {
        _processingSamples = samples.ToList();
        _stageBase = 0;
        _stageSpan = 100;
        _stageDescription = "";
        _currentSampleIndex = 0;
        _progressParser = null;

        Steps.Clear();
        foreach (var name in samples)
        {
            if (!SkipReconstruct)
                Steps.Add(new PipelineStep(name, "3D 重建"));
            if (!SkipBallistic)
                Steps.Add(new PipelineStep(name, "弹道拟合"));
            Steps.Add(new PipelineStep(name, "Unity 导出"));
        }
    }

    /// <summary>
    /// 一行引擎日志 → 进度条 + 步骤列表。
    /// 认日志的活儿交给 <see cref="PipelineProgressParser"/>（Core 里有单测），
    /// 这里只负责把它给出的阶段/样本/阶段内位置换算成界面数值。
    /// </summary>
    private void HandleLogLine(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        // 解析器只认正常流程，异常行自己判
        if (message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("Traceback", StringComparison.Ordinal))
        {
            FinishRunningStep(StepStatus.Failed);
        }

        var progress = ProgressParser.Consume(message);
        if (progress is null)
            return;

        if (progress.Stage == PipelineStage.Done)
        {
            ProcessingProgress = 100;
            ProcessingProgressText = "已完成";
            return;
        }

        var title = StageTitle(progress.Stage);
        if (title is null)
            return;

        if (title != _stageDescription)
            EnterStage(progress.Stage, title);

        if (progress.SampleName is { } sample)
        {
            var index = _processingSamples.IndexOf(sample);
            if (index >= 0)
            {
                _currentSampleIndex = index;
                MarkSampleStep(index);
            }
        }

        var count = Math.Max(1, _processingSamples.Count);
        var sampleIndex = Math.Clamp(_currentSampleIndex, 0, count - 1);
        // 完成事件算整格，进行中则用样本内百分比（重建阶段才有）细粒度内插
        var position = progress.State == StageState.Completed
            ? sampleIndex + 1.0
            : sampleIndex + (progress.SampleFraction ?? 0);

        ProcessingProgress = Math.Clamp(
            _stageBase + (int)Math.Round(position / count * _stageSpan), 0, 100);
        ProcessingProgressText = progress.Message;
    }

    /// <summary>阶段名 → 进度基线 / 跨度 / 提示语，集中一处，避免散落的魔数。</summary>
    private static (int Base, int Span, string Text) StageLayout(PipelineStage stage) => stage switch
    {
        PipelineStage.Reconstruct => (0, 60, "三维重建中..."),
        PipelineStage.Ballistic => (60, 25, "弹道拟合中..."),
        PipelineStage.Export => (85, 15, "Unity 导出中..."),
        _ => (0, 100, "处理中..."),
    };

    /// <summary>阶段名与步骤列表里的 Description 保持一致，MarkSampleStep 靠它配对。</summary>
    private static string? StageTitle(PipelineStage stage) => stage switch
    {
        PipelineStage.Reconstruct => "3D 重建",
        PipelineStage.Ballistic => "弹道拟合",
        PipelineStage.Export => "Unity 导出",
        _ => null,
    };

    private void EndStage()
    {
        if (string.IsNullOrEmpty(_stageDescription)) return;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Description == _stageDescription && Steps[i].Status == StepStatus.Running)
                Steps[i] = Steps[i] with { Status = StepStatus.Completed };
        }
    }

    private void MarkSampleStep(int sampleIndex)
    {
        if (sampleIndex < 0 || sampleIndex >= _processingSamples.Count) return;
        string name = _processingSamples[sampleIndex];

        // 同阶段其它样本的 Running 步骤 → Completed
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Description == _stageDescription
                && Steps[i].Status == StepStatus.Running
                && Steps[i].Name != name)
            {
                Steps[i] = Steps[i] with { Status = StepStatus.Completed };
            }
        }

        // 当前样本步骤 → Running
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Description == _stageDescription
                && Steps[i].Name == name
                && Steps[i].Status == StepStatus.Pending)
            {
                Steps[i] = Steps[i] with { Status = StepStatus.Running };
                break;
            }
        }
    }

    private void EnterStage(PipelineStage stage, string title)
    {
        EndStage();

        var layout = StageLayout(stage);
        _stageDescription = title;
        _stageBase = layout.Base;
        _stageSpan = layout.Span;
        _currentSampleIndex = 0;
        ProcessingProgress = layout.Base;
        ProcessingProgressText = layout.Text;

        // 该阶段第一个样本置为进行中，其余保持等待
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Description == title && Steps[index].Status == StepStatus.Pending)
            {
                Steps[index] = Steps[index] with { Status = StepStatus.Running };
                break;
            }
        }

        _mainVm.SetPipelineState(stage switch
        {
            PipelineStage.Reconstruct => 2,
            PipelineStage.Ballistic => 3,
            PipelineStage.Export => 4,
            _ => 0,
        }, layout.Text);
    }

    private void FinishRunningStep(StepStatus status)
    {
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Status != StepStatus.Running)
                continue;
            Steps[index] = Steps[index] with { Status = status };
            return;
        }
    }

    private void CompleteRemainingSteps(bool success)
    {
        for (var index = 0; index < Steps.Count; index++)
        {
            PipelineStep step = Steps[index];
            if (step.Status == StepStatus.Completed || step.Status == StepStatus.Failed)
                continue;

            Steps[index] = step with
            {
                Status = success ? StepStatus.Completed
                    : step.Status == StepStatus.Running ? StepStatus.Failed : StepStatus.Skipped
            };
        }
    }

    /// <summary>同步 MainViewModel.Samples 到 SampleItems（可选的复选框列表）</summary>
    public void SyncSampleItems()
    {
        var existingNames = new HashSet<string>(SampleItems.Select(s => s.Name));
        foreach (var sample in Samples)
        {
            if (!existingNames.Contains(sample.Name))
                SampleItems.Add(new SampleItem(sample));
        }
        // 移除已不存在的样本
        var currentNames = new HashSet<string>(Samples.Select(s => s.Name));
        for (int i = SampleItems.Count - 1; i >= 0; i--)
        {
            if (!currentNames.Contains(SampleItems[i].Name))
                SampleItems.RemoveAt(i);
        }
    }
}
