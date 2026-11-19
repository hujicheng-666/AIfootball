namespace AIfootball.Core.Models;

/// <summary>Python 引擎自检结果（来自 <c>python -m aifootball selfcheck</c> 的单行 JSON）。</summary>
public sealed record EngineReport(
    bool Available,
    string? PythonVersion,
    string? TorchVersion,
    string? OpenCvVersion,
    string? UltralyticsVersion,
    bool CudaAvailable,
    string ModelPath,
    bool ModelPresent,
    string? Error)
{
    public static EngineReport Unavailable(string reason) =>
        new(false, null, null, null, null, false, "", false, reason);

    public string InferenceProfile => CudaAvailable ? "CUDA 加速" : "CPU 推理";

    /// <summary>模型文件名（导航轨环境卡片用）。</summary>
    public string ModelLabel => ModelPresent
        ? Path.GetFileName(ModelPath)
        : "未找到模型";

    /// <summary>Python 版本短标签。</summary>
    public string PythonLabel => Available && !string.IsNullOrWhiteSpace(PythonVersion)
        ? PythonVersion!
        : "不可用";

    public string Summary
    {
        get
        {
            if (!Available) return Error ?? "推理环境不可用";
            var accelerator = CudaAvailable ? "CUDA" : "CPU";
            return $"Python {PythonVersion} · Torch {TorchVersion} · OpenCV {OpenCvVersion} · {accelerator}";
        }
    }
}

/// <summary>显卡信息（WMI + nvidia-smi）。</summary>
public sealed record GraphicsInfo(
    bool HasNvidiaAdapter,
    bool CudaRuntimeAvailable,
    string PrimaryAdapter,
    IReadOnlyList<string> Adapters)
{
    public static GraphicsInfo Unknown { get; } = new(false, false, "未知显卡", []);

    public string ShortLabel => HasNvidiaAdapter
        ? PrimaryAdapter
        : Adapters.Count > 0 ? $"{Adapters[0]}（无 CUDA）" : "未检测到显卡";

    public string AccelerationLabel => CudaRuntimeAvailable ? "GPU 加速可用" : "CPU 模式";
}
