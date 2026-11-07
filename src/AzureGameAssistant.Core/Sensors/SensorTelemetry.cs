using System.Runtime.InteropServices;
using System.Threading;
using AzureGameAssistant.Core.Ipc;

namespace AzureGameAssistant.Core.Sensors;

/// <summary>
/// Fixed layout of the optional sensor sidecar's shared memory. Slots are
/// appended once per sidecar interval; values that the hardware does not expose
/// stay <see cref="float.NaN"/>.
/// </summary>
public static class SensorLayout
{
    public const string MapName = @"Local\AzureGameAssistant.Sensors.v1";
    public const long Magic = 0x31534741; // 'AGS1'
    public const int ProtocolVersion = 1;

    public const int HeaderSize = 128;
    public const int SlotOffset = 128;
    public const int SlotStride = 128;
    public const int SlotCapacity = 900;
    public const int MapSize = 128 * 1024;

    public static bool IsValid =>
        SlotStride == Marshal.SizeOf<SensorRecord>() &&
        HeaderSize >= Marshal.SizeOf<SensorHeader>() &&
        SlotOffset + ((long)SlotStride * SlotCapacity) <= MapSize;
}

public static class SensorFlags
{
    public const ushort GpuValid = 1 << 0;
    public const ushort CpuValid = 1 << 1;
    public const ushort VramValid = 1 << 2;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct SensorHeader
{
    public long Magic;
    public int ProtocolVersion;
    public int SlotStride;
    public int SlotCapacity;
    public long WriteSequence;
    public long WriteIndex;
    public long HeartbeatQpc;
    public int WriterPid;
    public int IntervalMs;
    public int Capabilities;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct SensorRecord
{
    public long QpcTicks;
    public long UtcTicks;
    public float GpuTemperatureC;
    public float GpuHotSpotC;
    public float GpuPowerW;
    public float GpuCoreClockMhz;
    public float GpuMemoryClockMhz;
    public float GpuFanPct;
    public float VramUsedMb;
    public float VramTotalMb;
    public float CpuTemperatureC;
    public float CpuPowerW;
    public ushort GpuIndex;
    public ushort Flags;
    public int Reserved0;
    public long Reserved1;
    public long Reserved2;
    public long Reserved3;
    public long Reserved4;
    public long Reserved5;
    public long Reserved6;
    public long Reserved7;
    public long Reserved8;
}

/// <summary>
/// Read-only consumer of the sensor sidecar map. Only the newest slot is
/// exposed; consumers decide how fresh it must be before trusting it.
/// </summary>
public sealed unsafe class SensorReader : IDisposable
{
    private const int ReadRetryLimit = 8;

    private IntPtr _mapping;
    private IntPtr _view;
    private SensorHeader* _header;
    private byte* _slotBase;
    private long _frequency;
    private long _lastQpc = long.MinValue;

    public bool IsOpen => _view != IntPtr.Zero;

    public string? LastOpenError { get; private set; }

    public SensorRecord Latest { get; private set; }

    public long WriteIndex { get; private set; }

    public bool TryOpen()
    {
        Dispose();
        LastOpenError = null;

        if (!SensorLayout.IsValid)
        {
            LastOpenError = "传感器共享内存布局校验失败";
            return false;
        }

        if (_frequency == 0 && !TelemetryNative.QueryPerformanceFrequency(out _frequency))
        {
            _frequency = 10_000_000;
        }

        _mapping = TelemetryNative.OpenFileMappingW(TelemetryNative.FileMapRead, false, SensorLayout.MapName);
        if (_mapping == IntPtr.Zero)
        {
            LastOpenError = "传感器边车尚未启动";
            return false;
        }

        _view = TelemetryNative.MapViewOfFile(
            _mapping,
            TelemetryNative.FileMapRead,
            0,
            0,
            SensorLayout.MapSize);

        if (_view == IntPtr.Zero)
        {
            LastOpenError = "映射传感器共享内存失败";
            Dispose();
            return false;
        }

        _header = (SensorHeader*)_view;
        _slotBase = (byte*)_view + SensorLayout.SlotOffset;

        if (!TryReadHeader(out SensorHeader header) || !ValidateHeader(header))
        {
            LastOpenError = "传感器共享内存头无效";
            Dispose();
            return false;
        }

        return true;
    }

    public bool TryReadHeader(out SensorHeader header)
    {
        header = default;

        if (_header == null)
        {
            return false;
        }

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            long before = Volatile.Read(ref _header->WriteSequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(64);
                continue;
            }

            SensorHeader copy = *_header;
            long after = Volatile.Read(ref _header->WriteSequence);

            if (before == after)
            {
                header = copy;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reads the newest slot. Returns false when the map is closed, empty, or
    /// the writer was mid-update for every attempt.
    /// </summary>
    public bool Refresh()
    {
        if (_header == null)
        {
            return false;
        }

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            long before = Volatile.Read(ref _header->WriteSequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(64);
                continue;
            }

            long writeIndex = Volatile.Read(ref _header->WriteIndex);
            if (writeIndex <= 0)
            {
                return false;
            }

            long slot = (writeIndex - 1) % SensorLayout.SlotCapacity;
            SensorRecord copy = *(SensorRecord*)(_slotBase + (slot * SensorLayout.SlotStride));

            long after = Volatile.Read(ref _header->WriteSequence);
            if (before == after && writeIndex == Volatile.Read(ref _header->WriteIndex))
            {
                Latest = copy;
                WriteIndex = writeIndex;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a new slot arrived since the previous call and its heartbeat is
    /// younger than <paramref name="maxAge"/>.
    /// </summary>
    public bool TryReadFresh(TimeSpan maxAge)
    {
        if (!Refresh() || Latest.QpcTicks == _lastQpc)
        {
            return false;
        }

        double ageMs = HeartbeatAgeMs;
        if (double.IsNaN(ageMs) || ageMs > maxAge.TotalMilliseconds)
        {
            return false;
        }

        _lastQpc = Latest.QpcTicks;
        return true;
    }

    public double HeartbeatAgeMs
    {
        get
        {
            if (_header == null || _frequency <= 0)
            {
                return double.PositiveInfinity;
            }

            long heartbeat = Volatile.Read(ref _header->HeartbeatQpc);
            return heartbeat <= 0 || !TelemetryNative.QueryPerformanceCounter(out long now)
                ? double.PositiveInfinity
                : (now - heartbeat) * 1000.0 / _frequency;
        }
    }

    private static bool ValidateHeader(in SensorHeader header) =>
        header.Magic == SensorLayout.Magic &&
        header.ProtocolVersion == SensorLayout.ProtocolVersion &&
        header.SlotStride == SensorLayout.SlotStride &&
        header.SlotCapacity == SensorLayout.SlotCapacity;

    public void Dispose()
    {
        if (_view != IntPtr.Zero)
        {
            TelemetryNative.UnmapViewOfFile(_view);
            _view = IntPtr.Zero;
        }

        if (_mapping != IntPtr.Zero)
        {
            TelemetryNative.CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
        }

        _header = null;
        _slotBase = null;
    }
}
