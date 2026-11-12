using System.Diagnostics;
using AzureGameAssistant.Collector.Frames;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Collector.Ipc;
using AzureGameAssistant.Collector.Sampling;
using AzureGameAssistant.Collector.Sensors;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sensors;
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
    private readonly object _framesGate = new();
    private PresentMonSupervisor? _presentMon;
    private readonly string _presentMonPath;
    private readonly SensorSupervisor? _sensor;
    private readonly SensorReader? _sensorReader;
    private readonly Dictionary<uint, float> _gpuByPid = new();
    private readonly ProcessSlot[] _slots = new ProcessSlot[5];
    private ProcessSlot[] _latestSlots = new ProcessSlot[5];
    private readonly CancellationTokenSource _stop = new();
    private readonly Stopwatch _uptime = new();
    private volatile int _intervalMs;
    private readonly int _capabilities;
    private readonly string _appVersion;
    private ControlPipeServer? _pipeServer;
    private Task? _pipeTask;
    private long _sampleCount;
    private volatile bool _paused;
    private volatile bool _gameMode;
    private volatile string? _lastError;
    private volatile int _modeFlags;
    private volatile bool _sensorFresh;
    private SensorRecord _sensorRecord;
    private bool _errorLogged;
    private bool _disposed;
    private DateTime _lastFrameStartWarnAt;
    private string? _lastFrameStartWarnMessage;

    private static readonly TimeSpan FrameStartWarnInterval = TimeSpan.FromMinutes(5);

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

        _presentMonPath = ResolvePresentMonPath(settings.Frames.PresentMonPath);
        if (settings.Frames.Enabled &&
            settings.Frames.TargetPid is int targetPid &&
            targetPid > 0 &&
            IsProcessAlive(targetPid))
        {
            _presentMon = new PresentMonSupervisor(
                logger, _telemetry, _presentMonPath, targetPid,
                () => IsTargetActive(targetPid));
        }

        if (settings.Sensors.Enabled)
        {
            _sensorReader = new SensorReader();
            _sensor = new SensorSupervisor(
                logger,
                ResolveSensorsPath(),
                Math.Clamp(settings.Sensors.IntervalMs, 500, 10_000),
                settings.Sensors.CpuMode);
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

            if (_sensorFresh)
            {
                flags |= AzureGameAssistant.Core.Models.CapabilityFlags.VendorSensors;
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
        _sensor?.Start();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, externalToken);

        if (_options.ParentProcessId is int parentPid)
        {
            _ = WatchParentAsync(parentPid, linked.Token);
        }

        _uptime.Start();
        _logger.Info($"采集进程启动：间隔 {_intervalMs}ms，PID {Environment.ProcessId}");

        try
        {
            while (!linked.IsCancellationRequested)
            {
                // Re-read the interval every cycle so game detection can raise
                // the sampling rate without restarting the collector.
                await Task.Delay(_intervalMs, linked.Token);

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
            StopFrames();
            _sensor?.Stop();
            await StopPipeAsync();
            _logger.Info($"采集进程停止，共写入 {Interlocked.Read(ref _sampleCount)} 个采样点");
        }

        return 0;
    }

    /// <summary>
    /// A crashed or force-closed UI cannot ask the collector to quit, and the
    /// leftover process would keep the packaged executable locked. Exit with
    /// the parent instead of relying on the control pipe.
    /// </summary>
    private async Task WatchParentAsync(int parentPid, CancellationToken token)
    {
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(parentPid);
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2_000, token).ConfigureAwait(false);
                if (parent.HasExited)
                {
                    _logger.Info($"父进程 {parentPid} 已退出，采集进程随之停止");
                    _stop.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ArgumentException)
        {
            _logger.Info($"父进程 {parentPid} 不存在，采集进程退出");
            _stop.Cancel();
        }
        catch (InvalidOperationException)
        {
            _stop.Cancel();
        }
        finally
        {
            parent?.Dispose();
        }
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
            _latestSlots = _slots.ToArray();

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

            bool vendorValid = false;
            bool cpuValid = false;
            VendorSlot vendor = default;
            CpuSensorSlot cpu = default;
            if (_sensorReader is not null)
            {
                ReadSensors();
                SensorRecord sensors = _sensorRecord;
                if (_sensorFresh)
                {
                    vendorValid = (sensors.Flags & SensorFlags.GpuValid) != 0;
                    cpuValid = (sensors.Flags & SensorFlags.CpuValid) != 0;
                    vendor = new VendorSlot
                    {
                        TemperatureC = sensors.GpuTemperatureC,
                        HotSpotC = sensors.GpuHotSpotC,
                        PowerW = sensors.GpuPowerW,
                        CoreClockMhz = sensors.GpuCoreClockMhz,
                        MemoryClockMhz = sensors.GpuMemoryClockMhz,
                        FanPct = sensors.GpuFanPct,
                        VramUsedMb = sensors.VramUsedMb,
                        VramTotalMb = sensors.VramTotalMb,
                    };
                    cpu = new CpuSensorSlot
                    {
                        TemperatureC = sensors.CpuTemperatureC,
                        PackagePowerW = sensors.CpuPowerW,
                    };
                }
            }

            if (vendorValid)
            {
                flags |= SampleFlags.VendorValid;
            }

            if (cpuValid)
            {
                flags |= SampleFlags.CpuSensorValid;
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
                V0 = vendor,
                C0 = cpu,
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

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string ResolveSensorsPath()
    {
        // Packaged builds keep the sidecar and its dependencies in a subfolder so
        // the collector directory stays a single native executable plus licenses.
        string nested = Path.Combine(
            AppContext.BaseDirectory, "sensors", "AzureGameAssistant.Sensors.exe");
        if (File.Exists(nested))
        {
            return nested;
        }

        string local = Path.Combine(AppContext.BaseDirectory, "AzureGameAssistant.Sensors.exe");
        if (File.Exists(local))
        {
            return local;
        }

        string baseDirectory = AppContext.BaseDirectory;
        string separator = Path.DirectorySeparatorChar.ToString();
        string preferred = baseDirectory.Contains(
            $"{separator}Release{separator}",
            StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";
        string fallback = preferred == "Release" ? "Debug" : "Release";

        foreach (string configuration in new[] { preferred, fallback })
        {
            string candidate = Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..", "..", "..", "..",
                "AzureGameAssistant.Sensors",
                "bin",
                configuration,
                "net8.0-windows",
                "AzureGameAssistant.Sensors.exe"));

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return local;
    }

    private void ReadSensors()
    {
        if (_sensorReader is null)
        {
            return;
        }

        if (!_sensorReader.IsOpen && !_sensorReader.TryOpen())
        {
            _sensorFresh = false;
            return;
        }

        if (_sensorReader.Refresh())
        {
            _sensorRecord = _sensorReader.Latest;
        }

        _sensorFresh = _sensorReader.HeartbeatAgeMs <= 3_000;
    }

    private bool IsTargetActive(int pid)
    {
        if (GetForegroundPid() == (uint)pid)
        {
            return true;
        }

        ProcessSlot[] slots = _latestSlots;
        foreach (ProcessSlot slot in slots)
        {
            if (slot.Pid == pid)
            {
                return slot.GpuPct >= 5.0;
            }
        }

        return false;
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

    public CollectorStatus GetStatus()
    {
        PresentMonSupervisor? supervisor;
        lock (_framesGate)
        {
            supervisor = _presentMon;
        }

        return new CollectorStatus(
            _intervalMs,
            _gameMode,
            _paused,
            _uptime.Elapsed.TotalSeconds,
            Interlocked.Read(ref _sampleCount),
            _lastError ?? supervisor?.LastError,
            supervisor is null ? "stopped" : FrameStateName(supervisor.State),
            supervisor?.TargetPid,
            supervisor?.LastError);
    }

    public bool StartFrames(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        lock (_framesGate)
        {
            if (_presentMon is { } existing && existing.TargetPid == pid)
            {
                if (existing.State is not (FrameCaptureState.Unavailable or FrameCaptureState.Stopped))
                {
                    return true;
                }

                // A finished or failed session must be replaceable, otherwise
                // the target stays dead until the player switches games.
                _logger.Info($"帧采集重新启动：PID {pid}");
            }

            StopFramesLocked();

            var supervisor = new PresentMonSupervisor(                _logger, _telemetry, _presentMonPath, pid,
                () => IsTargetActive(pid));
            supervisor.Start();
            if (supervisor.State == FrameCaptureState.Unavailable)
            {
                WarnFrameStartFailure($"帧采集无法启动：{supervisor.LastError}");
                supervisor.Dispose();
                return false;
            }

            _presentMon = supervisor;
            return true;
        }
    }

    public bool StopFrames()
    {
        lock (_framesGate)
        {
            StopFramesLocked();
        }

        return true;
    }

    private void StopFramesLocked()
    {
        PresentMonSupervisor? supervisor = _presentMon;
        _presentMon = null;
        if (supervisor is null)
        {
            return;
        }

        // PresentMon needs up to a few seconds to exit. Blocking the control
        // pipe here made "frames.stop" exceed the client timeout, so the caller
        // never learned that the capture had actually ended.
        supervisor.Cancel();
        _ = Task.Run(() =>
        {
            try
            {
                supervisor.Dispose();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }

            _logger.Info($"帧采集已停止：PID {supervisor.TargetPid}");
        });
    }

    private void WarnFrameStartFailure(string message)
    {
        DateTime now = DateTime.UtcNow;
        if (message == _lastFrameStartWarnMessage && now - _lastFrameStartWarnAt < FrameStartWarnInterval)
        {
            _logger.Debug(message);
            return;
        }

        _lastFrameStartWarnAt = now;
        _lastFrameStartWarnMessage = message;
        _logger.Warn(message);
    }

    private static string FrameStateName(FrameCaptureState state) => state switch
    {
        FrameCaptureState.Starting => "starting",
        FrameCaptureState.Capturing => "capturing",
        FrameCaptureState.PermissionDenied => "permissionDenied",
        FrameCaptureState.Unavailable => "unavailable",
        _ => "stopped",
    };

    public bool SetSamplingInterval(int intervalMs, bool gameMode)
    {
        if (intervalMs <= 0)
        {
            return false;
        }

        int clamped = Math.Clamp(intervalMs, 100, 10_000);
        bool changed = clamped != _intervalMs;
        _intervalMs = clamped;
        _gameMode = gameMode;
        _telemetry.SetSamplingInterval(clamped);

        if (changed)
        {
            _logger.Info($"采样间隔调整为 {clamped}ms（游戏模式：{(gameMode ? "是" : "否")}）");
        }

        return true;
    }

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

        StopFrames();
        _sensor?.Dispose();
        _sensorReader?.Dispose();
        _jitter.Dispose();
        _pdh.Dispose();
        _processes.Dispose();
        _telemetry.Dispose();
        _stop.Dispose();
    }
}
