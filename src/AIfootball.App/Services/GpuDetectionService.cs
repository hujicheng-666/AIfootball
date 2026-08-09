using System.Diagnostics;
using AIfootball.App.Models;
using AIfootball.App.Services.Interfaces;

namespace AIfootball.App.Services;

/// <summary>GPU 检测服务</summary>
public class GpuDetectionService : IGpuDetectionService
{
    public async Task<GpuInfo> DetectAsync() => await Task.Run(Detect);

    private static GpuInfo Detect()
    {
        string adapters = DetectWindowsAdapters();
        bool hasNvidia = false;
        string gpuName = "";
        string cudaVersion = "";
        bool cudaAvailable = false;

        try
        {
            var output = RunNvidiaSmi("-L");
            if (!string.IsNullOrEmpty(output) && output.Contains("GPU"))
            {
                hasNvidia = true;
                gpuName = output.Split('\n')[0].Trim();
            }
        }
        catch { }

        if (hasNvidia)
        {
            try
            {
                cudaVersion = RunNvidiaSmi("--query-gpu=driver_version --format=csv,noheader");
                cudaAvailable = !string.IsNullOrEmpty(cudaVersion);
            }
            catch { cudaAvailable = true; }
        }

        bool hasDedicated = hasNvidia || LooksLikeNonNvidiaDedicated(adapters);
        return new GpuInfo(hasNvidia, cudaAvailable, gpuName, cudaVersion, hasDedicated, adapters);
    }

    private static string DetectWindowsAdapters()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join '; '");

            using var proc = Process.Start(psi);
            if (proc == null) return "";
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0 ? output : "";
        }
        catch { return ""; }
    }

    private static bool LooksLikeNonNvidiaDedicated(string adapters)
    {
        var upper = adapters.ToUpperInvariant();
        return upper.Contains("RADEON RX") || upper.Contains("RADEON PRO")
            || upper.Contains("FIREPRO") || upper.Contains("ARC");
    }

    private static string RunNvidiaSmi(string args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        proc.Start();
        proc.WaitForExit(5000);
        return proc.StandardOutput.ReadToEnd().Trim();
    }
}
