using System.Diagnostics;
using System.IO;
using System.Windows;
using AIfootball.App.Services;
using AIfootball.App.Services.Interfaces;
using AIfootball.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace AIfootball.App;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 任何 UI 线程异常以前都是静默退出（进程直接没了、日志一个字都不留），
        // 排查只能靠猜。这里兜住，至少把完整堆栈写进 logs/。
        DispatcherUnhandledException += (_, args) =>
        {
            LogException("未处理的 UI 异常", args.Exception);
            MessageBox.Show(
                $"界面发生未处理异常，已写入日志：\n\n{args.Exception.Message}\n\n" +
                $"位置：{args.Exception.TargetSite?.DeclaringType?.Name}.{args.Exception.TargetSite?.Name}",
                "AIfootball", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;   // 不让整个进程陪葬
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogException("未处理的非 UI 异常", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        // 确定应用根目录
        string baseDir = ResolveBaseDirectory();
        AppLogger.Initialize(baseDir);

        // 构建 DI 容器
        var services = new ServiceCollection();

        // 基础设施
        services.AddSingleton<IPythonEngine>(_ =>
            new PythonEngineService(baseDir));
        services.AddSingleton<IGpuDetectionService, GpuDetectionService>();
        services.AddSingleton<IEnvironmentService>(_ =>
            new EnvironmentService(baseDir));
        services.AddSingleton<IPipelineService, PipelineService>();
        services.AddSingleton<IShooterProfileService, ShooterProfileService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<PipelineViewModel>();

        // 窗口
        services.AddSingleton<MainWindow>();

        Services = services.BuildServiceProvider();

        // 启动主窗口
        var mainWindow = Services.GetRequiredService<MainWindow>();
        var startupWindow = new StartupWindow
        {
            Width = mainWindow.Width,
            Height = mainWindow.Height,
            MinWidth = mainWindow.MinWidth,
            MinHeight = mainWindow.MinHeight,
            WindowStartupLocation = mainWindow.WindowStartupLocation
        };
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        startupWindow.StartupCompleted += (_, _) =>
        {
            MainWindow = mainWindow;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            mainWindow.Show();
            startupWindow.Close();
        };
        startupWindow.Show();
    }

    private static void LogException(string context, Exception ex)
    {
        try
        {
            AppLogger.Write("ERROR", $"{context}：{ex.GetType().Name}: {ex.Message}\n{ex}");
        }
        catch
        {
            // 日志本身失败时不能再抛，否则就是递归崩溃
        }
    }

    private static string ResolveBaseDirectory()
    {
        // 优先级: 命令行 > 当前目录 > 向上搜索项目根
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--workspace" && i + 1 < args.Length)
                return Path.GetFullPath(args[i + 1]);
        }

        // 检查当前目录
        var cwd = Directory.GetCurrentDirectory();
        if (HasProjectFiles(cwd)) return cwd;

        // 从 exe 目录向上搜索，直到找到 calib/ 或 python_env/
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        var dir = exeDir;
        while (dir != null)
        {
            if (HasProjectFiles(dir)) return dir;
            // 也检查子目录 AIfootball-Windows
            var legacyDir = Path.Combine(dir, "AIfootball-Windows");
            if (Directory.Exists(legacyDir) && HasProjectFiles(legacyDir))
                return legacyDir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break; // 到达根目录
            dir = parent;
        }

        return exeDir;
    }

    private static bool HasProjectFiles(string dir) =>
        Directory.Exists(Path.Combine(dir, "calib")) ||
        Directory.Exists(Path.Combine(dir, "python_env"));

    protected override void OnExit(ExitEventArgs e)
    {
        // 关掉界面时必须同时结束正在采集的 Python 子进程：否则它会变成孤儿，
        // 继续占着 UDP 端口抢收数据包，下一次预览既绑不上端口、画面也是残缺的。
        if (Services?.GetService(typeof(IPythonEngine)) is IPythonEngine engine)
            engine.TerminateAll();
        base.OnExit(e);
        (Services as IDisposable)?.Dispose();
    }
}
