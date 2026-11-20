using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;
using AIfootball.App.ViewModels;
using Microsoft.Win32;

namespace AIfootball.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _mainVm;
    private readonly PipelineViewModel _pipelineVm;

    public MainWindow(MainViewModel mainVm, PipelineViewModel pipelineVm)
    {
        InitializeComponent();
        _mainVm = mainVm;
        _pipelineVm = pipelineVm;
        _mainVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsEnvironmentReady)
                or nameof(MainViewModel.IsCheckingEnvironment)
                or nameof(MainViewModel.IsSettingUp)
                or nameof(MainViewModel.StatusBarText)
                or nameof(MainViewModel.GpuInfo))
                UpdateStatusIndicators();
        };

        // 设置页面 DataContext
        // 概览 / 处理队列 / 相机标定三页已由 WebView2 承载，
        // 对应的 WPF 页面仅作回退保留，不再绑定数据。
        ResultsPage.DataContext = _mainVm;

        // WebView2 侧：状态从 ViewModel 单向推过去，命令从这里收
        WebHome.Attach(_mainVm, _pipelineVm);
        WebHome.NavigationRequested += NavigateTo;
        WebHome.CommandRequested += OnWebCommand;

        // 回放页不再承载训练器：那里只发"带着这个门将去训练"，导航由这里负责
        ResultsPage.TrainerRequested += (_, keeper) =>
        {
            NavigateTo("keeper");
            GoalkeeperPage.EnterWith(keeper);
        };

        DataContext = this;
        Loaded += async (_, _) => await InitializeAsync();
        _isInitialized = true;
    }

    /// <summary>
    /// 前端发来的命令。前端只表达意图，具体动作和顺序仍由这里决定，
    /// 避免把业务逻辑散到 TypeScript 里。
    /// </summary>
    private void OnWebCommand(string name, JsonElement? payload)
    {
        switch (name)
        {
            case "refresh":
                _ = _mainVm.RefreshListsAsync();
                break;

            // 登记新样本 / 在线采集都要在处理队列页完成
            case "newSample":
            case "capture":
                NavigateTo("pipeline");
                break;

            case "environment.setup":
                _mainVm.SetupEnvironmentCommand.Execute(null);
                break;

            // ── 处理队列 ──
            case "pipeline.runOffline":
                if (_pipelineVm.RunOfflineCommand.CanExecute(null))
                    _pipelineVm.RunOfflineCommand.Execute(null);
                break;
            case "pipeline.runOnline":
                _pipelineVm.RunOnlineCommand.Execute(null);
                break;
            case "pipeline.detectCameras":
                _pipelineVm.DetectCamerasCommand.Execute(null);
                break;
            case "pipeline.testNetwork":
                _pipelineVm.TestNetworkCommand.Execute(null);
                break;
            case "pipeline.cancel":
                _pipelineVm.CancelCommand.Execute(null);
                break;
            case "pipeline.startAnalysis":
                _pipelineVm.StartAnalysisCommand.Execute(null);
                break;
            case "pipeline.stopAnalysis":
                _pipelineVm.StopAnalysisCommand.Execute(null);
                break;

            // 点球手画像：从训练 CSV 归纳射手特征，结果随状态推送到分析页
            case "analyze.shooterProfile":
                {
                    var profiler = App.Services.GetService(typeof(IShooterProfileService)) as IShooterProfileService;
                    _mainVm.ShooterProfile = ShooterProfileSnapshot.From(profiler?.AnalyzeTrainingData());
                    _mainVm.StatusBarText = _mainVm.ShooterProfile?.HasData == true
                        ? _mainVm.ShooterProfile.CountText
                        : "暂无可用训练轨迹";
                    break;
                }

            case "pipeline.pickSamples":
                ApplySampleSelection(payload);
                break;

            case "pipeline.setField":
                ApplyPipelineField(payload);
                break;

            // ── 相机标定 ──
            case "calibration.browse":
                BrowseCalibrationFile(payload);
                break;
            case "calibration.runIntrinsics":
                _ = RunIntrinsicsAsync();
                break;
            case "calibration.runExtrinsics":
                _ = RunExtrinsicsAsync();
                break;
            case "calibration.recalibrate":
                _mainVm.CalibShowWorkflow = true;
                break;
        }

        WebHome.PushState();
    }

    private void ApplySampleSelection(JsonElement? payload)
    {
        if (payload is not { } p || !p.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
            return;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in names.EnumerateArray())
        {
            var n = item.GetString();
            if (!string.IsNullOrEmpty(n)) wanted.Add(n);
        }

        foreach (var item in _pipelineVm.SampleItems)
            item.IsSelected = wanted.Contains(item.Name);
    }

    private void ApplyPipelineField(JsonElement? payload)
    {
        if (payload is not { } p) return;
        if (!p.TryGetProperty("field", out var fieldEl)) return;
        var field = fieldEl.GetString();
        if (string.IsNullOrEmpty(field)) return;

        // 布尔与字符串混在一起，按字段名各自取，不做特殊推断。
        bool Bool(string key) =>
            p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.True;
        string Text(string key) =>
            p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        switch (field)
        {
            case "skipReconstruct": _pipelineVm.SkipReconstruct = Bool("value"); break;
            case "skipBallistic": _pipelineVm.SkipBallistic = Bool("value"); break;
            case "liveAnalysis": _pipelineVm.IsLiveAnalysis = Bool("value"); break;

            case "usbMode":
                _pipelineVm.IsUsbMode = Bool("value");
                break;
            case "networkMode":
                _pipelineVm.IsNetworkMode = Bool("value");
                break;

            case "camLeft": _pipelineVm.CamLeft = Text("value"); break;
            case "camRight": _pipelineVm.CamRight = Text("value"); break;
            case "urlLeft": _pipelineVm.UrlLeft = Text("value"); break;
            case "urlRight": _pipelineVm.UrlRight = Text("value"); break;
            case "sampleName": _pipelineVm.SampleName = Text("value"); break;

            case "goalkeeper":
                var name = Text("value");
                _pipelineVm.SelectedGoalkeeper = _pipelineVm.Goalkeepers
                    .FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
                break;
        }
    }

    private void BrowseCalibrationFile(JsonElement? payload)
    {
        if (payload is not { } p || !p.TryGetProperty("slot", out var slotEl)) return;
        var slot = slotEl.GetString() ?? "";

        var (title, filter) = slot switch
        {
            "leftChess" => ("选择射手视角左侧棋盘格视频（左相机）", "视频文件|*.mp4;*.avi;*.mov;*.mkv|所有文件|*.*"),
            "rightChess" => ("选择射手视角右侧棋盘格视频（右相机）", "视频文件|*.mp4;*.avi;*.mov;*.mkv|所有文件|*.*"),
            "leftField" => ("选择射门视角左侧参考照片（左相机）", "图片文件|*.jpg;*.jpeg;*.png;*.bmp"),
            "rightField" => ("选择射门视角右侧参考照片（右相机）", "图片文件|*.jpg;*.jpeg;*.png;*.bmp"),
            _ => ("选择文件", "所有文件|*.*"),
        };

        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        if (dlg.ShowDialog(this) != true) return;

        switch (slot)
        {
            case "leftChess": _mainVm.CalibLeftChessVideo = dlg.FileName; break;
            case "rightChess": _mainVm.CalibRightChessVideo = dlg.FileName; break;
            case "leftField": _mainVm.CalibLeftFieldImage = dlg.FileName; break;
            case "rightField": _mainVm.CalibRightFieldImage = dlg.FileName; break;
        }
    }

    private async Task RunIntrinsicsAsync()
    {
        if (string.IsNullOrWhiteSpace(_mainVm.CalibLeftChessVideo) ||
            string.IsNullOrWhiteSpace(_mainVm.CalibRightChessVideo))
        {
            _mainVm.CalibIntrinsicsStatus = "请先选择左右相机的棋盘格视频";
            return;
        }

        _mainVm.CalibIntrinsicsStatus = "正在标定内参，请稍候…（结果写入 calib/ 目录）";
        try
        {
            var ok = await _mainVm.CalibrateIntrinsicsAsync(
                _mainVm.CalibLeftChessVideo.Trim(), _mainVm.CalibRightChessVideo.Trim());
            _mainVm.CalibIntrinsicsStatus = ok
                ? "内参标定完成，结果已保存到 calib/，可继续外参标定"
                : "内参标定失败，请查看 logs 文件夹中的日志";
        }
        catch (Exception ex)
        {
            _mainVm.CalibIntrinsicsStatus = "标定异常: " + ex.Message;
        }
    }

    private async Task RunExtrinsicsAsync()
    {
        if (string.IsNullOrWhiteSpace(_mainVm.CalibLeftFieldImage) ||
            string.IsNullOrWhiteSpace(_mainVm.CalibRightFieldImage))
        {
            _mainVm.CalibExtrinsicsStatus = "请先选择射门视角左侧/右侧的参考照片";
            return;
        }

        _mainVm.CalibExtrinsicsStatus = "正在标定外参，请在弹出的图像窗口依次点击参考点…";
        try
        {
            var ok = await _mainVm.CalibrateExtrinsicsAsync(
                _mainVm.CalibLeftFieldImage.Trim(), _mainVm.CalibRightFieldImage.Trim());
            _mainVm.CalibExtrinsicsStatus = ok
                ? "外参标定完成，结果已保存到 calib/，可以开始处理样本了"
                : "外参标定失败，请查看 logs 文件夹中的日志";
        }
        catch (Exception ex)
        {
            _mainVm.CalibExtrinsicsStatus = "标定异常: " + ex.Message;
        }
    }

    private async Task InitializeAsync()
    {
        await _mainVm.InitializeAsync();
        UpdateStatusIndicators();
    }

    private void UpdateStatusIndicators()
    {
        if (_mainVm.IsEnvironmentReady)
        {
            EnvStatusDot.Fill = (System.Windows.Media.Brush)
                FindResource("AccentBrush");
            EnvStatusLabel.Text = "环境就绪 ✓";
        }
        else
        {
            EnvStatusDot.Fill = (System.Windows.Media.Brush)
                FindResource("WarningBrush");
            EnvStatusLabel.Text = _mainVm.IsSettingUp ? "安装环境中…"
                : _mainVm.IsCheckingEnvironment ? "正在检查"
                : "需要设置";
        }

        // 更新状态栏
        StatusBarTextBlock.Text = _mainVm.StatusBarText;
        if (_mainVm.GpuInfo != null)
            GpuLabel.Text = _mainVm.GpuInfo.GpuName;
    }

    // ─── 导航 ───
    private bool _isInitialized;

    /// <summary>从子页面调用的公共导航方法</summary>
    public void NavigateTo(string pageKey)
    {
        // 旧 key 继续可用：页面内部还有若干处按旧名跳转，不必一次性全改。
        pageKey = pageKey switch
        {
            "dashboard" => "workbench",
            "pipeline" => "analyze",
            "calibration" => "settings",
            "results" => "review",
            _ => pageKey,
        };

        RadioButton? target = pageKey switch
        {
            "workbench" => NavWorkbench,
            "capture" => NavCapture,
            "analyze" => NavAnalyze,
            "review" => NavReview,
            "keeper" => NavKeeper,
            "settings" => NavSettings,
            _ => null,
        };
        if (target != null)
            target.IsChecked = true;
    }
    private void NavItem_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        if (sender is not RadioButton rb || rb.Tag is not string key) return;

        // 隐藏所有页面
        DashboardPage.Visibility = Visibility.Collapsed;
        PipelinePage.Visibility = Visibility.Collapsed;
        CalibrationPage.Visibility = Visibility.Collapsed;
        ResultsPage.Visibility = Visibility.Collapsed;
        GoalkeeperPage.Visibility = Visibility.Collapsed;
        WebHome.Visibility = Visibility.Collapsed;

        // 显示目标页面
        switch (key)
        {
            case "workbench":
            case "capture":
            case "analyze":
            case "settings":
                // 这四页都由 WebView2 渲染，切换的是前端路由，不是 WPF 页面。
                // WebView2 是原生 HWND，其它页面一律收起，否则它会盖住 WPF 内容。
                WebHome.Visibility = Visibility.Visible;
                WebHome.SetRoute(key);
                TitleBarText.Text = key switch
                {
                    "workbench" => "工作台",
                    "capture" => "采集",
                    "analyze" => "分析",
                    _ => "设置",
                };
                break;
            case "keeper":
                // 门将训练已从回放页剥离成独立页：没有 HwndHost、没有 MediaElement 跨层，
                // 它是纯 WPF 页面，可以和侧栏一起正常切换。
                GoalkeeperPage.Attach(_mainVm);
                GoalkeeperPage.Visibility = Visibility.Visible;
                TitleBarText.Text = "门将";
                break;
            case "review":
                // 回放页必须留在 WPF：Unity 靠 HwndHost 贴窗，训练器用 MediaElement，
                // 两者都在 airspace 冲突区里，WebView2 不能与它们同屏。
                ResultsPage.Visibility = Visibility.Visible;
                TitleBarText.Text = "回放";
                break;
        }
    }

    // ─── 标题栏控制 ───
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaxRestore_Click(sender, e);
        else if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            MaxRestoreBtn.Content = "\uE922";
        }
        else
        {
            WindowState = WindowState.Maximized;
            MaxRestoreBtn.Content = "\uE923";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
        => Close();
}
