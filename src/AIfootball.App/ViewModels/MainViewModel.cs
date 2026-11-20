using System.Collections.ObjectModel;
using System.Windows;
using AIfootball.App.Models;
using AIfootball.App.Services;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.ViewModels;

/// <summary>主窗口 ViewModel — 仪表盘</summary>
public class MainViewModel : ViewModelBase, IDisposable
{
    private readonly IPipelineService _pipelineService;
    private readonly IPythonEngine _pythonEngine;
    private readonly IEnvironmentService _environmentService;
    private readonly IGpuDetectionService _gpuService;
    private readonly object _sampleWatcherLock = new();
    private FileSystemWatcher? _sampleWatcher;
    private CancellationTokenSource? _sampleRefreshCancellation;

    public MainViewModel(
        IPipelineService pipelineService,
        IPythonEngine pythonEngine,
        IEnvironmentService environmentService,
        IGpuDetectionService gpuService)
    {
        _pipelineService = pipelineService;
        _pythonEngine = pythonEngine;
        _environmentService = environmentService;
        _gpuService = gpuService;

        SetupEnvironmentCommand = new RelayCommand(async () => await OnSetupEnvironment());
    }

    // ─── 点球手画像（由分析页触发生成） ───
    private ShooterProfileSnapshot? _shooterProfile;
    public ShooterProfileSnapshot? ShooterProfile
    {
        get => _shooterProfile;
        set => SetProperty(ref _shooterProfile, value);
    }

    // ─── 环境状态 ───
    private bool _isEnvironmentReady;
    public bool IsEnvironmentReady
    {
        get => _isEnvironmentReady;
        set => SetProperty(ref _isEnvironmentReady, value);
    }

    private bool _isCheckingEnvironment = true;
    public bool IsCheckingEnvironment
    {
        get => _isCheckingEnvironment;
        private set => SetProperty(ref _isCheckingEnvironment, value);
    }

    private string _envStatusText = "正在检测计算环境…";
    public string EnvStatusText
    {
        get => _envStatusText;
        set => SetProperty(ref _envStatusText, value);
    }

    private int _setupProgress;
    public int SetupProgress
    {
        get => _setupProgress;
        set => SetProperty(ref _setupProgress, value);
    }

    private bool _isSettingUp;
    public bool IsSettingUp
    {
        get => _isSettingUp;
        set => SetProperty(ref _isSettingUp, value);
    }

    private bool _setupProgressVisible;
    public bool SetupProgressVisible
    {
        get => _setupProgressVisible;
        set => SetProperty(ref _setupProgressVisible, value);
    }

    // ─── 系统状态 ───
    private string _statusBarText = "";
    public string StatusBarText
    {
        get => _statusBarText;
        set => SetProperty(ref _statusBarText, value);
    }

    private GpuInfo? _gpuInfo;
    public GpuInfo? GpuInfo
    {
        get => _gpuInfo;
        set => SetProperty(ref _gpuInfo, value);
    }

    private CalibrationStatus? _calibrationStatus;
    public CalibrationStatus? CalibrationStatus
    {
        get => _calibrationStatus;
        set => SetProperty(ref _calibrationStatus, value);
    }

    // ─── 样本 ───
    public ObservableCollection<SampleInfo> Samples { get; } = new();

    private int _sampleCount;
    public int SampleCount
    {
        get => _sampleCount;
        set => SetProperty(ref _sampleCount, value);
    }

    // ─── 仪表盘分析链路（由真实流水线状态驱动） ───
    private int _pipelineStage;
    public int PipelineStage
    {
        get => _pipelineStage;
        private set => SetProperty(ref _pipelineStage, value);
    }

    private string _pipelineStatusText = "待执行";
    public string PipelineStatusText
    {
        get => _pipelineStatusText;
        private set => SetProperty(ref _pipelineStatusText, value);
    }

    public void SetPipelineState(int stage, string statusText)
    {
        PipelineStage = stage;
        PipelineStatusText = statusText;
    }

    // ─── 门将 ───
    public ObservableCollection<GoalkeeperInfo> Goalkeepers { get; } = new();

    // ─── 标定进度（仅在标定页显示） ───
    private bool _isCalibrationRunning;
    public bool IsCalibrationRunning
    {
        get => _isCalibrationRunning;
        set => SetProperty(ref _isCalibrationRunning, value);
    }

    private int _calibrationProgress;
    public int CalibrationProgress
    {
        get => _calibrationProgress;
        set => SetProperty(ref _calibrationProgress, value);
    }

    private string _calibrationProgressText = "待执行";
    public string CalibrationProgressText
    {
        get => _calibrationProgressText;
        set => SetProperty(ref _calibrationProgressText, value);
    }

    // ─── 标定表单（Web 界面是受控组件，值存在这里，切页再回来不会丢） ───
    private string _calibLeftChessVideo = "";
    public string CalibLeftChessVideo
    {
        get => _calibLeftChessVideo;
        set => SetProperty(ref _calibLeftChessVideo, value);
    }

    private string _calibRightChessVideo = "";
    public string CalibRightChessVideo
    {
        get => _calibRightChessVideo;
        set => SetProperty(ref _calibRightChessVideo, value);
    }

    private string _calibLeftFieldImage = "";
    public string CalibLeftFieldImage
    {
        get => _calibLeftFieldImage;
        set => SetProperty(ref _calibLeftFieldImage, value);
    }

    private string _calibRightFieldImage = "";
    public string CalibRightFieldImage
    {
        get => _calibRightFieldImage;
        set => SetProperty(ref _calibRightFieldImage, value);
    }

    private string _calibIntrinsicsStatus = "";
    public string CalibIntrinsicsStatus
    {
        get => _calibIntrinsicsStatus;
        set => SetProperty(ref _calibIntrinsicsStatus, value);
    }

    private string _calibExtrinsicsStatus = "";
    public string CalibExtrinsicsStatus
    {
        get => _calibExtrinsicsStatus;
        set => SetProperty(ref _calibExtrinsicsStatus, value);
    }

    /// <summary>已经确认过一次内参/外参都就绪时，标定页先折叠成"已完成"入口。</summary>
    private bool _calibShowWorkflow;
    public bool CalibShowWorkflow
    {
        get => _calibShowWorkflow;
        set => SetProperty(ref _calibShowWorkflow, value);
    }

    // ─── 命令 ───
    public RelayCommand SetupEnvironmentCommand { get; }

    // ─── 方法 ───
    public async Task InitializeAsync()
    {
        AddLog("info", "═══════ 系统自检开始 ═══════");
        AddLog("info", $"工作目录: {_pythonEngine.WorkspaceDir}");

        // 1. 引擎自检：统一走 `aifootball selfcheck`，界面与命令行看到同一份结论
        try
        {
            var check = await _pythonEngine.RunSelfCheckAsync(force: true);
            IsEnvironmentReady = check?.HasCoreDependencies == true;

            if (check is not null)
            {
                AddLog("info",
                    $"🐍 Python {check.Python ?? "?"} · torch {check.Torch ?? "缺失"} · " +
                    $"opencv {check.OpenCv ?? "缺失"} · ultralytics {check.Ultralytics ?? "缺失"}");
                AddLog(check.Cuda ? "info" : "warn",
                    check.Cuda ? "⚡ CUDA: 可用" : "⚡ CUDA: 不可用（CPU 推理）");
                if (!check.ModelPresent)
                    AddLog("warn", $"⚠ 检测模型缺失: {check.Model ?? "models/yolo11m.pt"}");
            }

            if (IsEnvironmentReady)
            {
                await SetReadyStateAsync(check!.Cuda);
            }
            else
            {
                EnvStatusText = check is not null
                    ? $"Python 依赖不齐全（{check.MissingSummary()}）"
                    : File.Exists(_pythonEngine.PythonExePath)
                        ? "Python 自检未通过，可重新安装推理环境"
                        : "请安装 Python 推理环境";
                AddLog("warn", $"{EnvStatusText}。解释器路径：{_pythonEngine.PythonExePath}");
                StatusBarText = "环境未就绪 — 点击「一键安装环境」";
                SetupProgressVisible = false;
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"✗ 环境检测失败: {ex.Message}");
            EnvStatusText = $"异常：{ex.Message}";
        }
        finally
        {
            IsCheckingEnvironment = false;
        }

        // 2. 无论环境是否就绪，检测 GPU
        try
        {
            GpuInfo = await _gpuService.DetectAsync();
            var gpuType = GpuInfo.CudaAvailable ? $"CUDA: {GpuInfo.GpuName}" :
                          GpuInfo.HasDedicatedGpu ? $"独显: {GpuInfo.AdapterNames}" :
                          "CPU 模式";
            AddLog("info", $"🖥 GPU: {gpuType}");
        }
        catch (Exception ex)
        {
            AddLog("warn", $"⚠ GPU 检测失败: {ex.Message}");
        }

        // 3. 检查标定文件
        try
        {
            CalibrationStatus = _pipelineService.GetCalibrationStatus();
            if (CalibrationStatus.FullyReady)
                AddLog("info", "📷 相机标定: ✓ 就绪 (内参+外参)");
            else
            {
                AddLog("warn", "⚠ 相机标定: 未完成");
                if (!CalibrationStatus.IntrinsicsReady)
                    AddLog("warn", "   → 缺少内参 (calib/intrinsics_*.npz)");
                if (!CalibrationStatus.ExtrinsicsReady)
                    AddLog("warn", "   → 缺少外参 (calib/*_pose.npz)");
            }
        }
        catch (Exception ex)
        {
            AddLog("warn", $"⚠ 标定检测失败: {ex.Message}");
        }

        // 4. 扫描数据
        try
        {
            await RefreshListsAsync();
            StartSampleWatcher();
            AddLog("info", $"📦 样本: {SampleCount} 个, 门将: {Goalkeepers.Count} 个");
        }
        catch (Exception ex)
        {
            AddLog("warn", $"⚠ 数据扫描失败: {ex.Message}");
        }

        // 5. 同步 PipelineViewModel 的样本列表
        try
        {
            var pipelineVm = App.Services.GetService(typeof(PipelineViewModel)) as PipelineViewModel;
            pipelineVm?.SyncSampleItems();
        }
        catch (Exception ex)
        {
            AddLog("warn", $"样本列表同步失败: {ex.Message}");
        }

        AddLog("info", "═══════ 自检完成 ═══════");
    }

    private async Task SetReadyStateAsync(bool cuda)
    {
        IsEnvironmentReady = true;
        EnvStatusText = "Python 环境就绪";
        SetupProgressVisible = false;

        // 检测 GPU
        GpuInfo = await _gpuService.DetectAsync();
        CalibrationStatus = _pipelineService.GetCalibrationStatus();

        if (cuda)
            StatusBarText = $"NVIDIA CUDA 推理: {GpuInfo?.GpuName}";
        else if (GpuInfo is { HasDedicatedGpu: true })
            StatusBarText = $"独立显卡 / CPU 兼容推理: {GpuInfo.AdapterNames}";
        else
            StatusBarText = $"CPU 推理模式: {GpuInfo?.AdapterNames}";

        AddLog("info", $"GPU: {StatusBarText}");
        AddLog("info", $"标定: {(CalibrationStatus?.FullyReady == true ? "✓ 就绪" : "⚠ 待标定")}");

        await RefreshListsAsync();
    }

    public async Task RefreshListsAsync()
    {
        var samples = await Task.Run(() => _pipelineService.ScanSamples());
        var goalkeepers = await Task.Run(() => _pipelineService.ScanGoalkeepers());
        var calib = await Task.Run(() => _pipelineService.GetCalibrationStatus());

        Samples.Clear();
        foreach (var s in samples)
            Samples.Add(s);
        SampleCount = Samples.Count;

        Goalkeepers.Clear();
        foreach (var gk in goalkeepers)
            Goalkeepers.Add(gk);

        CalibrationStatus = calib;
        SyncPipelineSampleItems();
    }

    /// <summary>视频内参标定：左右各用一段棋盘格视频</summary>
    private void StartSampleWatcher()
    {
        if (_sampleWatcher is not null) return;

        var samplesDirectory = Path.Combine(_pythonEngine.WorkspaceDir, "samples");
        if (!Directory.Exists(samplesDirectory)) return;

        _sampleWatcher = new FileSystemWatcher(samplesDirectory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _sampleWatcher.Changed += OnSampleDirectoryChanged;
        _sampleWatcher.Created += OnSampleDirectoryChanged;
        _sampleWatcher.Deleted += OnSampleDirectoryChanged;
        _sampleWatcher.Renamed += OnSampleDirectoryRenamed;
    }

    private void OnSampleDirectoryChanged(object sender, FileSystemEventArgs e) => QueueSampleRefresh();

    private void OnSampleDirectoryRenamed(object sender, RenamedEventArgs e) => QueueSampleRefresh();

    private void QueueSampleRefresh()
    {
        CancellationToken token;
        lock (_sampleWatcherLock)
        {
            _sampleRefreshCancellation?.Cancel();
            _sampleRefreshCancellation?.Dispose();
            _sampleRefreshCancellation = new CancellationTokenSource();
            token = _sampleRefreshCancellation.Token;
        }

        _ = RefreshSamplesAfterFileChangeAsync(token);
    }

    private async Task RefreshSamplesAfterFileChangeAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600), token);
            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                if (token.IsCancellationRequested) return;
                await RefreshListsAsync();
            }).Task.Unwrap();
        }
        catch (OperationCanceledException)
        {
            // A later file event has superseded this refresh.
        }
        catch (Exception ex)
        {
            AddLog("warn", $"样本目录自动同步失败: {ex.Message}");
        }
    }

    private void SyncPipelineSampleItems()
    {
        var pipelineVm = App.Services.GetService(typeof(PipelineViewModel)) as PipelineViewModel;
        pipelineVm?.SyncSampleItems();
    }

    public async Task<bool> CalibrateIntrinsicsAsync(string leftVideo, string rightVideo)
    {
        if (!IsEnvironmentReady)
        {
            AddLog("warn", "Python 环境未就绪，请先在仪表盘点击「一键安装环境」");
            return false;
        }

        var calibDir = Path.Combine(_pythonEngine.WorkspaceDir, "calib");
        AddLog("info", "======== 开始视频内参标定 ========");
        BeginCalibration("正在准备内参标定");
        try
        {
            var progress = new Progress<(string Message, string Level)>(p => ReportCalibration(p.Message, p.Level));
            var ok = await _pipelineService.CalibrateIntrinsicsAsync(leftVideo, rightVideo, calibDir, progress);
            CalibrationProgress = ok ? 100 : CalibrationProgress;
            CalibrationProgressText = ok ? "内参标定完成" : "内参标定失败，详情见日志";
            AddLog(ok ? "success" : "error", ok ? "内参标定完成" : "内参标定失败");
            await RefreshListsAsync();
            return ok;
        }
        finally { IsCalibrationRunning = false; }
    }

    /// <summary>外参标定：左右各一张足球场照片（交互点击参考点）</summary>
    public async Task<bool> CalibrateExtrinsicsAsync(string leftImage, string rightImage)
    {
        if (!IsEnvironmentReady)
        {
            AddLog("warn", "Python 环境未就绪，请先在仪表盘点击「一键安装环境」");
            return false;
        }

        var calibDir = Path.Combine(_pythonEngine.WorkspaceDir, "calib");
        AddLog("info", "======== 开始外参标定（请在弹出的图像窗口点击参考点） ========");
        BeginCalibration("正在准备外参标定");
        try
        {
            var progress = new Progress<(string Message, string Level)>(p => ReportCalibration(p.Message, p.Level));
            var ok = await _pipelineService.CalibrateExtrinsicsAsync(leftImage, rightImage, calibDir, progress);
            CalibrationProgress = ok ? 100 : CalibrationProgress;
            CalibrationProgressText = ok ? "外参标定完成" : "外参标定失败，详情见日志";
            AddLog(ok ? "success" : "error", ok ? "外参标定完成" : "外参标定失败");
            await RefreshListsAsync();
            return ok;
        }
        finally { IsCalibrationRunning = false; }
    }

    private async Task OnSetupEnvironment()
    {
        if (IsCheckingEnvironment || IsSettingUp || IsEnvironmentReady) return;

        IsSettingUp = true;
        SetupProgressVisible = true;
        SetupProgress = 0;
        EnvStatusText = "正在准备 Python 推理环境…";

        var progress = new Progress<(string Status, int Percent)>(p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                if (p.Percent != -2)
                    EnvStatusText = p.Status;
                // -2 = pip 实时输出，不更新进度条
                if (p.Percent >= 0)
                    SetupProgress = p.Percent;
                // -2 → "output" 级别, -1 → "error", 其他 → "info"
                var level = p.Percent == -2 ? "output" :
                            p.Percent == -1 ? "error" : "info";
                AddLog(level, p.Status);
            });
        });

        try
        {
            if (!await _environmentService.SetupAsync(progress))
                return;

            EnvStatusText = "正在检查已安装的推理环境…";
            var check = await _pythonEngine.RunSelfCheckAsync(force: true);
            if (check is not { HasCoreDependencies: true })
                throw new InvalidOperationException($"安装后的 Python 引擎自检失败。{check?.MissingSummary()}");

            await SetReadyStateAsync(check.Cuda);
        }
        catch (Exception ex)
        {
            AddLog("error", $"❌ 安装异常: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                AddLog("error", $"   内部错误: {ex.InnerException.Message}");
            EnvStatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            SetupProgressVisible = false;
            IsSettingUp = false;
        }
    }

    public void AddLog(string level, string message)
    {
        AppLogger.Write(level, message);
    }

    private void BeginCalibration(string status)
    {
        IsCalibrationRunning = true;
        CalibrationProgress = 0;
        CalibrationProgressText = status;
    }

    private void ReportCalibration(string message, string level)
    {
        AddLog(level, message);
        CalibrationProgressText = message.Trim();
        var match = System.Text.RegularExpressions.Regex.Match(message, @"(\d{1,3})%");
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
            CalibrationProgress = Math.Clamp(percent, 0, 99);
    }

    public void Dispose()
    {
        _sampleWatcher?.Dispose();
        _sampleRefreshCancellation?.Cancel();
        _sampleRefreshCancellation?.Dispose();
    }
}
