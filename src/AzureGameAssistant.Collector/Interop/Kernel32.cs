using System.Runtime.InteropServices;

namespace AzureGameAssistant.Collector.Interop;

internal static partial class Kernel32
{
    public const uint ErrorAlreadyExists = 183;
    public const uint PageReadWrite = 0x04;
    public const uint FileMapAllAccess = 0x000F001F;
    public const uint CreateWaitableTimerHighResolution = 0x00000002;
    public const uint TimerAllAccess = 0x001F0003;
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 258;

    public static readonly IntPtr InvalidHandleValue = new(-1);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateMutexW(
        IntPtr attributes,
        [MarshalAs(UnmanagedType.Bool)] bool initialOwner,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReleaseMutex(IntPtr mutex);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceCounter(out long counter);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceFrequency(out long frequency);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateFileMappingW(
        IntPtr file,
        IntPtr attributes,
        uint protect,
        uint maximumSizeHigh,
        uint maximumSizeLow,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr MapViewOfFile(
        IntPtr mapping,
        uint desiredAccess,
        uint offsetHigh,
        uint offsetLow,
        nuint numberOfBytesToMap);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnmapViewOfFile(IntPtr address);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr CreateWaitableTimerExW(
        IntPtr attributes,
        string? name,
        uint flags,
        uint desiredAccess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWaitableTimer(
        IntPtr timer,
        ref long dueTime,
        int period,
        IntPtr completionRoutine,
        IntPtr argument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelWaitableTimer(IntPtr timer);
}
