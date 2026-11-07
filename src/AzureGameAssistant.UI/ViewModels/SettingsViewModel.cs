using System.Reflection;
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
    public SettingsViewModel(AppSettings settings, LocalizationService loc, bool autoStartEnabled)
    {
        NormalMs = settings.Sampling.NormalMs;
        GameMs = settings.Sampling.GameMs;
        Theme = settings.Theme;
        Language = settings.Language;
        Notifications = settings.Notifications;
        AutoStart = autoStartEnabled;

        ChartCpu = settings.Charts.Cpu;
        ChartGpu = settings.Charts.Gpu;
        ChartMemory = settings.Charts.Memory;
        ChartVram = settings.Charts.Vram;
        ChartDisk = settings.Charts.Disk;

        OverlayEnabled = settings.Overlay.Enabled;
        OverlayCorner = settings.Overlay.Corner;
        OverlayOffsetX = settings.Overlay.OffsetX;
        OverlayOffsetY = settings.Overlay.OffsetY;
        OverlayFps = settings.Overlay.ShowFps;
        OverlayFrameTimes = settings.Overlay.ShowFrameTimes;
        OverlayCpu = settings.Overlay.ShowCpu;
        OverlayGpu = settings.Overlay.ShowGpu;
        OverlayTemperature = settings.Overlay.ShowTemperature;
        OverlayMemory = settings.Overlay.ShowMemory;
        OverlayVram = settings.Overlay.ShowVram;

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
    }

    public IReadOnlyList<int> IntervalOptions { get; } = [100, 200, 250, 500, 1000, 2000, 5000, 10000];

    public IReadOnlyList<SelectOption> ThemeOptions { get; }

    public IReadOnlyList<SelectOption> LanguageOptions { get; }

    public IReadOnlyList<SelectOption> OverlayCornerOptions { get; }

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
    private bool _overlayMemory;

    [ObservableProperty]
    private bool _overlayVram;

    public void ApplyTo(AppSettings settings)
    {
        settings.Sampling.NormalMs = NormalMs;
        settings.Sampling.GameMs = Math.Min(GameMs, NormalMs);
        settings.Theme = Theme;
        settings.Language = Language;
        settings.Notifications = Notifications;
        settings.AutoStart = AutoStart;

        settings.Charts.Cpu = ChartCpu;
        settings.Charts.Gpu = ChartGpu;
        settings.Charts.Memory = ChartMemory;
        settings.Charts.Vram = ChartVram;
        settings.Charts.Disk = ChartDisk;

        settings.Overlay.Enabled = OverlayEnabled;
        settings.Overlay.Corner = OverlayCorner;
        settings.Overlay.OffsetX = OverlayOffsetX;
        settings.Overlay.OffsetY = OverlayOffsetY;
        settings.Overlay.ShowFps = OverlayFps;
        settings.Overlay.ShowFrameTimes = OverlayFrameTimes;
        settings.Overlay.ShowCpu = OverlayCpu;
        settings.Overlay.ShowGpu = OverlayGpu;
        settings.Overlay.ShowTemperature = OverlayTemperature;
        settings.Overlay.ShowMemory = OverlayMemory;
        settings.Overlay.ShowVram = OverlayVram;
    }
}
