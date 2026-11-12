using System.ComponentModel;
using System.Diagnostics;
using AzureGameAssistant.Core.Logging;
using System.IO;

namespace AzureGameAssistant.UI.Services;

public enum CollectorState
{
    Starting,
    Running,
    Stopped,
    Missing,
    Failed,
}

public sealed class CollectorLauncher : IDisposable
{
    private static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(60);

    private readonly FileLogger _logger;
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;

    private Process? _process;
    private DateTime _startedAt;
    private int _restartAttempts;
    private bool _intentionalStop;
    private bool _disposed;

    public CollectorLauncher(FileLogger logger)
    {
        _logger = logger;
        ExecutablePath = ResolveExecutable();
    }

    public string? ExecutablePath { get; }

    public CollectorState State { get; private set; } = CollectorState.Stopped;

    public string? LastError { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public event EventHandler? StateChanged;

    public bool Start()
    {
        if (IsRunning)
        {
            return true;
        }

        _intentionalStop = false;

        if (ExecutablePath is null)
        {
            SetState(CollectorState.Missing, "未找到 AzureGameAssistant.Collector.exe");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                Arguments = $"--parent {Environment.ProcessId}",
                WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                SetState(CollectorState.Failed, "无法启动采集进程");
                return false;
            }

            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;
            _process = process;
            _startedAt = DateTime.UtcNow;

            _logger.Info($"采集进程已启动：{ExecutablePath}，PID {process.Id}");
            SetState(CollectorState.Starting, null);
            return true;
        }
        catch (Win32Exception ex)
        {
            SetState(CollectorState.Failed, ex.Message);
            _logger.Error("启动采集进程失败", ex);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            SetState(CollectorState.Failed, ex.Message);
            _logger.Error("启动采集进程失败", ex);
            return false;
        }
    }

    public void MarkIntentionalStop()
    {
        _intentionalStop = true;
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        Process? process = _process;
        if (process is null)
        {
            return true;
        }

        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        Process? process = Interlocked.Exchange(ref _process, null);
        int exitCode = -1;

        try
        {
            exitCode = process?.ExitCode ?? -1;
        }
        catch (InvalidOperationException)
        {
        }

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
        }

        if (_disposed || _intentionalStop)
        {
            return;
        }

        if (DateTime.UtcNow - _startedAt > StableUptime)
        {
            _restartAttempts = 0;
        }

        _logger.Warn($"采集进程退出，代码 {exitCode}，重启尝试 {_restartAttempts + 1}");

        if (_restartAttempts >= 3)
        {
            SetState(CollectorState.Failed, $"采集进程连续退出（代码 {exitCode}）");
            return;
        }

        int delaySeconds = _restartAttempts switch
        {
            0 => 1,
            1 => 5,
            _ => 15,
        };

        _restartAttempts++;
        SetState(CollectorState.Stopped, $"采集进程退出（代码 {exitCode}），{delaySeconds} 秒后重启");

        _ = RestartAfterDelayAsync(TimeSpan.FromSeconds(delaySeconds));
    }

    private async Task RestartAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (_disposed || _intentionalStop)
        {
            return;
        }

        Post(() => Start());
    }

    private void SetState(CollectorState state, string? error)
    {
        LastError = error;
        State = state;
        Post(() => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    private void Post(Action action)
    {
        if (_context is null)
        {
            action();
            return;
        }

        _context.Post(_ => action(), null);
    }

    private static string? ResolveExecutable()
    {
        string? overridePath = Environment.GetEnvironmentVariable("AZUREGA_COLLECTOR_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        string local = Path.Combine(AppContext.BaseDirectory, "AzureGameAssistant.Collector.exe");
        if (File.Exists(local))
        {
            return local;
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "collector", "AzureGameAssistant.Collector.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string separator = Path.DirectorySeparatorChar.ToString();
        string preferred = baseDirectory.Contains(
            $"{separator}bin{separator}Release{separator}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        string fallback = preferred == "Release" ? "Debug" : "Release";

        foreach (string configuration in new[] { preferred, fallback })
        {
            string candidate = Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..", "..", "..", "..",
                "AzureGameAssistant.Collector",
                "bin",
                configuration,
                "net8.0",
                "AzureGameAssistant.Collector.exe"));

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _disposed = true;

        Process? process = Interlocked.Exchange(ref _process, null);
        if (process is not null)
        {
            process.Exited -= OnProcessExited;
        }
    }
}
