using System.Runtime.InteropServices;

namespace AzureGameAssistant.Collector.Interop;

internal static partial class Ntdll
{
    public const int SystemProcessInformation = 5;
    public const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    public const int StatusBufferOverflow = unchecked((int)0x80000005);
    public const int StatusBufferTooSmall = unchecked((int)0xC0000023);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(
        int informationClass,
        IntPtr information,
        int informationLength,
        out int returnLength);
}
