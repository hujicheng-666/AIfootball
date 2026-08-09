using System.Collections.ObjectModel;
using System.Windows;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.ViewModels;

/// <summary>主窗口 ViewModel — 仪表盘</summary>
public class MainViewModel : ViewModelBase
{
    private readonly IPipelineService _pipelineService;
    private readonly IPythonEngine _pythonEngine;
    private readonly IEnvironmentService _environmentService;
    private readonly IGpuDetectionService _gpuService;

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

        RefreshCommand = new RelayCommand(OnRefresh);
        SetupEnvironmentCommand = new RelayCommand(async () => await OnSetupEnvironment());
    }

    // ─── 环境状态 ───
    private bool _isEnvironmentReady;
    public bool IsEnvironmentReady
    {
        get => _isEnvironmentReady;
        set => SetProperty(ref _isEnvironmentReady, value);
    }

    private string _envStatusText = "正在检查环境...";
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

    // ─── 门将 ───
    public ObservableCollection<GoalkeeperInfo> Goalkeepers { get; } = new();

    // ─── 日志 ───
    public ObservableCollection<LogEntry> LogEntries { get; } = new();

    // ─── 命令 ───
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SetupEnvironmentCommand { get; }

    // ─── 方法 ───
    public async Task InitializeAsync()
    {
        AddLog("info", "═══════ 系统自检开始 ═══════");
        AddLog("info", $"工作目录: {_pythonEngine.WorkspaceDir}");

        // 1. 检查 Python 环境
        try
        {
            IsEnvironmentReady = await _pythonEngine.IsEnvironmentReadyAsync();
            if (IsEnvironmentReady)
            {
                await SetReadyStateAsync();
            }
            else
            {
                AddLog("warn", "⚠ Python 推理环境: 未就绪");
                AddLog("info", "   → 请点击「一键安装环境」自动配置");
                EnvStatusText = "首次运行：需要安装 Python 推理环境";
                StatusBarText = "环境未就绪 — 点击「一键安装环境」";
                SetupProgressVisible = false;
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"✗ 环境检测失败: {ex.Message}");
            EnvStatusText = $"错误: {ex.Message}";
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
            RefreshLists();
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
        catch { }

        AddLog("info", "═══════ 自检完成 ═══════");
    }

    private async Task SetReadyStateAsync()
    {
        IsEnvironmentReady = true;
        EnvStatusText = "Python 环境就绪 ✓";
        SetupProgressVisible = false;
        IsSettingUp = false;

        // 检测 GPU
        GpuInfo = await _gpuService.DetectAsync();
        CalibrationStatus = _pipelineService.GetCalibrationStatus();

        if (GpuInfo is { CudaAvailable: true })
            StatusBarText = $"NVIDIA CUDA 推理: {GpuInfo.GpuName}";
        else if (GpuInfo is { HasDedicatedGpu: true })
            StatusBarText = $"独立显卡 / CPU 兼容推理: {GpuInfo.AdapterNames}";
        else
            StatusBarText = $"CPU 推理模式: {GpuInfo?.AdapterNames}";

        AddLog("info", $"GPU: {StatusBarText}");
        AddLog("info", $"标定: {(CalibrationStatus?.FullyReady == true ? "✓ 就绪" : "⚠ 待标定")}");

        RefreshLists();
    }

    public void RefreshLists()
    {
        Samples.Clear();
        foreach (var s in _pipelineService.ScanSamples())
            Samples.Add(s);
        SampleCount = Samples.Count;

        Goalkeepers.Clear();
        foreach (var gk in _pipelineService.ScanGoalkeepers())
            Goalkeepers.Add(gk);

        CalibrationStatus = _pipelineService.GetCalibrationStatus();
    }

    /// <summary>视频内参标定：左右各用一段棋盘格视频</summary>
    public async Task<bool> CalibrateIntrinsicsAsync(string leftVideo, string rightVideo)
    {
        if (!IsEnvironmentReady)
        {
            AddLog("warn", "Python 环境未就绪，请先在仪表盘点击「一键安装环境」");
            return false;
        }

        var calibDir = Path.Combine(_pythonEngine.WorkspaceDir, "calib");
        AddLog("info", "======== 开始视频内参标定 ========");
        var progress = new Progress<(string Message, string Level)>(p => AddLog(p.Level, p.Message));
        var ok = await _pipelineService.CalibrateIntrinsicsAsync(leftVideo, rightVideo, calibDir, progress);
        AddLog(ok ? "success" : "error", ok ? "✅ 内参标定完成" : "❌ 内参标定失败");
        RefreshLists();
        return ok;
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
        var progress = new Progress<(string Message, string Level)>(p => AddLog(p.Level, p.Message));
        var ok = await _pipelineService.CalibrateExtrinsicsAsync(leftImage, rightImage, calibDir, progress);
        AddLog(ok ? "success" : "error", ok ? "✅ 外参标定完成" : "❌ 外参标定失败");
        RefreshLists();
        return ok;
    }

    private void OnRefresh()
    {
        RefreshLists();
        AddLog("info", "列表已刷新");
    }

    private async Task OnSetupEnvironment()
    {
        if (IsSettingUp) return;

        IsSettingUp = true;
        SetupProgressVisible = true;
        SetupProgress = 0;

        var progress = new Progress<(string Status, int Percent)>(p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
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
            var success = await _environmentService.SetupAsync(progress);
            if (success)
                await SetReadyStateAsync();
            else
            {
                EnvStatusText = "安装失败，查看下方日志了解详情";
                SetupProgressVisible = false;
                IsSettingUp = false;
            }
        }
        catch (Exception ex)
        {
            AddLog("error", $"❌ 安装异常: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                AddLog("error", $"   内部错误: {ex.InnerException.Message}");
            EnvStatusText = $"安装失败: {ex.Message}";
            SetupProgressVisible = false;
            IsSettingUp = false;
        }
    }

    public void AddLog(string level, string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LogEntries.Add(new LogEntry(DateTime.Now, level, message));
            if (LogEntries.Count > 1000)
                LogEntries.RemoveAt(0);
        });
    }
}
