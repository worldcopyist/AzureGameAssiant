using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzureGameAssistant.Core.Diagnostics;

public readonly record struct ProcessResourceCounters(
    int HandleCount,
    int GdiObjects,
    int UserObjects,
    int ThreadCount,
    double PrivateMb);

/// <summary>
/// Reads handle, GDI and USER object counts. A leak in WPF shows up as slowly
/// rising GDI/USER counts long before memory moves, so the long-run recorder
/// samples these alongside memory.
/// </summary>
public static class ProcessResourceProbe
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint GrGdiObjects = 0;
    private const uint GrUserObjects = 1;

    public static bool TryRead(int pid, out ProcessResourceCounters counters)
    {
        counters = default;
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Refresh();
            ReadGuiObjects(pid, out int gdi, out int user);
            counters = new ProcessResourceCounters(
                process.HandleCount,
                gdi,
                user,
                process.Threads.Count,
                process.PrivateMemorySize64 / 1048576.0);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static void ReadGuiObjects(int pid, out int gdi, out int user)
    {
        gdi = -1;
        user = -1;
        IntPtr handle = OpenProcess(ProcessQueryInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            gdi = (int)GetGuiResources(handle, GrGdiObjects);
            user = (int)GetGuiResources(handle, GrUserObjects);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr process, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <summary>
/// Accumulates one process's resource samples so the summary can report first,
/// last, max and the net growth over the run.
/// </summary>
public sealed class ResourceSeries(string name)
{
    public string Name { get; } = name;

    public int Samples { get; private set; }

    public int HandlesFirst { get; private set; } = -1;

    public int HandlesLast { get; private set; } = -1;

    public int HandlesMax { get; private set; } = -1;

    public int GdiFirst { get; private set; } = -1;

    public int GdiLast { get; private set; } = -1;

    public int GdiMax { get; private set; } = -1;

    public int UserFirst { get; private set; } = -1;

    public int UserLast { get; private set; } = -1;

    public int UserMax { get; private set; } = -1;

    public int HandleGrowth => HandlesFirst < 0 || HandlesLast < 0 ? 0 : HandlesLast - HandlesFirst;

    public int GdiGrowth => GdiFirst < 0 || GdiLast < 0 ? 0 : GdiLast - GdiFirst;

    public int UserGrowth => UserFirst < 0 || UserLast < 0 ? 0 : UserLast - UserFirst;

    public void Add(in ProcessResourceCounters counters)
    {
        Samples++;
        HandlesFirst = HandlesFirst < 0 ? counters.HandleCount : HandlesFirst;
        HandlesLast = counters.HandleCount;
        HandlesMax = Math.Max(HandlesMax, counters.HandleCount);

        if (counters.GdiObjects >= 0)
        {
            GdiFirst = GdiFirst < 0 ? counters.GdiObjects : GdiFirst;
            GdiLast = counters.GdiObjects;
            GdiMax = Math.Max(GdiMax, counters.GdiObjects);
        }

        if (counters.UserObjects >= 0)
        {
            UserFirst = UserFirst < 0 ? counters.UserObjects : UserFirst;
            UserLast = counters.UserObjects;
            UserMax = Math.Max(UserMax, counters.UserObjects);
        }
    }
}
