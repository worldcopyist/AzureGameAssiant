using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sessions;

namespace AzureGameAssistant.Tests;

public sealed class SessionExportTests
{
    private static readonly long T0 = new DateTime(2026, 11, 10, 4, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void FileNameCarriesEventTimeAndCause()
    {
        SessionEventLine evt = Event(T0, T0 + TimeSpan.TicksPerSecond, "Cpu");

        string name = SessionMarkdownReport.BuildFileBaseName(evt);

        Assert.Equal(
            new DateTime(T0, DateTimeKind.Utc).ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-Cpu",
            name);
        Assert.Equal("unknown-Manual", SessionMarkdownReport.BuildFileBaseName(Event(0, 0, "Manual")));
    }

    [Fact]
    public void ChineseReportContainsEveryRequiredSection()
    {
        SessionDocument document = Document(BuildSamples(), BuildFrames(), Report());

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", null, "x-timeline.png");

        Assert.StartsWith("# 蓝天游戏助手 · 会话报告", markdown);
        Assert.Contains("## 环境摘要", markdown);
        Assert.Contains("## 事件时间轴", markdown);
        Assert.Contains("![会话时间轴](x-timeline.png)", markdown);
        Assert.Contains("## 指标峰值", markdown);
        Assert.Contains("## 帧统计", markdown);
        Assert.Contains("## 可疑进程", markdown);
        Assert.Contains("## 结论与建议", markdown);
        Assert.Contains("## 数据缺口", markdown);
        Assert.Contains("导出过程不联网", markdown);
        Assert.Contains("前台进程：game.exe · PID 42", markdown);
        Assert.Contains("数据窗口：", markdown);
        Assert.Contains("game.exe", markdown);
        Assert.Contains("background.exe", markdown);
        Assert.DoesNotContain("## AI 分析", markdown);
        Assert.DoesNotContain("NaN", markdown);
    }

    [Fact]
    public void EnglishReportUsesEnglishHeadings()
    {
        SessionDocument document = Document(BuildSamples(), BuildFrames(), Report());

        string markdown = SessionMarkdownReport.Build(document, "en-US", null, "x-timeline.png");

        Assert.StartsWith("# Azure Game Assistant · Session report", markdown);
        Assert.Contains("## Environment", markdown);
        Assert.Contains("## Timeline", markdown);
        Assert.Contains("## Metric peaks", markdown);
        Assert.Contains("## Frame statistics", markdown);
        Assert.Contains("## Suspects", markdown);
        Assert.Contains("## Findings and suggestions", markdown);
        Assert.Contains("## Data gaps", markdown);
        Assert.Contains("makes no network requests", markdown);
    }

    [Fact]
    public void AiSummaryIsAppendedWhenAvailable()
    {
        var ai = new SessionAiSummary(
            "后台进程抢占了 CPU。",
            "CpuContention",
            "medium",
            ["cpuPeakPct: 96% — 事件窗口内 CPU 饱和"],
            ["限制后台进程的 CPU 亲和性"],
            ["仅依据本地采样"],
            "仅供参考。");
        SessionDocument document = Document(BuildSamples(), BuildFrames(), Report());

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", ai, "x.png");

        Assert.Contains("## AI 分析（可选）", markdown);
        Assert.Contains("后台进程抢占了 CPU。", markdown);
        Assert.Contains("cpuPeakPct: 96% — 事件窗口内 CPU 饱和", markdown);
        Assert.Contains("限制后台进程的 CPU 亲和性", markdown);
        Assert.Contains("仅依据本地采样", markdown);
        Assert.Contains("仅供参考。", markdown);
    }

    [Fact]
    public void FrameStatsExcludeCollectorGaps()
    {
        FrameTimePoint[] frames = BuildFrames();
        var withGap = new List<FrameTimePoint>(frames);
        withGap.Insert(0, new FrameTimePoint(T0 + TimeSpan.TicksPerSecond, 5000f, false, 0, 0, 0));
        SessionDocument document = Document(BuildSamples(), withGap.ToArray(), report: null);

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", null, "x.png");

        Assert.Contains("有效帧 300 · 断帧 1 · 尖峰 3", markdown);
        Assert.Contains("最长有效帧 120.0 ms", markdown);
        Assert.Contains("采集断帧 | 1 次 ≥1000 ms", markdown);
        Assert.Contains("整段平均 56.4 FPS", markdown);
        Assert.DoesNotContain("5000.0 ms", markdown);
    }

    [Fact]
    public void ReportFrameEvidenceIsPreferred()
    {
        SessionDocument document = Document(BuildSamples(), BuildFrames(), Report());

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", null, "x.png");

        Assert.Contains("有效帧 300 · 最长帧 120.0 ms · 尖峰 4 · 断帧 8", markdown);
        Assert.Contains("平均 CPU 等待 40.0 ms", markdown);
        Assert.Contains("一致", markdown);
    }

    [Fact]
    public void DroppedCountsAndIncompleteFooterAreReported()
    {
        SessionDocument document = Document(
            BuildSamples(),
            BuildFrames(),
            Report(),
            footer: new SessionFooterLine { Complete = false, Dropped = 3, FrameDropped = 2 });

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", null, "x.png");

        Assert.Contains("会话仍在录制或写入未完成", markdown);
        Assert.Contains("采样 3 个，帧 2 个", markdown);
    }

    [Fact]
    public void PeaksUseOnlyTheEventWindow()
    {
        SampleRecord[] samples = BuildSamples();
        samples[0].CpuUtilityPct = 99;
        SessionDocument document = Document(samples, BuildFrames(), Report());

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", null, "x.png");

        Assert.Contains("96.0% / 90.0%", markdown);
        Assert.DoesNotContain("99.0%", markdown);
    }

    [Fact]
    public void TableCellsEscapePipes()
    {
        SessionReportLine report = Report();
        report.Suspects[0].Name = "bad|name";
        SessionDocument document = Document(BuildSamples(), BuildFrames(), report);

        string markdown = SessionMarkdownReport.Build(document, "zh-CN", null, "x.png");

        Assert.Contains("bad\\|name", markdown);
    }

    private static SessionDocument Document(
        SampleRecord[] samples,
        FrameTimePoint[] frames,
        SessionReportLine? report,
        SessionFooterLine? footer = null) => new()
    {
        Header = new SessionHeaderLine
        {
            AppVersion = "0.5.0",
            Os = "Windows 11",
            Cpu = "Test CPU",
            Gpu = "Test GPU",
            Mode = "deep",
            SamplingMs = 200,
            Elevated = true,
            CpuTempSource = "driver",
            Sensors = "on",
            PresentMon = "2.6.0",
            WindowStartUtc = T0 - 30 * TimeSpan.TicksPerSecond,
            WindowEndUtc = T0 + 30 * TimeSpan.TicksPerSecond,
        },
        Event = Event(T0 - 5 * TimeSpan.TicksPerSecond, T0 + 5 * TimeSpan.TicksPerSecond, "Cpu"),
        Samples = samples,
        Frames = frames,
        Report = report,
        Footer = footer ?? new SessionFooterLine { Complete = true, Dropped = 0, FrameDropped = 0 },
        Names = new Dictionary<uint, string> { [42] = "game.exe", [99] = "background.exe" },
    };

    private static SessionEventLine Event(long start, long end, string cause) => new()
    {
        Id = 7,
        StartUtc = start,
        EndUtc = end,
        DurationMs = (end - start) / (double)TimeSpan.TicksPerMillisecond,
        Score = 82,
        Signals = ["FrameSpike", "CpuSpike"],
        Cause = cause,
        PeakFrameTimeMs = 120,
        ForegroundPid = 42,
        ProcessName = "game.exe",
    };

    private static SessionReportLine Report() => new()
    {
        Cause = "Cpu",
        Confidence = "Medium",
        Summary = "疑似后台进程抢占 CPU 导致掉帧。",
        Suspects =
        [
            new SessionReportSuspect
            {
                Pid = 99,
                Name = "background.exe",
                CpuPeak = 95,
                GpuPeak = 1,
                WsDeltaMb = 120,
                Weight = 3.2,
            },
        ],
        FrameEvidence = new SessionFrameEvidence
        {
            FrameCount = 300,
            GapCount = 8,
            MaxFrameTimeMs = 120,
            SpikeCount = 4,
            AvgCpuWaitMs = 40,
            AvgGpuWaitMs = 8,
            AvgDisplayLatencyMs = 12,
            Consistent = true,
        },
        Evidence = ["CPU 峰值 96%", "后台进程 CPU 95%"],
        Suggestions = ["限制后台进程的 CPU 占用"],
    };

    private static SampleRecord[] BuildSamples()
    {
        var samples = new SampleRecord[65];
        for (int i = 0; i < samples.Length; i++)
        {
            long utc = T0 + (i - 32) * TimeSpan.TicksPerSecond;
            samples[i] = new SampleRecord
            {
                QpcTicks = utc / 10,
                UtcTicks = utc,
                CpuUtilityPct = i == 32 ? 96 : 40,
                CpuFrequencyPct = 90,
                MemAvailableMb = 4096,
                MemCommitMb = 8000,
                MemCommitLimitMb = 16000,
                DiskBusyPct = 10,
                DiskLatencyMs = 2,
                GpuUtilPct = 60,
                TimerJitterMs = 1,
                ForegroundPid = 42,
                Flags = SampleFlags.ProcessValid | SampleFlags.GpuValid | SampleFlags.VendorValid,
                V0 = new VendorSlot
                {
                    TemperatureC = 65,
                    VramUsedMb = 3000,
                    VramTotalMb = 8192,
                },
            };
        }

        return samples;
    }

    private static FrameTimePoint[] BuildFrames()
    {
        var frames = new FrameTimePoint[300];
        for (int i = 0; i < frames.Length; i++)
        {
            bool spike = i is > 120 and < 124;
            frames[i] = new FrameTimePoint(
                T0 - 30 * TimeSpan.TicksPerSecond + i * 200 * TimeSpan.TicksPerMillisecond,
                spike ? 120 : 16.7f,
                spike,
                spike ? 40 : 1.2f,
                spike ? 2 : 8,
                12);
        }

        return frames;
    }
}
