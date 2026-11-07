using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzureGameAssistant.Collector.Interop;

/// <summary>
/// Job object used to guarantee the PresentMon child is killed if the collector
/// exits without disposing it.
/// </summary>
internal static partial class JobObject
{
    private const int ExtendedLimitInformation = 9;
    private const uint LimitKillOnJobClose = 0x00002000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    public static IntPtr CreateKillOnClose()
    {
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var limit = new ExtendedLimit();
        limit.BasicLimitInformation.LimitFlags = LimitKillOnJobClose;

        int size = Marshal.SizeOf<ExtendedLimit>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limit, buffer, false);
            if (!SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size))
            {
                Kernel32.CloseHandle(job);
                return IntPtr.Zero;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return job;
    }

    public static bool Assign(IntPtr job, Process process) =>
        job != IntPtr.Zero && AssignProcessToJobObject(job, process.Handle);
}
