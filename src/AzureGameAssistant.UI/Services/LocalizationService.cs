using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace AzureGameAssistant.UI.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources = new(
        "AzureGameAssistant.UI.Resources.Strings",
        typeof(LocalizationService).Assembly);

    private string _currentLanguage = "en-US";

    public static LocalizationService Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string CurrentLanguage => _currentLanguage;

    public string this[string key] =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentUICulture, this[key], args);

    public void SetLanguage(string? language)
    {
        CultureInfo culture = Resolve(language);
        CultureInfo.CurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        string code = culture.Name;
        if (string.Equals(code, _currentLanguage, StringComparison.Ordinal))
        {
            return;
        }

        _currentLanguage = code;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static CultureInfo Resolve(string? language)
    {
        string normalized = language?.Trim().ToLowerInvariant() ?? "auto";

        return normalized switch
        {
            "en" or "en-us" => CultureInfo.GetCultureInfo("en-US"),
            "zh" or "zh-cn" => CultureInfo.GetCultureInfo("zh-CN"),
            "auto" => ResolveSystem(),
            _ => CultureInfo.GetCultureInfo("en-US"),
        };
    }

    private static CultureInfo ResolveSystem() =>
        CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo("zh-CN")
            : CultureInfo.GetCultureInfo("en-US");
}
