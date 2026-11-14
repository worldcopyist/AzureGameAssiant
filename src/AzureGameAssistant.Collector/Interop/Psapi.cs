using System.Runtime.InteropServices;

namespace AzureGameAssistant.Collector.Interop;

internal static partial class Psapi
{
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EmptyWorkingSet(IntPtr process);
}
