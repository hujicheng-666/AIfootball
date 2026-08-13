using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using AIfootball.App.Services;
using AIfootball.App.Services.Interfaces;
using AIfootball.App.Models;
using AIfootball.App.ViewModels;

namespace AIfootball.App.Views.Pages;

public partial class ResultsPage : UserControl
{
    private MainViewModel? _vm;
    private Process? _unityProcess;
    private CancellationTokenSource? _attachCancellation;
    private Window? _ownerWindow;

    public ResultsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_unityProcess is { HasExited: false }) return;
        _vm = DataContext as MainViewModel;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow != null)
            _ownerWindow.Closing += OwnerWindow_Closing;
        await StartUnityAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_ownerWindow != null)
            _ownerWindow.Closing -= OwnerWindow_Closing;
        _ownerWindow = null;
        CloseEmbeddedUnity();
    }

    private void OwnerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => CloseEmbeddedUnity();

    private async Task StartUnityAsync()
    {
        StartupOverlay.Visibility = Visibility.Visible;
        OverlayText.Text = "正在加载 Unity…";

        // A bare HwndHost is created during layout, which can be later than the
        // page Loaded event. Finish that layout before starting the child process.
        await Dispatcher.InvokeAsync(() => UnityHost.UpdateLayout(), DispatcherPriority.ContextIdle);
        _attachCancellation = new CancellationTokenSource();
        if (!await UnityHost.WaitForHostAsync(_attachCancellation.Token))
        {
            OverlayText.Text = UnityHost.LastAttachError ?? "WPF Unity host 未创建";
            return;
        }

        var engine = App.Services.GetService(typeof(IPythonEngine)) as IPythonEngine;

        // 清空上一次会话遗留的命令文件，避免 Unity 启动时执行旧 replay 命令导致自动播放
        try
        {
            await UnityCommandClient.SendAsync(engine?.WorkspaceDir ?? ".", "");
        }
        catch { }

        var dataDirectory = Path.Combine(engine?.WorkspaceDir ?? ".", "data");
        var sample = Directory.Exists(dataDirectory)
            ? Directory.GetFiles(dataDirectory, "*_trajectory.csv")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Select(file => Path.GetFileNameWithoutExtension(file)?.Replace("_trajectory", "", StringComparison.Ordinal))
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            : null;

        if (string.IsNullOrWhiteSpace(sample))
        {
            OverlayText.Text = "暂无可加载的轨迹数据";
            return;
        }

        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var process = pipeline?.LaunchUnityViewer([sample], embedded: true,
            hostWindowHandle: UnityHost.HostHandle);
        if (process is null)
        {
            OverlayText.Text = "Unity 启动失败";
            return;
        }

        _unityProcess = process;
        try
        {
            if (await UnityHost.AttachAsync(process, _attachCancellation.Token))
            {
                StartupOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            var detail = UnityHost.LastAttachError ?? "Unity 窗口无法嵌入";
            OverlayText.Text = detail;
            _vm?.AddLog("error", detail);
            CloseEmbeddedUnity();
        }
        catch (OperationCanceledException)
        {
            // The page was closed while Unity was starting.
        }
        catch (Exception ex)
        {
            OverlayText.Text = "Unity 内嵌失败";
            _vm?.AddLog("error", $"Unity 窗口内嵌失败: {ex.Message}");
            CloseEmbeddedUnity();
        }
    }

    private void CloseEmbeddedUnity()
    {
        _attachCancellation?.Cancel();
        _attachCancellation?.Dispose();
        _attachCancellation = null;
        if (_unityProcess is { HasExited: false })
        {
            try
            {
                _unityProcess.Kill(entireProcessTree: true);
                _unityProcess.WaitForExit(2000);
            }
            catch (InvalidOperationException) { }
        }
        UnityHost.Detach();
        _unityProcess?.Dispose();
        _unityProcess = null;
    }

    private async Task SendUnityCommandAsync(string command)
    {
        if (_unityProcess is not { HasExited: false }) return;
        try
        {
            var engine = App.Services.GetService(typeof(IPythonEngine)) as IPythonEngine;
            await UnityCommandClient.SendAsync(engine?.WorkspaceDir ?? ".", command);
            CommandStatusText.Text = "已发送: " + command.Split(':')[0];
        }
        catch (Exception ex) { _vm?.AddLog("error", $"Unity 控制命令失败: {ex.Message}"); }
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "轨迹 CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() == true)
            await SendUnityCommandAsync($"csv:{dialog.FileName}");
    }
    private async void Replay_Click(object sender, RoutedEventArgs e) => await SendUnityCommandAsync("replay");
    private async void Reset_Click(object sender, RoutedEventArgs e) => await SendUnityCommandAsync("reset");
    private async void View_Click(object sender, RoutedEventArgs e) => await SendUnityCommandAsync("view");
    private sealed record GoalkeeperChoice(string Key, string Label);

    private void SelectGoalkeeper_Click(object sender, RoutedEventArgs e)
    {
        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var gkList = pipeline?.ScanGoalkeepers() ?? new List<GoalkeeperInfo>();
        var choices = gkList.Select(gk =>
        {
            var stats = pipeline?.LoadGoalkeeperStats(gk.Name);
            string label = string.IsNullOrWhiteSpace(stats?.DisplayName) ? gk.Name : stats.DisplayName;
            return new GoalkeeperChoice(gk.Name, label);
        }).ToList();
        GoalkeeperList.ItemsSource = choices;
        GoalkeeperPopup.IsOpen = true;
    }

    private void GoalkeeperItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GoalkeeperChoice choice })
            ShowGoalkeeperPreview(choice.Key);
    }

    private async void GoalkeeperItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GoalkeeperChoice choice })
        {
            GoalkeeperPopup.IsOpen = false;
            await SendUnityCommandAsync($"goalkeeper:{choice.Key}");
        }
    }

    private void ShowGoalkeeperPreview(string key)
    {
        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var stats = pipeline?.LoadGoalkeeperStats(key);
        if (stats is null)
        {
            PreviewName.Text = key;
            PreviewDesc.Text = "";
            PreviewAttrs.Text = "无法加载该门将的属性";
            PreviewRadar.Values = null;
            return;
        }

        PreviewName.Text = stats.DisplayName;
        PreviewDesc.Text = stats.Description;
        PreviewAttrs.Text =
            $"速度: {stats.DiveSpeed:0.##}\n" +
            $"臂展: {stats.Reach:0.##}\n" +
            $"弹跳: {stats.JumpHeight:0.##}\n" +
            $"反应: {stats.GoalKeeping:0.##}\n" +
            $"身高: {stats.Height:0.##}\n" +
            $"站位距离: {stats.TendGoalDistance:0.##}\n" +
            $"站位速度: {stats.TendGoalSpeed:0.##}";
        PreviewRadar.Values = new[]
        {
            Normalize(stats.DiveSpeed, 2f, 6f),
            Normalize(stats.Reach, 0.3f, 0.7f),
            Normalize(stats.JumpHeight, 0.3f, 0.8f),
            Normalize(stats.GoalKeeping, 0.5f, 0.95f),
            Normalize(stats.Height, 1.7f, 2.1f),
        };
    }

    static double Normalize(float value, float min, float max) => Math.Clamp((value - min) / (max - min), 0f, 1f);
    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string speed })
            await SendUnityCommandAsync($"speed:{speed}");
    }

}
