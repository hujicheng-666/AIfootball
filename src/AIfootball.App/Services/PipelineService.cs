using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;
using AIfootball.Core.Models;

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
                // 落点随扫描一起带出，样本列表不用再点开才看得到结果
                var shot = File.Exists(Path.Combine(outputB, "ballistic_fit_summary.json"))
                    ? LoadShotPlacement(name)
                    : null;
                return new
                {
                    IsValid = leftVideos.Length == 1 && rightVideos.Length == 1,
                    Sample = new SampleInfo(name, d, leftVideos.Length + rightVideos.Length,
                        Directory.Exists(output3d),
                        Directory.Exists(outputB),
                        File.Exists(unityCsv),
                        shot?.Lateral,
                        shot?.Height,
                        shot?.Speed)
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

    /// <summary>
    /// 保存（或另存）门将属性。写入的是 Unity 播放器读的同一份文件，
    /// 因此界面里改完立刻对回放生效。
    /// </summary>
    public bool SaveGoalkeeperStats(string name, GoalkeeperStats stats, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(name))
        {
            message = "门将名称不能为空";
            return false;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            message = "名称不能包含 \\ / : * ? \" < > | 等字符";
            return false;
        }

        var gkDir = Path.Combine(_engine.WorkspaceDir, "data", "goalkeepers");
        var path = Path.Combine(gkDir, $"{name}.json");
        try
        {
            Directory.CreateDirectory(gkDir);
            // NaN/Infinity 序列化会抛异常，正好在这里挡住坏数据，而不是写出
            // 一份 Unity 解析不了的文件。
            File.WriteAllText(path, JsonSerializer.Serialize(stats,
                new JsonSerializerOptions { WriteIndented = true }));
            message = $"已保存 data/goalkeepers/{name}.json";
            return true;
        }
        catch (Exception ex)
        {
            message = $"保存失败：{ex.Message}";
            return false;
        }
    }

    public ShotPlacementSummary LoadShotPlacements(IEnumerable<string> sampleNames)
    {
        var shots = new List<ShotPlacement>();
        foreach (var sampleName in sampleNames
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var shot = LoadShotPlacement(sampleName);
            if (shot is not null) shots.Add(shot);
        }
        return new ShotPlacementSummary(shots);
    }

    private ShotPlacement? LoadShotPlacement(string sampleName)
    {
        var path = Path.Combine(_engine.WorkspaceDir, "output", "trajectory_ballistic",
            sampleName, "ballistic_fit_summary.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (!root.TryGetProperty("goal_line_crossing", out var crossing)) return null;
            if (!crossing.TryGetProperty("detected", out var detected)
                || detected.ValueKind != JsonValueKind.True) return null;
            if (!crossing.TryGetProperty("point_xyz_m", out var point)
                || point.ValueKind != JsonValueKind.Array || point.GetArrayLength() < 3) return null;

            // 场地坐标：x = 球门线横向（右为正），z = 离地高度
            double lateral = point[0].GetDouble();
            double height = point[2].GetDouble();
            double speed = root.TryGetProperty("initial_speed_mps", out var speedElement)
                           && speedElement.ValueKind == JsonValueKind.Number
                ? speedElement.GetDouble()
                : 0;
            double flightTime = crossing.TryGetProperty("time_sec", out var timeElement)
                                && timeElement.ValueKind == JsonValueKind.Number
                ? timeElement.GetDouble()
                : 0;

            return new ShotPlacement(sampleName, lateral, height, flightTime, speed);
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
        var args = new List<string> { "offline", "--samples" };
        args.AddRange(sampleNames);
        if (skipReconstruct) args.Add("--skip-reconstruct");
        if (skipBallistic) args.Add("--skip-ballistic");
        args.AddRange(["--workspace", _engine.WorkspaceDir]);

        logProgress?.Report(("开始离线处理...", "info"));

        var (code, _, _) = await _engine.RunAsync(
            // Video reconstruction duration depends on clip length and CPU/GPU
            // throughput.  It must be cancellable by the user, but must not be
            // killed by an arbitrary batch-wide wall-clock limit before later
            // samples reach the ballistic/export stages.
            _engine.EngineEntryPath, args, timeoutMs: Timeout.Infinite, cancellation: cancellation,
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

    /// <summary>把「向采集进程下发指令」的能力交给界面，失败只写调试输出。</summary>
    private static Action<StreamWriter>? BuildInputBridge(Action<Action<string>>? onControlReady)
    {
        if (onControlReady == null) return null;
        return writer => onControlReady(command =>
        {
            try
            {
                writer.WriteLine(command);
            }
            catch (Exception ex)
            {
                // 进程已经退出时写入会失败，这里只记录，不影响任务结果
                Debug.WriteLine($"[采集控制] 下发 {command} 失败: {ex.Message}");
            }
        });
    }

    public async Task<bool> RunOnlineAsync(
        string camLeft, string camRight, string sampleName,
        IProgress<(string, string)>? logProgress, CancellationToken cancellation,
        Action<Action<string>>? onControlReady = null)
    {
        // 本地索引或网络 URL 都直接透传给 Python 引擎（引擎已支持 RTSP/HTTP 流）
        var isNetwork = camLeft.Contains("://", StringComparison.Ordinal)
                        || camRight.Contains("://", StringComparison.Ordinal);
        logProgress?.Report((isNetwork
            ? $"开始在线录制(手机/网络相机): 左={camLeft} 右={camRight}..."
            : $"开始在线录制(USB 相机): 左={camLeft} 右={camRight}...", "info"));

        var (code, _, _) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            ["online", "--cam-left", camLeft, "--cam-right", camRight, "--sample", sampleName,
                "--workspace", _engine.WorkspaceDir],
            // 预览 + 录制由用户自己控制时长，不能用一个固定墙钟时间把它掐掉
            timeoutMs: Timeout.Infinite,
            cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")),
            inputReady: BuildInputBridge(onControlReady));

        return code == 0;
    }

    public async Task<bool> RunOnlineLiveAsync(
        string camLeft, string camRight, string sampleName,
        IProgress<(string, string)>? logProgress, CancellationToken cancellation,
        Action<Action<string>>? onControlReady = null)
    {
        var isNetwork = camLeft.Contains("://", StringComparison.Ordinal)
                        || camRight.Contains("://", StringComparison.Ordinal);
        logProgress?.Report((isNetwork
            ? $"开始边录制边分析(手机/网络相机): 左={camLeft} 右={camRight}..."
            : $"开始边录制边分析(USB 相机): 左={camLeft} 右={camRight}...", "info"));

        var (code, _, _) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            ["online-live", "--cam-left", camLeft, "--cam-right", camRight, "--sample", sampleName,
                "--workspace", _engine.WorkspaceDir],
            // 预览停留多久、分析录多久都由用户决定，这里不设墙钟上限
            timeoutMs: Timeout.Infinite,
            cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")),
            inputReady: BuildInputBridge(onControlReady));

        return code == 0;
    }

    public async Task<(bool Ok, string Message)> TestNetworkStreamAsync(
        string url, int timeoutSeconds = 8)
    {
        var (code, output, error) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            ["teststream", "--source", url, "--timeout", timeoutSeconds.ToString(CultureInfo.InvariantCulture),
                "--workspace", _engine.WorkspaceDir],
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
            _engine.EngineEntryPath, ["list-cameras", "--workspace", _engine.WorkspaceDir]);

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
        if (exe == null || sampleNames.Count == 0)
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
            argList.AddRange(["--wpf-command", commandFile]);
            if (hostWindowHandle != 0)
                argList.AddRange(["-parentHWND", hostWindowHandle.ToString(), "delayed"]);
        }
        argList.AddRange(["--csv", csvPath]);

        if (!string.IsNullOrEmpty(goalkeeperName))
        {
            var gkPath = Path.Combine(_engine.WorkspaceDir, "data", "goalkeepers",
                $"{goalkeeperName}.json");
            if (File.Exists(gkPath))
                argList.AddRange(["--goalkeeper", gkPath]);
        }

        foreach (var argument in argList)
            startInfo.ArgumentList.Add(argument);

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
        var (code, _, _) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            ["calibrate-intrinsics", "--left", leftVideo, "--right", rightVideo,
                "--calib", calibDir, "--workspace", _engine.WorkspaceDir],
            timeoutMs: 3600000, cancellation: cancellation,
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
        var (code, _, _) = await _engine.RunAsync(
            _engine.EngineEntryPath,
            ["calibrate-extrinsics", "--left-image", leftImage, "--right-image", rightImage,
                "--intrinsics-left", intrinsicsLeft, "--intrinsics-right", intrinsicsRight,
                "--calib", calibDir, "--workspace", _engine.WorkspaceDir],
            timeoutMs: 600000, cancellation: cancellation,
            outputReceived: line => logProgress?.Report((line, "output")),
            errorReceived: line => logProgress?.Report((line, "error")));
        return code == 0;
    }
}
