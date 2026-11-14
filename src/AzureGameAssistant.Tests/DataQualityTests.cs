using AzureGameAssistant.Core.Quality;

namespace AzureGameAssistant.Tests;

public sealed class DataQualityTests
{
    [Fact]
    public void CompleteCaptureHasNoFlags()
    {
        DataQualityFlags flags = DataQuality.Evaluate(
            deepMode: true,
            framesFresh: true,
            frameAgeSeconds: 1,
            collectorConnected: true,
            sampleDropped: 0,
            frameDropped: 0);

        Assert.Equal(DataQualityFlags.None, flags);
        Assert.Equal("完整", DataQuality.Describe(flags, zh: true));
    }

    [Fact]
    public void ShallowModeIsMarked()
    {
        DataQualityFlags flags = DataQuality.Evaluate(
            deepMode: false,
            framesFresh: false,
            frameAgeSeconds: double.PositiveInfinity,
            collectorConnected: true,
            sampleDropped: 0,
            frameDropped: 0);

        Assert.True(flags.HasFlag(DataQualityFlags.ShallowMode));
        Assert.True(flags.HasFlag(DataQualityFlags.FrameInterrupted));
        Assert.Contains("浅模式", DataQuality.Describe(flags, zh: true));
    }

    [Theory]
    [InlineData(10.0, false)]
    [InlineData(10.1, true)]
    [InlineData(55.0, true)]
    public void FrameAgeAboveThresholdInterrupts(double ageSeconds, bool interrupted)
    {
        DataQualityFlags flags = DataQuality.Evaluate(
            deepMode: true,
            framesFresh: true,
            frameAgeSeconds: ageSeconds,
            collectorConnected: true,
            sampleDropped: 0,
            frameDropped: 0);

        Assert.Equal(interrupted, flags.HasFlag(DataQualityFlags.FrameInterrupted));
    }

    [Fact]
    public void CollectorGapIsMarked()
    {
        DataQualityFlags flags = DataQuality.Evaluate(
            deepMode: true,
            framesFresh: true,
            frameAgeSeconds: 0,
            collectorConnected: false,
            sampleDropped: 4,
            frameDropped: 0);

        Assert.True(flags.HasFlag(DataQualityFlags.CollectorInterrupted));
        Assert.Contains("采集数据中断", DataQuality.Describe(flags, zh: true));
        Assert.Contains("collector data interrupted", DataQuality.Describe(flags, zh: false));
    }
}
