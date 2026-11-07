using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class TelemetryMathTests
{
    [Fact]
    public void MemoryUsagePct_UsesPhysicalMemoryNotCommitLimit()
    {
        double total = SystemMemory.TotalPhysicalMb;
        if (total <= 0)
        {
            return;
        }

        uint available = (uint)Math.Max(0, total / 2);
        var sample = new SampleRecord
        {
            MemAvailableMb = available,
            MemCommitMb = (uint)total,
            MemCommitLimitMb = (uint)(total * 2),
        };

        double pct = TelemetryMath.MemoryUsagePct(sample);

        Assert.InRange(pct, 49.0, 51.0);
    }
}
