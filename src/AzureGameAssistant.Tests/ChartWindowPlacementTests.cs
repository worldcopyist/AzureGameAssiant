using AzureGameAssistant.Core.Overlay;

namespace AzureGameAssistant.Tests;

public sealed class ChartWindowPlacementTests
{
    [Fact]
    public void SavedBoundsRestoreOnPrimaryScreen()
    {
        bool ok = ChartWindowPlacement.TryRestore(
            120, 140, 720, 420, 0, 0, 1920, 1040, out var bounds);

        Assert.True(ok);
        Assert.Equal(120, bounds.Left);
        Assert.Equal(140, bounds.Top);
        Assert.Equal(720, bounds.Width);
        Assert.Equal(420, bounds.Height);
    }

    [Fact]
    public void SecondaryScreenWithNegativeOriginRestores()
    {
        bool ok = ChartWindowPlacement.TryRestore(
            -2400, 100, 800, 400, -2560, 0, 2560, 1400, out var bounds);

        Assert.True(ok);
        Assert.Equal(-2400, bounds.Left);
        Assert.Equal(100, bounds.Top);
        Assert.Equal(800, bounds.Width);
        Assert.Equal(400, bounds.Height);
    }

    [Fact]
    public void MissingBoundsFallBackToDefaultPlacement()
    {
        Assert.False(ChartWindowPlacement.TryRestore(
            null, null, null, null, 0, 0, 1920, 1040, out _));
        Assert.False(ChartWindowPlacement.TryRestore(
            double.NaN, 10, 600, 300, 0, 0, 1920, 1040, out _));
    }

    [Fact]
    public void FullyOffScreenWindowIsRejected()
    {
        Assert.False(ChartWindowPlacement.TryRestore(
            3000, 1800, 700, 400, 0, 0, 1920, 1040, out _));
    }

    [Fact]
    public void PartiallyVisibleWindowSnapsInsideScreen()
    {
        bool ok = ChartWindowPlacement.TryRestore(
            -60, 20, 560, 300, 0, 0, 1920, 1040, out var bounds);

        Assert.True(ok);
        Assert.Equal(0, bounds.Left);
        Assert.Equal(20, bounds.Top);
    }

    [Fact]
    public void OversizedWindowIsClampedToScreenAndMinimumSize()
    {
        bool ok = ChartWindowPlacement.TryRestore(
            10, 10, 5000, 3000, 0, 0, 1920, 1040, out var bounds);

        Assert.True(ok);
        Assert.Equal(1920, bounds.Width);
        Assert.Equal(1040, bounds.Height);

        Assert.True(ChartWindowPlacement.TryRestore(
            10, 10, 10, 10, 0, 0, 1920, 1040, out var minimum));
        Assert.Equal(ChartWindowPlacement.MinWidth, minimum.Width);
        Assert.Equal(ChartWindowPlacement.MinHeight, minimum.Height);
    }
}
