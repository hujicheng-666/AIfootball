using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AIfootball.App.ViewModels;
using Microsoft.Win32;

namespace AIfootball.App.Views.Pages;

public partial class CalibrationPage : UserControl
{
    private MainViewModel? _vm;
    private bool _showCalibrationWorkflow;

    public CalibrationPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachViewModel();
        _showCalibrationWorkflow = false;
        UpdateCalibrationView();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = null;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            AttachViewModel();
            _showCalibrationWorkflow = false;
            UpdateCalibrationView();
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachViewModel();
        _showCalibrationWorkflow = false;
        UpdateCalibrationView();
    }

    private void AttachViewModel()
    {
        var next = DataContext as MainViewModel;
        if (ReferenceEquals(_vm, next))
            return;

        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelPropertyChanged;

        _vm = next;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.CalibrationStatus) or nameof(MainViewModel.IsCalibrationRunning))
            Dispatcher.InvokeAsync(UpdateCalibrationView);
    }

    private void Recalibrate_Click(object sender, RoutedEventArgs e)
    {
        _showCalibrationWorkflow = true;
        UpdateCalibrationView();
    }

    private void UpdateCalibrationView()
    {
        var calibrationReady = _vm?.CalibrationStatus?.FullyReady == true;
        var showWorkflow = !calibrationReady || _showCalibrationWorkflow;

        ExistingCalibrationPanel.Visibility = calibrationReady && !_showCalibrationWorkflow
            ? Visibility.Visible
            : Visibility.Collapsed;
        CalibrationProgressPanel.Visibility = showWorkflow && _vm?.IsCalibrationRunning == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        IntrinsicsCalibrationPanel.Visibility = showWorkflow ? Visibility.Visible : Visibility.Collapsed;
        ExtrinsicsCalibrationPanel.Visibility = showWorkflow ? Visibility.Visible : Visibility.Collapsed;
        CalibrationArtifactsPanel.Visibility = showWorkflow ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 目录/文件选择 ──

    private void BrowseLeftChess_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择射手视角左侧棋盘格视频（左相机）",
            Filter = "视频文件|*.mp4;*.avi;*.mov;*.mkv|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
            LeftChessVideo.Text = dlg.FileName;
    }

    private void BrowseRightChess_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择射手视角右侧棋盘格视频（右相机）",
            Filter = "视频文件|*.mp4;*.avi;*.mov;*.mkv|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
            RightChessVideo.Text = dlg.FileName;
    }

    private void BrowseLeftField_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择射门视角左侧参考照片（左相机）",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp"
        };
        if (dlg.ShowDialog() == true)
            LeftFieldImage.Text = dlg.FileName;
    }

    private void BrowseRightField_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择射门视角右侧参考照片（右相机）",
            Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp"
        };
        if (dlg.ShowDialog() == true)
            RightFieldImage.Text = dlg.FileName;
    }

    // ── 内参标定 ──

    private async void CalibrateIntrinsics_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        if (string.IsNullOrWhiteSpace(LeftChessVideo.Text) || string.IsNullOrWhiteSpace(RightChessVideo.Text))
        {
            IntrinsicsStatus.Text = "请先选择左右相机的棋盘格视频";
            return;
        }

        CalibrateIntrinsicsBtn.IsEnabled = false;
        IntrinsicsStatus.Text = "正在标定内参，请稍候...（结果写入 calib/ 目录）";
        try
        {
            var ok = await _vm.CalibrateIntrinsicsAsync(
                LeftChessVideo.Text.Trim(), RightChessVideo.Text.Trim());
            IntrinsicsStatus.Text = ok
                ? "✅ 内参标定完成，结果已保存到 calib/，可继续外参标定"
                : "内参标定失败，请查看 logs 文件夹中的日志";
        }
        catch (Exception ex)
        {
            IntrinsicsStatus.Text = "标定异常: " + ex.Message;
        }
        finally
        {
            CalibrateIntrinsicsBtn.IsEnabled = true;
        }
    }

    // ── 外参标定 ──

    private async void CalibrateExtrinsics_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;

        if (string.IsNullOrWhiteSpace(LeftFieldImage.Text) || string.IsNullOrWhiteSpace(RightFieldImage.Text))
        {
            ExtrinsicsStatus.Text = "请先选择射门视角左侧/右侧的参考照片";
            return;
        }

        CalibrateExtrinsicsBtn.IsEnabled = false;
        ExtrinsicsStatus.Text = "正在标定外参，请在弹出的图像窗口依次点击参考点...";
        try
        {
            var ok = await _vm.CalibrateExtrinsicsAsync(
                LeftFieldImage.Text.Trim(), RightFieldImage.Text.Trim());
            ExtrinsicsStatus.Text = ok
                ? "✅ 外参标定完成，结果已保存到 calib/，可以开始处理样本了"
                : "外参标定失败，请查看 logs 文件夹中的日志";
        }
        catch (Exception ex)
        {
            ExtrinsicsStatus.Text = "标定异常: " + ex.Message;
        }
        finally
        {
            CalibrateExtrinsicsBtn.IsEnabled = true;
        }
    }
}
