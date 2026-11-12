using System.Runtime.InteropServices;

namespace AzureGameAssistant.UI.Interop;

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
}
