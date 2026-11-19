using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using AIfootball.Core.Engine;
using AIfootball.Core.Models;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

public sealed record SetupProgress(string Message, int Percent)
{
    /// <summary>-1 = 纯日志行；-2 = 子进程原始输出。</summary>
    public static SetupProgress Log(string message) => new(message, -1);
    public static SetupProgress Raw(string message) => new(message, -2);
}

/// <summary>
/// 首次运行的环境自举：嵌入式 Python → pip → 依赖 → 自检。
/// 只在 <see cref="PythonEngine.ProbeAsync"/> 失败时才会被调用。
/// </summary>
public sealed class EnvironmentInstaller(AifootballWorkspace workspace, PythonEngine engine)
{
    private const string PythonVersion = "3.10.11";
    private const string PipBootstrapUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string PyPiMirror = "https://pypi.tuna.tsinghua.edu.cn/simple";

    private static readonly string[] PythonMirrors =
    [
        $"https://registry.npmmirror.com/-/binary/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
        $"https://mirrors.huaweicloud.com/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
        $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
    ];

    private readonly AifootballWorkspace _workspace = workspace;
    private readonly PythonEngine _engine = engine;
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromMinutes(30),
    };

    public async Task<bool> EnsureReadyAsync(IProgress<SetupProgress>? progress, CancellationToken cancellation)
    {
        try
        {
            if (!File.Exists(_workspace.PythonExe))
            {
                progress?.Report(SetupProgress.Log("未检测到嵌入式 Python，开始下载…"));
                if (!await DownloadPythonAsync(progress, cancellation).ConfigureAwait(false)) return false;
            }

            if (!await HasPipAsync(cancellation).ConfigureAwait(false))
            {
                progress?.Report(SetupProgress.Log("安装 pip…"));
                if (!await InstallPipAsync(progress, cancellation).ConfigureAwait(false)) return false;
            }

            if (!await InstallRequirementsAsync(progress, cancellation).ConfigureAwait(false)) return false;

            progress?.Report(new SetupProgress("校验推理环境…", 96));
            var report = await _engine.ProbeAsync(cancellation).ConfigureAwait(false);
            progress?.Report(SetupProgress.Log(report.Summary));
            if (!report.Available) return false;

            progress?.Report(new SetupProgress("环境就绪", 100));
            return true;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(SetupProgress.Log("安装已取消"));
            return false;
        }
        catch (Exception ex)
        {
            progress?.Report(SetupProgress.Log($"安装失败：{ex.Message}"));
            return false;
        }
    }

    private async Task<bool> DownloadPythonAsync(IProgress<SetupProgress>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(_workspace.PythonEnvDir);
        var archive = Path.Combine(_workspace.PythonEnvDir, "python-embed.zip");

        foreach (var url in PythonMirrors)
        {
            try
            {
                progress?.Report(new SetupProgress($"下载 Python 运行环境（{new Uri(url).Host}）…", 5));
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellation)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var target = File.Create(archive))
                {
                    await response.Content.CopyToAsync(target, cancellation).ConfigureAwait(false);
                }

                progress?.Report(new SetupProgress("解压运行环境…", 25));
                ZipFile.ExtractToDirectory(archive, _workspace.PythonEnvDir, overwriteFiles: true);
                File.Delete(archive);
                EnableSitePackages();
                progress?.Report(SetupProgress.Log($"Python {PythonVersion} 运行环境就绪"));
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                progress?.Report(SetupProgress.Log($"  ✗ {new Uri(url).Host}: {ex.Message}"));
            }
        }

        progress?.Report(SetupProgress.Log("所有镜像均下载失败，请检查网络/代理后重试"));
        return false;
    }

    /// <summary>嵌入式发行版默认不导入 site-packages，必须写 _pth 打开。</summary>
    private void EnableSitePackages()
    {
        var pth = Path.Combine(_workspace.PythonEnvDir, "python310._pth");
        if (!File.Exists(pth)) return;
        var lines = File.ReadAllLines(pth).ToList();
        if (!lines.Any(l => l.Trim().Equals("import site", StringComparison.OrdinalIgnoreCase)))
            lines.Add("import site");
        if (!lines.Any(l => l.Trim().Equals("Lib\\site-packages", StringComparison.OrdinalIgnoreCase)))
            lines.Add("Lib\\site-packages");
        File.WriteAllLines(pth, lines);
    }

    private async Task<bool> HasPipAsync(CancellationToken cancellation)
    {
        var result = await _engine.RunAsync(["--version"], new EngineRunOptions { TimeoutMs = 30_000 }, cancellation)
            .ConfigureAwait(false);
        // 引擎没有 --version 子命令时会打印帮助并返回非 0；这里只要进程能起来就说明解释器可用
        if (result.ExitCode == -1 && result.StandardError.Contains("No module named", StringComparison.Ordinal))
            return false;

        var pipCheck = await RunPythonAsync("-m pip --version", progress: null, cancellation).ConfigureAwait(false);
        return pipCheck.ExitCode == 0;
    }

    private async Task<bool> InstallPipAsync(IProgress<SetupProgress>? progress, CancellationToken cancellation)
    {
        var bootstrap = Path.Combine(_workspace.PythonEnvDir, "get-pip.py");
        try
        {
            var script = await _http.GetStringAsync(PipBootstrapUrl, cancellation).ConfigureAwait(false);
            await File.WriteAllTextAsync(bootstrap, script, cancellation).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            progress?.Report(SetupProgress.Log($"下载 get-pip.py 失败：{ex.Message}"));
            return false;
        }

        var result = await RunPythonAsync($"\"{bootstrap}\" -i {PyPiMirror}", progress, cancellation).ConfigureAwait(false);
        try { File.Delete(bootstrap); } catch (IOException) { }
        return result.ExitCode == 0;
    }

    private async Task<bool> InstallRequirementsAsync(IProgress<SetupProgress>? progress, CancellationToken cancellation)
    {
        var requirements = Path.Combine(_workspace.EngineDir, "requirements.txt");
        var argument = File.Exists(requirements)
            ? $"-m pip install -r \"{requirements}\" -i {PyPiMirror} --upgrade"
            : $"-m pip install numpy opencv-python scipy ultralytics torch -i {PyPiMirror} --upgrade";

        progress?.Report(new SetupProgress("安装推理依赖（torch / ultralytics / opencv）…", 40));
        var result = await RunPythonAsync(argument, progress, cancellation).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<EngineResult> RunPythonAsync(string arguments, IProgress<SetupProgress>? progress,
        CancellationToken cancellation)
    {
        if (!File.Exists(_workspace.PythonExe))
            return new EngineResult(-1, "", "缺少嵌入式 Python", false);

        var startInfo = new ProcessStartInfo
        {
            FileName = _workspace.PythonExe,
            Arguments = arguments,
            WorkingDirectory = _workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = new Process { StartInfo = startInfo };
        var output = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            progress?.Report(SetupProgress.Raw(e.Data));
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            progress?.Report(SetupProgress.Raw(e.Data));
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(cancellation).ConfigureAwait(false);
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new EngineResult(-1, output.ToString(), "已取消", true);
        }
        return new EngineResult(process.ExitCode, output.ToString(), "", false);
    }

    public void Dispose() => _http.Dispose();
}
