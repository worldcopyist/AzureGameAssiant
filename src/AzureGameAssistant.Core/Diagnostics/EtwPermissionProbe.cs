using System.Runtime.InteropServices;

namespace AzureGameAssistant.Core.Diagnostics;

public enum EtwProbeResult
{
    Allowed,
    AccessDenied,
    PrivilegeNotHeld,
    Failed,
}

/// <summary>
/// Starts and immediately stops a uniquely named real-time trace session to
/// check whether the current token can create ETW sessions. This is the same
/// privilege PresentMon needs, but the probe does not touch any game process.
/// </summary>
public static class EtwPermissionProbe
{
    private const int WnodeFlagTracedGuid = 0x00020000;
    private const int EventTraceRealTimeMode = 0x00000100;
    private const uint EventTraceControlStop = 1;
    private const int ErrorSuccess = 0;
    private const int ErrorAccessDenied = 5;
    private const int ErrorPrivilegeNotHeld = 1314;

    public static EtwProbeResult Probe(out int errorCode)
    {
        errorCode = ErrorSuccess;

        string name = "AzureGameAssistant-Probe-" + Guid.NewGuid().ToString("N");
        int structSize = Marshal.SizeOf<EventTraceProperties>();
        int nameBytes = (name.Length + 1) * sizeof(char);
        IntPtr buffer = Marshal.AllocHGlobal(structSize + nameBytes);

        try
        {
            var properties = new EventTraceProperties
            {
                Wnode = new WnodeHeader
                {
                    BufferSize = structSize + nameBytes,
                    Guid = Guid.NewGuid(),
                    Flags = WnodeFlagTracedGuid,
                },
                LogFileMode = EventTraceRealTimeMode,
                FlushTimer = 1,
                LoggerNameOffset = structSize,
            };

            Marshal.StructureToPtr(properties, buffer, false);
            Marshal.Copy(name.ToCharArray(), 0, buffer + structSize, name.Length);
            Marshal.WriteInt16(buffer + structSize + (name.Length * sizeof(char)), 0);

            int start = StartTraceW(out ulong handle, name, buffer);
            errorCode = start;
            if (start == ErrorSuccess)
            {
                ControlTraceW(handle, name, buffer, EventTraceControlStop);
                return EtwProbeResult.Allowed;
            }

            return start switch
            {
                ErrorAccessDenied => EtwProbeResult.AccessDenied,
                ErrorPrivilegeNotHeld => EtwProbeResult.PrivilegeNotHeld,
                _ => EtwProbeResult.Failed,
            };
        }
        catch (Exception)
        {
            errorCode = Marshal.GetLastWin32Error();
            return EtwProbeResult.Failed;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WnodeHeader
    {
        public int BufferSize;
        public int ProviderId;
        public long HistoricalContext;
        public long TimeStamp;
        public Guid Guid;
        public int ClientContext;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceProperties
    {
        public WnodeHeader Wnode;
        public int BufferSize;
        public int MinimumBuffers;
        public int MaximumBuffers;
        public int MaximumFileSize;
        public int LogFileMode;
        public int FlushTimer;
        public int EnableFlags;
        public int AgeLimit;
        public int NumberOfBuffers;
        public int FreeBuffers;
        public int EventsLost;
        public int BuffersWritten;
        public int LogBuffersLost;
        public int RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public int LogFileNameOffset;
        public int LoggerNameOffset;
    }

    [DllImport("advapi32.dll", EntryPoint = "StartTraceW", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(
        ulong sessionHandle,
        string? sessionName,
        IntPtr properties,
        uint controlCode);
}
