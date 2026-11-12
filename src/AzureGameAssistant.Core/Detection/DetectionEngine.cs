using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Core.Detection;

/// <summary>
/// Fuses frame and resource signals into stutter events. Fixed-size rings only:
/// a 120-frame median window, 30-sample baselines for CPU/GPU/foreground CPU,
/// one open event and a small completed-event queue. Single producer, no locks.
/// </summary>
public sealed class DetectionEngine
{
    private const int BaselineWindow = 30;
    private const int MedianWindow = 120;
    private const int QueueCapacity = 8;
    private const double DiscardFrameMs = 1000.0;

    private readonly float[] _cpuBaseline = new float[BaselineWindow];
    private readonly float[] _gpuBaseline = new float[BaselineWindow];
    private readonly float[] _foregroundBaseline = new float[BaselineWindow];
    private readonly float[] _median = new float[MedianWindow];
    private readonly StutterEvent[] _queue = new StutterEvent[QueueCapacity];

    private int _cpuStart, _cpuCount;
    private int _gpuStart, _gpuCount;
    private int _foregroundStart, _foregroundCount;
    private int _medianStart, _medianCount;
    private int _queueStart, _queueCount;

    private bool _active;
    private long _eventStartTicks;
    private long _lastSignalTicks;
    private long _cooldownUntilTicks;
    private long _nextId = 1;
    private StutterSignals _signals;
    private bool _jitterConfirmed;
    private bool _frameSpikeConfirmed;
    private int _frameSpikeStreak;
    private double _peakFrameMs;
    private double _peakJitterMs;
    private double _peakCpu;
    private double _peakGpu;
    private double _peakCommitRatio;
    private double _peakDiskLatency;
    private uint _foregroundPid;
    private uint _lastForegroundPid;
    private int _diskStreak;

    public DetectionEngine(DetectionSettings settings)
    {
        Settings = settings ?? new DetectionSettings();
    }

    public DetectionSettings Settings { get; set; }

    /// <summary>
    /// True while frame data is available: a frame spike then confirms an event
    /// on its own, matching the deep-mode rule.
    /// </summary>
    public bool DeepMode { get; set; }

    public bool HasActiveEvent => _active;

    public int PendingCount => _queueCount;

    public void Reset()
    {
        Array.Clear(_cpuBaseline);
        Array.Clear(_gpuBaseline);
        Array.Clear(_foregroundBaseline);
        Array.Clear(_median);
        _cpuStart = _cpuCount = 0;
        _gpuStart = _gpuCount = 0;
        _foregroundStart = _foregroundCount = 0;
        _medianStart = _medianCount = 0;
        _queueStart = _queueCount = 0;
        _active = false;
        _eventStartTicks = 0;
        _lastSignalTicks = 0;
        _cooldownUntilTicks = 0;
        _signals = StutterSignals.None;
        _jitterConfirmed = false;
        _frameSpikeConfirmed = false;
        _frameSpikeStreak = 0;
        _peakFrameMs = 0;
        _peakJitterMs = 0;
        _peakCpu = 0;
        _peakGpu = 0;
        _peakCommitRatio = 0;
        _peakDiskLatency = 0;
        _foregroundPid = 0;
        _lastForegroundPid = 0;
        _diskStreak = 0;
    }

    public void ObserveFrame(long utcTicks, double frameTimeMs, double displayLatencyMs)
    {
        if (!Settings.Enabled || utcTicks <= 0 || !(frameTimeMs > 0) || frameTimeMs >= DiscardFrameMs)
        {
            return;
        }

        bool spike = AddFrameSample((float)frameTimeMs);
        _frameSpikeStreak = spike ? _frameSpikeStreak + 1 : 0;
        if (spike)
        {
            AddSignal(StutterSignals.FrameSpike, utcTicks);
            if (_frameSpikeStreak >= 2 || frameTimeMs >= 100)
            {
                _frameSpikeConfirmed = true;
            }
        }

        if (displayLatencyMs >= Settings.DisplayGapMs)
        {
            AddSignal(StutterSignals.DisplayGap, utcTicks);
        }

        if (_active && frameTimeMs > _peakFrameMs)
        {
            _peakFrameMs = frameTimeMs;
        }

        CloseIfQuiet(utcTicks);
    }

    public void ObserveSample(in SampleRecord sample)
    {
        if (!Settings.Enabled || sample.UtcTicks <= 0)
        {
            return;
        }

        long ticks = sample.UtcTicks;
        float cpu = sample.CpuUtilityPct;
        float gpu = (sample.Flags & SampleFlags.GpuValid) != 0 ? sample.GpuUtilPct : 0;

        float cpuBaseline = AddBaseline(_cpuBaseline, ref _cpuStart, ref _cpuCount, cpu);
        float gpuBaseline = AddBaseline(_gpuBaseline, ref _gpuStart, ref _gpuCount, gpu);

        float foregroundCpu = FindForegroundCpu(sample, out uint foregroundPid);
        if (foregroundPid != _lastForegroundPid)
        {
            // A baseline only means something per process: a new foreground app
            // starts with an empty window instead of inheriting the old one.
            _lastForegroundPid = foregroundPid;
            _foregroundStart = _foregroundCount = 0;
            Array.Clear(_foregroundBaseline);
        }

        float foregroundBaseline = foregroundCpu >= 0
            ? AddBaseline(_foregroundBaseline, ref _foregroundStart, ref _foregroundCount, foregroundCpu)
            : -1;

        if (sample.TimerJitterMs >= Settings.JitterSuspectMs)
        {
            AddSignal(StutterSignals.Jitter, ticks);
            if (sample.TimerJitterMs >= Settings.JitterConfirmMs)
            {
                _jitterConfirmed = true;
            }
        }

        bool cpuSpike = cpu >= 85 ||
            (foregroundCpu >= 0 && foregroundBaseline > 0 && foregroundCpu >= Settings.CpuSpikeRatio * foregroundBaseline && foregroundCpu >= 30);
        if (cpuSpike)
        {
            AddSignal(StutterSignals.CpuSpike, ticks);
        }

        if (gpu >= Settings.GpuSpikePct && (gpuBaseline == 0 || gpu - gpuBaseline >= 20))
        {
            AddSignal(StutterSignals.GpuSpike, ticks);
        }

        if (IsMemoryPressure(sample))
        {
            AddSignal(StutterSignals.MemoryPressure, ticks);
        }

        bool diskPressure = sample.DiskLatencyMs >= Settings.DiskLatencyMs && sample.DiskBusyPct >= 95;
        _diskStreak = diskPressure ? _diskStreak + 1 : 0;
        if (_diskStreak >= 2)
        {
            AddSignal(StutterSignals.DiskPressure, ticks);
        }

        if (_active)
        {
            if (sample.TimerJitterMs > _peakJitterMs) _peakJitterMs = sample.TimerJitterMs;
            if (cpu > _peakCpu) _peakCpu = cpu;
            if (gpu > _peakGpu) _peakGpu = gpu;
            if (sample.MemCommitLimitMb > 0)
            {
                double commitRatio = (double)sample.MemCommitMb / sample.MemCommitLimitMb;
                if (commitRatio > _peakCommitRatio) _peakCommitRatio = commitRatio;
            }

            if (sample.DiskLatencyMs > _peakDiskLatency) _peakDiskLatency = sample.DiskLatencyMs;
            _foregroundPid = foregroundPid != 0 ? foregroundPid : sample.ForegroundPid;
        }
        else if (_cpuCount > 0)
        {
            _foregroundPid = sample.ForegroundPid;
        }

        CloseIfQuiet(ticks);
    }

    /// <summary>
    /// Drops an open event when automatic detection loses its context (the
    /// user left the game). Without this the next sample would close a stale
    /// event with an inflated duration; manual marks are unaffected.
    /// </summary>
    public void Suspend()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        ResetEventState();
    }

    public bool TryDequeue(out StutterEvent stutterEvent)    {
        if (_queueCount == 0)
        {
            stutterEvent = default;
            return false;
        }

        stutterEvent = _queue[_queueStart];
        _queueStart = (_queueStart + 1) % QueueCapacity;
        _queueCount--;
        return true;
    }

    /// <summary>
    /// Hotkey marks always produce an event with score 100, even while an
    /// automatic event is open or cooling down.
    /// </summary>
    public StutterEvent MarkManual(long utcTicks)
    {
        DateTime now = new(utcTicks, DateTimeKind.Utc);
        DateTime start = _active ? new DateTime(_eventStartTicks, DateTimeKind.Utc) : now;
        double peakFrameMs = _peakFrameMs;
        uint foregroundPid = _foregroundPid;
        _active = false;
        ResetEventState();
        _cooldownUntilTicks = utcTicks + Settings.CooldownMs * TimeSpan.TicksPerMillisecond;

        var manual = new StutterEvent(
            _nextId++,
            start,
            now,
            Math.Max(0, (now - start).TotalMilliseconds),
            100,
            StutterSignals.Manual,
            StutterCause.Manual,
            peakFrameMs,
            foregroundPid,
            true);

        Enqueue(manual);
        return manual;
    }

    private void AddSignal(StutterSignals signal, long utcTicks)
    {
        if (utcTicks < _cooldownUntilTicks && !_active)
        {
            return;
        }

        if (!_active)
        {
            _active = true;
            _eventStartTicks = utcTicks;
            _signals = StutterSignals.None;
            _jitterConfirmed = false;
            _frameSpikeConfirmed = false;
            _peakFrameMs = 0;
            _peakJitterMs = 0;
            _peakCpu = 0;
            _peakGpu = 0;
            _peakCommitRatio = 0;
            _peakDiskLatency = 0;
        }

        _signals |= signal;
        _lastSignalTicks = utcTicks;
    }

    private void CloseIfQuiet(long utcTicks)
    {
        if (!_active || utcTicks - _lastSignalTicks < Settings.QuietMs * TimeSpan.TicksPerMillisecond)
        {
            return;
        }

        _active = false;
        _cooldownUntilTicks = utcTicks + Settings.CooldownMs * TimeSpan.TicksPerMillisecond;

        int score = ComputeScore(_signals);
        if (!IsConfirmed(_signals, score))
        {
            ResetEventState();
            return;
        }

        DateTime start = new(_eventStartTicks, DateTimeKind.Utc);
        DateTime end = new(_lastSignalTicks, DateTimeKind.Utc);
        var stutterEvent = new StutterEvent(
            _nextId++,
            start,
            end,
            Math.Max(0, (end - start).TotalMilliseconds),
            score,
            _signals,
            Classify(_signals),
            _peakFrameMs,
            _foregroundPid,
            false);

        ResetEventState();
        Enqueue(stutterEvent);
    }

    private void ResetEventState()
    {
        _signals = StutterSignals.None;
        _jitterConfirmed = false;
        _frameSpikeConfirmed = false;
        _peakFrameMs = 0;
        _peakJitterMs = 0;
        _peakCpu = 0;
        _peakGpu = 0;
        _peakCommitRatio = 0;
        _peakDiskLatency = 0;
    }

    private void Enqueue(StutterEvent stutterEvent)
    {
        if (_queueCount == QueueCapacity)
        {
            _queueStart = (_queueStart + 1) % QueueCapacity;
            _queueCount--;
        }

        _queue[(_queueStart + _queueCount) % QueueCapacity] = stutterEvent;
        _queueCount++;
    }

    private bool IsConfirmed(StutterSignals signals, int score)
    {
        if ((signals & StutterSignals.Manual) != 0)
        {
            return true;
        }

        if (DeepMode && _frameSpikeConfirmed)
        {
            return true;
        }

        if (_jitterConfirmed || score >= Settings.ConfirmScore)
        {
            return true;
        }

        int resources = 0;
        if ((signals & StutterSignals.CpuSpike) != 0) resources++;
        if ((signals & StutterSignals.GpuSpike) != 0) resources++;
        if ((signals & StutterSignals.MemoryPressure) != 0) resources++;
        if ((signals & StutterSignals.DiskPressure) != 0) resources++;
        return resources >= 2;
    }

    private int ComputeScore(StutterSignals signals)
    {
        if ((signals & StutterSignals.Manual) != 0)
        {
            return 100;
        }

        // Matches the documented formula: the strongest single metric plus a
        // bonus when several signals corroborate. Shallow mode has no frame
        // data, so frame time never contributes there.
        int strongest = 0;
        if (DeepMode)
        {
            strongest = Math.Max(strongest, ScaleScore(_peakFrameMs, 50, 150));
        }

        if ((signals & StutterSignals.Jitter) != 0)
        {
            strongest = Math.Max(strongest, ScaleScore(_peakJitterMs, 50, 150));
        }

        if ((signals & StutterSignals.CpuSpike) != 0)
        {
            strongest = Math.Max(strongest, ScaleScore(_peakCpu, 80, 20));
        }

        if ((signals & StutterSignals.GpuSpike) != 0)
        {
            strongest = Math.Max(strongest, ScaleScore(_peakGpu, 85, 15));
        }

        if ((signals & StutterSignals.MemoryPressure) != 0)
        {
            strongest = Math.Max(strongest, ScaleScore(_peakCommitRatio, 0.90, 0.10));
        }

        if ((signals & StutterSignals.DiskPressure) != 0)
        {
            strongest = Math.Max(strongest, ScaleScore(_peakDiskLatency, 50, 100));
        }

        int signalsHit = CountSignals(signals);
        return Math.Min(100, strongest + (signalsHit >= 2 ? 10 : 0));
    }

    private static int ScaleScore(double value, double floor, double span) =>
        (int)Math.Round(Math.Clamp((value - floor) / span * 100, 0, 100));

    private static int CountSignals(StutterSignals signals)
    {
        int count = 0;
        if ((signals & StutterSignals.FrameSpike) != 0) count++;
        if ((signals & StutterSignals.DisplayGap) != 0) count++;
        if ((signals & StutterSignals.Jitter) != 0) count++;
        if ((signals & StutterSignals.CpuSpike) != 0) count++;
        if ((signals & StutterSignals.GpuSpike) != 0) count++;
        if ((signals & StutterSignals.MemoryPressure) != 0) count++;
        if ((signals & StutterSignals.DiskPressure) != 0) count++;
        return count;
    }

    private static StutterCause Classify(StutterSignals signals)
    {
        if ((signals & StutterSignals.Manual) != 0) return StutterCause.Manual;

        bool cpu = (signals & StutterSignals.CpuSpike) != 0;
        bool gpu = (signals & StutterSignals.GpuSpike) != 0;
        bool memory = (signals & StutterSignals.MemoryPressure) != 0;
        bool disk = (signals & StutterSignals.DiskPressure) != 0;
        bool frame = (signals & (StutterSignals.FrameSpike | StutterSignals.DisplayGap)) != 0;

        if (cpu && gpu) return StutterCause.Unknown;
        if (gpu) return StutterCause.Gpu;
        if (cpu) return StutterCause.Cpu;
        if (memory) return StutterCause.Memory;
        if (disk) return StutterCause.Disk;
        if (frame) return StutterCause.FramePacing;
        return StutterCause.Unknown;
    }

    private bool AddFrameSample(float frameTimeMs)
    {
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

        Span<float> scratch = stackalloc float[_median.Length];
        for (int i = 0; i < _median.Length; i++)
        {
            scratch[i] = _median[(_medianStart + i) % _median.Length];
        }

        scratch.Sort();
        float median = (scratch[(_median.Length - 1) / 2] + scratch[_median.Length / 2]) / 2f;
        return frameTimeMs >= Math.Max(median * Settings.FrameSpikeRatio, Settings.FrameSpikeFloorMs);
    }

    private static float AddBaseline(float[] ring, ref int start, ref int count, float value)
    {
        if (count < ring.Length)
        {
            ring[(start + count) % ring.Length] = value;
            count++;
        }
        else
        {
            ring[start] = value;
            start = (start + 1) % ring.Length;
        }

        Span<float> scratch = stackalloc float[BaselineWindow];
        int n = 0;
        for (int i = 0; i < count; i++)
        {
            scratch[n++] = ring[(start + i) % ring.Length];
        }

        scratch[..n].Sort();
        return n == 0 ? 0 : scratch[n / 2];
    }

    private static float FindForegroundCpu(in SampleRecord sample, out uint pid)
    {
        ProcessSlot[] slots = [sample.P0, sample.P1, sample.P2, sample.P3, sample.P4];
        pid = 0;
        for (int i = 0; i < slots.Length; i++)
        {
            ProcessSlot slot = slots[i];
            if (slot.Pid == 0)
            {
                continue;
            }

            if ((slot.Flags & ProcessSlotFlags.Foreground) != 0 || slot.Pid == sample.ForegroundPid)
            {
                pid = slot.Pid;
                return slot.CpuPct;
            }
        }

        return -1;
    }

    private bool IsMemoryPressure(in SampleRecord sample)
    {
        double floorMb = Math.Max(0, Settings.MemAvailableFloorMb);
        if (SystemMemory.TryGetUsage(sample, out _, out double totalMb) && totalMb > 0)
        {
            floorMb = Math.Max(floorMb, totalMb * 0.08);
        }

        if (sample.MemAvailableMb > 0 && sample.MemAvailableMb < floorMb)
        {
            return true;
        }

        return sample.MemCommitLimitMb > 0 &&
               (double)sample.MemCommitMb / sample.MemCommitLimitMb > Settings.CommitRatio;
    }
}
