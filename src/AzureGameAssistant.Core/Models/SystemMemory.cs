using System.Runtime.InteropServices;

namespace AzureGameAssistant.Core.Models;

/// <summary>
/// Physical memory size for the current machine. Commit limit (physical +
/// page file) must not be presented as total RAM; the UI uses this value to
/// derive used/available percentages.
/// </summary>
public static class SystemMemory
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private static readonly Lazy<double> TotalMb = new(ReadTotalMb);

    public static double TotalPhysicalMb => TotalMb.Value;

    public static bool TryGetUsage(in SampleRecord sample, out double usedMb, out double totalMb)
    {
        totalMb = TotalPhysicalMb;
        usedMb = Math.Max(0, totalMb - sample.MemAvailableMb);
        return totalMb > 0;
    }

    private static double ReadTotalMb()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };
            if (GlobalMemoryStatusEx(ref status) && status.TotalPhys > 0)
            {
                return status.TotalPhys / BytesPerMegabyte;
            }
        }

        long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available / BytesPerMegabyte : 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
