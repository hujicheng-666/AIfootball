using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>Python 引擎服务 — 管理与嵌入式 Python 的通信</summary>
public class PythonEngineService : IPythonEngine
{
    // selfcheck 要导入 torch/ultralytics 并探测 CUDA，冷启动可能超过 10 秒。
    // 单独给它一个宽松的超时，避免把"慢"误判成"没装"。
    private const int SelfCheckTimeoutMs = 60000;

    private readonly object _selfCheckLock = new();
    private EngineSelfCheck? _selfCheckResult;
    private DateTime _selfCheckAt = DateTime.MinValue;
    private static readonly TimeSpan SelfCheckTtl = TimeSpan.FromSeconds(60);

    private readonly string _baseDir;
    private readonly string _pythonExe;

    // 正在运行的 Python 子进程：应用退出时要把它们一起结束，
    // 否则采集进程会变成孤儿继续占着 UDP 端口。
    private readonly object _processLock = new();
    private readonly HashSet<Process> _runningProcesses = new();

    public PythonEngineService(string baseDir)
    {
        _baseDir = baseDir;
        _pythonExe = Path.Combine(ResolveEnvironmentDirectory(), "python.exe");
    }

    internal static string ResolveEnvironmentDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("AIFOOTBALL_PYTHON_ENV");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIfootball", "Python310")
            : Path.GetFullPath(configured);
    }

    public string PythonExePath => _pythonExe;
    public string WorkspaceDir => _baseDir;

    /// <summary>
    /// 引擎目录：优先开发布局 (src/AIfootball.Engine)，其次发布布局 (python_engine)。
    /// 发布版不会包含 src/ 目录，因此必须能回退到嵌入的 python_engine/aifootball。
    /// </summary>
    public string EngineDir
    {
        get
        {
            var devDir = Path.Combine(_baseDir, "src", "AIfootball.Engine");
            if (Directory.Exists(Path.Combine(devDir, "aifootball")))
                return devDir;
            var embeddedDir = Path.Combine(_baseDir, "python_engine");
            if (Directory.Exists(Path.Combine(embeddedDir, "aifootball")))
                return embeddedDir;
            return devDir;
        }
    }

    public string EngineEntryPath => Path.Combine(EngineDir, "aifootball", "__main__.py");

    public async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string script, string arguments = "", int timeoutMs = 300000,
        CancellationToken cancellation = default,
        Action<string>? outputReceived = null,
        Action<string>? errorReceived = null,
        Action<StreamWriter>? inputReady = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = $"-u \"{script}\" {arguments}",
            WorkingDirectory = _baseDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 需要向子进程下发指令（预览 -> 开始分析 -> 停止）时才接管 stdin：
            // 采集脚本用行协议读它，父进程自己不读，两边不会抢同一份输入。
            RedirectStandardInput = inputReady != null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        // 设置环境变量
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["YOLO_MODEL_PATH"] = Path.Combine(_baseDir, "models", "yolo11m.pt");
        // 让 Python 能找到 aifootball 引擎模块（开发与发布布局自动适配）
        psi.Environment["PYTHONPATH"] = EngineDir;

        using var proc = new Process { StartInfo = psi };

        // 仅保留最近若干行输出，避免长时间任务日志无上限增长内存
        const int maxBufferedLines = 2000;
        var outputLines = new List<string>();
        var errorLines = new List<string>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (outputLines.Count >= maxBufferedLines)
                outputLines.RemoveAt(0);
            outputLines.Add(e.Data);
            outputReceived?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            if (errorLines.Count >= maxBufferedLines)
                errorLines.RemoveAt(0);
            errorLines.Add(e.Data);
            errorReceived?.Invoke(e.Data);
        };

        proc.Start();
        if (inputReady != null)
        {
            // AutoFlush：命令按行下发，不能留在缓冲区里等进程结束
            proc.StandardInput.AutoFlush = true;
            inputReady(proc.StandardInput);
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        lock (_processLock)
        {
            _runningProcesses.Add(proc);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            if (!proc.HasExited)
                proc.Kill(entireProcessTree: true);
            return (-1, string.Join(Environment.NewLine, outputLines), "操作超时或被取消");
        }
        finally
        {
            lock (_processLock)
            {
                _runningProcesses.Remove(proc);
            }
        }

        return (proc.ExitCode, string.Join(Environment.NewLine, outputLines),
                string.Join(Environment.NewLine, errorLines));
    }

    public void TerminateAll()
    {
        Process[] snapshot;
        lock (_processLock)
        {
            snapshot = _runningProcesses.ToArray();
            _runningProcesses.Clear();
        }

        foreach (var process in snapshot)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Python] 退出时结束子进程失败: {ex.Message}");
            }
        }
    }

    public async Task<bool> IsEnvironmentReadyAsync() =>
        await RunSelfCheckAsync() is { HasCoreDependencies: true };

    /// <summary>
    /// 跑 `aifootball selfcheck`，界面与命令行看到的就是同一份结论。
    /// 结果缓存一小段时间，启动流程里多处调用不会反复导入 torch。
    /// </summary>
    public async Task<EngineSelfCheck?> RunSelfCheckAsync(bool force = false)
    {
        if (!File.Exists(_pythonExe))
            return null;

        lock (_selfCheckLock)
        {
            if (!force && _selfCheckResult is not null
                && DateTime.UtcNow - _selfCheckAt < SelfCheckTtl)
                return _selfCheckResult;
        }

        var (code, output, error) = await RunAsync(
            EngineEntryPath,
            $"selfcheck --workspace \"{_baseDir}\"",
            SelfCheckTimeoutMs);

        if (code != 0)
        {
            Debug.WriteLine($"[SelfCheck] 退出码 {code}: {error}");
            return null;
        }

        // stdout 里只认最后一行 JSON：库自身的初始化噪音都走 stderr
        var line = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(text => text.Trim())
            .LastOrDefault(text => text.StartsWith('{') && text.EndsWith('}'));
        if (line is null)
        {
            Debug.WriteLine("[SelfCheck] 输出里没有 JSON 行");
            return null;
        }

        try
        {
            var result = JsonSerializer.Deserialize<EngineSelfCheck>(line);
            lock (_selfCheckLock)
            {
                _selfCheckResult = result;
                _selfCheckAt = DateTime.UtcNow;
            }
            return result;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[SelfCheck] 解析失败: {ex.Message}");
            return null;
        }
    }

    public async Task<EnvironmentStatus> GetEnvironmentStatusAsync()
    {
        var check = await RunSelfCheckAsync();
        if (check is null)
            return EnvironmentStatus.Unknown;

        var gpu = await new GpuDetectionService().DetectAsync();
        return new EnvironmentStatus(
            check.HasCoreDependencies,
            check.Python ?? "",
            check.Torch ?? "",
            check.Cuda ? "cuda" : "cpu",
            gpu.CudaAvailable,
            gpu.GpuName,
            gpu.AdapterNames);
    }
}
