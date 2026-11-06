using System.Runtime.InteropServices;

namespace AzureGameAssistant.Collector.Interop;

internal static partial class User32
{
    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
