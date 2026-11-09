using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Diagnostics;

public readonly record struct UiProcessMetrics(
    double WorkingSetMb,
    double PrivateMb,
    double CpuPct,
    int Handles);

public sealed record ProcessDiagnostic(
    uint Pid,
    string Name,
    double CpuPct,
    double GpuPct,
    uint WorkingSetMb);

/// <summary>
/// Point-in-time view of everything the long-run recorder needs. UI assembles
/// it every refresh tick; the recorder throttles disk writes by its interval.
/// </summary>
public sealed record DiagnosticsSnapshot(
    DateTime TimestampUtc,
    double UptimeSeconds,
    int SamplingIntervalMs,
    bool HasSample,
    SampleRecord Sample,
    bool Connected,
    double HeartbeatAgeMs,
    long WriteIndex,
    int CollectorPid,
    string? CollectorError,
    string FrameState,
    int? FrameTargetPid,
    string? FrameError,
    bool FramesFresh,
    double FrameAgeSeconds,
    long FrameWriteIndex,
    FrameStatsSnapshot FrameStats,
    long FramesSeen,
    long SpikesSeen,
    bool HasGpuTemperature,
    double GpuTemperatureC,
    double GpuHotspotC,
    bool HasCpuTemperature,
    double CpuTemperatureC,
    double VramUsedMb,
    double VramTotalMb,
    UiProcessMetrics Ui,
    IReadOnlyList<ProcessDiagnostic> Processes);
