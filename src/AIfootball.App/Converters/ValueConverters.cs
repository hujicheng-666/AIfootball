using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AIfootball.App.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true)
            return Application.Current.TryFindResource("AccentBrush") as Brush
                   ?? new SolidColorBrush(Colors.Green);
        return Application.Current.TryFindResource("DangerBrush") as Brush
               ?? new SolidColorBrush(Colors.Red);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public class StepStatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Models.StepStatus status) return new SolidColorBrush(Colors.Gray);

        return status switch
        {
            Models.StepStatus.Completed => Application.Current.TryFindResource("AccentBrush")
                as Brush ?? new SolidColorBrush(Colors.Green),
            Models.StepStatus.Running => Application.Current.TryFindResource("PrimaryBrush")
                as Brush ?? new SolidColorBrush(Colors.Blue),
            Models.StepStatus.Failed => Application.Current.TryFindResource("DangerBrush")
                as Brush ?? new SolidColorBrush(Colors.Red),
            _ => Application.Current.TryFindResource("TextMutedBrush")
                as Brush ?? new SolidColorBrush(Colors.Gray),
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
