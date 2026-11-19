using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using AIfootball.App.Services;
using AIfootball.App.Services.Interfaces;
using AIfootball.App.Models;
using AIfootball.App.ViewModels;
using AIfootball.App.Views.Dialogs;
using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.App.Views.Pages;

public partial class ResultsPage : UserControl
{
    private MainViewModel? _vm;
    private PipelineViewModel? _pipelineVm;
    private string? _deliveredGoalkeeperName;
    private bool _hasExplicitGoalkeeperSelection;
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
        _vm = DataContext as MainViewModel;
        AttachPipelineViewModel();
        UpdateSelectedGoalkeeperLabel();

        if (_unityProcess is { HasExited: false }) return;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow != null)
            _ownerWindow.Closing += OwnerWindow_Closing;
        await StartUnityAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_pipelineVm is not null)
            _pipelineVm.PropertyChanged -= OnPipelineViewModelPropertyChanged;
        _pipelineVm = null;
        if (_ownerWindow != null)
            _ownerWindow.Closing -= OwnerWindow_Closing;
        _ownerWindow = null;
        CloseEmbeddedUnity();
    }

    private void OwnerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) => CloseEmbeddedUnity();

    private async Task StartUnityAsync()
    {
        StartupOverlay.Visibility = Visibility.Visible;
        OverlayText.Text = "正在加载 Unity 渲染端…";

        // A bare HwndHost is created during layout, which can be later than the
        // page Loaded event. Finish that layout before starting the child process.
        await Dispatcher.InvokeAsync(() => UnityHost.UpdateLayout(), DispatcherPriority.ContextIdle);
        _attachCancellation = new CancellationTokenSource();
        if (!await UnityHost.WaitForHostAsync(_attachCancellation.Token))
        {
            OverlayText.Text = UnityHost.LastAttachError ?? "Unity 宿主未创建";
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
        // Selecting a trajectory starts a new replay context.  A goalkeeper must
        // be selected explicitly for that context before the replay command is sent.
        _hasExplicitGoalkeeperSelection = false;
        SetDeliveredGoalkeeper(null);

        var process = pipeline?.LaunchUnityViewer([sample], goalkeeperName: null, embedded: true,
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
            OverlayText.Text = "Unity 渲染端嵌入失败";
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
            CommandStatusText.Text = "指令已发送：" + command.Split(':')[0];
        }
        catch (Exception ex) { _vm?.AddLog("error", $"Unity 控制命令失败: {ex.Message}"); }
    }

    private async void ImportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "轨迹 CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() == true)
        {
            _hasExplicitGoalkeeperSelection = false;
            SetDeliveredGoalkeeper(null);
            await SendUnityCommandAsync($"csv:{dialog.FileName}");
        }
    }



    private async void Replay_Click(object sender, RoutedEventArgs e)
    {
        CloseFloatingPanels();
        if (!_hasExplicitGoalkeeperSelection)
        {
            CommandStatusText.Text = "请先选择门将模型，再播放轨迹";
            return;
        }

        await SendUnityCommandAsync("replay");
    }
    private async void Reset_Click(object sender, RoutedEventArgs e) => await SendUnityCommandAsync("reset");
    private async void View_Click(object sender, RoutedEventArgs e) => await SendUnityCommandAsync("view");
    private sealed record GoalkeeperChoice(string Key, string Label);

    private void SelectGoalkeeper_Click(object sender, RoutedEventArgs e)
    {
        // 再点一次按钮应当收起面板；否则 StaysOpen=False 先关掉、这里又立刻打开，
        // 表现为"按钮怎么点都关不掉"。
        if (GoalkeeperPopup.IsOpen)
        {
            GoalkeeperPopup.IsOpen = false;
            return;
        }

        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var gkList = pipeline?.ScanGoalkeepers() ?? new List<GoalkeeperInfo>();
        var choices = gkList.Select(gk =>
        {
            var stats = pipeline?.LoadGoalkeeperStats(gk.Name);
            string label = string.IsNullOrWhiteSpace(stats?.DisplayName) ? gk.Name : stats.DisplayName;
            return new GoalkeeperChoice(gk.Name, label);
        }).ToList();
        GoalkeeperList.ItemsSource = choices;

        // 打开即预填一份预览，避免热图/雷达空白直到鼠标划过
        var initial = choices.FirstOrDefault(choice => choice.Key == _deliveredGoalkeeperName)
            ?? choices.FirstOrDefault();
        if (initial is not null)
        {
            GoalkeeperList.SelectedItem = initial;
            ShowGoalkeeperPreview(initial.Key);
        }

        GoalkeeperPopup.IsOpen = true;
    }

    private void GoalkeeperItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GoalkeeperChoice choice })
            ShowGoalkeeperPreview(choice.Key);
    }

    private void CloseGoalkeeperPopup_Click(object sender, RoutedEventArgs e) => GoalkeeperPopup.IsOpen = false;

    /// <summary>收起本页的浮层。训练与画像都已迁走，这里只剩门将选择面板。</summary>
    private void CloseFloatingPanels() => GoalkeeperPopup.IsOpen = false;

    /// <summary>Esc 收掉浮层。训练已迁到独立页，这里只管本页的弹层。</summary>
    private void ResultsPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (!GoalkeeperPopup.IsOpen) return;
        CloseFloatingPanels();
        e.Handled = true;
    }

    /// <summary>训练已独立成「门将」页；这里只把当前门将名交出去，由 MainWindow 负责导航。</summary>
    public event EventHandler<string?>? TrainerRequested;

    private void OpenTrainerPage_Click(object sender, RoutedEventArgs e)
    {
        GoalkeeperPopup.IsOpen = false;
        TrainerRequested?.Invoke(this, _deliveredGoalkeeperName);
    }

    private async void GoalkeeperItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: GoalkeeperChoice choice })
        {
            GoalkeeperPopup.IsOpen = false;
            if (_pipelineVm is not null)
                _pipelineVm.SelectedGoalkeeper = _pipelineVm.Goalkeepers
                    .FirstOrDefault(goalkeeper => goalkeeper.Name == choice.Key);
            _hasExplicitGoalkeeperSelection = true;
            SetDeliveredGoalkeeper(choice.Key);
            await SendUnityCommandAsync($"goalkeeper:{choice.Key}");
        }
    }

    private void AttachPipelineViewModel()
    {
        var pipelineVm = App.Services.GetService(typeof(PipelineViewModel)) as PipelineViewModel;
        if (ReferenceEquals(_pipelineVm, pipelineVm))
            return;

        if (_pipelineVm is not null)
            _pipelineVm.PropertyChanged -= OnPipelineViewModelPropertyChanged;

        _pipelineVm = pipelineVm;
        if (_pipelineVm is not null)
            _pipelineVm.PropertyChanged += OnPipelineViewModelPropertyChanged;
    }

    private void OnPipelineViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineViewModel.SelectedGoalkeeper))
        {
            Dispatcher.InvokeAsync(UpdateSelectedGoalkeeperLabel);
        }
    }

    private void UpdateSelectedGoalkeeperLabel()
    {
        var goalkeeperName = _deliveredGoalkeeperName;
        if (string.IsNullOrWhiteSpace(goalkeeperName))
        {
            KeeperNameText.Text = "未选择";
            return;
        }

        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var stats = pipeline?.LoadGoalkeeperStats(goalkeeperName);
        var label = string.IsNullOrWhiteSpace(stats?.DisplayName) ? goalkeeperName : stats.DisplayName;
        KeeperNameText.Text = label;
    }

    private void SetDeliveredGoalkeeper(string? goalkeeperName)
    {
        _deliveredGoalkeeperName = goalkeeperName;
        if (!string.IsNullOrWhiteSpace(goalkeeperName) && _pipelineVm is not null)
        {
            var goalkeeper = _pipelineVm.Goalkeepers
                .FirstOrDefault(item => item.Name == goalkeeperName);
            if (goalkeeper is not null)
                _pipelineVm.SelectedGoalkeeper = goalkeeper;
        }

        UpdateSelectedGoalkeeperLabel();
    }

    private string? _previewGoalkeeperKey;

    private void ShowGoalkeeperPreview(string key)
    {
        _previewGoalkeeperKey = key;
        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var stats = pipeline?.LoadGoalkeeperStats(key);
        if (stats is null)
        {
            PreviewName.Text = key;
            PreviewDesc.Text = "";
            PreviewAttrs.Text = "无法加载该门将的属性";
            PreviewRadar.Values = null;
            PreviewHeatmap.Values = null;
            PreviewHeatmapTitle.Text = "区域弱点";
            PreviewHeatmapLegend.Text = "无法加载该门将的属性";
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

        // 热力图表达"弱点"：扑救概率越低越红，和图上红色=危险的直觉一致。
        // 数值本身仍是 Unity 播放器读的那份 ProbabilityMap，界面没做二次加工。
        if (stats.HasProbabilityMap)
        {
            var grid = stats.ToZoneGrid();
            PreviewHeatmap.Values = grid.Select(probability => 1 - probability).ToArray();
            var rates = stats.ProbabilityMap.Values;
            PreviewHeatmapTitle.Text = "区域弱点";
            PreviewHeatmapLegend.Text =
                $"冷=门将稳　热=容易失球\n扑救率 {rates.Min():0.00}–{rates.Max():0.00}";
        }
        else
        {
            PreviewHeatmap.Values = null;
            PreviewHeatmapTitle.Text = "区域弱点";
            PreviewHeatmapLegend.Text = "该门将没有 ProbabilityMap";
        }
    }

    static double Normalize(float value, float min, float max) => Math.Clamp((value - min) / (max - min), 0f, 1f);

    private void EditGoalkeeper_Click(object sender, RoutedEventArgs e) => OpenGoalkeeperEditor(saveAs: false);

    private void SaveGoalkeeperAs_Click(object sender, RoutedEventArgs e) => OpenGoalkeeperEditor(saveAs: true);

    /// <summary>
    /// 打开属性编辑器。写回的是 data/goalkeepers 里的同一份 JSON，
    /// 因此覆盖当前正在回放的门将时，要让 Unity 重新读一次。
    /// </summary>
    private async void OpenGoalkeeperEditor(bool saveAs)
    {
        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        if (pipeline is null || string.IsNullOrWhiteSpace(_previewGoalkeeperKey))
        {
            _vm?.AddLog("warn", "没有选中的门将，无法编辑");
            CommandStatusText.Text = "请先在列表中选择门将模型";
            return;
        }

        var sourceName = _previewGoalkeeperKey;
        var stats = pipeline.LoadGoalkeeperStats(sourceName);
        if (stats is null)
        {
            _vm?.AddLog("error", $"无法读取门将 {sourceName} 的属性数据");
            return;
        }

        var editor = new GoalkeeperEditorWindow(sourceName, stats)
        {
            Owner = _ownerWindow ?? Window.GetWindow(this),
        };
        // 模态对话框会禁用主窗口，Popup 却不受影响、会浮在对话框上面且点不掉
        CloseFloatingPanels();

        if (saveAs)
        {
            var existing = pipeline.ScanGoalkeepers()
                .Select(goalkeeper => goalkeeper.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var suggestion = $"{sourceName}_copy";
            var suffix = 2;
            while (existing.Contains(suggestion))
                suggestion = $"{sourceName}_copy{suffix++}";
            editor.SuggestName(suggestion);
        }

        bool accepted;
        try
        {
            accepted = editor.ShowDialog() == true;
        }
        catch (Exception ex)
        {
            _vm?.AddLog("error", $"门将编辑器打开失败：{ex.Message}");
            CommandStatusText.Text = "门将编辑器打开失败，详情见日志";
            return;
        }

        GoalkeeperPopup.IsOpen = true;
        if (!accepted || !editor.Saved)
            return;

        var name = editor.ResultName;
        if (!pipeline.SaveGoalkeeperStats(name, editor.ResultStats, out var message))
        {
            CommandStatusText.Text = message;
            _vm?.AddLog("error", message);
            return;
        }

        _vm?.AddLog("info", $"门将已保存：{name}（{message}）");
        CommandStatusText.Text = $"门将模型已保存：{name}";

        // 列表与预览都按新文件重来，热图显示的就是刚存下的概率
        ReloadGoalkeeperChoices();
        _previewGoalkeeperKey = name;
        ShowGoalkeeperPreview(name);

        if (string.Equals(name, _deliveredGoalkeeperName, StringComparison.Ordinal))
            await SendUnityCommandAsync($"goalkeeper:{name}");
    }

    private void ReloadGoalkeeperChoices()
    {
        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var choices = (pipeline?.ScanGoalkeepers() ?? new List<GoalkeeperInfo>())
            .Select(goalkeeper =>
            {
                var stats = pipeline?.LoadGoalkeeperStats(goalkeeper.Name);
                var label = string.IsNullOrWhiteSpace(stats?.DisplayName)
                    ? goalkeeper.Name
                    : stats.DisplayName;
                return new GoalkeeperChoice(goalkeeper.Name, label);
            })
            .ToList();

        GoalkeeperList.ItemsSource = choices;
        GoalkeeperList.SelectedItem = choices.FirstOrDefault(choice => choice.Key == _previewGoalkeeperKey);
    }
    private async void Speed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string speed } toggle) return;
        // 三个档位互斥。ToggleButton 点击会自行翻转，这里统一按点击目标重算，
        // 顺带把"再点已选中的档位"变成保持选中，而不是全部熄灭。
        foreach (var item in new[] { SpeedSlow, SpeedNormal, SpeedFast })
            item.IsChecked = ReferenceEquals(item, toggle);
        await SendUnityCommandAsync($"speed:{speed}");
    }


}
