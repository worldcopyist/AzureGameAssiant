namespace AzureGameAssistant.Core.Logging;

/// <summary>
/// Resolves the user-configured log locations. Empty means "use the default";
/// only absolute paths are accepted so a relative value in settings can never
/// scatter logs into an unexpected working directory.
/// </summary>
public static class LogPaths
{
    public static string ResolveDirectory(string? configured) =>
        TryNormalize(configured, out string normalized) && normalized.Length > 0
            ? normalized
            : AppPaths.LogsDirectory;

    public static string ResolveIssueDirectory(string? configured, string logsDirectory) =>
        TryNormalize(configured, out string normalized) && normalized.Length > 0
            ? normalized
            : Path.Combine(ResolveDirectory(logsDirectory), "issues");

    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        try
        {
            // Validate before expansion: ExpandEnvironmentVariables stops at a NUL,
            // which would otherwise hide invalid characters from the check below.
            string trimmedInput = input.Trim();
            string expanded = Environment.ExpandEnvironmentVariables(trimmedInput);
            if (HasInvalidCharacters(trimmedInput) || !Path.IsPathRooted(expanded))
            {
                return false;
            }

            string full = Path.GetFullPath(expanded);
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool driveRoot = trimmed.Length == 2 && trimmed[1] == ':';
            normalized = trimmed.Length <= 1 || driveRoot ? full : trimmed;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool HasInvalidCharacters(string path)
    {
        bool extended = path.StartsWith(@"\\?\", StringComparison.Ordinal);
        foreach (char ch in path)
        {
            if (ch < 0x20)
            {
                return true;
            }

            if (!extended && ch is '"' or '<' or '>' or '|' or '*' or '?')
            {
                return true;
            }
        }

        return false;
    }
}
