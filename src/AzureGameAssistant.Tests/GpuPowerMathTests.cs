using AzureGameAssistant.Core.Sensors;

namespace AzureGameAssistant.Tests;

public sealed class GpuPowerMathTests
{
    [Theory]
    [InlineData(1u, 0.001f)]
    [InlineData(1_500u, 1.5f)]
    [InlineData(75_500u, 75.5f)]
    [InlineData(450_000u, 450f)]
    [InlineData(1_999_999u, 1999.999f)]
    public void ConvertsMilliwattsToWatts(uint milliwatts, float expected)
    {
        Assert.Equal(expected, GpuPowerMath.MilliwattsToWatts(milliwatts), 3);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2_000_000u)]
    [InlineData(uint.MaxValue)]
    public void RejectsImplausibleReadings(uint milliwatts)
    {
        Assert.True(float.IsNaN(GpuPowerMath.MilliwattsToWatts(milliwatts)));
    }

    [Fact]
    public void LiveNvmlIsOptionalButNeverThrows()
    {
        using NvmlPowerReader? reader = NvmlPowerReader.TryCreate();
        if (reader is null)
        {
            return;
        }

        bool ok = reader.TryRead(out float watts);
        if (ok)
        {
            Assert.InRange(watts, 0.001f, 1_999.999f);
        }
        else
        {
            Assert.True(float.IsNaN(watts));
        }
    }
}
