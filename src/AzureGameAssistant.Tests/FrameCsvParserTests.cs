using AzureGameAssistant.Core.Frames;

namespace AzureGameAssistant.Tests;

public sealed class FrameCsvParserTests
{
    private const string V2Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags," +
        "AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency," +
        "GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,AnimationTime," +
        "MsFlipDelay,AllInputToPhotonLatency,ClickToPhotonLatency";

    [Fact]
    public void TryParse_MapsV2Columns()
    {
        var parser = new FrameCsvParser(V2Header);

        bool parsed = parser.TryParse(
            "Arknights.exe,18096,0x7BC9DA90,DXGI,1,0,0,Hardware Composed: Independent Flip," +
            "12.3995,7.0337,6.6439,0.3898,5.6568,6.9749,1.9972,4.9777,18.7321,6.9149," +
            "-1.1772,12.3995,NA,NA,NA",
            out PresentMonFrame frame);

        Assert.True(parsed);
        Assert.Equal(18096, frame.Pid);
        Assert.Equal(12.3995, frame.TimeMs, 3);
        Assert.Equal(7.0337, frame.FrameTimeMs, 3);
        Assert.Equal(6.6439, frame.CpuBusyMs, 3);
        Assert.Equal(0.3898, frame.CpuWaitMs, 3);
        Assert.Equal(5.6568, frame.GpuLatencyMs, 3);
        Assert.Equal(6.9749, frame.GpuTimeMs, 3);
        Assert.Equal(1.9972, frame.GpuBusyMs, 3);
        Assert.Equal(4.9777, frame.GpuWaitMs, 3);
        Assert.Equal(18.7321, frame.DisplayLatencyMs, 3);
        Assert.Equal(-1.1772, frame.AnimationErrorMs, 3);
        Assert.Equal(PresentMode.HardwareComposedIndependentFlip, frame.PresentMode);
    }

    [Fact]
    public void TryParse_AcceptsLegacyMsColumnNames()
    {
        var parser = new FrameCsvParser(
            "ProcessID,MsBetweenPresents,MsCPUBusy,MsGPUWait,MsAnimationError,CPUStartTime");

        Assert.True(parser.TryParse("7,16.7,1.2,0.4,0.1,100", out PresentMonFrame frame));
        Assert.Equal(7, frame.Pid);
        Assert.Equal(16.7, frame.FrameTimeMs, 3);
        Assert.Equal(1.2, frame.CpuBusyMs, 3);
        Assert.Equal(0.4, frame.GpuWaitMs, 3);
        Assert.Equal(0.1, frame.AnimationErrorMs, 3);
        Assert.Equal(100, frame.TimeMs, 3);
    }

    [Fact]
    public void TryParse_HeaderLookupIsCaseInsensitive()
    {
        var parser = new FrameCsvParser("processid,MSBETWEENPRESENTS");

        Assert.True(parser.TryParse("7,20.0", out PresentMonFrame frame));
        Assert.Equal(7, frame.Pid);
        Assert.Equal(20.0, frame.FrameTimeMs, 3);
        Assert.Equal(-1, frame.TimeMs, 3);
        Assert.Equal(PresentMode.Unknown, frame.PresentMode);
    }

    [Fact]
    public void TryParse_MissingOptionalColumnsDegradeToZero()
    {
        var parser = new FrameCsvParser("ProcessID,MsBetweenPresents");

        Assert.True(parser.TryParse("9,8.25", out PresentMonFrame frame));
        Assert.Equal(0, frame.CpuBusyMs, 3);
        Assert.Equal(0, frame.GpuWaitMs, 3);
        Assert.Equal(0, frame.DisplayLatencyMs, 3);
    }

    [Fact]
    public void TryParse_RejectsInvalidFrameTime()
    {
        var parser = new FrameCsvParser("ProcessID,MsBetweenPresents,CPUStartTime");

        Assert.False(parser.TryParse("9,NA,100", out _));
        Assert.False(parser.TryParse("9,0,100", out _));
        Assert.False(parser.TryParse("9,-1,100", out _));
        Assert.False(parser.TryParse("0,16.7,100", out _));
    }

    [Fact]
    public void Constructor_RequiresFrameTimeAndProcessId()
    {
        Assert.Throws<FormatException>(() => new FrameCsvParser("ProcessID,CPUStartTime"));
        Assert.Throws<FormatException>(() => new FrameCsvParser("MsBetweenPresents"));
    }
}
