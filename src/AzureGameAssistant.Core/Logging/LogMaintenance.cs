using System.Text.Json;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Core.Logging;

/// <summary>
/// Time-based log retention. Size rotation already exists inside
/// <see cref="FileLogger"/>; this removes whole log files older than the
/// user-configured number of days so the logs directory stays bounded over
/// long-running installs.
/// </summary>
public static class LogMaintenance
{
    /// <summary>
    /// Helper processes (collector, sensors, MCP) read the same user switch at
    /// startup so disabling logging silences them as well.
    /// </summary>
    public static bool IsLoggingEnabled()
    {
        try
        {
            return new SettingsService().Load().Settings.Logs.Enabled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Helper processes run outside the UI and read the same settings file so a
    /// relocated log directory needs no extra command-line plumbing.
    /// </summary>
    public static (string LogsDirectory, string IssueLogsDirectory) ResolveLogDirectories()
    {
        try
        {
            AppSettings settings = new SettingsService().Load().Settings;
            string logs = LogPaths.ResolveDirectory(settings.Logs.Directory);
            return (logs, LogPaths.ResolveIssueDirectory(settings.Logs.IssueDirectory, logs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (AppPaths.LogsDirectory, AppPaths.IssueLogsDirectory);
        }
    }

    public static int Prune(string directory, int retentionDays, DateTime? utcNow = null)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        int days = Math.Clamp(retentionDays, 1, 30);
        DateTime cutoff = (utcNow ?? DateTime.UtcNow).AddDays(-days);
        int removed = 0;

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff)
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return removed;
    }
}
