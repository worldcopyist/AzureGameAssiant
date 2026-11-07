using System.Runtime.InteropServices;
using System.Threading;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Ipc;

/// <summary>
/// Read-only view of the collector's shared-memory telemetry map. All reads are
/// guarded by the writer's even/odd sequence counter, so a sample is either a
/// complete record or discarded.
/// </summary>
public sealed unsafe class TelemetryReader : IDisposable
{
    private const int ReadRetryLimit = 8;

    private IntPtr _mapping;
    private IntPtr _view;
    private SharedHeader* _header;
    private byte* _sampleBase;
    private byte* _frameBase;
    private long _frequency;

    public bool IsOpen => _view != IntPtr.Zero;

    public string? LastOpenError { get; private set; }

    public bool TryOpen()
    {
        Dispose();
        LastOpenError = null;

        if (!TelemetryLayout.IsValid)
        {
            LastOpenError = "遥测布局校验失败";
            return false;
        }

        if (_frequency == 0 && !TelemetryNative.QueryPerformanceFrequency(out _frequency))
        {
            _frequency = 10_000_000;
        }

        _mapping = TelemetryNative.OpenFileMappingW(
            TelemetryNative.FileMapRead,
            false,
            TelemetryLayout.MapName);

        if (_mapping == IntPtr.Zero)
        {
            LastOpenError = "遥测共享内存尚未创建";
            return false;
        }

        _view = TelemetryNative.MapViewOfFile(
            _mapping,
            TelemetryNative.FileMapRead,
            0,
            0,
            TelemetryLayout.MapSize);

        if (_view == IntPtr.Zero)
        {
            LastOpenError = "映射遥测共享内存失败";
            Dispose();
            return false;
        }

        _header = (SharedHeader*)_view;
        _sampleBase = (byte*)_view + TelemetryLayout.SampleRegionOffset;
        _frameBase = (byte*)_view + TelemetryLayout.FrameRegionOffset;

        if (!TryReadHeader(out SharedHeader header) || !ValidateHeader(header))
        {
            LastOpenError = "遥测头信息无效（采集进程可能正在启动或重启）";
            Dispose();
            return false;
        }

        return true;
    }

    public bool TryReadHeader(out SharedHeader header)
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

            SharedHeader copy = *_header;
            long after = Volatile.Read(ref _header->WriteSequence);

            if (before == after)
            {
                header = copy;
                return true;
            }
        }

        return false;
    }

    public bool TryReadLatest(out SampleRecord sample)
    {
        sample = default;

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

            long slot = (writeIndex - 1) % TelemetryLayout.SampleCapacity;
            SampleRecord copy = *(SampleRecord*)(_sampleBase + (slot * TelemetryLayout.SampleStride));

            long after = Volatile.Read(ref _header->WriteSequence);
            if (before == after && Volatile.Read(ref _header->WriteIndex) == writeIndex)
            {
                sample = copy;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies the newest samples into <paramref name="destination"/>, oldest first.
    /// Returns the number of samples written, or 0 when the map is unavailable or
    /// no consistent snapshot could be taken.
    /// </summary>
    public int ReadSamples(Span<SampleRecord> destination)
    {
        if (_header == null || destination.IsEmpty)
        {
            return 0;
        }

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            long before = Volatile.Read(ref _header->WriteSequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(128);
                continue;
            }

            long writeIndex = Volatile.Read(ref _header->WriteIndex);
            int available = (int)Math.Min(writeIndex, TelemetryLayout.SampleCapacity);
            int count = Math.Min(available, destination.Length);

            for (int i = 0; i < count; i++)
            {
                long sampleIndex = writeIndex - count + i;
                long slot = sampleIndex % TelemetryLayout.SampleCapacity;
                var source = new ReadOnlySpan<byte>(
                    _sampleBase + (slot * TelemetryLayout.SampleStride),
                    TelemetryLayout.SampleStride);

                destination[i] = MemoryMarshal.Read<SampleRecord>(source);
            }

            long after = Volatile.Read(ref _header->WriteSequence);
            if (before == after && writeIndex == Volatile.Read(ref _header->WriteIndex))
            {
                return count;
            }
        }

        return 0;
    }

    public bool TryReadLatestFrame(out FrameRecord frame)
    {
        frame = default;

        if (_header == null)
        {
            return false;
        }

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            long before = Volatile.Read(ref _header->FrameWriteSequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(64);
                continue;
            }

            long writeIndex = Volatile.Read(ref _header->FrameWriteIndex);
            if (writeIndex <= 0)
            {
                return false;
            }

            long slot = (writeIndex - 1) % TelemetryLayout.FrameCapacity;
            FrameRecord copy = *(FrameRecord*)(_frameBase + (slot * TelemetryLayout.FrameStride));

            long after = Volatile.Read(ref _header->FrameWriteSequence);
            if (before == after && Volatile.Read(ref _header->FrameWriteIndex) == writeIndex)
            {
                frame = copy;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Copies the newest frames into <paramref name="destination"/>, oldest
    /// first, using the frame region's own seqlock.
    /// </summary>
    public int ReadFrames(Span<FrameRecord> destination)
    {
        if (_header == null || destination.IsEmpty)
        {
            return 0;
        }

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            long before = Volatile.Read(ref _header->FrameWriteSequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(128);
                continue;
            }

            long writeIndex = Volatile.Read(ref _header->FrameWriteIndex);
            int available = (int)Math.Min(writeIndex, TelemetryLayout.FrameCapacity);
            int count = Math.Min(available, destination.Length);

            for (int i = 0; i < count; i++)
            {
                long frameIndex = writeIndex - count + i;
                long slot = frameIndex % TelemetryLayout.FrameCapacity;
                var source = new ReadOnlySpan<byte>(
                    _frameBase + (slot * TelemetryLayout.FrameStride),
                    TelemetryLayout.FrameStride);

                destination[i] = MemoryMarshal.Read<FrameRecord>(source);
            }

            long after = Volatile.Read(ref _header->FrameWriteSequence);
            if (before == after && writeIndex == Volatile.Read(ref _header->FrameWriteIndex))
            {
                return count;
            }
        }

        return 0;
    }

    /// <summary>
    /// Copies frames starting at <paramref name="startIndex"/> (oldest first).
    /// Indices older than the ring's retained window are advanced to the oldest
    /// available frame. Returns 0 when no new frame is available.
    /// </summary>
    public int ReadFrames(long startIndex, Span<FrameRecord> destination)
    {
        if (_header == null || destination.IsEmpty)
        {
            return 0;
        }

        for (int attempt = 0; attempt < ReadRetryLimit; attempt++)
        {
            long before = Volatile.Read(ref _header->FrameWriteSequence);
            if ((before & 1) != 0)
            {
                Thread.SpinWait(128);
                continue;
            }

            long writeIndex = Volatile.Read(ref _header->FrameWriteIndex);
            long oldest = Math.Max(0, writeIndex - TelemetryLayout.FrameCapacity);
            long from = Math.Max(startIndex, oldest);
            if (from >= writeIndex)
            {
                return 0;
            }

            int count = (int)Math.Min(writeIndex - from, destination.Length);
            for (int i = 0; i < count; i++)
            {
                long slot = (from + i) % TelemetryLayout.FrameCapacity;
                var source = new ReadOnlySpan<byte>(
                    _frameBase + (slot * TelemetryLayout.FrameStride),
                    TelemetryLayout.FrameStride);

                destination[i] = MemoryMarshal.Read<FrameRecord>(source);
            }

            long after = Volatile.Read(ref _header->FrameWriteSequence);
            if (before == after && writeIndex == Volatile.Read(ref _header->FrameWriteIndex))
            {
                return count;
            }
        }

        return 0;
    }

    public long WriteIndex
    {
        get
        {
            if (_header == null)
            {
                return 0;
            }

            return Volatile.Read(ref _header->WriteIndex);
        }
    }

    public long FrameWriteIndex
    {
        get
        {
            if (_header == null)
            {
                return 0;
            }

            return Volatile.Read(ref _header->FrameWriteIndex);
        }
    }

    /// <summary>
    /// Milliseconds since the collector last updated its heartbeat, or
    /// <see cref="double.PositiveInfinity"/> when no heartbeat was ever written.
    /// </summary>
    public double HeartbeatAgeMs
    {
        get
        {
            if (_header == null || _frequency <= 0)
            {
                return double.PositiveInfinity;
            }

            long heartbeat = Volatile.Read(ref _header->HeartbeatQpc);
            if (heartbeat <= 0 || !TelemetryNative.QueryPerformanceCounter(out long now))
            {
                return double.PositiveInfinity;
            }

            return (now - heartbeat) * 1000.0 / _frequency;
        }
    }

    public bool IsHeaderValid => TryReadHeader(out SharedHeader header) && ValidateHeader(header);

    private static bool ValidateHeader(in SharedHeader header) =>
        header.Magic == TelemetryLayout.Magic &&
        header.ProtocolVersion == TelemetryLayout.ProtocolVersion &&
        header.SampleStride == TelemetryLayout.SampleStride &&
        header.SampleCapacity == TelemetryLayout.SampleCapacity &&
        header.FrameStride == TelemetryLayout.FrameStride &&
        header.FrameCapacity == TelemetryLayout.FrameCapacity;

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
        _sampleBase = null;
        _frameBase = null;
    }
}
