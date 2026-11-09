using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sessions;

namespace AzureGameAssistant.Tests;

public sealed class LocalReportBuilderTests
{
    private static readonly long T0 = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void SevenCoreScenariosAreClassifiedAccurately()
    {
        var scenarios = new (StutterSignals Signals, Action<SampleRecord[]> Tweak, float CpuWait, float GpuWait, string Expected)[]
        {
            (StutterSignals.CpuSpike, samples => samples[4].CpuUtilityPct = 98, 40, 2, "Cpu"),
            (StutterSignals.GpuSpike, samples => samples[4].GpuUtilPct = 99, 1, 20, "Gpu"),
            (StutterSignals.FrameSpike, samples => samples[4].V0 = Vendor(65, 8000, 8192), 1, 8, "Vram"),
            (StutterSignals.MemoryPressure, samples => samples[4].MemAvailableMb = 300, 1, 8, "Memory"),
            (StutterSignals.DiskPressure, samples =>
            {
                samples[4].DiskBusyPct = 80;
                samples[4].DiskLatencyMs = 90;
            }, 1, 8, "Disk"),
            (StutterSignals.FrameSpike | StutterSignals.DisplayGap, _ => { }, 1, 8, "FramePacing"),
            (StutterSignals.GpuSpike, samples => samples[4].V0 = Vendor(89, 3000, 8192), 1, 20, "Thermal"),
        };

        int correct = 0;
        foreach (var scenario in scenarios)
        {
            SampleRecord[] samples = Samples();
            scenario.Tweak(samples);
            SessionReportLine report = Build(
                scenario.Signals,
                samples,
                Frames(cpuWait: scenario.CpuWait, gpuWait: scenario.GpuWait, spike: scenario.Signals != StutterSignals.None));

            if (report.Cause == scenario.Expected)
            {
                correct++;
            }
        }

        Assert.Equal(scenarios.Length, correct);
        Assert.True(correct / (double)scenarios.Length >= 0.9);
    }

    [Fact]
    public void GapsAreSeparatedFromSpikes()
    {
        var frames = new List<FrameTimePoint>();
        for (int i = 0; i < 7; i++)
        {
            bool spike = i is 2 or 5;
            frames.Add(new FrameTimePoint(
                T0 + (i * 50 * TimeSpan.TicksPerMillisecond),
                spike ? 120 : 16.7f,
                spike,
                spike ? 30 : 1.2f,
                spike ? 2 : 8,
                12));
        }

        for (int i = 0; i < 3; i++)
        {
            frames.Add(new FrameTimePoint(
                T0 + ((i + 7) * 50 * TimeSpan.TicksPerMillisecond),
                5000,
                true,
                0,
                0,
                0));
        }

        SessionReportLine report = Build(StutterSignals.FrameSpike, Samples(), frames.ToArray());
        SessionFrameEvidence evidence = Assert.IsType<SessionFrameEvidence>(report.FrameEvidence);

        Assert.Equal(10, evidence.FrameCount);
        Assert.Equal(3, evidence.GapCount);
        Assert.Equal(2, evidence.SpikeCount);
        Assert.Equal(120, evidence.MaxFrameTimeMs, 1);
        Assert.Contains(report.Evidence, line => line.Contains("断帧 3", StringComparison.Ordinal));
    }

    [Fact]
    public void CauseAndFrameWaitsContradictionIsReportedAsInconsistent()
    {
        SampleRecord[] samples = Samples();
        samples[4].CpuUtilityPct = 98;

        SessionReportLine report = Build(
            StutterSignals.CpuSpike,
            samples,
            Frames(cpuWait: 1, gpuWait: 20, spike: false));

        Assert.Equal("Cpu", report.Cause);
        Assert.False(Assert.IsType<SessionFrameEvidence>(report.FrameEvidence).Consistent);
    }

    [Fact]
    public void CauseAndFrameWaitsAgreementIsReportedAsConsistent()
    {
        SampleRecord[] samples = Samples();
        samples[4].CpuUtilityPct = 98;

        SessionReportLine report = Build(
            StutterSignals.CpuSpike,
            samples,
            Frames(cpuWait: 40, gpuWait: 2, spike: false));

        Assert.True(Assert.IsType<SessionFrameEvidence>(report.FrameEvidence).Consistent);
    }

    [Fact]
    public void UnknownReportWithoutSamplesStillHasNextSteps()
    {
        SessionReportLine report = Build(StutterSignals.None, [], []);

        Assert.Equal("Unknown", report.Cause);
        Assert.Null(report.FrameEvidence);
        Assert.NotEmpty(report.Summary);
        Assert.InRange(report.Suggestions.Length, 1, 3);
        Assert.Contains(report.Evidence, line => line.Contains("无帧级数据", StringComparison.Ordinal));
    }

    [Fact]
    public void ManualMarkKeepsLocalSuggestions()
    {
        SessionReportLine report = Build(StutterSignals.Manual, Samples(), Frames(spike: false));

        Assert.Equal("Manual", report.Cause);
        Assert.InRange(report.Suggestions.Length, 1, 3);
    }

    [Fact]
    public void ThermalReportExplainsHeatAndFrameEvidence()
    {
        SampleRecord[] samples = Samples();
        samples[4].V0 = Vendor(89, 3000, 8192);

        SessionReportLine report = Build(StutterSignals.GpuSpike, samples, Frames(spike: true));

        Assert.Equal("Thermal", report.Cause);
        Assert.Contains("温度", report.Summary, StringComparison.Ordinal);
        Assert.InRange(report.Suggestions.Length, 1, 3);
        Assert.Contains(report.Evidence, line => line.Contains("GPU 89°C", StringComparison.Ordinal));
    }

    [Fact]
    public void DeepModeReportCitesFrameEvidence()
    {
        SessionReportLine report = Build(StutterSignals.FrameSpike, Samples(), Frames(spike: true));

        SessionFrameEvidence evidence = Assert.IsType<SessionFrameEvidence>(report.FrameEvidence);
        Assert.True(evidence.FrameCount > 0);
        Assert.Contains(report.Evidence, line => line.Contains("帧证据", StringComparison.Ordinal));
        Assert.Contains(report.Evidence, line => line.Contains("尖峰", StringComparison.Ordinal));
    }

    [Fact]
    public void EnglishLocaleProducesEnglishReport()
    {
        SampleRecord[] samples = Samples();
        samples[4].CpuUtilityPct = 98;

        SessionReportLine report = Build(
            StutterSignals.CpuSpike,
            samples,
            Frames(cpuWait: 40, gpuWait: 2, spike: false),
            locale: "en-US");

        Assert.Contains("likely", report.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("疑似", report.Summary, StringComparison.Ordinal);
    }

    private static SessionReportLine Build(
        StutterSignals signals,
        SampleRecord[] samples,
        FrameTimePoint[] frames,
        string locale = "zh-CN")
    {
        var evt = new StutterEvent(
            1,
            new DateTime(T0, DateTimeKind.Utc),
            new DateTime(T0 + TimeSpan.TicksPerSecond, DateTimeKind.Utc),
            1000,
            70,
            signals,
            StutterCause.Unknown,
            120,
            42,
            signals.HasFlag(StutterSignals.Manual));

        return LocalReportBuilder.Build(evt, samples, frames, ResolveName, locale);
    }

    private static SampleRecord[] Samples()
    {
        var samples = new SampleRecord[10];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new SampleRecord
            {
                QpcTicks = (T0 + (i * TimeSpan.TicksPerSecond)) / 10,
                UtcTicks = T0 + (i * TimeSpan.TicksPerSecond),
                CpuUtilityPct = 30,
                CpuFrequencyPct = 90,
                MemAvailableMb = 8192,
                MemCommitMb = 8000,
                MemCommitLimitMb = 16000,
                DiskBusyPct = 5,
                DiskLatencyMs = 1,
                GpuUtilPct = 50,
                ForegroundPid = 42,
                Flags = SampleFlags.ProcessValid | SampleFlags.GpuValid | SampleFlags.VendorValid,
                P0 = new ProcessSlot
                {
                    Pid = 42,
                    CpuPct = 20,
                    GpuPct = 50,
                    WorkingSetMb = 4000,
                    PrivateMb = 3000,
                    Flags = ProcessSlotFlags.Foreground,
                },
                V0 = Vendor(65, 3000, 8192),
            };
        }

        return samples;
    }

    private static FrameTimePoint[] Frames(bool spike, float cpuWait = 1.2f, float gpuWait = 8)
    {
        var frames = new FrameTimePoint[10];
        for (int i = 0; i < frames.Length; i++)
        {
            bool isSpike = spike && i == 5;
            frames[i] = new FrameTimePoint(
                T0 + (i * 100 * TimeSpan.TicksPerMillisecond),
                isSpike ? 120 : 16.7f,
                isSpike,
                isSpike ? Math.Max(cpuWait, 40) : cpuWait,
                isSpike ? Math.Max(gpuWait, 2) : gpuWait,
                12);
        }

        return frames;
    }

    private static VendorSlot Vendor(float temperatureC, float vramUsedMb, float vramTotalMb) => new()
    {
        TemperatureC = temperatureC,
        VramUsedMb = vramUsedMb,
        VramTotalMb = vramTotalMb,
    };

    private static string ResolveName(uint pid) => pid == 42 ? "game.exe" : "other.exe";
}
