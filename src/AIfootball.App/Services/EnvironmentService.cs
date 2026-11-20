using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>在当前用户目录中安装应用自带的 Python 推理环境。</summary>
public class EnvironmentService : IEnvironmentService
{
    private const string PythonVersion = "3.10.11";
    private const string TorchVersion = "2.6.0";
    private const string TorchVisionVersion = "0.21.0";
    private const string PyPi = "https://pypi.org/simple";
    private const string PyPiMirror = "https://pypi.tuna.tsinghua.edu.cn/simple";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly string _baseDir;
    private readonly string _pythonEnvDir;
    private readonly string _pythonExe;
    private readonly string _profileFile;

    public EnvironmentService(string baseDir)
    {
        _baseDir = baseDir;
        _pythonEnvDir = PythonEngineService.ResolveEnvironmentDirectory();
        _pythonExe = Path.Combine(_pythonEnvDir, "python.exe");
        _profileFile = Path.Combine(_pythonEnvDir, ".inference-profile");
    }

    public async Task<bool> IsCompatibleAsync() =>
        File.Exists(_profileFile) && await CanRunPythonAsync("-c", "import torch, torchvision, cv2, scipy, ultralytics");

    public async Task<bool> SetupAsync(IProgress<(string Status, int Percent)> progress)
    {
        try
        {
            Directory.CreateDirectory(_pythonEnvDir);
            using var installLock = new FileStream(Path.Combine(_pythonEnvDir, ".install.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            if (await IsCompatibleAsync())
            {
                progress.Report(("Python 推理环境已就绪。", 100));
                return true;
            }

            File.Delete(_profileFile);
            if (!await CanRunPythonAsync("-c", "import encodings, ssl, site"))
            {
                progress.Report(("正在下载 Python 3.10 运行时…", 5));
                await DownloadPythonAsync();
            }

            if (!await CanRunPythonAsync("-m", "pip", "--version"))
            {
                progress.Report(("正在安装 pip…", 15));
                var bootstrap = Path.Combine(_pythonEnvDir, "get-pip.py");
                try
                {
                    await File.WriteAllBytesAsync(bootstrap, await Http.GetByteArrayAsync("https://bootstrap.pypa.io/get-pip.py"));
                    await InstallFromIndexAsync(progress, [bootstrap, "--no-warn-script-location"]);
                }
                finally
                {
                    File.Delete(bootstrap);
                }
            }

            var profile = "cpu";
            if (Environment.GetEnvironmentVariable("AIFOOTBALL_CPU_ONLY") != "1" &&
                (await new GpuDetectionService().DetectAsync()).CudaAvailable)
            {
                try
                {
                    progress.Report(("正在安装 CUDA 推理依赖…", 25));
                    await InstallTorchAsync("cu124", progress);
                    await RunPythonAsync(progress, TimeSpan.FromMinutes(2), "-c",
                        "import torch, torchvision; torch.zeros(1, device='cuda')");
                    profile = "cuda-cu124";
                }
                catch (Exception ex)
                {
                    progress.Report(($"CUDA 初始化失败，将改用 CPU。{ex.Message}", 30));
                }
            }

            if (profile == "cpu")
            {
                progress.Report(("正在安装 CPU 推理依赖…", 35));
                await InstallTorchAsync("cpu", progress);
            }

            progress.Report(("正在安装 YOLO、OpenCV 和 SciPy…", 65));
            await InstallFromIndexAsync(progress,
                ["-m", "pip", "install", "--only-binary=:all:", "--no-warn-script-location",
                 "numpy==2.2.6", "opencv-python==4.12.0.88", "scipy==1.15.3", "ultralytics==8.4.103"]);

            progress.Report(("正在验证推理环境…", 95));
            await RunPythonAsync(progress, TimeSpan.FromMinutes(2), "-c",
                "import torch, torchvision, cv2, scipy, ultralytics; torch.zeros(1)");
            await File.WriteAllTextAsync(_profileFile, profile);
            progress.Report(($"推理环境已就绪（{profile}）。", 100));
            return true;
        }
        catch (Exception ex)
        {
            progress.Report(($"推理环境安装失败。{ex.Message}", -1));
            return false;
        }
    }

    private async Task DownloadPythonAsync()
    {
        var archive = $"python-{PythonVersion}-embed-amd64.zip";
        var path = Path.Combine(_pythonEnvDir, archive);
        try
        {
            await File.WriteAllBytesAsync(path,
                await Http.GetByteArrayAsync($"https://www.python.org/ftp/python/{PythonVersion}/{archive}"));
            ZipFile.ExtractToDirectory(path, _pythonEnvDir, overwriteFiles: true);
            await File.WriteAllTextAsync(Path.Combine(_pythonEnvDir, "python310._pth"),
                "python310.zip\n.\nLib\\site-packages\nimport site\n");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private Task InstallTorchAsync(string wheelIndex, IProgress<(string, int)> progress) =>
        RunPythonAsync(progress, TimeSpan.FromMinutes(25), "-m", "pip", "install",
            "--only-binary=:all:", "--no-warn-script-location",
            "--index-url", $"https://download.pytorch.org/whl/{wheelIndex}",
            $"torch=={TorchVersion}+{wheelIndex}", $"torchvision=={TorchVisionVersion}+{wheelIndex}");

    private async Task InstallFromIndexAsync(IProgress<(string, int)> progress, string[] args)
    {
        try
        {
            await RunPythonAsync(progress, TimeSpan.FromMinutes(20), [.. args, "--index-url", PyPi]);
        }
        catch (Exception ex)
        {
            progress.Report(($"PyPI 安装失败，正在尝试镜像。{ex.Message}", -2));
            await RunPythonAsync(progress, TimeSpan.FromMinutes(20), [.. args, "--index-url", PyPiMirror]);
        }
    }

    private async Task<bool> CanRunPythonAsync(params string[] args)
    {
        if (!File.Exists(_pythonExe)) return false;

        try
        {
            await RunPythonAsync(null, TimeSpan.FromMinutes(2), args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunPythonAsync(IProgress<(string, int)>? progress, TimeSpan timeout, params string[] args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _pythonExe,
            WorkingDirectory = _baseDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动 Python：{_pythonExe}");
        var output = ReadProcessStreamAsync(process.StandardOutput, progress);
        var error = ReadProcessStreamAsync(process.StandardError, progress);
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await Task.WhenAll(output, error);
            throw new TimeoutException($"Python 命令超时：{string.Join(" ", args)}");
        }

        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Python 退出码 {process.ExitCode}。{await error}");
    }

    private static async Task<string> ReadProcessStreamAsync(StreamReader reader, IProgress<(string, int)>? progress)
    {
        var lastLine = "";
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            lastLine = line;
            progress?.Report((line, -2));
        }
        return lastLine;
    }
}
