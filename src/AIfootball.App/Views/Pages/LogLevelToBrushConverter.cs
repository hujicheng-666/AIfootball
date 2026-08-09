using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace AIfootball.App.Views.Pages;

/// <summary>日志级别 → 颜色转换器</summary>
public class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return (value as string) switch
        {
            "error" => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),   // 红色
            "warn" => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),    // 黄色
            "success" => new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81)), // 绿色
            "output" => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)),  // 灰色
            _ => new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)),         // 默认深色
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
