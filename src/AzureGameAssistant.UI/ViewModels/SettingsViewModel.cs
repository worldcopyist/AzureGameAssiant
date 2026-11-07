using System.Globalization;
using System.Reflection;
using System.Windows.Media;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

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
        Theme = settings.Theme;
        Language = settings.Language;
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
    private string _theme = "Auto";

    [ObservableProperty]
    private string _language = "auto";

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
    private bool _aiRedact = true;

    [ObservableProperty]
    private bool _aiHideProcessNames;

    [ObservableProperty]
    private double _aiDailyBudget;

    [ObservableProperty]
    private double _aiMonthlyBudget;

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

    public bool AiKeyInputEnabled => AiRequiresKey;

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

    public bool IsOverlayCustomTheme => string.Equals(OverlayTheme, "Custom", StringComparison.Ordinal);

    public Brush OverlayColorPreview =>
        TryParseColor(OverlayColor, out Color color)
            ? FrozenBrush(color)
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

        if (_aiDrafts.TryGetValue(key, out AiProviderDraft draft))
        {
            AiBaseUrl = draft.BaseUrl;
            AiModel = draft.Model;
            AiRequiresKey = draft.RequiresKey;
            RefreshKeyStatus();
            return;
        }

        AiProviderSettings? configured = _aiProviders.FirstOrDefault(provider =>
            string.Equals(provider.Id, key, StringComparison.OrdinalIgnoreCase));

        AiBaseUrl = !string.IsNullOrWhiteSpace(configured?.BaseUrl) ? configured!.BaseUrl : preset.BaseUrl;
        AiModel = !string.IsNullOrWhiteSpace(configured?.Model) ? configured!.Model : preset.SuggestedModel;
        AiRequiresKey = AiProviderPresets.IsKnown(key) ? preset.RequiresKey : configured?.RequiresKey ?? true;
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

        _aiDrafts[key] = new AiProviderDraft(baseUrl, (AiModel ?? string.Empty).Trim(), AiRequiresKey);
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
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.Sampling.NormalMs = NormalMs;
        settings.Sampling.GameMs = Math.Min(GameMs, NormalMs);
        settings.Theme = Theme;
        settings.Language = Language;
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

        settings.Ai.Enabled = AiEnabled;
        settings.Ai.ActiveProvider = (AiActiveProvider ?? string.Empty).Trim().ToLowerInvariant();
        settings.Ai.Redact = AiRedact;
        settings.Ai.HideProcessNames = AiHideProcessNames;
        settings.Ai.DailyBudget = double.IsFinite(AiDailyBudget) ? Math.Max(0, AiDailyBudget) : 0;
        settings.Ai.MonthlyBudget = double.IsFinite(AiMonthlyBudget) ? Math.Max(0, AiMonthlyBudget) : 0;
        CaptureAiProvider(AiActiveProvider);
        FlushAiProviders();
    }

    private readonly record struct AiProviderDraft(string BaseUrl, string Model, bool RequiresKey);

    private static bool TryParseColor(string? text, out Color color)
    {
        color = Colors.White;
        string value = (text ?? string.Empty).Trim().TrimStart('#');
        if (value.Length != 6 ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static SolidColorBrush FrozenBrush(in Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
