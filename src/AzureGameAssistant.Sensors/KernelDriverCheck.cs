using System.Runtime.InteropServices;

namespace AzureGameAssistant.Sensors;

/// <summary>
/// Refuses to run when a known Ring0 sensor driver is already loaded. GPU-only
/// LHM should never load one; this guard turns a silent regression into a clear
/// exit code instead of risking anti-cheat interference.
/// </summary>
internal static partial class KernelDriverCheck
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x00000004;

    private static readonly string[] DriverNames =
    [
        "WinRing0_1_2_0",
        "WinRing0x64",
        "WinRing0",
        "inpoutx64",
        "RTCore64",
        "MsIo",
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);

    public static string? FindRunning()
    {
        IntPtr manager = OpenSCManagerW(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            foreach (string name in DriverNames)
            {
                IntPtr service = OpenServiceW(manager, name, ServiceQueryStatus);
                if (service == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (QueryServiceStatus(service, out ServiceStatus status) &&
                        status.CurrentState == ServiceRunning)
                    {
                        return name;
                    }
                }
                finally
                {
                    CloseServiceHandle(service);
                }
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }

        return null;
    }
}
