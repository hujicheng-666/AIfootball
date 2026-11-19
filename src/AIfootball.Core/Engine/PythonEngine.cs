using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Engine;

/// <summary>一次引擎调用的结果。</summary>
public sealed record EngineResult(int ExitCode, string StandardOutput, string StandardError, bool Cancelled)
{
    public bool Ok => !Cancelled && ExitCode == 0;

    /// <summary>最后一行非空输出，适合当作人类可读的失败原因。</summary>
    public string LastLine
    {
        get
        {
            var lines = StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length > 0) return lines[^1];
            return StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                .LastOrDefault() ?? $"退出码 {ExitCode}";
        }
    }
}

public sealed class EngineRunOptions
{
    /// <summary>实时日志回调（stdout / stderr 都走这里，带级别）。</summary>
    public Action<LogEntry>? OnLog { get; init; }

    /// <summary>需要向子进程下发指令（预览 → start → stop）时提供。回调只调用一次。</summary>
    public Action<Action<string>>? OnControlChannelReady { get; init; }

    public int TimeoutMs { get; init; } = Timeout.Infinite;
    public string LogSource { get; init; } = "engine";
}

/// <summary>
/// Python 引擎宿主。所有推理/标定/采集都通过 <c>python -m aifootball &lt;子命令&gt;</c> 调用，
/// 工作区通过 <c>--workspace</c> + 环境变量双通道下发，不依赖进程工作目录。
/// </summary>
public sealed class PythonEngine : IDisposable
{
    private const int EnvironmentProbeTimeoutMs = 60_000;

    private readonly AifootballWorkspace _workspace;
    private readonly object _processGate = new();
    private readonly HashSet<Process> _liveProcesses = [];

    public PythonEngine(AifootballWorkspace workspace) => _workspace = workspace;

    public AifootballWorkspace Workspace => _workspace;
    public string PythonExecutable => _workspace.PythonExe;
    public bool IsPythonInstalled => File.Exists(_workspace.PythonExe);
    public int LiveProcessCount { get { lock (_processGate) return _liveProcesses.Count; } }

    /// <summary>运行引擎子命令。<paramref name="arguments"/> 为子命令及其参数（无需包含 python 与 -m）。</summary>
    public async Task<EngineResult> RunAsync(
        IReadOnlyList<string> arguments,
        EngineRunOptions? options = null,
        CancellationToken cancellation = default)
    {
        options ??= new EngineRunOptions();
        if (!IsPythonInstalled)
        {
            var message = $"未找到嵌入式 Python：{_workspace.PythonExe}";
            options.OnLog?.Invoke(new LogEntry(DateTimeOffset.Now, LogLevel.Error, message, options.LogSource));
            return new EngineResult(-1, "", message, false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _workspace.PythonExe,
            WorkingDirectory = _workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = options.OnControlChannelReady is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.ArgumentList.Add("-u");
        startInfo.ArgumentList.Add("-m");
        startInfo.ArgumentList.Add("aifootball");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["PYTHONPATH"] = _workspace.EngineDir;
        startInfo.Environment[AifootballWorkspace.WorkspaceEnvironmentVariable] = _workspace.Root;
        startInfo.Environment["YOLO_MODEL_PATH"] = _workspace.ModelFile;

        var output = new StringBuilder();
        var error = new StringBuilder();
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            options.OnLog?.Invoke(new LogEntry(DateTimeOffset.Now, LogLevel.Trace, e.Data, options.LogSource));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            error.AppendLine(e.Data);
            options.OnLog?.Invoke(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, e.Data, options.LogSource));
        };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            var message = $"启动引擎失败：{ex.Message}";
            options.OnLog?.Invoke(new LogEntry(DateTimeOffset.Now, LogLevel.Error, message, options.LogSource));
            return new EngineResult(-1, "", message, false);
        }

        if (options.OnControlChannelReady is not null)
        {
            process.StandardInput.AutoFlush = true;
            var writer = process.StandardInput;
            options.OnControlChannelReady(command =>
            {
                try { writer.WriteLine(command); }
                catch (IOException) { /* 进程已退出 */ }
                catch (ObjectDisposedException) { }
            });
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_processGate) _liveProcesses.Add(process);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (options.TimeoutMs != Timeout.Infinite && options.TimeoutMs > 0)
            timeoutSource.CancelAfter(options.TimeoutMs);

        var cancelled = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            // 等待异步读取把剩余缓冲刷完，避免丢失最后几行
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            TryKill(process);
            options.OnLog?.Invoke(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, "任务已取消", options.LogSource));
        }
        finally
        {
            lock (_processGate) _liveProcesses.Remove(process);
        }

        return new EngineResult(cancelled ? -1 : process.ExitCode, output.ToString(), error.ToString(), cancelled);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    /// <summary>退出应用时必须调用：否则采集子进程会变成孤儿，继续占用 UDP 端口抢收数据包。</summary>
    public void TerminateAll()
    {
        Process[] snapshot;
        lock (_processGate)
        {
            snapshot = [.. _liveProcesses];
            _liveProcesses.Clear();
        }
        foreach (var process in snapshot) TryKill(process);
    }

    /// <summary>单次自检：一次进程拿到全部环境信息（旧实现要起三个进程）。</summary>
    public async Task<EngineReport> ProbeAsync(CancellationToken cancellation = default)
    {
        if (!IsPythonInstalled) return EngineReport.Unavailable("未安装嵌入式 Python 运行环境");

        var result = await RunAsync(["selfcheck"], new EngineRunOptions
        {
            TimeoutMs = EnvironmentProbeTimeoutMs,
            LogSource = "selfcheck",
        }, cancellation).ConfigureAwait(false);

        var json = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => line.StartsWith('{'));
        if (json is null)
            return EngineReport.Unavailable(result.Cancelled ? "自检超时" : result.LastLine);

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new EngineReport(
                Available: true,
                PythonVersion: ReadString(root, "python"),
                TorchVersion: ReadString(root, "torch"),
                OpenCvVersion: ReadString(root, "opencv"),
                UltralyticsVersion: ReadString(root, "ultralytics"),
                CudaAvailable: root.TryGetProperty("cuda", out var cuda) && cuda.ValueKind == JsonValueKind.True,
                ModelPath: ReadString(root, "model") ?? _workspace.ModelFile,
                ModelPresent: root.TryGetProperty("model_present", out var present) && present.ValueKind == JsonValueKind.True,
                Error: null);
        }
        catch (JsonException ex)
        {
            return EngineReport.Unavailable($"自检输出无法解析：{ex.Message}");
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose() => TerminateAll();
}
