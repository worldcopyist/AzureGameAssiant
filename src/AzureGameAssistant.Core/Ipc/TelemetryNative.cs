using System.Runtime.InteropServices;

namespace AzureGameAssistant.Core.Ipc;

internal static partial class TelemetryNative
{
    public const uint FileMapRead = 0x0004;

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr OpenFileMappingW(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceCounter(out long counter);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryPerformanceFrequency(out long frequency);
}
