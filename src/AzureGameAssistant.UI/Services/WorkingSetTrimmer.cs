using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Returns pages the process no longer touches to the OS. Only used while every
/// window is hidden: trimming a visible WPF window forces a full repaint and
/// faults the same pages back in.
/// </summary>
internal static class WorkingSetTrimmer
{
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);

    public static (long BeforeMb, long AfterMb) Trim()
    {
        using Process process = Process.GetCurrentProcess();
        long before = 0;
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
            process.Refresh();
            before = process.WorkingSet64 / 1048576;

            if (!EmptyWorkingSet(process.Handle))
            {
                return (before, before);
            }

            process.Refresh();
            return (before, process.WorkingSet64 / 1048576);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return (before, before);
        }
    }
}
