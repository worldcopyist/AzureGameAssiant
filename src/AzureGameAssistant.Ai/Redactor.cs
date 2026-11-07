using System.Text.RegularExpressions;

namespace AzureGameAssistant.Ai;

public sealed record RedactionResult(string Json, int Hits, IReadOnlyList<string> Categories);

/// <summary>
/// Applies the last-mile redaction pass to the serialized fact layer, so the
/// preview and the request body are byte-identical. Process names are hidden
/// earlier, during fact construction.
/// </summary>
public static class Redactor
{
    private const string ReplacementPath = "[redacted-path]";
    private const string ReplacementUser = "[redacted-user]";
    private const string ReplacementMachine = "[redacted-machine]";
    private const string ReplacementIp = "[redacted-ip]";
    private const string ReplacementMac = "[redacted-mac]";
    private const string ReplacementEmail = "[redacted-email]";

    private static readonly Regex DrivePath = new(
        @"(?<![A-Za-z])[A-Za-z]:[\\/](?:[^""\\/\r\n]|[\\/])*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UncPath = new(
        @"\\\\[^""\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IpAddress = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MacAddress = new(
        @"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Email = new(
        @"\b[\w.+-]+@[\w-]+\.[\w.-]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RedactionResult Apply(string json, bool enabled)
    {
        if (!enabled)
        {
            return new RedactionResult(json, 0, []);
        }

        var categories = new List<string>();
        string result = json;
        int hits = 0;

        result = Replace(DrivePath, result, ReplacementPath, "path", categories, ref hits);
        result = Replace(UncPath, result, ReplacementPath, "path", categories, ref hits);

        string user = Environment.UserName;
        if (!string.IsNullOrWhiteSpace(user))
        {
            var userRegex = new Regex(Regex.Escape(user), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Replace(userRegex, result, ReplacementUser, "user", categories, ref hits);
        }

        string machine = Environment.MachineName;
        if (!string.IsNullOrWhiteSpace(machine))
        {
            var machineRegex = new Regex(Regex.Escape(machine), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Replace(machineRegex, result, ReplacementMachine, "machine", categories, ref hits);
        }

        result = Replace(MacAddress, result, ReplacementMac, "mac", categories, ref hits);
        result = Replace(IpAddress, result, ReplacementIp, "ip", categories, ref hits);
        result = Replace(Email, result, ReplacementEmail, "email", categories, ref hits);

        return new RedactionResult(result, hits, categories.Distinct().ToList());
    }

    private static string Replace(
        Regex regex,
        string input,
        string replacement,
        string category,
        List<string> categories,
        ref int hits)
    {
        int count = regex.Matches(input).Count;
        if (count == 0)
        {
            return input;
        }

        hits += count;
        categories.Add(category);
        return regex.Replace(input, replacement);
    }

    private static readonly Regex SecretToken = new(
        @"sk-[A-Za-z0-9_\-]{6,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerToken = new(
        @"Bearer\s+[A-Za-z0-9_\-\.]{6,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex KeyField = new(
        @"""(?:api[_-]?key|authorization|x-api-key)""\s*:\s*""[^""]*""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Removes credential-shaped text from error messages and logs.
    /// </summary>
    public static string SanitizeError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        string result = SecretToken.Replace(message, "sk-***");
        result = BearerToken.Replace(result, "Bearer ***");
        result = KeyField.Replace(result, "\"apiKey\":\"***\"");
        return result;
    }
}
