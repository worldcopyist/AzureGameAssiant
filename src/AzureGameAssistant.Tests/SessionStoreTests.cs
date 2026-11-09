using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sessions;

namespace AzureGameAssistant.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private static readonly long T0 = new DateTime(2026, 11, 10, 4, 0, 0, DateTimeKind.Utc).Ticks;
    private static readonly long Ms = TimeSpan.TicksPerMillisecond;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "aga-session-tests",
        Guid.NewGuid().ToString("N"));

    public SessionStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void RecorderWritesPartialThenCompletesAndDeduplicates()
    {
        SampleRecord[] samples = BuildSamples(T0);
        FrameTimePoint[] frames = BuildFrames(T0);
        var recorder = new SessionRecorder(_directory, 20, 500, ResolveName, () => "zh-CN");
        StutterEvent evt = new(
            7,
            new DateTime(T0, DateTimeKind.Utc),
            new DateTime(T0 + 100 * Ms, DateTimeKind.Utc),
            100,
            82,
            StutterSignals.CpuSpike | StutterSignals.FrameSpike,
            StutterCause.Cpu,
            120,
            42,
            Manual: false);

        SessionInfo? partial = recorder.Begin(evt, Context(), samples, samples.Length, frames, frames.Length);

        Assert.NotNull(partial);
        Assert.False(partial.Complete);
        Assert.Equal(1, recorder.PendingCount);
        Assert.Single(SessionStore.Enumerate(_directory));

        // The same event must not create a second file.
        Assert.Null(recorder.Begin(evt, Context(), samples, samples.Length, frames, frames.Length));

        recorder.Observe(new DateTime(T0 + 31 * TimeSpan.TicksPerSecond, DateTimeKind.Utc), samples, samples.Length, frames, frames.Length);

        Assert.Equal(0, recorder.PendingCount);
        SessionInfo info = Assert.Single(SessionStore.Enumerate(_directory));
        Assert.True(info.Complete);
        Assert.Equal(82, info.Score);
        Assert.Equal("Cpu", info.Cause);

        SessionDocument document = SessionStore.Read(info.Path);
        Assert.Equal(SessionSchema.CompleteState, document.Header.State);
        Assert.True(document.Footer?.Complete);
        Assert.True(document.Samples.Length > 0);
        Assert.True(document.Frames.Length > 0);
        Assert.Contains(document.Frames, frame => frame.Spike && frame.FrameTimeMs >= 100);
        Assert.Equal("Cpu", document.Report?.Cause);
        Assert.NotEmpty(document.Report!.Suggestions);
        Assert.Equal("game.exe", document.Event.ProcessName);
        Assert.Equal("background.exe", document.Report.Suspects[0].Name);
        Assert.Equal("game.exe", document.Names[42u]);
    }

    [Fact]
    public void NewRecorderRemovesRecordingAndTempFiles()
    {
        var recorder = new SessionRecorder(_directory, 20, 500, ResolveName, () => "zh-CN");
        StutterEvent evt = new(
            9,
            new DateTime(T0, DateTimeKind.Utc),
            new DateTime(T0 + 50 * Ms, DateTimeKind.Utc),
            50,
            40,
            StutterSignals.Manual,
            StutterCause.Manual,
            0,
            0,
            Manual: true);
        SampleRecord[] samples = BuildSamples(T0);
        Assert.NotNull(recorder.Begin(evt, Context(), samples, samples.Length, [], 0));
        File.WriteAllText(Path.Combine(_directory, "leftover.tmp"), "x");

        var restarted = new SessionRecorder(_directory, 20, 500, ResolveName, () => "zh-CN");

        Assert.Equal(2, restarted.CleanedIncomplete);
        Assert.Empty(SessionStore.Enumerate(_directory));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void PruneKeepsNewestFilesWithinBothLimits()
    {
        for (int i = 0; i < 4; i++)
        {
            string path = Path.Combine(_directory, $"20261110-00000{i}-{i}{SessionSchema.Extension}");
            File.WriteAllBytes(path, new byte[64 * 1024]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-10 + i));
        }

        SessionStore.Prune(_directory, maxCount: 2, maxTotalMb: 500);

        string[] remaining = Directory.GetFiles(_directory, "*" + SessionSchema.Extension);
        Assert.Equal(2, remaining.Length);
        Assert.Contains(remaining, path => Path.GetFileName(path).Contains("-3.", StringComparison.Ordinal));
        Assert.Contains(remaining, path => Path.GetFileName(path).Contains("-2.", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownReportStillCarriesNextSteps()
    {
        SampleRecord[] samples = BuildSamples(T0);
        StutterEvent evt = new(
            3,
            new DateTime(T0, DateTimeKind.Utc),
            new DateTime(T0, DateTimeKind.Utc),
            0,
            30,
            StutterSignals.None,
            StutterCause.Unknown,
            0,
            42,
            Manual: false);

        SessionReportLine report = LocalReportBuilder.Build(evt, samples, [], ResolveName, "zh-CN");

        Assert.Equal("Unknown", report.Cause);
        Assert.NotEmpty(report.Summary);
        Assert.InRange(report.Suggestions.Length, 1, 3);
        Assert.NotEmpty(report.Evidence);
    }

    [Fact]
    public void FrameTimelineCarriesWaitEvidence()
    {
        var timeline = new FrameTimeline(4);
        timeline.Add(T0, 16.7f, false, 1.5f, 8.2f, 12.0f);
        var buffer = new FrameTimePoint[4];

        int count = timeline.CopyTo(buffer);

        Assert.Equal(1, count);
        Assert.Equal(1.5f, buffer[0].CpuWaitMs);
        Assert.Equal(8.2f, buffer[0].GpuWaitMs);
        Assert.Equal(12.0f, buffer[0].DisplayLatencyMs);
    }

    private static SampleRecord[] BuildSamples(long centerTicks)
    {
        var samples = new SampleRecord[65];
        for (int i = 0; i < samples.Length; i++)
        {
            long utc = centerTicks + (i - 32) * TimeSpan.TicksPerSecond;
            samples[i] = new SampleRecord
            {
                QpcTicks = utc / 10,
                UtcTicks = utc,
                CpuUtilityPct = i == 32 ? 96 : 40,
                CpuFrequencyPct = 90,
                MemAvailableMb = 4096,
                MemCommitMb = 8000,
                MemCommitLimitMb = 16000,
                DiskBusyPct = 10,
                DiskLatencyMs = 2,
                GpuUtilPct = 60,
                TimerJitterMs = 1,
                ForegroundPid = 42,
                Flags = SampleFlags.ProcessValid | SampleFlags.GpuValid | SampleFlags.VendorValid,
                P0 = new ProcessSlot
                {
                    Pid = 42,
                    CpuPct = 20,
                    GpuPct = 60,
                    WorkingSetMb = 4000,
                    PrivateMb = 3000,
                    Flags = ProcessSlotFlags.Foreground,
                },
                P1 = new ProcessSlot
                {
                    Pid = 99,
                    CpuPct = i == 32 ? 95 : 10,
                    GpuPct = 1,
                    WorkingSetMb = 800,
                    PrivateMb = 700,
                },
                V0 = new VendorSlot
                {
                    TemperatureC = 65,
                    VramUsedMb = 3000,
                    VramTotalMb = 8192,
                },
            };
        }

        return samples;
    }

    private static FrameTimePoint[] BuildFrames(long centerTicks)
    {
        var frames = new FrameTimePoint[300];
        for (int i = 0; i < frames.Length; i++)
        {
            long utc = centerTicks - 30 * TimeSpan.TicksPerSecond + i * 200 * Ms;
            bool spike = i is > 120 and < 124;
            frames[i] = new FrameTimePoint(
                utc,
                spike ? 120 : 16.7f,
                spike,
                spike ? 40 : 1.2f,
                spike ? 2 : 8,
                12);
        }

        return frames;
    }

    private static SessionContext Context() => new(
        "0.5.0",
        "Windows 11",
        "Test CPU",
        "Test GPU",
        "deep",
        1000,
        "2.6.0",
        "off",
        Elevated: true,
        "driver",
        ["samples", "frames"],
        "zh-CN");

    private static string ResolveName(uint pid) => pid switch
    {
        42 => "game.exe",
        99 => "background.exe",
        _ => "unknown.exe",
    };
}
