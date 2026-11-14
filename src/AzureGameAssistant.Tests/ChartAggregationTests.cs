using AzureGameAssistant.Core.Charts;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class ChartAggregationTests
{
    private const long Tick = TimeSpan.TicksPerSecond;

    [Fact]
    public void SpanSecondsNeedsAtLeastTwoSamples()
    {
        Assert.Equal(0, ChartAggregation.SpanSeconds(0, 0, 1));
        Assert.Equal(5, ChartAggregation.SpanSeconds(10 * Tick, 15 * Tick, 2));
    }

    [Fact]
    public void LatestEndTicksPrefersNewestSeries()
    {
        Assert.Equal(300, ChartAggregation.LatestEndTicks(300, 4, 200, 3, true));
        Assert.Equal(300, ChartAggregation.LatestEndTicks(300, 4, 900, 3, false));
        Assert.Equal(900, ChartAggregation.LatestEndTicks(300, 4, 900, 3, true));
    }

    [Fact]
    public void LatestEndTicksFallsBackToFrames()
    {
        Assert.Equal(800, ChartAggregation.LatestEndTicks(0, 0, 800, 2, true));
        Assert.Equal(800, ChartAggregation.LatestEndTicks(0, 0, 800, 2, false));
        Assert.Equal(0, ChartAggregation.LatestEndTicks(0, 0, 0, 0, true));
    }

    [Fact]
    public void AggregateSamplesKeepsColumnMinAndMax()
    {
        var samples = new[]
        {
            Sample(0, cpu: 10),
            Sample(1, cpu: 90),
            Sample(2, cpu: 40),
        };
        var min = new float[4];
        var max = new float[4];

        ChartAggregation.AggregateSamples(
            samples, samples.Length, s => s.CpuUtilityPct, 0, 4 * Tick, 4, min, max);

        Assert.Equal(10, min[0]);
        Assert.Equal(10, max[0]);
        Assert.Equal(90, min[1]);
        Assert.Equal(90, max[1]);
        Assert.Equal(40, min[2]);
        Assert.Equal(40, max[2]);
        Assert.True(float.IsNaN(min[3]));
        Assert.True(float.IsNaN(max[3]));
    }

    [Fact]
    public void AggregateSamplesSkipsOutOfWindowSamples()
    {
        var samples = new[] { Sample(-5, cpu: 99), Sample(1, cpu: 20), Sample(50, cpu: 99) };
        var min = new float[2];
        var max = new float[2];

        ChartAggregation.AggregateSamples(
            samples, samples.Length, s => s.CpuUtilityPct, 0, 4 * Tick, 2, min, max);

        for (int i = 0; i < 2; i++)
        {
            if (float.IsNaN(min[i]))
            {
                continue;
            }

            Assert.Equal(20, min[i]);
            Assert.Equal(20, max[i]);
        }

        Assert.Contains(min, v => !float.IsNaN(v));
    }

    [Fact]
    public void AggregateSamplesClampsNegativeValuesAndRightEdge()
    {
        var samples = new[] { Sample(8, cpu: -7), Sample(10, cpu: 55) };
        var min = new float[5];
        var max = new float[5];

        ChartAggregation.AggregateSamples(
            samples, samples.Length, s => s.CpuUtilityPct, 0, 10 * Tick, 5, min, max);

        Assert.Equal(0, min[4]);
        Assert.Equal(55, max[4]);
    }

    [Fact]
    public void AggregateSamplesRespectsCountArgument()
    {
        var samples = new[] { Sample(0, cpu: 10), Sample(1, cpu: 80) };
        var min = new float[2];
        var max = new float[2];

        ChartAggregation.AggregateSamples(
            samples, 1, s => s.CpuUtilityPct, 0, 2 * Tick, 2, min, max);

        Assert.Equal(10, min[0]);
        Assert.True(float.IsNaN(min[1]));
    }

    [Fact]
    public void AggregateFramesTracksSpikesAndWindowMax()
    {
        var frames = new[]
        {
            Frame(0, 8, false),
            Frame(1, 45, true),
            Frame(2, 60, false),
            Frame(3, 20, true),
        };
        var min = new float[4];
        var max = new float[4];
        var spikes = new float[4];

        double windowMax = ChartAggregation.AggregateFrames(
            frames, frames.Length, 0, 4 * Tick, 4, min, max, spikes);

        Assert.Equal(60, windowMax);
        Assert.Equal(8, min[0]);
        Assert.Equal(45, max[1]);
        Assert.Equal(45, spikes[1]);
        Assert.Equal(20, spikes[3]);
        Assert.True(float.IsNaN(spikes[0]));
        Assert.True(float.IsNaN(spikes[2]));
    }

    [Fact]
    public void AggregateFramesSkipsNonPositiveFrameTimes()
    {
        var frames = new[] { Frame(1, 0, false), Frame(2, -4, true) };
        var min = new float[4];
        var max = new float[4];
        var spikes = new float[4];

        double windowMax = ChartAggregation.AggregateFrames(
            frames, frames.Length, 0, 4 * Tick, 4, min, max, spikes);

        Assert.Equal(0, windowMax);
        Assert.All(min, v => Assert.True(float.IsNaN(v)));
        Assert.All(spikes, v => Assert.True(float.IsNaN(v)));
    }

    [Theory]
    [InlineData(0, 16.7)]
    [InlineData(16.7, 16.7)]
    [InlineData(20, 33.3)]
    [InlineData(50, 50)]
    [InlineData(80, 100)]
    [InlineData(120, 150)]
    [InlineData(101, 150)]
    public void FrameScaleMaxUsesFixedTiers(double max, double expected)
    {
        Assert.Equal(expected, ChartAggregation.FrameScaleMax(max), 3);
    }

    private static SampleRecord Sample(long seconds, float cpu) => new()
    {
        UtcTicks = seconds * Tick,
        CpuUtilityPct = cpu,
    };

    private static FrameTimePoint Frame(long seconds, float frameTimeMs, bool spike) =>
        new(seconds * Tick, frameTimeMs, spike);
}
