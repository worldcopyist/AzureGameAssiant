using System.Runtime.InteropServices;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class TelemetryLayoutTests
{
    [Fact]
    public void LayoutConstantsAreConsistent()
    {
        Assert.True(TelemetryLayout.IsValid);
    }

    [Fact]
    public void HeaderFitsReservedArea()
    {
        Assert.True(Marshal.SizeOf<SharedHeader>() <= TelemetryLayout.HeaderSize);
    }

    [Fact]
    public void FrameRegionEndsWithinMap()
    {
        long end = TelemetryLayout.FrameRegionOffset + (long)TelemetryLayout.FrameStride * TelemetryLayout.FrameCapacity;
        Assert.True(end <= TelemetryLayout.MapSize);
    }

    [Fact]
    public void RecordSizesMatchPublishedStrides()
    {
        Assert.Equal(TelemetryLayout.SampleStride, Marshal.SizeOf<SampleRecord>());
        Assert.Equal(TelemetryLayout.FrameStride, Marshal.SizeOf<FrameRecord>());
        Assert.Equal(TelemetryLayout.EventStride, Marshal.SizeOf<EventRecord>());
    }
}
