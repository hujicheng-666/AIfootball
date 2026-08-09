using System.Diagnostics;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>Python 引擎服务 — 管理与嵌入式 Python 的通信</summary>
public class PythonEngineService : IPythonEngine
{
    private readonly string _baseDir;
    private readonly string _pythonEnvDir;
    private readonly string _pythonExe;

    public PythonEngineService(string baseDir)
    {
        _baseDir = baseDir;
        _pythonEnvDir = Path.Combine(baseDir, "python_env");
        _pythonExe = Path.Combine(_pythonEnvDir, "python.exe");
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
        Action<string>? errorReceived = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _pythonExe,
            Arguments = $"-u \"{script}\" {arguments}",
            WorkingDirectory = _baseDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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

        var output = new System.Text.StringBuilder();
        var error = new System.Text.StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            output.AppendLine(e.Data);
            outputReceived?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            error.AppendLine(e.Data);
            errorReceived?.Invoke(e.Data);
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

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
            return (-1, output.ToString(), "操作超时或被取消");
        }

        return (proc.ExitCode, output.ToString(), error.ToString());
    }

    public async Task<bool> IsEnvironmentReadyAsync()
    {
        if (!File.Exists(_pythonExe)) return false;

        var result = await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = _pythonExe,
                Arguments = "-c \"import torch, ultralytics, cv2, scipy; print('OK')\"",
                WorkingDirectory = _baseDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return (1, "");
            p.WaitForExit(10000);
            return (p.ExitCode, p.StandardOutput.ReadToEnd());
        });

        return result.Item1 == 0 && result.Item2.Contains("OK");
    }

    public async Task<EnvironmentStatus> GetEnvironmentStatusAsync()
    {
        if (!File.Exists(_pythonExe))
            return EnvironmentStatus.Unknown;

        try
        {
            // 注意：RunAsync 会自行拼接 "-u \"-c\"" 前缀，这里只传代码本体并用引号包裹，
            // 避免出现两个 "-c" 导致 Python 把代码当作脚本文件名而报 SyntaxError。
            var codeText = "import sys; print(sys.version.split()[0]); import torch; print(torch.__version__); print('cuda-cu124' if torch.cuda.is_available() else 'cpu')";
            var (code, output, _) = await RunAsync("-c", $"\"{codeText}\"", 15000);
            if (code != 0) return EnvironmentStatus.Unknown;

            var lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var pyVer = lines.Length > 0 ? lines[0].Trim() : "";
            var torchVer = lines.Length > 1 ? lines[1].Trim() : "";
            var profile = lines.Length > 2 ? lines[2].Trim() : "cpu";

            var gpu = await new GpuDetectionService().DetectAsync();
            return new EnvironmentStatus(true, pyVer, torchVer, profile,
                gpu.CudaAvailable, gpu.GpuName, gpu.AdapterNames);
        }
        catch
        {
            return EnvironmentStatus.Unknown;
        }
    }
}
