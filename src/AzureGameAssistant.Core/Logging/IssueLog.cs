using System.Globalization;
using System.Text;

namespace AzureGameAssistant.Core.Logging;

/// <summary>
/// Cross-run archive of software problems: warnings, errors and unhandled
/// exceptions from every process. One file per calendar month, appended and
/// closed per entry so month rollover needs no restart and long-running
/// sessions stay bounded to a single open file per write.
/// </summary>
public static class IssueLog
{
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string DefaultDirectory => AppPaths.IssueLogsDirectory;

    public static string PathFor(string directory, DateTime localTime) =>
        Path.Combine(directory, $"issues-{localTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.log");

    public static void Write(string component, LogLevel level, string message, Exception? exception = null) =>
        Write(DefaultDirectory, component, level, message, exception, DateTime.Now);

    public static void Write(
        string directory,
        string component,
        LogLevel level,
        string message,
        Exception? exception,
        DateTime localTime)
    {
        if (level < LogLevel.Warn || string.IsNullOrWhiteSpace(component))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            string path = PathFor(directory, localTime);

            var line = new StringBuilder(200);
            line.Append(localTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" [")
                .Append(level == LogLevel.Warn ? "WRN" : "ERR")
                .Append("] [")
                .Append(component)
                .Append(':')
                .Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
                .Append("] ")
                .Append(message);

            if (exception is not null)
            {
                line.AppendLine().Append(exception);
            }

            lock (Gate)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);

                if (stream.Length == 0)
                {
                    WriteChunk(stream, Header(localTime));
                }

                WriteChunk(stream, line.ToString());
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (NotSupportedException)
        {
        }
        catch (ArgumentException)
        {
        }
    }

    private static void WriteChunk(FileStream stream, string text)
    {
        using var writer = new StreamWriter(stream, Utf8, bufferSize: 256, leaveOpen: true);
        writer.WriteLine(text);
        writer.Flush();
    }

    private static string Header(DateTime localTime)
    {
        Version? version = typeof(IssueLog).Assembly.GetName().Version;
        string versionText = version is null ? "0.0.0" : version.ToString(3);
        return $"# AzureGameAssistant issue log · {localTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)} · " +
               $"v{versionText} · {Environment.OSVersion.VersionString}";
    }
}
