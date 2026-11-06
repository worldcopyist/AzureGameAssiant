using System.Windows;
using Microsoft.Win32;

namespace AzureGameAssistant.UI.Services;

public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ResourceDictionary? _palette;
    private static string _preference = "Auto";
    private static bool _watching;

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Apply(string? theme)
    {
        _preference = Normalize(theme);
        ApplyPalette(ResolveDark(_preference));
        EnsureWatcher();
    }

    public static void SetPreference(string? theme) => Apply(theme);

    private static string Normalize(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        "light" => "Light",
        "dark" => "Dark",
        _ => "Auto",
    };

    private static bool ResolveDark(string preference) => preference switch
    {
        "Light" => false,
        "Dark" => true,
        _ => IsSystemDark(),
    };

    private static bool IsSystemDark()
    {
        object? value = Registry.GetValue(
            @"HKEY_CURRENT_USER\" + PersonalizeKey,
            "AppsUseLightTheme",
            null);

        return value is int light && light == 0;
    }

    private static void ApplyPalette(bool dark)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        var candidate = new ResourceDictionary
        {
            Source = new Uri(
                dark ? "pack://application:,,,/AzureGameAssistant;component/Themes/Dark.xaml"
                     : "pack://application:,,,/AzureGameAssistant;component/Themes/Light.xaml",
                UriKind.Absolute),
        };

        if (_palette is not null)
        {
            int index = app.Resources.MergedDictionaries.IndexOf(_palette);
            if (index >= 0)
            {
                app.Resources.MergedDictionaries[index] = candidate;
            }
            else
            {
                app.Resources.MergedDictionaries.Add(candidate);
            }
        }
        else
        {
            app.Resources.MergedDictionaries.Add(candidate);
        }

        _palette = candidate;
        IsDark = dark;
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void EnsureWatcher()
    {
        if (_watching)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _watching = true;
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        if (!string.Equals(_preference, "Auto", StringComparison.Ordinal))
        {
            return;
        }

        bool dark = IsSystemDark();
        if (dark == IsDark)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => ApplyPalette(dark));
    }
}
