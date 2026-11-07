using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Controls;
using AzureGameAssistant.UI.Interop;
using AzureGameAssistant.UI.Services;

namespace AzureGameAssistant.UI.ViewModels;

public sealed record FrameTargetOption(int Pid, string Display, bool HasWindow);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TelemetryService _telemetry;
    private readonly CollectorClient _client;
    private readonly CollectorLauncher _launcher;
    private readonly ProcessNameCache _names = new();
    private readonly LocalizationService _loc;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly CancellationTokenSource _cts = new();

    private const int FrameTimelineCapacity = 16_384;

    private readonly SampleRecord[] _timeline = new SampleRecord[TelemetryLayout.SampleCapacity];
    private readonly FrameRecord[] _frameBuffer = new FrameRecord[1024];
    private readonly FrameStats _frameStats = new();
    private readonly FrameTimeline _frameTimeline = new(FrameTimelineCapacity);
    private readonly FrameTimePoint[] _frameTimelineBuffer = new FrameTimePoint[FrameTimelineCapacity];
    private int _refreshing;
    private int _statusTicks;
    private long _lastFrameIndex = -1;
    private DateTime _lastFrameAt = DateTime.MinValue;
    private FrameStatsSnapshot _frameSnapshot;
    private bool _connected;
    private bool _helloDone;
    private string _appVersion = string.Empty;
    private int _collectorPid;
    private string? _collectorError;
    private double _uptimeSeconds;
    private int _samplingIntervalMs;
    private string _frameState = "stopped";
    private int? _frameTargetPid;
    private string? _frameError;
    private DateTime _lastAutoCaptureAttempt = DateTime.MinValue;
    private DateTime _autoCaptureBlockedUntil = DateTime.MinValue;
    private int? _autoCaptureSuppressedPid;

    private static readonly HashSet<string> AutoCaptureExcluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer",
        "SearchHost",
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "ApplicationFrameHost",
        "LockApp",
        "TextInputHost",
        "SystemSettings",
        "Taskmgr",
        "Idle",
        "System",
        "Registry",
    };

    public MainViewModel(
        TelemetryService telemetry,
        CollectorClient client,
        CollectorLauncher launcher,
        LocalizationService loc,
        AppSettings settings,
        SettingsService settingsService)
    {
        _telemetry = telemetry;
        _client = client;
        _launcher = launcher;
        _loc = loc;
        _settings = settings;
        _settingsService = settingsService;

        for (int i = 0; i < 5; i++)
        {
            Processes.Add(new ProcessRow());
        }

        ShowElevationButton = !PermissionDiagnostics.IsElevated();
        PauseText = _loc["Pause"];
        UpdateCollectorTexts();
        UpdateFrameTexts();
        UpdateFooter();
    }

    public ObservableCollection<ProcessRow> Processes { get; } = new();

    public ObservableCollection<FrameTargetOption> FrameTargets { get; } = new();

    public SampleRecord[] TimelineBuffer => _timeline;

    public int TimelineCount { get; private set; }

    public FrameTimePoint[] FrameTimelineBuffer => _frameTimelineBuffer;

    public int CopyFrameTimeline() => _frameTimeline.CopyTo(_frameTimelineBuffer);

    /// <summary>
    /// Snapshots the recent sample ring and frame ring for the AI fact layer.
    /// Only process names (never paths or titles) are resolved.
    /// </summary>
    public FactsInput CreateFactsInput(string locale)
    {
        int sampleCount = Math.Min(TimelineCount, _timeline.Length);
        var samples = sampleCount > 0 ? _timeline.AsSpan(0, sampleCount).ToArray() : [];

        long frameEnd = _telemetry.FrameWriteIndex;
        long frameStart = Math.Max(0, frameEnd - 4_096);
        var frameBuffer = new FrameRecord[(int)Math.Max(0, frameEnd - frameStart)];
        int frameCount = frameBuffer.Length > 0 ? _telemetry.ReadFrames(frameStart, frameBuffer) : 0;

        var names = new Dictionary<uint, string>();
        SampleRecord latest = sampleCount > 0 ? samples[^1] : _telemetry.Latest;
        ProcessSlot[] slots = [latest.P0, latest.P1, latest.P2, latest.P3, latest.P4];
        foreach (ProcessSlot slot in slots)
        {
            if (slot.Pid != 0 && !names.ContainsKey(slot.Pid))
            {
                names[slot.Pid] = _names.GetName(slot.Pid);
            }
        }

        string mode = _frameState == "capturing" || _settings.Sensors.Enabled ? "deep" : "shallow";
        return new FactsInput(samples, frameBuffer.AsSpan(0, frameCount).ToArray(), names, mode, locale);
    }

    public SampleRecord Latest => _telemetry.Latest;

    public bool IsConnectedToCollector => _connected;

    public bool ShowCombinedChart => !_settings.Charts.Separate;

    public bool ShowSeparateCharts => _settings.Charts.Separate;

    public bool ShowCpuChart => ShowSeparateCharts && _settings.Charts.Cpu;

    public bool ShowGpuChart => ShowSeparateCharts && _settings.Charts.Gpu;

    public bool ShowMemoryChart => ShowSeparateCharts && _settings.Charts.Memory;

    public bool ShowVramChart => ShowSeparateCharts && _settings.Charts.Vram;

    public bool ShowDiskChart => ShowSeparateCharts && _settings.Charts.Disk;

    public bool ShowFrameChart => ShowSeparateCharts && _settings.Charts.Frames;

    public ChartSeries CombinedSeries
    {
        get
        {
            ChartSeries series = ChartSeries.None;
            if (_settings.Charts.Cpu)
            {
                series |= ChartSeries.Cpu;
            }

            if (_settings.Charts.Gpu)
            {
                series |= ChartSeries.Gpu;
            }

            if (_settings.Charts.Memory)
            {
                series |= ChartSeries.Memory;
            }

            if (_settings.Charts.Vram)
            {
                series |= ChartSeries.Vram;
            }

            if (_settings.Charts.Disk)
            {
                series |= ChartSeries.Disk;
            }

            if (_settings.Charts.Frames)
            {
                series |= ChartSeries.Frame;
            }

            return series;
        }
    }

    public bool HasCollectorProblem =>
        _launcher.State is CollectorState.Missing or CollectorState.Failed ||
        (!_connected && _launcher.State == CollectorState.Stopped);

    public event EventHandler? TelemetryUpdated;

    public event EventHandler? ElevationRequested;

    public event EventHandler? PermissionCheckRequested;

    [ObservableProperty]
    private bool _showElevationButton;

    [ObservableProperty]
    private string _cpuValue = "—";

    [ObservableProperty]
    private string _cpuSub = string.Empty;

    [ObservableProperty]
    private string _gpuValue = "—";

    [ObservableProperty]
    private string _gpuSub = string.Empty;

    [ObservableProperty]
    private string _memoryValue = "—";

    [ObservableProperty]
    private string _memorySub = string.Empty;

    [ObservableProperty]
    private string _vramValue = "—";

    [ObservableProperty]
    private string _vramSub = string.Empty;

    [ObservableProperty]
    private string _diskValue = "—";

    [ObservableProperty]
    private string _diskSub = string.Empty;

    [ObservableProperty]
    private string _jitterValue = "—";

    [ObservableProperty]
    private string _jitterSub = string.Empty;

    [ObservableProperty]
    private string _collectorStatusText = string.Empty;

    [ObservableProperty]
    private string _collectorDetailText = string.Empty;

    [ObservableProperty]
    private string _pauseText = string.Empty;

    [ObservableProperty]
    private string _intervalText = "—";

    [ObservableProperty]
    private string _uptimeText = "—";

    [ObservableProperty]
    private string _sampleCountText = "—";

    [ObservableProperty]
    private string _heartbeatText = "—";

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private FrameTargetOption? _selectedFrameTarget;

    [ObservableProperty]
    private string _frameCaptureText = string.Empty;

    [ObservableProperty]
    private string _frameCaptureDetail = string.Empty;

    [ObservableProperty]
    private string _frameValue = "—";

    [ObservableProperty]
    private string _frameSub = string.Empty;

    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1)
        {
            return;
        }

        try
        {
            bool open = _telemetry.Refresh();
            if (open)
            {
                UpdateMetrics();
                UpdateProcesses();
                TimelineCount = _telemetry.CopyTo(_timeline);
                UpdateFrames();
            }

            _statusTicks++;
            if (!_connected || _statusTicks % 4 == 0)
            {
                await RefreshCollectorStatusAsync().ConfigureAwait(true);
            }

            if (_connected && _statusTicks % 4 == 0)
            {
                await TryAutoCaptureAsync().ConfigureAwait(true);
            }

            UpdateFooter();
            TelemetryUpdated?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    [RelayCommand]
    private async Task TogglePauseAsync()
    {
        bool target = !IsPaused;
        if (await _client.SetPausedAsync(target, _cts.Token).ConfigureAwait(true))
        {
            IsPaused = target;
            PauseText = IsPaused ? _loc["Resume"] : _loc["Pause"];
        }
    }

    [RelayCommand]
    private void RefreshFrameTargets()
    {
        int? selectedPid = SelectedFrameTarget?.Pid;
        var options = new List<FrameTargetOption>();

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (name.StartsWith("AzureGameAssistant", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string title = process.MainWindowTitle;
                string display = string.IsNullOrWhiteSpace(title)
                    ? $"{name} ({process.Id})"
                    : $"{name} ({process.Id}) · {title}";
                options.Add(new FrameTargetOption(process.Id, display, title.Length > 0));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        List<FrameTargetOption> ordered = options
            .OrderByDescending(option => option.HasWindow)
            .ThenBy(option => option.Display, StringComparer.CurrentCultureIgnoreCase)
            .Take(200)
            .ToList();

        FrameTargets.Clear();
        foreach (FrameTargetOption option in ordered)
        {
            FrameTargets.Add(option);
        }

        SelectedFrameTarget = ordered.FirstOrDefault(option => option.Pid == selectedPid)
            ?? ordered.FirstOrDefault(option => option.Pid == _telemetry.Latest.ForegroundPid)
            ?? ordered.FirstOrDefault(option => option.HasWindow)
            ?? ordered.FirstOrDefault();
    }

    [RelayCommand]
    private async Task StartFrameCaptureAsync()
    {
        if (SelectedFrameTarget is not { } target)
        {
            FrameCaptureDetail = _loc["FrameCaptureNoTarget"];
            return;
        }

        string? state = await _client.StartFramesAsync(target.Pid, _cts.Token).ConfigureAwait(true);
        if (state is null)
        {
            FrameCaptureDetail = _loc["FrameCaptureStartFailed"];
            return;
        }

        _frameState = state;
        _frameTargetPid = target.Pid;
        _frameError = null;
        _autoCaptureSuppressedPid = null;
        _settings.Frames.TargetPid = target.Pid;
        _settingsService.Save(_settings);
        UpdateFrameTexts();
    }

    [RelayCommand]
    private void RestartElevated() => ElevationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void OpenPermissionCheck() => PermissionCheckRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private async Task StopFrameCaptureAsync()
    {
        string? state = await _client.StopFramesAsync(_cts.Token).ConfigureAwait(true);
        if (state is null)
        {
            return;
        }

        _frameState = "stopped";
        _autoCaptureSuppressedPid = _frameTargetPid;
        _frameTargetPid = null;
        _frameError = null;
        _settings.Frames.TargetPid = null;
        _settingsService.Save(_settings);
        UpdateFrameTexts();
    }

    private async Task TryAutoCaptureAsync()
    {
        if (!_settings.Frames.Enabled || !_settings.Frames.AutoCapture)
        {
            return;
        }

        if (!PermissionDiagnostics.IsElevated())
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        if (now - _lastAutoCaptureAttempt < TimeSpan.FromSeconds(2))
        {
            return;
        }

        uint foregroundPid = _telemetry.Latest.ForegroundPid;
        if (foregroundPid == 0)
        {
            return;
        }

        int pid = (int)foregroundPid;
        if (_autoCaptureSuppressedPid is int suppressed)
        {
            if (suppressed == pid)
            {
                return;
            }

            _autoCaptureSuppressedPid = null;
        }

        if (now < _autoCaptureBlockedUntil && pid == _frameTargetPid)
        {
            return;
        }

        if (_frameTargetPid == pid)
        {
            if (_frameState is "capturing" or "starting")
            {
                return;
            }

            if (_frameState is "permissionDenied" or "unavailable")
            {
                _autoCaptureBlockedUntil = now.AddSeconds(20);
                return;
            }
        }

        // Alt-tabbing to a desktop app must not tear down a live game capture.
        if (_frameTargetPid is int current &&
            current != pid &&
            _frameState is "capturing" or "starting" &&
            IsProcessAlive(current) &&
            !IsGameLikeProcess(pid))
        {
            return;
        }

        if (!IsAutoCaptureCandidate(pid))
        {
            return;
        }

        _lastAutoCaptureAttempt = now;
        string? state = await _client.StartFramesAsync(pid, _cts.Token).ConfigureAwait(true);
        if (state is null)
        {
            return;
        }

        _frameState = state;
        _frameTargetPid = pid;
        _frameError = null;

        if (state == "permissionDenied")
        {
            _autoCaptureBlockedUntil = now.AddMinutes(5);
        }
        else if (state == "unavailable")
        {
            _autoCaptureBlockedUntil = now.AddMinutes(1);
        }

        UpdateFrameTexts();
    }

    private bool IsAutoCaptureCandidate(int pid) => IsGameLikeProcess(pid);

    private bool IsGameLikeProcess(int pid)
    {
        string name = _names.GetName((uint)pid);
        if (string.IsNullOrEmpty(name) ||
            name.StartsWith("AzureGameAssistant", StringComparison.OrdinalIgnoreCase) ||
            AutoCaptureExcluded.Contains(name))
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            IntPtr window = process.MainWindowHandle;
            if (window == IntPtr.Zero)
            {
                return false;
            }

            return IsFullscreen(window) || GpuPercentFor(pid) >= 5.0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsFullscreen(IntPtr window)
    {
        long style = NativeMethods.GetWindowLongPtrW(window, NativeMethods.GwlStyle).ToInt64();
        if ((style & NativeMethods.WsCaption) != 0)
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(window, out NativeMethods.Rect32 bounds))
        {
            return false;
        }

        IntPtr monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMethods.MonitorInfo
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        const int tolerance = 2;
        return bounds.Left <= info.Monitor.Left + tolerance &&
               bounds.Top <= info.Monitor.Top + tolerance &&
               bounds.Right >= info.Monitor.Right - tolerance &&
               bounds.Bottom >= info.Monitor.Bottom - tolerance;
    }

    private double GpuPercentFor(int pid)
    {
        SampleRecord sample = _telemetry.Latest;
        ProcessSlot[] slots = [sample.P0, sample.P1, sample.P2, sample.P3, sample.P4];
        foreach (ProcessSlot slot in slots)
        {
            if (slot.Pid == pid)
            {
                return slot.GpuPct;
            }
        }

        return 0;
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
        catch (Win32Exception)
        {
            return true;
        }
    }

    public PermissionSnapshot GetPermissionSnapshot()
    {
        SampleRecord sample = _telemetry.Latest;
        bool hasSample = _telemetry.Count > 0;

        return new PermissionSnapshot(
            PermissionDiagnostics.IsElevated(),
            PermissionDiagnostics.IsPerformanceLogUser(),
            _telemetry.IsOpen,
            _telemetry.HeartbeatAgeMs,
            _frameState,
            _frameError,
            hasSample && TelemetryMath.TryGetGpuTemperature(sample, out _),
            hasSample && TelemetryMath.TryGetCpuTemperature(sample, out _),
            _settings.Sensors.CpuMode,
            PermissionDiagnostics.ResolvePresentMonPath(_settings.Frames.PresentMonPath));
    }

    private async Task RefreshCollectorStatusAsync()
    {
        CollectorStatusSnapshot? status = await _client.StatusAsync(_cts.Token).ConfigureAwait(true);
        if (status is null)
        {
            _connected = false;
            UpdateCollectorTexts();
            return;
        }

        _connected = true;
        IsPaused = status.Paused;
        PauseText = IsPaused ? _loc["Resume"] : _loc["Pause"];
        _uptimeSeconds = status.UptimeSeconds;
        _samplingIntervalMs = status.SamplingIntervalMs > 0 ? status.SamplingIntervalMs : _settings.Sampling.NormalMs;
        _collectorError = status.LastError;
        _frameState = status.FrameState;
        _frameTargetPid = status.FrameTargetPid;
        _frameError = status.FrameError;

        if (!_helloDone || _appVersion.Length == 0)
        {
            CollectorHello? hello = await _client.HelloAsync(_cts.Token).ConfigureAwait(true);
            if (hello is not null)
            {
                _helloDone = true;
                _appVersion = hello.AppVersion;
                _collectorPid = hello.CollectorPid;
            }
        }

        UpdateCollectorTexts();
        UpdateFrameTexts();
    }

    private void UpdateMetrics()
    {
        SampleRecord sample = _telemetry.Latest;

        CpuValue = sample.CpuUtilityPct.ToString("F1") + "%";
        CpuSub = _loc.Format("CpuFrequencySub", sample.CpuFrequencyPct.ToString("F0"));
        GpuValue = sample.GpuUtilPct.ToString("F1") + "%";
        GpuSub = _loc.Format("GpuMemorySub", sample.GpuDedicatedMb.ToString());
        MemoryValue = TelemetryMath.MemoryUsagePct(sample).ToString("F1") + "%";
        MemorySub = SystemMemory.TryGetUsage(sample, out double usedMb, out double totalMb)
            ? _loc.Format("MemorySub", (usedMb / 1024.0).ToString("F1"), (totalMb / 1024.0).ToString("F1"))
            : _loc.Format(
                "MemorySub",
                (sample.MemCommitMb / 1024.0).ToString("F1"),
                (sample.MemCommitLimitMb / 1024.0).ToString("F1"));
        VramValue = sample.GpuDedicatedMb > 0 ? FormatMegabytes(sample.GpuDedicatedMb) : _loc["NotAvailable"];
        VramSub = TelemetryMath.TryGetVramTotalMb(sample, out double vramTotal)
            ? _loc.Format("VramTotalSub", (vramTotal / 1024.0).ToString("F1"))
            : _loc["VramSub"];
        DiskValue = sample.DiskBusyPct.ToString("F1") + "%";
        DiskSub = _loc.Format("DiskSub", sample.DiskLatencyMs.ToString("F0"));
        JitterValue = sample.TimerJitterMs.ToString("F2") + " ms";
        int interval = _connected && _samplingIntervalMs > 0 ? _samplingIntervalMs : _settings.Sampling.NormalMs;
        JitterSub = _loc.Format("JitterSub", interval);
    }

    private void UpdateProcesses()
    {
        SampleRecord sample = _telemetry.Latest;

        for (int i = 0; i < Processes.Count; i++)
        {
            ProcessRow row = Processes[i];
            ProcessSlot slot = i switch
            {
                0 => sample.P0,
                1 => sample.P1,
                2 => sample.P2,
                3 => sample.P3,
                _ => sample.P4,
            };

            bool foreground = (slot.Flags & ProcessSlotFlags.Foreground) != 0;
            string name = slot.Pid == 0 ? _loc["NotAvailable"] : _names.GetName(slot.Pid);
            if (string.IsNullOrEmpty(name))
            {
                name = _loc["Unknown"];
            }

            row.Update(
                slot.Pid,
                name,
                slot.CpuPct.ToString("F1"),
                slot.GpuPct.ToString("F1"),
                slot.WorkingSetMb,
                foreground,
                foreground ? _loc["Foreground"] : string.Empty);
        }
    }

    private void UpdateFrames()
    {
        long writeIndex = _telemetry.FrameWriteIndex;
        if (writeIndex < _lastFrameIndex)
        {
            _frameStats.Clear();
            _frameTimeline.Clear();
            _frameSnapshot = default;
            _lastFrameIndex = -1;
        }

        if (_lastFrameIndex < 0)
        {
            _lastFrameIndex = Math.Max(0, writeIndex - _frameBuffer.Length);
        }

        SampleRecord anchor = _telemetry.Latest;
        double qpcTicksPerSecond = EstimateQpcTicksPerSecond();
        bool added = false;
        while (_lastFrameIndex < writeIndex)
        {
            int read = _telemetry.ReadFrames(_lastFrameIndex, _frameBuffer);
            if (read <= 0)
            {
                break;
            }

            for (int i = 0; i < read; i++)
            {
                FrameRecord frame = _frameBuffer[i];
                bool spike = _frameStats.Add(frame.FrameTimeMs);
                long utcTicks = anchor.QpcTicks > 0
                    ? FrameTimeline.ToUtcTicks(frame.QpcTicks, anchor.QpcTicks, anchor.UtcTicks, qpcTicksPerSecond)
                    : 0;
                _frameTimeline.Add(utcTicks, frame.FrameTimeMs, spike);
            }

            _lastFrameIndex += read;
            added = true;
        }

        if (added)
        {
            _lastFrameAt = DateTime.UtcNow;
            _frameSnapshot = _frameStats.Snapshot();
        }

        UpdateFrameMetrics();
    }

    private double EstimateQpcTicksPerSecond()
    {
        if (TimelineCount >= 2)
        {
            SampleRecord first = _timeline[0];
            SampleRecord last = _timeline[TimelineCount - 1];
            long qpcDelta = last.QpcTicks - first.QpcTicks;
            long utcDelta = last.UtcTicks - first.UtcTicks;
            if (qpcDelta > 0 && utcDelta > 0)
            {
                return qpcDelta * (double)TimeSpan.TicksPerSecond / utcDelta;
            }
        }

        return 10_000_000;
    }

    private void UpdateFrameMetrics()
    {
        bool fresh = _frameSnapshot.FrameCount > 0 && DateTime.UtcNow - _lastFrameAt < TimeSpan.FromSeconds(2);
        FrameValue = fresh ? _frameSnapshot.InstantFps.ToString("F1") + " FPS" : "—";
        FrameSub = fresh
            ? _loc.Format("FrameStatsSub", _frameSnapshot.AverageFps, _frameSnapshot.OnePercentLowFps)
            : string.Empty;
    }

    private void UpdateCollectorTexts()
    {
        switch (_launcher.State)
        {
            case CollectorState.Missing:
                CollectorStatusText = _loc["StatusCollectorMissing"];
                CollectorDetailText = _launcher.ExecutablePath ?? _loc["StatusCollectorMissing"];
                break;

            case CollectorState.Failed:
                CollectorStatusText = _loc["StatusCollectorFailed"];
                CollectorDetailText = _launcher.LastError ?? _loc["Unknown"];
                break;

            default:
                if (_connected)
                {
                    CollectorStatusText = _loc["StatusConnected"];
                    string version = _appVersion.Length > 0 ? $"v{_appVersion} · PID {_collectorPid}" : $"PID {_collectorPid}";
                    CollectorDetailText = _collectorError is null ? version : $"{version} · {_collectorError}";
                }
                else if (_launcher.State == CollectorState.Starting || _launcher.IsRunning)
                {
                    CollectorStatusText = _loc["StatusStarting"];
                    CollectorDetailText = _launcher.LastError ?? string.Empty;
                }
                else
                {
                    CollectorStatusText = _loc["StatusDisconnected"];
                    CollectorDetailText = _launcher.LastError ?? string.Empty;
                }

                break;
        }

        OnPropertyChanged(nameof(IsConnectedToCollector));
        OnPropertyChanged(nameof(HasCollectorProblem));
    }

    private void UpdateFrameTexts()
    {
        string key = _frameState switch
        {
            "starting" => "FrameStateStarting",
            "capturing" => "FrameStateCapturing",
            "permissionDenied" => "FrameStatePermissionDenied",
            "unavailable" => "FrameStateUnavailable",
            _ => "FrameStateStopped",
        };

        string text = _loc[key];
        FrameCaptureText = _frameTargetPid is int pid ? _loc.Format("FrameStateWithPid", text, pid) : text;
        FrameCaptureDetail = _frameError ?? string.Empty;
    }

    private void UpdateFooter()
    {
        int interval = _connected && _samplingIntervalMs > 0 ? _samplingIntervalMs : _settings.Sampling.NormalMs;

        IntervalText = _loc.Format("Milliseconds", interval);
        UptimeText = FormatUptime(_uptimeSeconds > 0 ? _uptimeSeconds : _telemetry.Count * Math.Max(interval, 1) / 1000.0);
        SampleCountText = _telemetry.Count.ToString();
        HeartbeatText = _telemetry.IsOpen && !double.IsInfinity(_telemetry.HeartbeatAgeMs)
            ? _telemetry.HeartbeatAgeMs.ToString("F0") + " ms"
            : _loc["NotAvailable"];
    }

    public OverlayData GetOverlayData()
    {
        SampleRecord sample = _telemetry.Latest;
        bool hasSample = _telemetry.Count > 0;

        bool hasTemperature = false;
        double temperature = 0;
        bool hasCpuTemperature = false;
        double cpuTemperature = 0;
        double vramTotal = 0;
        if (hasSample)
        {
            hasTemperature = TelemetryMath.TryGetGpuTemperature(sample, out temperature);
            hasCpuTemperature = TelemetryMath.TryGetCpuTemperature(sample, out cpuTemperature);
            TelemetryMath.TryGetVramTotalMb(sample, out vramTotal);
        }

        bool hasFrames = _frameSnapshot.FrameCount > 0 &&
            (DateTime.UtcNow - _lastFrameAt).TotalSeconds < 2.0;

        return new OverlayData(
            HasSample: hasSample,
            HasFrames: hasFrames,
            Fps: hasFrames ? _frameSnapshot.InstantFps : 0,
            AverageFps: hasFrames ? _frameSnapshot.AverageFps : 0,
            LowFps: hasFrames ? _frameSnapshot.OnePercentLowFps : 0,
            CpuPct: hasSample ? sample.CpuUtilityPct : 0,
            GpuPct: hasSample ? sample.GpuUtilPct : 0,
            HasTemperature: hasTemperature,
            TemperatureC: temperature,
            HasCpuTemperature: hasCpuTemperature,
            CpuTemperatureC: cpuTemperature,
            MemoryPct: hasSample ? TelemetryMath.MemoryUsagePct(sample) : 0,
            VramMb: hasSample ? sample.GpuDedicatedMb : 0,
            VramTotalMb: vramTotal);
    }

    public void RefreshDisplaySettings()
    {
        OnPropertyChanged(nameof(ShowCombinedChart));
        OnPropertyChanged(nameof(ShowSeparateCharts));
        OnPropertyChanged(nameof(ShowCpuChart));
        OnPropertyChanged(nameof(ShowGpuChart));
        OnPropertyChanged(nameof(ShowMemoryChart));
        OnPropertyChanged(nameof(ShowVramChart));
        OnPropertyChanged(nameof(ShowDiskChart));
        OnPropertyChanged(nameof(ShowFrameChart));
        OnPropertyChanged(nameof(CombinedSeries));
    }

    private static string FormatMegabytes(double megabytes) => megabytes >= 1024
        ? (megabytes / 1024.0).ToString("F1") + " GB"
        : megabytes.ToString("F0") + " MB";

    private static string FormatUptime(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }

    public void RefreshLocalization()
    {
        PauseText = IsPaused ? _loc["Resume"] : _loc["Pause"];
        UpdateCollectorTexts();
        UpdateFrameTexts();
        UpdateFooter();

        if (_telemetry.Count > 0)
        {
            UpdateMetrics();
            UpdateProcesses();
            UpdateFrameMetrics();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

public sealed partial class ProcessRow : ObservableObject
{
    [ObservableProperty]
    private string _pidText = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _cpuText = string.Empty;

    [ObservableProperty]
    private string _gpuText = string.Empty;

    [ObservableProperty]
    private string _memoryText = string.Empty;

    [ObservableProperty]
    private string _tag = string.Empty;

    [ObservableProperty]
    private bool _isForeground;

    public void Update(uint pid, string name, string cpu, string gpu, uint workingSetMb, bool foreground, string tag)
    {
        PidText = pid == 0 ? string.Empty : pid.ToString();
        Name = pid == 0 ? string.Empty : name;
        CpuText = pid == 0 ? string.Empty : cpu + "%";
        GpuText = pid == 0 ? string.Empty : gpu + "%";
        MemoryText = pid == 0 ? string.Empty : workingSetMb.ToString();
        IsForeground = foreground;
        Tag = tag;
    }
}
