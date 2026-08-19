using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using AIfootball.App.ViewModels;

namespace AIfootball.App.Views.Pages;

public partial class DashboardPage : UserControl
{
    private MainViewModel? _viewModel;
    private Storyboard? _stageStoryboard;

    public DashboardPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => StopStageAnimation();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        UpdatePipelineStage();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.PipelineStage))
            Dispatcher.InvokeAsync(UpdatePipelineStage);
    }

    private void UpdatePipelineStage()
    {
        StopStageAnimation();
        SetPipelineOpacity(0.48, 0.18);

        if (_viewModel?.PipelineStage == 5)
        {
            SetPipelineOpacity(1, 1);
            return;
        }

        var (node, arrow) = _viewModel?.PipelineStage switch
        {
            1 => (PipelineMaterial, PipelineArrow1),
            2 => (PipelineReconstruct, PipelineArrow2),
            3 => (PipelineFit, PipelineArrow3),
            4 => (PipelineDelivery, null),
            _ => (null, null)
        };

        if (node is null) return;

        _stageStoryboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        AddPulse(node, 0.45, 1);
        if (arrow is not null)
            AddPulse(arrow, 0.2, 0.82);
        _stageStoryboard.Begin(this, true);
    }

    private void AddPulse(UIElement target, double from, double to)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(650))
        {
            AutoReverse = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(UIElement.OpacityProperty));
        _stageStoryboard!.Children.Add(animation);
    }

    private void StopStageAnimation()
    {
        _stageStoryboard?.Stop(this);
        _stageStoryboard = null;
    }

    private void SetPipelineOpacity(double nodeOpacity, double arrowOpacity)
    {
        PipelineMaterial.Opacity = nodeOpacity;
        PipelineReconstruct.Opacity = nodeOpacity;
        PipelineFit.Opacity = nodeOpacity;
        PipelineDelivery.Opacity = nodeOpacity;
        PipelineArrow1.Opacity = arrowOpacity;
        PipelineArrow2.Opacity = arrowOpacity;
        PipelineArrow3.Opacity = arrowOpacity;
    }

    private void OfflineCard_Click(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo("pipeline");

    private void OnlineCard_Click(object sender, MouseButtonEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.NavigateTo("pipeline");
}
