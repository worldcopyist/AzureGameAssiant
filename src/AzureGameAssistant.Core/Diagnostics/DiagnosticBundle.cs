using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AzureGameAssistant.Core.Hardware;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Core.Diagnostics;

/// <summary>
/// Removes anything that could identify the machine or leak credentials before
/// diagnostics leave the local diagnostics directory.
/// </summary>
public static partial class DiagnosticRedactor
{
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        text = UserProfilePattern().Replace(text, "%USERPROFILE%");
        text = UncPathPattern().Replace(text, "<unc-path>");
        text = DrivePathPattern().Replace(text, "<path>");
        text = BearerPattern().Replace(text, "Bearer <redacted>");
        text = ApiKeyPattern().Replace(text, "<redacted-key>");
        text = EmailPattern().Replace(text, "<email>");
        text = IPv4Pattern().Replace(text, "<ip>");
        return text;
    }

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\\r\n""']+", RegexOptions.IgnoreCase)]
    private static partial Regex UserProfilePattern();

    [GeneratedRegex(@"\\\\[^\\\s""']+\\[^\r\n""']*")]
    private static partial Regex UncPathPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Za-z]:\\(?:[^\\\r\n""',;]+\\)*[^\\\r\n""',;]*")]
    private static partial Regex DrivePathPattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9\-\._~\+/]+=*")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{8,}")]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4Pattern();
}

public sealed record DiagnosticBundleOptions(
    string DiagnosticsDirectory,
    string IssueLogsDirectory,
    string SettingsPath,
    string? OutputPath = null,
    int MaxSampleLines = 20_000,
    int MaxIssueFiles = 20);

public sealed record DiagnosticBundleResult(
    string Path,
    IReadOnlyList<string> Entries,
    int RedactedFiles,
    long Bytes);

/// <summary>
/// Builds a single shareable ZIP with the latest long-run record, issue logs,
/// a hardware snapshot and a whitelisted settings summary. Every text entry is
/// passed through <see cref="DiagnosticRedactor"/>; credentials, user names,
/// absolute paths, e-mails and IPs never reach the archive.
/// </summary>
public static class DiagnosticBundle
{
    private static readonly string[] DiagnosticFiles =
    [
        "summary.txt",
        "samples.csv",
        "events.jsonl",
        "processes.csv",
        "resources.csv",
        "context.json",
    ];

    public static DiagnosticBundleResult Create(DiagnosticBundleOptions options)
    {
        string output = options.OutputPath ?? Path.Combine(
            AppPaths.DiagnosticBundlesDirectory,
            "ags-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".zip");
        string? outputDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var entries = new List<string>();
        int redactedFiles = 0;
        int sampleLines = 0;

        using (FileStream stream = new(output, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            string? latest = FindLatestDiagnostics(options.DiagnosticsDirectory);
            if (latest is not null)
            {
                foreach (string name in DiagnosticFiles)
                {
                    string path = Path.Combine(latest, name);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    string content;
                    if (name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    {
                        content = ReadTail(path, options.MaxSampleLines);
                        sampleLines += Math.Min(options.MaxSampleLines, CountLines(path));
                    }
                    else
                    {
                        content = ReadText(path);
                    }

                    AddText(archive, "diagnostics/" + name, content, entries, ref redactedFiles);
                }

                AddText(
                    archive,
                    "diagnostics/diagnostics-folder.txt",
                    Path.GetFileName(latest),
                    entries,
                    ref redactedFiles);
            }

            AddText(
                archive,
                "hardware.json",
                HardwareJson(),
                entries,
                ref redactedFiles);

            AddText(archive, "settings-summary.json", SettingsSummary(options.SettingsPath), entries, ref redactedFiles);
            AddIssueFiles(archive, options, entries, ref redactedFiles);

            string manifest = BuildManifest(entries, redactedFiles, sampleLines);
            AddText(archive, "manifest.json", manifest, entries, ref redactedFiles, track: false);
            entries.Add("manifest.json");
        }

        long bytes = new FileInfo(output).Length;
        return new DiagnosticBundleResult(output, entries, redactedFiles, bytes);
    }

    private static string? FindLatestDiagnostics(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.EnumerateDirectories(directory, "diag-*")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static void AddIssueFiles(
        ZipArchive archive,
        DiagnosticBundleOptions options,
        List<string> entries,
        ref int redactedFiles)
    {
        if (!Directory.Exists(options.IssueLogsDirectory))
        {
            return;
        }

        IReadOnlyList<string> files = Directory.EnumerateFiles(options.IssueLogsDirectory, "*", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Take(Math.Max(1, options.MaxIssueFiles))
            .ToList();

        foreach (string file in files)
        {
            string content = ReadTail(file, 2_000);
            AddText(archive, "issues/" + Path.GetFileName(file), content, entries, ref redactedFiles);
        }
    }

    private static string SettingsSummary(string path)
    {
        var json = new JsonBuilder();
        json.BeginObject();
        json.Property("note", "Whitelisted settings only; API keys, URLs, hotkeys and process lists are excluded.");

        if (!File.Exists(path))
        {
            json.Property("available", false);
            json.EndObject();
            return json.ToString();
        }

        try
        {
            AppSettings settings = new SettingsService(path).Load().Settings;
            json.Property("available", true);
            json.Property("schemaVersion", settings.SchemaVersion);
            json.Property("language", settings.Language);
            json.Property("theme", settings.Theme);
            json.Property("samplingNormalMs", settings.Sampling.NormalMs);
            json.Property("samplingGameMs", settings.Sampling.GameMs);
            json.Property("framesMode", settings.Frames.Mode);
            json.Property("sensorsEnabled", settings.Sensors.Enabled);
            json.Property("sensorCpuMode", settings.Sensors.CpuMode);
            json.Property("sensorIntervalMs", settings.Sensors.IntervalMs);
            json.Property("highLoadMode", settings.HighLoadMode);
            json.Property("pauseCharts", settings.HighLoadPause.Charts);
            json.Property("pauseOverlay", settings.HighLoadPause.Overlay);
            json.Property("pauseSessions", settings.HighLoadPause.Sessions);
            json.Property("pauseDiagnostics", settings.HighLoadPause.Diagnostics);
            json.Property("pauseSensors", settings.HighLoadPause.Sensors);
            json.Property("processMemoryPercent", settings.ProcessMemoryPercent);
            json.Property("chartsSeparate", settings.Charts.Separate);
            json.Property("chartCpu", settings.Charts.Cpu);
            json.Property("chartGpu", settings.Charts.Gpu);
            json.Property("chartMemory", settings.Charts.Memory);
            json.Property("chartVram", settings.Charts.Vram);
            json.Property("chartDisk", settings.Charts.Disk);
            json.Property("chartFrames", settings.Charts.Frames);
            json.Property("overlayEnabled", settings.Overlay.Enabled);
            json.Property("aiEnabled", settings.Ai.Enabled);
            json.Property("provider", settings.Ai.ActiveProvider);
            json.Property("aiAutoAnalyze", settings.Ai.AutoAnalyze);
            json.Property("aiRedact", settings.Ai.Redact);
            json.Property("aiDailyBudget", settings.Ai.DailyBudget);
            json.Property("aiMonthlyBudget", settings.Ai.MonthlyBudget);
            json.Property("longRunRecord", settings.Diagnostics.LongRunRecord);
            json.Property("longRunIntervalSeconds", settings.Diagnostics.IntervalSeconds);
            json.Property("logsEnabled", settings.Logs.Enabled);
            json.Property("logRetentionDays", settings.Logs.RetentionDays);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            json.Property("available", false);
            json.Property("error", ex.GetType().Name);
        }

        json.EndObject();
        return json.ToString();
    }

    private static string HardwareJson()
    {
        var json = new JsonBuilder();
        json.BeginObject();
        SystemInventorySnapshot hardware = SystemInventory.Capture();
        json.Property("os", hardware.Os);
        json.Property("cpu", hardware.Cpu);
        json.Property("logicalProcessors", hardware.LogicalProcessors);
        json.Property("memoryGb", hardware.MemoryGb);
        json.BeginArray("gpu");
        foreach (string gpu in hardware.Gpu)
        {
            json.ArrayString(gpu);
        }

        json.EndArray();
        json.Property("driver", hardware.Driver);
        json.EndObject();
        return json.ToString();
    }

    private static string BuildManifest(List<string> entries, int redactedFiles, int sampleLines)
    {
        var json = new JsonBuilder();
        json.BeginObject();
        json.Property("createdUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        json.Property("application", "AzureGameAssistant");
        json.Property("redactedFiles", redactedFiles);
        json.Property("sampleLines", sampleLines);
        json.BeginArray("files");
        foreach (string entry in entries)
        {
            json.ArrayString(entry);
        }

        json.EndArray();
        json.BeginArray("redaction");
        json.ArrayString("user profile paths -> %USERPROFILE%");
        json.ArrayString("absolute/UNC paths -> <path>/<unc-path>");
        json.ArrayString("bearer tokens and sk-* keys -> <redacted>");
        json.ArrayString("e-mails -> <email>");
        json.ArrayString("IPv4 addresses -> <ip>");
        json.EndArray();
        json.EndObject();
        return json.ToString();
    }

    private static void AddText(
        ZipArchive archive,
        string entryName,
        string content,
        List<string> entries,
        ref int redactedFiles,
        bool track = true)
    {
        string redacted = DiagnosticRedactor.Redact(content);
        if (!string.Equals(redacted, content, StringComparison.Ordinal))
        {
            redactedFiles++;
        }

        ZipArchiveEntry entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(redacted);
        if (track)
        {
            entries.Add(entryName);
        }
    }

    private static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ReadTail(string path, int maxLines)
    {
        int limit = Math.Max(1, maxLines);
        var lines = new Queue<string>(limit);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == limit)
                {
                    lines.Dequeue();
                }

                lines.Enqueue(line);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int CountLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            int count = 0;
            while (reader.ReadLine() is not null)
            {
                count++;
            }

            return count;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}

/// <summary>
/// Minimal JSON writer for the fixed-shape bundle metadata. Hand-rolled so the
/// Core library stays trim/AOT clean without extra source-generated contexts.
/// </summary>
internal sealed class JsonBuilder
{
    private readonly StringBuilder _text = new(1024);
    private bool _first = true;

    public void BeginObject()
    {
        _text.Append('{');
        _first = true;
    }

    public void EndObject()
    {
        _text.Append('\n').Append('}');
    }

    public void BeginArray(string name)
    {
        AppendSeparator();
        _text.Append("  \"").Append(name).Append("\": [");
        _first = true;
    }

    public void EndArray()
    {
        _text.Append("\n  ]");
        _first = false;
    }

    public void ArrayString(string value)
    {
        _first = false;
        _text.Append("\n    ");
        AppendString(value);
    }

    public void Property(string name, string? value)
    {
        AppendSeparator();
        _text.Append("  \"").Append(name).Append("\": ");
        if (value is null)
        {
            _text.Append("null");
        }
        else
        {
            AppendString(value);
        }
    }

    public void Property(string name, bool value)
    {
        AppendSeparator();
        _text.Append("  \"").Append(name).Append("\": ").Append(value ? "true" : "false");
    }

    public void Property(string name, int value)
    {
        AppendSeparator();
        _text.Append("  \"").Append(name).Append("\": ").Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void Property(string name, double value)
    {
        AppendSeparator();
        _text.Append("  \"").Append(name).Append("\": ");
        if (!double.IsFinite(value))
        {
            _text.Append("null");
            return;
        }

        _text.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
    }

    public override string ToString() => _text.ToString();

    private void AppendSeparator()
    {
        if (!_first)
        {
            _text.Append(',');
        }

        _text.Append('\n');
        _first = false;
    }

    private void AppendString(string value)
    {
        _text.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"':
                    _text.Append("\\\"");
                    break;
                case '\\':
                    _text.Append("\\\\");
                    break;
                case '\n':
                    _text.Append("\\n");
                    break;
                case '\r':
                    _text.Append("\\r");
                    break;
                case '\t':
                    _text.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        _text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _text.Append(c);
                    }

                    break;
            }
        }

        _text.Append('"');
    }
}
