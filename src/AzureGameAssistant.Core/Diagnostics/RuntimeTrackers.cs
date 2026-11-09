using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Diagnostics;

/// <summary>
/// Groups frame-time spikes into drop episodes. An episode stays open while
/// spikes keep arriving within one second; it is stored once the stream goes
/// quiet. Fixed-size ring, no per-sample allocations.
/// </summary>
public sealed class FrameDropTracker
{
    private const int Capacity = 64;
    private const long EpisodeGapTicks = TimeSpan.TicksPerSecond;
    private const double MinEpisodeFrameMs = 50;

    private readonly FrameDropMarker[] _items = new FrameDropMarker[Capacity];
    private int _next;
    private int _count;

    private bool _open;
    private long _startTicks;
    private long _lastTicks;
    private double _startFrameMs;
    private double _maxFrameMs;
    private double _baselineFps;
    private double _cpuWaitSum;
    private double _gpuWaitSum;
    private int _frames;
    private double _cpuPct;
    private double _gpuPct;
    private double _memAvailableMb;
    private double _diskLatencyMs;
    private uint _suspectPid;

    public int Count => _count;

    public void Clear()
    {
        Array.Clear(_items);
        _next = 0;
        _count = 0;
        _open = false;
    }

    public void Observe(
        long utcTicks,
        in FrameRecord frame,
        bool spike,
        double baselineFps,
        in SampleRecord sample)
    {
        if (!spike)
        {
            Tick(utcTicks);
            return;
        }

        if (_open && utcTicks - _lastTicks > EpisodeGapTicks)
        {
            Close();
        }

        if (!_open)
        {
            _open = true;
            _startTicks = utcTicks;
            _startFrameMs = frame.FrameTimeMs;
            _maxFrameMs = frame.FrameTimeMs;
            _baselineFps = baselineFps > 0 ? baselineFps : 0;
            _cpuWaitSum = 0;
            _gpuWaitSum = 0;
            _frames = 0;
            _cpuPct = sample.CpuUtilityPct;
            _gpuPct = sample.GpuUtilPct;
            _memAvailableMb = sample.MemAvailableMb;
            _diskLatencyMs = sample.DiskLatencyMs;
            _suspectPid = DominantPid(sample);
        }

        _lastTicks = utcTicks;
        _maxFrameMs = Math.Max(_maxFrameMs, frame.FrameTimeMs);
        _cpuWaitSum += Math.Max(0, frame.CpuWaitMs);
        _gpuWaitSum += Math.Max(0, frame.GpuWaitMs);
        _frames++;
        _cpuPct = Math.Max(_cpuPct, sample.CpuUtilityPct);
        _gpuPct = Math.Max(_gpuPct, sample.GpuUtilPct);
        if (sample.MemAvailableMb > 0)
        {
            _memAvailableMb = _memAvailableMb > 0
                ? Math.Min(_memAvailableMb, sample.MemAvailableMb)
                : sample.MemAvailableMb;
        }

        _diskLatencyMs = Math.Max(_diskLatencyMs, sample.DiskLatencyMs);
    }

    public void Tick(long utcTicks)
    {
        if (_open && utcTicks - _lastTicks >= EpisodeGapTicks)
        {
            Close();
        }
    }

    public int CopyTo(Span<FrameDropMarker> destination)
    {
        int written = Math.Min(_count, destination.Length);
        int skip = _count - written;
        int start = _count == Capacity ? _next : 0;
        for (int i = 0; i < written; i++)
        {
            destination[i] = _items[(start + skip + i) % Capacity];
        }

        return written;
    }

    private void Close()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        if (!(_maxFrameMs >= MinEpisodeFrameMs))
        {
            return;
        }

        double cpuWait = _frames > 0 ? _cpuWaitSum / _frames : 0;
        double gpuWait = _frames > 0 ? _gpuWaitSum / _frames : 0;
        var marker = new FrameDropMarker(
            _startTicks,
            _startFrameMs,
            _maxFrameMs,
            _baselineFps,
            cpuWait,
            gpuWait,
            _cpuPct,
            _gpuPct,
            _memAvailableMb,
            _diskLatencyMs,
            _suspectPid,
            Classify(_maxFrameMs, cpuWait, gpuWait));

        _items[_next] = marker;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    private static string Classify(double maxFrameMs, double cpuWait, double gpuWait)
    {
        if (maxFrameMs >= 500)
        {
            return "long-stall";
        }

        if (gpuWait >= 4 && gpuWait > cpuWait * 1.5)
        {
            return "gpu-wait";
        }

        if (cpuWait >= 4 && cpuWait > gpuWait * 1.5)
        {
            return "cpu-wait";
        }

        return "mixed";
    }

    private static uint DominantPid(in SampleRecord sample)
    {
        uint foreground = sample.ForegroundPid;
        uint best = 0;
        double bestValue = 0;
        uint fallback = 0;
        double fallbackValue = 0;

        Add(sample.P0);
        Add(sample.P1);
        Add(sample.P2);
        Add(sample.P3);
        Add(sample.P4);

        return best != 0 ? best : fallback;

        void Add(in ProcessSlot slot)
        {
            if (slot.Pid == 0)
            {
                return;
            }

            double value = Math.Max(slot.CpuPct, slot.GpuPct);
            if (slot.Pid == foreground)
            {
                if (value > fallbackValue)
                {
                    fallback = slot.Pid;
                    fallbackValue = value;
                }

                return;
            }

            if (value > bestValue)
            {
                best = slot.Pid;
                bestValue = value;
            }
        }
    }
}

/// <summary>
/// Tracks sustained high load per hardware resource (CPU, GPU, VRAM, memory,
/// disk latency) and remembers which processes consumed the most of it.
/// Episodes shorter than three samples are ignored to avoid noise.
/// </summary>
public sealed class HighLoadTracker
{
    private const int Capacity = 16;
    private const int MinSamples = 3;
    private const int SlotCount = 5;
    private const double CpuThreshold = 85;
    private const double GpuThreshold = 90;
    private const double VramThreshold = 92;
    private const double MemoryCommitThreshold = 90;
    private const double MinAvailableMb = 1_024;
    private const double DiskLatencyThreshold = 50;

    private static readonly string[] Resources = ["cpu", "gpu", "vram", "memory", "disk"];

    private readonly HighLoadMarker[] _items = new HighLoadMarker[Capacity];
    private readonly Episode[] _episodes = new Episode[SlotCount];
    private int _next;
    private int _count;

    public int Count => _count;

    public void Clear()
    {
        Array.Clear(_items);
        Array.Clear(_episodes);
        _next = 0;
        _count = 0;
    }

    public void Observe(in SampleRecord sample, long utcTicks)
    {
        for (int i = 0; i < SlotCount; i++)
        {
            ref Episode episode = ref _episodes[i];
            bool high = TryMeasure(Resources[i], sample, out double value);
            if (high)
            {
                if (!episode.Active)
                {
                    episode = default;
                    episode.Active = true;
                    episode.StartTicks = utcTicks;
                    episode.Peak = value;
                    episode.Samples = 1;
                }
                else
                {
                    episode.Samples++;
                    episode.Peak = Math.Max(episode.Peak, value);
                }

                episode.LastTicks = utcTicks;
                TrackContributors(ref episode, Resources[i], sample);
            }
            else if (episode.Active)
            {
                Store(Resources[i], ref episode);
                episode = default;
            }
        }
    }

    /// <summary>
    /// Removes and returns completed episodes. Used by the diagnostics recorder
    /// so each episode becomes one event exactly once.
    /// </summary>
    public int DrainCompleted(Span<HighLoadMarker> destination)
    {
        int written = Math.Min(_count, destination.Length);
        int skip = _count - written;
        int start = _count == Capacity ? _next : 0;
        for (int i = 0; i < written; i++)
        {
            destination[i] = _items[(start + skip + i) % Capacity];
        }

        Array.Clear(_items);
        _next = 0;
        _count = 0;
        return written;
    }

    /// <summary>Drops any open episode without recording it (collector went away).</summary>
    public void Suspend()
    {
        for (int i = 0; i < SlotCount; i++)
        {
            _episodes[i] = default;
        }
    }

    public int CopyTo(Span<HighLoadMarker> destination)
    {
        int total = _count;
        for (int i = 0; i < SlotCount; i++)
        {
            if (_episodes[i].Active && _episodes[i].Samples >= MinSamples)
            {
                total++;
            }
        }

        int written = Math.Min(total, destination.Length);
        int skip = total - written;
        int index = 0;
        int start = _count == Capacity ? _next : 0;

        for (int i = 0; i < _count && index < written; i++)
        {
            if (skip > 0)
            {
                skip--;
                continue;
            }

            destination[index++] = _items[(start + i) % Capacity];
        }

        for (int i = 0; i < SlotCount && index < written; i++)
        {
            ref Episode episode = ref _episodes[i];
            if (episode.Active && episode.Samples >= MinSamples)
            {
                destination[index++] = Build(Resources[i], ref episode);
            }
        }

        return written;
    }

    private void Store(string resource, ref Episode episode)
    {
        if (episode.Samples < MinSamples)
        {
            return;
        }

        _items[_next] = Build(resource, ref episode);
        _next = (_next + 1) % Capacity;
        if (_count < Capacity)
        {
            _count++;
        }
    }

    private static HighLoadMarker Build(string resource, ref Episode episode)
    {
        double seconds = Math.Round(
            Math.Max(0, episode.LastTicks - episode.StartTicks) / (double)TimeSpan.TicksPerSecond,
            1);
        return new HighLoadMarker(
            resource,
            Math.Round(episode.Peak, 1),
            seconds,
            episode.TopPid,
            Math.Round(episode.TopValue, 1),
            episode.SecondPid,
            Math.Round(episode.SecondValue, 1));
    }

    private static void TrackContributors(ref Episode episode, string resource, in SampleRecord sample)
    {
        Add(ref episode, sample.P0, resource);
        Add(ref episode, sample.P1, resource);
        Add(ref episode, sample.P2, resource);
        Add(ref episode, sample.P3, resource);
        Add(ref episode, sample.P4, resource);
    }

    private static void Add(ref Episode episode, in ProcessSlot slot, string resource)
    {
        if (slot.Pid == 0)
        {
            return;
        }

        double value = resource switch
        {
            "gpu" or "vram" => slot.GpuPct,
            "memory" => slot.PrivateMb,
            _ => slot.CpuPct,
        };

        if (!(value > 0))
        {
            return;
        }

        if (slot.Pid == episode.TopPid)
        {
            episode.TopValue = Math.Max(episode.TopValue, value);
            return;
        }

        if (value > episode.TopValue)
        {
            episode.SecondPid = episode.TopPid;
            episode.SecondValue = episode.TopValue;
            episode.TopPid = slot.Pid;
            episode.TopValue = value;
        }
        else if (slot.Pid != episode.SecondPid && value > episode.SecondValue)
        {
            episode.SecondPid = slot.Pid;
            episode.SecondValue = value;
        }
    }

    private static bool TryMeasure(string resource, in SampleRecord sample, out double value)
    {
        switch (resource)
        {
            case "cpu":
                value = sample.CpuUtilityPct;
                return value >= CpuThreshold;

            case "gpu":
                value = sample.GpuUtilPct;
                return value >= GpuThreshold;

            case "vram":
                if (sample.V0.VramTotalMb > 0 && sample.V0.VramUsedMb > 0)
                {
                    value = Math.Round(sample.V0.VramUsedMb * 100.0 / sample.V0.VramTotalMb, 1);
                    return value >= VramThreshold;
                }

                value = 0;
                return false;

            case "memory":
                value = sample.MemCommitLimitMb > 0
                    ? Math.Round(sample.MemCommitMb * 100.0 / sample.MemCommitLimitMb, 1)
                    : 0;
                return value >= MemoryCommitThreshold ||
                       (sample.MemAvailableMb > 0 && sample.MemAvailableMb < MinAvailableMb);

            default:
                value = sample.DiskLatencyMs;
                return value >= DiskLatencyThreshold;
        }
    }

    private struct Episode
    {
        public bool Active;
        public long StartTicks;
        public long LastTicks;
        public int Samples;
        public double Peak;
        public uint TopPid;
        public double TopValue;
        public uint SecondPid;
        public double SecondValue;
    }
}
