using System.Runtime.InteropServices;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Collector.Ipc;

internal sealed unsafe class TelemetryWriter : IDisposable
{
    private readonly FileLogger _logger;
    private IntPtr _mapping;
    private IntPtr _view;
    private SharedHeader* _header;
    private byte* _sampleBase;
    private byte* _frameBase;

    public TelemetryWriter(FileLogger logger, int samplingIntervalMs, int capabilityFlags)
    {
        _logger = logger;

        if (!TelemetryLayout.IsValid)
        {
            _logger.Error("共享内存布局校验失败，采集进程继续运行但不发布遥测");
            return;
        }

        _mapping = Kernel32.CreateFileMappingW(
            Kernel32.InvalidHandleValue,
            IntPtr.Zero,
            Kernel32.PageReadWrite,
            0,
            TelemetryLayout.MapSize,
            TelemetryLayout.MapName);

        if (_mapping == IntPtr.Zero)
        {
            _logger.Error($"创建共享内存失败：{Marshal.GetLastWin32Error()}");
            return;
        }

        _view = Kernel32.MapViewOfFile(
            _mapping,
            Kernel32.FileMapAllAccess,
            0,
            0,
            TelemetryLayout.MapSize);

        if (_view == IntPtr.Zero)
        {
            _logger.Error($"映射共享内存失败：{Marshal.GetLastWin32Error()}");
            Kernel32.CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
            return;
        }

        NativeMemory.Clear((void*)_view, TelemetryLayout.MapSize);
        _header = (SharedHeader*)_view;
        _sampleBase = (byte*)_view + TelemetryLayout.SampleRegionOffset;
        _frameBase = (byte*)_view + TelemetryLayout.FrameRegionOffset;

        *_header = new SharedHeader
        {
            Magic = TelemetryLayout.Magic,
            ProtocolVersion = TelemetryLayout.ProtocolVersion,
            SampleStride = TelemetryLayout.SampleStride,
            SampleCapacity = TelemetryLayout.SampleCapacity,
            EventCapacity = TelemetryLayout.EventCapacity,
            FrameStride = TelemetryLayout.FrameStride,
            FrameCapacity = TelemetryLayout.FrameCapacity,
            CollectorPid = Environment.ProcessId,
            SamplingIntervalMs = samplingIntervalMs,
            CapabilityFlags = capabilityFlags,
        };

        _logger.Info($"遥测共享内存已创建：{TelemetryLayout.MapName}");
    }

    public bool IsOpen => _view != IntPtr.Zero;

    public void WriteSample(in SampleRecord sample)
    {
        if (_view == IntPtr.Zero)
        {
            return;
        }

        long index = _header->WriteIndex;
        long slot = index % TelemetryLayout.SampleCapacity;
        byte* destination = _sampleBase + (slot * TelemetryLayout.SampleStride);

        Interlocked.Increment(ref _header->WriteSequence);
        *(SampleRecord*)destination = sample;
        _header->WriteIndex = index + 1;
        Interlocked.Increment(ref _header->WriteSequence);
    }

    public void WriteFrames(ReadOnlySpan<FrameRecord> frames)
    {
        if (_view == IntPtr.Zero || frames.IsEmpty)
        {
            return;
        }

        int count = Math.Min(frames.Length, TelemetryLayout.FrameCapacity);
        ReadOnlySpan<FrameRecord> source = frames[^count..];
        long index = _header->FrameWriteIndex;

        Interlocked.Increment(ref _header->FrameWriteSequence);
        for (int i = 0; i < count; i++)
        {
            long slot = (index + i) % TelemetryLayout.FrameCapacity;
            *(FrameRecord*)(_frameBase + (slot * TelemetryLayout.FrameStride)) = source[i];
        }

        _header->FrameWriteIndex = index + count;
        Interlocked.Increment(ref _header->FrameWriteSequence);
    }

    public void Heartbeat(long qpc, int modeFlags)    {
        if (_view == IntPtr.Zero)
        {
            return;
        }

        _header->HeartbeatQpc = qpc;
        _header->ModeFlags = modeFlags;
    }

    public void SetModeFlags(int flags)
    {
        if (_view != IntPtr.Zero)
        {
            _header->ModeFlags = flags;
        }
    }

    public void SetCapabilities(int flags)
    {
        if (_view != IntPtr.Zero)
        {
            _header->CapabilityFlags = flags;
        }
    }

    public void Dispose()
    {
        if (_view != IntPtr.Zero)
        {
            Kernel32.UnmapViewOfFile(_view);
            _view = IntPtr.Zero;
            _header = null;
            _sampleBase = null;
        }

        if (_mapping != IntPtr.Zero)
        {
            Kernel32.CloseHandle(_mapping);
            _mapping = IntPtr.Zero;
        }
    }
}
