using AzureGameAssistant.Core.Diagnostics;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Tests;

public sealed class LongRunRecorderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ags-diag-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void WritesSampleRowsAndSummary()
    {
        var recorder = new LongRunRecorder(_directory, 1, """{"app":"test"}""");
        DateTime start = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        SampleRecord sample = Sample(start);

        for (int i = 0; i < 5; i++)
        {
            recorder.Record(Snapshot(start.AddSeconds(i), sample, framesFresh: true, fps: 60));
        }

        recorder.Stop("test");

        string[] lines = File.ReadAllLines(Path.Combine(_directory, "samples.csv"));
        Assert.Equal(6, lines.Length);
        Assert.StartsWith("time_utc,uptime_s", lines[0]);
        Assert.Contains(",50.0,", lines[1]);

        string summary = File.ReadAllText(Path.Combine(_directory, "summary.txt"));
        Assert.Contains("CSV 行数: 5", summary);
        Assert.Contains("平均 FPS: 60.0", summary);
        Assert.True(File.Exists(Path.Combine(_directory, "events.jsonl")));
        Assert.True(File.Exists(Path.Combine(_directory, "context.json")));
    }

    [Fact]
    public void RecordsSampleGapAndCollectorRestart()
    {
        var recorder = new LongRunRecorder(_directory, 1, "{}");
        DateTime start = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        SampleRecord sample = Sample(start);

        recorder.Record(Snapshot(start, sample, framesFresh: false, fps: 0, pid: 10));
        recorder.Record(Snapshot(start.AddSeconds(12), sample, framesFresh: false, fps: 0, pid: 20));
        recorder.Stop("test");

        string events = File.ReadAllText(Path.Combine(_directory, "events.jsonl"));
        Assert.Contains("\"type\":\"sample_gap_start\"", events);
        Assert.Contains("\"type\":\"collector_pid\"", events);
        Assert.Contains("\"restart\":true", events);

        string summary = File.ReadAllText(Path.Combine(_directory, "summary.txt"));
        Assert.Contains("采集进程重启次数: 1", summary);
        Assert.Contains("数据缺口次数: 1", summary);
    }

    [Fact]
    public void WritesProcessRankingAndEscapesNames()
    {
        var recorder = new LongRunRecorder(_directory, 60, "{}");
        DateTime start = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        SampleRecord sample = Sample(start);

        recorder.Record(Snapshot(start, sample, framesFresh: false, fps: 0, processName: "a,b \"c\""));
        recorder.Stop("test");

        string[] lines = File.ReadAllLines(Path.Combine(_directory, "processes.csv"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"a,b \"\"c\"\"\"", lines[1]);
    }

    private static SampleRecord Sample(DateTime start) => new()
    {
        UtcTicks = start.Ticks,
        CpuUtilityPct = 50,
        CpuFrequencyPct = 100,
        GpuUtilPct = 60,
        GpuDedicatedMb = 5_000,
        MemAvailableMb = 4_000,
        MemCommitMb = 8_000,
        MemCommitLimitMb = 32_000,
        DiskBusyPct = 5,
        DiskLatencyMs = 1.2f,
        TimerJitterMs = 0.5f,
    };

    private static DiagnosticsSnapshot Snapshot(
        DateTime timestamp,
        SampleRecord sample,
        bool framesFresh,
        double fps,
        int pid = 1,
        string processName = "game")
    {
        var frameStats = framesFresh
            ? new FrameStatsSnapshot(fps, fps, 50, 45, 16.7, 120)
            : default;

        return new DiagnosticsSnapshot(
            timestamp,
            120,
            1_000,
            true,
            sample,
            true,
            100,
            42,
            pid,
            null,
            framesFresh ? "capturing" : "stopped",
            framesFresh ? 1234 : null,
            null,
            framesFresh,
            frameStats,
            9_000,
            5,
            true,
            55,
            65,
            true,
            47,
            5_000,
            8_000,
            new UiProcessMetrics(50, 70, 0.4, 600),
            [new ProcessDiagnostic(1234, processName, 22.5, 30.1, 3_000)]);
    }
}
