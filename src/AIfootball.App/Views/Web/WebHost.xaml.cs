using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using AIfootball.App.ViewModels;

namespace AIfootball.App.Views.Web;

/// <summary>
/// WebView2 宿主：React 界面跑在这里，WPF 只负责窗口、导航和本地能力。
///
/// 两点必须记住：
/// 1. WebView2 与 HwndHost / MediaElement 属于 airspace 冲突区域，不能与它们重叠。
///    切到仍由 WPF 渲染的页面时，这个控件必须 Collapsed（见 MainWindow 的导航逻辑）。
/// 2. 前端拿不到文件系统，所有状态都由这里 PushState 推过去；前端只发「命令」。
/// </summary>
public partial class WebHost : UserControl
{
    private MainViewModel? _vm;
    private PipelineViewModel? _pipeline;
    private DispatcherTimer? _pushTimer;
    private bool _pageReady;
    private bool _attached;
    private string _route = "workbench";

    /// <summary>前端请求切换页面（例如点侧栏、点「开始分析」）。</summary>
    public event Action<string>? NavigationRequested;

    /// <summary>前端请求执行后端动作。payload 是原样的 JSON 片段，解析由接收方负责。</summary>
    public event Action<string, JsonElement?>? CommandRequested;

    public WebHost()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsReady => _pageReady && View.CoreWebView2 is not null;

    // ─────────────────────────── 生命周期 ───────────────────────────

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (View.CoreWebView2 is not null) return;

        try
        {
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AIfootball", "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await View.EnsureCoreWebView2Async(env);

            View.DefaultBackgroundColor = System.Drawing.Color.FromArgb(247, 249, 252);

            var core = View.CoreWebView2;
            if (core is null)
            {
                ShowBootError("界面初始化失败", "WebView2 核心未就绪。");
                return;
            }

            var s = core.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.AreDevToolsEnabled = IsDevMode();

            core.WebMessageReceived += OnWebMessageReceived;
            core.ProcessFailed += OnProcessFailed;

            var webRoot = Path.Combine(AppContext.BaseDirectory, "web");
            if (!Directory.Exists(webRoot))
            {
                ShowBootError("找不到界面资源目录", webRoot + " 不存在，请先执行 npm run build。");
                return;
            }

            core.SetVirtualHostNameToFolderMapping(
                "app.local", webRoot, CoreWebView2HostResourceAccessKind.Allow);

            // 开发时可指向 vite dev server，改前端不用重新 build：
            //   set AIFOOTBALL_WEB_DEV=http://localhost:5173
            var dev = Environment.GetEnvironmentVariable("AIFOOTBALL_WEB_DEV");
            core.Navigate(string.IsNullOrWhiteSpace(dev) ? "https://app.local/index.html" : dev);
        }
        catch (Exception ex)
        {
            ShowBootError("界面加载失败", ex.Message);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pushTimer?.Stop();
        Detach();
    }

    public void Detach()
    {
        if (!_attached || _vm is null) return;
        _vm.PropertyChanged -= OnVmChanged;
        _vm.Samples.CollectionChanged -= OnCollectionChanged;
        _vm.Goalkeepers.CollectionChanged -= OnCollectionChanged;
        if (_pipeline is not null)
        {
            _pipeline.PropertyChanged -= OnVmChanged;
            _pipeline.SampleItems.CollectionChanged -= OnCollectionChanged;
            _pipeline.Steps.CollectionChanged -= OnCollectionChanged;
        }
        _attached = false;
    }

    /// <summary>绑定 ViewModel；之后任何属性或集合变化都会自动推给前端。</summary>
    public void Attach(MainViewModel vm, PipelineViewModel? pipeline = null)
    {
        if (_attached) Detach();

        _vm = vm;
        _pipeline = pipeline;
        vm.PropertyChanged += OnVmChanged;
        vm.Samples.CollectionChanged += OnCollectionChanged;
        vm.Goalkeepers.CollectionChanged += OnCollectionChanged;

        if (pipeline is not null)
        {
            pipeline.PropertyChanged += OnVmChanged;
            pipeline.SampleItems.CollectionChanged += OnCollectionChanged;
            pipeline.Steps.CollectionChanged += OnCollectionChanged;
        }

        _attached = true;
        MapPreviewHost();
        PushState();
    }

    /// <summary>让前端能直接 <c>&lt;img src="https://preview.local/…"&gt;</c> 取预览帧。</summary>
    private void MapPreviewHost()
    {
        var core = View.CoreWebView2;
        if (core is null || _pipeline is null) return;

        try
        {
            var dir = _pipeline.LivePreviewDirectory;
            Directory.CreateDirectory(dir);
            core.SetVirtualHostNameToFolderMapping(
                "preview.local", dir, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch
        {
            // 映射失败只影响在线预览的图片，不影响其它功能。
        }
    }

    /// <summary>告诉前端当前该显示哪一页。宿主是导航的唯一权威，前端只跟随。</summary>
    public void SetRoute(string route)
    {
        _route = route;
        if (!_pageReady || View.CoreWebView2 is null) return;
        try
        {
            var payload = JsonSerializer.Serialize(new { type = "route", page = route });
            View.CoreWebView2.PostWebMessageAsJson(payload);
        }
        catch
        {
            // 忽略：页面下次 ready 时会重新同步。
        }
    }

    // ─────────────────────────── 状态推送 ───────────────────────────

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e) => SchedulePush();

    private void OnCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => SchedulePush();

    /// <summary>合并 120ms 内的多次变化，避免扫描样本时刷屏。</summary>
    private void SchedulePush()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(SchedulePush);
            return;
        }

        _pushTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _pushTimer.Tick -= OnPushTick;
        _pushTimer.Tick += OnPushTick;
        _pushTimer.Stop();
        _pushTimer.Start();
    }

    private void OnPushTick(object? sender, EventArgs e)
    {
        _pushTimer?.Stop();
        PushState();
    }

    public void PushState()
    {
        if (!_pageReady || _vm is null || View.CoreWebView2 is null) return;

        try
        {
            var state = DashboardStateBuilder.BuildJson(_vm, _pipeline);
            View.CoreWebView2.PostWebMessageAsJson($"{{\"type\":\"state\",\"payload\":{state}}}");
        }
        catch
        {
            // 推送失败不致命：下一次状态变化会重试。
        }
    }

    // ─────────────────────────── 从前端收消息 ───────────────────────────

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? type;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            switch (type)
            {
                case "ready":
                    _pageReady = true;
                    BootLayer.Visibility = Visibility.Collapsed;
                    MapPreviewHost();
                    SetRoute(_route);
                    PushState();
                    break;

                case "navigate":
                    if (root.TryGetProperty("page", out var page))
                    {
                        var key = page.GetString();
                        if (!string.IsNullOrEmpty(key)) NavigationRequested?.Invoke(key);
                    }
                    break;

                case "command":
                    if (root.TryGetProperty("name", out var name))
                    {
                        var cmd = name.GetString();
                        if (!string.IsNullOrEmpty(cmd))
                        {
                            // doc 在这里就被释放了，payload 必须克隆出去。
                            JsonElement? payload = root.TryGetProperty("payload", out var p)
                                ? p.Clone()
                                : null;
                            CommandRequested?.Invoke(cmd, payload);
                        }
                    }
                    break;
            }
        }
        catch
        {
            // 前端发来的内容不可信，解析失败直接丢弃。
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _pageReady = false;
        ShowBootError("界面进程异常", $"{e.ProcessFailedKind} — 已尝试恢复，若仍空白请重启应用。");
        Dispatcher.BeginInvoke(() =>
        {
            try { View.CoreWebView2?.Reload(); } catch { /* 进程都没了就随它去 */ }
        });
    }

    // ─────────────────────────── 辅助 ───────────────────────────

    private static bool IsDevMode() =>
#if DEBUG
        true;
#else
        false;
#endif

    private void ShowBootError(string title, string detail)
    {
        BootLayer.Visibility = Visibility.Visible;
        BootTitle.Text = title;
        BootTitle.Foreground = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));
        BootDetail.Text = detail;
        BootDetail.Visibility = Visibility.Visible;
    }
}
