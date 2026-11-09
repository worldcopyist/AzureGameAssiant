using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Tests;

public sealed class DetectionEngineTests
{
    private static readonly long T0 = new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long Ms = TimeSpan.TicksPerMillisecond;

    private static DetectionEngine Create(bool deep = true)
    {
        return new DetectionEngine(new DetectionSettings()) { DeepMode = deep };
    }

    [Fact]
    public void FrameSpikeConfirmsEventInDeepMode()
    {
        DetectionEngine engine = Create();
        FeedFrames(engine, T0, 120, 16.7);

        long spikeAt = T0 + (long)(120 * 16.7 * Ms);
        engine.ObserveFrame(spikeAt, 100, 0);
        FeedFrames(engine, spikeAt + 2_100 * Ms, 30, 16.7);

        Assert.True(engine.TryDequeue(out StutterEvent evt));
        Assert.Equal(StutterSignals.FrameSpike, evt.Signals);
        Assert.Equal(StutterCause.FramePacing, evt.Cause);
        Assert.Equal(100.0, evt.PeakFrameTimeMs, 3);
        Assert.Equal(33, evt.Score);
        Assert.Equal(spikeAt, evt.StartUtc.Ticks);
        Assert.Equal(evt.StartUtc, evt.EndUtc);
        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void SingleModerateFrameSpikeStaysSuspected()
    {
        DetectionEngine engine = Create();
        FeedFrames(engine, T0, 120, 16.7);

        long spikeAt = T0 + (long)(120 * 16.7 * Ms);
        engine.ObserveFrame(spikeAt, 60, 0);
        FeedFrames(engine, spikeAt + 2_100 * Ms, 30, 16.7);

        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void TwoConsecutiveFrameSpikesConfirmInDeepMode()
    {
        DetectionEngine engine = Create();
        FeedFrames(engine, T0, 120, 16.7);

        long first = T0 + (long)(120 * 16.7 * Ms);
        engine.ObserveFrame(first, 60, 0);
        engine.ObserveFrame(first + 60 * Ms, 60, 0);
        FeedFrames(engine, first + 2_100 * Ms, 30, 16.7);

        Assert.True(engine.TryDequeue(out StutterEvent evt));
        Assert.True((evt.Signals & StutterSignals.FrameSpike) != 0);
        Assert.Equal(StutterCause.FramePacing, evt.Cause);
    }

    [Fact]
    public void FrameSpikeDoesNotConfirmInShallowMode()
    {
        DetectionEngine engine = Create(deep: false);
        FeedFrames(engine, T0, 120, 16.7);

        long spikeAt = T0 + (long)(120 * 16.7 * Ms);
        engine.ObserveFrame(spikeAt, 100, 0);
        FeedFrames(engine, spikeAt + 2_100 * Ms, 30, 16.7);

        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void SingleSuspectJitterDoesNotConfirm()
    {
        DetectionEngine engine = Create(deep: false);
        engine.ObserveSample(Sample(T0, jitter: 60));
        engine.ObserveSample(Sample(T0 + 3_000 * Ms, jitter: 0));

        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void ConfirmedJitterCreatesEvent()
    {
        DetectionEngine engine = Create(deep: false);
        engine.ObserveSample(Sample(T0, jitter: 120));
        engine.ObserveSample(Sample(T0 + 3_000 * Ms, jitter: 0));

        Assert.True(engine.TryDequeue(out StutterEvent evt));
        Assert.True((evt.Signals & StutterSignals.Jitter) != 0);
        Assert.Equal(47, evt.Score);
    }

    [Fact]
    public void TwoResourceSignalsConfirmTogether()
    {
        DetectionEngine engine = Create(deep: false);
        engine.ObserveSample(Sample(T0, cpu: 92, availableMb: 500));
        engine.ObserveSample(Sample(T0 + 3_000 * Ms, cpu: 10, availableMb: 8_000));

        Assert.True(engine.TryDequeue(out StutterEvent evt));
        Assert.True((evt.Signals & StutterSignals.CpuSpike) != 0);
        Assert.True((evt.Signals & StutterSignals.MemoryPressure) != 0);
        Assert.Equal(StutterCause.Cpu, evt.Cause);
        Assert.Equal(70, evt.Score);
    }

    [Fact]
    public void DiskPressureNeedsTwoSamples()
    {
        DetectionEngine engine = Create(deep: false);
        engine.ObserveSample(Sample(T0, cpu: 92, diskLatency: 60, diskBusy: 96));
        engine.ObserveSample(Sample(T0 + 1_000 * Ms, cpu: 92, diskLatency: 60, diskBusy: 96));
        engine.ObserveSample(Sample(T0 + 4_000 * Ms, cpu: 10, diskLatency: 1, diskBusy: 5));

        // CPU alone scores 60; the second signal adds the corroboration bonus.
        Assert.True(engine.TryDequeue(out StutterEvent evt));
        Assert.True((evt.Signals & StutterSignals.DiskPressure) != 0);
        Assert.Equal(70, evt.Score);
    }

    [Fact]
    public void SignalsWithinQuietWindowMergeIntoOneEvent()
    {
        DetectionEngine engine = Create();
        FeedFrames(engine, T0, 120, 16.7);
        long first = T0 + (long)(120 * 16.7 * Ms);
        engine.ObserveFrame(first, 100, 0);
        FeedFrames(engine, first + 500 * Ms, 10, 16.7);
        long second = first + (long)(10 * 16.7 * Ms + 100 * Ms);
        engine.ObserveFrame(second, 120, 0);
        FeedFrames(engine, second + 2_100 * Ms, 30, 16.7);

        Assert.True(engine.TryDequeue(out StutterEvent evt));
        Assert.Equal(120.0, evt.PeakFrameTimeMs, 3);
        Assert.True(evt.DurationMs >= 100);
        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void NewEventIsSuppressedDuringCooldown()
    {
        DetectionEngine engine = Create();
        FeedFrames(engine, T0, 120, 16.7);
        long first = T0 + (long)(120 * 16.7 * Ms);
        engine.ObserveFrame(first, 100, 0);
        long quiet = first + 2_100 * Ms;
        FeedFrames(engine, quiet, 30, 16.7);
        Assert.True(engine.TryDequeue(out _));

        engine.ObserveFrame(quiet + 1_000 * Ms, 100, 0);
        FeedFrames(engine, quiet + 3_200 * Ms, 60, 16.7);

        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void ManualMarkAlwaysProducesScore100()
    {
        DetectionEngine engine = Create(deep: false);
        StutterEvent evt = engine.MarkManual(T0);

        Assert.Equal(100, evt.Score);
        Assert.Equal(StutterCause.Manual, evt.Cause);
        Assert.True(evt.Manual);
        Assert.Equal(StutterSignals.Manual, evt.Signals);
        Assert.True(engine.TryDequeue(out StutterEvent queued));
        Assert.Equal(evt.Id, queued.Id);
    }

    [Fact]
    public void DiscontinuityFramesAreIgnored()
    {
        DetectionEngine engine = Create();
        FeedFrames(engine, T0, 120, 16.7);
        engine.ObserveFrame(T0 + 5_000 * Ms, 19_278, 0);
        FeedFrames(engine, T0 + 7_200 * Ms, 30, 16.7);
        engine.ObserveFrame(T0 + 9_000 * Ms, 1_529, 0);

        Assert.False(engine.TryDequeue(out _));
    }

    [Fact]
    public void DisabledEngineIgnoresEverything()
    {
        DetectionEngine engine = new(new DetectionSettings { Enabled = false }) { DeepMode = true };
        FeedFrames(engine, T0, 120, 16.7);
        engine.ObserveFrame(T0 + 5_000 * Ms, 100, 0);
        FeedFrames(engine, T0 + 7_200 * Ms, 30, 16.7);

        Assert.False(engine.TryDequeue(out _));
    }

    private static void FeedFrames(DetectionEngine engine, long startTicks, int count, double frameMs)
    {
        long step = (long)(frameMs * Ms);
        for (int i = 0; i < count; i++)
        {
            engine.ObserveFrame(startTicks + (i * step), frameMs, 0);
        }
    }

    private static SampleRecord Sample(
        long utcTicks,
        float cpu = 10,
        float jitter = 0,
        uint availableMb = 8_000,
        float diskLatency = 1,
        float diskBusy = 5)
    {
        return new SampleRecord
        {
            UtcTicks = utcTicks,
            CpuUtilityPct = cpu,
            TimerJitterMs = jitter,
            MemAvailableMb = availableMb,
            MemCommitMb = 8_000,
            MemCommitLimitMb = 32_000,
            DiskLatencyMs = diskLatency,
            DiskBusyPct = diskBusy,
            Flags = SampleFlags.ProcessValid,
            ForegroundPid = 4242,
            P0 = new ProcessSlot
            {
                Pid = 4242,
                CpuPct = 12,
                Flags = ProcessSlotFlags.Foreground,
            },
        };
    }
}
