using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIfootball.App.Views.Pages;

public partial class DashboardPage : UserControl
{
    public DashboardPage() => InitializeComponent();

    private void OfflineCard_Click(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo("pipeline");

    private void OnlineCard_Click(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo("pipeline");
}
