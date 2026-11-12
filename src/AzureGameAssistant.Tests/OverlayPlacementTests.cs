using AzureGameAssistant.Core.Overlay;

namespace AzureGameAssistant.Tests;

public sealed class OverlayPlacementTests
{
    [Fact]
    public void TopLeftUsesWorkAreaOriginAndOffset()
    {
        (double left, double top) = OverlayPlacement.Place(
            "TopLeft", 0, 0, 1920, 1040, 300, 120, 12, 8);

        Assert.Equal(12, left);
        Assert.Equal(8, top);
    }

    [Fact]
    public void BottomRightAlignsAgainstWorkAreaEdges()
    {
        (double left, double top) = OverlayPlacement.Place(
            "BottomRight", 0, 0, 1920, 1040, 300, 120, 12, 8);

        Assert.Equal(1920 - 300 - 12, left);
        Assert.Equal(1040 - 120 - 8, top);
    }

    [Fact]
    public void SecondaryMonitorKeepsItsOwnWorkArea()
    {
        (double left, double top) = OverlayPlacement.Place(
            "TopRight", 1920, -200, 2560, 1400, 400, 100, 10, 10);

        Assert.Equal(1920 + 2560 - 400 - 10, left);
        Assert.Equal(-200 + 10, top);
    }

    [Fact]
    public void OversizedOverlayIsClampedToTheWorkArea()
    {
        (double left, double top) = OverlayPlacement.Place(
            "BottomRight", 100, 50, 320, 200, 500, 400, 16, 16);

        Assert.Equal(100, left);
        Assert.Equal(50, top);
    }
}
