using System.Text.Json;
using System.Text.Json.Serialization;

namespace AzureGameAssistant.Ai;

public sealed class FactsDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Locale { get; set; } = "zh-CN";
    public FactsEnvironment Environment { get; set; } = new();
    public FactsMetrics Metrics { get; set; } = new();
    public FactsFrames? Frames { get; set; }
    public List<FactsProcess> Processes { get; set; } = [];
    public FactsSensors? Sensors { get; set; }
    public List<string> Gaps { get; set; } = [];
    public string? Note { get; set; }
}

public sealed class FactsEnvironment
{
    public string? Os { get; set; }
    public string? Cpu { get; set; }
    public int LogicalProcessors { get; set; }
    public double? MemoryGb { get; set; }
    public List<string> Gpu { get; set; } = [];
    public string? Driver { get; set; }
    public string Mode { get; set; } = "shallow";
    public int SamplingMs { get; set; }
}

public sealed class FactsMetrics
{
    public double? CpuPeakPct { get; set; }
    public double? CpuAvgPct { get; set; }
    public double? GpuPeakPct { get; set; }
    public double? GpuAvgPct { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }
    public double? MemAvailableMb { get; set; }
    public double? CommitRatio { get; set; }
    public double? DiskLatencyMs { get; set; }
    public double? TimerJitterMaxMs { get; set; }
}

public sealed class FactsFrames
{
    public int Count { get; set; }
    public double? AvgFps { get; set; }
    public double? OnePercentLowFps { get; set; }
    public double? PointOnePercentLowFps { get; set; }
    public double? MaxFrameTimeMs { get; set; }
    public double? P99FrameTimeMs { get; set; }
    public double? AvgCpuWaitMs { get; set; }
    public double? AvgGpuWaitMs { get; set; }
    public int DisplayGapCount { get; set; }
}

public sealed class FactsProcess
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "background";
    public double? CpuPeakPct { get; set; }
    public double? GpuPeakPct { get; set; }
    public double? PrivateMb { get; set; }
}

public sealed class FactsSensors
{
    public double? GpuTemperatureC { get; set; }
    public double? GpuHotSpotC { get; set; }
    public double? GpuPowerW { get; set; }
    public double? GpuCoreClockMhz { get; set; }
    public double? GpuFanPct { get; set; }
    public double? CpuTemperatureC { get; set; }
    public double? CpuPowerW { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(FactsDocument))]
public partial class FactsJsonContext : JsonSerializerContext
{
}

public static class FactsSerializer
{
    public static string Serialize(FactsDocument document) =>
        JsonSerializer.Serialize(document, FactsJsonContext.Default.FactsDocument);
}
