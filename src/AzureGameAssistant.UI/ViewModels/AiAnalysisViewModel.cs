using System.Collections.ObjectModel;
using System.Globalization;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureGameAssistant.UI.ViewModels;

public sealed record AiEvidenceRow(string Metric, string Value, string Interpretation);

public sealed record AiSuspectRow(string Name, string Role, string Reason);

public sealed record AiDropRow(string TimeText, string CauseText, string DetailText, string SuspectText, bool HasSuspect);

public sealed record AiHighLoadRow(string ResourceText, string ProcessText, string DetailText);

public sealed record AiRecommendationRow(
    string Action,
    string Risk,
    string ExpectedText,
    string HowText,
    string? RollbackText)
{
    public bool HasRollback => !string.IsNullOrWhiteSpace(RollbackText);
}

public sealed partial class AiAnalysisViewModel : ObservableObject
{
    private readonly LocalizationService _loc;

    public AiAnalysisViewModel(
        AppSettings settings,
        LocalizationService loc,
        AiPreparedRequest prepared,
        bool hasData)
    {
        _loc = loc;
        HasData = hasData;

        Signature = AiAnalysisService.ConsentSignature(
            prepared.Provider.Id,
            prepared.Provider.BaseUrl,
            prepared.Provider.Model,
            settings.Ai.Redact,
            settings.Ai.HideProcessNames);

        ProviderText = prepared.Provider.DisplayName + " · " + prepared.Provider.Model;
        EndpointText = prepared.Endpoint?.ToString() ?? "—";
        TokenText = loc.Format("AiTokenSummary", prepared.InputTokens, prepared.MaxOutputTokens);

        bool priced = prepared.Provider.InputPricePerMillion > 0 ||
                      prepared.Provider.OutputPricePerMillion > 0;
        CostText = priced
            ? loc.Format("AiCostEstimate", "$" + prepared.EstimatedCost.ToString("F4", CultureInfo.InvariantCulture))
            : loc["AiCostUnknown"];

        BudgetText = FormatBudget(prepared, loc);
        RedactionText = prepared.RedactionHits > 0
            ? loc.Format("AiRedactionHits", prepared.RedactionHits, string.Join(", ", prepared.RedactionCategories))
            : loc["AiRedactionNone"];
        KeyText = !prepared.Provider.RequiresKey
            ? loc["AiKeyNotRequired"]
            : prepared.KeyAvailable ? loc["AiKeySavedStatus"] : loc["AiKeyMissingStatus"];

        HasConfigError = prepared.ConfigurationError is not null;
        ConfigErrorText = prepared.ConfigurationError is { } error ? loc.Format("AiConfigError", error) : string.Empty;
        ConsentText = loc.Format("AiConsentLabel", prepared.Provider.DisplayName);
        ConsentRequired = !HasConfigError &&
                          !string.Equals(settings.Ai.ConsentSignature, Signature, StringComparison.Ordinal);
        PreviewJson = prepared.FactsJson;
        StatusText = loc["AiReady"];
        RunText = HasConfigError ? loc["AiRunLocal"] : loc["AiRun"];
        UpdateCanRun();
    }

    public string Signature { get; }

    public string PreviewJson { get; }

    public string ProviderText { get; }

    public string EndpointText { get; }

    public string TokenText { get; }

    public string CostText { get; }

    public string BudgetText { get; }

    public string RedactionText { get; }

    public string KeyText { get; }

    public string ConsentText { get; }

    public string RunText { get; }

    public bool HasData { get; }

    public bool HasConfigError { get; }

    public string ConfigErrorText { get; }

    public string NoDataText => _loc["AiNoData"];

    public ObservableCollection<AiEvidenceRow> Evidence { get; } = [];

    public ObservableCollection<AiSuspectRow> Suspects { get; } = [];

    public ObservableCollection<AiDropRow> DropRows { get; } = [];

    public ObservableCollection<AiHighLoadRow> HighLoadRows { get; } = [];

    public ObservableCollection<AiRecommendationRow> Recommendations { get; } = [];

    [ObservableProperty]
    private bool _consentRequired;

    [ObservableProperty]
    private bool _consentChecked;

    [ObservableProperty]
    private bool _canRun;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private bool _usedLocalFallback;

    [ObservableProperty]
    private string _fallbackText = string.Empty;

    [ObservableProperty]
    private string _summaryText = string.Empty;

    [ObservableProperty]
    private string _causeText = string.Empty;

    [ObservableProperty]
    private bool _hasFrameAnalysis;

    [ObservableProperty]
    private string _frameText = string.Empty;

    [ObservableProperty]
    private bool _hasDropAnalysis;

    [ObservableProperty]
    private bool _hasHighLoadAttribution;

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private string _warningsText = string.Empty;

    [ObservableProperty]
    private bool _hasLimitations;

    [ObservableProperty]
    private string _limitationsText = string.Empty;

    [ObservableProperty]
    private bool _hasDisclaimer;

    [ObservableProperty]
    private string _disclaimerText = string.Empty;

    [ObservableProperty]
    private bool _hasSavedPath;

    [ObservableProperty]
    private string _savedText = string.Empty;

    partial void OnIsRunningChanged(bool value) => UpdateCanRun();

    partial void OnConsentCheckedChanged(bool value) => UpdateCanRun();

    partial void OnConsentRequiredChanged(bool value) => UpdateCanRun();

    public void BeginRun()
    {
        IsRunning = true;
        ProgressText = _loc["AiRunning"];
        StatusText = _loc["AiRunning"];
        UpdateCanRun();
    }

    public void ReportProgress(int characters) =>
        ProgressText = _loc.Format("AiReceiving", characters);

    public void SetStatus(string status) => StatusText = status;

    public void ApplyCancelled()
    {
        IsRunning = false;
        StatusText = _loc["AiCancelled"];
        UpdateCanRun();
    }

    public void ApplyError(string error)
    {
        IsRunning = false;
        StatusText = error;
        UpdateCanRun();
    }

    public void ApplyResult(AiAnalysisResult result)
    {
        IsRunning = false;
        HasResult = true;
        UsedLocalFallback = result.UsedLocalFallback;
        FallbackText = result.Error is { } error
            ? _loc["AiLocalFallback"] + " · " + error
            : _loc["AiLocalFallback"];

        AiReport report = result.Report;
        SummaryText = report.Summary;
        CauseText = LocalizeCause(report.PrimaryCause) + " · " +
                    _loc.Format("AiConfidence", LocalizeConfidence(report.Confidence));

        Evidence.Clear();
        foreach (AiEvidence evidence in report.Evidence)
        {
            Evidence.Add(new AiEvidenceRow(evidence.Metric, evidence.Value, evidence.Interpretation));
        }

        Suspects.Clear();
        foreach (AiSuspect suspect in report.Suspects)
        {
            Suspects.Add(new AiSuspectRow(suspect.Name, LocalizeRole(suspect.Role), suspect.Reason));
        }

        AiFrameAnalysis? frame = report.FrameAnalysis;
        HasFrameAnalysis = frame is not null && !string.IsNullOrWhiteSpace(frame.Summary);
        FrameText = frame is null ? string.Empty : BuildFrameText(frame);

        DropRows.Clear();
        foreach (AiDropAnalysis drop in report.DropAnalysis)
        {
            bool hasSuspect = !string.IsNullOrWhiteSpace(drop.SuspectProcess);
            DropRows.Add(new AiDropRow(
                _loc.Format("AiDropWhen", drop.SecondsAgo.ToString("0.#", CultureInfo.InvariantCulture)),
                _loc["AiDropCause" + CauseKey(drop.Cause)],
                drop.Explanation,
                hasSuspect ? _loc.Format("AiDropSuspect", drop.SuspectProcess) : string.Empty,
                hasSuspect));
        }

        HasDropAnalysis = DropRows.Count > 0;

        HighLoadRows.Clear();
        foreach (AiHighLoadAttribution load in report.HighLoadAttribution)
        {
            HighLoadRows.Add(new AiHighLoadRow(
                _loc["AiResource" + ResourceKey(load.Resource)],
                _loc.Format("AiHighLoadProcess", load.Process),
                load.Explanation));
        }

        HasHighLoadAttribution = HighLoadRows.Count > 0;

        Recommendations.Clear();
        foreach (AiRecommendation recommendation in report.Recommendations)
        {
            Recommendations.Add(new AiRecommendationRow(
                recommendation.Action,
                LocalizeRisk(recommendation.Risk),
                _loc["AiExpectedEffect"] + "：" + recommendation.ExpectedEffect,
                _loc["AiHowTo"] + "：" + recommendation.HowTo,
                string.IsNullOrWhiteSpace(recommendation.Rollback)
                    ? null
                    : _loc["AiRollback"] + "：" + recommendation.Rollback));
        }

        HasWarnings = result.Warnings.Count > 0;
        WarningsText = string.Join(Environment.NewLine, result.Warnings);

        HasLimitations = report.Limitations.Count > 0;
        LimitationsText = string.Join(Environment.NewLine, report.Limitations);

        HasDisclaimer = !string.IsNullOrWhiteSpace(report.Disclaimer);
        DisclaimerText = report.Disclaimer;

        HasSavedPath = !string.IsNullOrEmpty(result.SavedPath);
        SavedText = HasSavedPath ? _loc.Format("AiSavedReport", result.SavedPath) : string.Empty;

        StatusText = _loc["AiResultTitle"];
        UpdateCanRun();
    }

    private void UpdateCanRun() =>
        CanRun = HasData && !IsRunning && (!ConsentRequired || ConsentChecked);

    private string BuildFrameText(AiFrameAnalysis frame)
    {
        var parts = new List<string> { frame.Summary };
        var flags = new List<string>();
        if (frame.CpuBound)
        {
            flags.Add(_loc["AiFrameCpuBound"]);
        }

        if (frame.GpuBound)
        {
            flags.Add(_loc["AiFrameGpuBound"]);
        }

        if (frame.PacingIssue)
        {
            flags.Add(_loc["AiFramePacing"]);
        }

        if (flags.Count > 0)
        {
            parts.Add(string.Join(" · ", flags));
        }

        return string.Join(Environment.NewLine, parts);
    }

    private static string CauseKey(string cause) => cause switch
    {
        "long-stall" => "LongStall",
        "gpu-wait" => "GpuWait",
        "cpu-wait" => "CpuWait",
        "mixed" => "Mixed",
        "shader-compilation" => "Shader",
        "asset-streaming" => "Streaming",
        "thermal-throttle" => "Thermal",
        "vram-pressure" => "Vram",
        "memory-pressure" => "Memory",
        "disk-stall" => "Disk",
        _ => "Unknown",
    };

    private static string ResourceKey(string resource) => resource switch
    {
        "gpu" => "Gpu",
        "vram" => "Vram",
        "memory" => "Memory",
        "disk" => "Disk",
        _ => "Cpu",
    };

    private string LocalizeCause(string cause)
    {
        string key = "AiCause" + cause;
        string text = _loc[key];
        return string.Equals(text, key, StringComparison.Ordinal) ? cause : text;
    }

    private string LocalizeConfidence(string confidence) => confidence switch
    {
        "high" => _loc["AiConfidenceHigh"],
        "medium" => _loc["AiConfidenceMedium"],
        _ => _loc["AiConfidenceLow"],
    };

    private string LocalizeRisk(string risk) => risk switch
    {
        "high" => _loc["AiRiskHigh"],
        "medium" => _loc["AiRiskMedium"],
        _ => _loc["AiRiskLow"],
    };

    private string LocalizeRole(string role) => role switch
    {
        "foreground" => _loc["AiRoleForeground"],
        "background" => _loc["AiRoleBackground"],
        _ => _loc["AiRoleOther"],
    };

    private static string FormatBudget(AiPreparedRequest prepared, LocalizationService loc)
    {
        if (prepared.DailyBudget <= 0 && prepared.MonthlyBudget <= 0)
        {
            return loc["AiBudgetUnlimited"];
        }

        string dayLimit = prepared.DailyBudget > 0
            ? "$" + prepared.DailyBudget.ToString("F2", CultureInfo.InvariantCulture)
            : loc["AiBudgetUnlimited"];
        string monthLimit = prepared.MonthlyBudget > 0
            ? "$" + prepared.MonthlyBudget.ToString("F2", CultureInfo.InvariantCulture)
            : loc["AiBudgetUnlimited"];

        return loc.Format(
            "AiBudgetLine",
            "$" + prepared.Budget.DayCost.ToString("F2", CultureInfo.InvariantCulture),
            dayLimit,
            "$" + prepared.Budget.MonthCost.ToString("F2", CultureInfo.InvariantCulture),
            monthLimit);
    }
}
