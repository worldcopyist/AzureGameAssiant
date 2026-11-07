using System.Runtime.InteropServices;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sensors;

namespace AzureGameAssistant.Tests;

public sealed class SensorLayoutTests
{
    [Fact]
    public void SensorLayoutIsConsistent()
    {
        Assert.True(SensorLayout.IsValid);
        Assert.Equal(SensorLayout.SlotStride, Marshal.SizeOf<SensorRecord>());
        Assert.Equal(SensorLayout.SlotOffset + (SensorLayout.SlotStride * SensorLayout.SlotCapacity) <= SensorLayout.MapSize, true);
    }

    [Fact]
    public void CpuSlotFitsSampleStride()
    {
        Assert.Equal(8, Marshal.SizeOf<CpuSensorSlot>());
        Assert.Equal(TelemetryLayout.SampleStride, Marshal.SizeOf<SampleRecord>());
    }

    [Fact]
    public void CpuTemperatureRequiresValidFlag()
    {
        var sample = new SampleRecord
        {
            C0 = new CpuSensorSlot { TemperatureC = 61.5f },
            Flags = SampleFlags.CpuSensorValid,
        };

        Assert.True(TelemetryMath.TryGetCpuTemperature(sample, out double celsius));
        Assert.Equal(61.5, celsius, 3);

        sample.Flags = 0;
        Assert.False(TelemetryMath.TryGetCpuTemperature(sample, out _));
    }
}
