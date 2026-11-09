using System.Globalization;
using System.Numerics;
using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Sessions;

/// <summary>
/// Rule-based attribution that always works offline. It only reads the event
/// window and emits qualified language ("suspect/possibly") plus numeric
/// evidence; no window titles or paths are included.
/// </summary>
public static class LocalReportBuilder
{
    public static SessionReportLine Build(
        in StutterEvent stutter,
        IReadOnlyList<SampleRecord> samples,
        IReadOnlyList<FrameTimePoint> frames,
        Func<uint, string> resolveName,
        string? locale)
    {
        bool zh = (locale ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var suspects = BuildSuspects(samples, resolveName);
        SessionFrameEvidence? frameEvidence = BuildFrameEvidence(frames);
        string cause = Classify(stutter, samples, frames);
        string confidence = ResolveConfidence(stutter, cause);
        var evidence = BuildEvidence(samples, frames, suspects, zh);

        return new SessionReportLine
        {
            Cause = cause,
            Confidence = confidence,
            Summary = Summary(cause, zh),
            Suspects = suspects,
            FrameEvidence = frameEvidence,
            Evidence = evidence,
            Suggestions = Suggestions(cause, zh),
        };
    }

    public static string[] SignalNames(StutterSignals signals)
    {
        if (signals == StutterSignals.None)
        {
            return [];
        }

        var names = new List<string>(4);
        foreach (StutterSignals value in Enum.GetValues<StutterSignals>())
        {
            if (value != StutterSignals.None && signals.HasFlag(value))
            {
                names.Add(value.ToString());
            }
        }

        return names.ToArray();
    }

    private static SessionReportSuspect[] BuildSuspects(
        IReadOnlyList<SampleRecord> samples,
        Func<uint, string> resolveName)
    {
        if (samples.Count == 0)
        {
            return [];
        }

        var map = new Dictionary<uint, Accumulator>();
        foreach (SampleRecord sample in samples)
        {
            Observe(sample.P0);
            Observe(sample.P1);
            Observe(sample.P2);
            Observe(sample.P3);
            Observe(sample.P4);
        }

        return map.Values
            .Where(entry => entry.Pid != 0)
            .Select(entry => new
            {
                Entry = entry,
                Weight = (entry.CpuPeak * 1.0) + (entry.GpuPeak * 0.8) +
                         (Math.Max(0, entry.WsMaxMb - entry.WsMinMb) / 64.0),
            })
            .Where(entry => entry.Weight >= 0.5)
            .OrderByDescending(entry => entry.Weight)
            .Take(3)
            .Select(entry => new SessionReportSuspect
            {
                Pid = entry.Entry.Pid,
                Name = Trim(resolveName(entry.Entry.Pid)),
                CpuPeak = Math.Round(entry.Entry.CpuPeak, 1),
                GpuPeak = Math.Round(entry.Entry.GpuPeak, 1),
                WsDeltaMb = Math.Round(Math.Max(0, entry.Entry.WsMaxMb - entry.Entry.WsMinMb), 1),
                Weight = Math.Round(entry.Weight, 1),
            })
            .ToArray();

        void Observe(ProcessSlot slot)
        {
            if (slot.Pid == 0)
            {
                return;
            }

            if (!map.TryGetValue(slot.Pid, out Accumulator? accumulator))
            {
                accumulator = new Accumulator
                {
                    Pid = slot.Pid,
                    WsMinMb = slot.WorkingSetMb,
                };
                map[slot.Pid] = accumulator;
            }

            accumulator.CpuPeak = Math.Max(accumulator.CpuPeak, slot.CpuPct);
            accumulator.GpuPeak = Math.Max(accumulator.GpuPeak, slot.GpuPct);
            accumulator.WsMinMb = Math.Min(accumulator.WsMinMb, slot.WorkingSetMb);
            accumulator.WsMaxMb = Math.Max(accumulator.WsMaxMb, slot.WorkingSetMb);
        }
    }

    private static SessionFrameEvidence? BuildFrameEvidence(IReadOnlyList<FrameTimePoint> frames)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        double cpuWait = 0;
        double gpuWait = 0;
        double display = 0;
        int cpuCount = 0;
        int gpuCount = 0;
        int displayCount = 0;
        double maxFrame = 0;
        int spikes = 0;

        foreach (FrameTimePoint frame in frames)
        {
            maxFrame = Math.Max(maxFrame, frame.FrameTimeMs);
            if (frame.Spike)
            {
                spikes++;
            }

            if (frame.CpuWaitMs > 0)
            {
                cpuWait += frame.CpuWaitMs;
                cpuCount++;
            }

            if (frame.GpuWaitMs > 0)
            {
                gpuWait += frame.GpuWaitMs;
                gpuCount++;
            }

            if (frame.DisplayLatencyMs > 0)
            {
                display += frame.DisplayLatencyMs;
                displayCount++;
            }
        }

        return new SessionFrameEvidence
        {
            FrameCount = frames.Count,
            MaxFrameTimeMs = Math.Round(maxFrame, 1),
            SpikeCount = spikes,
            AvgCpuWaitMs = Math.Round(cpuCount > 0 ? cpuWait / cpuCount : 0, 1),
            AvgGpuWaitMs = Math.Round(gpuCount > 0 ? gpuWait / gpuCount : 0, 1),
            AvgDisplayLatencyMs = Math.Round(displayCount > 0 ? display / displayCount : 0, 1),
            Consistent = true,
        };
    }

    private static string Classify(
        in StutterEvent stutter,
        IReadOnlyList<SampleRecord> samples,
        IReadOnlyList<FrameTimePoint> frames)
    {
        double vramRatio = 0;
        double memAvailable = double.MaxValue;
        double diskBusy = 0;
        double diskLatency = 0;
        double cpuPeak = 0;
        double gpuPeak = 0;

        foreach (SampleRecord sample in samples)
        {
            if (sample.V0.VramTotalMb > 0)
            {
                vramRatio = Math.Max(vramRatio, sample.V0.VramUsedMb / sample.V0.VramTotalMb);
            }

            memAvailable = Math.Min(memAvailable, sample.MemAvailableMb);
            diskBusy = Math.Max(diskBusy, sample.DiskBusyPct);
            diskLatency = Math.Max(diskLatency, sample.DiskLatencyMs);
            cpuPeak = Math.Max(cpuPeak, sample.CpuUtilityPct);
            gpuPeak = Math.Max(gpuPeak, sample.GpuUtilPct);
        }

        StutterSignals signals = stutter.Signals;
        if (vramRatio >= 0.92)
        {
            return "Vram";
        }

        if (signals.HasFlag(StutterSignals.MemoryPressure) || memAvailable < 512)
        {
            return "Memory";
        }

        if (signals.HasFlag(StutterSignals.DiskPressure) || (diskLatency >= 50 && diskBusy >= 50))
        {
            return "Disk";
        }

        bool cpu = signals.HasFlag(StutterSignals.CpuSpike);
        bool gpu = signals.HasFlag(StutterSignals.GpuSpike);
        if (cpu && gpu)
        {
            return gpuPeak >= cpuPeak ? "Gpu" : "Cpu";
        }

        if (cpu)
        {
            return "Cpu";
        }

        if (gpu)
        {
            return "Gpu";
        }

        if (signals.HasFlag(StutterSignals.FrameSpike) || signals.HasFlag(StutterSignals.DisplayGap))
        {
            return "FramePacing";
        }

        if (frames.Count >= 2 && AverageWait(frames, gpu: true) > AverageWait(frames, gpu: false) * 2)
        {
            return "Gpu";
        }

        if (signals.HasFlag(StutterSignals.Manual))
        {
            return "Manual";
        }

        return "Unknown";
    }

    private static double AverageWait(IReadOnlyList<FrameTimePoint> frames, bool gpu)
    {
        double total = 0;
        int count = 0;
        foreach (FrameTimePoint frame in frames)
        {
            double value = gpu ? frame.GpuWaitMs : frame.CpuWaitMs;
            if (value > 0)
            {
                total += value;
                count++;
            }
        }

        return count > 0 ? total / count : 0;
    }

    private static string ResolveConfidence(in StutterEvent stutter, string cause)
    {
        int signals = BitOperations.PopCount((uint)stutter.Signals);
        if (stutter.Score >= 80 || signals >= 3)
        {
            return "High";
        }

        if (stutter.Score >= 50 || signals >= 2)
        {
            return "Medium";
        }

        return cause is "Unknown" or "Manual" ? "Low" : "Medium";
    }

    private static string Summary(string cause, bool zh) => cause switch
    {
        "Cpu" => zh
            ? "疑似后台进程抢占 CPU，导致帧提交出现尖峰。"
            : "CPU contention from a background process likely caused the frame spike.",
        "Gpu" => zh
            ? "疑似 GPU 负载接近上限，帧节奏受限于渲染。"
            : "GPU load was likely near its limit, limiting frame pacing.",
        "Vram" => zh
            ? "疑似显存接近耗尽，资源换入换出拉长了帧时间。"
            : "VRAM was likely close to exhaustion, stretching frame times.",
        "Memory" => zh
            ? "疑似物理内存压力增加，页面活动拖慢了渲染线程。"
            : "Physical memory pressure likely slowed the render path.",
        "Disk" => zh
            ? "疑似磁盘延迟升高，资产读取阻塞了帧提交。"
            : "Elevated disk latency likely blocked asset streaming.",
        "FramePacing" => zh
            ? "资源占用未明显越界，疑似帧节奏或呈现环节异常。"
            : "Resource usage stayed in range; frame pacing or presentation looks abnormal.",
        "Manual" => zh
            ? "这是手动标记的卡顿，本地规则未发现明确的资源越界信号。"
            : "This is a manual mark; local rules found no clear resource overrun.",
        _ => zh
            ? "本地规则未定位到明确主因，需要更多证据。"
            : "Local rules could not identify a clear cause; more evidence is needed.",
    };

    private static string[] BuildEvidence(
        IReadOnlyList<SampleRecord> samples,
        IReadOnlyList<FrameTimePoint> frames,
        SessionReportSuspect[] suspects,
        bool zh)
    {
        var evidence = new List<string>(4);
        if (samples.Count > 0)
        {
            double cpuPeak = samples.Max(sample => sample.CpuUtilityPct);
            double gpuPeak = samples.Max(sample => sample.GpuUtilPct);
            double memMin = samples.Min(sample => (double)sample.MemAvailableMb);
            double diskPeak = samples.Max(sample => sample.DiskLatencyMs);
            evidence.Add(zh
                ? string.Create(CultureInfo.InvariantCulture,
                    $"窗口峰值：CPU {cpuPeak:F0}% · GPU {gpuPeak:F0}% · 可用内存 {memMin:F0} MB · 磁盘 {diskPeak:F0} ms")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Window peaks: CPU {cpuPeak:F0}% · GPU {gpuPeak:F0}% · available memory {memMin:F0} MB · disk {diskPeak:F0} ms"));
        }

        if (frames.Count > 0)
        {
            double maxFrame = frames.Max(frame => (double)frame.FrameTimeMs);
            int spikes = frames.Count(frame => frame.Spike);
            evidence.Add(zh
                ? string.Create(CultureInfo.InvariantCulture,
                    $"帧证据：最大帧 {maxFrame:F1} ms，尖峰 {spikes} 次，共 {frames.Count} 帧")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Frame evidence: max frame {maxFrame:F1} ms, {spikes} spikes across {frames.Count} frames"));
        }
        else
        {
            evidence.Add(zh
                ? "无帧级数据：请开启帧采集后复现，以获得更可靠的归因。"
                : "No frame-level data: enable frame capture and reproduce for stronger attribution.");
        }

        if (suspects.Length > 0)
        {
            SessionReportSuspect top = suspects[0];
            evidence.Add(zh
                ? $"首要可疑进程：{top.Name}（CPU 峰值 {top.CpuPeak:F0}%）"
                : $"Top suspect: {top.Name} (CPU peak {top.CpuPeak:F0}%)");
        }

        return evidence.ToArray();
    }

    private static string[] Suggestions(string cause, bool zh) => cause switch
    {
        "Cpu" => zh
            ? ["关闭或限制后台高 CPU 进程后复现一次。", "把游戏与后台任务分开到不同核心，观察是否仍有尖峰。"]
            : ["Close or limit background CPU-heavy apps, then reproduce.", "Separate the game from background work and re-check the spikes."],
        "Gpu" => zh
            ? ["降低画质、分辨率或帧率上限，观察帧时间是否收敛。", "检查 GPU 温度与功耗是否触发降频。"]
            : ["Lower quality, resolution or frame cap and compare frame times.", "Check GPU temperature and power for throttling."],
        "Vram" => zh
            ? ["降低纹理与阴影质量，关闭占用显存的程序。", "确认显存总量与游戏预设是否匹配。"]
            : ["Lower texture/shadow quality and close VRAM-heavy apps.", "Verify the game preset fits the available VRAM."],
        "Memory" => zh
            ? ["关闭高内存占用的程序，确认页面文件可用。", "减少同时运行的游戏与浏览器标签。"]
            : ["Close memory-heavy apps and verify the page file.", "Reduce concurrently running games and browser tabs."],
        "Disk" => zh
            ? ["把游戏移到更快的磁盘，暂停后台下载与扫描。", "观察磁盘队列与延迟是否随帧尖峰同步升高。"]
            : ["Move the game to a faster disk and pause background downloads.", "Watch whether disk queue/latency rises with the frame spikes."],
        "FramePacing" => zh
            ? ["尝试切换垂直同步或帧率上限，对比帧时间稳定性。", "尝试无边框窗口模式，并更新显卡驱动。"]
            : ["Try toggling V-Sync or a frame cap and compare stability.", "Try borderless mode and update the GPU driver."],
        _ => zh
            ? ["开启帧采集后在相同场景复现，补充帧级证据。", "关闭不必要的后台程序后再次标记，比较两次结果。"]
            : ["Enable frame capture, reproduce in the same scene and add frame evidence.", "Close unnecessary background apps, mark again and compare."],
    };

    private static string Trim(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        name = name.Trim();
        return name.Length <= 64 ? name : name[..64];
    }

    private sealed class Accumulator
    {
        public uint Pid;
        public double CpuPeak;
        public double GpuPeak;
        public double WsMinMb = double.MaxValue;
        public double WsMaxMb;
    }
}
