using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AIfootball.App.ViewModels;

namespace AIfootball.App.Views.Pages;

public partial class DashboardPage : UserControl
{
    private MainViewModel? _vm;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as MainViewModel;
        if (_vm != null)
        {
            // 自动滚动到最新日志
            _vm.LogEntries.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Add)
                    Dispatcher.BeginInvoke(() => LogScrollViewer.ScrollToEnd());
            };
        }
    }

    private void OfflineCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateTo("pipeline");
    }

    private void OnlineCard_Click(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow mw)
            mw.NavigateTo("pipeline");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _vm?.LogEntries.Clear();
        _vm?.AddLog("info", "日志已清空");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null || _vm.LogEntries.Count == 0) return;
        var text = string.Join(Environment.NewLine,
            _vm.LogEntries.Select(entry =>
                $"[{entry.Timestamp:HH:mm:ss}] [{entry.Level}] {entry.Message}"));
        Clipboard.SetText(text);
        _vm.AddLog("info", $"已复制 {_vm.LogEntries.Count} 条日志到剪贴板");
    }
}
