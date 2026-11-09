using System.Globalization;
using System.Text;

namespace AzureGameAssistant.Core.Logging;

public enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

public sealed class FileLogger : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _issueDirectory;
    private readonly string _component;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private StreamWriter? _writer;
    private long _written;
    private bool _disposed;
    private volatile bool _enabled = true;

    public FileLogger(string component, string? directory = null, long maxBytes = 2 * 1024 * 1024, int maxFiles = 5)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        _component = Sanitize(component);
        _directory = directory ?? AppPaths.LogsDirectory;
        _issueDirectory = Path.Combine(_directory, "issues");
        _maxBytes = Math.Max(64 * 1024, maxBytes);
        _maxFiles = Math.Max(1, maxFiles);
    }

    public string DirectoryPath => _directory;

    public string IssueDirectoryPath => _issueDirectory;

    /// <summary>
    /// When false, every write is dropped and the file handle is released.
    /// The user switch in settings controls this at runtime.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            if (!value)
            {
                lock (_gate)
                {
                    CloseWriter();
                }
            }
        }
    }

    public void Debug(string message) => Write(LogLevel.Debug, message, null);

    public void Info(string message) => Write(LogLevel.Info, message, null);

    public void Warn(string message, Exception? exception = null) => Write(LogLevel.Warn, message, exception);

    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        if (Volatile.Read(ref _disposed) || !_enabled)
        {
            return;
        }

        // Problems also go to the cross-run monthly archive; routine info
        // stays in the per-run file so the archive stays small and readable.
        if (level >= LogLevel.Warn)
        {
            IssueLog.Write(_issueDirectory, _component, level, message, exception, DateTime.Now);
        }

        lock (_gate)
        {
            try
            {
                EnsureWriter();
                if (_writer is null)
                {
                    return;
                }

                var line = new StringBuilder(160);
                line.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                    .Append(" [")
                    .Append(LevelText(level))
                    .Append("] ")
                    .Append(message);

                if (exception is not null)
                {
                    line.AppendLine().Append(exception);
                }

                _writer.WriteLine(line.ToString());
                _writer.Flush();
                _written += line.Length + Environment.NewLine.Length;
            }
            catch (IOException)
            {
                CloseWriter();
            }
            catch (UnauthorizedAccessException)
            {
                CloseWriter();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CloseWriter();
        }
    }

    private void EnsureWriter()
    {
        if (_writer is not null && _written < _maxBytes)
        {
            return;
        }

        CloseWriter();
        CleanupOldFiles();

        Directory.CreateDirectory(_directory);
        string name = $"{_component}-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log";
        string path = Path.Combine(_directory, name);

        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(false));
        _written = stream.Length;
    }

    private void CleanupOldFiles()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var files = new DirectoryInfo(_directory)
                .GetFiles($"{_component}-*.log")
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();

            int excess = files.Count - (_maxFiles - 1);
            for (int i = 0; i < excess; i++)
            {
                try
                {
                    files[i].Delete();
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
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (IOException)
        {
        }

        _writer = null;
        _written = 0;
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char ch in name)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-');
        }

        return builder.ToString();
    }
}
