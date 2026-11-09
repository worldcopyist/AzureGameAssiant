using AzureGameAssistant.Ai;

namespace AzureGameAssistant.Tests;

public sealed class AiReportValidatorTests
{
    private const string ValidReport = """
    {
      "summary": "CPU 占用过高导致帧率波动",
      "primaryCause": "CpuContention",
      "confidence": "medium",
      "evidence": [
        { "metric": "cpuPeakPct", "value": "95%", "interpretation": "峰值接近满载" }
      ],
      "suspects": [
        { "name": "Game", "role": "foreground", "reason": "占用最高" }
      ],
      "frameAnalysis": {
        "summary": "CPU 等待偏高",
        "cpuBound": true,
        "gpuBound": false,
        "pacingIssue": false
      },
      "recommendations": [
        {
          "action": "关闭后台录制",
          "risk": "low",
          "expectedEffect": "释放 CPU",
          "howTo": "在录制软件中停止录制",
          "rollback": null
        }
      ],
      "limitations": ["仅覆盖当前采集窗口"],
      "disclaimer": "仅供参考"
    }
    """;

    [Fact]
    public void AcceptsValidReport()
    {
        AiValidationResult result = AiReportValidator.Validate(ValidReport, Facts());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.NotNull(result.Report);
        Assert.Equal("CpuContention", result.Report!.PrimaryCause);
        Assert.Equal("medium", result.Report.Confidence);
        Assert.Single(result.Report.Evidence);
        Assert.Single(result.Report.Recommendations);
    }

    [Fact]
    public void AcceptsFencedJson()
    {
        AiValidationResult result = AiReportValidator.Validate(
            "```json\n" + ValidReport + "\n```",
            Facts());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Fact]
    public void NormalizesUnknownEnumValues()
    {
        string json = ValidReport
            .Replace("\"CpuContention\"", "\"MadeUpCause\"", StringComparison.Ordinal)
            .Replace("\"medium\"", "\"certain\"", StringComparison.Ordinal);

        AiValidationResult result = AiReportValidator.Validate(json, Facts());

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("Unknown", result.Report!.PrimaryCause);
        Assert.Equal("low", result.Report.Confidence);
        Assert.NotEmpty(result.Warnings);
    }

    [Fact]
    public void RejectsReportWithoutVerifiableEvidence()
    {
        string json = ValidReport.Replace("\"cpuPeakPct\"", "\"inventedMetric\"", StringComparison.Ordinal);

        AiValidationResult result = AiReportValidator.Validate(json, Facts());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("证据", StringComparison.Ordinal));
    }

    [Fact]
    public void DropsDangerousRecommendation()
    {
        string json = ValidReport.Replace("\"关闭后台录制\"", "\"关闭 Windows Defender\"", StringComparison.Ordinal);

        AiValidationResult result = AiReportValidator.Validate(json, Facts());

        Assert.False(result.IsValid);
        Assert.Contains(result.Warnings, warning => warning.Contains("危险建议", StringComparison.Ordinal));
    }

    [Fact]
    public void DropsHighRiskRecommendationWithoutRollback()
    {
        string json = ValidReport.Replace("\"risk\": \"low\"", "\"risk\": \"high\"", StringComparison.Ordinal);

        AiValidationResult result = AiReportValidator.Validate(json, Facts());

        Assert.False(result.IsValid);
        Assert.Contains(result.Warnings, warning => warning.Contains("回滚", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("禁用 Windows Defender", true)]
    [InlineData("bypass anti-cheat", true)]
    [InlineData("运行 powershell 脚本", true)]
    [InlineData("关闭后台录制", false)]
    [InlineData("降低游戏画质", false)]
    public void ClassifiesDangerousAdvice(string text, bool dangerous)
    {
        Assert.Equal(dangerous, AiReportValidator.IsDangerous(text));
    }

    [Fact]
    public void RequiresDropCoverageAndHighLoadProcess()
    {
        FactsDocument facts = Facts();
        facts.Processes.Add(new FactsProcess { Name = "Browser" });
        facts.Drops.Add(new FactsDrop
        {
            SecondsAgo = 12.5,
            FrameTimeMs = 120,
            Kind = "gpu-wait",
            SuspectProcess = "Browser",
        });
        facts.HighLoads.Add(new FactsHighLoad
        {
            Resource = "cpu",
            PeakPct = 95,
            Seconds = 4,
            TopProcess = "Browser",
        });

        AiValidationResult missing = AiReportValidator.Validate(ValidReport, facts);

        Assert.False(missing.IsValid);
        Assert.Contains(missing.Errors, error => error.Contains("掉帧事件", StringComparison.Ordinal));
        Assert.Contains(missing.Errors, error => error.Contains("高负载", StringComparison.Ordinal));

        const string complete = """
        {
          "summary": "CPU 占用过高导致帧率波动",
          "primaryCause": "CpuContention",
          "confidence": "medium",
          "evidence": [
            { "metric": "cpuPeakPct", "value": "95%", "interpretation": "峰值接近满载" }
          ],
          "suspects": [
            { "name": "Browser", "role": "background", "reason": "占用最高" }
          ],
          "frameAnalysis": {
            "summary": "CPU 等待偏高",
            "cpuBound": true,
            "gpuBound": false,
            "pacingIssue": false
          },
          "dropAnalysis": [
            { "secondsAgo": 12.5, "cause": "gpu-wait", "explanation": "GPU 等待导致帧时间上升", "suspectProcess": "Browser" }
          ],
          "highLoadAttribution": [
            { "resource": "cpu", "process": "Browser", "explanation": "该进程 CPU 占用最高" }
          ],
          "recommendations": [
            {
              "action": "关闭后台录制",
              "risk": "low",
              "expectedEffect": "释放 CPU",
              "howTo": "在录制软件中停止录制",
              "rollback": null
            }
          ],
          "limitations": ["仅覆盖当前采集窗口"],
          "disclaimer": "仅供参考"
        }
        """;

        AiValidationResult ok = AiReportValidator.Validate(complete, facts);

        Assert.True(ok.IsValid, string.Join("; ", ok.Errors));
        Assert.Single(ok.Report!.DropAnalysis);
        Assert.Single(ok.Report.HighLoadAttribution);
    }

    private static FactsDocument Facts() => new()
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
}
