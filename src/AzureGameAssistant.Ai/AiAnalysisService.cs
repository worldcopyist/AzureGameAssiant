using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Ai;

public sealed record AiProviderOptions(
    string Id,
    string DisplayName,
    string BaseUrl,
    string Model,
    bool RequiresKey,
    int MaxInputTokens,
    int MaxOutputTokens,
    double InputPricePerMillion,
    double OutputPricePerMillion);

public sealed record AiPreparedRequest(
    AiProviderOptions Provider,
    Uri? Endpoint,
    FactsDocument Facts,
    string FactsJson,
    string FactsHash,
    string Locale,
    int InputTokens,
    int MaxOutputTokens,
    double EstimatedCost,
    int RedactionHits,
    IReadOnlyList<string> RedactionCategories,
    bool KeyAvailable,
    double DailyBudget,
    double MonthlyBudget,
    AiBudgetStatus Budget,
    string? ConfigurationError);

public sealed record AiAnalysisResult(
    bool Success,
    bool UsedLocalFallback,
    AiReport Report,
    AiFailureKind Failure,
    string? Error,
    string? RawContent,
    string? SavedPath,
    int InputTokens,
    int OutputTokens,
    double Cost,
    IReadOnlyList<string> Warnings,
    int Attempts,
    bool Streamed);

/// <summary>
/// Orchestrates fact trimming, redaction, budget checks, the HTTP call and the
/// validation/repair cycle. Always returns a local rule report when the model
/// path fails, so the feature degrades instead of disappearing.
/// </summary>
public sealed class AiAnalysisService : IDisposable
{
    private readonly IAiCredentialStore _credentials;
    private readonly AiUsageStore _usage;
    private readonly OpenAiCompatibleClient _client;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string _reportsDirectory;
    private readonly string _logsDirectory;
    private int _active;

    public AiAnalysisService(
        IAiCredentialStore? credentials = null,
        AiUsageStore? usage = null,
        HttpMessageHandler? handler = null,
        TimeSpan? retryDelay = null,
        Func<DateTimeOffset>? clock = null,
        string? dataDirectory = null)
    {
        _credentials = credentials ?? CredentialStore.Shared;
        _usage = usage ?? new AiUsageStore(clock: clock);
        _client = new OpenAiCompatibleClient(handler, retryDelay);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _reportsDirectory = dataDirectory is null
            ? AppPaths.AiReportsDirectory
            : Path.Combine(dataDirectory, "reports");
        _logsDirectory = dataDirectory is null
            ? AppPaths.LogsDirectory
            : Path.Combine(dataDirectory, "logs");
    }

    public bool IsBusy => Volatile.Read(ref _active) != 0;

    public AiPreparedRequest BuildRequest(
        AppSettings settings,
        FactsDocument facts,
        string locale,
        string? providerId = null)
    {
        AiProviderOptions provider = ResolveProvider(settings, providerId, out string? configError);

        if (string.IsNullOrEmpty(configError) && !settings.Ai.Enabled)
        {
            configError = "AI 功能未启用";
        }

        Uri? endpoint = null;
        if (configError is null)
        {
            if (!AiEndpoint.TryNormalize(provider.BaseUrl, out endpoint, out string? urlError))
            {
                configError = urlError;
            }
            else if (string.IsNullOrWhiteSpace(provider.Model))
            {
                configError = "请填写模型名称";
            }
        }

        facts.Locale = string.IsNullOrWhiteSpace(locale) ? "zh-CN" : locale;

        string factsHash = ComputeFactsHash(facts);
        string json = SerializeAndRedact(facts, settings.Ai.Redact, out int hits, out IReadOnlyList<string> categories);
        AiPrompt prompt = PromptBuilder.Build(json, facts.Locale);
        int tokens = CostEstimator.EstimateTokens(prompt.System) + CostEstimator.EstimateTokens(prompt.User);

        if (tokens > provider.MaxInputTokens)
        {
            bool trimmed = Trim(facts, provider.MaxInputTokens, settings.Ai.Redact, out json, out hits, out categories, out tokens);
            if (!trimmed)
            {
                configError ??= "事实层超过模型输入上限，请提高 MaxInputTokens 或缩短采集窗口";
            }
        }

        bool requiresKey = provider.RequiresKey;
        bool keyAvailable = !requiresKey || _credentials.TryRead(provider.Id, out _);

        double estimatedCost = CostEstimator.EstimateCost(
            tokens,
            provider.MaxOutputTokens,
            provider.InputPricePerMillion,
            provider.OutputPricePerMillion);

        AiBudgetStatus budget = _usage.Check(estimatedCost, settings.Ai.DailyBudget, settings.Ai.MonthlyBudget);

        return new AiPreparedRequest(
            provider,
            endpoint,
            facts,
            json,
            factsHash,
            facts.Locale,
            tokens,
            provider.MaxOutputTokens,
            estimatedCost,
            hits,
            categories,
            keyAvailable,
            settings.Ai.DailyBudget,
            settings.Ai.MonthlyBudget,
            budget,
            configError);
    }

    public async Task<AiAnalysisResult> AnalyzeAsync(
        AiPreparedRequest prepared,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _active);
        try
        {
            return await AnalyzeCoreAsync(prepared, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private async Task<AiAnalysisResult> AnalyzeCoreAsync(
        AiPreparedRequest prepared,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        AiReport local = LocalReportBuilder.Build(prepared.Facts);
        var warnings = new List<string>();

        if (prepared.ConfigurationError is { } configurationError)
        {
            return Failure(local, AiFailureKind.InvalidResponse, configurationError, null, warnings, 0);
        }

        if (prepared.Provider.RequiresKey && !prepared.KeyAvailable)
        {
            return Failure(local, AiFailureKind.Unauthorized, "请先在设置中保存 API Key", null, warnings, 0);
        }

        AiBudgetStatus budget = _usage.Check(
            prepared.EstimatedCost,
            prepared.DailyBudget,
            prepared.MonthlyBudget);

        if (!budget.Allowed)
        {
            return Failure(local, AiFailureKind.Budget, budget.Reason ?? "已达到预算上限", null, warnings, 0);
        }

        string key = prepared.Provider.RequiresKey && _credentials.TryRead(prepared.Provider.Id, out string stored)
            ? stored
            : string.Empty;

        var request = new AiRequest(
            prepared.Endpoint!,
            prepared.Provider.Model,
            key,
            prepared.MaxOutputTokens);

        AiPrompt prompt = PromptBuilder.Build(prepared.FactsJson, prepared.Locale);
        AiCompletion completion = await _client
            .CompleteAsync(request, prompt, progress, cancellationToken)
            .ConfigureAwait(false);

        int attempts = completion.Attempts;
        bool streamed = completion.Streamed;
        (int inputTokens, int outputTokens) = CountTokens(completion, prompt);
        double cost = CostOf(prepared, inputTokens, outputTokens);

        if (completion.Success)
        {
            _usage.Record(inputTokens, outputTokens, cost);
        }
        else if (completion.Failure == AiFailureKind.Cancelled)
        {
            return Failure(local, AiFailureKind.Cancelled, "已取消", completion.Content.Length > 0 ? completion.Content : null, warnings, attempts);
        }
        else
        {
            _usage.Record(inputTokens, outputTokens, cost);
            return Failure(
                local,
                completion.Failure,
                completion.Error ?? "模型请求失败",
                null,
                warnings,
                attempts);
        }

        AiValidationResult validation = AiReportValidator.Validate(completion.Content, prepared.Facts);
        warnings.AddRange(validation.Warnings);
        string raw = completion.Content;

        if (!validation.IsValid)
        {
            WriteParseLog(raw, validation.Errors, "校验失败");

            AiPrompt repair = PromptBuilder.BuildRepair(
                prepared.FactsJson,
                prepared.Locale,
                raw,
                validation.Errors);

            AiCompletion second = await _client
                .CompleteAsync(request, repair, null, cancellationToken)
                .ConfigureAwait(false);

            attempts += second.Attempts;
            (int repairInput, int repairOutput) = CountTokens(second, repair);
            double repairCost = CostOf(prepared, repairInput, repairOutput);

            if (second.Success)
            {
                _usage.Record(repairInput, repairOutput, repairCost);
            }
            else if (second.Failure == AiFailureKind.Cancelled)
            {
                return Failure(local, AiFailureKind.Cancelled, "已取消", raw, warnings, attempts);
            }
            else
            {
                WriteParseLog(second.Content, [second.Error ?? "修复请求失败"], "修复失败");
                return Failure(local, second.Failure, "模型输出无效，已回退本地报告", raw, warnings, attempts);
            }

            inputTokens += repairInput;
            outputTokens += repairOutput;
            cost += repairCost;
            streamed |= second.Streamed;

            validation = AiReportValidator.Validate(second.Content, prepared.Facts);
            warnings.AddRange(validation.Warnings);
            raw = second.Content;

            if (!validation.IsValid)
            {
                WriteParseLog(raw, validation.Errors, "修复后仍无效");
                return Failure(local, AiFailureKind.InvalidResponse, "模型输出无效，已回退本地报告", raw, warnings, attempts);
            }
        }

        string? savedPath = SaveReport(validation.Report!);

        return new AiAnalysisResult(
            true,
            false,
            validation.Report!,
            AiFailureKind.None,
            null,
            raw,
            savedPath,
            inputTokens,
            outputTokens,
            cost,
            warnings,
            attempts,
            streamed);
    }

    private static AiProviderOptions ResolveProvider(AppSettings settings, string? providerId, out string? error)
    {
        error = null;
        string id = (providerId ?? settings.Ai.ActiveProvider ?? string.Empty).Trim().ToLowerInvariant();
        if (id.Length == 0)
        {
            id = "deepseek";
        }

        AiProviderPreset preset = AiProviderPresets.Resolve(id);
        AiProviderSettings? configured = settings.Ai.Providers
            .FirstOrDefault(provider => string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));

        string baseUrl = configured?.BaseUrl?.Trim() ?? string.Empty;
        if (baseUrl.Length == 0)
        {
            baseUrl = preset.BaseUrl;
        }

        string model = configured?.Model?.Trim() ?? string.Empty;
        if (model.Length == 0)
        {
            model = preset.SuggestedModel;
        }

        bool requiresKey = AiProviderPresets.IsKnown(id)
            ? preset.RequiresKey
            : configured?.RequiresKey ?? true;

        return new AiProviderOptions(
            id,
            preset.DisplayName,
            baseUrl,
            model,
            requiresKey,
            Math.Clamp(configured?.MaxInputTokens ?? 8_000, 1_000, 200_000),
            Math.Clamp(configured?.MaxOutputTokens ?? 2_000, 128, 32_000),
            Math.Max(0, configured?.InputPricePerMillion ?? 0),
            Math.Max(0, configured?.OutputPricePerMillion ?? 0));
    }

    /// <summary>
    /// Identity of the fact layer a report was produced from. Locale is left
    /// out so switching the UI language does not invalidate a stored report.
    /// </summary>
    public static string ComputeFactsHash(FactsDocument facts)
    {
        string locale = facts.Locale;
        facts.Locale = string.Empty;
        try
        {
            return Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(FactsSerializer.Serialize(facts))))
                .ToLowerInvariant();
        }
        finally
        {
            facts.Locale = locale;
        }
    }

    private static string SerializeAndRedact(
        FactsDocument facts,
        bool redact,
        out int hits,
        out IReadOnlyList<string> categories)
    {
        string json = FactsSerializer.Serialize(facts);
        RedactionResult result = Redactor.Apply(json, redact);
        hits = result.Hits;
        categories = result.Categories;
        return result.Json;
    }

    private static bool Trim(
        FactsDocument facts,
        int maxInputTokens,
        bool redact,
        out string json,
        out int hits,
        out IReadOnlyList<string> categories,
        out int tokens)
    {
        bool noted = false;

        void Note()
        {
            if (!noted)
            {
                facts.Gaps.Add("为满足模型输入上限，已裁剪部分事实字段");
                noted = true;
            }
        }

        json = SerializeAndRedact(facts, redact, out hits, out categories);
        tokens = CostEstimator.EstimateTokens(json) + 400;

        while (tokens > maxInputTokens && facts.Processes.Count > 0)
        {
            Note();
            facts.Processes.RemoveAt(facts.Processes.Count - 1);
            json = SerializeAndRedact(facts, redact, out hits, out categories);
            tokens = CostEstimator.EstimateTokens(json) + 400;
        }

        if (tokens > maxInputTokens && facts.Sensors is not null)
        {
            Note();
            facts.Sensors = null;
            json = SerializeAndRedact(facts, redact, out hits, out categories);
            tokens = CostEstimator.EstimateTokens(json) + 400;
        }

        if (tokens > maxInputTokens && facts.Frames is not null)
        {
            Note();
            facts.Frames = null;
            json = SerializeAndRedact(facts, redact, out hits, out categories);
            tokens = CostEstimator.EstimateTokens(json) + 400;
        }

        return tokens <= maxInputTokens;
    }

    private static (int Input, int Output) CountTokens(AiCompletion completion, AiPrompt prompt)
    {
        int input = completion.PromptTokens ??
                    CostEstimator.EstimateTokens(prompt.System) +
                    CostEstimator.EstimateTokens(prompt.User);
        int output = completion.CompletionTokens ?? CostEstimator.EstimateTokens(completion.Content);
        return (Math.Max(0, input), Math.Max(0, output));
    }

    private static double CostOf(AiPreparedRequest prepared, int inputTokens, int outputTokens) =>
        CostEstimator.EstimateCost(
            inputTokens,
            outputTokens,
            prepared.Provider.InputPricePerMillion,
            prepared.Provider.OutputPricePerMillion);

    private static AiAnalysisResult Failure(
        AiReport local,
        AiFailureKind failure,
        string error,
        string? raw,
        List<string> warnings,
        int attempts) =>
        new(false, true, local, failure, error, raw, null, 0, 0, 0, warnings, attempts, false);

    private string? SaveReport(AiReport report)
    {
        try
        {
            Directory.CreateDirectory(_reportsDirectory);
            string name = "report-" + _clock().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff") + ".json";
            string path = Path.Combine(_reportsDirectory, name);
            File.WriteAllText(path, JsonSerializer.Serialize(report, AiReportJsonContext.Default.AiReport));
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteParseLog(string raw, IReadOnlyList<string> errors, string reason)
    {
        try
        {
            Directory.CreateDirectory(_logsDirectory);
            string name = "ai-parse-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log";
            string path = Path.Combine(_logsDirectory, name);
            string sanitized = Redactor.SanitizeError(raw);
            if (sanitized.Length > 8_192)
            {
                sanitized = sanitized[..8_192];
            }

            var builder = new StringBuilder();
            builder.AppendLine("AI 输出解析失败：" + reason);
            foreach (string error in errors)
            {
                builder.AppendLine("- " + Redactor.SanitizeError(error));
            }

            builder.AppendLine();
            builder.AppendLine(sanitized);
            File.WriteAllText(path, builder.ToString());
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string ConsentSignature(
        string providerId,
        string baseUrl,
        string model,
        bool redact,
        bool hideProcessNames)
    {
        string input = string.Join('|', providerId, baseUrl, model, redact ? "1" : "0", hideProcessNames ? "1" : "0");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash, 0, 8);
    }

    public void Dispose() => _client.Dispose();
}
