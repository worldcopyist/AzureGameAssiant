using AzureGameAssistant.Core.Charts;

namespace AzureGameAssistant.Tests;

public sealed class ChartViewportTests
{
    [Fact]
    public void DefaultsToSixtySecondWindowNotPaused()
    {
        var viewport = new ChartViewport();

        Assert.Equal(60, viewport.WindowSeconds);
        Assert.Equal(0, viewport.PanSeconds);
        Assert.False(viewport.IsPaused);
        Assert.False(viewport.IsPanned);
    }

    [Fact]
    public void PauseFreezesAtLatestTicksUntilResumed()
    {
        var viewport = new ChartViewport();

        Assert.True(viewport.Pause(1000));
        Assert.True(viewport.IsPaused);
        Assert.Equal(1000, viewport.ResolveEndTicks(1400));

        Assert.True(viewport.Resume());
        Assert.False(viewport.IsPaused);
        Assert.Equal(1400, viewport.ResolveEndTicks(1400));
    }

    [Fact]
    public void PauseWithoutDataIsIgnored()
    {
        var viewport = new ChartViewport();

        Assert.False(viewport.Pause(0));
        Assert.False(viewport.IsPaused);
        Assert.Equal(500, viewport.ResolveEndTicks(500));
    }

    [Fact]
    public void PauseTwiceKeepsFirstFrozenTick()
    {
        var viewport = new ChartViewport();

        Assert.True(viewport.Pause(1000));
        Assert.False(viewport.Pause(2000));
        Assert.Equal(1000, viewport.ResolveEndTicks(2000));
    }

    [Fact]
    public void ResumeWithoutPauseIsNoOp()
    {
        var viewport = new ChartViewport();

        Assert.False(viewport.Resume());
        Assert.False(viewport.IsPaused);
    }

    [Fact]
    public void PausedViewportFallsBackWhenDataRangeReset()
    {
        var viewport = new ChartViewport();
        viewport.Pause(5000);

        Assert.Equal(3000, viewport.ResolveEndTicks(3000));
    }

    [Fact]
    public void PanClampsToAvailableSpan()
    {
        var viewport = new ChartViewport();

        Assert.True(viewport.SetPan(500, 120));
        Assert.Equal(60, viewport.PanSeconds);
        Assert.True(viewport.IsPanned);
    }

    [Fact]
    public void NegativePanClampsToZeroAndResets()
    {
        var viewport = new ChartViewport();
        viewport.SetPan(30, 120);

        Assert.True(viewport.SetPan(-10, 120));
        Assert.Equal(0, viewport.PanSeconds);
        Assert.False(viewport.IsPanned);

        viewport.SetPan(30, 120);
        Assert.True(viewport.ResetPan());
        Assert.Equal(0, viewport.PanSeconds);
        Assert.False(viewport.ResetPan());
    }

    [Fact]
    public void TinyPanDoesNotCountAsPanned()
    {
        var viewport = new ChartViewport();

        Assert.True(viewport.SetPan(0.4, 120));
        Assert.False(viewport.IsPanned);
    }

    [Fact]
    public void ZoomHonoursWindowBounds()
    {
        var viewport = new ChartViewport();

        Assert.True(viewport.SetWindowSeconds(5000, 300));
        Assert.Equal(300, viewport.WindowSeconds);

        Assert.True(viewport.SetWindowSeconds(1, 300));
        Assert.Equal(ChartViewport.MinWindowSeconds, viewport.WindowSeconds);

        Assert.False(viewport.SetWindowSeconds(1, 300));
    }

    [Fact]
    public void ZoomOutWithoutDataUsesMaxWindow()
    {
        Assert.Equal(ChartViewport.MaxWindowSeconds, ChartViewport.MaxWindowFor(0));
        Assert.Equal(120, ChartViewport.MaxWindowFor(120));
        Assert.Equal(ChartViewport.MinWindowSeconds, ChartViewport.MaxWindowFor(4));
    }

    [Fact]
    public void GrowingWindowPullsPanBack()
    {
        var viewport = new ChartViewport();
        viewport.SetPan(240, 300);

        Assert.True(viewport.SetWindowSeconds(250, 300));
        Assert.Equal(50, viewport.PanSeconds);
    }
}
