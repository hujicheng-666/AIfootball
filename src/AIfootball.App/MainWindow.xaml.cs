using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIfootball.App.ViewModels;

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

        // 设置页面 DataContext
        DashboardPage.DataContext = _mainVm;
        PipelinePage.DataContext = _pipelineVm;
        CalibrationPage.DataContext = _mainVm;
        ResultsPage.DataContext = _mainVm;

        DataContext = this;
        Loaded += async (_, _) => await InitializeAsync();
        _isInitialized = true;
    }

    private async Task InitializeAsync()
    {
        await _mainVm.InitializeAsync();

        // 更新环境状态指示器
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
            EnvStatusLabel.Text = "需要设置";
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
        RadioButton? target = pageKey switch
        {
            "dashboard" => NavDashboard,
            "pipeline" => NavPipeline,
            "calibration" => NavCalibration,
            "results" => NavResults,
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

        // 显示目标页面
        switch (key)
        {
            case "dashboard":
                DashboardPage.Visibility = Visibility.Visible;
                TitleBarText.Text = "AI 足球轨迹分析平台 — 仪表盘";
                break;
            case "pipeline":
                PipelinePage.Visibility = Visibility.Visible;
                TitleBarText.Text = "流水线处理";
                break;
            case "calibration":
                CalibrationPage.Visibility = Visibility.Visible;
                TitleBarText.Text = "相机标定";
                break;
            case "results":
                ResultsPage.Visibility = Visibility.Visible;
                TitleBarText.Text = "结果查看";
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
