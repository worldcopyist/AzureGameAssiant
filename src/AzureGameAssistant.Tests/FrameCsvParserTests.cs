using AzureGameAssistant.Core.Frames;

namespace AzureGameAssistant.Tests;

public sealed class FrameCsvParserTests
{
    private const string V2Header =
        "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags," +
        "AllowsTearing,PresentMode,CPUStartTime,MsCPUBusy,MsCPUWait,MsGPULatency," +
        "MsGPUTime,MsGPUBusy,MsGPUWait,DisplayLatency,MsAnimationError,MsBetweenPresents," +
        "MsBetweenDisplayChange";

    [Fact]
    public void TryParse_MapsV2Columns()
    {
        var parser = new FrameCsvParser(V2Header);

        bool parsed = parser.TryParse(
            "\"Game, The.exe\",4242,0x1,DXGI,1,0,1,Hardware: Independent Flip," +
            "123.4,1.5,2.5,3.5,4.5,5.5,6.5,7.5,8.5,16.7,16.6",
            out PresentMonFrame frame);

        Assert.True(parsed);
        Assert.Equal(4242, frame.Pid);
        Assert.Equal(123.4, frame.TimeMs, 3);
        Assert.Equal(16.7, frame.FrameTimeMs, 3);
        Assert.Equal(1.5, frame.CpuBusyMs, 3);
        Assert.Equal(2.5, frame.CpuWaitMs, 3);
        Assert.Equal(3.5, frame.GpuLatencyMs, 3);
        Assert.Equal(4.5, frame.GpuTimeMs, 3);
        Assert.Equal(5.5, frame.GpuBusyMs, 3);
        Assert.Equal(6.5, frame.GpuWaitMs, 3);
        Assert.Equal(7.5, frame.DisplayLatencyMs, 3);
        Assert.Equal(8.5, frame.AnimationErrorMs, 3);
        Assert.Equal(PresentMode.HardwareIndependentFlip, frame.PresentMode);
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
