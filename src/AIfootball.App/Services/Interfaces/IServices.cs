using AIfootball.App.Models;

namespace AIfootball.App.Services.Interfaces;

/// <summary>Python 引擎服务接口</summary>
public interface IPythonEngine
{
    /// <summary>Python 可执行文件路径</summary>
    string PythonExePath { get; }

    /// <summary>工作目录</summary>
    string WorkspaceDir { get; }

    /// <summary>引擎目录（含 aifootball 包）</summary>
    string EngineDir { get; }

    /// <summary>引擎 CLI 入口 __main__.py 路径</summary>
    string EngineEntryPath { get; }

    /// <summary>异步执行 Python 脚本</summary>
    Task<(int ExitCode, string Output, string Error)> RunAsync(
        string script,
        string arguments = "",
        int timeoutMs = 300000,
        CancellationToken cancellation = default,
        Action<string>? outputReceived = null,
        Action<string>? errorReceived = null);

    /// <summary>检查 Python 环境是否已就绪</summary>
    Task<bool> IsEnvironmentReadyAsync();

    /// <summary>获取环境状态</summary>
    Task<EnvironmentStatus> GetEnvironmentStatusAsync();
}

/// <summary>Python 环境安装服务接口</summary>
public interface IEnvironmentService
{
    /// <summary>检查环境是否兼容</summary>
    Task<bool> IsCompatibleAsync();

    /// <summary>执行一键安装</summary>
    Task<bool> SetupAsync(IProgress<(string Status, int Percent)> progress);
}

/// <summary>GPU 检测服务接口</summary>
public interface IGpuDetectionService
{
    Task<GpuInfo> DetectAsync();
}

/// <summary>流水线服务接口</summary>
public interface IPipelineService
{
    /// <summary>扫描样本</summary>
    List<SampleInfo> ScanSamples();

    /// <summary>扫描门将数据</summary>
    List<GoalkeeperInfo> ScanGoalkeepers();

    /// <summary>获取标定状态</summary>
    CalibrationStatus GetCalibrationStatus();

    /// <summary>运行离线流水线</summary>
    Task<bool> RunOfflineAsync(
        List<string> sampleNames,
        bool skipReconstruct = false,
        bool skipBallistic = false,
        string? goalkeeperName = null,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default);

    /// <summary>
    /// 运行在线流水线。相机源为本地 USB 相机索引（如 "0"、"1"）
    /// 或网络视频流 URL（如 rtsp://... / http://.../video）。
    /// </summary>
    Task<bool> RunOnlineAsync(
        string camLeft, string camRight,
        string sampleName,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default);

    /// <summary>边录制边分析（实时检测叠加，停止后立即出结果）</summary>
    Task<bool> RunOnlineLiveAsync(
        string camLeft, string camRight,
        string sampleName,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default);

    /// <summary>检测本地摄像头</summary>
    Task<List<int>> DetectCamerasAsync();

    /// <summary>测试网络视频流是否可连接（手机/RTSP/HTTP 流）</summary>
    Task<(bool Ok, string Message)> TestNetworkStreamAsync(string url, int timeoutSeconds = 8);

    /// <summary>启动 Unity 查看器</summary>
    void LaunchUnityViewer(List<string> sampleNames, string? goalkeeperName = null);

    /// <summary>视频内参标定：左右各用一段棋盘格视频</summary>
    Task<bool> CalibrateIntrinsicsAsync(
        string leftVideo, string rightVideo, string calibDir,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default);

    /// <summary>外参标定：左右各一张足球场照片（交互点击参考点）</summary>
    Task<bool> CalibrateExtrinsicsAsync(
        string leftImage, string rightImage, string calibDir,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default);
}
