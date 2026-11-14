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
        long before = ReadWorkingSetMb(process);
        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);

            if (!EmptyWorkingSet(process.Handle))
            {
                return (before, before);
            }

            return (before, ReadWorkingSetMb(process));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return (before, before);
        }
    }

    private static long ReadWorkingSetMb(Process process)
    {
        try
        {
            process.Refresh();
            long bytes = process.WorkingSet64;
            if (bytes > 0)
            {
                return bytes / 1048576;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
        }

        // The counter occasionally reads zero right after a trim; report the
        // runtime's own figure instead of logging a bogus 0 MB.
        return Math.Max(1, Environment.WorkingSet / 1048576);
    }
}
