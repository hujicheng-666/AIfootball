using System.Windows;
using System.Windows.Controls;

namespace AIfootball.App.Views.Pages;

public partial class PipelinePage : UserControl
{
    private bool _isInitialized;

    public PipelinePage()
    {
        InitializeComponent();
        _isInitialized = true;
    }

    private void TabOffline_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        OfflinePanel.Visibility = Visibility.Visible;
        OnlinePanel.Visibility = Visibility.Collapsed;
    }

    private void TabOnline_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        OfflinePanel.Visibility = Visibility.Collapsed;
        OnlinePanel.Visibility = Visibility.Visible;
    }
}
