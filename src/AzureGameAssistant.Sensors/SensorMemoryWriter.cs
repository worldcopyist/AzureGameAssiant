using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using AzureGameAssistant.Core.Sensors;

namespace AzureGameAssistant.Sensors;

/// <summary>
/// Appends one sensor slot per interval using the same even/odd seqlock the
/// telemetry map uses, so readers never observe a half-written slot.
/// </summary>
internal sealed class SensorMemoryWriter : IDisposable
{
    private static readonly long WriteSequenceOffset =
        Marshal.OffsetOf<SensorHeader>(nameof(SensorHeader.WriteSequence)).ToInt64();

    private static readonly long WriteIndexOffset =
        Marshal.OffsetOf<SensorHeader>(nameof(SensorHeader.WriteIndex)).ToInt64();

    private static readonly long HeartbeatOffset =
        Marshal.OffsetOf<SensorHeader>(nameof(SensorHeader.HeartbeatQpc)).ToInt64();

    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private long _writeIndex;
    private long _sequence;

    public SensorMemoryWriter(string mapName, int intervalMs)
    {
        _file = MemoryMappedFile.CreateOrOpen(
            mapName,
            SensorLayout.MapSize,
            MemoryMappedFileAccess.ReadWrite);

        _view = _file.CreateViewAccessor(0, SensorLayout.MapSize, MemoryMappedFileAccess.ReadWrite);

        var header = new SensorHeader
        {
            Magic = SensorLayout.Magic,
            ProtocolVersion = SensorLayout.ProtocolVersion,
            SlotStride = SensorLayout.SlotStride,
            SlotCapacity = SensorLayout.SlotCapacity,
            WriterPid = Environment.ProcessId,
            IntervalMs = intervalMs,
        };

        _view.Write(0, ref header);
    }

    public void Write(in SensorRecord record)
    {
        long slot = _writeIndex % SensorLayout.SlotCapacity;
        long slotOffset = SensorLayout.SlotOffset + (slot * SensorLayout.SlotStride);
        SensorRecord copy = record;

        _sequence++;
        _view.Write(WriteSequenceOffset, _sequence);
        _view.Write(slotOffset, ref copy);
        _view.Write(WriteIndexOffset, _writeIndex + 1);
        _view.Write(HeartbeatOffset, record.QpcTicks);
        _sequence++;
        _view.Write(WriteSequenceOffset, _sequence);

        _writeIndex++;
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}
