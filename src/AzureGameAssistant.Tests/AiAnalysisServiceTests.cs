using System.Net;
using System.Text;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Tests;

public sealed class AiAnalysisServiceTests
{
    private static readonly DateTimeOffset Clock = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    private const string ReportJson = """
    {
      "summary": "CPU 占用偏高",
      "primaryCause": "CpuContention",
      "confidence": "medium",
      "evidence": [
        { "metric": "cpuPeakPct", "value": "95%", "interpretation": "峰值接近满载" }
      ],
      "suspects": [],
      "frameAnalysis": { "summary": "CPU 等待偏高", "cpuBound": true, "gpuBound": false, "pacingIssue": false },
      "recommendations": [
        { "action": "关闭后台录制", "risk": "low", "expectedEffect": "释放 CPU", "howTo": "停止录制", "rollback": null }
      ],
      "limitations": ["仅覆盖当前窗口"],
      "disclaimer": "仅供参考"
    }
    """;

    [Fact]
    public void BuildRequestReportsDisabledFeature()
    {
        using var fixture = new Fixture();
        AppSettings settings = Fixture.Settings(enabled: false);

        AiPreparedRequest prepared = fixture.Service.BuildRequest(settings, Fixture.Facts(), "zh-CN");

        Assert.NotNull(prepared.ConfigurationError);
        Assert.Contains("未启用", prepared.ConfigurationError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnalyzeWithoutKeyFallsBackToLocalReport()
    {
        using var fixture = new Fixture();
        AppSettings settings = Fixture.Settings();
        AiPreparedRequest prepared = fixture.Service.BuildRequest(settings, Fixture.Facts(), "zh-CN");

        AiAnalysisResult result = await fixture.Service.AnalyzeAsync(prepared, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.UsedLocalFallback);
        Assert.Equal(AiFailureKind.Unauthorized, result.Failure);
        Assert.NotEmpty(result.Report.Summary);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task AnalyzeStopsAtBudgetLimit()
    {
        using var fixture = new Fixture();
        AppSettings settings = Fixture.Settings();
        settings.Ai.DailyBudget = 1;
        fixture.Store.Keys["test-local"] = "sk-test";
        fixture.Usage.Record(0, 0, 5);

        AiPreparedRequest prepared = fixture.Service.BuildRequest(settings, Fixture.Facts(), "zh-CN");
        AiAnalysisResult result = await fixture.Service.AnalyzeAsync(prepared, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(AiFailureKind.Budget, result.Failure);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task AnalyzeSuccessSavesReportAndRecordsUsage()
    {
        using var fixture = new Fixture(respond: (_, _) => Json(HttpStatusCode.OK, Envelope(ReportJson)));
        AppSettings settings = Fixture.Settings();
        fixture.Store.Keys["test-local"] = "sk-test";

        AiPreparedRequest prepared = fixture.Service.BuildRequest(settings, Fixture.Facts(), "zh-CN");
        AiAnalysisResult result = await fixture.Service.AnalyzeAsync(prepared, null, CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.False(result.UsedLocalFallback);
        Assert.Equal("CpuContention", result.Report.PrimaryCause);
        Assert.NotNull(result.SavedPath);
        Assert.True(File.Exists(result.SavedPath));
        Assert.True(fixture.Usage.Snapshot().Cost > 0);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task InvalidOutputTwiceFallsBackAndLogs()
    {
        using var fixture = new Fixture(respond: (_, _) => Json(HttpStatusCode.OK, Envelope("not json")));
        AppSettings settings = Fixture.Settings();
        fixture.Store.Keys["test-local"] = "sk-test";

        AiPreparedRequest prepared = fixture.Service.BuildRequest(settings, Fixture.Facts(), "zh-CN");
        AiAnalysisResult result = await fixture.Service.AnalyzeAsync(prepared, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.UsedLocalFallback);
        Assert.Equal(AiFailureKind.InvalidResponse, result.Failure);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.True(Directory.Exists(fixture.LogsDirectory));
        Assert.NotEmpty(Directory.GetFiles(fixture.LogsDirectory, "ai-parse-*.log"));
    }

    private static string Envelope(string content) =>
        "{\"choices\":[{\"message\":{\"content\":" +
        System.Text.Json.JsonSerializer.Serialize(content) +
        "}}],\"usage\":{\"prompt_tokens\":120,\"completion_tokens\":60}}";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "aga-ai-tests-" + Guid.NewGuid().ToString("N"));

        public Fixture(Func<HttpRequestMessage, int, HttpResponseMessage>? respond = null)
        {
            Directory.CreateDirectory(_root);
            Handler = new StubHandler(respond ?? ((_, _) =>
                Json(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}")));
            Store = new FakeCredentialStore();
            Usage = new AiUsageStore(Path.Combine(_root, "usage.json"), () => Clock);
            Service = new AiAnalysisService(
                Store,
                Usage,
                Handler,
                retryDelay: TimeSpan.Zero,
                clock: () => Clock,
                dataDirectory: _root);
        }

        public StubHandler Handler { get; }

        public FakeCredentialStore Store { get; }

        public AiUsageStore Usage { get; }

        public AiAnalysisService Service { get; }

        public string LogsDirectory => Path.Combine(_root, "logs");

        public static AppSettings Settings(bool enabled = true)
        {
            var settings = new AppSettings();
            settings.Ai.Enabled = enabled;
            settings.Ai.ActiveProvider = "test-local";
            settings.Ai.Providers.Add(new AiProviderSettings
            {
                Id = "test-local",
                BaseUrl = "http://127.0.0.1:9999/v1",
                Model = "test-model",
                RequiresKey = true,
                InputPricePerMillion = 1,
                OutputPricePerMillion = 2,
            });

            return settings;
        }

        public static FactsDocument Facts() => new()
        {
            Environment = new FactsEnvironment { LogicalProcessors = 12 },
            Metrics = new FactsMetrics
            {
                CpuPeakPct = 95,
                CpuAvgPct = 80,
                GpuPeakPct = 70,
                GpuAvgPct = 50,
                MemAvailableMb = 800,
            },
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FakeCredentialStore : IAiCredentialStore
    {
        public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool TryRead(string providerId, out string key) =>
            Keys.TryGetValue(providerId, out key!) && key.Length > 0;

        public bool Save(string providerId, string key)
        {
            Keys[providerId] = key;
            return true;
        }

        public bool Delete(string providerId) => Keys.Remove(providerId);

        public bool Exists(string providerId) => Keys.ContainsKey(providerId);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(responder(request, Calls));
        }
    }
}
