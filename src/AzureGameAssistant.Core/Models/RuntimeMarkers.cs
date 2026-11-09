namespace AzureGameAssistant.Core.Models;

/// <summary>
/// One automatically detected frame-drop episode. Carries only aggregate
/// metrics and a suspect PID; names are resolved later from the whitelist.
/// </summary>
public readonly record struct FrameDropMarker(
    long UtcTicks,
    double StartFrameTimeMs,
    double MaxFrameTimeMs,
    double BaselineFps,
    double AvgCpuWaitMs,
    double AvgGpuWaitMs,
    double CpuPct,
    double GpuPct,
    double MemAvailableMb,
    double DiskLatencyMs,
    uint SuspectPid,
    string Kind);

/// <summary>
/// One sustained hardware high-load episode with the processes that consumed
/// the most of that resource.
/// </summary>
public readonly record struct HighLoadMarker(
    string Resource,
    double Peak,
    double Seconds,
    uint TopPid,
    double TopValue,
    uint SecondPid,
    double SecondValue);
