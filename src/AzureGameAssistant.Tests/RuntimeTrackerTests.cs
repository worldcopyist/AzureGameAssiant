using AzureGameAssistant.Core.Diagnostics;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class RuntimeTrackerTests
{
    [Fact]
    public void FrameDropTrackerGroupsSpikesIntoOneEpisode()
    {
        var tracker = new FrameDropTracker();
        long start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;
        SampleRecord sample = Sample(start, cpu: 50, gpu: 40);

        tracker.Observe(start, Frame(80, cpuWait: 30, gpuWait: 2), true, 60, sample);
        tracker.Observe(start + 50, Frame(120, cpuWait: 40, gpuWait: 3), true, 60, sample);
        tracker.Tick(start + TimeSpan.TicksPerSecond * 2);

        var buffer = new FrameDropMarker[4];
        int count = tracker.CopyTo(buffer);

        Assert.Equal(1, count);
        Assert.Equal(120, buffer[0].MaxFrameTimeMs, 1);
        Assert.Equal("cpu-wait", buffer[0].Kind);
        Assert.Equal(200u, buffer[0].SuspectPid);
    }

    [Fact]
    public void FrameDropTrackerIgnoresSpikesBelowFloor()
    {
        var tracker = new FrameDropTracker();
        long start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;

        tracker.Observe(start, Frame(20), true, 60, Sample(start, 20, 10));
        tracker.Tick(start + TimeSpan.TicksPerSecond * 2);

        Assert.Equal(0, tracker.CopyTo(new FrameDropMarker[1]));
    }

    [Fact]
    public void HighLoadTrackerRecordsSustainedEpisodeWithTopProcess()
    {
        var tracker = new HighLoadTracker();
        long start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;

        for (int i = 0; i < 3; i++)
        {
            tracker.Observe(Sample(start + (i * TimeSpan.TicksPerSecond), cpu: 92, gpu: 40), start + (i * TimeSpan.TicksPerSecond));
        }

        tracker.Observe(Sample(start + (3 * TimeSpan.TicksPerSecond), cpu: 40, gpu: 40), start + (3 * TimeSpan.TicksPerSecond));

        var buffer = new HighLoadMarker[4];
        int count = tracker.CopyTo(buffer);

        Assert.Equal(1, count);
        Assert.Equal("cpu", buffer[0].Resource);
        Assert.Equal(92, buffer[0].Peak, 1);
        Assert.Equal(2, buffer[0].Seconds, 1);
        Assert.Equal(200u, buffer[0].TopPid);
    }

    [Fact]
    public void HighLoadTrackerIgnoresShortSpikes()
    {
        var tracker = new HighLoadTracker();
        long start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;

        tracker.Observe(Sample(start, cpu: 92, gpu: 40), start);
        tracker.Observe(Sample(start + TimeSpan.TicksPerSecond, cpu: 40, gpu: 40), start + TimeSpan.TicksPerSecond);

        Assert.Equal(0, tracker.CopyTo(new HighLoadMarker[1]));
    }

    [Fact]
    public void DrainCompletedReturnsEachEpisodeOnce()
    {
        var tracker = new HighLoadTracker();
        long start = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc).Ticks;

        for (int i = 0; i < 3; i++)
        {
            tracker.Observe(Sample(start + (i * TimeSpan.TicksPerSecond), cpu: 95, gpu: 40), start + (i * TimeSpan.TicksPerSecond));
        }

        tracker.Observe(Sample(start + (3 * TimeSpan.TicksPerSecond), cpu: 40, gpu: 40), start + (3 * TimeSpan.TicksPerSecond));

        var buffer = new HighLoadMarker[4];
        Assert.Equal(1, tracker.DrainCompleted(buffer));
        Assert.Equal(0, tracker.DrainCompleted(buffer));
    }

    private static FrameRecord Frame(float frameTimeMs, float cpuWait = 0, float gpuWait = 0) => new()
    {
        FrameTimeMs = frameTimeMs,
        CpuWaitMs = cpuWait,
        GpuWaitMs = gpuWait,
    };

    private static SampleRecord Sample(long utcTicks, float cpu, float gpu) => new()
    {
        UtcTicks = utcTicks,
        CpuUtilityPct = cpu,
        GpuUtilPct = gpu,
        MemAvailableMb = 4096,
        ForegroundPid = 100,
        P0 = new ProcessSlot { Pid = 100, CpuPct = 15, GpuPct = 30 },
        P1 = new ProcessSlot { Pid = 200, CpuPct = 70, GpuPct = 5 },
    };
}
