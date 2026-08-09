using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.RegularExpressions;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

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

    private string _urlLeft = "http://192.168.1.101:8080/video";
    public string UrlLeft
    {
        get => _urlLeft;
        set => SetProperty(ref _urlLeft, value);
    }

    private string _urlRight = "http://192.168.1.102:8080/video";
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

    // ─── 步骤进度 ───
    public ObservableCollection<PipelineStep> Steps { get; } = new();

    private int _processingProgress;
    public int ProcessingProgress
    {
        get => _processingProgress;
        set => SetProperty(ref _processingProgress, value);
    }

    private string _processingProgressText = "等待开始";
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
            ((RelayCommand)RunOfflineCommand).CanExecute(null);
            ((RelayCommand)RunOnlineCommand).CanExecute(null);
            ((RelayCommand)CancelCommand).CanExecute(null);
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

    private CancellationTokenSource? _cts;

    private async Task RunOfflineAsync()
    {
        if (!_mainVm.IsEnvironmentReady)
        {
            _mainVm.AddLog("warn", "Python 环境未就绪，请先在仪表盘点击「一键安装环境」");
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
            _mainVm.AddLog("warn", "没有可处理的样本");
            return;
        }

        IsRunning = true;
        _cts = new CancellationTokenSource();
        InitSteps(targetSamples);
        ProcessingProgress = 0;
        ProcessingProgressText = "等待 Python 任务启动";

        _mainVm.AddLog("info", $"======== 离线处理 {targetSamples.Count} 个样本 ========");

        var progress = new Progress<(string Message, string Level)>(p =>
        {
            _mainVm.AddLog(p.Level, p.Message);
            UpdateProcessingProgress(p.Message);
            UpdateStepStatusFromLog(p.Message);
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
                ProcessingProgressText = "全部完成";
            }
            _mainVm.AddLog(success ? "success" : "error",
                success ? "======== 全部完成 ========" : "======== 处理失败 ========");
            _mainVm.RefreshLists();
        }
        catch (Exception ex)
        {
            CompleteRemainingSteps(false);
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

        _mainVm.AddLog("info", $"======== 在线录制: {SampleName} ========");

        var progress = new Progress<(string Message, string Level)>(p =>
        {
            _mainVm.AddLog(p.Level, p.Message);
        });

        var success = IsLiveAnalysis
            ? await _pipelineService.RunOnlineLiveAsync(
                camLeft, camRight, SampleName, progress, _cts.Token)
            : await _pipelineService.RunOnlineAsync(
                camLeft, camRight, SampleName, progress, _cts.Token);

        IsRunning = false;
        _mainVm.AddLog(success ? "success" : "error",
            success ? "======== 录制+处理完成 ========" : "======== 失败 ========");

        _mainVm.RefreshLists();
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
        var url = IsNetworkMode ? UrlLeft?.Trim() ?? "" : "";
        if (string.IsNullOrEmpty(url))
        {
            NetworkTestText = "请先填写要测试的网络流地址";
            return;
        }

        IsTestingStream = true;
        NetworkTestText = "正在连接测试...";
        try
        {
            var (ok, message) = await _pipelineService.TestNetworkStreamAsync(url);
            NetworkTestText = ok ? $"✅ {message}" : $"❌ {message}";
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
        _mainVm.AddLog("warn", "操作已取消");
    }

    private void InitSteps(List<string> samples)
    {
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

    private void UpdateStepStatusFromLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (message.Contains("[1/3]", StringComparison.Ordinal))
        {
            StartNextStep("3D 重建");
            return;
        }

        if (message.Contains("[2/3]", StringComparison.Ordinal))
        {
            FinishRunningStep(StepStatus.Completed);
            StartNextStep("弹道拟合");
            return;
        }

        if (message.Contains("[3/3]", StringComparison.Ordinal))
        {
            FinishRunningStep(StepStatus.Completed);
            StartNextStep("Unity 导出");
            return;
        }

        if (message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("Traceback", StringComparison.Ordinal))
        {
            FinishRunningStep(StepStatus.Failed);
            return;
        }

        if (message.Contains("完成", StringComparison.Ordinal))
            FinishRunningStep(StepStatus.Completed);
    }

    private void UpdateProcessingProgress(string message)
    {
        // 阶段切换：把各阶段子进度映射到总进度区间（3D重建 0-60%，弹道拟合 60-85%，导出 85-100%）
        if (message.Contains("[1/3]", StringComparison.Ordinal))
        {
            ProcessingProgress = 0;
            ProcessingProgressText = "三维重建中...";
            return;
        }

        if (message.Contains("[2/3]", StringComparison.Ordinal))
        {
            ProcessingProgress = 60;
            ProcessingProgressText = "弹道拟合中...";
            return;
        }

        if (message.Contains("[3/3]", StringComparison.Ordinal))
        {
            ProcessingProgress = 85;
            ProcessingProgressText = "Unity 导出中...";
            return;
        }

        if (message.Contains("全部完成", StringComparison.Ordinal))
        {
            ProcessingProgress = 100;
            ProcessingProgressText = "全部完成";
            return;
        }

        Match match = Regex.Match(message, @"\((\d{1,3})%\)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out int percent))
            return;

        // 检测阶段（密集跟踪/起脚定位）的百分比映射到 3D 重建区间 0-60，其余阶段直接显示
        if (message.Contains("密集跟踪", StringComparison.Ordinal)
            || message.Contains("起脚定位", StringComparison.Ordinal))
        {
            ProcessingProgress = Math.Clamp((int)Math.Round(percent * 0.60), 0, 60);
        }
        else
        {
            ProcessingProgress = Math.Clamp(percent, 0, 100);
        }
        ProcessingProgressText = message.Trim();
    }

    private void StartNextStep(string description)
    {
        for (var index = 0; index < Steps.Count; index++)
        {
            if (Steps[index].Description != description || Steps[index].Status != StepStatus.Pending)
                continue;
            Steps[index] = Steps[index] with { Status = StepStatus.Running };
            return;
        }
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
