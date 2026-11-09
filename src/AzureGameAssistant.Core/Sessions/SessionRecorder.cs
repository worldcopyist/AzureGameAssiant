using System.Globalization;
using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Sessions;

/// <summary>
/// Turns a confirmed stutter into a bounded session file. A partial file is
/// written as soon as the event is confirmed (so the list can find it), then
/// rewritten atomically 30 seconds later with the full post-roll. Only fixed
/// windows are kept in memory; no allocation happens per frame.
/// </summary>
public sealed class SessionRecorder
{
    private static readonly long PreWindowTicks = 30 * TimeSpan.TicksPerSecond;
    private static readonly long PostWindowTicks = 30 * TimeSpan.TicksPerSecond;
    private const int FrameBatchSize = 64;
    private const double DefaultQpcTicksPerSecond = 10_000_000;

    private static double R0(double value) => SessionNumbers.Round(value, 0);

    private static double R1(double value) => SessionNumbers.Round(value, 1);

    private static double R2(double value) => SessionNumbers.Round(value, 2);

    private readonly string _directory;
    private readonly Func<uint, string> _resolveName;
    private readonly Func<string> _locale;
    private readonly Dictionary<long, PendingCapture> _pending = new();
    private readonly HashSet<long> _written = new();

    private int _maxCount;
    private int _maxTotalMb;

    public SessionRecorder(
        string directory,
        int maxCount,
        int maxTotalMb,
        Func<uint, string> resolveName,
        Func<string> locale)
    {
        _directory = directory;
        _resolveName = resolveName;
        _locale = locale;
        _maxCount = Math.Max(1, maxCount);
        _maxTotalMb = Math.Max(1, maxTotalMb);

        CleanedIncomplete = SessionStore.CleanupIncomplete(_directory);
        SessionStore.Prune(_directory, _maxCount, _maxTotalMb);
    }

    public string DirectoryPath => _directory;

    public int CleanedIncomplete { get; }

    public string? LastError { get; private set; }

    public int PendingCount => _pending.Count;

    public bool HasPending => _pending.Count > 0;

    public event EventHandler<SessionInfo>? SessionWritten;

    public void ApplyRetention(int maxCount, int maxTotalMb)
    {
        _maxCount = Math.Max(1, maxCount);
        _maxTotalMb = Math.Max(1, maxTotalMb);
        SessionStore.Prune(_directory, _maxCount, _maxTotalMb);
    }

    /// <summary>
    /// Writes the pre-roll snapshot. Returns the partial file info so callers
    /// can surface it immediately; the same event id is ignored afterwards.
    /// </summary>
    public SessionInfo? Begin(
        in StutterEvent stutter,
        SessionContext context,
        SampleRecord[] samples,
        int sampleCount,
        FrameTimePoint[] frames,
        int frameCount)
    {
        if (_pending.ContainsKey(stutter.Id) || _written.Contains(stutter.Id))
        {
            return null;
        }

        var pending = new PendingCapture(
            stutter,
            context,
            stutter.StartUtc.Ticks - PreWindowTicks,
            stutter.EndUtc.Ticks + PostWindowTicks);

        _pending[stutter.Id] = pending;
        SessionInfo? info = WriteCapture(pending, samples, sampleCount, frames, frameCount, complete: false);
        if (info is not null)
        {
            _written.Add(stutter.Id);
            SessionWritten?.Invoke(this, info);
        }
        else
        {
            _pending.Remove(stutter.Id);
        }

        return info;
    }

    /// <summary>
    /// Called after every telemetry refresh. Finalizes captures whose post-roll
    /// has elapsed using the current rings.
    /// </summary>
    public void Observe(
        DateTime utcNow,
        SampleRecord[] samples,
        int sampleCount,
        FrameTimePoint[] frames,
        int frameCount)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        long now = utcNow.Ticks;
        long[] due = _pending.Values
            .Where(capture => capture.DueTicks <= now)
            .Select(capture => capture.Event.Id)
            .ToArray();

        foreach (long id in due)
        {
            if (!_pending.TryGetValue(id, out PendingCapture? capture))
            {
                continue;
            }

            _pending.Remove(id);
            SessionInfo? info = WriteCapture(capture, samples, sampleCount, frames, frameCount, complete: true);
            if (info is not null)
            {
                SessionWritten?.Invoke(this, info);
            }
        }
    }

    /// <summary>
    /// Flushes captures that are still collecting on shutdown. The file is
    /// closed with whatever data exists and marked complete so a normal exit
    /// never leaves a "recording" file behind.
    /// </summary>
    public void FlushPending(
        DateTime utcNow,
        SampleRecord[] samples,
        int sampleCount,
        FrameTimePoint[] frames,
        int frameCount)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (PendingCapture capture in _pending.Values.ToArray())
        {
            SessionInfo? info = WriteCapture(capture, samples, sampleCount, frames, frameCount, complete: true);
            if (info is not null)
            {
                SessionWritten?.Invoke(this, info);
            }
        }

        _pending.Clear();
    }

    private SessionInfo? WriteCapture(
        PendingCapture capture,
        SampleRecord[] samples,
        int sampleCount,
        FrameTimePoint[] frames,
        int frameCount,
        bool complete)
    {
        try
        {
            long windowStart = capture.WindowStartTicks;
            long windowEnd = complete
                ? Math.Min(capture.DueTicks, LatestUtcTicks(samples, sampleCount, frames, frameCount))
                : LatestUtcTicks(samples, sampleCount, frames, frameCount);
            if (windowEnd <= windowStart)
            {
                windowEnd = windowStart + TimeSpan.TicksPerSecond;
            }

            var sampleLines = new List<SessionSampleLine>(128);
            var names = new Dictionary<uint, string>();
            var windowSamples = new List<SampleRecord>(128);
            for (int i = 0; i < sampleCount; i++)
            {
                SampleRecord sample = samples[i];
                if (sample.UtcTicks < windowStart || sample.UtcTicks > windowEnd)
                {
                    continue;
                }

                windowSamples.Add(sample);
                sampleLines.Add(ToSampleLine(sample, names));
            }

            var windowFrames = new List<FrameTimePoint>(FrameBatchSize * 4);
            for (int i = 0; i < frameCount; i++)
            {
                FrameTimePoint frame = frames[i];
                if (frame.UtcTicks < windowStart || frame.UtcTicks > windowEnd)
                {
                    continue;
                }

                windowFrames.Add(frame);
            }

            SessionHeaderLine header = BuildHeader(capture, names, windowStart, windowEnd, complete);
            SessionEventLine eventLine = BuildEvent(capture, names);
            SessionReportLine report = LocalReportBuilder.Build(
                capture.Event,
                windowSamples,
                windowFrames,
                _resolveName,
                _locale());
            List<SessionFrameBatchLine> batches = BuildFrameBatches(windowFrames, windowSamples);

            int expectedSamples = (int)Math.Max(1, ((windowEnd - windowStart) / Math.Max(1, header.SamplingMs * TimeSpan.TicksPerMillisecond)) + 1);
            int frameDropped = 0;
            foreach (FrameTimePoint frame in windowFrames)
            {
                if (frame.FrameTimeMs >= 1000)
                {
                    frameDropped++;
                }
            }

            var footer = new SessionFooterLine
            {
                Complete = complete,
                EndUtc = windowEnd,
                Samples = windowSamples.Count,
                Frames = windowFrames.Count,
                Dropped = Math.Max(0, expectedSamples - windowSamples.Count),
                FrameDropped = frameDropped,
            };

            string fileName = SessionStore.BuildFileName(capture.Event.StartUtc, capture.Event.Id);
            SessionInfo? info = SessionStore.Write(
                _directory,
                fileName,
                header,
                eventLine,
                sampleLines,
                batches,
                report,
                footer,
                out string? writeError);

            if (info is null)
            {
                LastError = $"写入会话失败：{writeError ?? "unknown"}";
                return null;
            }

            if (complete)
            {
                SessionStore.Prune(_directory, _maxCount, _maxTotalMb);
            }

            LastError = null;
            return info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            LastError = $"写入会话失败：{ex.Message}";
            return null;
        }
    }

    private SessionHeaderLine BuildHeader(
        PendingCapture capture,
        Dictionary<uint, string> names,
        long windowStart,
        long windowEnd,
        bool complete)
    {
        SessionContext context = capture.Context;
        SessionEventSummary summary = new()
        {
            Id = capture.Event.Id,
            StartUtc = capture.Event.StartUtc.Ticks,
            EndUtc = capture.Event.EndUtc.Ticks,
            DurationMs = R1(capture.Event.DurationMs),
            Score = capture.Event.Score,
            Signals = LocalReportBuilder.SignalNames(capture.Event.Signals),
            Cause = capture.Event.Cause.ToString(),
            PeakFrameTimeMs = R1(capture.Event.PeakFrameTimeMs),
            ForegroundPid = capture.Event.ForegroundPid,
            ProcessName = ResolveProcessName(capture.Event.ForegroundPid, names),
            Manual = capture.Event.Manual,
        };

        return new SessionHeaderLine
        {
            State = complete ? SessionSchema.CompleteState : SessionSchema.RecordingState,
            AppVersion = context.AppVersion,
            Os = context.Os,
            Cpu = context.Cpu,
            Gpu = context.Gpu,
            SamplingMs = context.SamplingMs,
            WindowStartUtc = windowStart,
            WindowEndUtc = windowEnd,
            PlannedEndUtc = capture.DueTicks,
            Mode = context.Mode,
            PresentMon = context.PresentMon,
            Sensors = context.Sensors,
            Elevated = context.Elevated,
            CpuTempSource = context.CpuTempSource,
            Capabilities = context.Capabilities,
            Locale = context.Locale,
            Event = summary,
        };
    }

    private SessionEventLine BuildEvent(PendingCapture capture, Dictionary<uint, string> names) => new()
    {
        Id = capture.Event.Id,
        StartUtc = capture.Event.StartUtc.Ticks,
        EndUtc = capture.Event.EndUtc.Ticks,
        DurationMs = R1(capture.Event.DurationMs),
        Score = capture.Event.Score,
        Signals = LocalReportBuilder.SignalNames(capture.Event.Signals),
        Cause = capture.Event.Cause.ToString(),
        PeakFrameTimeMs = R1(capture.Event.PeakFrameTimeMs),
        ForegroundPid = capture.Event.ForegroundPid,
        ProcessName = ResolveProcessName(capture.Event.ForegroundPid, names),
        Manual = capture.Event.Manual,
    };

    private string? ResolveProcessName(uint pid, Dictionary<uint, string> cache)
    {
        if (pid == 0)
        {
            return null;
        }

        if (!cache.TryGetValue(pid, out string? name))
        {
            name = _resolveName(pid);
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            name = name.Trim();
            cache[pid] = name;
        }

        return name.Length <= 64 ? name : name[..64];
    }

    private SessionSampleLine ToSampleLine(SampleRecord sample, Dictionary<uint, string> names)
    {
        ProcessSlot[] slots = [sample.P0, sample.P1, sample.P2, sample.P3, sample.P4];
        var processes = new List<SessionProcessLine>(5);
        foreach (ProcessSlot slot in slots)
        {
            if (slot.Pid == 0)
            {
                continue;
            }

            processes.Add(new SessionProcessLine
            {
                Pid = slot.Pid,
                Name = ResolveProcessName(slot.Pid, names) ?? string.Empty,
                StartTimeLow = slot.StartTimeLow,
                Cpu = R1(slot.CpuPct),
                Gpu = R1(slot.GpuPct),
                WorkingSetMb = slot.WorkingSetMb,
                PrivateMb = slot.PrivateMb,
                Foreground = (slot.Flags & ProcessSlotFlags.Foreground) != 0,
            });
        }

        SessionVendorLine? vendor = null;
        if ((sample.Flags & SampleFlags.VendorValid) != 0)
        {
            vendor = new SessionVendorLine
            {
                TempC = R1(sample.V0.TemperatureC),
                HotspotC = R1(sample.V0.HotSpotC),
                PowerW = R1(sample.V0.PowerW),
                CoreMhz = R0(sample.V0.CoreClockMhz),
                MemMhz = R0(sample.V0.MemoryClockMhz),
                FanPct = R0(sample.V0.FanPct),
                VramUsedMb = R0(sample.V0.VramUsedMb),
                VramTotalMb = R0(sample.V0.VramTotalMb),
            };
        }

        SessionCpuSensorLine? cpuSensor = null;
        if ((sample.Flags & SampleFlags.CpuSensorValid) != 0)
        {
            cpuSensor = new SessionCpuSensorLine
            {
                TempC = R1(sample.C0.TemperatureC),
                PowerW = R1(sample.C0.PackagePowerW),
            };
        }

        return new SessionSampleLine
        {
            Qpc = sample.QpcTicks,
            Utc = sample.UtcTicks,
            Flags = sample.Flags,
            Cpu = R1(sample.CpuUtilityPct),
            Freq = R1(sample.CpuFrequencyPct),
            MemAvailMb = sample.MemAvailableMb,
            CommitMb = sample.MemCommitMb,
            CommitLimitMb = sample.MemCommitLimitMb,
            PageFilePct = R1(sample.PageFilePct),
            DiskBusy = R1(sample.DiskBusyPct),
            DiskMs = R1(sample.DiskLatencyMs),
            Gpu = R1(sample.GpuUtilPct),
            GpuDedicatedMb = sample.GpuDedicatedMb,
            GpuSharedMb = sample.GpuSharedMb,
            JitterMs = R1(sample.TimerJitterMs),
            FgPid = sample.ForegroundPid,
            QueueLength = sample.ProcessorQueueLength,
            Procs = processes.Count > 0 ? processes.ToArray() : null,
            Vendor = vendor,
            CpuSensor = cpuSensor,
        };
    }

    private static List<SessionFrameBatchLine> BuildFrameBatches(
        List<FrameTimePoint> frames,
        List<SampleRecord> samples)
    {
        var batches = new List<SessionFrameBatchLine>();
        if (frames.Count == 0)
        {
            return batches;
        }

        double qpcPerSecond = DefaultQpcTicksPerSecond;
        long anchorQpc = 0;
        long anchorUtc = 0;
        if (samples.Count >= 2)
        {
            SampleRecord first = samples[0];
            SampleRecord last = samples[^1];
            long qpcDelta = last.QpcTicks - first.QpcTicks;
            long utcDelta = last.UtcTicks - first.UtcTicks;
            if (qpcDelta > 0 && utcDelta > 0)
            {
                qpcPerSecond = qpcDelta * (double)TimeSpan.TicksPerSecond / utcDelta;
            }
        }

        if (samples.Count > 0)
        {
            anchorQpc = samples[0].QpcTicks;
            anchorUtc = samples[0].UtcTicks;
        }

        for (int start = 0; start < frames.Count; start += FrameBatchSize)
        {
            int count = Math.Min(FrameBatchSize, frames.Count - start);
            var batch = new SessionFrameBatchLine
            {
                N = count,
                Pid = 0,
                Q = new long[count],
                U = new long[count],
                Ft = new double[count],
                Cb = new double[count],
                Cw = new double[count],
                Gl = new double[count],
                Gb = new double[count],
                Gw = new double[count],
                Dl = new double[count],
                Ae = new double[count],
                Pm = new int[count],
                Sp = new byte[count],
            };

            for (int i = 0; i < count; i++)
            {
                FrameTimePoint frame = frames[start + i];
                long qpc = anchorUtc > 0 && anchorQpc > 0
                    ? anchorQpc + (long)((frame.UtcTicks - anchorUtc) * qpcPerSecond / TimeSpan.TicksPerSecond)
                    : frame.UtcTicks;
                batch.Q[i] = qpc;
                batch.U[i] = frame.UtcTicks;
                batch.Ft[i] = R2(frame.FrameTimeMs);
                batch.Cw[i] = R2(frame.CpuWaitMs);
                batch.Gw[i] = R2(frame.GpuWaitMs);
                batch.Dl[i] = R2(frame.DisplayLatencyMs);
                batch.Sp[i] = frame.Spike ? (byte)1 : (byte)0;
            }

            batches.Add(batch);
        }

        return batches;
    }

    private static long LatestUtcTicks(
        SampleRecord[] samples,
        int sampleCount,
        FrameTimePoint[] frames,
        int frameCount)
    {
        long latest = 0;
        if (sampleCount > 0)
        {
            latest = Math.Max(latest, samples[sampleCount - 1].UtcTicks);
        }

        if (frameCount > 0)
        {
            latest = Math.Max(latest, frames[frameCount - 1].UtcTicks);
        }

        return latest > 0 ? latest : DateTime.UtcNow.Ticks;
    }

    private sealed class PendingCapture
    {
        public PendingCapture(StutterEvent stutter, SessionContext context, long windowStart, long due)
        {
            Event = stutter;
            Context = context;
            WindowStartTicks = windowStart;
            DueTicks = due;
        }

        public StutterEvent Event { get; }

        public SessionContext Context { get; }

        public long WindowStartTicks { get; }

        public long DueTicks { get; }
    }
}
