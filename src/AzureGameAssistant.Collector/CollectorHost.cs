using System.Diagnostics;
using AzureGameAssistant.Collector.Frames;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Collector.Ipc;
using AzureGameAssistant.Collector.Sampling;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Collector;

internal sealed class CollectorHost : ICollectorControl, IDisposable
{
    private readonly FileLogger _logger;
    private readonly CollectorOptions _options;
    private readonly TelemetryWriter _telemetry;
    private readonly PdhSampler _pdh;
    private readonly ProcessSampler _processes;
    private readonly JitterMonitor _jitter;
    private readonly PresentMonSupervisor? _presentMon;
    private readonly Dictionary<uint, float> _gpuByPid = new();
    private readonly ProcessSlot[] _slots = new ProcessSlot[5];
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _uptime = new();
    private readonly int _intervalMs;
    private readonly int _capabilities;
    private readonly string _appVersion;
    private ControlPipeServer? _pipeServer;
    private Task? _pipeTask;
    private long _sampleCount;
    private volatile bool _paused;
    private volatile string? _lastError;
    private volatile int _modeFlags;
    private bool _errorLogged;
    private bool _disposed;

    public CollectorHost(AppSettings settings, FileLogger logger, CollectorOptions options)
    {
        _logger = logger;
        _options = options;
        _intervalMs = Math.Clamp(options.IntervalOverrideMs ?? settings.Sampling.NormalMs, 100, 10_000);
        _capabilities = AzureGameAssistant.Core.Models.CapabilityFlags.ProcessAccess;
        _appVersion = typeof(CollectorHost).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

        _telemetry = new TelemetryWriter(logger, _intervalMs, _capabilities);
        _pdh = new PdhSampler(logger);
        _processes = new ProcessSampler(logger);
        _jitter = new JitterMonitor(logger);

        if (settings.Frames.Enabled && settings.Frames.TargetPid is int targetPid && targetPid > 0)
        {
            string path = ResolvePresentMonPath(settings.Frames.PresentMonPath);
            _presentMon = new PresentMonSupervisor(logger, _telemetry, path, targetPid);
        }
    }

    public bool IsTelemetryOpen => _telemetry.IsOpen;

    public long SampleCount => Interlocked.Read(ref _sampleCount);

    public string AppVersion => _appVersion;

    public int CapabilityFlags
    {
        get
        {
            int flags = _capabilities;
            if (_presentMon is { IsCapturing: true })
            {
                flags |= AzureGameAssistant.Core.Models.CapabilityFlags.Frames;
            }

            return flags;
        }
    }

    public async Task<int> RunAsync(CancellationToken externalToken)
    {
        if (!_telemetry.IsOpen)
        {
            _logger.Error("遥测共享内存不可用，采集进程退出");
            return 1;
        }

        if (_options.EnablePipe)
        {
            _pipeServer = new ControlPipeServer(_logger, this);
            _pipeTask = Task.Run(() => _pipeServer.RunAsync(_stop.Token), CancellationToken.None);
        }

        _presentMon?.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, externalToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_intervalMs));

        _uptime.Start();
        _logger.Info($"采集进程启动：间隔 {_intervalMs}ms，PID {Environment.ProcessId}");

        try
        {
            while (!linked.IsCancellationRequested)
            {
                if (!await timer.WaitForNextTickAsync(linked.Token))
                {
                    break;
                }

                Tick();

                if (_options.Duration is TimeSpan duration && _uptime.Elapsed >= duration)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _stop.Cancel();
            _presentMon?.Stop();
            await StopPipeAsync();
            _logger.Info($"采集进程停止，共写入 {Interlocked.Read(ref _sampleCount)} 个采样点");
        }

        return 0;
    }

    private void Tick()
    {
        Kernel32.QueryPerformanceCounter(out long qpc);

        int mode = _modeFlags | (_paused ? CollectorModeFlags.Paused : 0);
        _telemetry.Heartbeat(qpc, mode);
        _telemetry.SetCapabilities(CapabilityFlags);

        if (_paused)
        {
            return;
        }

        try
        {
            bool collected = _pdh.Collect();
            if (!collected)
            {
                ReportError("PDH 采集失败");
            }

            SystemMetrics metrics = _pdh.ReadSystem();
            bool gpuValid = _pdh.ReadGpu(out GpuTotals gpu, _gpuByPid);
            uint foregroundPid = GetForegroundPid();
            int processCount = _processes.Sample(_slots, foregroundPid, _gpuByPid);

            ushort flags = 0;
            if (processCount > 0)
            {
                flags |= SampleFlags.ProcessValid;
            }

            if (gpuValid)
            {
                flags |= SampleFlags.GpuValid;
            }

            if (_presentMon is { IsCapturing: true })
            {
                flags |= SampleFlags.FrameValid;
            }

            var sample = new SampleRecord
            {
                QpcTicks = qpc,
                UtcTicks = DateTime.UtcNow.Ticks,
                CpuUtilityPct = metrics.CpuUtilityPct,
                CpuFrequencyPct = metrics.CpuFrequencyPct,
                MemAvailableMb = metrics.MemAvailableMb,
                MemCommitMb = metrics.MemCommitMb,
                MemCommitLimitMb = metrics.MemCommitLimitMb,
                PageFilePct = metrics.PageFilePct,
                DiskBusyPct = metrics.DiskBusyPct,
                DiskLatencyMs = metrics.DiskLatencyMs,
                GpuUtilPct = gpu.UtilPct,
                GpuDedicatedMb = gpu.DedicatedMb,
                GpuSharedMb = gpu.SharedMb,
                TimerJitterMs = _jitter.TakeMaxJitterMs(),
                ForegroundPid = foregroundPid,
                ProcessorQueueLength = metrics.ProcessorQueueLength,
                Flags = flags,
                P0 = _slots[0],
                P1 = _slots[1],
                P2 = _slots[2],
                P3 = _slots[3],
                P4 = _slots[4],
            };

            _telemetry.WriteSample(in sample);
            Interlocked.Increment(ref _sampleCount);

            if (collected)
            {
                ClearError();
            }
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            if (!_errorLogged)
            {
                _errorLogged = true;
                _logger.Error("采样循环异常", ex);
            }
        }
    }

    private static string ResolvePresentMonPath(string configured)
    {
        string path = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);

        return Path.GetFullPath(path);
    }

    private static uint GetForegroundPid()    {
        IntPtr window = User32.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return 0;
        }

        User32.GetWindowThreadProcessId(window, out uint pid);
        return pid;
    }

    public CollectorStatus GetStatus() => new(
        _intervalMs,
        false,
        _paused,
        _uptime.Elapsed.TotalSeconds,
        Interlocked.Read(ref _sampleCount),
        _lastError ?? _presentMon?.LastError);

    public bool SetPaused(bool paused)
    {
        _paused = paused;
        _modeFlags = paused ? CollectorModeFlags.Paused : 0;
        _telemetry.SetModeFlags(_modeFlags);

        if (paused)
        {
            _logger.Info("采集已暂停");
        }
        else
        {
            _logger.Info("采集已恢复");
        }

        return true;
    }

    public void RequestQuit() => _stop.Cancel();

    private void ReportError(string message)
    {
        _lastError = message;
    }

    private void ClearError()
    {
        if (_lastError is not null)
        {
            _lastError = null;
            _errorLogged = false;
        }
    }

    private async Task StopPipeAsync()
    {
        if (_pipeTask is null)
        {
            return;
        }

        try
        {
            await _pipeTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warn($"控制管道停止异常：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();

        _presentMon?.Dispose();
        _jitter.Dispose();
        _pdh.Dispose();
        _processes.Dispose();
        _telemetry.Dispose();
        _stop.Dispose();
    }
}
