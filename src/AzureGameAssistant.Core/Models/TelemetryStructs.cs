using System.Runtime.InteropServices;

namespace AzureGameAssistant.Core.Models;

/// <summary>
/// Fixed layout of the shared-memory telemetry map. The constants are part of the
/// collector/UI protocol and must be asserted by tests whenever a struct changes.
/// </summary>
public static class TelemetryLayout
{
    public const string MapName = @"Local\AzureGameAssistant.Telemetry.v3";
    public const long Magic = 0x33475341; // 'AGS3'
    public const int ProtocolVersion = 3;

    public const int HeaderSize = 128;
    public const int SampleRegionOffset = 256;
    public const int SampleStride = 256;
    public const int SampleCapacity = 4500;

    public const int EventRegionOffset = SampleRegionOffset + (SampleStride * SampleCapacity);
    public const int EventStride = 64;
    public const int EventCapacity = 256;

    public const int FrameRegionOffset = EventRegionOffset + (EventStride * EventCapacity);
    public const int FrameStride = 48;
    public const int FrameCapacity = 65536;

    public const int MapSize = 8 * 1024 * 1024;

    public static bool IsValid =>
        SampleStride == Marshal.SizeOf<SampleRecord>() &&
        EventStride == Marshal.SizeOf<EventRecord>() &&
        FrameStride == Marshal.SizeOf<FrameRecord>() &&
        SampleRegionOffset + (SampleStride * SampleCapacity) == EventRegionOffset &&
        EventRegionOffset + (EventStride * EventCapacity) <= FrameRegionOffset &&
        FrameRegionOffset + (FrameStride * FrameCapacity) <= MapSize;
}

public static class SampleFlags
{
    public const ushort ProcessValid = 1 << 0;
    public const ushort GpuValid = 1 << 1;
    public const ushort VendorValid = 1 << 2;
    public const ushort FrameValid = 1 << 3;
    public const ushort CpuSensorValid = 1 << 4;
}

public static class ProcessSlotFlags
{
    public const ushort Foreground = 1 << 0;
    public const ushort Protected = 1 << 1;
}

public static class CollectorModeFlags
{
    public const int GameMode = 1 << 0;
    public const int Paused = 1 << 1;
    public const int DeepMode = 1 << 2;
    public const int FrameCapture = 1 << 3;
    public const int Sensors = 1 << 4;
}

public static class CapabilityFlags
{
    public const int Frames = 1 << 0;
    public const int VendorSensors = 1 << 1;
    public const int ProcessAccess = 1 << 2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct SharedHeader
{
    public long Magic;
    public int ProtocolVersion;
    public int SampleStride;
    public int SampleCapacity;
    public int EventCapacity;
    public int FrameStride;
    public int FrameCapacity;
    public long WriteSequence;
    public long WriteIndex;
    public long EventWriteIndex;
    public long FrameWriteSequence;
    public long FrameWriteIndex;
    public long HeartbeatQpc;
    public int CollectorPid;
    public int SamplingIntervalMs;
    public int ModeFlags;
    public int CapabilityFlags;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ProcessSlot
{
    public uint Pid;
    public uint StartTimeLow;
    public float CpuPct;
    public float GpuPct;
    public uint WorkingSetMb;
    public uint PrivateMb;
    public ushort NameHint;
    public ushort Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VendorSlot
{
    public float TemperatureC;
    public float HotSpotC;
    public float PowerW;
    public float CoreClockMhz;
    public float MemoryClockMhz;
    public float FanPct;
    public float VramUsedMb;
    public float VramTotalMb;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct CpuSensorSlot
{
    public float TemperatureC;
    public float PackagePowerW;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct SampleRecord
{
    public long QpcTicks;
    public long UtcTicks;
    public float CpuUtilityPct;
    public float CpuFrequencyPct;
    public uint MemAvailableMb;
    public uint MemCommitMb;
    public uint MemCommitLimitMb;
    public float PageFilePct;
    public float DiskBusyPct;
    public float DiskLatencyMs;
    public float GpuUtilPct;
    public uint GpuDedicatedMb;
    public uint GpuSharedMb;
    public float TimerJitterMs;
    public uint ForegroundPid;
    public ushort ProcessorQueueLength;
    public ushort Flags;
    public ProcessSlot P0;
    public ProcessSlot P1;
    public ProcessSlot P2;
    public ProcessSlot P3;
    public ProcessSlot P4;
    public VendorSlot V0;
    public CpuSensorSlot C0;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct EventRecord
{
    public long Id;
    public long StartQpc;
    public long EndQpc;
    public float Score;
    public int Signals;
    public int Cause;
    public uint PrimaryPid;
    public int Reserved0;
    public long Reserved1;
    public long Reserved2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct FrameRecord
{
    public long QpcTicks;
    public float FrameTimeMs;
    public float CpuBusyMs;
    public float CpuWaitMs;
    public float GpuLatencyMs;
    public float GpuBusyMs;
    public float GpuWaitMs;
    public float DisplayLatencyMs;
    public float AnimationErrorMs;
    public int Pid;
    public ushort PresentMode;
    public ushort Flags;
}
