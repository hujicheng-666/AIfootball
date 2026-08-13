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

    // ─── 批次处理进度状态机（一次跑完所有样本的同一阶段） ───
    private List<string> _processingSamples = new();
    private int _stageBase;
    private int _stageSpan = 100;
    private string _stageDescription = "";
    private int _currentSampleIndex;

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
            await _mainVm.RefreshListsAsync();
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
        InitSteps(new List<string> { SampleName });
        ProcessingProgress = 0;
        ProcessingProgressText = "等待采集任务启动";

        _mainVm.AddLog("info", $"======== 在线录制: {SampleName} ========");

        var progress = new Progress<(string Message, string Level)>(p =>
        {
            _mainVm.AddLog(p.Level, p.Message);
            UpdateProcessingProgress(p.Message);
            UpdateStepStatusFromLog(p.Message);
        });
        try
        {
            var success = IsLiveAnalysis
                ? await _pipelineService.RunOnlineLiveAsync(
                    camLeft, camRight, SampleName, progress, _cts.Token)
                : await _pipelineService.RunOnlineAsync(
                    camLeft, camRight, SampleName, progress, _cts.Token);

            CompleteRemainingSteps(success);
            if (success)
            {
                ProcessingProgress = 100;
                ProcessingProgressText = "录制与处理完成";
            }
            _mainVm.AddLog(success ? "success" : "error",
                success ? "录制与处理完成" : "录制或处理失败");
            await _mainVm.RefreshListsAsync();
        }
        catch (Exception ex)
        {
            CompleteRemainingSteps(false);
            ProcessingProgressText = "任务异常，详情已写入日志文件";
            _mainVm.AddLog("error", "在线任务异常: " + ex.Message);
        }
        finally
        {
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
        _processingSamples = samples.ToList();
        _stageBase = 0;
        _stageSpan = 100;
        _stageDescription = "";
        _currentSampleIndex = 0;

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
            BeginStage("3D 重建");
            return;
        }

        if (message.Contains("[2/3]", StringComparison.Ordinal))
        {
            EndStage();
            BeginStage("弹道拟合");
            return;
        }

        if (message.Contains("[3/3]", StringComparison.Ordinal))
        {
            EndStage();
            BeginStage("Unity 导出");
            return;
        }

        // 样本名 [sampleX] → 推进该样本在当前阶段的步骤
        var sampleMatch = Regex.Match(message, @"^\[(.+?)\]");
        if (sampleMatch.Success)
        {
            int idx = _processingSamples.IndexOf(sampleMatch.Groups[1].Value);
            if (idx >= 0 && !string.IsNullOrEmpty(_stageDescription))
                MarkSampleStep(idx);
            return;
        }

        if (message.Contains("失败", StringComparison.Ordinal)
            || message.Contains("Traceback", StringComparison.Ordinal))
        {
            FinishRunningStep(StepStatus.Failed);
        }
    }

    private void BeginStage(string description)
    {
        _stageDescription = description;
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].Description == description && Steps[i].Status == StepStatus.Pending)
            {
                Steps[i] = Steps[i] with { Status = StepStatus.Running };
                return;
            }
        }
    }

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

    private void UpdateProcessingProgress(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        if (message.Contains("[1/3]", StringComparison.Ordinal)) { EnterStage("3D 重建", 0, 60, "三维重建中..."); return; }
        if (message.Contains("[2/3]", StringComparison.Ordinal)) { EnterStage("弹道拟合", 60, 25, "弹道拟合中..."); return; }
        if (message.Contains("[3/3]", StringComparison.Ordinal)) { EnterStage("Unity 导出", 85, 15, "Unity 导出中..."); return; }
        if (message.Contains("全部完成", StringComparison.Ordinal)) { ProcessingProgress = 100; ProcessingProgressText = "全部完成"; return; }

        var sampleMatch = Regex.Match(message, @"^\[(.+?)\]");
        if (sampleMatch.Success)
        {
            int idx = _processingSamples.IndexOf(sampleMatch.Groups[1].Value);
            if (idx >= 0)
            {
                _currentSampleIndex = idx;
                // 拟合/导出阶段没有百分比输出，用样本序号推进进度
                int n = Math.Max(1, _processingSamples.Count);
                if (_stageDescription == "弹道拟合" || _stageDescription == "Unity 导出")
                {
                    ProcessingProgress = Math.Clamp(
                        _stageBase + (int)Math.Round((idx + 1.0) / n * _stageSpan), 0, 100);
                }
            }
        }

        var m = Regex.Match(message, @"\((\d{1,3})%\)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out int percent))
            return;

        int count = Math.Max(1, _processingSamples.Count);
        int sampleIdx = Math.Clamp(_currentSampleIndex, 0, count - 1);

        // 重建阶段：起脚定位约占样本内 20%，密集跟踪约占 80%，避免两段各自 0-100% 导致进度回退
        double sampleFraction;
        if (message.Contains("起脚定位", StringComparison.Ordinal))
            sampleFraction = percent / 100.0 * 0.2;
        else if (message.Contains("密集跟踪", StringComparison.Ordinal))
            sampleFraction = 0.2 + percent / 100.0 * 0.8;
        else
            sampleFraction = percent / 100.0;

        ProcessingProgress = Math.Clamp(
            _stageBase + (int)Math.Round((sampleIdx + sampleFraction) / count * _stageSpan), 0, 100);
        ProcessingProgressText = message.Trim();
    }

    private void EnterStage(string description, int basePercent, int span, string text)
    {
        _stageDescription = description;
        _stageBase = basePercent;
        _stageSpan = span;
        _currentSampleIndex = 0;
        ProcessingProgress = basePercent;
        ProcessingProgressText = text;
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
