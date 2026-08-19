using System.Diagnostics;
using System.Text.Json;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>流水线服务 — 封装 Python Pipeline 调用</summary>
public class PipelineService : IPipelineService
{
    private sealed record TrajectoryDeliveryMetadata(string GoalkeeperName);
    private readonly IPythonEngine _engine;

    private List<int>? _cachedCameras;
    private DateTime _camerasCacheTime = DateTime.MinValue;
    private static readonly TimeSpan CamerasCacheTtl = TimeSpan.FromSeconds(30);

    public PipelineService(IPythonEngine engine)
    {
        _engine = engine;
    }

    public List<SampleInfo> ScanSamples()
    {
        var samplesDir = Path.Combine(_engine.WorkspaceDir, "samples");
        if (!Directory.Exists(samplesDir)) return new();

        return Directory.GetDirectories(samplesDir)
            .Select(d =>
            {
                var name = Path.GetFileName(d);
                var leftVideos = GetCameraVideos(d, "left");
                var rightVideos = GetCameraVideos(d, "right");
                var output3d = Path.Combine(_engine.WorkspaceDir, "output", "trajectory_3d", name);
                var outputB = Path.Combine(_engine.WorkspaceDir, "output", "trajectory_ballistic", name);
                var unityCsv = Path.Combine(_engine.WorkspaceDir, "data", $"{name}_trajectory.csv");
                return new
                {
                    IsValid = leftVideos.Length == 1 && rightVideos.Length == 1,
                    Sample = new SampleInfo(name, d, leftVideos.Length + rightVideos.Length,
                        Directory.Exists(output3d),
                        Directory.Exists(outputB),
                        File.Exists(unityCsv))
                };
            })
            // A valid sample has one video for each calibrated camera.  Do not
            // infer sides from filenames or the image content.
            .Where(s => s.IsValid)
            .Select(s => s.Sample)
            .OrderBy(s => s.Name)
            .ToList();
    }

    private static string[] GetCameraVideos(string sampleDirectory, string cameraName)
    {
        var cameraDirectory = Path.Combine(sampleDirectory, cameraName);
        return Directory.Exists(cameraDirectory)
            ? Directory.GetFiles(cameraDirectory, "*.mp4", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
    }

    public List<GoalkeeperInfo> ScanGoalkeepers()
    {
        var gkDir = Path.Combine(_engine.WorkspaceDir, "data", "goalkeepers");
        if (!Directory.Exists(gkDir)) return new();

        return Directory.GetFiles(gkDir, "*.json")
            .Select(f => new GoalkeeperInfo(Path.GetFileNameWithoutExtension(f), f))
            .OrderBy(g => g.Name)
            .ToList();
    }

    public GoalkeeperStats? LoadGoalkeeperStats(string name)
    {
        var gkDir = Path.Combine(_engine.WorkspaceDir, "data", "goalkeepers");
        var path = Path.Combine(gkDir, $"{name}.json");
        if (!File.Exists(path)) return null;

        try
        {
            string json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<GoalkeeperStats>(json);
        }
        catch
        {
            return null;
        }
    }

    public CalibrationStatus GetCalibrationStatus()
    {
        var calibDir = Path.Combine(_engine.WorkspaceDir, "calib");
        return new CalibrationStatus(
            File.Exists(Path.Combine(calibDir, "intrinsics_left.npz")) &&
            File.Exists(Path.Combine(calibDir, "intrinsics_right.npz")),
            File.Exists(Path.Combine(calibDir, "left_pose.npz")) &&
            File.Exists(Path.Combine(calibDir, "right_pose.npz")),
            Path.Combine(calibDir, "intrinsics_left.npz"),
            Path.Combine(calibDir, "intrinsics_right.npz"),
            Path.Combine(calibDir, "left_pose.npz"),
            Path.Combine(calibDir, "right_pose.npz")
        );
    }

    public async Task<bool> RunOfflineAsync(
        List<string> sampleNames, bool skipReconstruct, bool skipBallistic,
        string? goalkeeperName, IProgress<(string, string)>? logProgress,
        CancellationToken cancellation)
    {
        var samples = string.Join(" ", sampleNames.Select(n => $"\"{n}\""));
        var skip = "";
        if (skipReconstruct) skip += " --skip-reconstruct";
        if (skipBallistic) skip += " --skip-ballistic";

        var scriptPath = _engine.EngineEntryPath;
        var args = $"offline --samples {samples} {skip} --workspace \"{_engine.WorkspaceDir}\"";

        logProgress?.Report(("开始离线处理...", "info"));

        var (code, output, error) = await _engine.RunAsync(
            scriptPath, args, timeoutMs: 1800000, cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")));

        if (code == 0 && !string.IsNullOrWhiteSpace(goalkeeperName))
            SaveGoalkeeperSelection(sampleNames, goalkeeperName);

        logProgress?.Report((code == 0 ? "======== 全部完成 ========" : $"处理失败 (退出码: {code})",
            code == 0 ? "success" : "error"));
        return code == 0;
    }

    public string? GetGoalkeeperForTrajectory(string sampleName)
    {
        if (string.IsNullOrWhiteSpace(sampleName))
            return null;

        var path = GetTrajectoryMetadataPath(sampleName);
        if (!File.Exists(path))
            return null;

        try
        {
            var metadata = JsonSerializer.Deserialize<TrajectoryDeliveryMetadata>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(metadata?.GoalkeeperName) ? null : metadata.GoalkeeperName;
        }
        catch
        {
            return null;
        }
    }

    private void SaveGoalkeeperSelection(IEnumerable<string> sampleNames, string goalkeeperName)
    {
        foreach (var sampleName in sampleNames.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var trajectory = Path.Combine(_engine.WorkspaceDir, "data", $"{sampleName}_trajectory.csv");
            if (!File.Exists(trajectory))
                continue;

            File.WriteAllText(GetTrajectoryMetadataPath(sampleName),
                JsonSerializer.Serialize(new TrajectoryDeliveryMetadata(goalkeeperName)));
        }
    }

    private string GetTrajectoryMetadataPath(string sampleName) =>
        Path.Combine(_engine.WorkspaceDir, "data", $"{sampleName}_trajectory.meta.json");

    public async Task<bool> RunOnlineAsync(
        string camLeft, string camRight, string sampleName,
        IProgress<(string, string)>? logProgress, CancellationToken cancellation)
    {
        // 本地索引或网络 URL 都直接透传给 Python 引擎（引擎已支持 RTSP/HTTP 流）
        var isNetwork = camLeft.Contains("://", StringComparison.Ordinal)
                        || camRight.Contains("://", StringComparison.Ordinal);
        logProgress?.Report((isNetwork
            ? $"开始在线录制(手机/网络相机): 左={camLeft} 右={camRight}..."
            : $"开始在线录制(USB 相机): 左={camLeft} 右={camRight}...", "info"));

        var (code, output, error) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            $"online --cam-left \"{camLeft}\" --cam-right \"{camRight}\" --sample \"{sampleName}\" " +
            $"--workspace \"{_engine.WorkspaceDir}\"",
            cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")));

        return code == 0;
    }

    public async Task<bool> RunOnlineLiveAsync(
        string camLeft, string camRight, string sampleName,
        IProgress<(string, string)>? logProgress, CancellationToken cancellation)
    {
        var isNetwork = camLeft.Contains("://", StringComparison.Ordinal)
                        || camRight.Contains("://", StringComparison.Ordinal);
        logProgress?.Report((isNetwork
            ? $"开始边录制边分析(手机/网络相机): 左={camLeft} 右={camRight}..."
            : $"开始边录制边分析(USB 相机): 左={camLeft} 右={camRight}...", "info"));

        var (code, output, error) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            $"online-live --cam-left \"{camLeft}\" --cam-right \"{camRight}\" " +
            $"--sample \"{sampleName}\" --workspace \"{_engine.WorkspaceDir}\"",
            timeoutMs: 1800000,
            cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")));

        return code == 0;
    }

    public async Task<(bool Ok, string Message)> TestNetworkStreamAsync(
        string url, int timeoutSeconds = 8)
    {
        var (code, output, error) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            $"teststream --source \"{url}\" --timeout {timeoutSeconds} " +
            $"--workspace \"{_engine.WorkspaceDir}\"",
            timeoutMs: (timeoutSeconds + 30) * 1000);

        var msg = (output ?? "").Trim();
        if (string.IsNullOrEmpty(msg)) msg = (error ?? "").Trim();
        if (string.IsNullOrEmpty(msg)) msg = $"测试失败 (退出码 {code})";

        return (code == 0, msg);
    }

    public async Task<List<int>> DetectCamerasAsync()
    {
        // 短时间内复用缓存，避免每次点“检测摄像头”都启动一个 Python 子进程
        if (_cachedCameras is not null && DateTime.UtcNow - _camerasCacheTime < CamerasCacheTtl)
            return _cachedCameras;

        var (code, output, _) = await _engine.RunAsync(
            "-c",
            "\"from aifootball.capture.dual_camera import DualCameraRecorder; " +
            "print(','.join(str(c) for c in DualCameraRecorder.list_cameras()))\"");

        var cameras = (code != 0 || string.IsNullOrWhiteSpace(output))
            ? new List<int>()
            : output.Trim().Split(',')
                .Select(s => int.TryParse(s.Trim(), out var c) ? c : -1)
                .Where(c => c >= 0)
                .ToList();

        _cachedCameras = cameras;
        _camerasCacheTime = DateTime.UtcNow;
        return cameras;
    }

    public Process? LaunchUnityViewer(List<string> sampleNames, string? goalkeeperName = null, bool embedded = false,
        nint hostWindowHandle = 0)
    {
        var exe = FindUnityExe();
        if (exe == null)
        {
            Debug.WriteLine("[Unity] 未找到 Unity 可执行文件");
            return null;
        }

        var csvPath = Path.Combine(_engine.WorkspaceDir, "data",
            $"{sampleNames[0]}_trajectory.csv");
        if (!File.Exists(csvPath))
        {
            Debug.WriteLine($"[Unity] CSV 不存在: {csvPath}");
            return null;
        }

        // 同步 CSV 到 Unity exe 所在目录的 data/ 下（Unity 自动检测需要）
        var unityDataDir = Path.Combine(Path.GetDirectoryName(exe)!, "data");
        try
        {
            Directory.CreateDirectory(unityDataDir);
            var destCsv = Path.Combine(unityDataDir, $"{sampleNames[0]}_trajectory.csv");
            File.Copy(csvPath, destCsv, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Unity] CSV 同步失败: {ex.Message}");
        }

        // 构建命令行参数
        var startInfo = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe),
            UseShellExecute = !embedded,
            CreateNoWindow = embedded,
            WindowStyle = ProcessWindowStyle.Normal,
        };

        // 传 --csv 绝对路径（Unity 支持直接加载）
        var argList = new List<string>();
        if (embedded)
        {
            // Let the Unity Windows Player create itself as a child window from startup.
            // This preserves its native input routing and dynamic render resolution.
            argList.AddRange(["-popupwindow", "-screen-fullscreen", "0", "--wpf-host"]);
            var commandFile = Path.Combine(_engine.WorkspaceDir, "runtime", "data", "wpf-unity-command.txt");
            argList.AddRange(["--wpf-command", $"\"{commandFile}\""]);
            if (hostWindowHandle != 0)
                argList.AddRange(["-parentHWND", hostWindowHandle.ToString(), "delayed"]);
        }
        argList.AddRange(["--csv", $"\"{csvPath}\""]);

        if (!string.IsNullOrEmpty(goalkeeperName))
        {
            var gkPath = Path.Combine(_engine.WorkspaceDir, "data", "goalkeepers",
                $"{goalkeeperName}.json");
            if (File.Exists(gkPath))
                argList.Add($"--goalkeeper \"{gkPath}\"");
        }

        startInfo.Arguments = string.Join(" ", argList);

        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Unity] 启动失败: {ex.Message}");
        }
        return null;
    }

    private string? FindUnityExe()
    {
        var ws = _engine.WorkspaceDir;
        // 优先 FootballViewer.exe（根目录）
        // 搜索根目录下所有 Unity 构建的 exe
        // 当前主项目的 Unity 运行版位于 runtime/。这里优先于旧发布目录，
        // 确保桌面程序启动的是刚由 Unity Build 更新的查看器。
        // Packaged releases keep Unity separate from the self-contained .NET files.
        // Otherwise Unity Mono can load the WPF runtime assemblies and crash on startup.
        var runtimeDir = Path.Combine(ws, "runtime");
        if (Directory.Exists(runtimeDir))
        {
            foreach (var exe in FindUnityExesInDir(runtimeDir))
                return exe;
        }

        // 打包发布版把 Unity 查看器放在 viewer/（与自包含 .NET 文件分离，
        // 避免 Unity Mono 误加载 WPF 运行时程序集导致启动崩溃）
        var viewerDir = Path.Combine(ws, "viewer");
        if (Directory.Exists(viewerDir))
        {
            foreach (var exe in FindUnityExesInDir(viewerDir))
                return exe;
        }

        // 也搜索 AIfootball-Windows 子目录（旧项目数据）
        var legacyDir = Path.Combine(ws, "AIfootball-Windows");
        if (Directory.Exists(legacyDir))
        {
            foreach (var exe in FindUnityExesInDir(legacyDir))
                return exe;
        }

        return null;
    }

    private static IEnumerable<string> FindUnityExesInDir(string dir)
    {
        foreach (var exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(exe);
            // 跳过自己的 exe
            if (name == "AIfootball" || name == "dotnet") continue;

            // 有对应 _Data 目录 = Unity 构建
            var dataDir = Path.Combine(dir, $"{name}_Data");
            if (Directory.Exists(dataDir))
                yield return exe;
        }
    }

    public async Task<bool> CalibrateIntrinsicsAsync(
        string leftVideo, string rightVideo, string calibDir,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default)
    {
        logProgress?.Report(("开始视频内参标定...", "info"));
        var args = $"calibrate-intrinsics --left \"{leftVideo}\" --right \"{rightVideo}\" " +
                   $"--calib \"{calibDir}\" --workspace \"{_engine.WorkspaceDir}\"";
        var (code, _, _) = await _engine.RunAsync(
            _engine.EngineEntryPath, args, timeoutMs: 3600000, cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")));
        return code == 0;
    }

    public async Task<bool> CalibrateExtrinsicsAsync(
        string leftImage, string rightImage, string calibDir,
        IProgress<(string Message, string Level)>? logProgress = null,
        CancellationToken cancellation = default)
    {
        logProgress?.Report(("开始外参标定（注意弹出的参考点点击窗口）...", "info"));
        var intrinsicsLeft = Path.Combine(calibDir, "intrinsics_left.npz");
        var intrinsicsRight = Path.Combine(calibDir, "intrinsics_right.npz");
        var args = $"calibrate-extrinsics --left-image \"{leftImage}\" --right-image \"{rightImage}\" " +
                   $"--intrinsics-left \"{intrinsicsLeft}\" --intrinsics-right \"{intrinsicsRight}\" " +
                   $"--calib \"{calibDir}\" --workspace \"{_engine.WorkspaceDir}\"";
        var (code, _, _) = await _engine.RunAsync(
            _engine.EngineEntryPath, args, timeoutMs: 600000, cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")));
        return code == 0;
    }
}
