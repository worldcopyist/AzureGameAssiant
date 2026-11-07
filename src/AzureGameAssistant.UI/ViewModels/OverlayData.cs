namespace AzureGameAssistant.UI.ViewModels;

/// <summary>
/// Snapshot handed to the desktop overlay. Frame statistics stay unavailable
/// until the PresentMon pipeline lands in M2.
/// </summary>
public readonly record struct OverlayData(
    bool HasSample,
    bool HasFrames,
    double Fps,
    double AverageFps,
    double LowFps,
    double CpuPct,
    double GpuPct,
    bool HasTemperature,
    double TemperatureC,
    double MemoryPct,
    double VramMb,
    double VramTotalMb);
