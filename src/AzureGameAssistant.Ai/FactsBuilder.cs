using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Hardware;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Ai;

public sealed record FactsInput(
    IReadOnlyList<SampleRecord> Samples,
    IReadOnlyList<FrameRecord> Frames,
    IReadOnlyDictionary<uint, string> ProcessNames,
    string Mode,
    string Locale,
    string? Note = null,
    IReadOnlyList<FrameDropMarker>? Drops = null,
    IReadOnlyList<HighLoadMarker>? HighLoads = null);

/// <summary>
/// Builds the fixed whitelist fact layer. No window titles, command lines,
/// paths, user names, machine names, IPs or MACs are ever read.
/// </summary>
public static class FactsBuilder
{
    private const int MaxProcesses = 8;
    private const int MaxMarkers = 6;
    private const int MaxNameLength = 64;
    private const double DisplayGapFrameMs = 100.0;

    public static FactsDocument Build(
        FactsInput input,
        bool hideProcessNames,
        SystemInventorySnapshot? inventory = null)
    {
        inventory ??= SystemInventory.Capture();

        var document = new FactsDocument
        {
            Locale = string.IsNullOrWhiteSpace(input.Locale) ? "zh-CN" : input.Locale,
            Note = SanitizeText(input.Note, 500),
            Environment = new FactsEnvironment
            {
                Os = inventory.Os,
                Cpu = inventory.Cpu,
                LogicalProcessors = inventory.LogicalProcessors,
                MemoryGb = inventory.MemoryGb > 0 ? inventory.MemoryGb : null,
                Gpu = inventory.Gpu.ToList(),
                Driver = inventory.Driver,
                Mode = string.Equals(input.Mode, "deep", StringComparison.OrdinalIgnoreCase) ? "deep" : "shallow",
                SamplingMs = MedianIntervalMs(input.Samples),
            },
            Metrics = BuildMetrics(input.Samples),
            Frames = BuildFrames(input.Frames),
            Sensors = BuildSensors(input.Samples),
        };

        var processNames = new Dictionary<uint, string>();
        document.Processes = BuildProcesses(input.Samples, input.ProcessNames, hideProcessNames, processNames);

        long windowEndTicks = input.Samples.Count > 0
            ? input.Samples[^1].UtcTicks
            : DateTime.UtcNow.Ticks;
        document.Drops = BuildDrops(input.Drops, windowEndTicks, input.ProcessNames, processNames, hideProcessNames);
        document.HighLoads = BuildHighLoads(input.HighLoads, input.ProcessNames, processNames, hideProcessNames);
        document.Gaps = BuildGaps(input.Samples, input.Frames, document);

        return document;
    }

    private static FactsMetrics BuildMetrics(IReadOnlyList<SampleRecord> samples)
    {
        if (samples.Count == 0)
        {
            return new FactsMetrics();
        }

        double cpuPeak = 0;
        double cpuSum = 0;
        double gpuPeak = 0;
        double gpuSum = 0;
        double vramUsed = 0;
        double vramTotal = 0;
        uint memAvailableMin = uint.MaxValue;
        double commitPeak = 0;
        double diskLatencyPeak = 0;
        double jitterPeak = 0;

        foreach (SampleRecord sample in samples)
        {
            cpuPeak = Math.Max(cpuPeak, sample.CpuUtilityPct);
            cpuSum += sample.CpuUtilityPct;
            gpuPeak = Math.Max(gpuPeak, sample.GpuUtilPct);
            gpuSum += sample.GpuUtilPct;
            memAvailableMin = Math.Min(memAvailableMin, sample.MemAvailableMb);
            diskLatencyPeak = Math.Max(diskLatencyPeak, sample.DiskLatencyMs);
            jitterPeak = Math.Max(jitterPeak, sample.TimerJitterMs);

            if (sample.MemCommitLimitMb > 0)
            {
                commitPeak = Math.Max(commitPeak, (double)sample.MemCommitMb / sample.MemCommitLimitMb);
            }

            if (TelemetryMath.HasVendorData(sample))
            {
                vramUsed = Math.Max(vramUsed, sample.V0.VramUsedMb);
                vramTotal = Math.Max(vramTotal, sample.V0.VramTotalMb);
            }
        }

        return new FactsMetrics
        {
            CpuPeakPct = Round(cpuPeak),
            CpuAvgPct = Round(cpuSum / samples.Count),
            GpuPeakPct = Round(gpuPeak),
            GpuAvgPct = Round(gpuSum / samples.Count),
            VramUsedMb = vramTotal > 0 ? Round(vramUsed) : null,
            VramTotalMb = vramTotal > 0 ? Round(vramTotal) : null,
            MemAvailableMb = memAvailableMin == uint.MaxValue ? null : memAvailableMin,
            CommitRatio = commitPeak > 0 ? Math.Round(commitPeak, 3) : null,
            DiskLatencyMs = Round(diskLatencyPeak),
            TimerJitterMaxMs = Round(jitterPeak),
        };
    }

    private static FactsFrames? BuildFrames(IReadOnlyList<FrameRecord> frames)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        var stats = new FrameStats(frames.Count);
        var times = new List<double>(frames.Count);
        double cpuWait = 0;
        double gpuWait = 0;
        int displayGaps = 0;

        foreach (FrameRecord frame in frames)
        {
            if (!(frame.FrameTimeMs > 0) || float.IsInfinity(frame.FrameTimeMs))
            {
                continue;
            }

            stats.Add(frame.FrameTimeMs);
            times.Add(frame.FrameTimeMs);
            cpuWait += frame.CpuWaitMs;
            gpuWait += frame.GpuWaitMs;
            if (frame.FrameTimeMs > DisplayGapFrameMs)
            {
                displayGaps++;
            }
        }

        if (times.Count == 0)
        {
            return null;
        }

        FrameStatsSnapshot snapshot = stats.Snapshot();
        times.Sort();
        double p99 = times[Math.Clamp((int)Math.Ceiling(times.Count * 0.99) - 1, 0, times.Count - 1)];

        return new FactsFrames
        {
            Count = times.Count,
            AvgFps = Round(snapshot.AverageFps),
            OnePercentLowFps = Round(snapshot.OnePercentLowFps),
            PointOnePercentLowFps = Round(snapshot.PointOnePercentLowFps),
            MaxFrameTimeMs = Round(times[^1]),
            P99FrameTimeMs = Round(p99),
            AvgCpuWaitMs = Round(cpuWait / times.Count),
            AvgGpuWaitMs = Round(gpuWait / times.Count),
            DisplayGapCount = displayGaps,
        };
    }

    private static FactsSensors? BuildSensors(IReadOnlyList<SampleRecord> samples)
    {
        if (samples.Count == 0)
        {
            return null;
        }

        SampleRecord latest = samples[^1];
        bool vendor = TelemetryMath.HasVendorData(latest);
        bool cpu = TelemetryMath.HasCpuSensorData(latest);

        if (!vendor && !cpu)
        {
            return null;
        }

        return new FactsSensors
        {
            GpuTemperatureC = vendor ? Positive(latest.V0.TemperatureC) : null,
            GpuHotSpotC = vendor ? Positive(latest.V0.HotSpotC) : null,
            GpuPowerW = vendor ? Positive(latest.V0.PowerW) : null,
            GpuCoreClockMhz = vendor ? Positive(latest.V0.CoreClockMhz) : null,
            GpuFanPct = vendor ? Positive(latest.V0.FanPct) : null,
            CpuTemperatureC = cpu ? Positive(latest.C0.TemperatureC) : null,
            CpuPowerW = cpu ? Positive(latest.C0.PackagePowerW) : null,
        };
    }

    private static List<FactsProcess> BuildProcesses(
        IReadOnlyList<SampleRecord> samples,
        IReadOnlyDictionary<uint, string> names,
        bool hideNames,
        Dictionary<uint, string> mapped)
    {
        var aggregated = new Dictionary<uint, ProcessAggregate>();

        foreach (SampleRecord sample in samples)
        {
            AddSlot(sample, sample.P0, aggregated);
            AddSlot(sample, sample.P1, aggregated);
            AddSlot(sample, sample.P2, aggregated);
            AddSlot(sample, sample.P3, aggregated);
            AddSlot(sample, sample.P4, aggregated);
        }

        List<ProcessAggregate> top = aggregated.Values
            .OrderByDescending(process => process.CpuPeakPct)
            .ThenByDescending(process => process.GpuPeakPct)
            .ThenByDescending(process => process.PrivateMb)
            .Take(MaxProcesses)
            .ToList();

        var result = new List<FactsProcess>(top.Count);
        foreach (ProcessAggregate process in top)
        {
            result.Add(new FactsProcess
            {
                Name = ResolveName(process.Pid, mapped, names, hideNames),
                Role = process.Foreground ? "foreground" : "background",
                CpuPeakPct = Round(process.CpuPeakPct),
                GpuPeakPct = Round(process.GpuPeakPct),
                PrivateMb = process.PrivateMb > 0 ? process.PrivateMb : null,
            });
        }

        return result;
    }

    private static List<FactsDrop> BuildDrops(
        IReadOnlyList<FrameDropMarker>? drops,
        long windowEndTicks,
        IReadOnlyDictionary<uint, string> names,
        Dictionary<uint, string> mapped,
        bool hideNames)
    {
        var result = new List<FactsDrop>();
        if (drops is null || drops.Count == 0)
        {
            return result;
        }

        int start = Math.Max(0, drops.Count - MaxMarkers);
        for (int i = start; i < drops.Count; i++)
        {
            FrameDropMarker marker = drops[i];
            double secondsAgo = marker.UtcTicks > 0 && windowEndTicks > marker.UtcTicks
                ? Math.Round((windowEndTicks - marker.UtcTicks) / (double)TimeSpan.TicksPerSecond, 1)
                : 0;

            result.Add(new FactsDrop
            {
                SecondsAgo = secondsAgo,
                FrameTimeMs = Round(marker.MaxFrameTimeMs) ?? 0,
                FpsBefore = marker.BaselineFps > 0 ? Round(marker.BaselineFps) : null,
                FpsDuring = marker.MaxFrameTimeMs > 0
                    ? Math.Round(1000.0 / marker.MaxFrameTimeMs, 1)
                    : 0,
                CpuWaitMs = marker.AvgCpuWaitMs > 0 ? Round(marker.AvgCpuWaitMs) : null,
                GpuWaitMs = marker.AvgGpuWaitMs > 0 ? Round(marker.AvgGpuWaitMs) : null,
                Kind = marker.Kind,
                SuspectProcess = marker.SuspectPid != 0
                    ? ResolveName(marker.SuspectPid, mapped, names, hideNames)
                    : null,
                CpuPct = Round(marker.CpuPct),
                GpuPct = Round(marker.GpuPct),
                MemAvailableMb = marker.MemAvailableMb > 0 ? Round(marker.MemAvailableMb) : null,
                DiskLatencyMs = marker.DiskLatencyMs > 0 ? Round(marker.DiskLatencyMs) : null,
            });
        }

        return result;
    }

    private static List<FactsHighLoad> BuildHighLoads(
        IReadOnlyList<HighLoadMarker>? highLoads,
        IReadOnlyDictionary<uint, string> names,
        Dictionary<uint, string> mapped,
        bool hideNames)
    {
        var result = new List<FactsHighLoad>();
        if (highLoads is null || highLoads.Count == 0)
        {
            return result;
        }

        int start = Math.Max(0, highLoads.Count - MaxMarkers);
        for (int i = start; i < highLoads.Count; i++)
        {
            HighLoadMarker marker = highLoads[i];
            result.Add(new FactsHighLoad
            {
                Resource = marker.Resource,
                PeakPct = Math.Round(marker.Peak, 1),
                Seconds = Math.Round(marker.Seconds, 1),
                TopProcess = marker.TopPid != 0
                    ? ResolveName(marker.TopPid, mapped, names, hideNames)
                    : null,
                TopProcessPct = marker.TopPid != 0 ? Math.Round(marker.TopValue, 1) : null,
                SecondProcess = marker.SecondPid != 0
                    ? ResolveName(marker.SecondPid, mapped, names, hideNames)
                    : null,
                SecondProcessPct = marker.SecondPid != 0 ? Math.Round(marker.SecondValue, 1) : null,
            });
        }

        return result;
    }

    private static string ResolveName(
        uint pid,
        Dictionary<uint, string> mapped,
        IReadOnlyDictionary<uint, string> names,
        bool hideNames)
    {
        if (mapped.TryGetValue(pid, out string? existing))
        {
            return existing;
        }

        string name = hideNames
            ? "process_" + (mapped.Count + 1)
            : SanitizeProcessName(names.TryGetValue(pid, out string? known) ? known : "unknown");
        mapped[pid] = name;
        return name;
    }

    private static void AddSlot(SampleRecord sample, in ProcessSlot slot, Dictionary<uint, ProcessAggregate> aggregated)
    {
        if (slot.Pid == 0)
        {
            return;
        }

        if (!aggregated.TryGetValue(slot.Pid, out ProcessAggregate? process))
        {
            process = new ProcessAggregate(slot.Pid);
            aggregated[slot.Pid] = process;
        }

        process.CpuPeakPct = Math.Max(process.CpuPeakPct, slot.CpuPct);
        process.GpuPeakPct = Math.Max(process.GpuPeakPct, slot.GpuPct);
        process.PrivateMb = Math.Max(process.PrivateMb, slot.PrivateMb);
        process.Foreground |= slot.Pid == sample.ForegroundPid ||
                              (slot.Flags & ProcessSlotFlags.Foreground) != 0;
    }

    private static List<string> BuildGaps(
        IReadOnlyList<SampleRecord> samples,
        IReadOnlyList<FrameRecord> frames,
        FactsDocument document)
    {
        var gaps = new List<string>(4);

        if (samples.Count < 2)
        {
            gaps.Add("可用系统采样少于 2 个，指标趋势仅供参考");
        }
        else
        {
            int median = MedianIntervalMs(samples);
            long maxGapTicks = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                maxGapTicks = Math.Max(maxGapTicks, samples[i].UtcTicks - samples[i - 1].UtcTicks);
            }

            double maxGapMs = maxGapTicks / (double)TimeSpan.TicksPerMillisecond;
            if (median > 0 && maxGapMs > Math.Max(3.0 * median, 3_000))
            {
                gaps.Add($"采样在窗口内出现 {Math.Round(maxGapMs)}ms 缺口");
            }
        }

        if (frames.Count == 0)
        {
            gaps.Add("窗口内没有帧数据（可能未开始帧采集或缺少管理员权限）");
        }

        if (document.Sensors?.CpuTemperatureC is null)
        {
            gaps.Add("CPU 温度不可用");
        }

        if (document.Sensors?.GpuTemperatureC is null)
        {
            gaps.Add("GPU 温度不可用");
        }

        return gaps;
    }

    private static int MedianIntervalMs(IReadOnlyList<SampleRecord> samples)
    {
        if (samples.Count < 2)
        {
            return 1_000;
        }

        var intervals = new List<long>(samples.Count - 1);
        for (int i = 1; i < samples.Count; i++)
        {
            long delta = samples[i].UtcTicks - samples[i - 1].UtcTicks;
            if (delta > 0)
            {
                intervals.Add(delta);
            }
        }

        if (intervals.Count == 0)
        {
            return 1_000;
        }

        intervals.Sort();
        double median = intervals[intervals.Count / 2] / (double)TimeSpan.TicksPerMillisecond;
        return Math.Max(1, (int)Math.Round(median));
    }

    private static string SanitizeProcessName(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value.Trim())
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }

            if (builder.Length >= MaxNameLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string? SanitizeText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value.Trim())
        {
            if (!char.IsControl(ch))
            {
                builder.Append(ch);
            }

            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static double? Positive(float value) =>
        value > 0 && !float.IsNaN(value) && !float.IsInfinity(value) ? Round(value) : null;

    private static double? Round(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : Math.Round(value, 1);

    private sealed class ProcessAggregate(uint pid)
    {
        public uint Pid { get; } = pid;
        public double CpuPeakPct { get; set; }
        public double GpuPeakPct { get; set; }
        public double PrivateMb { get; set; }
        public bool Foreground { get; set; }
    }
}
