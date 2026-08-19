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
        base.OnExit(e);
        (Services as IDisposable)?.Dispose();
    }
}
