using System.Runtime.InteropServices;

namespace AzureGameAssistant.Core.Sensors;

/// <summary>
/// Converts NVML milliwatt readings into watts and rejects implausible values
/// so a driver quirk cannot show a nonsense number in the overlay.
/// </summary>
public static class GpuPowerMath
{
    public const uint NvmlSuccess = 0;
    public const uint NvmlErrorNotSupported = 3;

    public static float MilliwattsToWatts(uint milliwatts)
    {
        if (milliwatts == 0)
        {
            return float.NaN;
        }

        float watts = milliwatts / 1000f;
        return watts is > 0 and < 2_000 ? watts : float.NaN;
    }
}

/// <summary>
/// Secondary GPU power source. LibreHardwareMonitor occasionally lacks a power
/// sensor for newer NVIDIA GPUs; NVML usually exposes it. When either the DLL,
/// the device or the reading is missing, callers keep NaN and the UI shows "—".
/// </summary>
public sealed class NvmlPowerReader : IDisposable
{
    private IntPtr _device;
    private bool _initialized;

    private NvmlPowerReader(IntPtr device)
    {
        _device = device;
        _initialized = true;
    }

    public static NvmlPowerReader? TryCreate()
    {
        try
        {
            if (nvmlInit_v2() != GpuPowerMath.NvmlSuccess)
            {
                return null;
            }

            int handleResult = nvmlDeviceGetHandleByIndex_v2(0, out IntPtr device);
            if (handleResult != GpuPowerMath.NvmlSuccess || device == IntPtr.Zero)
            {
                handleResult = nvmlDeviceGetHandleByIndex(0, out device);
            }

            if (handleResult != GpuPowerMath.NvmlSuccess || device == IntPtr.Zero)
            {
                nvmlShutdown();
                return null;
            }

            return new NvmlPowerReader(device);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public bool TryRead(out float watts)
    {
        watts = float.NaN;
        if (!_initialized)
        {
            return false;
        }

        try
        {
            if (nvmlDeviceGetPowerUsage(_device, out uint milliwatts) != GpuPowerMath.NvmlSuccess)
            {
                return false;
            }

            watts = GpuPowerMath.MilliwattsToWatts(milliwatts);
            return !float.IsNaN(watts);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (!_initialized)
        {
            return;
        }

        _initialized = false;
        _device = IntPtr.Zero;
        try
        {
            nvmlShutdown();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex")]
    private static extern int nvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int nvmlShutdown();
}
