using System.Diagnostics;
using Microsoft.Win32;
using AIfootball.Core.Models;

namespace AIfootball.Core.Services;

/// <summary>
/// 显卡探测。旧实现走 WMI（首次查询要几百毫秒且可能抛异常）。
/// 这里改读注册表里的显示适配器类，零依赖、毫秒级返回；CUDA 是否真的可用由引擎自检给出。
/// </summary>
public static class GraphicsProbe
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static GraphicsInfo Detect(bool cudaRuntimeAvailable = false)
    {
        var adapters = ReadAdapterNames();
        if (adapters.Count == 0) return GraphicsInfo.Unknown with { CudaRuntimeAvailable = cudaRuntimeAvailable };

        var nvidia = adapters.FirstOrDefault(a => a.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        return new GraphicsInfo(
            HasNvidiaAdapter: nvidia is not null,
            CudaRuntimeAvailable: cudaRuntimeAvailable,
            PrimaryAdapter: nvidia ?? adapters[0],
            Adapters: adapters);
    }

    private static List<string> ReadAdapterNames()
    {
        var names = new List<string>();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (key is null) return names;
            foreach (var subKeyName in key.GetSubKeyNames())
            {
                if (subKeyName.Length != 4 || !int.TryParse(subKeyName, out _)) continue;
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey?.GetValue("DriverDesc") is string description && description.Length > 0)
                    names.Add(description);
            }
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return names;
    }

    /// <summary>nvidia-smi 可用时给出更具体的型号（可选增强，失败不影响主流程）。</summary>
    public static string? TryQueryNvidiaSmi()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(2000);
            return output.Length > 0 ? output.Split('\n')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
