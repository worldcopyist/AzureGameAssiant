using AzureGameAssistant.Core.Overlay;

namespace AzureGameAssistant.Tests;

public sealed class OverlayColorTests
{
    [Fact]
    public void SixDigitColorIsOpaque()
    {
        Assert.True(OverlayColor.TryParse(" #00ff7f ", out (byte A, byte R, byte G, byte B) color));
        Assert.Equal(255, color.A);
        Assert.Equal(0x00, color.R);
        Assert.Equal(0xFF, color.G);
        Assert.Equal(0x7F, color.B);
    }

    [Fact]
    public void EightDigitColorKeepsAlpha()
    {
        Assert.True(OverlayColor.TryParse("#8000FF7F", out (byte A, byte R, byte G, byte B) color));
        Assert.Equal(0x80, color.A);
        Assert.Equal(0x00, color.R);
        Assert.Equal(0xFF, color.G);
        Assert.Equal(0x7F, color.B);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData("white")]
    public void InvalidColorFallsBackToWhite(string text)
    {
        Assert.False(OverlayColor.TryParse(text, out _));
        Assert.Equal("#FFFFFF", OverlayColor.Normalize(text));
    }

    [Fact]
    public void NormalizeUppercasesAndKeepsAlphaOnlyWhenNeeded()
    {
        Assert.Equal("#00FF7F", OverlayColor.Normalize("00ff7f"));
        Assert.Equal("#00FF7F", OverlayColor.Normalize("#ff00ff7f"));
        Assert.Equal("#8000FF7F", OverlayColor.Normalize("#8000ff7f"));
    }
}
