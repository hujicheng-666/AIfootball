using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace AIfootball.App.Controls;

/// <summary>时间轴上的一个脚次分段。</summary>
public sealed class TimelineShotMark
{
    public int Index { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    /// <summary>这一脚已保存标注。</summary>
    public bool Marked { get; init; }
    /// <summary>已标注且判定为扑出（否则为进球）。</summary>
    public bool Saved { get; init; }
    /// <summary>这一脚标过门框。</summary>
    public bool Calibrated { get; init; }
}

public sealed class TimelineSeekEventArgs(double seconds) : EventArgs
{
    public double Seconds { get; } = seconds;
}

public sealed class TimelineZoomEventArgs(double contentX, double factor) : EventArgs
{
    /// <summary>鼠标在内容坐标里的位置（已包含横向滚动偏移）。</summary>
    public double ContentX { get; } = contentX;

    public double Factor { get; } = factor;
}

public sealed class TimelinePanEventArgs(double deltaPixels) : EventArgs
{
    public double DeltaPixels { get; } = deltaPixels;
}

/// <summary>
/// 剪映式时间轴条：刻度尺 + 缩略图带 + 状态轨 + 脚次分段 + 可拖播放头。
///
/// 为什么不用 Slider：点球只有一两秒，而合集动辄五六分钟。
/// 单一刻度的滑块在"看全片"和"看单帧"之间没有中间态——要么整段挤成一条线，
/// 要么只能看到零点几秒。这里按秒做像素密度（<see cref="PixelsPerSecond"/>），
/// 缩放交给调用方（滚轮走到 <see cref="ZoomRequested"/>），
/// 内容宽度随密度变化，放在 ScrollViewer 里就能横向滚动。
///
/// 控件本身不持有播放器：只负责把"点到的时间"抛出去，由页面去 seek。
/// </summary>
public sealed class TimelineStrip : FrameworkElement
{
    private const double RulerHeight = 18;
    private const double BandTop = RulerHeight + 2;
    private const double FilmHeight = 30;
    private const double LaneHeight = 10;
    private const double BandHeight = FilmHeight + LaneHeight;
    private const double TotalHeight = BandTop + BandHeight;

    // 视觉：深色胶片带 + 红色播放头。深色是让缩略图自己跳出来，
    // 浅色带会让一排小图糊成一条灰带，看不出切点在哪。
    private static readonly Brush BandBrush = Frozen(Color.FromRgb(0x16, 0x20, 0x2B));
    private static readonly Brush LaneBrush = Frozen(Color.FromRgb(0x0C, 0x13, 0x1A));
    private static readonly Brush BandEdgeBrush = Frozen(Color.FromRgb(0x0B, 0x12, 0x19));
    private static readonly Brush ShotLineBrush = Frozen(Color.FromRgb(0x3A, 0x4C, 0x5E));
    private static readonly Brush ChipBrush = Frozen(Color.FromArgb(0xB4, 0x0B, 0x12, 0x19));
    private static readonly Brush ShotLabelBrush = Frozen(Color.FromRgb(0xC7, 0xD5, 0xE4));
    private static readonly Brush CalibratedBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush SavedBrush = Frozen(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush GoalBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush PlayheadBrush = Frozen(Color.FromRgb(0xE1, 0x1D, 0x48));
    private static readonly Brush TickBrush = Frozen(Color.FromRgb(0xB7, 0xC3, 0xD2));
    private static readonly Brush MinorTickBrush = Frozen(Color.FromRgb(0xE3, 0xE8, 0xEF));
    private static readonly Brush LabelBrush = Frozen(Color.FromRgb(0x7C, 0x87, 0x98));
    private static readonly Brush HoverBrush = Frozen(Color.FromRgb(0x8F, 0x9E, 0xB2));

    private static readonly Pen PlayheadPen = FrozenPen(PlayheadBrush, 1.4);
    private static readonly Pen HoverPen = FrozenDashedPen(HoverBrush, 1);
    private static readonly Pen BandEdgePen = FrozenPen(BandEdgeBrush, 1);
    private static readonly Pen TickPen = FrozenPen(TickBrush, 1);
    private static readonly Pen MinorTickPen = FrozenPen(MinorTickBrush, 1);
    private static readonly Pen ShotLinePen = FrozenPen(ShotLineBrush, 0.8);

    /// <summary>刻度步长候选（秒）：从一帧到十分钟，保证标签不会挤在一起。</summary>
    private static readonly double[] TickSteps =
    [
        0.04, 0.08, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600,
    ];

    private double _duration;
    private double _position;
    private double _pixelsPerSecond = 40;
    private IReadOnlyList<TimelineShotMark> _shots = [];
    private IReadOnlyList<ImageSource> _thumbnails = [];

    /// <summary>
    /// 每张缩略图覆盖的秒数：等间隔铺满全片，直接由"时长 ÷ 张数"推出来。
    ///
    /// 以前它是个外部可写属性，结果调用方一旦在时长还没就绪时算过一次，
    /// 就会把 0 存进来，之后即使图和时长都齐了也永远画不出东西（轨道只剩一条黑带）。
    /// 这个值本来就完全由 Duration 和 Thumbnails 决定，不该再让外部喂第二遍。
    /// </summary>
    private double SlotSeconds => _duration > 0 && _thumbnails.Count > 0
        ? _duration / _thumbnails.Count
        : 0;

    private bool _dragging;
    private double _hoverX = -1;

    public TimelineStrip()
    {
        ClipToBounds = true;
        Cursor = Cursors.SizeWE;
        SnapsToDevicePixels = true;
    }

    /// <summary>素材总时长（秒）；&lt;= 0 时不绘制内容。</summary>
    public double Duration
    {
        get => _duration;
        set
        {
            if (Math.Abs(_duration - value) < 1e-6) return;
            _duration = Math.Max(0, value);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>播放头位置（秒）。</summary>
    public double Position
    {
        get => _position;
        set
        {
            if (Math.Abs(_position - value) < 1e-6) return;
            _position = Math.Max(0, value);
            InvalidateVisual();
        }
    }

    /// <summary>每秒占多少像素：缩放档位就是改这个值。</summary>
    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set
        {
            var next = Math.Max(0.5, value);
            if (Math.Abs(_pixelsPerSecond - next) < 1e-6) return;
            _pixelsPerSecond = next;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public IReadOnlyList<TimelineShotMark> Shots
    {
        get => _shots;
        set
        {
            _shots = value ?? [];
            InvalidateVisual();
        }
    }

    /// <summary>按时间顺序排列的缩略图，等间隔铺满全片（每张代表 时长÷张数 秒）。</summary>
    public IReadOnlyList<ImageSource> Thumbnails
    {
        get => _thumbnails;
        set
        {
            _thumbnails = value ?? [];
            InvalidateVisual();
        }
    }

    /// <summary>正在拖动播放头：调用方据此暂停"自动跟随滚动"，避免画面从手指下逃走。</summary>
    public bool IsDragging => _dragging;

    /// <summary>用户点了某个位置（按下或拖动中）。</summary>
    public event EventHandler<TimelineSeekEventArgs>? SeekRequested;

    /// <summary>滚轮缩放：调用方决定新的像素密度并保持鼠标下的时间点不动。</summary>
    public event EventHandler<TimelineZoomEventArgs>? ZoomRequested;

    /// <summary>Shift + 滚轮：横向平移。</summary>
    public event EventHandler<TimelinePanEventArgs>? PanRequested;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = _duration * _pixelsPerSecond;
        var height = double.IsInfinity(availableSize.Height) ? TotalHeight : availableSize.Height;
        if (double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width))
            return new Size(width, height);
        return new Size(Math.Max(availableSize.Width, width), height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        if (_duration <= 0) return;

        DrawRuler(dc, width);
        DrawFilmBand(dc, width);

        // 播放头：贯穿刻度尺和胶片带，顶部留一个抓手，鼠标能一把抓住
        var playheadX = _position * _pixelsPerSecond;
        dc.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, height));
        dc.DrawRoundedRectangle(PlayheadBrush, null, new Rect(playheadX - 4.5, 0, 9, 11), 2, 2);

        if (!_dragging && _hoverX >= 0 && Math.Abs(_hoverX - playheadX) > 3)
        {
            dc.DrawLine(HoverPen, new Point(_hoverX, RulerHeight), new Point(_hoverX, height));
        }
    }

    private void DrawRuler(DrawingContext dc, double width)
    {
        var step = ChooseTickStep();
        if (step <= 0) return;

        for (var t = 0.0; t <= _duration + 1e-6; t += step)
        {
            var x = t * _pixelsPerSecond;
            if (x > width) break;
            dc.DrawLine(TickPen, new Point(x, RulerHeight - 5), new Point(x, RulerHeight));
            DrawText(dc, FormatTick(t, step), x + 3, 1.5, LabelBrush, 9.5);

            for (var i = 1; i < 5; i++)
            {
                var minorX = (t + step * i / 5.0) * _pixelsPerSecond;
                if (minorX > width) break;
                dc.DrawLine(MinorTickPen, new Point(minorX, RulerHeight - 3), new Point(minorX, RulerHeight));
            }
        }
    }

    private void DrawFilmBand(DrawingContext dc, double width)
    {
        var band = new Rect(0, BandTop, width, BandHeight);
        dc.DrawRoundedRectangle(BandBrush, null, band, 4, 4);

        dc.PushClip(new RectangleGeometry(band, 4, 4));
        try
        {
            dc.DrawRectangle(LaneBrush, null, new Rect(0, BandTop + FilmHeight, width, LaneHeight));
            DrawThumbnails(dc, width);
            DrawShots(dc, width);
        }
        finally
        {
            dc.Pop();
        }

        dc.DrawRoundedRectangle(null, BandEdgePen, band, 4, 4);
    }

    private void DrawThumbnails(DrawingContext dc, double width)
    {
        if (_thumbnails.Count == 0 || SlotSeconds <= 0) return;

        var aspect = 16.0 / 9.0;
        if (_thumbnails[0] is { Width: > 0, Height: > 0 })
            aspect = _thumbnails[0].Width / _thumbnails[0].Height;
        var natural = FilmHeight * aspect;
        var slot = SlotSeconds * _pixelsPerSecond;
        if (slot < 1) return;

        for (var i = 0; i < _thumbnails.Count; i++)
        {
            var x = i * slot;
            if (x > width) break;
            // 缩略图按原比例铺，槽位不够就裁掉两侧——拉扁的画面会让人误判球员位置
            var drawRect = new Rect(x + (slot - natural) / 2, BandTop, natural, FilmHeight);
            dc.PushClip(new RectangleGeometry(new Rect(x, BandTop, slot, FilmHeight)));
            dc.DrawImage(_thumbnails[i], drawRect);
            dc.Pop();
        }
    }

    private void DrawShots(DrawingContext dc, double width)
    {
        if (_shots.Count == 0) return;

        var laneTop = BandTop + FilmHeight;
        foreach (var shot in _shots)
        {
            var x0 = shot.Start * _pixelsPerSecond;
            var x1 = shot.End * _pixelsPerSecond;
            if (x1 < 0 || x0 > width) continue;
            var slot = Math.Max(1, x1 - x0);

            // 切镜分隔线：这是"哪几脚是各自独立的镜头"的唯一可视信息
            if (x0 > 0.5)
                dc.DrawLine(ShotLinePen, new Point(x0, BandTop), new Point(x0, BandTop + BandHeight));

            // 脚次序号：压一层深色药丸底，缩略图再花也读得出来
            if (slot > 26)
            {
                var chip = new Rect(x0 + 2, BandTop + 2, 20, 11);
                dc.DrawRoundedRectangle(ChipBrush, null, chip, 2, 2);
                DrawText(dc, shot.Index.ToString(CultureInfo.InvariantCulture), x0 + 5, BandTop + 2.5,
                    ShotLabelBrush, 8.5);
            }

            // 状态轨（底部 10px 独立一条）：上排＝门框已标（蓝），下排＝结果（扑出绿 / 进球红）
            if (shot.Calibrated)
                dc.DrawRectangle(CalibratedBrush, null, new Rect(x0, laneTop + 2, slot, 3));
            if (shot.Marked)
                dc.DrawRectangle(shot.Saved ? SavedBrush : GoalBrush, null, new Rect(x0, laneTop + 6, slot, 3));
        }
    }

    private double ChooseTickStep()
    {
        foreach (var step in TickSteps)
        {
            if (step * _pixelsPerSecond >= 62) return step;
        }
        return TickSteps[^1];
    }

    private static string FormatTick(double seconds, double step)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";
        if (step < 1) return $"{t.Minutes}:{t.Seconds:00}.{t.Milliseconds / 100}";
        return $"{t.Minutes}:{t.Seconds:00}";
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, Brush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas, Segoe UI"), FontStyles.Normal, FontWeights.Normal,
                FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    #region 交互

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_duration <= 0) return;
        _dragging = true;
        CaptureMouse();
        RaiseSeek(e.GetPosition(this).X);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var x = e.GetPosition(this).X;
        _hoverX = x;
        if (_dragging)
        {
            // 拖出控件边界时按边界定位，手感更接近播放器
            RaiseSeek(x);
            e.Handled = true;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverX = -1;
        InvalidateVisual();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_duration <= 0) return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            PanRequested?.Invoke(this, new TimelinePanEventArgs(-e.Delta * 0.6));
        }
        else
        {
            var factor = e.Delta > 0 ? 1.3 : 1 / 1.3;
            ZoomRequested?.Invoke(this, new TimelineZoomEventArgs(e.GetPosition(this).X, factor));
        }
        e.Handled = true;
    }

    private void RaiseSeek(double x)
    {
        var seconds = Math.Clamp(x / _pixelsPerSecond, 0, _duration);
        Position = seconds;
        SeekRequested?.Invoke(this, new TimelineSeekEventArgs(seconds));
    }

    #endregion

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen FrozenDashedPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness) { DashStyle = new DashStyle([3, 3], 0) };
        pen.Freeze();
        return pen;
    }
}
