using AzureGameAssistant.Ai;
using AzureGameAssistant.Core.Hardware;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class AiFactsBuilderTests
{
    private static readonly SystemInventorySnapshot Inventory = new(
        "Windows 11 Pro 24H2",
        "Test CPU",
        12,
        32,
        ["Test GPU"],
        "1.2.3");

    [Fact]
    public void BuildsMetricsFramesSensorsAndRoles()
    {
        FactsDocument facts = FactsBuilder.Build(
            new FactsInput(Samples(), Frames(), Names(), "deep", "zh-CN"),
            hideProcessNames: false,
            Inventory);

        Assert.Equal("zh-CN", facts.Locale);
        Assert.Equal("deep", facts.Environment.Mode);
        Assert.Equal(1000, facts.Environment.SamplingMs);
        Assert.Equal("Test CPU", facts.Environment.Cpu);

        Assert.Equal(60d, facts.Metrics.CpuPeakPct);
        Assert.Equal(50d, facts.Metrics.CpuAvgPct);
        Assert.Equal(50d, facts.Metrics.GpuPeakPct);
        Assert.Equal(1800u, facts.Metrics.MemAvailableMb);
        Assert.Equal(0.5d, facts.Metrics.CommitRatio);
        Assert.Equal(4000d, facts.Metrics.VramUsedMb);
        Assert.Equal(8000d, facts.Metrics.VramTotalMb);

        Assert.NotNull(facts.Frames);
        Assert.Equal(4, facts.Frames!.Count);
        Assert.Equal(150d, facts.Frames.MaxFrameTimeMs);
        Assert.Equal(1, facts.Frames.DisplayGapCount);

        Assert.NotNull(facts.Sensors);
        Assert.Equal(70d, facts.Sensors!.GpuTemperatureC);
        Assert.Equal(55d, facts.Sensors.CpuTemperatureC);

        Assert.Collection(
            facts.Processes,
            first =>
            {
                Assert.Equal("Game", first.Name);
                Assert.Equal("foreground", first.Role);
            },
            second =>
            {
                Assert.Equal("Browser", second.Name);
                Assert.Equal("background", second.Role);
            });

        Assert.Empty(facts.Gaps);
    }

    [Fact]
    public void HidesProcessNamesAndReportsGaps()
    {
        var samples = new List<SampleRecord>
        {
            new()
            {
                UtcTicks = 1_000,
                CpuUtilityPct = 20,
                GpuUtilPct = 10,
                MemAvailableMb = 4096,
                ForegroundPid = 100,
                P0 = new ProcessSlot { Pid = 100, CpuPct = 12, PrivateMb = 400 },
                P1 = new ProcessSlot { Pid = 200, CpuPct = 4, PrivateMb = 200 },
            },
        };

        FactsDocument facts = FactsBuilder.Build(
            new FactsInput(samples, [], Names(), "shallow", "en-US"),
            hideProcessNames: true,
            Inventory);

        Assert.Equal(new[] { "process_1", "process_2" }, facts.Processes.Select(process => process.Name));
        Assert.Null(facts.Frames);
        Assert.Null(facts.Sensors);
        Assert.Contains(facts.Gaps, gap => gap.Contains("帧数据", StringComparison.Ordinal));
        Assert.Contains(facts.Gaps, gap => gap.Contains("CPU 温度", StringComparison.Ordinal));
        Assert.Contains(facts.Gaps, gap => gap.Contains("GPU 温度", StringComparison.Ordinal));
    }

    private static List<SampleRecord> Samples()
    {
        var samples = new List<SampleRecord>();
        for (int index = 0; index < 3; index++)
        {
            samples.Add(new SampleRecord
            {
                UtcTicks = 10_000_000 + (index * TimeSpan.TicksPerSecond),
                CpuUtilityPct = 40 + (index * 10),
                GpuUtilPct = 30 + (index * 10),
                MemAvailableMb = (uint)(2000 - (index * 100)),
                MemCommitMb = 8000,
                MemCommitLimitMb = 16000,
                DiskLatencyMs = 5 + index,
                TimerJitterMs = 1.5f,
                ForegroundPid = 100,
                P0 = new ProcessSlot { Pid = 100, CpuPct = 30, GpuPct = 20, PrivateMb = 500 },
                P1 = new ProcessSlot { Pid = 200, CpuPct = 10, GpuPct = 5, PrivateMb = 300 },
                V0 = new VendorSlot
                {
                    TemperatureC = 70,
                    HotSpotC = 85,
                    PowerW = 120,
                    VramUsedMb = 4000,
                    VramTotalMb = 8000,
                },
                C0 = new CpuSensorSlot { TemperatureC = 55, PackagePowerW = 80 },
                Flags = SampleFlags.GpuValid | SampleFlags.VendorValid | SampleFlags.CpuSensorValid,
            });
        }

        return samples;
    }

    private static List<FrameRecord> Frames() =>
    [
        new() { FrameTimeMs = 16.7f, CpuWaitMs = 2, GpuWaitMs = 1 },
        new() { FrameTimeMs = 33.3f, CpuWaitMs = 3, GpuWaitMs = 2 },
        new() { FrameTimeMs = 50f, CpuWaitMs = 4, GpuWaitMs = 3 },
        new() { FrameTimeMs = 150f, CpuWaitMs = 5, GpuWaitMs = 4 },
    ];

    private static Dictionary<uint, string> Names() => new()
    {
        [100] = "Game",
        [200] = "Browser",
    };
}
