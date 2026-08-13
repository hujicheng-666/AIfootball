using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace AIfootball.App.Controls;

/// <summary>
/// 五维雷达图（速度/臂展/弹跳/反应/身高）。
/// 值已归一化到 [0,1]，直接用 DrawingContext 绘制，无第三方依赖。
/// </summary>
public sealed class RadarChartControl : FrameworkElement
{
    static readonly string[] AxisNames = { "速度", "臂展", "弹跳", "反应", "身高" };

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(double[]), typeof(RadarChartControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values
    {
        get => (double[]?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 20 || h < 20)
            return;

        double cx = w / 2.0;
        double cy = h / 2.0;
        double maxR = Math.Min(w, h) * 0.32;

        var gridPen = new Pen(new SolidColorBrush(Color.FromRgb(0xc6, 0xcd, 0xd4)), 1.0);
        gridPen.Freeze();

        // 网格环（3 层参考五边形）
        for (int ring = 1; ring <= 3; ring++)
        {
            double r = maxR * ring / 3.0;
            for (int i = 0; i < 5; i++)
            {
                Point p1 = PointAt(cx, cy, r, i);
                Point p2 = PointAt(cx, cy, r, (i + 1) % 5);
                dc.DrawLine(gridPen, p1, p2);
            }
        }

        // 轴线
        for (int i = 0; i < 5; i++)
        {
            Point outer = PointAt(cx, cy, maxR, i);
            dc.DrawLine(gridPen, new Point(cx, cy), outer);
        }

        // 属性多边形
        double[]? values = Values;
        if (values != null && values.Length >= 5)
        {
            var poly = new List<Point>(5);
            for (int i = 0; i < 5; i++)
            {
                double v = Math.Clamp(values[i], 0.0, 1.0);
                poly.Add(PointAt(cx, cy, maxR * v, i));
            }

            var fill = new SolidColorBrush(Color.FromArgb(0x40, 0x23, 0x83, 0x6b));
            fill.Freeze();
            var edge = new Pen(new SolidColorBrush(Color.FromRgb(0x23, 0x83, 0x6b)), 2.0);
            edge.Freeze();

            var geometry = new StreamGeometry();
            using (StreamGeometryContext gc = geometry.Open())
            {
                gc.BeginFigure(poly[0], isFilled: true, isClosed: true);
                gc.PolyLineTo(poly.Skip(1).ToList(), isStroked: true, isSmoothJoin: false);
            }
            geometry.Freeze();
            dc.DrawGeometry(fill, edge, geometry);
        }

        // 轴标签
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Microsoft YaHei UI"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x5a, 0x68, 0x75));
        labelBrush.Freeze();

        for (int i = 0; i < 5; i++)
        {
            var ft = new FormattedText(
                AxisNames[i], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, 11.0, labelBrush, dpi);
            double a = Angle(i);
            double r = maxR + 16;
            var pos = new Point(
                cx + r * Math.Cos(a) - ft.Width / 2.0,
                cy + r * Math.Sin(a) - ft.Height / 2.0);
            dc.DrawText(ft, pos);
        }
    }

    static double Angle(int i) => -Math.PI / 2.0 + i * 2.0 * Math.PI / 5.0;

    static Point PointAt(double cx, double cy, double r, int i)
    {
        double a = Angle(i);
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }
}
