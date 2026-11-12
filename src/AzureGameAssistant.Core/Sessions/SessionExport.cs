using System.Globalization;
using System.Text;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Sessions;

/// <summary>
/// Model-supplied analysis that the caller may attach to an exported report.
/// Core stays independent of the AI project, so the UI flattens its AI report
/// into these strings first.
/// </summary>
public sealed record SessionAiSummary(
    string Summary,
    string PrimaryCause,
    string Confidence,
    string[] Evidence,
    string[] Recommendations,
    string[] Limitations,
    string Disclaimer);

/// <summary>
/// Builds the Markdown report for one session. Everything is derived from the
/// session file that is already on disk; no network or extra telemetry is used.
/// </summary>
public static class SessionMarkdownReport
{
    public const string ChartSuffix = "-timeline.png";
    public const string MarkdownExtension = ".md";

    private const double DiscontinuityFrameMs = 1000.0;

    public static string BuildFileBaseName(SessionEventLine eventLine)
    {
        string time = eventLine.StartUtc > 0
            ? new DateTime(eventLine.StartUtc, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            : "unknown";
        string cause = string.IsNullOrWhiteSpace(eventLine.Cause) ? "Unknown" : eventLine.Cause;
        return $"{time}-{cause}";
    }

    public static string Build(
        SessionDocument document,
        string? locale,
        SessionAiSummary? ai,
        string chartFileName)
    {
        ArgumentNullException.ThrowIfNull(document);
        bool zh = (locale ?? string.Empty).StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        SessionHeaderLine header = document.Header;
        SessionEventLine eventLine = document.Event;
        SessionReportLine? report = document.Report;

        SampleRecord[] samples = SelectSamples(document);
        FrameTimePoint[] frames = document.Frames;
        var builder = new StringBuilder(2048);

        builder.AppendLine(zh ? "# 蓝天游戏助手 · 会话报告" : "# Azure Game Assistant · Session report");
        builder.AppendLine();
        builder.AppendLine(zh
            ? $"- 事件：{LocalTime(eventLine.StartUtc)} · 评分 {eventLine.Score} · 主因 {CauseText(eventLine.Cause, zh)}"
            : $"- Event: {LocalTime(eventLine.StartUtc)} · score {eventLine.Score} · cause {CauseText(eventLine.Cause, zh)}");
        builder.AppendLine(zh
            ? $"- 时长：{eventLine.DurationMs / 1000.0:0.0} 秒 · 信号：{SignalText(eventLine.Signals, zh)}"
            : $"- Duration: {eventLine.DurationMs / 1000.0:0.0} s · signals: {SignalText(eventLine.Signals, zh)}");
        if (!string.IsNullOrWhiteSpace(eventLine.ProcessName))
        {
            builder.AppendLine(zh
                ? $"- 前台进程：{eventLine.ProcessName} · PID {eventLine.ForegroundPid}"
                : $"- Foreground process: {eventLine.ProcessName} · PID {eventLine.ForegroundPid}");
        }

        if (header.WindowStartUtc > 0 && header.WindowEndUtc > header.WindowStartUtc)
        {
            builder.AppendLine(zh
                ? $"- 数据窗口：{LocalTime(header.WindowStartUtc)} – {LocalTime(header.WindowEndUtc)}"
                : $"- Data window: {LocalTime(header.WindowStartUtc)} – {LocalTime(header.WindowEndUtc)}");
        }
        builder.AppendLine(zh
            ? $"- 文件状态：{(document.Footer is { Complete: true } ? "完整" : "录制中")} · 采样 {document.Samples.Length} · 帧 {frames.Length}"
            : $"- File state: {(document.Footer is { Complete: true } ? "complete" : "recording")} · samples {document.Samples.Length} · frames {frames.Length}");

        builder.AppendLine();
        builder.AppendLine(zh ? "## 环境摘要" : "## Environment");
        builder.AppendLine();
        builder.AppendLine(zh ? "| 项 | 值 |" : "| Item | Value |");
        builder.AppendLine("| --- | --- |");
        AppendRow(builder, zh ? "应用版本" : "App version", header.AppVersion);
        AppendRow(builder, "OS", header.Os);
        AppendRow(builder, "CPU", header.Cpu);
        AppendRow(builder, "GPU", header.Gpu);
        AppendRow(builder, zh ? "模式" : "Mode", $"{header.Mode} · {header.SamplingMs} ms");
        AppendRow(builder, zh ? "权限" : "Privilege", header.Elevated ? (zh ? "管理员" : "elevated") : (zh ? "普通" : "standard"));
        AppendRow(builder, zh ? "传感器" : "Sensors", header.Sensors);
        AppendRow(builder, zh ? "CPU 温度来源" : "CPU temperature source", header.CpuTempSource);
        AppendRow(builder, "PresentMon", header.PresentMon);

        builder.AppendLine();
        builder.AppendLine(zh ? "## 事件时间轴" : "## Timeline");
        builder.AppendLine();
        builder.AppendLine($"![{ (zh ? "会话时间轴" : "Session timeline") }]({chartFileName})");
        builder.AppendLine();
        AppendKeyMoments(builder, eventLine, samples, frames, zh);

        builder.AppendLine(zh ? "## 指标峰值" : "## Metric peaks");
        builder.AppendLine();
        builder.AppendLine(zh ? "| 指标 | 峰值 | 时间 |" : "| Metric | Peak | At |");
        builder.AppendLine("| --- | --- | --- |");
        AppendPeaks(builder, eventLine, samples, zh);

        builder.AppendLine(zh ? "## 帧统计" : "## Frame statistics");
        builder.AppendLine();
        AppendFrameStats(builder, report, frames, zh);

        builder.AppendLine(zh ? "## 可疑进程" : "## Suspects");
        builder.AppendLine();
        if (report is null || report.Suspects.Length == 0)
        {
            builder.AppendLine(zh ? "会话未生成可疑进程列表。" : "No suspects were recorded.");
        }
        else
        {
            builder.AppendLine(zh
                ? "| 进程 | PID | CPU 峰值 | GPU 峰值 | 工作集增量 |"
                : "| Process | PID | CPU peak | GPU peak | Working set delta |");
            builder.AppendLine("| --- | --- | --- | --- | --- |");
            foreach (SessionReportSuspect suspect in report.Suspects)
            {
                builder.AppendLine(
                    $"| {Cell(string.IsNullOrEmpty(suspect.Name) ? "?" : suspect.Name)} | {suspect.Pid} | " +
                    $"{Pct(suspect.CpuPeak)} | {Pct(suspect.GpuPeak)} | {MiB(suspect.WsDeltaMb)} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine(zh ? "## 结论与建议" : "## Findings and suggestions");
        builder.AppendLine();
        if (report is null)
        {
            builder.AppendLine(zh ? "会话未生成本地归因报告。" : "No local report was recorded.");
        }
        else
        {
            builder.AppendLine(zh
                ? $"**本地归因**：{CauseText(report.Cause, zh)}（置信度 {report.Confidence}）"
                : $"**Local attribution**: {CauseText(report.Cause, zh)} (confidence {report.Confidence})");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(report.Summary))
            {
                builder.AppendLine(report.Summary);
                builder.AppendLine();
            }

            AppendBullets(builder, zh ? "证据" : "Evidence", report.Evidence);
            AppendBullets(builder, zh ? "建议" : "Suggestions", report.Suggestions);
        }

        builder.AppendLine(zh ? "## 数据缺口" : "## Data gaps");
        builder.AppendLine();
        int dropped = document.Footer?.Dropped ?? 0;
        int frameDropped = document.Footer?.FrameDropped ?? 0;
        if (document.Footer is not { Complete: true })
        {
            builder.AppendLine(zh
                ? "- 会话仍在录制或写入未完成，尾部统计可能缺失。"
                : "- The session was still recording or the write did not finish; footer statistics may be missing.");
        }

        builder.AppendLine(dropped == 0 && frameDropped == 0
            ? zh ? "- 采样与帧数据没有记录到缺口。" : "- No sample or frame gaps were recorded."
            : zh
                ? $"- 采集缺口：采样 {dropped} 个，帧 {frameDropped} 个。缺口期间的读数不参与统计。"
                : $"- Collector gaps: {dropped} samples, {frameDropped} frames. Readings during gaps are excluded.");
        builder.AppendLine(zh
            ? $"- 帧统计排除 ≥{DiscontinuityFrameMs:0} ms 的单帧断档，避免停顿被当作一次渲染。"
            : $"- Frame statistics exclude single frames ≥{DiscontinuityFrameMs:0} ms so stalls are not counted as renders.");

        if (ai is not null)
        {
            builder.AppendLine();
            builder.AppendLine(zh ? "## AI 分析（可选）" : "## AI analysis (optional)");
            builder.AppendLine();
            builder.AppendLine(zh
                ? $"> {ai.Summary}\n>\n> 主因 {ai.PrimaryCause} · 置信度 {ai.Confidence}"
                : $"> {ai.Summary}\n>\n> Cause {ai.PrimaryCause} · confidence {ai.Confidence}");
            builder.AppendLine();
            AppendBullets(builder, zh ? "模型证据" : "Model evidence", ai.Evidence);
            AppendBullets(builder, zh ? "优化建议" : "Recommendations", ai.Recommendations);
            AppendBullets(builder, zh ? "分析局限" : "Limitations", ai.Limitations);
            if (!string.IsNullOrWhiteSpace(ai.Disclaimer))
            {
                builder.AppendLine();
                builder.AppendLine($"> {ai.Disclaimer}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(zh
            ? "> 本报告由蓝天游戏助手在本地生成，导出过程不联网。"
            : "> Generated locally by Azure Game Assistant; exporting makes no network requests.");

        return builder.ToString();
    }

    private static SampleRecord[] SelectSamples(SessionDocument document)
    {
        SessionEventLine eventLine = document.Event;
        if (eventLine.StartUtc <= 0 || eventLine.EndUtc <= eventLine.StartUtc)
        {
            return document.Samples;
        }

        var window = new List<SampleRecord>(document.Samples.Length);
        foreach (SampleRecord sample in document.Samples)
        {
            if (sample.UtcTicks >= eventLine.StartUtc && sample.UtcTicks <= eventLine.EndUtc)
            {
                window.Add(sample);
            }
        }

        return window.Count > 0 ? window.ToArray() : document.Samples;
    }

    private static void AppendKeyMoments(
        StringBuilder builder,
        SessionEventLine eventLine,
        SampleRecord[] samples,
        FrameTimePoint[] frames,
        bool zh)
    {
        builder.AppendLine(zh ? "| 时间 | 指标 | 数值 |" : "| Time | Metric | Value |");
        builder.AppendLine("| --- | --- | --- |");
        builder.AppendLine($"| {LocalTime(eventLine.StartUtc)} | { (zh ? "事件开始" : "event start") } | { (zh ? "评分" : "score") } {eventLine.Score} |");

        if (samples.Length > 0)
        {
            SampleRecord peak = samples.MaxBy(sample => sample.CpuUtilityPct);
            builder.AppendLine($"| {LocalTime(peak.UtcTicks)} | CPU | {Pct(peak.CpuUtilityPct)} |");
            SampleRecord gpu = samples.MaxBy(sample => sample.GpuUtilPct);
            builder.AppendLine($"| {LocalTime(gpu.UtcTicks)} | GPU | {Pct(gpu.GpuUtilPct)} |");
            SampleRecord disk = samples.MaxBy(sample => sample.DiskLatencyMs);
            builder.AppendLine($"| {LocalTime(disk.UtcTicks)} | { (zh ? "磁盘延迟" : "disk latency") } | {disk.DiskLatencyMs:0.0} ms |");
        }

        if (frames.Length > 0)
        {
            FrameTimePoint? worst = null;
            int gaps = 0;
            foreach (FrameTimePoint frame in frames)
            {
                if (!double.IsFinite(frame.FrameTimeMs) || frame.FrameTimeMs <= 0)
                {
                    continue;
                }

                if (frame.FrameTimeMs >= DiscontinuityFrameMs)
                {
                    gaps++;
                    continue;
                }

                if (worst is null || frame.FrameTimeMs > worst.Value.FrameTimeMs)
                {
                    worst = frame;
                }
            }

            if (worst is { } longest)
            {
                builder.AppendLine(
                    $"| {LocalTime(longest.UtcTicks)} | { (zh ? "最长有效帧" : "longest valid frame") } | " +
                    $"{longest.FrameTimeMs:0.0} ms ({(longest.FrameTimeMs > 0 ? (1000.0 / longest.FrameTimeMs).ToString("0.0", CultureInfo.InvariantCulture) : "—")} FPS) |");
            }

            if (gaps > 0)
            {
                string value = zh
                    ? $"{gaps} 次 ≥{DiscontinuityFrameMs:0} ms"
                    : $"{gaps} ≥{DiscontinuityFrameMs:0} ms";
                builder.AppendLine($"| — | { (zh ? "采集断帧" : "collector gaps") } | {value} |");
            }
        }

        builder.AppendLine($"| {LocalTime(eventLine.EndUtc)} | { (zh ? "事件结束" : "event end") } | {eventLine.DurationMs / 1000.0:0.0} s |");
    }

    private static void AppendPeaks(
        StringBuilder builder,
        SessionEventLine eventLine,
        SampleRecord[] samples,
        bool zh)
    {
        if (samples.Length == 0)
        {
            builder.AppendLine(zh ? "| — | 无采样数据 | — |" : "| — | no samples | — |");
            return;
        }

        SampleRecord cpu = samples.MaxBy(sample => sample.CpuUtilityPct);
        AppendRow(builder, "CPU", $"{Pct(cpu.CpuUtilityPct)} / {Pct(cpu.CpuFrequencyPct)}", LocalTime(cpu.UtcTicks));
        SampleRecord gpu = samples.MaxBy(sample => sample.GpuUtilPct);
        AppendRow(builder, "GPU", Pct(gpu.GpuUtilPct), LocalTime(gpu.UtcTicks));
        SampleRecord mem = samples.MinBy(sample => sample.MemAvailableMb);
        AppendRow(
            builder,
            zh ? "可用内存（最低）" : "Available memory (min)",
            MiB(mem.MemAvailableMb),
            LocalTime(mem.UtcTicks));
        SampleRecord commit = samples.MaxBy(sample => sample.MemCommitMb);
        AppendRow(
            builder,
            zh ? "提交内存（峰值）" : "Committed memory (peak)",
            commit.MemCommitLimitMb > 0
                ? $"{MiB(commit.MemCommitMb)} / {MiB(commit.MemCommitLimitMb)}"
                : MiB(commit.MemCommitMb),
            LocalTime(commit.UtcTicks));
        SampleRecord disk = samples.MaxBy(sample => sample.DiskLatencyMs);
        AppendRow(
            builder,
            zh ? "磁盘延迟（峰值）" : "Disk latency (peak)",
            $"{disk.DiskLatencyMs:0.0} ms · {Pct(disk.DiskBusyPct)}",
            LocalTime(disk.UtcTicks));
        SampleRecord jitter = samples.MaxBy(sample => sample.TimerJitterMs);
        AppendRow(builder, zh ? "计时抖动（峰值）" : "Timer jitter (peak)", $"{jitter.TimerJitterMs:0.0} ms", LocalTime(jitter.UtcTicks));

        SampleRecord vram = samples.MaxBy(sample => sample.V0.VramUsedMb);
        if (vram.V0.VramUsedMb > 0)
        {
            AppendRow(
                builder,
                zh ? "显存（峰值）" : "VRAM (peak)",
                vram.V0.VramTotalMb > 0
                    ? $"{MiB(vram.V0.VramUsedMb)} / {MiB(vram.V0.VramTotalMb)}"
                    : MiB(vram.V0.VramUsedMb),
                LocalTime(vram.UtcTicks));
        }

        SampleRecord gpuTemp = samples.MaxBy(sample => sample.V0.TemperatureC);
        if (gpuTemp.V0.TemperatureC > 0)
        {
            string value = $"{gpuTemp.V0.TemperatureC:0.#} °C";
            if (gpuTemp.V0.HotSpotC > 0)
            {
                value += $" ({ (zh ? "热点" : "hot spot") } {gpuTemp.V0.HotSpotC:0.#} °C)";
            }

            AppendRow(builder, zh ? "GPU 温度（峰值）" : "GPU temperature (peak)", value, LocalTime(gpuTemp.UtcTicks));
        }

        SampleRecord cpuTemp = samples.MaxBy(sample => sample.C0.TemperatureC);
        if (cpuTemp.C0.TemperatureC > 0)
        {
            AppendRow(builder, zh ? "CPU 温度（峰值）" : "CPU temperature (peak)", $"{cpuTemp.C0.TemperatureC:0.#} °C", LocalTime(cpuTemp.UtcTicks));
        }

        AppendRow(
            builder,
            zh ? "事件时长" : "Event duration",
            $"{eventLine.DurationMs / 1000.0:0.0} s",
            LocalTime(eventLine.EndUtc));
    }

    private static void AppendFrameStats(
        StringBuilder builder,
        SessionReportLine? report,
        FrameTimePoint[] frames,
        bool zh)
    {
        if (report?.FrameEvidence is { } evidence)
        {
            builder.AppendLine(zh
                ? $"- 有效帧 {evidence.FrameCount} · 最长帧 {evidence.MaxFrameTimeMs:0.0} ms · 尖峰 {evidence.SpikeCount} · 断帧 {evidence.GapCount}"
                : $"- Valid frames {evidence.FrameCount} · longest frame {evidence.MaxFrameTimeMs:0.0} ms · spikes {evidence.SpikeCount} · gaps {evidence.GapCount}");
            builder.AppendLine(zh
                ? $"- 平均 CPU 等待 {evidence.AvgCpuWaitMs:0.0} ms · GPU 等待 {evidence.AvgGpuWaitMs:0.0} ms · 显示延迟 {evidence.AvgDisplayLatencyMs:0.0} ms"
                : $"- Average CPU wait {evidence.AvgCpuWaitMs:0.0} ms · GPU wait {evidence.AvgGpuWaitMs:0.0} ms · display latency {evidence.AvgDisplayLatencyMs:0.0} ms");
            builder.AppendLine(zh
                ? $"- 主因与帧证据：{(evidence.Consistent ? "一致" : "不一致或证据不足")}"
                : $"- Cause vs. frame evidence: {(evidence.Consistent ? "consistent" : "inconsistent or insufficient")}");
            return;
        }

        (double totalMs, int valid, int gaps, int spikes, double worstMs) = SummarizeFrames(frames);
        if (valid == 0)
        {
            builder.AppendLine(zh
                ? "- 会话没有有效帧数据；如需帧率统计，请在游戏运行且帧采集为 capturing 时复现事件。"
                : "- The session has no valid frames. Reproduce with frame capture in state capturing to get FPS statistics.");
            return;
        }

        double averageFps = totalMs > 0 ? valid * 1000.0 / totalMs : 0;
        builder.AppendLine(zh
            ? $"- 有效帧 {valid} · 断帧 {gaps} · 尖峰 {spikes} · 整段平均 {averageFps:0.0} FPS"
            : $"- Valid frames {valid} · gaps {gaps} · spikes {spikes} · whole-session average {averageFps:0.0} FPS");
        builder.AppendLine(zh
            ? $"- 最长有效帧 {worstMs:0.0} ms · 1% 低帧 {LowFps(frames, 0.99):0.0} FPS · 0.1% 低帧 {LowFps(frames, 0.999):0.0} FPS"
            : $"- Longest valid frame {worstMs:0.0} ms · 1% low {LowFps(frames, 0.99):0.0} FPS · 0.1% low {LowFps(frames, 0.999):0.0} FPS");
    }

    private static (double TotalMs, int Valid, int Gaps, int Spikes, double WorstMs) SummarizeFrames(FrameTimePoint[] frames)
    {
        double total = 0;
        double worst = 0;
        int valid = 0;
        int gaps = 0;
        int spikes = 0;

        foreach (FrameTimePoint frame in frames)
        {
            if (!double.IsFinite(frame.FrameTimeMs) || frame.FrameTimeMs <= 0)
            {
                continue;
            }

            if (frame.FrameTimeMs >= DiscontinuityFrameMs)
            {
                gaps++;
                continue;
            }

            valid++;
            total += frame.FrameTimeMs;
            worst = Math.Max(worst, frame.FrameTimeMs);
            if (frame.Spike)
            {
                spikes++;
            }
        }

        return (total, valid, gaps, spikes, worst);
    }

    private static double LowFps(FrameTimePoint[] frames, double percentile)
    {
        var times = new List<double>(frames.Length);
        foreach (FrameTimePoint frame in frames)
        {
            if (double.IsFinite(frame.FrameTimeMs) && frame.FrameTimeMs > 0 && frame.FrameTimeMs < DiscontinuityFrameMs)
            {
                times.Add(frame.FrameTimeMs);
            }
        }

        if (times.Count == 0)
        {
            return 0;
        }

        times.Sort();
        int index = Math.Clamp((int)Math.Ceiling(times.Count * percentile) - 1, 0, times.Count - 1);
        return times[index] > 0 ? 1000.0 / times[index] : 0;
    }

    private static void AppendBullets(StringBuilder builder, string title, string[] items)
    {
        if (items.Length == 0)
        {
            return;
        }

        builder.AppendLine($"**{title}**");
        builder.AppendLine();
        foreach (string item in items)
        {
            if (!string.IsNullOrWhiteSpace(item))
            {
                builder.AppendLine("- " + item.Trim());
            }
        }

        builder.AppendLine();
    }

    private static void AppendRow(StringBuilder builder, string item, string value) =>
        builder.AppendLine($"| {Cell(item)} | {Cell(value)} |");

    private static void AppendRow(StringBuilder builder, string item, string value, string at) =>
        builder.AppendLine($"| {Cell(item)} | {Cell(value)} | {Cell(at)} |");

    private static string Cell(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Replace("|", "\\|", StringComparison.Ordinal).Trim();

    private static string Pct(double value) =>
        double.IsFinite(value) ? value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

    private static string MiB(double value) =>
        double.IsFinite(value) ? value.ToString("0", CultureInfo.InvariantCulture) + " MB" : "—";

    private static string LocalTime(long utcTicks)
    {
        if (utcTicks <= 0)
        {
            return "—";
        }

        try
        {
            return new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "—";
        }
    }

    private static string CauseText(string cause, bool zh) => cause switch
    {
        "Cpu" => zh ? "CPU 竞争" : "CPU contention",
        "Gpu" => zh ? "GPU 受限" : "GPU bound",
        "Memory" => zh ? "内存压力" : "Memory pressure",
        "Disk" => zh ? "磁盘瓶颈" : "Disk bound",
        "Vram" => zh ? "显存压力" : "VRAM pressure",
        "FramePacing" => zh ? "帧节奏" : "Frame pacing",
        "Thermal" => zh ? "热降频" : "Thermal throttling",
        "Manual" => zh ? "手动标记" : "Manual mark",
        _ => zh ? "未知" : "Unknown",
    };

    private static string SignalText(string[] signals, bool zh)
    {
        if (signals.Length == 0)
        {
            return zh ? "无" : "none";
        }

        var parts = new List<string>(signals.Length);
        foreach (string signal in signals)
        {
            parts.Add(signal switch
            {
                "FrameSpike" => zh ? "帧尖峰" : "frame spike",
                "DisplayGap" => zh ? "显示空洞" : "display gap",
                "Jitter" => zh ? "时序抖动" : "jitter",
                "CpuSpike" => zh ? "CPU 尖峰" : "CPU spike",
                "GpuSpike" => zh ? "GPU 尖峰" : "GPU spike",
                "MemoryPressure" => zh ? "内存压力" : "memory pressure",
                "DiskPressure" => zh ? "磁盘压力" : "disk pressure",
                "Manual" => zh ? "手动" : "manual",
                _ => signal,
            });
        }

        return string.Join(" · ", parts);
    }
}
