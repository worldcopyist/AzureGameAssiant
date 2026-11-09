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
                footer);

            if (info is null)
            {
                LastError = $"写入会话失败：{fileName}";
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
            DurationMs = Math.Round(capture.Event.DurationMs, 1),
            Score = capture.Event.Score,
            Signals = LocalReportBuilder.SignalNames(capture.Event.Signals),
            Cause = capture.Event.Cause.ToString(),
            PeakFrameTimeMs = Math.Round(capture.Event.PeakFrameTimeMs, 1),
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
        DurationMs = Math.Round(capture.Event.DurationMs, 1),
        Score = capture.Event.Score,
        Signals = LocalReportBuilder.SignalNames(capture.Event.Signals),
        Cause = capture.Event.Cause.ToString(),
        PeakFrameTimeMs = Math.Round(capture.Event.PeakFrameTimeMs, 1),
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
                Cpu = Math.Round(slot.CpuPct, 1),
                Gpu = Math.Round(slot.GpuPct, 1),
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
                TempC = Math.Round(sample.V0.TemperatureC, 1),
                HotspotC = Math.Round(sample.V0.HotSpotC, 1),
                PowerW = Math.Round(sample.V0.PowerW, 1),
                CoreMhz = Math.Round(sample.V0.CoreClockMhz, 0),
                MemMhz = Math.Round(sample.V0.MemoryClockMhz, 0),
                FanPct = Math.Round(sample.V0.FanPct, 0),
                VramUsedMb = Math.Round(sample.V0.VramUsedMb, 0),
                VramTotalMb = Math.Round(sample.V0.VramTotalMb, 0),
            };
        }

        SessionCpuSensorLine? cpuSensor = null;
        if ((sample.Flags & SampleFlags.CpuSensorValid) != 0)
        {
            cpuSensor = new SessionCpuSensorLine
            {
                TempC = Math.Round(sample.C0.TemperatureC, 1),
                PowerW = Math.Round(sample.C0.PackagePowerW, 1),
            };
        }

        return new SessionSampleLine
        {
            Qpc = sample.QpcTicks,
            Utc = sample.UtcTicks,
            Flags = sample.Flags,
            Cpu = Math.Round(sample.CpuUtilityPct, 1),
            Freq = Math.Round(sample.CpuFrequencyPct, 1),
            MemAvailMb = sample.MemAvailableMb,
            CommitMb = sample.MemCommitMb,
            CommitLimitMb = sample.MemCommitLimitMb,
            PageFilePct = Math.Round(sample.PageFilePct, 1),
            DiskBusy = Math.Round(sample.DiskBusyPct, 1),
            DiskMs = Math.Round(sample.DiskLatencyMs, 1),
            Gpu = Math.Round(sample.GpuUtilPct, 1),
            GpuDedicatedMb = sample.GpuDedicatedMb,
            GpuSharedMb = sample.GpuSharedMb,
            JitterMs = Math.Round(sample.TimerJitterMs, 1),
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
                batch.Ft[i] = Math.Round(frame.FrameTimeMs, 2);
                batch.Cw[i] = Math.Round(frame.CpuWaitMs, 2);
                batch.Gw[i] = Math.Round(frame.GpuWaitMs, 2);
                batch.Dl[i] = Math.Round(frame.DisplayLatencyMs, 2);
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
