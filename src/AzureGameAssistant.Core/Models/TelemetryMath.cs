namespace AzureGameAssistant.Core.Models;

public static class TelemetryMath
{
    public static double MemoryUsagePct(in SampleRecord sample) =>
        sample.MemCommitLimitMb == 0
            ? 0
            : Math.Clamp(100.0 * sample.MemCommitMb / sample.MemCommitLimitMb, 0, 100);

    public static bool HasVendorData(in SampleRecord sample) =>
        (sample.Flags & SampleFlags.VendorValid) != 0;

    public static bool TryGetVramTotalMb(in SampleRecord sample, out double totalMb)
    {
        totalMb = HasVendorData(sample) ? sample.V0.VramTotalMb : 0;
        return totalMb > 0;
    }

    public static bool TryGetGpuTemperature(in SampleRecord sample, out double celsius)
    {
        celsius = HasVendorData(sample) ? sample.V0.TemperatureC : 0;
        return celsius > 0;
    }

    public static bool TryGetCpuTemperature(in SampleRecord sample, out double celsius)
    {
        celsius = (sample.Flags & SampleFlags.CpuSensorValid) != 0 ? sample.C0.TemperatureC : 0;
        return celsius > 0;
    }

    public static bool HasCpuSensorData(in SampleRecord sample) =>
        (sample.Flags & SampleFlags.CpuSensorValid) != 0;
}
