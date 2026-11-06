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
    }

    public IReadOnlyList<int> IntervalOptions { get; } = [100, 200, 250, 500, 1000, 2000, 5000, 10000];

    public IReadOnlyList<SelectOption> ThemeOptions { get; }

    public IReadOnlyList<SelectOption> LanguageOptions { get; }

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

    public void ApplyTo(AppSettings settings)
    {
        settings.Sampling.NormalMs = NormalMs;
        settings.Sampling.GameMs = Math.Min(GameMs, NormalMs);
        settings.Theme = Theme;
        settings.Language = Language;
        settings.Notifications = Notifications;
        settings.AutoStart = AutoStart;
    }
}
