using AzureGameAssistant.Core.Frames;

namespace AzureGameAssistant.Tests;

public sealed class FrameStatsTests
{
    [Fact]
    public void Snapshot_ConstantFrameTime_ReportsStableFps()
    {
        var stats = new FrameStats(capacity: 256);
        for (int i = 0; i < 100; i++)
        {
            stats.Add(10.0);
        }

        FrameStatsSnapshot snapshot = stats.Snapshot();

        Assert.Equal(100.0, snapshot.InstantFps, 3);
        Assert.Equal(100.0, snapshot.AverageFps, 3);
        Assert.Equal(100.0, snapshot.OnePercentLowFps, 3);
        Assert.Equal(100.0, snapshot.PointOnePercentLowFps, 3);
        Assert.Equal(100, snapshot.FrameCount);
    }

    [Fact]
    public void InstantFps_SurvivesSingleMissedVsync()
    {
        // 60 fps with one 33.3 ms frame used to read exactly 30 FPS because the
        // value was 1000 / last frame time.
        var stats = new FrameStats(capacity: 1024);
        for (int i = 0; i < 120; i++)
        {
            stats.Add(16.7);
        }

        stats.Add(33.3);
        FrameStatsSnapshot snapshot = stats.Snapshot();

        Assert.InRange(snapshot.InstantFps, 57.0, 59.0);
        Assert.InRange(snapshot.AverageFps, 58.0, 59.6);
    }

    [Fact]
    public void AverageFps_IgnoresOlderSlowCapture()
    {
        // A slow phase older than the average window must not pull down the
        // reading of the current, fast gameplay.
        var stats = new FrameStats(capacity: 2048);
        for (int i = 0; i < 400; i++)
        {
            stats.Add(100.0);
        }

        for (int i = 0; i < 600; i++)
        {
            stats.Add(16.7);
        }

        FrameStatsSnapshot snapshot = stats.Snapshot();

        Assert.InRange(snapshot.AverageFps, 59.0, 60.0);
        Assert.InRange(snapshot.OnePercentLowFps, 55.0, 60.0);
        Assert.InRange(snapshot.InstantFps, 59.0, 60.0);
    }

    [Fact]
    public void Snapshot_SlowestFramesDriveLowFps()
    {
        var stats = new FrameStats(capacity: 256);
        for (int i = 0; i < 99; i++)
        {
            stats.Add(10.0);
        }

        stats.Add(100.0);
        FrameStatsSnapshot snapshot = stats.Snapshot();

        Assert.Equal(10.0, snapshot.OnePercentLowFps, 3);
        Assert.Equal(10.0, snapshot.PointOnePercentLowFps, 3);
        Assert.Equal(1000.0 * 100 / 1090.0, snapshot.AverageFps, 3);
        Assert.InRange(snapshot.InstantFps, 90.0, 100.0);
    }

    [Fact]
    public void Snapshot_RollsOverOldFrames()
    {
        var stats = new FrameStats(capacity: 3);
        stats.Add(10);
        stats.Add(20);
        stats.Add(30);

        FrameStatsSnapshot first = stats.Snapshot();
        Assert.Equal(3, first.FrameCount);
        Assert.Equal(1000.0 * 3 / 60.0, first.AverageFps, 3);

        stats.Add(40);
        FrameStatsSnapshot second = stats.Snapshot();
        Assert.Equal(3, second.FrameCount);
        Assert.Equal(1000.0 * 3 / 90.0, second.AverageFps, 3);
        Assert.Equal(25.0, second.OnePercentLowFps, 3);
    }

    [Fact]
    public void Add_DetectsSpikeAgainstRollingMedian()
    {
        var stats = new FrameStats(capacity: 256);
        for (int i = 0; i < 120; i++)
        {
            Assert.False(stats.Add(16.7));
        }

        Assert.True(stats.Add(100.0));
        Assert.False(stats.Add(30.0));
        Assert.False(stats.Add(40.0));
    }

    [Fact]
    public void Add_IgnoresInvalidFrameTimes()
    {
        var stats = new FrameStats(capacity: 8);

        Assert.False(stats.Add(0));
        Assert.False(stats.Add(-1));
        Assert.False(stats.Add(double.NaN));
        Assert.False(stats.Add(double.PositiveInfinity));
        Assert.Equal(0, stats.Count);
    }

    [Fact]
    public void Clear_ResetsWindowAndSpikeBaseline()
    {
        var stats = new FrameStats(capacity: 256);
        for (int i = 0; i < 120; i++)
        {
            stats.Add(16.7);
        }

        stats.Clear();

        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.Snapshot().FrameCount);
        Assert.False(stats.Add(100.0));
    }
}
