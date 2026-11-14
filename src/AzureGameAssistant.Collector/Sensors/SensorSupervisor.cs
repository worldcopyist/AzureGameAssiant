using System.ComponentModel;
using System.Diagnostics;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Sensors;

namespace AzureGameAssistant.Collector.Sensors;

/// <summary>
/// Supervises the optional GPU sensor sidecar. The sidecar is a separate process
/// so a vendor-driver crash cannot take down telemetry sampling.
/// </summary>
internal sealed class SensorSupervisor : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
    ];

    private readonly FileLogger _logger;
    private readonly string _executablePath;
    private readonly int _intervalMs;
    private readonly string _cpuMode;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();

    private Task? _task;
    private Process? _process;
    private IntPtr _job;
    private volatile string? _lastError;
    private volatile bool _running;
    private volatile bool _paused;

    public SensorSupervisor(FileLogger logger, string executablePath, int intervalMs, string cpuMode)
    {
        _logger = logger;
        _executablePath = executablePath;
        _intervalMs = intervalMs;
        _cpuMode = cpuMode;
    }

    public bool IsRunning => _running;

    public bool IsPaused => _paused;

    public string? LastError => _lastError;

    /// <summary>
    /// Stops or restarts the sidecar without tearing down the supervisor. The
    /// reduced profile pauses it because the sidecar's vendor libraries hold
    /// tens of megabytes even while the game does not show temperatures.
    /// </summary>
    public void SetPaused(bool paused)
    {
        if (_paused == paused)
        {
            return;
        }

        _paused = paused;

        if (!paused)
        {
            _logger.Info("传感器边车恢复");
            return;
        }

        _logger.Info("传感器边车暂停（高占用模式）");
        Process? process = _process;
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_task is not null)
            {
                return;
            }

            if (!File.Exists(_executablePath))
            {
                _lastError = $"未找到传感器边车：{_executablePath}";
                _logger.Warn(_lastError);
                return;
            }

            _job = JobObject.CreateKillOnClose();
            _running = true;
            _logger.Info($"传感器边车启动：{_executablePath}，间隔 {_intervalMs}ms，CPU 模式 {_cpuMode}");
            _task = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        _stop.Cancel();

        try
        {
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        try
        {
            Process? process = _process;
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }

        _running = false;
    }

    private async Task RunAsync(CancellationToken token)
    {
        int consecutiveFailures = 0;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (_paused)
                {
                    consecutiveFailures = 0;
                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                using var process = new Process
                {
                    StartInfo = BuildStartInfo(),
                };

                try
                {
                    if (!process.Start())
                    {
                        _lastError = "无法启动传感器边车";
                        break;
                    }
                }
                catch (Win32Exception ex)
                {
                    _lastError = ex.Message;
                    _logger.Warn($"启动传感器边车失败：{ex.Message}");
                    break;
                }
                catch (InvalidOperationException ex)
                {
                    _lastError = ex.Message;
                    _logger.Warn($"启动传感器边车失败：{ex.Message}");
                    break;
                }

                _process = process;
                if (_job != IntPtr.Zero && !JobObject.Assign(_job, process))
                {
                    _logger.Warn($"传感器边车未能加入作业对象：{System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
                }

                if (_paused)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                    {
                    }

                    _process = null;
                    continue;
                }

                _lastError = null;

                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                finally
                {
                    _process = null;
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (_paused)
                {
                    // The exit was requested by the reduced profile, not a crash.
                    continue;
                }

                int exitCode = process.HasExited ? process.ExitCode : -1;

                if (exitCode == 3)
                {
                    _lastError = "检测到内核驱动，传感器已停止";
                    _logger.Error(_lastError);
                    break;
                }

                if (exitCode == 4)
                {
                    _lastError = "未发现可用 GPU，传感器已停止";
                    _logger.Warn(_lastError);
                    break;
                }

                if (exitCode == 2)
                {
                    consecutiveFailures++;
                    _lastError = "传感器初始化失败";
                    if (consecutiveFailures >= 3)
                    {
                        _logger.Error(_lastError);
                        break;
                    }
                }
                else
                {
                    consecutiveFailures = 0;
                    _lastError = $"传感器边车退出（代码 {exitCode}）";
                }

                TimeSpan delay = RetryDelays[Math.Min(Math.Max(consecutiveFailures - 1, 0), RetryDelays.Length - 1)];
                _logger.Warn($"传感器边车退出（代码 {exitCode}），{delay.TotalSeconds:0}s 后重启");
                await Task.Delay(delay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _running = false;
        }
    }

    private ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo(_executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add("--interval");
        info.ArgumentList.Add(_intervalMs.ToString(System.Globalization.CultureInfo.InvariantCulture));
        info.ArgumentList.Add("--cpu-mode");
        info.ArgumentList.Add(_cpuMode);
        info.ArgumentList.Add("--mmf");
        info.ArgumentList.Add(SensorLayout.MapName);
        return info;
    }

    public void Dispose()
    {
        Stop();

        if (_job != IntPtr.Zero)
        {
            Kernel32.CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        _stop.Dispose();
    }
}
