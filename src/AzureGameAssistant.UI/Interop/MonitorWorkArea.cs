using System.Runtime.InteropServices;

namespace AzureGameAssistant.UI.Interop;

internal sealed record MonitorEntry(string DeviceName, bool Primary, NativeMethods.Rect32 Work);

internal static class MonitorWorkArea
{
    /// <summary>
    /// Returns the work area (physical pixels) of the monitor that owns the
    /// given window, so overlays can follow the main window across displays.
    /// </summary>
    public static bool TryGet(IntPtr window, out NativeMethods.Rect32 work)
    {
        work = default;
        if (window == IntPtr.Zero)
        {
            return false;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MonitorInfo
        {
            Size = Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        work = info.Work;
        return true;
    }

    public static IReadOnlyList<MonitorEntry> Enumerate()
    {
        var monitors = new List<MonitorEntry>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new NativeMethods.MonitorInfoEx
            {
                Size = Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            };

            if (NativeMethods.GetMonitorInfoEx(monitor, ref info))
            {
                monitors.Add(new MonitorEntry(
                    info.DeviceName,
                    (info.Flags & NativeMethods.MonitorInfoPrimary) != 0,
                    info.Work));
            }

            return true;
        }, IntPtr.Zero);

        return monitors;
    }

    public static bool TryGetByDevice(string? deviceName, out NativeMethods.Rect32 work)
    {
        work = default;
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        foreach (MonitorEntry monitor in Enumerate())
        {
            if (string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                work = monitor.Work;
                return true;
            }
        }

        return false;
    }
}
