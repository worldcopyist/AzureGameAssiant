using Microsoft.Win32;

namespace AzureGameAssistant.UI.Services;

public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AzureGameAssistant";

    public static bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            string executable = Environment.ProcessPath ?? string.Empty;
            if (executable.Length > 0)
            {
                key.SetValue(ValueName, $"\"{executable}\" --tray");
            }
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
