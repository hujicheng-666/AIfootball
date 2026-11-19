using System.Diagnostics;
using System.Globalization;
using AIfootball.Core.Workspace;

namespace AIfootball.Core.Services;

/// <summary>Unity 查看器的运行模式。</summary>
public enum ViewerMode
{
    /// <summary>独立窗口（默认，最稳定）。</summary>
    Standalone,
    /// <summary>嵌入到 WPF 窗口内的子窗口。</summary>
    Embedded,
}

public sealed record ViewerLaunchOptions(
    ViewerMode Mode,
    string SampleName,
    string? GoalkeeperName,
    nint HostWindowHandle = 0);

/// <summary>
/// Unity 回放查看器的宿主。
///
/// 与 Unity 侧的契约（详见 Assets/SuperGoalie/Scripts/Platform/WpfCommandBridge.cs）：
/// 启动参数 <c>--csv</c> / <c>--goalkeeper</c> / <c>--wpf-host</c> / <c>--wpf-command</c> / <c>-parentHWND</c>；
/// 指令通过命令文件以"序号 + 换行 + 指令"写入，因此连续两条相同指令也会被当成两次动作。
/// </summary>
public sealed class UnityViewerService(AifootballWorkspace workspace) : IDisposable
{
    private readonly AifootballWorkspace _workspace = workspace;
    private readonly object _gate = new();
    private Process? _process;
    private long _sequence;

    public Process? Process { get { lock (_gate) return _process; } }

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                if (_process is null) return false;
                try { return !_process.HasExited; }
                catch (InvalidOperationException) { return false; }
            }
        }
    }

    public string? PlayerExecutable { get; private set; }

    /// <summary>交互式 Unity 编辑器进程与播放器同名风险很低，这里按"_Data 目录同名"识别构建产物。</summary>
    public string? FindPlayerExecutable()
    {
        foreach (var directory in new[]
                 {
                     _workspace.RuntimeDir,
                     _workspace.Combine("viewer"),
                     _workspace.Combine("AIfootball-Windows"),
                 })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (var executable in Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(executable);
                if (name.Equals("AIfootball", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(Path.Combine(directory, name + "_Data")))
                {
                    PlayerExecutable = executable;
                    return executable;
                }
            }
        }
        PlayerExecutable = null;
        return null;
    }

    public bool TryLaunch(ViewerLaunchOptions options, out string message)
    {
        message = "";
        var executable = FindPlayerExecutable();
        if (executable is null)
        {
            message = "未找到 Unity 回放查看器（runtime/*.exe）";
            return false;
        }

        var csvPath = _workspace.UnityCsv(options.SampleName);
        if (!File.Exists(csvPath))
        {
            message = $"轨迹文件不存在：{csvPath}";
            return false;
        }

        Kill();

        var playerDirectory = Path.GetDirectoryName(executable)!;
        SyncTrajectoryToPlayer(playerDirectory, options.SampleName, csvPath);

        var arguments = new List<string>();
        if (options.Mode == ViewerMode.Embedded)
        {
            // 让 Unity 自己创建子窗口，保留其原生输入路由与动态分辨率
            arguments.AddRange(["-popupwindow", "-screen-fullscreen", "0", "--wpf-host"]);
            arguments.AddRange(["--wpf-command", Quote(_workspace.UnityCommandFile)]);
            if (options.HostWindowHandle != 0)
                arguments.AddRange(["-parentHWND", options.HostWindowHandle.ToString(CultureInfo.InvariantCulture), "delayed"]);
        }
        arguments.AddRange(["--csv", Quote(csvPath)]);
        if (!string.IsNullOrWhiteSpace(options.GoalkeeperName))
        {
            var goalkeeper = Path.Combine(_workspace.GoalkeepersDir, $"{options.GoalkeeperName}.json");
            if (File.Exists(goalkeeper)) arguments.AddRange(["--goalkeeper", Quote(goalkeeper)]);
        }

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = playerDirectory,
                Arguments = string.Join(' ', arguments),
                UseShellExecute = options.Mode == ViewerMode.Standalone,
                CreateNoWindow = options.Mode == ViewerMode.Embedded,
            });
            if (process is null)
            {
                message = "查看器进程启动失败";
                return false;
            }
            lock (_gate) _process = process;
            message = "查看器已启动";
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            message = $"查看器启动异常：{ex.Message}";
            return false;
        }
    }

    /// <summary>把轨迹同步到播放器目录，Unity 的自动检测逻辑会优先读这里。</summary>
    private void SyncTrajectoryToPlayer(string playerDirectory, string sampleName, string csvPath)
    {
        try
        {
            var target = Path.Combine(playerDirectory, "data");
            Directory.CreateDirectory(target);
            File.Copy(csvPath, Path.Combine(target, $"{sampleName}_trajectory.csv"), overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string Quote(string value) => $"\"{value}\"";

    // ─── 指令通道 ───

    public void LoadTrajectory(string sampleName) => Send($"csv:{_workspace.UnityCsv(sampleName)}");
    public void Replay() => Send("replay");
    public void Reset() => Send("reset");
    public void CycleView() => Send("view");
    public void PreviousGoalkeeper() => Send("goalkeeper:previous");
    public void NextGoalkeeper() => Send("goalkeeper:next");
    public void SwitchGoalkeeper(string name) => Send($"goalkeeper:{name}");
    public void SetSpeed(double speed) => Send($"speed:{speed.ToString("0.###", CultureInfo.InvariantCulture)}");

    /// <summary>写入一条指令。文件不可写时静默忽略（查看器可能已经退出）。</summary>
    public void Send(string command)
    {
        try
        {
            var path = _workspace.UnityCommandFile;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var sequence = Interlocked.Increment(ref _sequence);
            File.WriteAllText(path, $"{sequence}\n{command}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Kill()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }
        if (process is null) return;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.Dispose();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose() => Kill();
}
