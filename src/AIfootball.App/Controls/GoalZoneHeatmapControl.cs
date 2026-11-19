using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AIfootball.App.Controls;

/// <summary>
/// 门面 3×5 区域热图（7.32 m × 2.44 m），按区域强度渲染成连续色场。
///
/// 两种用法共用同一个控件：
/// <list type="bullet">
/// <item>门将：<see cref="Values"/> 传 15 格扑救概率（0–1）；</item>
/// <item>射门：<see cref="Values"/> 传归一化后的落点密度，<see cref="Points"/> 叠原始散点。</item>
/// </list>
/// 区域顺序与 <c>GoalGeometry.ZoneKeys</c> 一致：行优先，低→高，左→右。
///
/// 画的是连续热力图而不是"方格 + 数字表"：15 个区域值做双线性插值后逐像素上色，
/// 颜色按本图内的 min–max 归一化——门将概率普遍在 0.7 以上，用绝对刻度会整块糊成
/// 一个颜色，看不出相对弱点在哪。
/// </summary>
public sealed class GoalZoneHeatmapControl : FrameworkElement
{
    private const int Columns = 5;
    private const int Rows = 3;

    // 热力图的取样分辨率：低到足以逐像素重算，高到拉伸后看不出台阶
    private const int SampleWidth = 240;
    private const int SampleHeight = 88;

    /// <summary>冷 → 热：淡蓝（弱）→ 蓝 → 琥珀 → 砖红（强）。</summary>
    private static readonly (double Stop, Color Color)[] Palette =
    [
        (0.00, Color.FromRgb(0xEC, 0xF4, 0xFB)),
        (0.35, Color.FromRgb(0x8C, 0xBF, 0xE3)),
        (0.70, Color.FromRgb(0xF0, 0xC0, 0x50)),
        (1.00, Color.FromRgb(0xBE, 0x35, 0x28)),
    ];

    private static readonly Color FrameColor = Color.FromRgb(0x1D, 0x27, 0x31);
    private static readonly Color EmptyColor = Color.FromRgb(0xF1, 0xF4, 0xF7);
    private static readonly Color DotStrokeColor = Color.FromRgb(0xBE, 0x35, 0x28);

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(GoalZoneHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(Point[]), typeof(GoalZoneHeatmapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowValuesProperty = DependencyProperty.Register(
        nameof(ShowValues), typeof(bool), typeof(GoalZoneHeatmapControl),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>15 格强度，行优先（低→高 × 左→右）。为空时画空网格。</summary>
    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    /// <summary>归一化散点：X/Y 均 0→1，原点在球门左下角；允许超出以表示射偏。</summary>
    public Point[]? Points
    {
        get => (Point[]?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    /// <summary>是否在格子里标百分比（默认关，热力图靠颜色表达强度）。</summary>
    public bool ShowValues
    {
        get => (bool)GetValue(ShowValuesProperty);
        set => SetValue(ShowValuesProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        // 球门宽高比决定绘制区，避免被拉变形
        const double goalAspect = 7.32 / 2.44;
        var availableWidth = Math.Max(0, ActualWidth - 16);
        var availableHeight = Math.Max(0, ActualHeight - 16);
        if (availableWidth < 40 || availableHeight < 20)
            return;

        var goalHeight = Math.Min(availableHeight, availableWidth / goalAspect);
        // 两侧留约 5% 余量：射偏的散点落在门框外，压到框线上会被误看成"打中门柱"
        if (goalHeight * goalAspect > availableWidth * 0.9)
            goalHeight = availableWidth * 0.9 / goalAspect;
        var goalWidth = goalHeight * goalAspect;
        var left = (ActualWidth - goalWidth) / 2.0;
        var top = (ActualHeight - goalHeight) / 2.0;
        var goalRect = new Rect(left, top, goalWidth, goalHeight);

        var values = NormalizedValues();
        if (values is null)
        {
            var empty = new SolidColorBrush(EmptyColor);
            empty.Freeze();
            dc.DrawRectangle(empty, null, goalRect);
        }
        else
        {
            var bitmap = BuildHeatmap(values);
            // 位图按像素拉伸，用高质量插值避免马赛克
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            dc.DrawImage(bitmap, goalRect);
        }

        if (ShowValues && values is not null)
            DrawPercentLabels(dc, values, goalRect);

        DrawFrame(dc, left, top, goalWidth, goalHeight);
        DrawPoints(dc, left, top, goalWidth, goalHeight);
    }

    /// <summary>把 15 格强度归一化到 0–1；全等值时统一取中间色。</summary>
    private double[]? NormalizedValues()
    {
        if (Values is not { Length: >= Columns * Rows } raw)
            return null;

        var min = double.MaxValue;
        var max = double.MinValue;
        for (var index = 0; index < Columns * Rows; index++)
        {
            var value = raw[index];
            if (double.IsNaN(value)) continue;
            if (value < min) min = value;
            if (value > max) max = value;
        }

        if (min > max) return null;

        var normalized = new double[Columns * Rows];
        var range = max - min;
        for (var index = 0; index < normalized.Length; index++)
        {
            var value = raw[index];
            normalized[index] = double.IsNaN(value)
                ? 0.5
                : range < 1e-6 ? 0.5 : Math.Clamp((value - min) / range, 0, 1);
        }
        return normalized;
    }

    private static BitmapSource BuildHeatmap(double[] values)
    {
        var stride = SampleWidth * 4;
        var pixels = new byte[stride * SampleHeight];

        for (var y = 0; y < SampleHeight; y++)
        {
            // 位图第 0 行在顶部，热力图向上为"高"
            var v = 1.0 - y / (double)(SampleHeight - 1);
            var rowOffset = y * stride;
            for (var x = 0; x < SampleWidth; x++)
            {
                var u = x / (double)(SampleWidth - 1);
                var color = PaletteColor(SampleBilinear(values, u, v));
                var offset = rowOffset + x * 4;
                pixels[offset] = color.B;
                pixels[offset + 1] = color.G;
                pixels[offset + 2] = color.R;
                pixels[offset + 3] = 0xFF;
            }
        }

        var bitmap = BitmapSource.Create(SampleWidth, SampleHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>双线性插值：格心在 (c+0.5)/5、(r+0.5)/3，边界外按最近的格子取值。</summary>
    private static double SampleBilinear(double[] values, double u, double v)
    {
        var gx = u * Columns - 0.5;
        var gy = v * Rows - 0.5;

        var column0 = (int)Math.Floor(gx);
        var row0 = (int)Math.Floor(gy);
        var tx = gx - column0;
        var ty = gy - row0;

        double At(int row, int column) =>
            values[Math.Clamp(row, 0, Rows - 1) * Columns + Math.Clamp(column, 0, Columns - 1)];

        var top = At(row0, column0) * (1 - tx) + At(row0, column0 + 1) * tx;
        var bottom = At(row0 + 1, column0) * (1 - tx) + At(row0 + 1, column0 + 1) * tx;
        return top * (1 - ty) + bottom * ty;
    }

    private static Color PaletteColor(double t)
    {
        t = Math.Clamp(t, 0, 1);
        for (var index = 1; index < Palette.Length; index++)
        {
            if (t > Palette[index].Stop) continue;
            var (fromStop, fromColor) = Palette[index - 1];
            var (toStop, toColor) = Palette[index];
            var span = toStop - fromStop;
            var local = span < 1e-6 ? 0 : (t - fromStop) / span;
            return Lerp(fromColor, toColor, local);
        }
        return Palette[^1].Color;
    }

    private static Color Lerp(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private void DrawPercentLabels(DrawingContext dc, double[] values, Rect goalRect)
    {
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var cellWidth = goalRect.Width / Columns;
        var cellHeight = goalRect.Height / Rows;

        for (var row = 0; row < Rows; row++)
        {
            var cellTop = goalRect.Top + (Rows - 1 - row) * cellHeight;
            for (var column = 0; column < Columns; column++)
            {
                var intensity = values[row * Columns + column];
                var label = (intensity * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
                var text = new FormattedText(label, CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, 10.0,
                    new SolidColorBrush(intensity > 0.62
                        ? Color.FromArgb(0xEE, 0xFF, 0xFF, 0xFF)
                        : Color.FromArgb(0xCC, 0x35, 0x45, 0x55)),
                    dpi);
                var cellLeft = goalRect.Left + column * cellWidth;
                dc.DrawText(text, new Point(
                    cellLeft + (cellWidth - text.Width) / 2.0,
                    cellTop + (cellHeight - text.Height) / 2.0));
            }
        }
    }

    private static void DrawFrame(DrawingContext dc, double left, double top, double width, double height)
    {
        // 门框（立柱 + 横梁）：压在色场上，强调"这是球门不是图表"
        var framePen = new Pen(new SolidColorBrush(FrameColor), 3.0);
        framePen.Freeze();
        dc.DrawLine(framePen, new Point(left, top), new Point(left + width, top));       // 横梁
        dc.DrawLine(framePen, new Point(left, top), new Point(left, top + height));      // 左柱
        dc.DrawLine(framePen, new Point(left + width, top),
            new Point(left + width, top + height));                                      // 右柱

        var groundPen = new Pen(new SolidColorBrush(FrameColor), 1.5);
        groundPen.Freeze();
        dc.DrawLine(groundPen, new Point(left, top + height),
            new Point(left + width, top + height));                                      // 门线
    }

    private void DrawPoints(DrawingContext dc, double left, double top, double width, double height)
    {
        if (Points is not { Length: > 0 } points)
            return;

        // 热力图上红点会和背景糊在一起，改用白心红边
        var fill = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        fill.Freeze();
        var pen = new Pen(new SolidColorBrush(DotStrokeColor), 1.6);
        pen.Freeze();

        foreach (var point in points)
        {
            // 略微允许越界，射偏的球要能看出"在门框外"
            var x = left + Math.Clamp(point.X, -0.05, 1.05) * width;
            var y = top + (1 - Math.Clamp(point.Y, -0.05, 1.05)) * height;
            dc.DrawEllipse(fill, pen, new Point(x, y), 3.4, 3.4);
        }
    }
}
