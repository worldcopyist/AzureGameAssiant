using AzureGameAssistant.Core.Frames;

namespace AzureGameAssistant.Tests;

public sealed class FrameTimelineTests
{
    [Fact]
    public void CopyTo_KeepsInsertionOrder()
    {
        var timeline = new FrameTimeline(capacity: 8);
        timeline.Add(1, 16.7f, false);
        timeline.Add(2, 33.3f, true);

        var destination = new FrameTimePoint[8];
        int copied = timeline.CopyTo(destination);

        Assert.Equal(2, copied);
        Assert.Equal(1, destination[0].UtcTicks);
        Assert.True(destination[1].Spike);
    }

    [Fact]
    public void CopyTo_WhenFull_ReturnsNewestChronologicalWindow()
    {
        var timeline = new FrameTimeline(capacity: 4);
        for (int i = 1; i <= 6; i++)
        {
            timeline.Add(i, i, false);
        }

        var destination = new FrameTimePoint[4];
        int copied = timeline.CopyTo(destination);

        Assert.Equal(4, copied);
        Assert.Equal(3, destination[0].UtcTicks);
        Assert.Equal(6, destination[3].UtcTicks);
    }

    [Fact]
    public void CopyTo_TruncatesToDestination()
    {
        var timeline = new FrameTimeline(capacity: 4);
        for (int i = 1; i <= 4; i++)
        {
            timeline.Add(i, i, false);
        }

        var destination = new FrameTimePoint[2];
        int copied = timeline.CopyTo(destination);

        Assert.Equal(2, copied);
        Assert.Equal(3, destination[0].UtcTicks);
        Assert.Equal(4, destination[1].UtcTicks);
    }

    [Fact]
    public void ToUtcTicks_UsesMeasuredFrequency()
    {
        const long anchorQpc = 1_000_000;
        const long anchorUtc = 638_000_000_000_000_000;
        const double frequency = 10_000_000;

        long utc = FrameTimeline.ToUtcTicks(anchorQpc + 5_000_000, anchorQpc, anchorUtc, frequency);

        Assert.Equal(anchorUtc + TimeSpan.TicksPerSecond / 2, utc);
    }

    [Fact]
    public void ToUtcTicks_WithoutFrequencyFallsBackToAnchor()
    {
        long utc = FrameTimeline.ToUtcTicks(123, 100, 42, 0);

        Assert.Equal(42, utc);
    }
}
