using System.Buffers;

namespace AzureGameAssistant.Core.Frames;

public readonly record struct FrameStatsSnapshot(
    double InstantFps,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double LastFrameTimeMs,
    int FrameCount);

/// <summary>
/// Maintains a rolling window of frame times and derives the statistics used by
/// the UI and the lag detector. Frame times are milliseconds; invalid values are
/// ignored. Methods are not thread-safe and expect a single producer.
/// </summary>
public sealed class FrameStats
{
    private const int MedianWindow = 120;
    private const double SpikeRatio = 2.5;
    private const double SpikeFloorMs = 50.0;

    private readonly double[] _times;
    private readonly double[] _median;
    private int _start;
    private int _count;
    private int _medianStart;
    private int _medianCount;
    private double _lastFrameTimeMs;
    private double _sumFrameTimeMs;

    public FrameStats(int capacity = 16_384)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _times = new double[capacity];
        _median = new double[Math.Min(MedianWindow, capacity)];
    }

    public int Count => _count;

    public double LastFrameTimeMs => _lastFrameTimeMs;

    public void Clear()
    {
        Array.Clear(_times);
        Array.Clear(_median);
        _start = 0;
        _count = 0;
        _medianStart = 0;
        _medianCount = 0;
        _lastFrameTimeMs = 0;
        _sumFrameTimeMs = 0;
    }

    /// <summary>
    /// Adds a frame time and returns true when it is a spike relative to the
    /// rolling median. Spike detection needs a full median window.
    /// </summary>
    public bool Add(double frameTimeMs)
    {
        if (!(frameTimeMs > 0) || double.IsInfinity(frameTimeMs))
        {
            return false;
        }

        _lastFrameTimeMs = frameTimeMs;

        if (_count < _times.Length)
        {
            _times[(_start + _count) % _times.Length] = frameTimeMs;
            _count++;
            _sumFrameTimeMs += frameTimeMs;
        }
        else
        {
            _sumFrameTimeMs -= _times[_start];
            _times[_start] = frameTimeMs;
            _start = (_start + 1) % _times.Length;
            _sumFrameTimeMs += frameTimeMs;
        }

        if (_medianCount < _median.Length)
        {
            _median[(_medianStart + _medianCount) % _median.Length] = frameTimeMs;
            _medianCount++;
        }
        else
        {
            _median[_medianStart] = frameTimeMs;
            _medianStart = (_medianStart + 1) % _median.Length;
        }

        if (_medianCount < _median.Length)
        {
            return false;
        }

        Span<double> scratch = stackalloc double[_median.Length];
        for (int i = 0; i < _median.Length; i++)
        {
            scratch[i] = _median[(_medianStart + i) % _median.Length];
        }

        scratch.Sort();
        double median = (scratch[(_median.Length - 1) / 2] + scratch[_median.Length / 2]) / 2.0;
        return frameTimeMs >= Math.Max(median * SpikeRatio, SpikeFloorMs);
    }

    public FrameStatsSnapshot Snapshot()
    {
        if (_count == 0)
        {
            return default;
        }

        double instant = _lastFrameTimeMs > 0 ? 1000.0 / _lastFrameTimeMs : 0;
        double average = _sumFrameTimeMs > 0 ? _count * 1000.0 / _sumFrameTimeMs : 0;

        double[] rented = ArrayPool<double>.Shared.Rent(_count);
        try
        {
            for (int i = 0; i < _count; i++)
            {
                rented[i] = _times[(_start + i) % _times.Length];
            }

            Span<double> window = rented.AsSpan(0, _count);
            window.Sort();

            double onePercent = LowFps(window, 0.01);
            double pointOnePercent = LowFps(window, 0.001);
            return new FrameStatsSnapshot(
                instant,
                average,
                onePercent,
                pointOnePercent,
                _lastFrameTimeMs,
                _count);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static double LowFps(ReadOnlySpan<double> sortedAscending, double fraction)
    {
        int slowCount = Math.Max(1, (int)Math.Ceiling(sortedAscending.Length * fraction));
        double sum = 0;
        for (int i = sortedAscending.Length - slowCount; i < sortedAscending.Length; i++)
        {
            sum += sortedAscending[i];
        }

        double average = sum / slowCount;
        return average > 0 ? 1000.0 / average : 0;
    }
}
