namespace AzureGameAssistant.Core.Detection;

[Flags]
public enum StutterSignals
{
    None = 0,
    FrameSpike = 1 << 0,
    DisplayGap = 1 << 1,
    Jitter = 1 << 2,
    CpuSpike = 1 << 3,
    GpuSpike = 1 << 4,
    MemoryPressure = 1 << 5,
    DiskPressure = 1 << 6,
    Manual = 1 << 7,
}

public enum StutterCause
{
    Unknown,
    Cpu,
    Gpu,
    Memory,
    Disk,
    FramePacing,
    Manual,
}

/// <summary>
/// One confirmed stutter. Only numbers and the numeric foreground PID are kept;
/// names are resolved by the display layer so no title or path leaks here.
/// </summary>
public readonly record struct StutterEvent(
    long Id,
    DateTime StartUtc,
    DateTime EndUtc,
    double DurationMs,
    int Score,
    StutterSignals Signals,
    StutterCause Cause,
    double PeakFrameTimeMs,
    uint ForegroundPid,
    bool Manual);
