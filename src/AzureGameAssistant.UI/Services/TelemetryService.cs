using AzureGameAssistant.Core.Buffers;
using AzureGameAssistant.Core.Ipc;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Polls the collector's shared-memory ring and keeps a UI-local history window.
/// </summary>
public sealed class TelemetryService : IDisposable
{
    private const int HistoryCapacity = TelemetryLayout.SampleCapacity;
    private const int StagingCapacity = 1024;

    private readonly TelemetryReader _reader = new();
    private readonly RingBuffer<SampleRecord> _history = new(HistoryCapacity);
    private readonly SampleRecord[] _staging = new SampleRecord[StagingCapacity];
    private long _lastQpc = long.MinValue;

    public bool IsOpen => _reader.IsOpen;

    public string? LastError => _reader.LastOpenError;

    public int Count => _history.Count;

    public SampleRecord Latest { get; private set; }

    public double HeartbeatAgeMs => _reader.HeartbeatAgeMs;

    public long WriteIndex => _reader.WriteIndex;

    public long FrameWriteIndex => _reader.FrameWriteIndex;

    public int ReadFrames(long startIndex, Span<FrameRecord> destination) =>
        _reader.ReadFrames(startIndex, destination);

    public int CopyTo(Span<SampleRecord> destination) => _history.CopyTo(destination);

    public bool Refresh()
    {
        if (!_reader.IsOpen && !_reader.TryOpen())
        {
            return false;
        }

        int count = _reader.ReadSamples(_staging);
        if (count == 0)
        {
            return _reader.IsOpen;
        }

        for (int i = 0; i < count; i++)
        {
            SampleRecord sample = _staging[i];
            if (sample.QpcTicks <= _lastQpc)
            {
                continue;
            }

            _history.Write(sample);
            _lastQpc = sample.QpcTicks;
            Latest = sample;
        }

        return true;
    }

    public void Reset()
    {
        _reader.Dispose();
        _history.Clear();
        _lastQpc = long.MinValue;
    }

    public void Dispose()
    {
        _reader.Dispose();
    }
}
