using AzureGameAssistant.Ai;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sessions;

namespace AzureGameAssistant.Tests;

public sealed class AiReportStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "ags-ai-" + Guid.NewGuid().ToString("N"));

    public AiReportStoreTests()
    {
        Directory.CreateDirectory(_directory);
    }

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
    public void ReportIsStoredNextToTheSession()
    {
        string sessionPath = Path.Combine(_directory, "20261112-120000-12.agsession.jsonl.gz");

        Assert.True(AiReportStore.Write(sessionPath, new AiReport
        {
            Summary = "GPU 受限",
            PrimaryCause = "GpuBound",
            Confidence = "high",
        }));

        string expected = sessionPath + ".ai.json";
        Assert.True(File.Exists(expected));
        AiReport? report = AiReportStore.Read(sessionPath);
        Assert.NotNull(report);
        Assert.Equal("GPU 受限", report!.Summary);
        Assert.Equal("GpuBound", report.PrimaryCause);
    }

    [Fact]
    public void MissingSessionHasNoReport()
    {
        string sessionPath = Path.Combine(_directory, "missing.agsession.jsonl.gz");

        Assert.Null(AiReportStore.Read(sessionPath));
        Assert.False(File.Exists(AiReportStore.PathFor(sessionPath)));
    }

    [Fact]
    public void DamagedReportReadsAsMissing()
    {
        string sessionPath = Path.Combine(_directory, "broken.agsession.jsonl.gz");
        File.WriteAllText(AiReportStore.PathFor(sessionPath), "{ not json");

        Assert.Null(AiReportStore.Read(sessionPath));
    }

    [Fact]
    public void ModelAndFactsHashSurviveRoundTrip()
    {
        string sessionPath = Path.Combine(_directory, "reusable.agsession.jsonl.gz");
        Assert.True(AiReportStore.Write(sessionPath, new AiReport
        {
            Model = "gpt-test",
            FactsHash = "0123abcd",
            Summary = "CPU 受限",
        }));

        AiReport? report = AiReportStore.Read(sessionPath);

        Assert.NotNull(report);
        Assert.Equal("gpt-test", report!.Model);
        Assert.True(AiReportStore.IsReusable(report, "GPT-Test", "0123abcd"));
        Assert.False(AiReportStore.IsReusable(report, "other-model", "0123abcd"));
        Assert.False(AiReportStore.IsReusable(report, "gpt-test", "ffff"));
    }

    [Fact]
    public void ReportsWithoutFingerprintAreNeverReused()
    {
        Assert.False(AiReportStore.IsReusable(null, "m", "hash"));
        Assert.False(AiReportStore.IsReusable(new AiReport(), "m", "hash"));
        Assert.False(AiReportStore.IsReusable(new AiReport { Model = "m" }, "m", ""));
    }

    [Fact]
    public void FactsHashIgnoresLanguage()
    {
        var chinese = new FactsDocument
        {
            Locale = "zh-CN",
            Metrics = new FactsMetrics { CpuPeakPct = 12 },
        };
        var english = new FactsDocument
        {
            Locale = "en-US",
            Metrics = new FactsMetrics { CpuPeakPct = 12 },
        };

        string hash = AiAnalysisService.ComputeFactsHash(chinese);

        Assert.Equal("zh-CN", chinese.Locale);
        Assert.Equal(hash, AiAnalysisService.ComputeFactsHash(english));

        english.Metrics.CpuPeakPct = 13;
        Assert.NotEqual(hash, AiAnalysisService.ComputeFactsHash(english));
    }

    [Fact]
    public void FactsComeFromTheSessionAndSkipCollectorGaps()
    {
        SessionDocument document = new()
        {
            Header = new SessionHeaderLine { Mode = "deep", SamplingMs = 200 },
            Event = new SessionEventLine { Id = 7 },
            Samples =
            [
                new SampleRecord { UtcTicks = 1, CpuUtilityPct = 40 },
                new SampleRecord { UtcTicks = 2, CpuUtilityPct = 60 },
            ],
            Frames =
            [
                new FrameTimePoint(1, 16.7f, false, 1.5f, 2.5f, 7.5f),
                new FrameTimePoint(2, 5000f, false),
                new FrameTimePoint(3, 33.3f, true, 3f, 4f, 9f),
            ],
            Names = new Dictionary<uint, string> { [42] = "game.exe" },
        };

        FactsInput input = SessionFactsFactory.FromSession(document, "zh-CN");

        Assert.Equal("deep", input.Mode);
        Assert.Equal("zh-CN", input.Locale);
        Assert.Same(document.Samples, input.Samples);
        Assert.Equal(42u, Assert.Single(input.ProcessNames.Keys));
        Assert.Equal(2, input.Frames.Count);
        Assert.Equal(16.7f, input.Frames[0].FrameTimeMs);
        Assert.Equal(1.5f, input.Frames[0].CpuWaitMs);
        Assert.Equal(9f, input.Frames[1].DisplayLatencyMs);
        Assert.Equal(3f, input.Frames[1].CpuWaitMs);
    }
}
