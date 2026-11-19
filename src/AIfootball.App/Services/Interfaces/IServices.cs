using System.Diagnostics;
using System.IO;
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
    /// <param name="inputReady">
    /// 需要向子进程 stdin 下发指令时传入：进程启动后回调一次，
    /// 参数是写入端（已开启 AutoFlush），调用方据此把命令按行写进去。
    /// 为 null 时不重定向 stdin，子进程沿用父进程的输入。
    /// </param>
    Task<(int ExitCode, string Output, string Error)> RunAsync(
        string script,
        string arguments = "",
        int timeoutMs = 300000,
        CancellationToken cancellation = default,
        Action<string>? outputReceived = null,
        Action<string>? errorReceived = null,
        Action<StreamWriter>? inputReady = null);

    /// <summary>检查 Python 环境是否已就绪</summary>
    Task<bool> IsEnvironmentReadyAsync();

    /// <summary>
    /// 跑一次引擎自检（`aifootball selfcheck`）。结果缓存一小段时间，
    /// 启动流程里多处调用不会重复导入 torch。失败返回 null。
    /// </summary>
    Task<EngineSelfCheck?> RunSelfCheckAsync(bool force = false);

    /// <summary>
    /// 结束所有仍在运行的 Python 子进程（应用退出时调用）。
    /// Process.Dispose 只释放句柄，不会结束进程；采集进程若残留会一直占着
    /// UDP 端口抢收数据包，下一次预览既绑不上端口、画面也是残缺的。
    /// </summary>
    void TerminateAll();

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

/// <summary>从工作区训练 CSV 生成点球手特点。</summary>
public interface IShooterProfileService
{
    ShooterProfileResult AnalyzeTrainingData();
}

/// <summary>流水线服务接口</summary>
public interface IPipelineService
{
    /// <summary>扫描样本</summary>
    List<SampleInfo> ScanSamples();

    /// <summary>扫描门将数据</summary>
    List<GoalkeeperInfo> ScanGoalkeepers();

    /// <summary>加载门将属性数据（雷达图用），失败返回 null</summary>
    GoalkeeperStats? LoadGoalkeeperStats(string name);

    /// <summary>
    /// 保存门将属性。name 已存在则覆盖，否则新建；ProbabilityMap 原样写回，
    /// Unity 播放器读的是同一份文件，保存后立即生效。
    /// </summary>
    bool SaveGoalkeeperStats(string name, GoalkeeperStats stats, out string message);

    /// <summary>汇总若干样本的球门落点/球速，用于落点热图与散点</summary>
    ShotPlacementSummary LoadShotPlacements(IEnumerable<string> sampleNames);

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
    /// <param name="onControlReady">
    /// 采集进程就绪时回调，参数是下发指令的委托（"start" 开始录制 / "stop" 停止）。
    /// </param>
    Task<bool> RunOnlineAsync(
        string camLeft, string camRight,
        string sampleName,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default,
        Action<Action<string>>? onControlReady = null);

    /// <summary>
    /// 边录制边分析：进程先进入预览，收到 "start" 后才加载检测模型并开始分析，
    /// 收到 "stop" 后停止并立即出结果。
    /// </summary>
    Task<bool> RunOnlineLiveAsync(
        string camLeft, string camRight,
        string sampleName,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default,
        Action<Action<string>>? onControlReady = null);

    /// <summary>检测本地摄像头</summary>
    Task<List<int>> DetectCamerasAsync();

    /// <summary>测试网络视频流是否可连接（手机/RTSP/HTTP 流）</summary>
    Task<(bool Ok, string Message)> TestNetworkStreamAsync(string url, int timeoutSeconds = 8);

    /// <summary>启动 Unity 查看器</summary>
    Process? LaunchUnityViewer(List<string> sampleNames, string? goalkeeperName = null, bool embedded = false,
        nint hostWindowHandle = 0);

    /// <summary>读取某个已交付轨迹所绑定的门将；旧结果或未选择时返回 null。</summary>
    string? GetGoalkeeperForTrajectory(string sampleName);

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
