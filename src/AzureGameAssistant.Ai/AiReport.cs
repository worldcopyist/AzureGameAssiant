using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AzureGameAssistant.Ai;

public sealed class AiReport
{
    public string Summary { get; set; } = string.Empty;
    public string PrimaryCause { get; set; } = "Unknown";
    public string Confidence { get; set; } = "low";
    public List<AiEvidence> Evidence { get; set; } = [];
    public List<AiSuspect> Suspects { get; set; } = [];
    public AiFrameAnalysis? FrameAnalysis { get; set; }
    public List<AiDropAnalysis> DropAnalysis { get; set; } = [];
    public List<AiHighLoadAttribution> HighLoadAttribution { get; set; } = [];
    public List<AiRecommendation> Recommendations { get; set; } = [];
    public List<string> Limitations { get; set; } = [];
    public string Disclaimer { get; set; } = string.Empty;
}

public sealed class AiEvidence
{
    public string Metric { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Interpretation { get; set; } = string.Empty;
}

public sealed class AiSuspect
{
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "background";
    public string Reason { get; set; } = string.Empty;
}

public sealed class AiFrameAnalysis
{
    public string Summary { get; set; } = string.Empty;
    public bool CpuBound { get; set; }
    public bool GpuBound { get; set; }
    public bool PacingIssue { get; set; }
}

public sealed class AiDropAnalysis
{
    public double SecondsAgo { get; set; }
    public string Cause { get; set; } = "unknown";
    public string Explanation { get; set; } = string.Empty;
    public string? SuspectProcess { get; set; }
}

public sealed class AiHighLoadAttribution
{
    public string Resource { get; set; } = "cpu";
    public string Process { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
}

public sealed class AiRecommendation
{
    public string Action { get; set; } = string.Empty;
    public string Risk { get; set; } = "low";
    public string ExpectedEffect { get; set; } = string.Empty;
    public string HowTo { get; set; } = string.Empty;
    public string? Rollback { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AiReport))]
public partial class AiReportJsonContext : JsonSerializerContext
{
}

public sealed record AiValidationResult(
    bool IsValid,
    AiReport? Report,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

public static class AiReportValidator
{
    private static readonly HashSet<string> Causes = new(StringComparer.Ordinal)
    {
        "CpuContention", "GpuBound", "VramPressure", "MemoryPressure", "DiskBound",
        "SystemScheduling", "FramePacing", "ShaderCompilation", "AssetStreaming",
        "ThermalThrottle", "Unknown",
    };

    private static readonly HashSet<string> Confidences = new(StringComparer.Ordinal)
    {
        "high", "medium", "low",
    };

    private static readonly HashSet<string> Risks = new(StringComparer.Ordinal)
    {
        "low", "medium", "high",
    };

    private static readonly HashSet<string> DropCauses = new(StringComparer.OrdinalIgnoreCase)
    {
        "long-stall", "gpu-wait", "cpu-wait", "mixed", "shader-compilation",
        "asset-streaming", "thermal-throttle", "vram-pressure", "memory-pressure",
        "disk-stall", "unknown",
    };

    private static readonly HashSet<string> LoadResources = new(StringComparer.Ordinal)
    {
        "cpu", "gpu", "vram", "memory", "disk",
    };

    private static readonly (Regex Pattern, string Label)[] DangerousPatterns =
    [
        (new Regex(@"(关闭|禁用|停止|disable|turn\s+off|deactivate).{0,12}(defender|杀毒|防病毒|antivirus|防火墙|firewall|security\s+center|安全中心)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "关闭安全软件"),
        (new Regex(@"(修改|编辑|删除|写入|modify|edit|delete|write).{0,12}(注册表|registry)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "修改注册表"),
        (new Regex(@"bcdedit|reg\s+add|regedit", RegexOptions.IgnoreCase | RegexOptions.Compiled), "引导/注册表命令"),
        (new Regex(@"(超频|overclock|undervolt|降压|加电压|increase\s+voltage)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "超频或调压"),
        (new Regex(@"(下载|download).{0,20}(脚本|script|工具|tool).{0,20}(运行|执行|run|execute)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "下载并运行未知脚本"),
        (new Regex(@"(运行|执行|run|execute).{0,20}(脚本|script)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "执行脚本"),
        (new Regex(@"(绕过|关闭|禁用|bypass|disable|turn\s+off).{0,10}(反作弊|anti[- ]?cheat)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "绕过反作弊"),
        (new Regex(@"powershell|cmd\s*/c|bash\s+-c", RegexOptions.IgnoreCase | RegexOptions.Compiled), "命令行执行"),
        (new Regex(@"(禁用|disable).{0,10}(驱动|driver)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "禁用驱动"),
        (new Regex(@"(停止|disable|stop).{0,10}(windows\s+update|系统更新)", RegexOptions.IgnoreCase | RegexOptions.Compiled), "停止系统更新"),
    ];

    public static AiValidationResult Validate(string content, FactsDocument facts)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!TryExtractJson(content, out string json))
        {
            return new AiValidationResult(false, null, ["响应中没有 JSON 对象"], warnings);
        }

        AiReport? report;
        try
        {
            report = JsonSerializer.Deserialize(json, AiReportJsonContext.Default.AiReport);
        }
        catch (JsonException ex)
        {
            return new AiValidationResult(false, null, ["JSON 解析失败：" + ex.Message], warnings);
        }

        if (report is null)
        {
            return new AiValidationResult(false, null, ["JSON 内容为空"], warnings);
        }

        NormalizeSummary(report);
        if (report.Summary.Length == 0)
        {
            errors.Add("summary 为空");
        }

        NormalizeEnum(report, warnings);
        ValidateEvidence(report, facts, errors, warnings);
        NormalizeSuspects(report, warnings);
        NormalizeFrameAnalysis(report, warnings);
        ValidateDropAnalysis(report, facts, errors, warnings);
        ValidateHighLoadAttribution(report, facts, errors, warnings);
        ValidateRecommendations(report, warnings);
        NormalizeLimitations(report);
        NormalizeDisclaimer(report);

        if (report.Recommendations.Count == 0)
        {
            errors.Add("没有可用的优化建议");
        }

        return new AiValidationResult(errors.Count == 0, errors.Count == 0 ? report : null, errors, warnings);
    }

    public static bool IsDangerous(string text) =>
        DangerousPatterns.FirstOrDefault(entry => entry.Pattern.IsMatch(text)).Label is not null;

    private static string? FindDangerous(string text) =>
        DangerousPatterns.FirstOrDefault(entry => entry.Pattern.IsMatch(text)).Label;

    private static bool TryExtractJson(string content, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstBreak = text.IndexOf('\n');
            text = firstBreak >= 0 ? text[(firstBreak + 1)..] : text;
            int fence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                text = text[..fence];
            }
        }

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        json = text[start..(end + 1)];
        return true;
    }

    private static void NormalizeSummary(AiReport report)
    {
        report.Summary = Trim(StripControl(report.Summary), 120);
    }

    private static void NormalizeEnum(AiReport report, List<string> warnings)
    {
        if (!Causes.Contains(report.PrimaryCause))
        {
            warnings.Add($"未知 primaryCause：{report.PrimaryCause}，已改为 Unknown");
            report.PrimaryCause = "Unknown";
        }

        string confidence = (report.Confidence ?? string.Empty).Trim().ToLowerInvariant();
        if (!Confidences.Contains(confidence))
        {
            warnings.Add("未知 confidence，已改为 low");
            confidence = "low";
        }

        report.Confidence = confidence;
    }

    private static void ValidateEvidence(AiReport report, FactsDocument facts, List<string> errors, List<string> warnings)
    {
        HashSet<string> available = AvailableMetrics(facts);
        var kept = new List<AiEvidence>();

        foreach (AiEvidence evidence in report.Evidence ?? [])
        {
            string metric = (evidence.Metric ?? string.Empty).Trim();
            if (metric.Length == 0 || !available.Contains(metric))
            {
                warnings.Add($"丢弃无法在事实层确认的证据：{metric}");
                continue;
            }

            evidence.Value = Trim(StripControl(evidence.Value), 80);
            evidence.Interpretation = Trim(StripControl(evidence.Interpretation), 200);
            kept.Add(evidence);
            if (kept.Count >= 8)
            {
                break;
            }
        }

        report.Evidence = kept;
        if (kept.Count == 0)
        {
            errors.Add("证据列表为空或未引用事实层指标");
        }
    }

    private static HashSet<string> AvailableMetrics(FactsDocument facts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        void Add(string name, object? value)
        {
            if (value is not null)
            {
                names.Add(name);
            }
        }

        Add("cpuPeakPct", facts.Metrics.CpuPeakPct);
        Add("cpuAvgPct", facts.Metrics.CpuAvgPct);
        Add("gpuPeakPct", facts.Metrics.GpuPeakPct);
        Add("gpuAvgPct", facts.Metrics.GpuAvgPct);
        Add("vramUsedMb", facts.Metrics.VramUsedMb);
        Add("vramTotalMb", facts.Metrics.VramTotalMb);
        Add("memAvailableMb", facts.Metrics.MemAvailableMb);
        Add("commitRatio", facts.Metrics.CommitRatio);
        Add("diskLatencyMs", facts.Metrics.DiskLatencyMs);
        Add("timerJitterMaxMs", facts.Metrics.TimerJitterMaxMs);

        if (facts.Frames is { } frames)
        {
            Add("avgFps", frames.AvgFps);
            Add("onePercentLowFps", frames.OnePercentLowFps);
            Add("pointOnePercentLowFps", frames.PointOnePercentLowFps);
            Add("maxFrameTimeMs", frames.MaxFrameTimeMs);
            Add("p99FrameTimeMs", frames.P99FrameTimeMs);
            Add("avgCpuWaitMs", frames.AvgCpuWaitMs);
            Add("avgGpuWaitMs", frames.AvgGpuWaitMs);
            Add("displayGapCount", frames.Count > 0 ? frames.DisplayGapCount : null);
        }

        if (facts.Sensors is { } sensors)
        {
            Add("gpuTemperatureC", sensors.GpuTemperatureC);
            Add("gpuHotSpotC", sensors.GpuHotSpotC);
            Add("gpuPowerW", sensors.GpuPowerW);
            Add("gpuCoreClockMhz", sensors.GpuCoreClockMhz);
            Add("gpuFanPct", sensors.GpuFanPct);
            Add("cpuTemperatureC", sensors.CpuTemperatureC);
            Add("cpuPowerW", sensors.CpuPowerW);
        }

        if (facts.Environment.LogicalProcessors > 0)
        {
            names.Add("logicalProcessors");
        }

        Add("memoryGb", facts.Environment.MemoryGb);
        Add("samplingMs", facts.Environment.SamplingMs > 0 ? facts.Environment.SamplingMs : null);

        return names;
    }

    private static void NormalizeSuspects(AiReport report, List<string> warnings)
    {
        var kept = new List<AiSuspect>();
        foreach (AiSuspect suspect in report.Suspects ?? [])
        {
            string name = Trim(StripControl(suspect.Name), 64);
            if (name.Length == 0)
            {
                continue;
            }

            string role = (suspect.Role ?? string.Empty).Trim().ToLowerInvariant();
            if (role is not ("foreground" or "background"))
            {
                role = "background";
            }

            suspect.Name = name;
            suspect.Role = role;
            suspect.Reason = Trim(StripControl(suspect.Reason), 200);
            kept.Add(suspect);
            if (kept.Count >= 8)
            {
                break;
            }
        }

        if (kept.Count != (report.Suspects?.Count ?? 0))
        {
            warnings.Add("已清理无效的可疑进程项");
        }

        report.Suspects = kept;
    }

    private static void NormalizeFrameAnalysis(AiReport report, List<string> warnings)
    {
        if (report.FrameAnalysis is null)
        {
            warnings.Add("模型未提供帧分析");
            report.FrameAnalysis = new AiFrameAnalysis { Summary = "未提供" };
        }

        report.FrameAnalysis.Summary = Trim(StripControl(report.FrameAnalysis.Summary), 200);
        if (string.IsNullOrEmpty(report.FrameAnalysis.Summary))
        {
            report.FrameAnalysis.Summary = "未提供";
        }
    }

    private static void ValidateDropAnalysis(
        AiReport report,
        FactsDocument facts,
        List<string> errors,
        List<string> warnings)
    {
        var kept = new List<AiDropAnalysis>();
        foreach (AiDropAnalysis entry in report.DropAnalysis ?? [])
        {
            string cause = (entry.Cause ?? string.Empty).Trim();
            if (!DropCauses.Contains(cause))
            {
                warnings.Add($"未知掉帧原因：{cause}，已改为 unknown");
                cause = "unknown";
            }

            entry.Cause = cause.ToLowerInvariant();
            entry.Explanation = Trim(StripControl(entry.Explanation), 300);
            entry.SuspectProcess = string.IsNullOrWhiteSpace(entry.SuspectProcess)
                ? null
                : Trim(StripControl(entry.SuspectProcess), 64);
            entry.SecondsAgo = Math.Round(entry.SecondsAgo, 1);

            if (entry.Explanation.Length == 0)
            {
                warnings.Add("丢弃缺少解释的掉帧分析项");
                continue;
            }

            kept.Add(entry);
            if (kept.Count >= 12)
            {
                break;
            }
        }

        report.DropAnalysis = kept;
        if (facts.Drops.Count == 0)
        {
            return;
        }

        foreach (FactsDrop drop in facts.Drops)
        {
            bool covered = kept.Any(entry => Math.Abs(entry.SecondsAgo - drop.SecondsAgo) <= 2.0);
            if (!covered)
            {
                errors.Add($"掉帧事件（约 {drop.SecondsAgo:0.#} 秒前）没有给出原因分析");
            }
        }
    }

    private static void ValidateHighLoadAttribution(
        AiReport report,
        FactsDocument facts,
        List<string> errors,
        List<string> warnings)
    {
        var kept = new List<AiHighLoadAttribution>();
        foreach (AiHighLoadAttribution entry in report.HighLoadAttribution ?? [])
        {
            string resource = (entry.Resource ?? string.Empty).Trim().ToLowerInvariant();
            if (!LoadResources.Contains(resource))
            {
                warnings.Add($"未知高负载资源：{entry.Resource}");
                continue;
            }

            entry.Resource = resource;
            entry.Process = Trim(StripControl(entry.Process), 64);
            entry.Explanation = Trim(StripControl(entry.Explanation), 300);

            if (entry.Process.Length == 0)
            {
                warnings.Add($"丢弃未指明进程的高负载归因（{resource}）");
                continue;
            }

            kept.Add(entry);
            if (kept.Count >= 12)
            {
                break;
            }
        }

        report.HighLoadAttribution = kept;
        if (facts.HighLoads.Count == 0)
        {
            return;
        }

        HashSet<string> known = KnownProcessNames(facts);
        foreach (FactsHighLoad load in facts.HighLoads)
        {
            AiHighLoadAttribution? match = kept.FirstOrDefault(
                entry => string.Equals(entry.Resource, load.Resource, StringComparison.Ordinal));
            if (match is null)
            {
                errors.Add($"高负载事件（{load.Resource}）没有返回造成高负载的进程");
                continue;
            }

            if (known.Count > 0 && !known.Contains(match.Process))
            {
                warnings.Add($"高负载归因进程不在事实层列表中：{match.Process}");
            }
        }
    }

    private static HashSet<string> KnownProcessNames(FactsDocument facts)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (FactsProcess process in facts.Processes)
        {
            if (!string.IsNullOrWhiteSpace(process.Name))
            {
                names.Add(process.Name);
            }
        }

        foreach (FactsDrop drop in facts.Drops)
        {
            if (!string.IsNullOrWhiteSpace(drop.SuspectProcess))
            {
                names.Add(drop.SuspectProcess);
            }
        }

        return names;
    }

    private static void ValidateRecommendations(AiReport report, List<string> warnings)
    {
        var kept = new List<AiRecommendation>();

        foreach (AiRecommendation recommendation in report.Recommendations ?? [])
        {
            recommendation.Action = Trim(StripControl(recommendation.Action), 200);
            recommendation.HowTo = Trim(StripControl(recommendation.HowTo), 500);
            recommendation.ExpectedEffect = Trim(StripControl(recommendation.ExpectedEffect), 200);
            recommendation.Rollback = string.IsNullOrWhiteSpace(recommendation.Rollback)
                ? null
                : Trim(StripControl(recommendation.Rollback), 300);

            string risk = (recommendation.Risk ?? string.Empty).Trim().ToLowerInvariant();
            if (!Risks.Contains(risk))
            {
                risk = "medium";
            }

            recommendation.Risk = risk;

            if (recommendation.Action.Length == 0 || recommendation.HowTo.Length == 0)
            {
                warnings.Add("丢弃缺少 action 或 howTo 的建议");
                continue;
            }

            string text = recommendation.Action + " " + recommendation.HowTo + " " + recommendation.ExpectedEffect;
            string? dangerous = FindDangerous(text);
            if (dangerous is not null)
            {
                warnings.Add($"丢弃危险建议（{dangerous}）：{recommendation.Action}");
                continue;
            }

            if (risk == "high" && recommendation.Rollback is null)
            {
                warnings.Add($"丢弃缺少回滚方案的高风险建议：{recommendation.Action}");
                continue;
            }

            kept.Add(recommendation);
            if (kept.Count >= 5)
            {
                break;
            }
        }

        report.Recommendations = kept;
    }

    private static void NormalizeLimitations(AiReport report)
    {
        var limitations = new List<string>();
        foreach (string limitation in report.Limitations ?? [])
        {
            string text = Trim(StripControl(limitation), 200);
            if (text.Length > 0)
            {
                limitations.Add(text);
            }

            if (limitations.Count >= 5)
            {
                break;
            }
        }

        if (limitations.Count == 0)
        {
            limitations.Add("模型未说明分析局限");
        }

        report.Limitations = limitations;
    }

    private static void NormalizeDisclaimer(AiReport report)
    {
        report.Disclaimer = Trim(StripControl(report.Disclaimer), 300);
        if (report.Disclaimer.Length == 0)
        {
            report.Disclaimer = "本报告由模型根据脱敏后的本地硬件数据生成，仅供参考；请自行评估每条建议的风险。";
        }
    }

    private static string StripControl(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsControl(ch) || ch == '\n')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString().Trim();
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
