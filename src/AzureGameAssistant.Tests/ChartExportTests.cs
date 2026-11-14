using AzureGameAssistant.Core.Charts;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class ChartExportTests
{
    private static readonly long BaseTicks =
        new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void OnlySamplesInsideTheVisibleRangeAreExported()
    {
        SampleRecord[] samples =
        [
            Sample(0, 10),
            Sample(1, 20),
            Sample(2, 30),
            Sample(3, 40),
        ];

        string csv = ChartExport.BuildVisibleCsv(
            samples, samples.Length, Array.Empty<FrameTimePoint>(), 0,
            BaseTicks + TimeSpan.TicksPerSecond,
            BaseTicks + (2 * TimeSpan.TicksPerSecond));

        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(ChartExport.CsvHeader, lines[0]);
        Assert.Contains(",20.0,", lines[1]);
        Assert.Contains(",30.0,", lines[2]);
        Assert.DoesNotContain(",10.0,", csv);
        Assert.DoesNotContain(",40.0,", csv);
    }

    [Fact]
    public void RowsUseIsoUtcAndInvariantNumbers()
    {
        SampleRecord[] samples = [Sample(0, 12.5f)];

        string csv = ChartExport.BuildVisibleCsv(
            samples, 1, Array.Empty<FrameTimePoint>(), 0,
            BaseTicks, BaseTicks + TimeSpan.TicksPerSecond);

        Assert.Contains("2026-11-14T12:00:00.000Z,12.5", csv);
        Assert.DoesNotContain(";", csv);
    }

    [Fact]
    public void NearestFrameAtOrBeforeTheSampleIsJoined()
    {
        SampleRecord[] samples = [Sample(0, 20), Sample(1, 20)];
        FrameTimePoint[] frames =
        [
            new(BaseTicks, 16.7f, false),
            new(BaseTicks + (950 * TimeSpan.TicksPerMillisecond), 40.0f, true),
        ];

        string csv = ChartExport.BuildVisibleCsv(
            samples, samples.Length, frames, frames.Length,
            BaseTicks, BaseTicks + (3 * TimeSpan.TicksPerSecond));

        string[] lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.EndsWith(",16.70,0", lines[1]);
        Assert.EndsWith(",40.00,1", lines[2]);
    }

    [Fact]
    public void FramesOlderThanOneSecondAreNotAttached()
    {
        SampleRecord[] samples = [Sample(5, 20)];
        FrameTimePoint[] frames = [new(BaseTicks, 16.7f, false)];

        string csv = ChartExport.BuildVisibleCsv(
            samples, 1, frames, 1,
            BaseTicks, BaseTicks + (10 * TimeSpan.TicksPerSecond));

        Assert.EndsWith(",0", csv.TrimEnd());
        Assert.DoesNotContain("16.70", csv);
    }

    [Fact]
    public void EmptyRangeReturnsHeaderOnly()
    {
        SampleRecord[] samples = [Sample(0, 10)];

        string csv = ChartExport.BuildVisibleCsv(
            samples, 1, Array.Empty<FrameTimePoint>(), 0, BaseTicks, BaseTicks);

        Assert.Equal(ChartExport.CsvHeader + "\n", csv);
    }

    private static SampleRecord Sample(int seconds, float cpu)
    {
        long ticks = BaseTicks + (seconds * TimeSpan.TicksPerSecond);
        return new SampleRecord
        {
            UtcTicks = ticks,
            QpcTicks = ticks,
            CpuUtilityPct = cpu,
            GpuUtilPct = cpu,
            MemCommitMb = 1024,
            MemCommitLimitMb = 8192,
            MemAvailableMb = 4096,
            DiskBusyPct = 5,
            DiskLatencyMs = 1.25f,
            GpuDedicatedMb = 512,
        };
    }
}
