using System.Reflection;
using System.Windows.Media;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using OverlayColorParser = AzureGameAssistant.Core.Overlay.OverlayColor;

namespace AzureGameAssistant.UI.ViewModels;

public sealed record SelectOption(string Value, string Display)
{
    public override string ToString() => Display;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LocalizationService _loc;
    private readonly List<AiProviderSettings> _aiProviders;
    private readonly Dictionary<string, AiProviderDraft> _aiDrafts = new(StringComparer.OrdinalIgnoreCase);
    private string _loadedProvider;

    public SettingsViewModel(AppSettings settings, LocalizationService loc, bool autoStartEnabled)
    {
        _loc = loc;
        _aiProviders = settings.Ai.Providers;
        NormalMs = settings.Sampling.NormalMs;
        GameMs = settings.Sampling.GameMs;

        DetectionEnabled = settings.Detection.Enabled;
        DetectionFrameSpikeRatio = settings.Detection.FrameSpikeRatio;
        DetectionFrameSpikeFloorMs = settings.Detection.FrameSpikeFloorMs;
        DetectionDisplayGapMs = settings.Detection.DisplayGapMs;
        DetectionJitterSuspectMs = settings.Detection.JitterSuspectMs;
        DetectionJitterConfirmMs = settings.Detection.JitterConfirmMs;
        DetectionCpuSpikeRatio = settings.Detection.CpuSpikeRatio;
        DetectionGpuSpikePct = settings.Detection.GpuSpikePct;
        DetectionMemFloorMb = settings.Detection.MemAvailableFloorMb;
        DetectionCommitRatio = settings.Detection.CommitRatio;
        DetectionDiskLatencyMs = settings.Detection.DiskLatencyMs;
        DetectionConfirmScore = settings.Detection.ConfirmScore;
        DetectionQuietMs = settings.Detection.QuietMs;
        DetectionCooldownMs = settings.Detection.CooldownMs;

        Theme = settings.Theme;
        Language = settings.Language;
        Hotkey = settings.Hotkey;
        Notifications = settings.Notifications;
        AutoStart = autoStartEnabled;
        RequestElevationOnStartup = settings.RequestElevationOnStartup;
        AutoCapture = settings.Frames.AutoCapture;
        CpuMode = settings.Sensors.CpuMode;

        ChartSeparate = settings.Charts.Separate;
        ChartCpu = settings.Charts.Cpu;
        ChartGpu = settings.Charts.Gpu;
        ChartMemory = settings.Charts.Memory;
        ChartVram = settings.Charts.Vram;
        ChartDisk = settings.Charts.Disk;
        ChartFrames = settings.Charts.Frames;
        LongRunRecord = settings.Diagnostics.LongRunRecord;
        LongRunIntervalSeconds = settings.Diagnostics.IntervalSeconds;
        LogRecording = settings.Logs.Enabled;
        LogRetentionDays = settings.Logs.RetentionDays;
        SessionMaxCount = settings.Sessions.MaxCount;
        SessionMaxTotalMb = settings.Sessions.MaxTotalMb;

        OverlayEnabled = settings.Overlay.Enabled;
        OverlayCorner = settings.Overlay.Corner;
        OverlayOffsetX = settings.Overlay.OffsetX;
        OverlayOffsetY = settings.Overlay.OffsetY;
        OverlayFps = settings.Overlay.ShowFps;
        OverlayFrameTimes = settings.Overlay.ShowFrameTimes;
        OverlayCpu = settings.Overlay.ShowCpu;
        OverlayGpu = settings.Overlay.ShowGpu;
        OverlayTemperature = settings.Overlay.ShowTemperature;
        OverlayCpuTemperature = settings.Overlay.ShowCpuTemperature;
        OverlayMemory = settings.Overlay.ShowMemory;
        OverlayVram = settings.Overlay.ShowVram;
        OverlayTheme = settings.Overlay.Theme;
        OverlayColor = settings.Overlay.Color;
        OverlayOpacity = settings.Overlay.Opacity;
        OverlayBackdropOpacity = settings.Overlay.BackdropOpacity;

        ThemeOptions =
        [
            new SelectOption("Auto", loc["ThemeAuto"]),
            new SelectOption("Light", loc["ThemeLight"]),
            new SelectOption("Dark", loc["ThemeDark"]),
        ];

        LanguageOptions =
        [
            new SelectOption("auto", loc["LanguageAuto"]),
            new SelectOption("zh-CN", loc["Chinese"]),
            new SelectOption("en-US", loc["English"]),
        ];

        OverlayCornerOptions =
        [
            new SelectOption("TopLeft", loc["OverlayTopLeft"]),
            new SelectOption("TopRight", loc["OverlayTopRight"]),
            new SelectOption("BottomLeft", loc["OverlayBottomLeft"]),
            new SelectOption("BottomRight", loc["OverlayBottomRight"]),
        ];

        OverlayThemeOptions =
        [
            new SelectOption("Default", loc["OverlayThemeDefault"]),
            new SelectOption("Light", loc["OverlayThemeLight"]),
            new SelectOption("Azure", loc["OverlayThemeAzure"]),
            new SelectOption("Custom", loc["OverlayThemeCustom"]),
        ];

        CpuModeOptions =
        [
            new SelectOption("acpi", loc["CpuTempModeAcpi"]),
            new SelectOption("driver", loc["CpuTempModeDriver"]),
            new SelectOption("off", loc["CpuTempModeOff"]),
        ];

        AiEnabled = settings.Ai.Enabled;
        AiActiveProvider = settings.Ai.ActiveProvider;
        AiRedact = settings.Ai.Redact;
        AiHideProcessNames = settings.Ai.HideProcessNames;
        AiDailyBudget = settings.Ai.DailyBudget;
        AiMonthlyBudget = settings.Ai.MonthlyBudget;
        AiAutoAnalyze = settings.Ai.AutoAnalyze;
        AiAutoAnalyzeMinScore = settings.Ai.AutoAnalyzeMinScore;

        var providerOptions = AiProviderPresets.All
            .Select(preset => new SelectOption(preset.Id, preset.DisplayName))
            .ToList();
        foreach (AiProviderSettings provider in _aiProviders)
        {
            if (!AiProviderPresets.IsKnown(provider.Id) &&
                providerOptions.All(option => !string.Equals(option.Value, provider.Id, StringComparison.OrdinalIgnoreCase)))
            {
                providerOptions.Add(new SelectOption(provider.Id, provider.Id));
            }
        }

        AiProviderOptions = providerOptions;
        _loadedProvider = AiActiveProvider;
        LoadAiProvider(AiActiveProvider);
    }

    public IReadOnlyList<int> IntervalOptions { get; } = [100, 200, 250, 500, 1000, 2000, 5000, 10000];

    public IReadOnlyList<SelectOption> ThemeOptions { get; }

    public IReadOnlyList<SelectOption> LanguageOptions { get; }

    public IReadOnlyList<SelectOption> OverlayCornerOptions { get; }

    public IReadOnlyList<SelectOption> OverlayThemeOptions { get; }

    public IReadOnlyList<SelectOption> CpuModeOptions { get; }

    public IReadOnlyList<SelectOption> AiProviderOptions { get; }

    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    public string LogsPath => AppPaths.LogsDirectory;

    public string DataPath => AppPaths.RootDirectory;

    [ObservableProperty]
    private int _normalMs;

    [ObservableProperty]
    private int _gameMs;

    [ObservableProperty]
    private bool _detectionEnabled;

    [ObservableProperty]
    private double _detectionFrameSpikeRatio;

    [ObservableProperty]
    private double _detectionFrameSpikeFloorMs;

    [ObservableProperty]
    private double _detectionDisplayGapMs;

    [ObservableProperty]
    private double _detectionJitterSuspectMs;

    [ObservableProperty]
    private double _detectionJitterConfirmMs;

    [ObservableProperty]
    private double _detectionCpuSpikeRatio;

    [ObservableProperty]
    private double _detectionGpuSpikePct;

    [ObservableProperty]
    private double _detectionMemFloorMb;

    [ObservableProperty]
    private double _detectionCommitRatio;

    [ObservableProperty]
    private double _detectionDiskLatencyMs;

    [ObservableProperty]
    private double _detectionConfirmScore;

    [ObservableProperty]
    private double _detectionQuietMs;

    [ObservableProperty]
    private double _detectionCooldownMs;

    [ObservableProperty]
    private string _theme = "Auto";

    [ObservableProperty]
    private string _language = "auto";

    [ObservableProperty]
    private string _hotkey = HotkeyService.DefaultGesture;

    [ObservableProperty]
    private bool _notifications;

    [ObservableProperty]
    private bool _autoStart;

    [ObservableProperty]
    private bool _requestElevationOnStartup;

    [ObservableProperty]
    private bool _autoCapture;

    [ObservableProperty]
    private string _cpuMode = "acpi";

    [ObservableProperty]
    private bool _aiEnabled;

    [ObservableProperty]
    private string _aiActiveProvider = "deepseek";

    [ObservableProperty]
    private string _aiBaseUrl = string.Empty;

    [ObservableProperty]
    private string _aiModel = string.Empty;

    [ObservableProperty]
    private double _aiInputPricePerMillion;

    [ObservableProperty]
    private double _aiOutputPricePerMillion;

    [ObservableProperty]
    private bool _aiRedact = true;

    [ObservableProperty]
    private bool _aiHideProcessNames;

    [ObservableProperty]
    private double _aiDailyBudget;

    [ObservableProperty]
    private double _aiMonthlyBudget;

    [ObservableProperty]
    private bool _aiAutoAnalyze;

    [ObservableProperty]
    private double _aiAutoAnalyzeMinScore = 80;

    [ObservableProperty]
    private bool _aiRequiresKey = true;

    [ObservableProperty]
    private string _aiKeyStatus = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<string> _aiModelOptions = Array.Empty<string>();

    [ObservableProperty]
    private string _aiModelsStatus = string.Empty;

    [ObservableProperty]
    private bool _aiModelsBusy;

    [ObservableProperty]
    private string _aiTestStatus = string.Empty;

    [ObservableProperty]
    private bool _aiTesting;

    public bool AiKeyInputEnabled => AiRequiresKey;

    public bool CanTestAiConnection => !AiTesting;

    public bool CanFetchAiModels => !AiModelsBusy;

    public bool HasAiModelOptions => AiModelOptions.Count > 0;

    [ObservableProperty]
    private bool _chartSeparate;

    [ObservableProperty]
    private bool _chartCpu;

    [ObservableProperty]
    private bool _chartGpu;

    [ObservableProperty]
    private bool _chartMemory;

    [ObservableProperty]
    private bool _chartVram;

    [ObservableProperty]
    private bool _chartDisk;

    [ObservableProperty]
    private bool _chartFrames;

    public IReadOnlyList<int> LongRunIntervalOptions { get; } = [5, 10, 30];

    [ObservableProperty]
    private bool _longRunRecord;

    [ObservableProperty]
    private int _longRunIntervalSeconds = 5;

    public IReadOnlyList<int> LogRetentionOptions { get; } = [1, 2, 3, 5, 7, 14, 30];

    [ObservableProperty]
    private bool _logRecording = true;

    [ObservableProperty]
    private int _logRetentionDays = 3;

    [ObservableProperty]
    private int _sessionMaxCount = 20;

    [ObservableProperty]
    private int _sessionMaxTotalMb = 500;

    [ObservableProperty]
    private bool _overlayEnabled;

    [ObservableProperty]
    private string _overlayCorner = "TopLeft";

    [ObservableProperty]
    private int _overlayOffsetX;

    [ObservableProperty]
    private int _overlayOffsetY;

    [ObservableProperty]
    private bool _overlayFps;

    [ObservableProperty]
    private bool _overlayFrameTimes;

    [ObservableProperty]
    private bool _overlayCpu;

    [ObservableProperty]
    private bool _overlayGpu;

    [ObservableProperty]
    private bool _overlayTemperature;

    [ObservableProperty]
    private bool _overlayCpuTemperature;

    [ObservableProperty]
    private bool _overlayMemory;

    [ObservableProperty]
    private bool _overlayVram;

    [ObservableProperty]
    private string _overlayTheme = "Default";

    [ObservableProperty]
    private string _overlayColor = "#FFFFFF";

    [ObservableProperty]
    private int _overlayOpacity = 100;

    [ObservableProperty]
    private int _overlayBackdropOpacity;

    public bool IsOverlayCustomTheme => string.Equals(OverlayTheme, "Custom", StringComparison.Ordinal);

    public Brush OverlayColorPreview =>
        OverlayColorParser.TryParse(OverlayColor, out (byte A, byte R, byte G, byte B) color)
            ? FrozenBrush(Color.FromArgb(color.A, color.R, color.G, color.B))
            : Brushes.Transparent;

    partial void OnOverlayThemeChanged(string value) => OnPropertyChanged(nameof(IsOverlayCustomTheme));

    partial void OnOverlayColorChanged(string value) => OnPropertyChanged(nameof(OverlayColorPreview));

    partial void OnAiActiveProviderChanged(string value)
    {
        CaptureAiProvider(_loadedProvider);
        _loadedProvider = value;
        LoadAiProvider(value);
    }

    partial void OnAiRequiresKeyChanged(bool value) => OnPropertyChanged(nameof(AiKeyInputEnabled));

    partial void OnAiModelsBusyChanged(bool value) => OnPropertyChanged(nameof(CanFetchAiModels));

    partial void OnAiTestingChanged(bool value) => OnPropertyChanged(nameof(CanTestAiConnection));

    partial void OnAiModelOptionsChanged(IReadOnlyList<string> value) =>
        OnPropertyChanged(nameof(HasAiModelOptions));

    public void RefreshKeyStatus()
    {
        if (!AiRequiresKey)
        {
            AiKeyStatus = _loc["AiKeyNotRequired"];
            return;
        }

        AiKeyStatus = CredentialStore.Shared.Exists(AiActiveProvider)
            ? _loc["AiKeySavedStatus"]
            : _loc["AiKeyMissingStatus"];
    }

    private void LoadAiProvider(string? id)
    {
        string key = (id ?? string.Empty).Trim().ToLowerInvariant();
        AiProviderPreset preset = AiProviderPresets.Resolve(key);
        AiModelOptions = Array.Empty<string>();
        AiModelsStatus = string.Empty;
        AiTestStatus = string.Empty;

        if (_aiDrafts.TryGetValue(key, out AiProviderDraft draft))
        {
            AiBaseUrl = draft.BaseUrl;
            AiModel = draft.Model;
            AiRequiresKey = draft.RequiresKey;
            AiInputPricePerMillion = draft.InputPrice;
            AiOutputPricePerMillion = draft.OutputPrice;
            RefreshKeyStatus();
            return;
        }

        AiProviderSettings? configured = _aiProviders.FirstOrDefault(provider =>
            string.Equals(provider.Id, key, StringComparison.OrdinalIgnoreCase));

        AiBaseUrl = !string.IsNullOrWhiteSpace(configured?.BaseUrl) ? configured!.BaseUrl : preset.BaseUrl;
        AiModel = !string.IsNullOrWhiteSpace(configured?.Model) ? configured!.Model : preset.SuggestedModel;
        AiRequiresKey = AiProviderPresets.IsKnown(key) ? preset.RequiresKey : configured?.RequiresKey ?? true;
        AiInputPricePerMillion = configured?.InputPricePerMillion ?? 0;
        AiOutputPricePerMillion = configured?.OutputPricePerMillion ?? 0;
        RefreshKeyStatus();
    }

    private void CaptureAiProvider(string? id)
    {
        string key = (id ?? string.Empty).Trim().ToLowerInvariant();
        string baseUrl = (AiBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (key.Length == 0 || baseUrl.Length == 0)
        {
            return;
        }

        _aiDrafts[key] = new AiProviderDraft(
            baseUrl,
            (AiModel ?? string.Empty).Trim(),
            AiRequiresKey,
            double.IsFinite(AiInputPricePerMillion) ? Math.Max(0, AiInputPricePerMillion) : 0,
            double.IsFinite(AiOutputPricePerMillion) ? Math.Max(0, AiOutputPricePerMillion) : 0);
    }

    private void FlushAiProviders()
    {
        foreach ((string id, AiProviderDraft draft) in _aiDrafts)
        {
            AiProviderSettings? entry = _aiProviders.FirstOrDefault(provider =>
                string.Equals(provider.Id, id, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                entry = new AiProviderSettings { Id = id };
                _aiProviders.Add(entry);
            }

            entry.BaseUrl = draft.BaseUrl;
            entry.Model = draft.Model;
            entry.RequiresKey = draft.RequiresKey;
            entry.InputPricePerMillion = draft.InputPrice;
            entry.OutputPricePerMillion = draft.OutputPrice;
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.Sampling.NormalMs = NormalMs;
        settings.Sampling.GameMs = Math.Min(GameMs, NormalMs);

        settings.Detection.Enabled = DetectionEnabled;
        settings.Detection.FrameSpikeRatio = Clamp(DetectionFrameSpikeRatio, 1.2, 10, 2.5);
        settings.Detection.FrameSpikeFloorMs = Clamp(DetectionFrameSpikeFloorMs, 10, 500, 50);
        settings.Detection.DisplayGapMs = Clamp(DetectionDisplayGapMs, 20, 1000, 100);
        settings.Detection.JitterSuspectMs = (int)Math.Round(Clamp(DetectionJitterSuspectMs, 10, 500, 50));
        settings.Detection.JitterConfirmMs = (int)Math.Round(Clamp(DetectionJitterConfirmMs, 20, 2000, 100));
        settings.Detection.CpuSpikeRatio = Clamp(DetectionCpuSpikeRatio, 1.1, 10, 2.5);
        settings.Detection.GpuSpikePct = Clamp(DetectionGpuSpikePct, 50, 100, 85);
        settings.Detection.MemAvailableFloorMb = (int)Math.Round(Clamp(DetectionMemFloorMb, 256, 65_536, 1_024));
        settings.Detection.CommitRatio = Clamp(DetectionCommitRatio, 0.5, 1, 0.90);
        settings.Detection.DiskLatencyMs = Clamp(DetectionDiskLatencyMs, 5, 1000, 50);
        settings.Detection.ConfirmScore = (int)Math.Round(Clamp(DetectionConfirmScore, 10, 100, 40));
        settings.Detection.QuietMs = (int)Math.Round(Clamp(DetectionQuietMs, 500, 10_000, 2_000));
        settings.Detection.CooldownMs = (int)Math.Round(Clamp(DetectionCooldownMs, 0, 60_000, 5_000));

        settings.Theme = Theme;
        settings.Language = Language;
        settings.Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? HotkeyService.DefaultGesture : Hotkey.Trim();
        settings.Notifications = Notifications;
        settings.AutoStart = AutoStart;
        settings.RequestElevationOnStartup = RequestElevationOnStartup;
        settings.Frames.AutoCapture = AutoCapture;
        settings.Sensors.CpuMode = CpuMode;

        settings.Charts.Separate = ChartSeparate;
        settings.Charts.Cpu = ChartCpu;
        settings.Charts.Gpu = ChartGpu;
        settings.Charts.Memory = ChartMemory;
        settings.Charts.Vram = ChartVram;
        settings.Charts.Disk = ChartDisk;
        settings.Charts.Frames = ChartFrames;
        settings.Diagnostics.LongRunRecord = LongRunRecord;
        settings.Diagnostics.IntervalSeconds = LongRunIntervalSeconds;
        settings.Logs.Enabled = LogRecording;
        settings.Logs.RetentionDays = LogRetentionDays;
        settings.Sessions.MaxCount = (int)Math.Round(Clamp(SessionMaxCount, 1, 500, 20));
        settings.Sessions.MaxTotalMb = (int)Math.Round(Clamp(SessionMaxTotalMb, 10, 10_000, 500));

        settings.Overlay.Enabled = OverlayEnabled;
        settings.Overlay.Corner = OverlayCorner;
        settings.Overlay.OffsetX = OverlayOffsetX;
        settings.Overlay.OffsetY = OverlayOffsetY;
        settings.Overlay.ShowFps = OverlayFps;
        settings.Overlay.ShowFrameTimes = OverlayFrameTimes;
        settings.Overlay.ShowCpu = OverlayCpu;
        settings.Overlay.ShowGpu = OverlayGpu;
        settings.Overlay.ShowTemperature = OverlayTemperature;
        settings.Overlay.ShowCpuTemperature = OverlayCpuTemperature;
        settings.Overlay.ShowMemory = OverlayMemory;
        settings.Overlay.ShowVram = OverlayVram;
        settings.Overlay.Theme = OverlayTheme;
        settings.Overlay.Color = OverlayColor;
        settings.Overlay.Opacity = (int)Math.Round(Clamp(OverlayOpacity, 30, 100, 100));
        settings.Overlay.BackdropOpacity = (int)Math.Round(Clamp(OverlayBackdropOpacity, 0, 80, 0));

        settings.Ai.Enabled = AiEnabled;
        settings.Ai.ActiveProvider = (AiActiveProvider ?? string.Empty).Trim().ToLowerInvariant();
        settings.Ai.Redact = AiRedact;
        settings.Ai.HideProcessNames = AiHideProcessNames;
        settings.Ai.DailyBudget = double.IsFinite(AiDailyBudget) ? Math.Max(0, AiDailyBudget) : 0;
        settings.Ai.MonthlyBudget = double.IsFinite(AiMonthlyBudget) ? Math.Max(0, AiMonthlyBudget) : 0;
        settings.Ai.AutoAnalyze = AiAutoAnalyze;
        settings.Ai.AutoAnalyzeMinScore = double.IsFinite(AiAutoAnalyzeMinScore)
            ? (int)Math.Clamp(Math.Round(AiAutoAnalyzeMinScore), 0, 100)
            : 80;
        CaptureAiProvider(AiActiveProvider);
        FlushAiProviders();
    }

    private readonly record struct AiProviderDraft(
        string BaseUrl,
        string Model,
        bool RequiresKey,
        double InputPrice,
        double OutputPrice);

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static SolidColorBrush FrozenBrush(in Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
