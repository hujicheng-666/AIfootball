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
using AIfootball.App.Controls;
using AIfootball.App.Models;
using AIfootball.App.ViewModels;
using AIfootball.App.Views.Dialogs;
using AIfootball.Core.Models;
using AIfootball.Core.Services;

namespace AIfootball.App.Views.Pages;

/// <summary>
/// 门将模型训练：标注比赛片段 → 训练区域概率 → 发布到门将库。
/// 从回放页剥离出来的独立页面——它是一条完整工作流，不该寄生在 3D 回放的覆盖层里。
/// </summary>
public partial class GoalkeeperPage : UserControl
{
    private MainViewModel? _vm;
    private Window? _ownerWindow;

    public GoalkeeperPage()
    {
        InitializeComponent();
        Timeline.SeekRequested += Timeline_SeekRequested;
        Timeline.ZoomRequested += Timeline_ZoomRequested;
        Timeline.PanRequested += Timeline_PanRequested;
        Loaded += GoalkeeperPage_Loaded;
        Unloaded += GoalkeeperPage_Unloaded;
    }

    /// <summary>由 MainWindow 注入，用于写操作日志。</summary>
    public void Attach(MainViewModel viewModel) => _vm = viewModel;

    private void GoalkeeperPage_Loaded(object sender, RoutedEventArgs e)
    {
        _ownerWindow = Window.GetWindow(this);
        ReloadTrainerKeepers();
        StartTimelineTimer();
        Focus();
    }

    private void GoalkeeperPage_Unloaded(object sender, RoutedEventArgs e)
    {
        StopTrainerVideo();
        StopTimelineTimer();
    }

    private async Task NotifyUnityKeeperAsync(string name)
    {
        try
        {
            var engine = App.Services.GetService(typeof(IPythonEngine)) as IPythonEngine;
            await UnityCommandClient.SendAsync(engine?.WorkspaceDir ?? ".", $"goalkeeper:{name}");
        }
        catch (Exception ex)
        {
            _vm?.AddLog("error", $"Unity 控制命令失败: {ex.Message}");
        }
    }

    #region 门将训练

    private GoalkeeperTrainingStore? _trainingStore;
    private string? _trainerKeeper;
    private string? _trainerClipPath;
    private readonly List<string> _trainerClips = [];
    private readonly List<(double X, double Y)> _calibrationClicks = [];
    private (double Lateral, double Height)? _trainerPendingPoint;
    private GoalkeeperModelDocument? _trainerDocument;
    private bool _trainerPlaying;
    private bool _trainerCalibrating;
    /// <summary>开始标定时的播放头秒数：四个点属于这一帧，换了帧就得重新点。</summary>
    private double _calibrationFrameSeconds = -1;

    // 当前视频的分段门框标定（内存缓存，换视频/重标时刷新）
    private VideoCalibrationDocument? _videoCalibrations;
    private string? _videoCalibrationsKey;

    private GoalkeeperTrainingStore TrainerStore
    {
        get
        {
            if (_trainingStore is not null) return _trainingStore;
            var engine = App.Services.GetService(typeof(IPythonEngine)) as IPythonEngine;
            _trainingStore = new GoalkeeperTrainingStore(engine?.WorkspaceDir ?? AppContext.BaseDirectory);
            return _trainingStore;
        }
    }




    private enum TrainerTab { Annotate, Train }

    /// <summary>程序里切换页签时挡住 Checked 回调，避免两个方法互相调用绕成环。</summary>
    private bool _syncingTab;

    private void KeeperTab_Checked(object sender, RoutedEventArgs e)
    {
        // InitializeComponent 阶段 RadioButton 会先触发一次 Checked，那时面板字段还没赋值
        if (!IsLoaded || _syncingTab) return;
        var train = ReferenceEquals(sender, TrainerTrainTab);
        ShowTrainerTab(train ? TrainerTab.Train : TrainerTab.Annotate);
        if (train) ShowTrainerSummary();
    }

    private void ShowTrainerTab(TrainerTab tab)
    {
        TrainerAnnotatePanel.Visibility = tab == TrainerTab.Annotate ? Visibility.Visible : Visibility.Collapsed;
        TrainerTrainPanel.Visibility = tab == TrainerTab.Train ? Visibility.Visible : Visibility.Collapsed;
        // 训练页没有画面，走带一起收起来，否则播放键按下去什么都看不到
        TrainerTransportBar.Visibility = tab == TrainerTab.Annotate ? Visibility.Visible : Visibility.Collapsed;

        _syncingTab = true;
        try
        {
            TrainerAnnotateTab.IsChecked = tab == TrainerTab.Annotate;
            TrainerTrainTab.IsChecked = tab == TrainerTab.Train;
        }
        finally
        {
            _syncingTab = false;
        }

        if (tab != TrainerTab.Annotate) StopTrainerVideo();
    }

    // ===== 时间轴 + 门框叠加 =====
    // 点球只有一两秒，自动播放根本看不清。这里把整段合集开放成可拖的时间轴：
    // 用户自己拖到球过线那一帧、点一下球。
    // 门框不做自动检测——用户手点四个门角标一次，整段视频通用（见 ApplyManualGoalFrame）。
    private const double TrainerFps = 25.0;

    private GoalRect? _frameGoalRect;
    private string _frameGoalSource = "";
    private (int Frame, double T, double X, double Y)? _ballMark;
    private GoalFrameCalibration? _pendingCalibration;
    private double? _reloadSeekSeconds;
    private DispatcherTimer? _timelineTimer;

    // ── 定位（seek）合并 ─────────────────────────────────────────────────
    // MediaElement 的 seek 是异步的，一次定位要从关键帧解到目标帧。
    // 连点「+1s / -1帧」或按住方向键时如果每次点击都压一次 Position，管线会被打满卡死。
    // 这里只记"最后想去哪"，由节拍器按最小间隔合并下发。
    private double? _pendingSeek;
    private DispatcherTimer? _seekTimer;
    private DateTime _lastSeekAppliedAt = DateTime.MinValue;
    private DateTime? _seekSettleAt;
    private double _seekSettleTarget;
    private (double Target, DateTime At)? _seekWatch;
    private DateTime _lastRecoveryAt = DateTime.MinValue;
    private bool _seekRecoveryInFlight;

    /// <summary>手停下来多久才把合并后的目标真正下发。</summary>
    private const int SeekQuietMs = 110;

    /// <summary>按住方向键一直不停时，最多憋这么久就强制下发一次（保证画面还在动）。</summary>
    private const int SeekMaxHoldMs = 450;

    // 合并效果的可观测证据：连点结束后把"多少次请求 → 多少次真 seek"写进日志
    private int _seekRequests;
    private int _seekApplies;
    private DateTime? _seekSessionStart;
    private DateTime _lastSeekRequestAt = DateTime.MinValue;
    private DateTime? _pendingSince;

    // ── 脚次表 ───────────────────────────────────────────────────────────
    // 一段合集里几十脚点球，只靠 ±1s 按钮摸过去是不可能标完的。
    // shot_list.py 按镜头切换把视频切成"一脚一段"，导航和断点续标都建在这上面。
    private ShotListDocument? _shotList;
    private List<ShotSpan> _shotNav = [];
    private string? _shotListFile;
    private string? _scanningPath;
    private bool _shotScanRunning;
    private bool _resumeToNextShot;

    /// <summary>超过这个长度的"镜头"里其实挤了好几脚，按 4 秒再切成虚拟脚次。</summary>
    private const double LongShotSeconds = 8.0;

    /// <summary>长镜头细分成虚拟脚次时用的步长（322 秒 / 80 脚 ≈ 4 秒）。</summary>
    private const double VirtualShotSeconds = 4.0;

    /// <summary>判定"同一脚"的时间容差：重新标注时不新增而是覆盖。</summary>
    private const double SameShotTolerance = 1.2;

    // 播放时每 60ms 刷新一次"第几脚"读数，不能每次都去读一遍 clips 目录
    private List<KeeperClipAnnotation>? _videoAnnotationCache;
    private string? _videoAnnotationCacheKey;
    private int _annotationVersion;

    private void InvalidateAnnotationCache() => _annotationVersion++;

    private void StartTimelineTimer()
    {
        _timelineTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _timelineTimer.Tick -= TimelineTimer_Tick;
        _timelineTimer.Tick += TimelineTimer_Tick;
        _timelineTimer.Start();
    }

    private void StopTimelineTimer() => _timelineTimer?.Stop();

    private void TimelineTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible) return;
        if (TrainerVideo.Source is null || !_trainerPlaying) return;
        // 播放时滑块只是跟随显示；用户拖动时由滑块驱动播放头，定时器不去抢
        SyncTimelineFromVideo();
    }

    private double VideoDurationSeconds() =>
        TrainerVideo.NaturalDuration.HasTimeSpan ? TrainerVideo.NaturalDuration.TimeSpan.TotalSeconds : 0;

    private void SyncTimelineFromVideo()
    {
        if (TrainerVideo.Source is null) return;
        // 定位还没落地时用"想去的位置"，否则滑块会被旧位置拽回去，看起来像点不动
        SyncTimelineTo(PendingOrCurrentSeconds);
    }

    private void SyncTimelineTo(double seconds)
    {
        if (TrainerVideo.Source is null) return;
        var duration = VideoDurationSeconds();
        Timeline.Duration = duration;
        Timeline.Position = Math.Clamp(seconds, 0, Math.Max(0.001, duration));
        UpdateFrameText();
        UpdateShotText();
        EnsurePlayheadVisible();
    }

    private void UpdateFrameText() => UpdateFrameText(PendingOrCurrentSeconds);

    private void UpdateFrameText(double seconds)
    {
        var duration = VideoDurationSeconds();
        if (duration <= 0)
        {
            TrainerFrameText.Text = "帧 - / -";
            return;
        }
        var frame = (int)Math.Round(seconds * TrainerFps);
        var total = (int)Math.Round(duration * TrainerFps);
        TrainerFrameText.Text = $"帧 {frame} / {total}";
    }

    // ── 时间轴：拖动 / 缩放 / 平移 ────────────────────────────────────────
    // 轨道本身只抛"点到哪一刻"，定位仍然走 SeekTrainer：
    // 播放中拖轨道要先停，否则定时器会把播放头冲回去，看着像点不动。

    private void Timeline_SeekRequested(object? sender, TimelineSeekEventArgs e)
    {
        if (TrainerVideo.Source is null) return;
        if (_trainerPlaying) StopTrainerVideo();
        SeekTrainer(e.Seconds);
    }

    private void Timeline_ZoomRequested(object? sender, TimelineZoomEventArgs e)
    {
        var oldPixelsPerSecond = Timeline.PixelsPerSecond;
        if (oldPixelsPerSecond <= 0) return;

        // 鼠标下的时间点保持不动：缩放才有"放大这一脚"的手感，而不是每次都回到片头
        var offsetBefore = TimelineScroll.HorizontalOffset;
        var xInView = e.ContentX - offsetBefore;
        var anchorSeconds = e.ContentX / oldPixelsPerSecond;

        var next = Math.Clamp(oldPixelsPerSecond * e.Factor, MinPixelsPerSecond(), MaxPixelsPerSecond());
        if (Math.Abs(next - oldPixelsPerSecond) < 0.01) return;

        Timeline.PixelsPerSecond = next;
        UpdateZoomText();
        // 内容宽度变了才谈得上滚动：等布局（Render）跑完再定位，否则范围还是旧的会被夹回去
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            TimelineScroll.ScrollToHorizontalOffset(
                Math.Max(0, anchorSeconds * Timeline.PixelsPerSecond - xInView));
        }));
    }

    private void Timeline_PanRequested(object? sender, TimelinePanEventArgs e)
    {
        TimelineScroll.ScrollToHorizontalOffset(
            Math.Max(0, TimelineScroll.HorizontalOffset + e.DeltaPixels));
    }

    private string? _zoomClip;

    /// <summary>
    /// 换片后给一个默认缩放：整段可见太粗（切点糊成一条线），
    /// 所以按"整段铺 6 屏"开——既看得到结构，也分得清每一脚。
    /// </summary>
    private void ResetZoomForNewClip()
    {
        if (TrainerVideo.Source is null) return;
        if (string.Equals(_zoomClip, _trainerClipPath, StringComparison.OrdinalIgnoreCase)) return;
        _zoomClip = _trainerClipPath;

        var fit = MinPixelsPerSecond();
        Timeline.PixelsPerSecond = Math.Clamp(fit * 6, fit, MaxPixelsPerSecond());
        UpdateZoomText();
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    /// <summary>整段可见时的像素密度：缩放下限就取它，保证内容永远铺满视口。</summary>
    private double MinPixelsPerSecond()
    {
        var duration = VideoDurationSeconds();
        var viewport = TimelineScroll.ViewportWidth;
        if (duration <= 0 || viewport <= 0) return 0.5;
        return viewport / duration;
    }

    /// <summary>上限：25fps 下一帧 8px，逐帧比对时还看得清相邻两帧的差别。</summary>
    private double MaxPixelsPerSecond() => 200;

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomAroundPlayhead(1.4);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAroundPlayhead(1 / 1.4);

    private void ZoomAroundPlayhead(double factor)
    {
        if (TrainerVideo.Source is null) return;
        var next = Math.Clamp(Timeline.PixelsPerSecond * factor, MinPixelsPerSecond(), MaxPixelsPerSecond());
        Timeline.PixelsPerSecond = next;
        UpdateZoomText();
        EnsurePlayheadVisible();
    }

    private void ZoomFit_Click(object sender, RoutedEventArgs e)
    {
        if (TrainerVideo.Source is null) return;
        Timeline.PixelsPerSecond = MinPixelsPerSecond();
        UpdateZoomText();
        TimelineScroll.ScrollToHorizontalOffset(0);
    }

    private void UpdateZoomText()
    {
        var fit = MinPixelsPerSecond();
        ZoomText.Text = fit > 0 && Timeline.PixelsPerSecond > 0
            ? $"{Timeline.PixelsPerSecond / fit:0.#}×"
            : "—";
    }

    private void TimelineScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 视口变了，"整段可见"的密度也跟着变：已经处于全览状态时跟着重算
        if (VideoDurationSeconds() <= 0) return;
        if (Timeline.PixelsPerSecond <= MinPixelsPerSecond() * 1.05)
            Timeline.PixelsPerSecond = MinPixelsPerSecond();
        UpdateZoomText();
    }

    /// <summary>播放头跑出视口时把轨道滚过去，跟播放器的自动跟随一样。</summary>
    private void EnsurePlayheadVisible()
    {
        var viewport = TimelineScroll.ViewportWidth;
        if (viewport <= 0) return;
        // 拖动时跟着指针走就够了；这时候自动滚会让播放头从指针下面滑走
        if (Timeline.IsDragging) return;
        var x = PendingOrCurrentSeconds * Timeline.PixelsPerSecond;
        var offset = TimelineScroll.HorizontalOffset;
        if (x < offset + 8)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - viewport * 0.15));
        else if (x > offset + viewport - 24)
            TimelineScroll.ScrollToHorizontalOffset(Math.Max(0, x - viewport * 0.6));
    }

    /// <summary>
    /// 把播放头挪到指定秒。
    ///
    /// 这里**不直接**去写 <see cref="MediaElement.Position"/>：WPF 的 seek 是异步的，
    /// 一次定位要从最近的关键帧解到目标帧，代价远大于一次 UI 事件。
    /// 连续快点「+1s / -1帧」时（或按住方向键自动重复）每点一次就压一次 Position，
    /// 管线来不及消化，最后会卡死：画面停在旧帧、Position 读回归零。
    ///
    /// 所以改成"先记目标、安静下来才下发"：
    /// 单点立刻落地（跟手），连点只落地首尾两次，中间全部合并掉。
    /// </summary>
    private void SeekTrainer(double seconds)
    {
        if (TrainerVideo.Source is null) return;
        var duration = VideoDurationSeconds();
        var target = duration > 0 ? Math.Clamp(seconds, 0, duration) : Math.Max(0, seconds);
        if (_trainerPlaying)
        {
            TrainerVideo.Pause();
            _trainerPlaying = false;
        }

        var now = DateTime.UtcNow;
        var idleBefore = (now - _lastSeekRequestAt).TotalMilliseconds;
        _lastSeekRequestAt = now;
        _seekRequests++;
        _seekSessionStart ??= now;

        // 读数立刻跟手，用户不需要等 seek 落地就知道自己点到哪了
        UpdateTimeText(target);
        UpdateFrameText(target);
        UpdateShotText();
        SyncTimelineTo(target);
        // 拖过镜头边界时门框要跟着换，否则橙框会一直停在上一段的机位上
        ApplyManualGoalFrame(target);
        RedrawGoalOverlay();
        DropCalibrationClicksIfFrameChanged(target);

        // 上一轮已经安静下来了：这是一次独立点击，立刻落地，不能有"点了没反应"的延迟
        if (_pendingSeek is null && idleBefore > SeekQuietMs)
        {
            ApplySeek(target);
            _pendingSince = null;
            PumpSeek();
            return;
        }

        // 连点：只更新目标，等安静 SeekQuietMs 或按住超过 SeekMaxHoldMs 再下发
        _pendingSeek = target;
        _pendingSince ??= now;
        PumpSeek();
    }

    /// <summary>还没落地的目标位置；为 null 表示已同步。</summary>
    private double PendingOrCurrentSeconds => _pendingSeek ?? TrainerVideo.Position.TotalSeconds;

    /// <summary>把待下发的定位立刻落地（保存标注前要保证秒数与画面一致）。</summary>
    private void FlushPendingSeek()
    {
        if (_pendingSeek is not { } target) return;
        _pendingSeek = null;
        ApplySeek(target);
    }

    private void PumpSeek()
    {
        _seekTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _seekTimer.Tick -= SeekPump_Tick;
        _seekTimer.Tick += SeekPump_Tick;
        if (!_seekTimer.IsEnabled) _seekTimer.Start();
    }

    private void SeekPump_Tick(object? sender, EventArgs e)
    {
        if (TrainerVideo.Source is null)
        {
            _pendingSeek = null;
            _seekTimer?.Stop();
            return;
        }

        var now = DateTime.UtcNow;

        // 1) 有新的目标：等"手停下来"再下发。
        //    按住方向键时手不会停，所以再加一个最长持有时间，避免一直不刷新画面。
        if (_pendingSeek is { } target)
        {
            var idle = (now - _lastSeekRequestAt).TotalMilliseconds;
            var held = _pendingSince is { } since ? (now - since).TotalMilliseconds : 0;
            if (idle >= SeekQuietMs || held >= SeekMaxHoldMs)
            {
                _pendingSeek = null;
                _pendingSince = null;
                ApplySeek(target);
            }
            return;
        }

        // 2) 停手 260ms 后补压一次位置：暂停态 seek 完 WPF 偶尔不重绘，
        //    但绝不能像以前那样每次点击都补压（那会把 seek 次数直接翻倍）。
        if (_seekSettleAt is { } due && now >= due)
        {
            _seekSettleAt = null;
            if (!_trainerPlaying && TrainerVideo.Source is not null
                && Math.Abs(TrainerVideo.Position.TotalSeconds - _seekSettleTarget) > 0.02)
            {
                TrainerVideo.Position = TimeSpan.FromSeconds(_seekSettleTarget);
            }
        }

        // 3) 看门狗：下发过定位，1.5 秒后 Position 却被冲回别的值（典型是 0），
        //    说明管线已经卡死。自动重载一次，省得用户自己去找「重载」按钮。
        if (_seekWatch is { } watch && (now - watch.At).TotalMilliseconds > 1500)
        {
            _seekWatch = null;
            if (!_trainerPlaying && TrainerVideo.Source is not null
                && Math.Abs(TrainerVideo.Position.TotalSeconds - watch.Target) > 0.5)
            {
                RecoverTrainerVideo(watch.Target);
            }
        }

        if (_pendingSeek is null && _seekSettleAt is null && _seekWatch is null)
        {
            _seekTimer?.Stop();
            // 一轮连点结束：把合并比写进日志，卡不卡一眼能看出来
            if (_seekSessionStart is { } start)
            {
                var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
                if (_seekRequests > 1)
                {
                    AppLogger.Write("INFO",
                        $"定位合并：{_seekRequests} 次点击 → {_seekApplies} 次 seek（{elapsed:0}ms）");
                }
                _seekRequests = 0;
                _seekApplies = 0;
                _seekSessionStart = null;
            }
        }
    }

    private void ApplySeek(double target)
    {
        _lastSeekAppliedAt = DateTime.UtcNow;
        _seekApplies++;
        _seekSettleTarget = target;
        _seekSettleAt = _lastSeekAppliedAt.AddMilliseconds(260);
        _seekWatch = (target, _lastSeekAppliedAt);
        try
        {
            TrainerVideo.Position = TimeSpan.FromSeconds(target);
        }
        catch (Exception ex)
        {
            AppLogger.Write("WARN", $"定位失败 {target:0.00}s：{ex.Message}");
        }
    }

    /// <summary>
    /// 播放管线卡死后的自愈：重建 Source 并回到目标位置。
    /// 5 秒内只允许触发一次，避免"重载→又卡→再重载"的死循环。
    /// </summary>
    private void RecoverTrainerVideo(double seconds)
    {
        if (_seekRecoveryInFlight || _trainerClipPath is null) return;
        if ((DateTime.UtcNow - _lastRecoveryAt).TotalSeconds < 5) return;

        _seekRecoveryInFlight = true;
        _lastRecoveryAt = DateTime.UtcNow;
        AppLogger.Write("WARN", $"播放管线疑似卡死，自动重载（目标 {seconds:0.00}s）");
        TrainerProgressText.Text = "画面卡住了，正在自动重载并回到刚才那一帧…";

        _reloadSeekSeconds = seconds;
        StopTrainerVideo();
        TrainerVideo.Source = null;
        var path = _trainerClipPath;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                TrainerVideo.Source = new Uri(path, UriKind.Absolute);
                TrainerVideo.Play();
                _trainerPlaying = false;
                UpdatePlayButton();
            }
            catch (Exception ex)
            {
                AppLogger.Write("ERROR", $"自动重载失败：{ex.Message}");
                TrainerProgressText.Text = $"自动重载失败：{ex.Message}（请点「重载」）";
            }
            finally
            {
                _seekRecoveryInFlight = false;
            }
        }));
    }

    private void ClearBallMark_Click(object sender, RoutedEventArgs e) => ResetBallMark();

    private void ResetBallMark()
    {
        _ballMark = null;
        _pendingCalibration = null;
        _trainerPendingPoint = null;
        // 门框是"标一次、整段视频通用"的，清球标记不能把它一起清掉
        ApplyManualGoalFrame(PendingOrCurrentSeconds);
        TrainerPointText.Text = "拖到球过线帧，点一下球心。";
        RedrawGoalOverlay();
        DrawBallMark();
    }

    /// <summary>把当前视频的逐脚标定读进内存（换视频 / 重标后调用）。</summary>
    private void EnsureVideoCalibrations()
    {
        var key = $"{_trainerKeeper}|{_trainerClipPath}";
        if (_videoCalibrationsKey == key) return;
        _videoCalibrationsKey = key;
        _videoCalibrations = string.IsNullOrWhiteSpace(_trainerKeeper) || _trainerClipPath is null
            ? null
            : TrainerStore.LoadVideoCalibrations(_trainerKeeper, _trainerClipPath);
    }

    /// <summary>
    /// 某一秒属于哪一脚的起点（就是脚次表里的 Start）。
    /// 没有脚次表的单片段视频，整段算一脚，键固定为 0。
    /// </summary>
    private double ShotStartAt(double seconds)
    {
        if (_shotNav.Count > 0 && ShotListStore.FindAt(_shotNav, seconds) is { } shot) return shot.Start;
        return 0;
    }

    private double CurrentShotStart() => ShotStartAt(PendingOrCurrentSeconds);

    /// <summary>
    /// 这一脚的门框。**只认这一脚自己标的**：没标过就是 null，
    /// 绝不沿用上一脚——合集里相邻两脚就是不同机位，沿用等于给错答案。
    /// </summary>
    private GoalFrameCalibration? CalibrationAt(double seconds)
    {
        EnsureVideoCalibrations();
        return _videoCalibrations?.ForShot(ShotStartAt(seconds));
    }

    private bool CurrentShotHasCalibration() => CalibrationAt(PendingOrCurrentSeconds) is not null;

    /// <summary>
    /// 用这一脚手标的四点生成"当前门框"。
    ///
    /// 这里彻底不跑自动检测：单帧检测在合集里经常只认出一根门柱、或者把广告牌当横梁，
    /// 误差比手点四针大得多。每一脚自己标，橙框对不对得上一眼就能看出来。
    /// </summary>
    private void ApplyManualGoalFrame(double seconds)
    {
        if (CalibrationAt(seconds) is not { IsComplete: true } calibration)
        {
            _frameGoalRect = null;
            _frameGoalSource = "";
            return;
        }

        var c = calibration.ImagePoints();
        var minX = c.Min(p => p.X);
        var maxX = c.Max(p => p.X);
        var minY = c.Min(p => p.Y);
        var maxY = c.Max(p => p.Y);
        _frameGoalRect = new GoalRect
        {
            Cx = (minX + maxX) / 2.0,
            Cy = (minY + maxY) / 2.0,
            W = maxX - minX,
            H = maxY - minY,
            // 四点必须原样保留：倾斜机位下用包围盒会把门线压平
            Quad =
            [
                [c[0].X, c[0].Y],
                [c[1].X, c[1].Y],
                [c[2].X, c[2].Y],
                [c[3].X, c[3].Y],
            ],
        };
        _frameGoalSource = "manual";
    }

    /// <summary>标注某一帧的球：用该时刻生效的门框把球的像素换算成球门平面上的落点。</summary>
    private void MarkBall(double videoX, double videoY)
    {
        if (TrainerVideo.Source is null) return;
        // 用"面板上显示的那一帧"而不是 MediaElement 当前落地的位置：
        // seek 是异步的，刚点完 +1帧 就点球的话 Position 还停在前一帧，
        // 标注会落到上一脚上去。读数、脚次、球点必须用同一个时间基准。
        var t = PendingOrCurrentSeconds;
        _ballMark = ((int)Math.Round(t * TrainerFps), t, videoX, videoY);
        DrawBallMark();

        ApplyManualGoalFrame(t);
        RedrawGoalOverlay();
        UpdatePointFromBall();
    }

    private sealed record BallMapping(double Lateral, double Height,
                                      GoalFrameCalibration Calibration,
                                      double? BelowLine, string Source);

    /// <summary>把球的像素换算成落点。返回 null 表示这个时刻还没有可用的门框标定。</summary>
    private BallMapping? MapBallToGoal(double videoX, double videoY, double seconds)
    {
        // 门框只有一种来源：用户手标的四个门角。没有检测、没有预扫描兜底。
        if (CalibrationAt(seconds) is not { IsComplete: true } calibration) return null;

        var belowLine = _frameGoalRect?.BelowLinePx(videoX, videoY);
        var homography = Homography.Solve(calibration.ImagePoints(), GoalFrameCalibration.GoalPoints());
        var mapped = homography?.Map(videoX, videoY);
        if (mapped is null) return null;

        var lateral = TrainerMirrorBox.IsChecked == true ? -mapped.Value.X : mapped.Value.X;
        return new BallMapping(lateral, mapped.Value.Y, calibration, belowLine, "manual");
    }

    private void UpdatePointFromBall()
    {
        if (_ballMark is not { } mark) return;
        var mapped = MapBallToGoal(mark.X, mark.Y, mark.T);
        if (mapped is null)
        {
            _trainerPendingPoint = null;
            _pendingCalibration = null;
            TrainerPointText.Text = "本脚还没有门框标定，落点算不出来——先做第 ① 步。";
            return;
        }

        _trainerPendingPoint = (mapped.Lateral, mapped.Height);
        _pendingCalibration = mapped.Calibration;

        var zone = GoalGeometry.ZoneKey(mapped.Lateral, mapped.Height);
        var onTarget = Math.Abs(mapped.Lateral) <= GoalGeometry.HalfWidth
                       && mapped.Height >= 0 && mapped.Height <= GoalGeometry.Height;

        var lines = new List<string>
        {
            $"第 {mark.Frame} 帧 · 横向 {mapped.Lateral:+0.00;-0.00} m · 高度 {mapped.Height:0.00} m",
            onTarget ? $"落点：{ZoneLabel(zone)}（已换算到门平面）" : "落点：门框外",
        };
        if (mapped.BelowLine is { } below)
        {
            lines.Add(below > 0
                ? $"球还在门线前 {below:F0}px，往前拖到过线帧再点"
                : $"已越过门线 {-below:F0}px");
        }

        TrainerPointText.Text = string.Join("\n", lines);
        DrawBallMark();
    }

    /// <summary>
    /// 橙框只画在"点这四个门角的那一帧"上。
    ///
    /// 之前是按**脚**画：这一脚的每一帧都套一个橙框。合集里一脚好几秒、上百帧，
    /// 结果就是满屏橙框，看起来像标定串到别的脚上去了，也根本没法判断框对不对。
    /// 标定是对一帧画的，就只在那一帧画。
    /// </summary>
    private bool ShouldShowGoalOverlay()
    {
        if (CalibrationAt(PendingOrCurrentSeconds) is not { IsComplete: true } calibration) return false;
        // 正在标这一脚：把已有的框显示出来当参照，好判断这次点得准不准
        if (_trainerCalibrating) return true;
        // 老数据没记帧号，不敢猜它属于哪一帧，干脆不画
        if (!calibration.HasFrame) return false;
        return Math.Abs(PendingOrCurrentSeconds - calibration.FrameSeconds) <= 0.5 / TrainerFps;
    }

    /// <summary>把当前帧的门框画到画面上：橙框是门框，红线是门线。</summary>
    private void RedrawGoalOverlay()
    {
        foreach (var child in TrainerOverlay.Children.Cast<UIElement>().ToList())
        {
            if (child is FrameworkElement { Tag: "goal" }) TrainerOverlay.Children.Remove(child);
        }

        if (_frameGoalRect is not { } rect || TrainerVideo.Source is null)
        {
            TrainerGoalFlagText.Text = "门框未标定";
            return;
        }

        if (!ShouldShowGoalOverlay())
        {
            // 这一脚标过，但不是这一帧——明确说出来，免得用户以为标定丢了
            TrainerGoalFlagText.Text = CalibrationAt(PendingOrCurrentSeconds) is { HasFrame: true } other
                ? $"门框已标定（第 {other.Frame} 帧）"
                : "门框已标定（老数据，无帧号）";
            return;
        }

        var corners = rect.Corners().Select(VideoToScreen).ToArray();
        var polyline = new System.Windows.Shapes.Polyline
        {
            Stroke = System.Windows.Media.Brushes.Orange,
            StrokeThickness = 2,
            Tag = "goal",
        };
        foreach (var corner in corners) polyline.Points.Add(corner);
        polyline.Points.Add(corners[0]);
        TrainerOverlay.Children.Add(polyline);

        // 门线单独画粗一点：判断过没过线看的就是这条
        TrainerOverlay.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = corners[3].X, Y1 = corners[3].Y,
            X2 = corners[2].X, Y2 = corners[2].Y,
            Stroke = System.Windows.Media.Brushes.Red,
            StrokeThickness = 3,
            Tag = "goal",
        });

        var label = _frameGoalSource switch
        {
            "manual" => "手标四点",
            _ => "来源未知",
        };
        TrainerGoalFlagText.Text = $"门框 {rect.W:F0}×{rect.H:F0}（{label}）";
    }

    private void DrawBallMark()
    {
        foreach (var child in TrainerOverlay.Children.Cast<UIElement>().ToList())
        {
            if (child is FrameworkElement { Tag: "ball" }) TrainerOverlay.Children.Remove(child);
        }
        if (_ballMark is not { } mark || TrainerVideo.Source is null) return;

        var p = VideoToScreen((mark.X, mark.Y));
        const double arm = 13;
        var brush = System.Windows.Media.Brushes.White;
        TrainerOverlay.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = p.X - arm, Y1 = p.Y, X2 = p.X + arm, Y2 = p.Y,
            Stroke = brush, StrokeThickness = 2, Tag = "ball",
        });
        TrainerOverlay.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = p.X, Y1 = p.Y - arm, X2 = p.X, Y2 = p.Y + arm,
            Stroke = brush, StrokeThickness = 2, Tag = "ball",
        });
        var ring = new System.Windows.Shapes.Ellipse
        {
            Width = 16, Height = 16, Stroke = brush, StrokeThickness = 2,
            Fill = System.Windows.Media.Brushes.Transparent, Tag = "ball",
        };
        Canvas.SetLeft(ring, p.X - 8);
        Canvas.SetTop(ring, p.Y - 8);
        TrainerOverlay.Children.Add(ring);
    }

    /// <summary>从回放页跳进来时带着当前门将，用户不用再选一次。</summary>
    public void EnterWith(string? keeperName)
    {
        if (string.IsNullOrWhiteSpace(keeperName)) return;
        _trainerKeeper = keeperName;
        ReloadTrainerKeepers();
        SelectTrainerKeeper(keeperName);
    }

    private void ReloadTrainerKeepers()
    {
        var keepers = TrainerStore.ListKeepers()
            .Select(name => new TrainerKeeperChoice(name, name))
            .ToList();
        TrainerKeeperList.ItemsSource = keepers;
        TrainerKeeperList.SelectedItem = keepers.FirstOrDefault(choice => choice.Key == _trainerKeeper);
        KeeperEmptyHint.Visibility = keepers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_trainerKeeper is null)
        {
            TrainerProgressText.Text = keepers.Count == 0
                ? "门将库为空：在左栏「门将模型」下输入名称并点「新建」，再载入比赛片段。"
                : "在左栏选择一个门将模型，再载入比赛片段开始标注。";
        }
    }

    private void SelectTrainerKeeper(string name)
    {
        _trainerKeeper = name;
        // 逐脚标定是按"门将 + 视频"存的，换门将等于换一个数据集，必须重读
        _videoCalibrationsKey = null;
        EnsureVideoCalibrations();
        ApplyManualGoalFrame(PendingOrCurrentSeconds);
        UpdateCalibrationText();
        RedrawGoalOverlay();
        ReloadTrainerClips();
        RefreshTrainerProgress();
        TrainerHeaderText.Text = name;
        var model = TrainerStore.LoadModel(name);
        _trainerDocument = model;
        ShowTrainerSummary();
        // 换门将等于换一套标注：轨道上的"哪几脚标过"必须跟着重画，
        // 否则会继续显示上一个门将的进度，看着像保存没生效。
        RedrawMarkTrack();
    }

    private void TrainerKeeper_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TrainerKeeperList.SelectedItem is TrainerKeeperChoice choice && choice.Key != _trainerKeeper)
        {
            SelectTrainerKeeper(choice.Key);
        }
    }

    private void CreateKeeper_Click(object sender, RoutedEventArgs e)
    {
        var name = NewKeeperNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            TrainerProgressText.Text = "先输入门将名字。";
            return;
        }

        Directory.CreateDirectory(TrainerStore.ClipsDirectory(name));
        NewKeeperNameBox.Text = "";
        SelectTrainerKeeper(name);
        ReloadTrainerKeepers();
        _vm?.AddLog("info", $"已新建门将训练目录：{name}");
    }

    private void ReloadTrainerClips()
    {
        // 每保存一脚都会重刷这个列表（为了让"（N 条）"跟着变），
        // 不记住选中项的话每标一脚高亮就没了，看起来像"片段被取消了"。
        var selectedPath = (TrainerClipList.SelectedItem as TrainerClipItem)?.Path ?? _trainerClipPath;

        _trainerClips.Clear();
        TrainerClipList.ItemsSource = null;

        // 没选门将也要能把视频列出来，否则"先开视频还是先选门将"会卡住用户
        IReadOnlyList<string> annotated = string.IsNullOrWhiteSpace(_trainerKeeper)
            ? []
            : TrainerStore.ListAnnotatedClips(_trainerKeeper);
        if (_trainerClipFolder is not null && Directory.Exists(_trainerClipFolder))
        {
            foreach (var file in Directory.GetFiles(_trainerClipFolder)
                         .Where(path => _videoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                _trainerClips.Add(file);
            }
        }

        var items = _trainerClips
            .Select(path =>
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var marked = annotated.Count(clip => BelongsToClip(clip, stem));
                return marked > 0
                    ? new TrainerClipItem(path, $"✓ {stem}（{marked} 条）")
                    : new TrainerClipItem(path, $"· {stem}");
            })
            .ToList();

        // 已标注但视频不在本文件夹里的条目也要列出来，否则用户看不到历史标注
        foreach (var clip in annotated.Where(clip => _trainerClips.All(path =>
                     !BelongsToClip(clip, Path.GetFileNameWithoutExtension(path)))))
        {
            items.Add(new TrainerClipItem(null, $"✓ {clip}（仅标注）"));
        }

        TrainerClipList.ItemsSource = items;
        // 空列表要有话说：否则"载入文件夹后什么都没有"看起来像按钮没反应
        ClipEmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClipEmptyHint.Text = _trainerClipFolder is null
            ? "未载入片段：点「载入…」选文件夹"
            : "这个文件夹里没有视频文件";

        // 恢复选中项。用一个标志挡住 SelectionChanged：否则会再走一次 LoadTrainerVideo，
        // 把刚标完球的位置和提示语都清掉。
        if (selectedPath is not null)
        {
            var restored = items.FirstOrDefault(item =>
                string.Equals(item.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (restored is not null)
            {
                _restoringClipSelection = true;
                try
                {
                    TrainerClipList.SelectedItem = restored;
                }
                finally
                {
                    _restoringClipSelection = false;
                }
            }
        }
    }

    /// <summary>
    /// 标注是否属于某个视频文件：单片段标注就是视频名本身，
    /// 合集视频的标注形如 "视频名 @ 12s"，因此按 "视频名 @ " 前缀归属。
    /// </summary>
    private static bool BelongsToClip(string annotation, string stem) =>
        annotation.Equals(stem, StringComparison.OrdinalIgnoreCase)
        || annotation.StartsWith(stem + " @ ", StringComparison.OrdinalIgnoreCase);

    private string? _trainerClipFolder;
    private bool _restoringClipSelection;
    private static readonly string[] _videoExtensions = [".mp4", ".mov", ".avi", ".mkv", ".wmv"];

    private void OpenClips_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择存放比赛片段的文件夹",
            InitialDirectory = VideosFolder(),
        };
        if (dialog.ShowDialog(_ownerWindow) != true) return;
        _trainerClipFolder = dialog.FolderName;
        ReloadTrainerClips();
        RefreshTrainerProgress();
        if (_trainerClips.Count == 0)
        {
            TrainerProgressText.Text = $"这个文件夹里没有 mp4/mov/avi/mkv/wmv：{dialog.FolderName}";
        }
    }

    /// <summary>示例素材目录（data/external/videos）；不存在时退回工作区根目录。</summary>
    private string VideosFolder()
    {
        var folder = Path.Combine(TrainerStore.WorkspaceRoot, "data", "external", "videos");
        return Directory.Exists(folder) ? folder : TrainerStore.WorkspaceRoot;
    }

    private void OpenSampleClips_Click(object sender, RoutedEventArgs e)
    {
        var folder = VideosFolder();
        _trainerClipFolder = folder;
        ReloadTrainerClips();
        RefreshTrainerProgress();
        TrainerProgressText.Text = _trainerClips.Count > 0
            ? $"已载入 {_trainerClips.Count} 个片段：{folder}"
            : $"目录里没有视频：{folder}";
    }

    private void TrainerClip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringClipSelection) return;
        if (TrainerClipList.SelectedItem is not TrainerClipItem item) return;
        _trainerPendingPoint = null;
        TrainerPointText.Text = "拖到球过线帧，点一下球心。";

        if (item.Path is null || !File.Exists(item.Path))
        {
            TrainerProgressText.Text = $"{item.Label}：源视频不在当前文件夹，只能查看已有标注。";
            StopTrainerVideo();
            TrainerVideo.Source = null;
            ViewerClipText.Text = "仅标注数据";
            ViewerHintText.Visibility = Visibility.Visible;
            return;
        }

        LoadTrainerVideo(item.Path);
    }

    private void LoadTrainerVideo(string path)
    {
        var sameFile = string.Equals(_trainerClipPath, path, StringComparison.OrdinalIgnoreCase);
        _trainerCalibrating = false;
        _calibrationClicks.Clear();
        TrainerOverlay.Children.Clear();
        if (sameFile)
        {
            // 同一个文件重选时保持播放头，别把标到一半的位置跳回开头
            UpdateCalibrationText();
            RedrawGoalOverlay();
            DrawBallMark();
            return;
        }

        // 换了视频，上一段视频的门框和已点的球都不再适用
        ResetBallMark();
        TrainerOverlay.Children.Clear();
        _trainerClipPath = path;
        // 角标先给文件名：分辨率要等管线打开才知道，MediaOpened 里再补全
        ViewerClipText.Text = Path.GetFileNameWithoutExtension(path);
        ViewerHintText.Visibility = Visibility.Collapsed;

        // 换片后先按"续标"处理：已标过的会自动落到第一脚没标的，全新的则从头开始
        _resumeToNextShot = true;
        _shotList = null;
        _shotNav = [];
        _shotListFile = null;

        // 逐脚标定按"门将 + 视频"存；旧视频的脚次表必须已经清掉，
        // 否则 ShotStartAt 会拿上一段视频的脚次起点去查标定。
        _videoCalibrationsKey = null;
        EnsureVideoCalibrations();
        ApplyManualGoalFrame(0);
        try
        {
            TrainerVideo.Source = new Uri(path, UriKind.Absolute);
            TrainerVideo.Position = TimeSpan.Zero;
            // 先 Play 让解码器真正打开文件；暂停放到 MediaOpened 里做，
            // 否则文件还没打开就 Pause，画面会一直黑着。
            TrainerVideo.Play();
            _trainerPlaying = false;
            TrainerProgressText.Text = "正在载入视频…";
            _vm?.AddLog("info", $"门将训练：载入视频 {path}");
        }
        catch (Exception ex)
        {
            TrainerProgressText.Text = $"无法打开视频：{ex.Message}";
            _vm?.AddLog("error", $"门将训练：打开视频失败 {path}：{ex.Message}");
        }

        UpdateCalibrationText();
        RedrawGoalOverlay();
        DrawBallMark();
        UpdateShotText();

        // 脚次表可能要扫 30 秒，放到后台跑，别挡住视频载入
        _ = EnsureShotListAsync(path);
        _ = EnsureTimelineThumbsAsync(path);
    }

    private void TrainerVideo_MediaOpened(object sender, RoutedEventArgs e)
    {
        // 新的管线刚打开：之前挂着的定位/看门狗全部作废，
        // 否则看门狗会把"重载过程中读到的旧位置"误判成卡死，反复重载。
        _pendingSeek = null;
        _seekWatch = null;
        _seekSettleAt = null;
        _lastSeekAppliedAt = DateTime.MinValue;

        // 重载视频后回到原来那一帧，而不是跳回开头
        if (_reloadSeekSeconds is { } resume)
        {
            _reloadSeekSeconds = null;
            TrainerVideo.Position = TimeSpan.FromSeconds(resume);
            TrainerVideo.Pause();
            _trainerPlaying = false;
        }
        else if (!_trainerPlaying)
        {
            // 打开后停在第一帧，等用户自己定位
            TrainerVideo.Position = TimeSpan.Zero;
            TrainerVideo.Pause();
        }

        if (TrainerVideo.NaturalVideoWidth > 0)
        {
            var duration = TrainerVideo.NaturalDuration.HasTimeSpan
                ? TrainerVideo.NaturalDuration.TimeSpan.ToString(@"m\:ss")
                : "未知";
            TrainerProgressText.Text = $"视频 {TrainerVideo.NaturalVideoWidth}×{TrainerVideo.NaturalVideoHeight}，" +
                                       $"时长 {duration}";
            // 角标把"这是哪段、多大"钉在画面上，截图沟通时不用再问一遍
            ViewerClipText.Text = $"{Path.GetFileNameWithoutExtension(_trainerClipPath ?? string.Empty)}　" +
                                  $"{TrainerVideo.NaturalVideoWidth}×{TrainerVideo.NaturalVideoHeight}";
            ViewerHintText.Visibility = Visibility.Collapsed;
        }

        _vm?.AddLog("info", $"门将训练：视频已打开 {TrainerVideo.NaturalVideoWidth}x{TrainerVideo.NaturalVideoHeight}");

        UpdatePlayButton();
        UpdateTimeText();
        SyncTimelineFromVideo();
        RedrawMarkTrack();
        RedrawGoalOverlay();
        DrawBallMark();
        UpdateShotText();

        // 脚次表已经在缓存里的话，这里就能直接落到"第一脚还没标的"；
        // 还在扫的话，扫描结束后 EnsureShotListAsync 会自己再调一次。
        TryResumeToNextShot();

        // 时长和视口都要等布局落定才知道，缩放初值放到下一轮消息里算
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            ResetZoomForNewClip();
            EnsurePlayheadVisible();
        }));
    }

    private void TrainerVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        var message = e.ErrorException?.Message ?? "未知错误";
        TrainerProgressText.Text = $"视频加载失败：{message}";
        ViewerClipText.Text = "画面加载失败";
        ViewerHintText.Visibility = Visibility.Visible;
        _vm?.AddLog("error", $"门将训练：视频加载失败 {_trainerClipPath}：{message}");
    }

    /// <summary>
    /// 重载当前视频。WPF 的 MediaElement 偶尔会把整条管线卡死
    /// （画面停在旧帧、Position 读回归零），除了重建 Source 没有别的解法。
    /// </summary>
    private void ReloadTrainerVideo_Click(object sender, RoutedEventArgs e)
    {
        if (_trainerClipPath is null || !File.Exists(_trainerClipPath))
        {
            TrainerProgressText.Text = "当前没有可重载的视频。";
            return;
        }

        _reloadSeekSeconds = PendingOrCurrentSeconds;
        StopTrainerVideo();
        // 先摘掉 Source，等一轮消息再挂回去，否则 WPF 可能直接复用旧的管线
        TrainerVideo.Source = null;
        var path = _trainerClipPath;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try
            {
                TrainerVideo.Source = new Uri(path, UriKind.Absolute);
                TrainerVideo.Play();
                _trainerPlaying = false;
                UpdatePlayButton();
                TrainerProgressText.Text = "正在重载视频…";
            }
            catch (Exception ex)
            {
                TrainerProgressText.Text = $"重载失败：{ex.Message}";
            }
        }));
    }

    private void StopTrainerVideo()
    {
        try
        {
            var wasPlaying = _trainerPlaying;
            var position = PendingOrCurrentSeconds;
            TrainerVideo.Pause();
            _trainerPlaying = false;
            // 只有从"正在播放"切到暂停时才需要回压一次位置（WPF 的 Pause 偶尔不刷新画面）。
            // 本来就在暂停态还写 Position 的话，等于每次点「+1帧」都多压一次 seek——
            // 连点十下就是二十次 seek，管线必然被打满。
            if (wasPlaying && TrainerVideo.Source is not null)
            {
                TrainerVideo.Position = TimeSpan.FromSeconds(position);
            }
        }
        catch
        {
            // 播放器已卸载时忽略
        }
        UpdatePlayButton();
    }

    private void TrainerVideo_MediaEnded(object sender, RoutedEventArgs e)
    {
        _trainerPlaying = false;
        UpdatePlayButton();
    }

    /// <summary>播放键的文字跟着播放状态走：按下去之前就该知道会发生什么。</summary>
    private void UpdatePlayButton()
    {
        if (PlayButton is null) return;
        PlayButton.Content = _trainerPlaying ? "暂停" : "播放";
    }

    private void TogglePlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (TrainerVideo.Source is null) return;
        if (_trainerPlaying)
        {
            StopTrainerVideo();
        }
        else
        {
            // 播放前把还没落地的定位压下去，否则一按播放就被节拍器拽走
            FlushPendingSeek();
            TrainerVideo.Play();
            _trainerPlaying = true;
        }
        UpdatePlayButton();
    }

    // 25fps 每帧 40ms：球过线只有一两帧，必须能逐帧定位
    private void StepBack_Click(object sender, RoutedEventArgs e) => StepTrainerVideo(-40);
    private void StepForward_Click(object sender, RoutedEventArgs e) => StepTrainerVideo(40);
    private void JumpBack_Click(object sender, RoutedEventArgs e) => StepTrainerVideo(-1000);
    private void JumpForward_Click(object sender, RoutedEventArgs e) => StepTrainerVideo(1000);
    private void JumpBack5_Click(object sender, RoutedEventArgs e) => StepTrainerVideo(-5000);
    private void JumpForward5_Click(object sender, RoutedEventArgs e) => StepTrainerVideo(5000);

    // ── 按帧号定位 ────────────────────────────────────────────────────────
    // 检测结果、日志、与同事沟通时给的都是"第 1247 帧"这种绝对帧号，
    // 靠连点 ±1 帧摸过去不现实，必须能直接输入。
    private void JumpToFrame_Click(object sender, RoutedEventArgs e) => JumpToTypedFrame();

    private void FrameJumpBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        JumpToTypedFrame();
        e.Handled = true;
    }

    private void JumpToTypedFrame()
    {
        if (TrainerVideo.Source is null)
        {
            TrainerProgressText.Text = "先载入片段，再按帧定位。";
            return;
        }

        // 允许带上"帧 1247"这种粘贴来的前缀，只取数字
        var digits = new string((FrameJumpBox.Text ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var frame))
        {
            TrainerProgressText.Text = "帧号填整数（0 到总帧数之间）。";
            return;
        }

        var duration = VideoDurationSeconds();
        var total = duration > 0 ? (int)Math.Round(duration * TrainerFps) : int.MaxValue;
        frame = Math.Clamp(frame, 0, total);
        StopTrainerVideo();
        SeekTrainer(frame / TrainerFps);
        SyncTimelineFromVideo();
        FrameJumpBox.Text = frame.ToString();
    }

    /// <summary>
    /// 上一脚 / 下一脚。有脚次表就按脚次走（第一次通标时这才是唯一有用的导航），
    /// 没扫出脚次表时退回"在已标注的点之间跳"。
    /// </summary>
    private void PrevMark_Click(object sender, RoutedEventArgs e) => JumpToAdjacentMark(-1);
    private void NextMark_Click(object sender, RoutedEventArgs e) => JumpToAdjacentMark(1);

    private void JumpToAdjacentMark(int direction)
    {
        if (TrainerVideo.Source is null) return;
        var current = PendingOrCurrentSeconds;

        if (_shotNav.Count > 0)
        {
            var shot = NeighbourShot(direction);
            if (shot is null)
            {
                TrainerProgressText.Text = direction > 0 ? "已经是最后一脚了。" : "已经是第一脚了。";
                return;
            }
            SeekTrainer(shot.Key);
            SyncTimelineFromVideo();
            UpdateShotText(shot);
            return;
        }

        if (string.IsNullOrWhiteSpace(_trainerKeeper)) return;
        var marks = TrainerStore.LoadAnnotations(_trainerKeeper)
            .Select(a => a.VideoSeconds)
            .Where(t => t > 0)
            .OrderBy(t => t)
            .ToList();
        if (marks.Count == 0)
        {
            TrainerProgressText.Text = "脚次表尚未生成，本段暂无标注记录。";
            return;
        }

        // 0.2s 容差：正好停在某一脚上时，再点"下一脚"应当去下一脚而不是原地不动
        double? target = null;
        if (direction > 0)
        {
            foreach (var t in marks)
            {
                if (t > current + 0.2) { target = t; break; }
            }
        }
        else
        {
            for (var i = marks.Count - 1; i >= 0; i--)
            {
                if (marks[i] < current - 0.2) { target = marks[i]; break; }
            }
        }

        if (target is null) return;
        SeekTrainer(target.Value);
        UpdateTimeText();
        SyncTimelineFromVideo();
    }

    // ── 脚次表：扫描 / 展开 / 续标 ────────────────────────────────────────

    /// <summary>
    /// 载入（必要时先生成）当前视频的脚次表。缓存命中同步读；没有就后台跑镜头切换扫描，
    /// 8064 帧约 30 秒，期间界面照常可用（只是「下一脚」暂时退回旧逻辑）。
    /// </summary>
    private async Task EnsureShotListAsync(string path)
    {
        // 同一段视频已经在扫了就别重入；换到别的视频则允许另起一次，
        // 旧任务跑完会因为在结尾比对 _trainerClipPath 而自动作废。
        if (string.Equals(_scanningPath, path, StringComparison.OrdinalIgnoreCase)) return;
        if (string.Equals(_shotListFile, path, StringComparison.OrdinalIgnoreCase) && _shotNav.Count > 0) return;

        _scanningPath = path;
        _shotScanRunning = true;
        _shotListFile = path;
        _shotList = null;
        _shotNav = [];
        UpdateShotText();
        try
        {
            var workspace = TrainerStore.WorkspaceRoot;
            var cached = ShotListStore.Load(workspace, path);
            if (cached is null) cached = await ScanShotListAsync(path);
            _shotList = cached;
            _shotNav = BuildNavigation(cached);
            if (cached is not null)
            {
                AppLogger.Write("INFO", $"脚次表：{cached.Shots.Count} 段 → 导航 {_shotNav.Count} 脚" +
                                        (cached.Shots.Count > 0 ? $"（阈值 {cached.Threshold}）" : ""));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("WARN", $"脚次表载入失败：{ex.Message}");
        }
        finally
        {
            if (string.Equals(_scanningPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _scanningPath = null;
                _shotScanRunning = false;
            }
        }

        // 扫描期间用户可能已经换了视频，别把结果画到别的片段上
        if (!string.Equals(_trainerClipPath, path, StringComparison.OrdinalIgnoreCase)) return;

        RedrawMarkTrack();
        UpdateShotText();
        // 脚次表到了之后，"这一脚"才有确定的起点，门框要按新的起点重查一遍
        ApplyManualGoalFrame(PendingOrCurrentSeconds);
        RedrawGoalOverlay();
        UpdateCalibrationText();
        if (_resumeToNextShot) TryResumeToNextShot();
    }

    /// <summary>跑一次镜头切换扫描。脚本自己写缓存文件，这里只负责触发和读回。</summary>
    /// <summary>右栏「本片段」的四个读数：素材是什么、切成几脚、已标几脚。</summary>
    private void UpdateClipFacts()
    {
        if (ClipResolutionText is null) return;
        var duration = VideoDurationSeconds();
        var width = TrainerVideo.NaturalVideoWidth;
        var height = TrainerVideo.NaturalVideoHeight;

        ClipResolutionText.Text = width > 0 ? $"{width}×{height}" : "—";
        ClipDurationText.Text = duration > 0
            ? $"{TimeSpan.FromSeconds(duration):m\\:ss}"
            : "—";
        ClipShotsText.Text = _shotNav.Count > 0
            ? $"{_shotNav.Count} 脚"
            : _shotScanRunning ? "扫描中…" : "—";
        var marked = string.IsNullOrWhiteSpace(_trainerKeeper) ? 0 : AnnotationsForCurrentVideo().Count;
        ClipMarkedText.Text = marked > 0 ? $"{marked} 脚" : "—";
    }

    /// <summary>
    /// 时间轴缩略图：用 python 抽一批等间隔小图铺到轨道上（与脚次表一样按视频缓存）。
    /// 抽帧是解码 + 落盘的活，不能放 UI 线程；失败就算了——
    /// 轨道会退化成"分段 + 状态条"，定位仍然可用，只是少了一层视觉参照。
    /// </summary>
    private async Task EnsureTimelineThumbsAsync(string path)
    {
        if (string.Equals(_thumbKey, path, StringComparison.OrdinalIgnoreCase) && _timelineThumbs.Count > 0) return;
        _thumbKey = path;
        _timelineThumbs.Clear();
        ApplyTimelineThumbnails();

        if (App.Services.GetService(typeof(IPythonEngine)) is not IPythonEngine engine)
        {
            AppLogger.Write("WARN", "时间轴缩略图：取不到 Python 引擎，无法定位缩略图缓存目录");
            return;
        }

        var script = Path.Combine(engine.WorkspaceDir, "tools", "goal_detect", "timeline_thumbs.py");
        var folder = Path.Combine(engine.WorkspaceDir, "data", "goal_detect", "thumbs",
            Path.GetFileNameWithoutExtension(path));

        try
        {
            // 已经抽过就直接读盘：重新打开同一段视频不该再等一遍解码
            if (!HasCachedThumbnails(folder))
            {
                if (!File.Exists(script) || !File.Exists(engine.PythonExePath))
                {
                    AppLogger.Write("WARN",
                        $"时间轴缩略图：抽帧脚本或解释器缺失（script={File.Exists(script)}, " +
                        $"python={File.Exists(engine.PythonExePath)}）");
                    return;
                }

                var args = $"--video \"{path}\" --workspace \"{engine.WorkspaceDir}\"";
                var (exitCode, _, error) = await engine.RunAsync(script, args, 600000);
                if (exitCode != 0)
                {
                    AppLogger.Write("WARN", $"时间轴缩略图生成失败：{error.Trim()}");
                    return;
                }
            }

            // 生成期间用户可能已经换片：结果不能贴到别的视频上
            if (!string.Equals(_trainerClipPath, path, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Write("INFO", $"时间轴缩略图：期间已换片，丢弃 {Path.GetFileName(path)} 的结果");
                return;
            }

            if (!Directory.Exists(folder))
            {
                AppLogger.Write("WARN", $"时间轴缩略图：缓存目录不存在 {folder}");
                return;
            }

            var files = Directory.GetFiles(folder, "t*.jpg")
                .OrderBy(file => file, StringComparer.Ordinal)
                .ToList();
            if (files.Count == 0)
            {
                AppLogger.Write("WARN", $"时间轴缩略图：缓存目录里没有图 {folder}");
                return;
            }

            foreach (var file in files)
            {
                if (LoadThumbnail(file) is { } image) _timelineThumbs.Add(image);
            }

            AppLogger.Write("INFO", $"时间轴缩略图：载入 {_timelineThumbs.Count} 张（{Path.GetFileName(folder)}）");
        }
        catch (Exception ex)
        {
            AppLogger.Write("WARN", $"时间轴缩略图异常：{ex.Message}");
            return;
        }

        // 直接喂图，不走 RedrawMarkTrack：那条路上有门将/脚次的前置条件和重入锁，
        // 缩略图不该被它们连坐。脚次本身在 EnsureShotListAsync 里会单独刷新。
        ApplyTimelineThumbnails();
    }

    private static bool HasCachedThumbnails(string folder) =>
        Directory.Exists(folder) && Directory.GetFiles(folder, "t*.jpg").Length >= 8;

    private static System.Windows.Media.ImageSource? LoadThumbnail(string file)
    {
        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(file, UriKind.Absolute);
            // 轨道高 34px，按 44 解码就够，再大只是白占内存
            image.DecodePixelHeight = 44;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            AppLogger.Write("WARN", $"缩略图读取失败 {Path.GetFileName(file)}：{ex.Message}");
            return null;
        }
    }

    private async Task<ShotListDocument?> ScanShotListAsync(string path)
    {
        if (App.Services.GetService(typeof(IPythonEngine)) is not IPythonEngine engine) return null;
        var script = Path.Combine(engine.WorkspaceDir, "tools", "goal_detect", "shot_list.py");
        if (!File.Exists(engine.PythonExePath) || !File.Exists(script))
        {
            _vm?.AddLog("warn", "脚次扫描：找不到 shot_list.py 或 Python，改为按固定步长前进");
            return null;
        }

        TrainerProgressText.Text = "正在扫脚次（按镜头切换，约 30 秒，每段视频只扫一次）…";
        try
        {
            var args = $"--video \"{path}\" --workspace \"{engine.WorkspaceDir}\"";
            var (exitCode, _, error) = await engine.RunAsync(script, args, 600000);
            if (exitCode != 0)
            {
                _vm?.AddLog("warn", $"脚次扫描失败：{error.Trim()}");
                return null;
            }
            return ShotListStore.Load(engine.WorkspaceDir, path);
        }
        catch (Exception ex)
        {
            _vm?.AddLog("warn", $"脚次扫描异常：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 镜头切换给出的段并不总是一脚一段：两脚之间没有硬切时会连成 10~27 秒的长段。
    /// 把长段按 4 秒再切开，导航粒度才跟"一脚一次射门"对得上。
    /// </summary>
    private static List<ShotSpan> BuildNavigation(ShotListDocument? document)
    {
        if (document is null || document.Shots.Count == 0) return [];
        var navigation = new List<ShotSpan>();
        foreach (var shot in document.Shots)
        {
            var length = shot.Length;
            if (length <= LongShotSeconds)
            {
                navigation.Add(new ShotSpan
                {
                    Index = navigation.Count + 1,
                    Start = shot.Start,
                    End = shot.End,
                    Key = shot.Key,
                    Frames = shot.Frames,
                });
                continue;
            }

            var parts = Math.Max(2, (int)Math.Round(length / VirtualShotSeconds));
            var step = length / parts;
            for (var i = 0; i < parts; i++)
            {
                var start = shot.Start + i * step;
                var end = i == parts - 1 ? shot.End : start + step;
                navigation.Add(new ShotSpan
                {
                    Index = navigation.Count + 1,
                    Start = start,
                    End = end,
                    // 虚拟脚次没有真实切点，落在后 1/4 处（射门通常在镜头末尾）
                    Key = start + (end - start) * 0.75,
                    Frames = 0,
                });
            }
        }

        return navigation;
    }

    /// <summary>当前播放头属于第几脚；不在任何一脚里时返回 null。</summary>
    private ShotSpan? CurrentShot()
    {
        if (_shotNav.Count == 0 || TrainerVideo.Source is null) return null;
        return ShotListStore.FindAt(_shotNav, PendingOrCurrentSeconds);
    }

    private void UpdateShotText(ShotSpan? shot = null)
    {
        if (TrainerShotText is null) return;
        if (TrainerVideo.Source is null)
        {
            TrainerShotText.Text = "";
            return;
        }
        if (_shotScanRunning && _shotNav.Count == 0)
        {
            TrainerShotText.Text = "脚次：扫描中…";
            return;
        }
        if (_shotNav.Count == 0)
        {
            TrainerShotText.Text = "脚次：未扫描（按 4 秒步进）";
            return;
        }

        shot ??= CurrentShot();
        var marked = AnnotationsForCurrentVideo().Count;
        EnsureVideoCalibrations();
        var calibrated = _videoCalibrations?.Count ?? 0;
        // 标注和门框分开报：只标了门框没点球时，"已标 0"会让人以为刚才那四针白点了
        var counts = $"标注 {marked} · 门框 {calibrated} 脚";
        TrainerShotText.Text = shot is null
            ? $"候选 {_shotNav.Count} 脚 · {counts}"
            : $"第 {shot.Index}/{_shotNav.Count} 脚（候选）· {counts}";
    }

    /// <summary>本视频已有的标注（按秒排序）。合集里几十脚共用一段视频，必须按视频过滤。</summary>
    private List<KeeperClipAnnotation> AnnotationsForCurrentVideo()
    {
        if (string.IsNullOrWhiteSpace(_trainerKeeper) || _trainerClipPath is null) return [];
        var key = $"{_trainerKeeper}|{_trainerClipPath}|{_annotationVersion}";
        if (_videoAnnotationCache is not null && _videoAnnotationCacheKey == key)
            return _videoAnnotationCache;

        var stem = Path.GetFileNameWithoutExtension(_trainerClipPath);
        var annotations = TrainerStore.LoadAnnotations(_trainerKeeper)
            .Where(annotation => BelongsToClip(annotation.Clip, stem))
            .OrderBy(annotation => annotation.VideoSeconds)
            .ToList();
        _videoAnnotationCache = annotations;
        _videoAnnotationCacheKey = key;
        return annotations;
    }

    /// <summary>
    /// 第一脚还没标过的脚次。判定用"落在这一脚的时间区间里"，
    /// 而不是跟跳转落点比距离——用户是在切点之后若干帧才点球的，差个一两秒很正常。
    /// </summary>
    private ShotSpan? FirstUnannotatedShot(IReadOnlyList<KeeperClipAnnotation> existing)
    {
        foreach (var shot in _shotNav)
        {
            var marked = existing.Any(annotation =>
                annotation.VideoSeconds >= shot.Start - 0.3
                && annotation.VideoSeconds <= shot.End + 0.3);
            if (!marked) return shot;
        }
        return null;
    }

    /// <summary>重新打开同一段视频时，接着上次标到的地方往下标，而不是从头再来。</summary>
    private bool TryResumeToNextShot()
    {
        if (!_resumeToNextShot || _shotNav.Count == 0 || TrainerVideo.Source is null) return false;
        _resumeToNextShot = false;

        var existing = AnnotationsForCurrentVideo();
        if (existing.Count == 0) return false;

        var target = FirstUnannotatedShot(existing);
        if (target is null)
        {
            TrainerProgressText.Text = $"这段视频的 {existing.Count} 脚都标过了。可以点「训练」重算门将模型。";
            return false;
        }

        SeekTrainer(target.Key);
        SyncTimelineFromVideo();
        UpdateShotText(target);
        TrainerProgressText.Text =
            $"续标：从第 {target.Index} 脚继续（本视频已标 {existing.Count} 脚，共 {_shotNav.Count} 脚）。";
        return true;
    }

    /// <summary>
    /// 把脚次表和标注状态喂给时间轴。
    ///
    /// 轨道要同时回答两个问题：这段视频切成哪几脚、哪几脚已经标过。
    /// 只画"已标注"的刻度是不够的——第一次通标时一条都没有，轨道看起来就是空的；
    /// 只有「切镜分段 + 每段的状态条」才能让人知道还剩哪几脚没做。
    /// </summary>
    private bool _redrawingMarks;
    private readonly List<System.Windows.Media.ImageSource> _timelineThumbs = [];
    private string? _thumbKey;

    private void RedrawMarkTrack()
    {
        // 会被布局和异步加载反复调用：异常从这里冒出去是直接杀进程的，所以自己兜住并挡重入
        if (_redrawingMarks) return;
        _redrawingMarks = true;
        try
        {
            // 没选门将不代表轨道该是空的：缩略图只跟视频有关，
            // 脚次分段只跟脚次表有关，两者都不依赖"标的是谁"。
            // 这里以前会直接 return，结果整条轨道只剩一条看不出内容的黑带。
            var annotations = AnnotationsForCurrentVideo();
            EnsureVideoCalibrations();
            var shots = _shotNav.Select(shot =>
            {
                var annotation = annotations.FirstOrDefault(a =>
                    a.VideoSeconds >= shot.Start - SameShotTolerance
                    && a.VideoSeconds <= shot.End + SameShotTolerance);
                return new TimelineShotMark
                {
                    Index = shot.Index,
                    Start = shot.Start,
                    End = shot.End,
                    Marked = annotation is not null,
                    Saved = annotation?.Saved == true,
                    Calibrated = _videoCalibrations?.Has(shot.Start) == true,
                };
            }).ToList();

            ApplyTimelineThumbnails();
            Timeline.Shots = shots;
            UpdateClipFacts();
        }
        catch (Exception ex)
        {
            _vm?.AddLog("warn", $"时间轴绘制失败：{ex.Message}");
        }
        finally
        {
            _redrawingMarks = false;
        }
    }

    /// <summary>
    /// 只把「时长 + 缩略图」喂给轨道，不碰脚次、不碰门将。
    ///
    /// 这三样东西的生命周期完全不同：时长来自播放器（MediaOpened 才有），
    /// 缩略图来自后台抽帧（可能要几十秒），脚次来自扫描脚本和标注数据。
    /// 以前它们全挤在 RedrawMarkTrack 里一起下发，任何一个前置条件不满足
    /// （比如还没选门将就提前 return），缩略图就跟着一起丢了，
    /// 表现成"刻度、播放头、读数都对，唯独胶片带全黑"。
    /// </summary>
    private void ApplyTimelineThumbnails()
    {
        Timeline.Duration = VideoDurationSeconds();
        Timeline.Thumbnails = _timelineThumbs;
    }

    /// <summary>面板上的键盘操作：逐帧标注不该逼用户去点按钮。</summary>
    private void TrainerRoot_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsVisible) return;
        // 正在输入门将名字时，方向键要留给文本框
        if (Keyboard.FocusedElement is TextBox) return;

        var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 1000 : 40;
        switch (e.Key)
        {
            case Key.Left:
                StepTrainerVideo(-step);
                e.Handled = true;
                break;
            case Key.Right:
                StepTrainerVideo(step);
                e.Handled = true;
                break;
            case Key.Up:
                StepTrainerVideo(1000);
                e.Handled = true;
                break;
            case Key.Down:
                StepTrainerVideo(-1000);
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Enter:
                SaveCurrentAnnotation();
                e.Handled = true;
                break;
            case Key.Escape:
                // 正在标门框时，Esc 先退出标定，别直接把整个面板关掉
                if (_trainerCalibrating)
                {
                    _trainerCalibrating = false;
                    _calibrationFrameSeconds = -1;
                    _calibrationClicks.Clear();
                    ClearCalibrationMarkers();
                    UpdateCalibrationText();
                }
                else if (_trainerPendingPoint is not null)
                {
                    // 页面不会被"关掉"，Esc 退到上一级：先撤掉还没保存的球心
                    ResetBallMark();
                }
                e.Handled = true;
                break;
        }
    }

    private void StepTrainerVideo(int milliseconds)
    {
        if (TrainerVideo.Source is null) return;
        StopTrainerVideo();
        // 从"上一次想去的目标"累加，连点时才能一步一步叠上去而不是原地打转
        var target = PendingOrCurrentSeconds + milliseconds / 1000.0;
        if (target < 0) target = 0;
        var duration = VideoDurationSeconds();
        if (duration > 0 && target > duration) target = duration;
        SeekTrainer(target);
    }

    private void UpdateTimeText() => UpdateTimeText(PendingOrCurrentSeconds);

    private void UpdateTimeText(double seconds)
    {
        if (!TrainerVideo.NaturalDuration.HasTimeSpan)
        {
            TrainerTimeText.Text = "";
            return;
        }
        var current = TimeSpan.FromSeconds(Math.Max(0, seconds));
        TrainerTimeText.Text = $"{current:mm\\:ss\\.ff} / {TrainerVideo.NaturalDuration.TimeSpan:mm\\:ss\\.ff}";
    }

    private void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (TrainerVideo.Source is null)
        {
            TrainerCalibrationText.Text = "先载入一个片段再标定门框。";
            return;
        }

        // 标定要落到门将目录里才有意义，否则换一段视频就丢了
        if (string.IsNullOrWhiteSpace(_trainerKeeper))
        {
            TrainerCalibrationText.Text = "先在左边选一个门将，标定结果才有地方保存。";
            return;
        }

        if (ShotListPending)
        {
            TrainerCalibrationText.Text = "正在扫脚次（约 30 秒，每段视频只扫一次）。" +
                                          "扫完才能确定这一脚的起点。";
            return;
        }

        _trainerCalibrating = true;
        _calibrationFrameSeconds = PendingOrCurrentSeconds;
        _calibrationClicks.Clear();
        // 只清掉上一次的标定点，门框和球标记留着当参照
        ClearCalibrationMarkers();
        UpdateCalibrationText();
    }

    private void UpdateCalibrationText()
    {
        var shot = CurrentShot();
        var label = shot is not null && _shotNav.Count > 0
            ? $"第 {shot.Index}/{_shotNav.Count} 脚"
            : "本脚";

        var calibration = CalibrationAt(PendingOrCurrentSeconds);
        UpdateCalibrationDots(_calibrationClicks.Count, calibration is { IsComplete: true });

        if (_trainerCalibrating)
        {
            TrainerCalibrationText.Text = _calibrationClicks.Count switch
            {
                0 => $"{label}：点第 1 个角——左上（左门柱 × 横梁）。",
                1 => $"{label}：点第 2 个角——右上（右门柱 × 横梁）。",
                2 => $"{label}：点第 3 个角——右下（右门柱 × 地面）。",
                _ => $"{label}：点第 4 个角——左下（左门柱 × 地面）。",
            };
            return;
        }

        if (calibration is not { IsComplete: true })
        {
            TrainerCalibrationText.Text = $"{label}未标定门框。点「标定四角」，依次点左上、右上、右下、左下。";
            return;
        }

        var done = _videoCalibrations?.Count ?? 0;
        var progress = _shotNav.Count > 0 ? $" · 全片 {done}/{_shotNav.Count} 脚" : "";
        var frameText = calibration.HasFrame ? $"第 {calibration.Frame} 帧" : "无帧号";
        TrainerCalibrationText.Text =
            $"{label}已标定（{frameText} · {calibration.VideoWidth}×{calibration.VideoHeight}）{progress}。" +
            "橙框只画在标定帧上，要重标先「清除」。";
    }

    /// <summary>
    /// 四个门角的完成进度。标定是"点一针、看一眼"的循环，
    /// 剩下几个角必须一眼可数，不能让用户去数画布上的小橙点。
    /// </summary>
    private void UpdateCalibrationDots(int clicked, bool complete)
    {
        var dots = new[] { CalibDot1, CalibDot2, CalibDot3, CalibDot4 };
        var filled = complete ? dots.Length : Math.Clamp(clicked, 0, dots.Length);
        var primary = (System.Windows.Media.Brush)FindResource("PrimaryBrush");
        var idle = (System.Windows.Media.Brush)FindResource("BorderBrush");
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].Background = i < filled ? primary : System.Windows.Media.Brushes.Transparent;
            dots[i].BorderBrush = i < filled ? primary : idle;
        }
    }

    /// <summary>清掉上一次标定留在画面上的四个小圆点。</summary>
    private void ClearCalibrationMarkers()
    {
        foreach (var child in TrainerOverlay.Children.Cast<UIElement>().ToList())
        {
            if (child is FrameworkElement { Tag: "calib" }) TrainerOverlay.Children.Remove(child);
        }
    }

    /// <summary>
    /// 标定过程中换了帧：已经点的门角属于原来那一帧，必须作废。
    ///
    /// 不作废的话，那几颗橙点会留在画布上不动，用户接着点后两个角，
    /// 结果四个点分别属于两帧——橙框看着像对的，换算出来的落点却是错的。
    /// </summary>
    private void DropCalibrationClicksIfFrameChanged(double seconds)
    {
        if (!_trainerCalibrating) return;
        if (_calibrationFrameSeconds < 0) return;
        if (Math.Abs(seconds - _calibrationFrameSeconds) <= 0.5 / TrainerFps) return;

        _calibrationFrameSeconds = seconds;
        if (_calibrationClicks.Count == 0) return;
        _calibrationClicks.Clear();
        ClearCalibrationMarkers();
        UpdateCalibrationText();
    }

    /// <summary>
    /// 清掉"这一脚"的门框标定：文件里删掉、画面上撤掉橙框和红线。
    ///
    /// 之前只有"再标一次覆盖"，没有任何删除入口，所以标错四个点以后
    /// 那圈橙框就永远挂在画面上——看起来像"清不掉"，其实是根本没地方清。
    /// </summary>
    private void ClearShotCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (TrainerVideo.Source is null) return;
        if (string.IsNullOrWhiteSpace(_trainerKeeper) || _trainerClipPath is null)
        {
            TrainerCalibrationText.Text = "先在左边选一个门将，标定才有地方读写。";
            return;
        }

        var shotStart = CurrentShotStart();
        var shot = ShotListStore.FindAt(_shotNav, shotStart);
        var label = shot is not null && _shotNav.Count > 0
            ? $"第 {shot.Index}/{_shotNav.Count} 脚"
            : "这一脚";

        // 正在标的时候点清除，必须先退出标定状态，否则下一次点击会接着凑第 1、2 个点
        _trainerCalibrating = false;
        _calibrationFrameSeconds = -1;
        _calibrationClicks.Clear();
        ClearCalibrationMarkers();

        var removed = TrainerStore.DeleteVideoCalibration(_trainerKeeper, _trainerClipPath, shotStart);

        // 不管文件里有没有，内存和画面都按"这一脚没有门框"重来
        _videoCalibrationsKey = null;
        EnsureVideoCalibrations();
        ApplyManualGoalFrame(PendingOrCurrentSeconds);
        RedrawGoalOverlay();

        // 这一脚的球点失去门框就换算不出落点，一起撤掉，别留个半成品在待保存里
        _trainerPendingPoint = null;
        _pendingCalibration = null;
        UpdateCalibrationText();
        if (_ballMark is not null) UpdatePointFromBall();
        else TrainerPointText.Text = "本脚门框已清除，重新「标定四角」后再点球。";

        if (!removed)
        {
            TrainerCalibrationText.Text = $"{label}本来就没有门框标定，画面已刷新。";
            return;
        }

        _vm?.AddLog("info",
            $"已清除门框标定：{_trainerKeeper} · {Path.GetFileName(_trainerClipPath)} · " +
            $"{label}（{shotStart:0.00}s 起）");
    }

    /// <summary>
    /// 脚次表还在扫：这时"这一脚"的起点还没确定，标定会存到错误的键上，
    /// 扫完就变成孤儿。所以扫完之前不允许标门框。
    /// </summary>
    private bool ShotListPending => _shotScanRunning && _shotNav.Count == 0;

    /// <summary>
    /// 跳到的这一脚还没标门框时，直接进标定模式。
    /// 80 脚、每脚四针，不能让用户每跳一脚都回头去点一次按钮。
    /// </summary>
    private bool BeginCalibrationIfNeeded()
    {
        if (TrainerVideo.Source is null) return false;
        if (string.IsNullOrWhiteSpace(_trainerKeeper)) return false;
        if (ShotListPending) return false;
        if (_trainerCalibrating || CurrentShotHasCalibration()) return false;

        _trainerCalibrating = true;
        _calibrationFrameSeconds = PendingOrCurrentSeconds;
        _calibrationClicks.Clear();
        ClearCalibrationMarkers();
        UpdateCalibrationText();
        return true;
    }

    private void TrainerOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var point = e.GetPosition(TrainerOverlay);
        var video = ScreenToVideo(point);
        if (video is null) return;

        if (_trainerCalibrating)
        {
            HandleCalibrationClick(video.Value);
            return;
        }

        if (TrainerVideo.Source is null) return;

        // 点哪里就是球心。落点由已标定的门框换算，不再让用户去找"过线的位置"
        MarkBall(video.Value.X, video.Value.Y);
    }

    private void HandleCalibrationClick((double X, double Y) videoPoint)
    {
        _calibrationClicks.Add(videoPoint);
        var marker = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Stroke = System.Windows.Media.Brushes.Orange,
            StrokeThickness = 2,
            Fill = System.Windows.Media.Brushes.Transparent,
            Tag = "calib",
        };
        var screen = VideoToScreen(videoPoint);
        Canvas.SetLeft(marker, screen.X - 5);
        Canvas.SetTop(marker, screen.Y - 5);
        TrainerOverlay.Children.Add(marker);

        if (_calibrationClicks.Count < 4)
        {
            UpdateCalibrationText();
            return;
        }

        var calibration = new GoalFrameCalibration
        {
            VideoWidth = TrainerVideo.NaturalVideoWidth,
            VideoHeight = TrainerVideo.NaturalVideoHeight,
            LeftTopX = _calibrationClicks[0].X, LeftTopY = _calibrationClicks[0].Y,
            RightTopX = _calibrationClicks[1].X, RightTopY = _calibrationClicks[1].Y,
            RightBottomX = _calibrationClicks[2].X, RightBottomY = _calibrationClicks[2].Y,
            LeftBottomX = _calibrationClicks[3].X, LeftBottomY = _calibrationClicks[3].Y,
            // 标定是对**这一帧**画的，帧号一起存下来：
            // 不存的话橙框会被画到这一脚的每一帧上（用户看到的就是"满屏都是框"）
            FrameSeconds = PendingOrCurrentSeconds,
            Frame = (int)Math.Round(PendingOrCurrentSeconds * TrainerFps),
        };

        var homography = Homography.Solve(calibration.ImagePoints(), GoalFrameCalibration.GoalPoints());
        if (homography is null)
        {
            _trainerCalibrating = true;
            _calibrationFrameSeconds = PendingOrCurrentSeconds;
            _calibrationClicks.Clear();
            ClearCalibrationMarkers();
            TrainerCalibrationText.Text = "四个点太接近共线，无法解算。请重新点四个门角。";
            return;
        }

        _trainerCalibrating = false;
        _calibrationFrameSeconds = -1;

        // 键是"这一脚的起点"，不是播放头：同一脚里挪几帧重标，仍然是覆盖这一脚
        var shotStart = CurrentShotStart();
        if (!string.IsNullOrWhiteSpace(_trainerKeeper) && _trainerClipPath is not null)
        {
            TrainerStore.SaveVideoCalibration(_trainerKeeper, _trainerClipPath, shotStart, calibration);
            _videoCalibrationsKey = null;   // 强制重读，让内存里也有这一脚
            EnsureVideoCalibrations();
        }

        ApplyManualGoalFrame(PendingOrCurrentSeconds);
        UpdateCalibrationText();
        // 四个点已经变成橙框了，留着就是重复的视觉噪音
        ClearCalibrationMarkers();
        RedrawGoalOverlay();
        // 标完门框，之前点的球要用新门框重算落点
        UpdatePointFromBall();
        var shot = ShotListStore.FindAt(_shotNav, shotStart);
        TrainerPointText.Text = _trainerPendingPoint is null
            ? $"第 {shot?.Index.ToString() ?? "-"} 脚门框已标定。现在拖到球过线那一帧，点一下球。"
            : TrainerPointText.Text;
        _vm?.AddLog("info",
            $"门框标定已保存：{_trainerKeeper} · {Path.GetFileName(_trainerClipPath)} · " +
            $"第 {shot?.Index.ToString() ?? "-"} 脚（{shotStart:0.00}s 起）" +
            $" ({calibration.VideoWidth}×{calibration.VideoHeight})");
    }

    /// <summary>画布坐标 → 视频原始像素（Stretch=Uniform 的留黑边要扣掉）。</summary>
    private (double X, double Y)? ScreenToVideo(Point point)
    {
        var (scale, offsetX, offsetY) = VideoTransform();
        if (scale <= 0) return null;
        var x = (point.X - offsetX) / scale;
        var y = (point.Y - offsetY) / scale;
        if (x < 0 || y < 0 || x > TrainerVideo.NaturalVideoWidth || y > TrainerVideo.NaturalVideoHeight) return null;
        return (x, y);
    }

    private Point VideoToScreen((double X, double Y) video)
    {
        var (scale, offsetX, offsetY) = VideoTransform();
        return new Point(video.X * scale + offsetX, video.Y * scale + offsetY);
    }

    private (double Scale, double OffsetX, double OffsetY) VideoTransform()
    {
        var vw = (double)TrainerVideo.NaturalVideoWidth;
        var vh = (double)TrainerVideo.NaturalVideoHeight;
        var aw = TrainerOverlay.ActualWidth;
        var ah = TrainerOverlay.ActualHeight;
        if (vw <= 0 || vh <= 0 || aw <= 0 || ah <= 0) return (0, 0, 0);
        var scale = Math.Min(aw / vw, ah / vh);
        return (scale, (aw - vw * scale) / 2, (ah - vh * scale) / 2);
    }

    private void SaveAnnotation_Click(object sender, RoutedEventArgs e) => SaveCurrentAnnotation();

    private void SaveCurrentAnnotation()
    {
        if (string.IsNullOrWhiteSpace(_trainerKeeper))
        {
            TrainerProgressText.Text = "先选一个门将。";
            return;
        }

        if (_trainerPendingPoint is not { } point)
        {
            TrainerPointText.Text = "先在画面上点一下球心，再保存。";
            return;
        }

        var stem = _trainerClipPath is not null
            ? Path.GetFileNameWithoutExtension(_trainerClipPath)
            : Path.GetFileNameWithoutExtension(
                (TrainerClipList.SelectedItem as TrainerClipItem)?.Path ?? "");
        if (string.IsNullOrWhiteSpace(stem))
        {
            TrainerProgressText.Text = "先选一个片段。";
            return;
        }

        // 一段合集视频里有几十脚点球，用"视频名 @ 秒数"区分，否则后一脚会覆盖前一脚。
        // 秒数保留一位小数：两脚之间只差 0.4 秒时，取整会让后一脚直接盖掉前一脚。
        // 用"点球那一刻"的帧时间，而不是当前播放头：点了球之后又拖走时间轴的话，
        // 位置和秒数就会对不上，训练时这一脚会落到错误的脚次上。
        var seconds = _ballMark?.T ?? PendingOrCurrentSeconds;

        // 同一脚重新标（微调了球的位置/改了扑救判定）应当覆盖而不是再存一条，
        // 否则重新核对一遍就会凭空多出几十条重复样本。
        var overwritten = 0;
        foreach (var existing in AnnotationsForCurrentVideo())
        {
            if (Math.Abs(existing.VideoSeconds - seconds) > SameShotTolerance) continue;
            if (TrainerStore.DeleteAnnotation(_trainerKeeper, existing.Clip)) overwritten++;
        }

        var clip = $"{stem} @ {seconds:0.0}s";
        var saved = TrainerSavedRadio.IsChecked == true;
        TrainerStore.SaveAnnotation(_trainerKeeper, new KeeperClipAnnotation
        {
            Clip = clip,
            Lateral = point.Lateral,
            Height = point.Height,
            Saved = saved,
            VideoSeconds = seconds,
            Calibration = _pendingCalibration,
            BallX = _ballMark?.X,
            BallY = _ballMark?.Y,
            FrameIndex = _ballMark?.Frame,
            BelowLinePx = _ballMark is { } mark && _frameGoalRect is { } rect
                ? rect.BelowLinePx(mark.X, mark.Y)
                : null,
            GoalRect = _frameGoalRect,
            GoalRectSource = string.IsNullOrEmpty(_frameGoalSource) ? null : _frameGoalSource,
        });
        InvalidateAnnotationCache();

        var zone = GoalGeometry.ZoneKey(point.Lateral, point.Height);
        ResetBallMark();
        var marked = AnnotationsForCurrentVideo().Count;
        var head = overwritten > 0 ? "已更新" : "已保存";
        TrainerPointText.Text =
            $"{head}：横向 {point.Lateral:F2} m · 高度 {point.Height:F2} m · {ZoneLabel(zone)} · {(saved ? "扑出" : "进球")}";
        _vm?.AddLog("info", $"门将标注：{_trainerKeeper} · {clip} · {ZoneLabel(zone)} · {(saved ? "扑出" : "进球")}" +
                            (overwritten > 0 ? "（覆盖同一脚旧标注）" : ""));

        ReloadTrainerClips();
        RefreshTrainerProgress();

        // 保存后自动进下一脚——80 脚点球不可能每脚都自己去找位置。
        var hadShotList = _shotNav.Count > 0;
        var next = AdvanceToNextShot();
        // 下一脚还没标门框就直接进标定模式：每脚四针，不能让人再回头点一次按钮
        var calibrating = next is not null && BeginCalibrationIfNeeded();
        TrainerPointText.Text += next is not null
            ? calibrating
                ? $"\n已到第 {next.Index} 脚（累计 {marked} 脚），本脚未标门框——先点左上角。"
                : $"\n已到第 {next.Index} 脚（累计 {marked}/{_shotNav.Count} 脚），拖到过线帧再点球。"
            : hadShotList
                ? $"\n候选脚次已走完（累计 {marked} 脚），可以「开始训练」了。"
                : $"\n已前进 {VirtualShotSeconds:0} 秒，继续标下一脚。";
    }

    /// <summary>
    /// 保存后跳到下一脚。有脚次表按脚次走；没有（扫描失败）就按 4 秒步进，
    /// 步长取"一段合集 80 脚"的经验值，至少能落在下一脚附近。
    /// </summary>
    private ShotSpan? AdvanceToNextShot()
    {
        if (TrainerVideo.Source is null) return null;
        var current = PendingOrCurrentSeconds;

        if (_shotNav.Count > 0)
        {
            // 按脚次序号走，不按 Key 走：用户是在切点之前若干帧点球的，
            // 那一脚的 Key 可能还在前面，用 Key 比较会"前进"到同一脚。
            var next = NeighbourShot(1);
            if (next is null) return null;
            SeekTrainer(next.Key);
            SyncTimelineFromVideo();
            UpdateShotText(next);
            return next;
        }

        var duration = VideoDurationSeconds();
        var target = current + VirtualShotSeconds;
        if (duration > 0 && target >= duration - 0.1) return null;
        SeekTrainer(duration > 0 ? Math.Min(target, duration) : target);
        SyncTimelineFromVideo();
        UpdateShotText();
        return null;
    }

    /// <summary>
    /// 当前播放头所在脚次的前一脚 / 后一脚（按序号走，不按 Key）。
    /// 落在脚次之间的空隙里时，退回"按 Key 找最近的一脚"。
    /// </summary>
    private ShotSpan? NeighbourShot(int direction)
    {
        if (_shotNav.Count == 0) return null;
        var seconds = PendingOrCurrentSeconds;
        var current = ShotListStore.FindAt(_shotNav, seconds);
        if (current is null)
        {
            return direction > 0
                ? ShotListStore.Next(_shotNav, seconds)
                : ShotListStore.Previous(_shotNav, seconds);
        }

        var index = current.Index - 1 + direction;   // Index 是 1-based
        return index >= 0 && index < _shotNav.Count ? _shotNav[index] : null;
    }

    /// <summary>
    /// 撤销**这一脚**：先删这一脚已保存的标注；没有标注就删这一脚的门框标定。
    ///
    /// 原来的实现只认"本视频最后保存的一条标注"，于是"只标了门框、还没保存球点"的时候
    /// 按钮永远回一句"本视频还没有可撤销的标注"——门框撤不掉，只能去 clips 目录删文件。
    /// </summary>
    private void UndoLastAnnotation_Click(object sender, RoutedEventArgs e) => UndoLastAnnotation();

    /// <summary>落在这一脚时间区间里的标注（有重复时取最后标的那条）。</summary>
    private KeeperClipAnnotation? AnnotationForCurrentShot()
    {
        var all = AnnotationsForCurrentVideo();
        if (all.Count == 0) return null;

        if (_shotNav.Count > 0 && ShotListStore.FindAt(_shotNav, PendingOrCurrentSeconds) is { } shot)
        {
            return all
                .Where(a => a.VideoSeconds >= shot.Start - SameShotTolerance
                            && a.VideoSeconds <= shot.End + SameShotTolerance)
                .OrderByDescending(a => a.MarkedAt, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        // 没有脚次表的单片段：按"点了球的那一帧"就近找，容差就是一帧标注的宽度
        var anchor = _ballMark?.T ?? PendingOrCurrentSeconds;
        return all
            .Where(a => Math.Abs(a.VideoSeconds - anchor) <= SameShotTolerance)
            .OrderByDescending(a => a.MarkedAt, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private void UndoLastAnnotation()
    {
        if (string.IsNullOrWhiteSpace(_trainerKeeper))
        {
            TrainerProgressText.Text = "先选一个门将。";
            return;
        }

        var shotStart = CurrentShotStart();
        var shot = _shotNav.Count > 0 ? ShotListStore.FindAt(_shotNav, PendingOrCurrentSeconds) : null;
        var label = shot is not null && _shotNav.Count > 0
            ? $"第 {shot.Index}/{_shotNav.Count} 脚"
            : "这一脚";

        // ① 这一脚已保存的标注
        var target = AnnotationForCurrentShot();
        if (target is not null)
        {
            if (!TrainerStore.DeleteAnnotation(_trainerKeeper, target.Clip))
            {
                TrainerPointText.Text = $"撤销失败：删不掉 {target.Clip}.json（可能被占用）。";
                return;
            }

            InvalidateAnnotationCache();
            ResetBallMark();
            SeekTrainer(target.VideoSeconds);
            SyncTimelineFromVideo();
            ReloadTrainerClips();
            RefreshTrainerProgress();
            UpdateShotText();
            TrainerPointText.Text =
                $"已撤销{label}的标注 {target.Clip}（" +
                $"{ZoneLabel(GoalGeometry.ZoneKey(target.Lateral, target.Height))} · " +
                $"{(target.Saved ? "扑出" : "进球")}）。播放头已回到那一帧，重新点球即可再存。";
            _vm?.AddLog("info", $"门将标注撤销：{_trainerKeeper} · {target.Clip}");
            return;
        }

        // ② 这一脚的门框标定（只标了门框、还没点球的情况）
        if (_trainerClipPath is not null &&
            TrainerStore.DeleteVideoCalibration(_trainerKeeper, _trainerClipPath, shotStart))
        {
            _trainerCalibrating = false;
            _calibrationFrameSeconds = -1;
            _calibrationClicks.Clear();
            ClearCalibrationMarkers();
            _videoCalibrationsKey = null;
            EnsureVideoCalibrations();
            ApplyManualGoalFrame(PendingOrCurrentSeconds);
            RedrawGoalOverlay();
            _trainerPendingPoint = null;
            _pendingCalibration = null;
            UpdateCalibrationText();
            TrainerPointText.Text = $"已撤销{label}的门框标定（这一脚还没有保存标注）。橙框和红线已经撤掉。";
            _vm?.AddLog("info",
                $"门框标定撤销：{_trainerKeeper} · {Path.GetFileName(_trainerClipPath)} · " +
                $"{label}（{shotStart:0.00}s 起）");
            return;
        }

        TrainerPointText.Text = $"{label}既没有保存标注、也没有门框标定，没有可撤销的。";
    }

    private void RefreshTrainerProgress()
    {
        if (string.IsNullOrWhiteSpace(_trainerKeeper)) return;
        var annotations = TrainerStore.LoadAnnotations(_trainerKeeper);
        var onTarget = annotations.Count(annotation =>
            Math.Abs(annotation.Lateral) <= GoalGeometry.HalfWidth
            && annotation.Height >= 0 && annotation.Height <= GoalGeometry.Height);
        var total = _trainerClips.Count;
        var currentVideo = AnnotationsForCurrentVideo().Count;
        var scope = _shotNav.Count > 0
            ? $" · 本视频 {currentVideo}/{_shotNav.Count} 脚"
            : currentVideo > 0 ? $" · 本视频 {currentVideo} 脚" : "";
        TrainerProgressText.Text = (total > 0
            ? $"已标注 {annotations.Count} 条（射正 {onTarget}）· 文件夹里 {total} 个片段"
            : $"已标注 {annotations.Count} 条（射正 {onTarget}）") + scope;
        RedrawMarkTrack();
    }

    private void ImportEvents_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_trainerKeeper))
        {
            TrainerProgressText.Text = "先选一个门将，再导入事件文件。";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 StatsBomb 事件 JSON（可多选）",
            Filter = "事件 JSON (*.json)|*.json|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(_ownerWindow) != true) return;

        var files = dialog.FileNames
            .Select(path => (MatchId: ParseMatchId(path), Json: File.ReadAllText(path)))
            .ToList();
        var summary = StatsBombPenaltyImporter.Import(files);
        if (summary.UsableSamples == 0)
        {
            TrainerProgressText.Text = summary.PenaltyEvents == 0
                ? "这些文件里没有点球事件。"
                : $"读到 {summary.PenaltyEvents} 个点球，但全都射偏了。";
            return;
        }

        var keeperName = summary.KeeperNames.Count == 1
            ? summary.KeeperNames[0]
            : summary.KeeperNames.OrderByDescending(name =>
                    StatsBombPenaltyImporter.Import(files, name).UsableSamples).First();

        var keeperSamples = StatsBombPenaltyImporter.Import(files, keeperName);
        var written = StatsBombPenaltyImporter.WriteAnnotations(
            TrainerStore, _trainerKeeper, keeperSamples.Samples, $"statsbomb:{string.Join(",", files.Count)}");

        InvalidateAnnotationCache();
        ReloadTrainerClips();
        RefreshTrainerProgress();
        TrainerProgressText.Text = $"导入 {written} 条（门将：{keeperName}）· " +
                                   $"跳过偏出 {summary.OffTarget} 条";
        _vm?.AddLog("info", $"门将训练：从 {files.Count} 个事件文件导入 {written} 条标注（{keeperName}）");
    }

    private static int ParseMatchId(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return int.TryParse(stem, out var id) ? id : 0;
    }

    private void RunTraining_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_trainerKeeper))
        {
            TrainerProgressText.Text = "先选一个门将。";
            return;
        }

        var samples = TrainerStore.LoadSamples(_trainerKeeper);
        if (samples.Count == 0)
        {
            TrainerProgressText.Text = "还没有标注，先标几脚再训练。";
            return;
        }

        var template = BuildTemplateFor(_trainerKeeper);
        _trainerDocument = TrainerStore.TrainAndSave(
            _trainerKeeper, template, $"{TrainerStore.TrainingRoot}（{samples.Count} 条标注）");

        ShowTrainerSummary();
        ShowTrainerTab(TrainerTab.Train);
        _vm?.AddLog("info",
            $"门将训练完成：{_trainerKeeper} v{_trainerDocument.Version}，" +
            $"{_trainerDocument.ShotCount} 脚 / 扑出 {_trainerDocument.SaveCount}，" +
            $"置信度 {_trainerDocument.Confidence}");
    }

    private void PublishTrained_Click(object sender, RoutedEventArgs e)
    {
        if (_trainerDocument is null)
        {
            TrainerProgressText.Text = "先训练一次再发布。";
            return;
        }

        var path = TrainerStore.Publish(_trainerDocument.Profile);
        ReloadTrainerKeepers();
        _trainerKeeper = _trainerDocument.Profile.Name;
        SelectTrainerKeeper(_trainerKeeper);

        TrainerProgressText.Text = $"已发布到门将库：{Path.GetFileName(path)}";
        _vm?.AddLog("info", $"门将模型已发布：{path}");
        _ = NotifyUnityKeeperAsync(_trainerDocument.Profile.Name);
    }

    private void ShowTrainerSummary()
    {
        if (_trainerDocument is null)
        {
            TrainerSummaryText.Text = "还没有训练结果。标好片段后点「开始训练」。";
            TrainerHeatmap.Values = null;
            TrainerZoneSamplesText.Text = "";
            TrainerHistoryText.Text = "";
            TrainerWarningsText.Text = "";
            TileShotsText.Text = "—";
            TileSavesText.Text = "—";
            TileRateText.Text = "—";
            TileConfidenceText.Text = "—";
            return;
        }

        var document = _trainerDocument;
        var quality = document.Quality;
        var qualityText = quality.Samples > 0
            ? $"交叉验证 logloss {quality.LogLoss:F3}（基线 {quality.BaselineLogLoss:F3}，" +
              $"{(quality.BaselineLogLoss > quality.LogLoss ? "优于" : "不如")}基线）"
            : "样本太少，未做交叉验证";

        // 四个关键数进指标块，摘要只留"第几代 + 交叉验证是否真的变好"
        TileShotsText.Text = document.ShotCount.ToString();
        TileSavesText.Text = document.SaveCount.ToString();
        TileRateText.Text = document.GlobalSaveRate.ToString("P0");
        TileConfidenceText.Text = ShortConfidence(document.Confidence);
        TrainerSummaryText.Text = $"第 {document.Version} 代 · {qualityText}";

        TrainerHeatmap.Values = GoalkeeperProfile.ZoneKeys
            .Select(key => 1.0 - document.Profile.ProbabilityMap.GetValueOrDefault(key, 0.5f))
            .ToArray();

        var cells = new List<string>();
        for (var row = 2; row >= 0; row--)
        {
            var line = new List<string>();
            for (var column = 0; column < 5; column++)
            {
                var index = row * 5 + column;
                var samples = index < document.ZoneSamples.Length ? document.ZoneSamples[index] : 0;
                var saves = index < document.ZoneSaves.Length ? document.ZoneSaves[index] : 0;
                var probability = document.Profile.ProbabilityMap.GetValueOrDefault(
                    GoalkeeperProfile.ZoneKeys[index], 0f);
                line.Add($"{ZoneLabel(GoalkeeperProfile.ZoneKeys[index])} {probability:P0}({saves}/{samples})");
            }

            cells.Add(string.Join("  ", line));
        }

        TrainerZoneSamplesText.Text = string.Join("\n", cells);
        TrainerHistoryText.Text = document.History.Count == 0
            ? "—"
            : string.Join("\n", document.History.Select(entry =>
                $"v{entry.Version} · {entry.TrainedAt} · {entry.ShotCount} 脚 · " +
                $"logloss {entry.CrossValidationLogLoss:F3}"));
        TrainerWarningsText.Text = string.Join("\n", document.Warnings.Select(warning => "· " + warning));
    }

    /// <summary>置信度存档是枚举名，指标块里只放一个字，长说明放到摘要里。</summary>
    private static string ShortConfidence(string confidence) => confidence switch
    {
        "High" => "高",
        "Medium" => "中",
        "Low" => "低",
        "Insufficient" => "不足",
        _ => string.IsNullOrWhiteSpace(confidence) ? "—" : confidence,
    };

    /// <summary>训练只改概率表，身体参数从现有门将 JSON 继承（单机位视频测不出这些量）。</summary>
    private GoalkeeperProfile BuildTemplateFor(string keeper)
    {
        var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
        var stats = pipeline?.LoadGoalkeeperStats(keeper);
        if (stats is null) return new GoalkeeperProfile { Height = 1.90f, DiveSpeed = 4.0f };
        return new GoalkeeperProfile
        {
            Height = (float)stats.Height,
            DiveSpeed = (float)stats.DiveSpeed,
            JumpDistance = (float)stats.JumpDistance,
            JumpHeight = (float)stats.JumpHeight,
            Reach = (float)stats.Reach,
            TendGoalSpeed = (float)stats.TendGoalSpeed,
            TendGoalDistance = (float)stats.TendGoalDistance,
            GoalKeeping = (float)stats.GoalKeeping,
        };
    }

    private static string ZoneLabel(string key) => key switch
    {
        "LowLeft" => "左低",
        "LowMidLeft" => "左中低",
        "LowCenter" => "中低",
        "LowMidRight" => "右中低",
        "LowRight" => "右低",
        "MidLeft" => "左中",
        "MidMidLeft" => "中偏左",
        "MidCenter" => "中路",
        "MidMidRight" => "中偏右",
        "MidRight" => "右中",
        "HighLeft" => "左上",
        "HighMidLeft" => "左中高",
        "HighCenter" => "中高",
        "HighMidRight" => "右中高",
        "HighRight" => "右上",
        _ => key,
    };

    private sealed record TrainerKeeperChoice(string Key, string Label);

    private sealed record TrainerClipItem(string? Path, string Label)
    {
        public override string ToString() => Label;
    }

    #endregion
}
