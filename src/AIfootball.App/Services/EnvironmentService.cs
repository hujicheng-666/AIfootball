using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>环境安装服务 — 首次运行自动配置 Python + PyTorch</summary>
public class EnvironmentService : IEnvironmentService
{
    private const string PythonVersion = "3.10.11";
    private static readonly string[] PythonDownloadUrls =
    {
        // 国内镜像
        $"https://registry.npmmirror.com/-/binary/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
        $"https://mirrors.huaweicloud.com/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
        $"https://cdn.npmmirror.com/binaries/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
        // 官方源
        $"https://www.python.org/ftp/python/{PythonVersion}/python-{PythonVersion}-embed-amd64.zip",
    };
    private const string PipBootstrapUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string PipMirror = "https://pypi.tuna.tsinghua.edu.cn/simple";

    private readonly string _baseDir;
    private readonly string _pythonEnvDir;
    private readonly string _pythonExe;
    private readonly string _profileFile;
    private readonly HttpClient _http;

    public EnvironmentService(string baseDir)
    {
        _baseDir = baseDir;
        _pythonEnvDir = Path.Combine(baseDir, "python_env");
        _pythonExe = Path.Combine(_pythonEnvDir, "python.exe");
        _profileFile = Path.Combine(_pythonEnvDir, ".inference-profile");

        // 全局启用 TLS 1.2+（兼容旧 .NET 运行时）
        System.Net.ServicePointManager.SecurityProtocol =
            SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            // 使用系统默认代理
            UseProxy = true,
        };

        // 兜底：忽略 SSL 证书错误（某些企业环境需要）
        handler.ServerCertificateCustomValidationCallback = (_, cert, chain, errors) =>
        {
            if (errors == System.Net.Security.SslPolicyErrors.None)
                return true;
            // 仅放行证书链问题（自签名/企业内网 CA），拒绝主机名不匹配
            bool allow = errors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;
            Debug.WriteLine($"[SSL] 证书校验: {errors} -> {(allow ? "放行" : "拒绝")}");
            return allow;
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AIfootball/2.0");
    }

    public async Task<bool> IsCompatibleAsync()
    {
        if (!File.Exists(_pythonExe)) return false;

        var gpu = await new GpuDetectionService().DetectAsync();
        string wanted = gpu.CudaAvailable ? "cuda-cu124" : "cpu";

        if (!File.Exists(_profileFile)) return false;
        string installed = await File.ReadAllTextAsync(_profileFile);
        return string.Equals(installed.Trim(), wanted, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<bool> SetupAsync(IProgress<(string Status, int Percent)> progress)
    {
        try
        {
            // Step 0: 连通性诊断
            progress.Report(("[诊断] 检测网络连通性...", 1));
            var diagResult = await DiagnoseConnectivityAsync();
            progress.Report(($"  网络诊断: {diagResult}", 2));
            if (diagResult.Contains("全部失败"))
            {
                progress.Report(("❌ 网络不通! 请检查: 防火墙/代理/VPN 设置", -1));
                progress.Report(("   可尝试: 关闭代理后重试, 或手动安装 Python 到 python_env\\ 目录", -1));
                return false;
            }

            // Step 1: 确保目录存在
            progress.Report(("正在准备环境目录...", 3));
            Directory.CreateDirectory(_pythonEnvDir);

            // Step 2: 下载 Python (~8MB)
            if (!File.Exists(_pythonExe))
            {
                progress.Report(("[1/6] 下载 Python 3.10 运行时 (~8MB)...", 3));
                try
                {
                    await DownloadPythonAsync(progress);
                    progress.Report(("✓ Python 运行时下载解压完成", 12));
                }
                catch (Exception ex)
                {
                    progress.Report(($"❌ Python 下载失败: {GetShortError(ex)}", -1));
                    var detail = ex.InnerException ?? ex;
                    progress.Report(($"   详情: {detail.Message}", -1));
                    progress.Report(($"   尝试的URL: {string.Join(", ", PythonDownloadUrls)}", -1));
                    return false;
                }
            }
            else
            {
                progress.Report(("✓ Python 运行时已存在, 跳过下载", 12));
            }

            // Step 3: 安装 pip
            progress.Report(("[2/6] 安装 pip 包管理器...", 15));
            try
            {
                await InstallPipAsync();
                progress.Report(("✓ pip 安装完成", 20));
            }
            catch (Exception ex)
            {
                progress.Report(($"❌ pip 安装失败: {GetShortError(ex)}", -1));
                progress.Report(($"   可能原因: Python 嵌入版配置问题或网络不通", -1));
                return false;
            }

            // Step 4: 检测 GPU
            progress.Report(("[3/6] 检测 GPU 硬件...", 22));
            GpuInfo gpu;
            try
            {
                gpu = await new GpuDetectionService().DetectAsync();
                progress.Report(($"✓ GPU 检测完成: {(gpu.CudaAvailable ? gpu.GpuName : "CPU 模式")}", 25));
            }
            catch (Exception ex)
            {
                progress.Report(($"⚠ GPU 检测异常: {ex.Message}, 使用 CPU 模式", 25));
                gpu = new GpuInfo(false, false, "", "", false, "");
            }
            bool useCuda = gpu.CudaAvailable;
            string profile = useCuda ? "cuda-cu124" : "cpu";

            // Step 5: 安装依赖
            if (useCuda)
                progress.Report(($"[4/6] 检测到 NVIDIA GPU, 安装 CUDA 推理依赖...", 28));
            else
                progress.Report(("[4/6] 安装 CPU 兼容依赖 (体积更小)...", 28));

            try
            {
                await InstallDependenciesAsync(useCuda, progress);
                progress.Report(("✓ 所有依赖安装完成", 92));
            }
            catch (Exception ex)
            {
                progress.Report(($"❌ 依赖安装失败: {GetShortError(ex)}", -1));
                progress.Report(($"   请确保网络连接正常, 也可手动安装", -1));
                return false;
            }

            // Step 6: 验证
            progress.Report(("[5/6] 验证安装...", 95));
            await VerifyInstallationAsync(progress);

            progress.Report(("[6/6] 写入配置...", 98));
            await File.WriteAllTextAsync(_profileFile, profile);

            progress.Report(($"✅ 推理环境安装完成! ({profile})", 100));
            return true;
        }
        catch (Exception ex)
        {
            progress.Report(($"❌ 未知错误: {GetShortError(ex)}", -1));
            progress.Report(($"   {ex.StackTrace?.Split('\n').FirstOrDefault() ?? ""}", -1));
            return false;
        }
    }

    /// <summary>清理上次失败残留的临时文件</summary>
    private static void CleanupStaleFiles(string dir)
    {
        try
        {
            foreach (var pattern in new[] { "python.zip", "python_*.tmp" })
            {
                foreach (var f in Directory.GetFiles(dir, pattern))
                {
                    try { File.Delete(f); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
    }

    /// <summary>快速诊断网络连通性</summary>
    private async Task<string> DiagnoseConnectivityAsync()
    {
        var testUrls = new Dictionary<string, string>
        {
            ["百度"] = "https://www.baidu.com",
            ["Python.org"] = "https://www.python.org",
            ["清华源"] = "https://pypi.tuna.tsinghua.edu.cn",
        };

        var results = new List<string>();
        foreach (var (name, url) in testUrls)
        {
            try
            {
                using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts2.Token);
                results.Add($"{name}: {(int)response.StatusCode}");
            }
            catch (TaskCanceledException)
            {
                results.Add($"{name}: 超时(8s)");
            }
            catch (HttpRequestException ex)
            {
                results.Add($"{name}: {(ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode}" : "连接失败")}");
            }
            catch (Exception ex)
            {
                results.Add($"{name}: {ex.GetType().Name}");
            }
        }

        var ok = results.Count(r => !r.Contains("超时") && !r.Contains("异常"));
        return ok == 0
            ? "全部失败 — 请检查防火墙/代理"
            : $"{ok}/{results.Count} 通 ({string.Join(", ", results)})";
    }

    private async Task DownloadPythonAsync(IProgress<(string, int)> progress)
    {
        var zipPath = Path.Combine(_pythonEnvDir, "python.zip");
        Directory.CreateDirectory(_pythonEnvDir);

        // 清理上次失败的残留临时文件
        CleanupStaleFiles(_pythonEnvDir);

        var errors = new List<string>();
        for (int i = 0; i < PythonDownloadUrls.Length; i++)
        {
            var url = PythonDownloadUrls[i];
            // 每次用唯一临时文件名，避免文件锁冲突
            var tmpPath = Path.Combine(_pythonEnvDir, $"python_{Guid.NewGuid():N}.tmp");
            try
            {
                progress.Report(($"  尝试 URL {i + 1}/{PythonDownloadUrls.Length}: {new Uri(url).Host}...", 5));

                using var response = await _http.GetAsync(url,
                    HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                // 先下载到临时文件
                await using (var stream = await response.Content.ReadAsStreamAsync())
                await using (var file = File.Create(tmpPath, 8192, FileOptions.None))
                {
                    await stream.CopyToAsync(file);
                    await file.FlushAsync();
                } // using 块确保文件句柄完全释放

                // 删除旧 zip，把临时文件改名
                if (File.Exists(zipPath))
                {
                    File.Delete(zipPath);
                    // 等一小会确保 OS 释放文件句柄
                    await Task.Delay(100);
                }
                File.Move(tmpPath, zipPath);

                var sizeMb = new FileInfo(zipPath).Length / 1024.0 / 1024.0;
                progress.Report(($"  ✓ 下载成功 ({sizeMb:F1}MB)", 8));
                break;
            }
            catch (HttpRequestException ex)
            {
                var status = ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode}" : "无响应";
                errors.Add($"{new Uri(url).Host}: {status}");
                progress.Report(($"  ✗ {new Uri(url).Host}: {status}", 5));
            }
            catch (TaskCanceledException)
            {
                errors.Add($"{new Uri(url).Host}: 连接超时");
                progress.Report(($"  ✗ {new Uri(url).Host}: 连接超时(8s)", 5));
            }
            catch (Exception ex)
            {
                var msg = ex.GetType().Name + ": " + ex.Message.Split('\n')[0];
                if (msg.Length > 150) msg = msg[..150] + "...";
                errors.Add($"{new Uri(url).Host}: {msg}");
                progress.Report(($"  ✗ {new Uri(url).Host}: {msg}", 5));
            }
        }

        if (!File.Exists(zipPath))
        {
            var allErrors = string.Join("\n", errors);
            throw new InvalidOperationException(
                $"所有 {PythonDownloadUrls.Length} 个下载源均失败:\n{allErrors}" +
                $"\n\n可能原因: 防火墙/代理/VPN 阻止了 HTTPS 连接" +
                $"\n手动方案: 浏览器打开 python.org 下载 python-{PythonVersion}-embed-amd64.zip" +
                $"\n         解压到: {_pythonEnvDir}");
        }

        progress.Report(("解压 Python...", 10));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, _pythonEnvDir, true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"解压失败: {GetShortError(ex)}", ex);
        }
        File.Delete(zipPath);

        // 启用 pip + 模块导入 + 引擎路径
        var pthFile = Path.Combine(_pythonEnvDir, "python310._pth");
        if (File.Exists(pthFile))
        {
            var content = await File.ReadAllTextAsync(pthFile);
            content = content.Replace("#import site", "import site");
            if (!content.Contains("Lib\\site-packages"))
                content += "\nLib\\site-packages\n";
            if (!content.Contains(".."))
                content += "..\n";
            // 添加引擎源代码路径，让 Python 能找到 aifootball 模块
            var enginePath = "..\\src\\AIfootball.Engine";
            if (!content.Contains(enginePath))
                content += enginePath + "\n";
            await File.WriteAllTextAsync(pthFile, content);
        }
        else
        {
            // Python 3.10+ 使用 python310._pth，如果不存在则创建
            var pthContent = "python310.zip\n.\n..\nimport site\nLib\\site-packages\n";
            await File.WriteAllTextAsync(pthFile, pthContent);
        }
    }

    private async Task InstallPipAsync()
    {
        var getPipPath = Path.Combine(_pythonEnvDir, "get-pip.py");
        try
        {
            var response = await _http.GetAsync(PipBootstrapUrl);
            response.EnsureSuccessStatusCode();
            await File.WriteAllTextAsync(getPipPath, await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"下载 get-pip.py 失败 ({PipBootstrapUrl}): {GetShortError(ex)}", ex);
        }

        try
        {
            await RunProcessAsync(_pythonExe, $"\"{getPipPath}\" --no-warn-script-location");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"运行 get-pip.py 失败: {GetShortError(ex)}", ex);
        }
        finally
        {
            if (File.Exists(getPipPath)) File.Delete(getPipPath);
        }
    }

    private async Task InstallDependenciesAsync(bool useCuda, IProgress<(string, int)> progress)
    {
        // 升级 pip
        progress.Report(("  • 升级 pip 到最新版...", 30));
        try
        {
            await RunProcessWithOutputAsync(_pythonExe,
                "-m pip install --upgrade pip -i " + PipMirror + " --no-warn-script-location",
                progress, timeoutMs: 120000);
        }
        catch (Exception ex)
        {
            progress.Report(($"  ⚠ pip 升级失败(继续): {GetShortError(ex)}", 30));
        }

        // 核心依赖 —— 实时输出
        var packages = new[] { "numpy", "opencv-python", "scipy", "pillow" };
        int pkgIdx = 0;
        foreach (var pkg in packages)
        {
            pkgIdx++;
            progress.Report(($"  • 安装 {pkg} ({pkgIdx}/{packages.Length})...", 32 + pkgIdx));
            try
            {
                await RunProcessWithOutputAsync(_pythonExe,
                    $"-m pip install {pkg} -i {PipMirror} --no-warn-script-location",
                    progress, timeoutMs: 300000);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"安装 {pkg} 失败: {GetShortError(ex)}", ex);
            }
        }

        // PyTorch
        if (useCuda)
        {
            progress.Report(("  • 安装 PyTorch CUDA 版 (~2GB, 实时输出见下)...", 40));
            bool ok = await TryInstallPytorchAsync("cu124", true, progress);
            if (!ok)
            {
                progress.Report(("  • 阿里云镜像失败, 尝试官方源...", 45));
                try
                {
                    await RunProcessWithOutputAsync(_pythonExe,
                        $"-m pip install torch torchvision torchaudio " +
                        $"--index-url https://download.pytorch.org/whl/cu124 " +
                        $"--no-warn-script-location",
                        progress, timeoutMs: 900000);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"PyTorch CUDA 安装失败: {GetShortError(ex)}", ex);
                }
            }
        }
        else
        {
            progress.Report(("  • 安装 PyTorch CPU 版 (~200MB, 实时输出见下)...", 40));
            bool ok = await TryInstallPytorchAsync("cpu", false, progress);
            if (!ok)
            {
                progress.Report(("  • 镜像失败, 尝试官方源...", 45));
                try
                {
                    await RunProcessWithOutputAsync(_pythonExe,
                        $"-m pip install torch torchvision torchaudio " +
                        $"--index-url https://download.pytorch.org/whl/cpu " +
                        $"--no-warn-script-location",
                        progress, timeoutMs: 900000);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"PyTorch CPU 安装失败: {GetShortError(ex)}", ex);
                }
            }
        }

        // Ultralytics — 实时显示 pip 下载进度
        progress.Report(("  • 安装 ultralytics (YOLO, 实时输出见下)...", 70));
        try
        {
            if (!await TryInstallUltralyticsAsync(PipMirror, progress))
            {
                progress.Report(("  • 清华源失败, 尝试阿里云镜像...", 75));
                if (!await TryInstallUltralyticsAsync("https://mirrors.aliyun.com/pypi/simple/", progress))
                {
                    throw new InvalidOperationException("所有镜像源均超时");
                }
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"ultralytics 安装失败: {GetShortError(ex)}", ex);
        }

        progress.Report(("  ✓ 依赖安装完成", 90));
    }

    /// <summary>尝试安装 ultralytics（实时显示 pip 输出）</summary>
    private async Task<bool> TryInstallUltralyticsAsync(string mirror, IProgress<(string, int)> progress)
    {
        try
        {
            await RunProcessWithOutputAsync(_pythonExe,
                $"-m pip install ultralytics -i {mirror} --no-warn-script-location",
                progress, timeoutMs: 900000); // 15分钟，实时输出
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>带实时输出的进程执行</summary>
    private Task RunProcessWithOutputAsync(string fileName, string arguments,
        IProgress<(string, int)> progress, int timeoutMs = 300000)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _baseDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"无法启动: {fileName}");

            var outputLock = new object();

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var line = e.Data.Trim();
                    if (line.Length > 120) line = line[..120] + "...";
                    lock (outputLock)
                    {
                        App.Current?.Dispatcher?.Invoke(() =>
                            progress.Report(($"    pip: {line}", -2))); // -2 = 不更新进度条
                    }
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    var line = e.Data.Trim();
                    if (line.Contains("WARNING") || line.Contains("ERROR"))
                    {
                        if (line.Length > 120) line = line[..120] + "...";
                        lock (outputLock)
                        {
                            App.Current?.Dispatcher?.Invoke(() =>
                                progress.Report(($"    ⚠ {line}", -2)));
                        }
                    }
                }
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(timeoutMs))
            {
                proc.Kill(entireProcessTree: true);
                throw new TimeoutException($"进程超时 ({timeoutMs / 60000}分钟): pip install");
            }

            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException($"pip 退出码 {proc.ExitCode}: {err.Trim()[..Math.Min(err.Length, 200)]}");
            }
        });
    }

    /// <summary>尝试使用阿里云镜像安装 PyTorch</summary>
    private async Task<bool> TryInstallPytorchAsync(string cudaSuffix, bool isCuda,
        IProgress<(string, int)> progress)
    {
        try
        {
            var indexUrl = isCuda
                ? $"https://mirrors.aliyun.com/pytorch-wheels/cu124"
                : $"https://mirrors.aliyun.com/pytorch-wheels/cpu";
            await RunProcessWithOutputAsync(_pythonExe,
                $"-m pip install torch torchvision torchaudio " +
                $"--index-url {indexUrl} " +
                $"--no-warn-script-location",
                progress, timeoutMs: 900000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>验证安装是否可用</summary>
    private async Task VerifyInstallationAsync(IProgress<(string, int)> progress)
    {
        try
        {
            var (exitCode, output, error) = await RunWithOutputAsync(
                _pythonExe,
                "-c \"import torch; print('torch', torch.__version__); " +
                "import ultralytics; print('yolo OK'); " +
                "import cv2; print('cv2 OK')\"");

            if (exitCode != 0)
                throw new InvalidOperationException($"验证失败 (exit={exitCode}): {error}");

            progress.Report(($"✓ 验证通过: {output.Trim().Replace("\n", ", ")}", 96));
        }
        catch (Exception ex)
        {
            progress.Report(($"⚠ 验证警告: {GetShortError(ex)}", 96));
            // 验证失败不阻塞整个流程
        }
    }

    /// <summary>提取简洁的错误信息</summary>
    private static string GetShortError(Exception ex)
    {
        var msg = ex.Message;
        // 截断过长的错误信息
        if (msg.Length > 200) msg = msg[..200] + "...";
        // 如果是 HttpRequestException，提取状态码
        if (ex is HttpRequestException hre && hre.StatusCode.HasValue)
            msg = $"HTTP {(int)hre.StatusCode} ({hre.StatusCode}): {msg}";
        return msg;
    }

    /// <summary>带输出捕获的进程执行</summary>
    private async Task<(int ExitCode, string Output, string Error)> RunWithOutputAsync(
        string fileName, string arguments, int timeoutMs = 30000)
    {
        return await Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _baseDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"无法启动: {fileName}");
            if (!proc.WaitForExit(timeoutMs))
            {
                proc.Kill(entireProcessTree: true);
                return (-1, "", "超时");
            }
            return (proc.ExitCode, proc.StandardOutput.ReadToEnd(), proc.StandardError.ReadToEnd());
        });
    }
    private Task RunProcessAsync(string fileName, string arguments, int timeoutMs = 300000)
    {
        return Task.Run(() =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = _baseDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
            if (!proc.WaitForExit(timeoutMs))
            {
                proc.Kill(entireProcessTree: true);
                throw new TimeoutException($"进程超时: {fileName} {arguments}");
            }
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"进程退出码 {proc.ExitCode}: {err.Trim()}");
            }
        });
    }
}
