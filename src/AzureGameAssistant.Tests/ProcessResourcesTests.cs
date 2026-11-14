using System.Diagnostics;
using AzureGameAssistant.Core.Diagnostics;

namespace AzureGameAssistant.Tests;

public sealed class ProcessResourcesTests
{
    [Fact]
    public void ReadsCurrentProcessCounters()
    {
        bool ok = ProcessResourceProbe.TryRead(Environment.ProcessId, out ProcessResourceCounters counters);

        Assert.True(ok);
        Assert.True(counters.HandleCount > 0);
        Assert.True(counters.ThreadCount > 0);
        Assert.True(counters.PrivateMb > 0);
        Assert.True(counters.GdiObjects >= 0);
        Assert.True(counters.UserObjects >= 0);
    }

    [Fact]
    public void RejectsInvalidPid()
    {
        Assert.False(ProcessResourceProbe.TryRead(0, out _));
        Assert.False(ProcessResourceProbe.TryRead(-5, out _));
        Assert.False(ProcessResourceProbe.TryRead(int.MaxValue, out _));
    }

    [Fact]
    public void SeriesReportsGrowthAndPeaks()
    {
        var series = new ResourceSeries("ui");

        series.Add(new ProcessResourceCounters(500, 100, 80, 20, 50));
        series.Add(new ProcessResourceCounters(620, 130, 95, 21, 51));
        series.Add(new ProcessResourceCounters(610, 140, 90, 21, 52));

        Assert.Equal(3, series.Samples);
        Assert.Equal(500, series.HandlesFirst);
        Assert.Equal(610, series.HandlesLast);
        Assert.Equal(620, series.HandlesMax);
        Assert.Equal(110, series.HandleGrowth);
        Assert.Equal(40, series.GdiGrowth);
        Assert.Equal(10, series.UserGrowth);
        Assert.Equal(140, series.GdiMax);
        Assert.Equal(95, series.UserMax);
    }

    [Fact]
    public void SeriesIgnoresUnavailableGuiCounters()
    {
        var series = new ResourceSeries("collector");

        series.Add(new ProcessResourceCounters(300, -1, -1, 12, 10));
        series.Add(new ProcessResourceCounters(310, -1, -1, 12, 10));

        Assert.Equal(2, series.Samples);
        Assert.Equal(10, series.HandleGrowth);
        Assert.Equal(-1, series.GdiFirst);
        Assert.Equal(0, series.GdiGrowth);
        Assert.Equal(-1, series.UserLast);
    }

    [Fact]
    public void ProbeMatchesProcessHandleCount()
    {
        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        Assert.True(ProcessResourceProbe.TryRead(Environment.ProcessId, out ProcessResourceCounters counters));
        Assert.InRange(counters.HandleCount, process.HandleCount - 20, process.HandleCount + 20);
    }
}
