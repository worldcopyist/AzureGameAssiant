namespace AzureGameAssistant.Core.Frames;

public readonly record struct FrameTimePoint(
    long UtcTicks,
    float FrameTimeMs,
    bool Spike,
    float CpuWaitMs = 0,
    float GpuWaitMs = 0,
    float DisplayLatencyMs = 0);

/// <summary>
/// Fixed-capacity ring of frame times aligned to the wall clock, used by the
/// main-window frame chart. Frames arrive faster than samples, so the UI keeps
/// its own bounded history instead of retaining the whole shared-memory ring.
/// </summary>
public sealed class FrameTimeline
{
    private readonly FrameTimePoint[] _points;
    private int _start;
    private int _count;

    public FrameTimeline(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _points = new FrameTimePoint[capacity];
    }

    public int Capacity => _points.Length;

    public int Count => _count;

    public void Clear()
    {
        _start = 0;
        _count = 0;
    }

    public void Add(
        long utcTicks,
        float frameTimeMs,
        bool spike,
        float cpuWaitMs = 0,
        float gpuWaitMs = 0,
        float displayLatencyMs = 0)
    {
        var point = new FrameTimePoint(utcTicks, frameTimeMs, spike, cpuWaitMs, gpuWaitMs, displayLatencyMs);
        if (_count < _points.Length)
        {
            _points[(_start + _count) % _points.Length] = point;
            _count++;
        }
        else
        {
            _points[_start] = point;
            _start = (_start + 1) % _points.Length;
        }
    }

    public int CopyTo(Span<FrameTimePoint> destination)
    {
        int copied = Math.Min(_count, destination.Length);
        int skip = _count - copied;
        for (int i = 0; i < copied; i++)
        {
            destination[i] = _points[(_start + skip + i) % _points.Length];
        }

        return copied;
    }

    /// <summary>
    /// Maps a QPC timestamp onto the sample ring's wall clock using a nearby
    /// sample as the anchor and the locally measured QPC frequency.
    /// </summary>
    public static long ToUtcTicks(long frameQpc, long anchorQpc, long anchorUtcTicks, double qpcTicksPerSecond)
    {
        if (qpcTicksPerSecond <= 0 || anchorUtcTicks <= 0)
        {
            return anchorUtcTicks;
        }

        double offsetTicks = (frameQpc - anchorQpc) * (TimeSpan.TicksPerSecond / qpcTicksPerSecond);
        return anchorUtcTicks + (long)offsetTicks;
    }
}
