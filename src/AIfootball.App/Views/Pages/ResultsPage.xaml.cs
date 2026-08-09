using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using AIfootball.App.Services.Interfaces;
using AIfootball.App.ViewModels;

namespace AIfootball.App.Views.Pages;

public partial class ResultsPage : UserControl
{
    private MainViewModel? _vm;

    public ResultsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _vm = DataContext as MainViewModel;
    }

    private void OpenOutputDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var engine = App.Services.GetService(typeof(IPythonEngine)) as IPythonEngine;
            var outputDir = System.IO.Path.Combine(engine?.WorkspaceDir ?? ".", "output");
            if (!System.IO.Directory.Exists(outputDir))
                System.IO.Directory.CreateDirectory(outputDir);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = outputDir, UseShellExecute = true });
            _vm?.AddLog("info", $"已打开输出目录: {outputDir}");
        }
        catch (Exception ex) { _vm?.AddLog("error", $"打开失败: {ex.Message}"); }
    }

    private void LaunchUnity_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pipeline = App.Services.GetService(typeof(IPipelineService)) as IPipelineService;
            var samples = pipeline?.ScanSamples() ?? new();
            var done = samples.Where(s => s.HasUnityCsv).Select(s => s.Name).ToList();
            if (done.Count > 0)
            {
                pipeline?.LaunchUnityViewer(done);
                _vm?.AddLog("info", $"启动 Unity 查看器: {string.Join(", ", done)}");
            }
            else
            {
                _vm?.AddLog("warn", "没有已完成的样本，请先运行流水线处理");
                MessageBox.Show("暂无已完成处理的样本。\n\n请先在「流水线处理」页面执行处理。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { _vm?.AddLog("error", $"启动失败: {ex.Message}"); }
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var engine = App.Services.GetService(typeof(IPythonEngine)) as IPythonEngine;
            var dataDir = System.IO.Path.Combine(engine?.WorkspaceDir ?? ".", "data");
            if (System.IO.Directory.Exists(dataDir))
            {
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = dataDir, UseShellExecute = true });
                _vm?.AddLog("info", $"已打开数据目录: {dataDir}");
            }
            else
            {
                _vm?.AddLog("warn", "数据目录不存在，请先运行流水线处理");
                MessageBox.Show("数据目录尚不存在。请先运行流水线处理。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex) { _vm?.AddLog("error", $"导出失败: {ex.Message}"); }
    }
}
