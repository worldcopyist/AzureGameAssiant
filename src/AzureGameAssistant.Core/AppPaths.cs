namespace AzureGameAssistant.Core;

public static class AppPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AzureGameAssistant");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    public static string SessionsDirectory => Path.Combine(RootDirectory, "sessions");
    public static string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public static string AiDirectory => Path.Combine(RootDirectory, "ai");
    public static string AiReportsDirectory => Path.Combine(AiDirectory, "reports");
    public static string AiUsagePath => Path.Combine(AiDirectory, "usage.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SessionsDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(AiDirectory);
        Directory.CreateDirectory(AiReportsDirectory);
    }
}
