using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using System.Text.Json.Serialization;

namespace AzureGameAssistant.Core.Sessions;

/// <summary>
/// Session file schema v2. One JSON object per line inside a gzip stream.
/// Times are UTC ticks so a replay never depends on the local time zone.
/// </summary>
public static class SessionSchema
{
    public const int Version = 2;
    public const string Extension = ".agsession.jsonl.gz";
    public const string RecordingState = "recording";
    public const string CompleteState = "complete";
}

/// <summary>
/// JSON cannot represent NaN or infinity. Collector counters may briefly carry
/// them (PDH first sample, missing sensors), so every number crosses this guard
/// before it reaches the writer.
/// </summary>
public static class SessionNumbers
{
    public static double Finite(double value) => double.IsFinite(value) ? value : 0;

    public static double Round(double value, int digits) =>
        double.IsFinite(value) ? Math.Round(value, digits) : 0;
}

public sealed class SessionHeaderLine
{
    public string Type { get; set; } = "header";
    public int SchemaVersion { get; set; } = SessionSchema.Version;
    public string State { get; set; } = SessionSchema.RecordingState;
    public string AppVersion { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string Cpu { get; set; } = string.Empty;
    public string Gpu { get; set; } = string.Empty;
    public int SamplingMs { get; set; } = 1000;
    public long WindowStartUtc { get; set; }
    public long WindowEndUtc { get; set; }
    public long PlannedEndUtc { get; set; }
    public string Mode { get; set; } = "shallow";
    public string PresentMon { get; set; } = string.Empty;
    public string Sensors { get; set; } = "off";
    public bool Elevated { get; set; }
    public string CpuTempSource { get; set; } = string.Empty;
    public string[] Capabilities { get; set; } = [];
    public string Locale { get; set; } = "zh-CN";

    /// <summary>
    /// Duplicated event summary so the session list can render a row after
    /// reading only the first line of the file.
    /// </summary>
    public SessionEventSummary? Event { get; set; }
}

public sealed class SessionEventSummary
{
    public long Id { get; set; }
    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
    public double DurationMs { get; set; }
    public int Score { get; set; }
    public string[] Signals { get; set; } = [];
    public string Cause { get; set; } = "Unknown";
    public double PeakFrameTimeMs { get; set; }
    public uint ForegroundPid { get; set; }
    public string? ProcessName { get; set; }
    public bool Manual { get; set; }
}

public sealed class SessionEventLine
{
    public string Type { get; set; } = "event";
    public long Id { get; set; }
    public long StartUtc { get; set; }
    public long EndUtc { get; set; }
    public double DurationMs { get; set; }
    public int Score { get; set; }
    public string[] Signals { get; set; } = [];
    public string Cause { get; set; } = "Unknown";
    public double PeakFrameTimeMs { get; set; }
    public uint ForegroundPid { get; set; }
    public string? ProcessName { get; set; }
    public bool Manual { get; set; }
}

public sealed class SessionSampleLine
{
    public string Type { get; set; } = "sample";
    public long Qpc { get; set; }
    public long Utc { get; set; }
    public int Flags { get; set; }
    public double Cpu { get; set; }
    public double Freq { get; set; }
    public double MemAvailMb { get; set; }
    public double CommitMb { get; set; }
    public double CommitLimitMb { get; set; }
    public double PageFilePct { get; set; }
    public double DiskBusy { get; set; }
    public double DiskMs { get; set; }
    public double Gpu { get; set; }
    public double GpuDedicatedMb { get; set; }
    public double GpuSharedMb { get; set; }
    public double JitterMs { get; set; }
    public uint FgPid { get; set; }
    public double QueueLength { get; set; }
    public SessionProcessLine[]? Procs { get; set; }
    public SessionVendorLine? Vendor { get; set; }
    public SessionCpuSensorLine? CpuSensor { get; set; }
}

public sealed class SessionProcessLine
{
    public uint Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint StartTimeLow { get; set; }
    public double Cpu { get; set; }
    public double Gpu { get; set; }
    public double WorkingSetMb { get; set; }
    public double PrivateMb { get; set; }
    public bool Foreground { get; set; }
}

public sealed class SessionVendorLine
{
    public double TempC { get; set; }
    public double HotspotC { get; set; }
    public double PowerW { get; set; }
    public double CoreMhz { get; set; }
    public double MemMhz { get; set; }
    public double FanPct { get; set; }
    public double VramUsedMb { get; set; }
    public double VramTotalMb { get; set; }
}

public sealed class SessionCpuSensorLine
{
    public double TempC { get; set; }
    public double PowerW { get; set; }
}

public sealed class SessionFrameBatchLine
{
    public string Type { get; set; } = "frames";
    public int N { get; set; }
    public int Pid { get; set; }
    public long[] Q { get; set; } = [];
    public long[] U { get; set; } = [];
    public double[] Ft { get; set; } = [];
    public double[] Cb { get; set; } = [];
    public double[] Cw { get; set; } = [];
    public double[] Gl { get; set; } = [];
    public double[] Gb { get; set; } = [];
    public double[] Gw { get; set; } = [];
    public double[] Dl { get; set; } = [];
    public double[] Ae { get; set; } = [];
    public int[] Pm { get; set; } = [];
    public byte[] Sp { get; set; } = [];
}

public sealed class SessionReportLine
{
    public string Type { get; set; } = "report";
    public string Cause { get; set; } = "Unknown";
    public string Confidence { get; set; } = "Low";
    public string Summary { get; set; } = string.Empty;
    public SessionReportSuspect[] Suspects { get; set; } = [];
    public SessionFrameEvidence? FrameEvidence { get; set; }
    public string[] Evidence { get; set; } = [];
    public string[] Suggestions { get; set; } = [];
}

public sealed class SessionReportSuspect
{
    public uint Pid { get; set; }
    public string Name { get; set; } = string.Empty;
    public double CpuPeak { get; set; }
    public double GpuPeak { get; set; }
    public double WsDeltaMb { get; set; }
    public double Weight { get; set; }
}

public sealed class SessionFrameEvidence
{
    public int FrameCount { get; set; }
    public int GapCount { get; set; }
    public double MaxFrameTimeMs { get; set; }
    public int SpikeCount { get; set; }
    public double AvgCpuWaitMs { get; set; }
    public double AvgGpuWaitMs { get; set; }
    public double AvgDisplayLatencyMs { get; set; }
    public bool Consistent { get; set; }
}

public sealed class SessionFooterLine
{
    public string Type { get; set; } = "footer";
    public bool Complete { get; set; }
    public long EndUtc { get; set; }
    public int Samples { get; set; }
    public int Frames { get; set; }
    public int Dropped { get; set; }
    public int FrameDropped { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed record SessionInfo(
    string Path,
    string FileName,
    DateTime CreatedUtc,
    DateTime StartUtc,
    DateTime EndUtc,
    bool Complete,
    long EventId,
    int Score,
    string Cause,
    string? ProcessName,
    int SamplingMs,
    string Mode,
    long SizeBytes);

public sealed class SessionDocument
{
    public required SessionHeaderLine Header { get; init; }
    public required SessionEventLine Event { get; init; }
    public required SampleRecord[] Samples { get; init; }
    public required FrameTimePoint[] Frames { get; init; }
    public SessionReportLine? Report { get; init; }
    public SessionFooterLine? Footer { get; init; }
    public required Dictionary<uint, string> Names { get; init; }
}

public sealed record SessionContext(
    string AppVersion,
    string Os,
    string Cpu,
    string Gpu,
    string Mode,
    int SamplingMs,
    string PresentMon,
    string Sensors,
    bool Elevated,
    string CpuTempSource,
    string[] Capabilities,
    string Locale);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionHeaderLine))]
[JsonSerializable(typeof(SessionEventLine))]
[JsonSerializable(typeof(SessionSampleLine))]
[JsonSerializable(typeof(SessionFrameBatchLine))]
[JsonSerializable(typeof(SessionReportLine))]
[JsonSerializable(typeof(SessionFooterLine))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
