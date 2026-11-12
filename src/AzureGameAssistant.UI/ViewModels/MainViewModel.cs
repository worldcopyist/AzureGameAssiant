using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.Core.Diagnostics;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Hardware;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.Core.Sessions;
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
    private readonly FrameDropTracker _dropTracker = new();
    private readonly HighLoadTracker _highLoadTracker = new();
    private readonly DetectionEngine _detector;
    private readonly SessionRecorder _sessions;
    private readonly GameModeTracker _gameMode = new();

    private const int StutterEventCapacity = 50;
    private const string PresentMonVersion = "2.6.0";
    private long _lastStutterSampleTicks;

    private const int DropMarkerCapacity = 64;
    private const int HighLoadMarkerCapacity = 16;
    private readonly FrameTimePoint[] _frameTimelineBuffer = new FrameTimePoint[FrameTimelineCapacity];
    private int _refreshing;
    private int _statusTicks;
    private long _lastFrameIndex = -1;
    private DateTime _lastFrameAt = DateTime.MinValue;
    private FrameStatsSnapshot _frameSnapshot;
    private long _framesSeen;
    private long _spikesSeen;
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
    private long _lastLoadSampleTicks;
    private DateTime _lastAutoCaptureAttempt = DateTime.MinValue;
    private DateTime _autoCaptureBlockedUntil = DateTime.MinValue;
    private int? _autoCaptureBlockedPid;
    private int? _autoCaptureSuppressedPid;
    private int? _autoCaptureCandidatePid;
    private int? _autoCaptureStartedPid;
    private int? _autoCaptureStopPendingPid;
    private DateTime _autoCaptureCandidateSince = DateTime.MinValue;
    private int _samplingTargetMs;
    private int? _gameProbePid;
    private uint _gameProbeForeground;
    private DateTime _gameProbeAt = DateTime.MinValue;

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
        "dwm",
        "RuntimeBroker",
        "GameBar",
        "GameBarFTServer",
        "PhoneExperienceHost",
    };

    // Launchers, recorders, browsers and desktop tools can own a fullscreen
    // window or show GPU usage; capturing them would steal the target from a
    // real game. Substring match keeps related builds (obs64, obs32) covered.
    private static readonly string[] AutoCaptureExcludedMarkers =
    [
        "obs",
        "streamlabs",
        "xsplit",
        "discord",
        "chrome",
        "msedge",
        "firefox",
        "opera",
        "brave",
        "vivaldi",
        "browser",
        "steam",
        "epicgames",
        "battle.net",
        "galaxyclient",
        "ubisoftconnect",
        "riotclient",
        "nvidia",
        "radeon",
        "afterburner",
        "rtss",
        "hwinfo",
        "wallpaper",
        "terminal",
        "powershell",
        "notepad",
        "devenv",
        "vlc",
        "potplayer",
        "wmplayer",
        "spotify",
        "teams",
        "slack",
        "telegram",
        "wechat",
        "weixin",
        "obsidian",
    ];

    private static readonly TimeSpan FrameFreshWindow = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrameHoldWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OverlayFrameHoldWindow = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan AutoCaptureDwell = TimeSpan.FromSeconds(2);

    // After a pause PresentMon reports the whole idle gap as one frame time,
    // often seconds long (observed 1529 ms and 19278 ms). Such a frame is a
    // capture interruption, not a rendered frame, and must not enter the FPS
    // window; a real 60 fps game never has a legitimate one-second frame.
    private const double DiscontinuityFrameMs = 1000.0;

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
        _detector = new DetectionEngine(_settings.Detection);
        _sessions = new SessionRecorder(
            AppPaths.SessionsDirectory,
            _settings.Sessions.MaxCount,
            _settings.Sessions.MaxTotalMb,
            pid => _names.GetName(pid),
            () => _loc.CurrentLanguage);
        _sessions.SessionWritten += (_, _) => SessionsChanged?.Invoke(this, EventArgs.Empty);

        for (int i = 0; i < 5; i++)
        {
            Processes.Add(new ProcessRow());
        }

        ShowElevationButton = !PermissionDiagnostics.IsElevated();
        _samplingTargetMs = Math.Clamp(settings.Sampling.NormalMs, 100, 10_000);
        PauseText = _loc["Pause"];
        UpdateCollectorTexts();
        UpdateFrameTexts();
        UpdateFooter();
    }

    public ObservableCollection<ProcessRow> Processes { get; } = new();

    public ObservableCollection<FrameTargetOption> FrameTargets { get; } = new();

    public ObservableCollection<StutterEventRow> StutterEvents { get; } = new();

    [ObservableProperty]
    private int _stutterEventCount;

    public string StutterSummaryText => StutterEventCount == 0
        ? _loc["StutterEmpty"]
        : _loc.Format("StutterCount", StutterEventCount);

    public event EventHandler<StutterEvent>? StutterDetected;

    public event EventHandler? SessionsChanged;

    public SessionRecorder Sessions => _sessions;

    partial void OnStutterEventCountChanged(int value) =>
        OnPropertyChanged(nameof(StutterSummaryText));

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

        var dropBuffer = new FrameDropMarker[DropMarkerCapacity];
        int dropCount = _dropTracker.CopyTo(dropBuffer);
        var loadBuffer = new HighLoadMarker[HighLoadMarkerCapacity];
        int loadCount = _highLoadTracker.CopyTo(loadBuffer);

        for (int i = 0; i < dropCount; i++)
        {
            uint pid = dropBuffer[i].SuspectPid;
            if (pid != 0 && !names.ContainsKey(pid))
            {
                names[pid] = _names.GetName(pid);
            }
        }

        for (int i = 0; i < loadCount; i++)
        {
            AddMarkerName(loadBuffer[i].TopPid);
            AddMarkerName(loadBuffer[i].SecondPid);

            void AddMarkerName(uint pid)
            {
                if (pid != 0 && !names.ContainsKey(pid))
                {
                    names[pid] = _names.GetName(pid);
                }
            }
        }

        string mode = _frameState == "capturing" || _settings.Sensors.Enabled ? "deep" : "shallow";
        return new FactsInput(
            samples,
            frameBuffer.AsSpan(0, frameCount).ToArray(),
            names,
            mode,
            locale,
            null,
            dropCount > 0 ? dropBuffer.AsSpan(0, dropCount).ToArray() : null,
            loadCount > 0 ? loadBuffer.AsSpan(0, loadCount).ToArray() : null);
    }

    public SampleRecord Latest => _telemetry.Latest;

    /// <summary>Read-only, non-identifying hardware labels for the CPU/GPU cards.</summary>
    public string CpuName { get; } = CleanCpuName(Inventory.Cpu);

    public string GpuName { get; } = PickGpuName(Inventory.Gpu);

    private static readonly SystemInventorySnapshot Inventory = SystemInventory.Capture();

    private static string CleanCpuName(string name)
    {
        string text = name
            .Replace("(R)", string.Empty, StringComparison.Ordinal)
            .Replace("(TM)", string.Empty, StringComparison.Ordinal)
            .Replace("CPU ", string.Empty, StringComparison.Ordinal);
        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        return text.Trim();
    }

    private static string PickGpuName(IReadOnlyList<string> adapters)
    {
        foreach (string adapter in adapters)
        {
            if (!adapter.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) &&
                !adapter.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
            {
                return adapter;
            }
        }

        return adapters.Count > 0 ? adapters[0] : string.Empty;
    }

    public bool IsConnectedToCollector => _connected;

    public string FrameStateName => _frameState;

    public int? FrameTargetPid => _frameTargetPid;

    public bool IsFrameCaptureRunning => _frameState is "capturing" or "starting";

    public bool IsFrameDataFresh =>
        _frameSnapshot.FrameCount > 0 &&
        _lastFrameAt != DateTime.MinValue &&
        DateTime.UtcNow - _lastFrameAt < FrameFreshWindow;

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
                TrackHighLoad();
                _detector.DeepMode = IsFrameDataFresh;
                UpdateFrames();
                TrackStutter();
                DrainStutterEvents();
                int sessionFrames = CopyFrameTimeline();
                _sessions.Observe(DateTime.UtcNow, _timeline, TimelineCount, _frameTimelineBuffer, sessionFrames);
            }
            else
            {
                _highLoadTracker.Suspend();
            }

            _statusTicks++;
            if (!_connected || _statusTicks % 4 == 0)
            {
                await RefreshCollectorStatusAsync().ConfigureAwait(true);
            }

            if (_connected)
            {
                if (_autoCaptureStopPendingPid is int pendingPid)
                {
                    await StopAutoFramesAsync(pendingPid).ConfigureAwait(true);
                }

                await UpdateGameModeAsync().ConfigureAwait(true);

                if (_statusTicks % 4 == 0)
                {
                    await TryAutoCaptureAsync().ConfigureAwait(true);
                }
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
        _autoCaptureStartedPid = null;
        _autoCaptureStopPendingPid = null;
        _settings.Frames.TargetPid = target.Pid;
        _settingsService.Save(_settings);
        UpdateFrameTexts();
    }

    [RelayCommand]
    private void RestartElevated() => ElevationRequested?.Invoke(this, EventArgs.Empty);

    public DetachedChartSettings DetachedChartPlacement => _settings.Charts.Detached;

    public void SaveDetachedChartPlacement(double left, double top, double width, double height, bool topmost)
    {
        DetachedChartSettings target = _settings.Charts.Detached;
        target.Left = left;
        target.Top = top;
        target.Width = width;
        target.Height = height;
        target.Topmost = topmost;
        _settingsService.Save(_settings);
    }

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
        _autoCaptureStartedPid = null;
        _autoCaptureStopPendingPid = null;
        _settings.Frames.TargetPid = null;
        _settingsService.Save(_settings);
        UpdateFrameTexts();
    }

    /// <summary>
    /// Raises the sampler to the game interval while a fullscreen game owns the
    /// foreground, and restores the normal interval (plus the auto-started
    /// frame capture) after the exit delay. The collector is never restarted.
    /// </summary>
    private async Task UpdateGameModeAsync()
    {
        if (!_settings.GameDetection.Enabled)
        {
            if (_gameMode.IsActive)
            {
                _gameMode.Reset();
                int normalMs = Math.Clamp(_settings.Sampling.NormalMs, 100, 10_000);
                await ApplySamplingAsync(normalMs, gameMode: false).ConfigureAwait(true);
            }

            return;
        }

        DateTime now = DateTime.UtcNow;
        GameModeUpdate update = _gameMode.Observe(now, DetectGamePid(now));

        if (update.Active)
        {
            int gameMs = Math.Clamp(_settings.Sampling.GameMs, 100, 10_000);
            await ApplySamplingAsync(gameMs, gameMode: true).ConfigureAwait(true);
            return;
        }

        if (update.ExitDue)
        {
            int normalMs = Math.Clamp(_settings.Sampling.NormalMs, 100, 10_000);
            await ApplySamplingAsync(normalMs, gameMode: false).ConfigureAwait(true);
            await StopAutoFramesAsync().ConfigureAwait(true);
        }
    }

    private async Task ApplySamplingAsync(int intervalMs, bool gameMode)
    {
        if (_samplingTargetMs == intervalMs)
        {
            return;
        }

        if (await _client.SetSamplingIntervalAsync(intervalMs, gameMode, _cts.Token).ConfigureAwait(true))
        {
            _samplingTargetMs = intervalMs;
            _samplingIntervalMs = intervalMs;
        }
        else
        {
            _samplingTargetMs = 0;
        }
    }

    /// <summary>Stops only the capture that game detection started itself.</summary>
    private async Task StopAutoFramesAsync()
    {
        if (_autoCaptureStartedPid is not int pid ||
            _frameTargetPid != pid ||
            _frameState is not ("capturing" or "starting"))
        {
            _autoCaptureStartedPid = null;
            return;
        }

        _autoCaptureStartedPid = null;
        // Leaving fullscreen must not let auto-capture pick the same target up
        // again on the next tick while the player is still in that window.
        _autoCaptureSuppressedPid = pid;
        await StopAutoFramesAsync(pid).ConfigureAwait(true);
    }

    /// <summary>
    /// Sends frames.stop for an auto-started capture and keeps the request
    /// pending until the collector acknowledges it, so a single slow or lost
    /// reply cannot leave PresentMon running after the game has exited.
    /// </summary>
    private async Task StopAutoFramesAsync(int pid)
    {
        _autoCaptureStopPendingPid = pid;
        string? state = await _client.StopFramesAsync(_cts.Token).ConfigureAwait(true);
        if (state is null)
        {
            return;
        }

        _autoCaptureStopPendingPid = null;
        if (_frameTargetPid == pid)
        {
            _frameState = "stopped";
            _frameTargetPid = null;
            _frameError = null;
            UpdateFrameTexts();
        }
    }

    // Process queries cost more than the telemetry read, so cache the result
    // for a second and only re-probe immediately when the foreground changes.
    private int? DetectGamePid(DateTime now)
    {
        uint foregroundPid = _telemetry.Latest.ForegroundPid;
        if (foregroundPid == _gameProbeForeground &&
            now - _gameProbeAt < TimeSpan.FromSeconds(1))
        {
            return _gameProbePid;
        }

        _gameProbeAt = now;
        _gameProbeForeground = foregroundPid;
        _gameProbePid = foregroundPid != 0 && IsGameLikeProcess((int)foregroundPid)
            ? (int)foregroundPid
            : null;
        return _gameProbePid;
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
        if (now < _autoCaptureBlockedUntil)
        {
            if (_autoCaptureBlockedPid == pid)
            {
                return;
            }
        }
        else
        {
            _autoCaptureBlockedUntil = DateTime.MinValue;
            _autoCaptureBlockedPid = null;
        }

        if (_autoCaptureSuppressedPid is int suppressed)
        {
            if (suppressed == pid)
            {
                return;
            }

            _autoCaptureSuppressedPid = null;
        }

        if (_frameTargetPid == pid)
        {
            if (_frameState is "capturing" or "starting")
            {
                return;
            }

            if (_frameState is "permissionDenied" or "unavailable")
            {
                // A failed target cools down only while it stays in the
                // background; the player returning to the game retries at once.
                if (_frameState == "permissionDenied" || !IsAutoCaptureCandidate(pid))
                {
                    _autoCaptureBlockedUntil = now.AddSeconds(20);
                    _autoCaptureBlockedPid = pid;
                    return;
                }

                _autoCaptureBlockedUntil = DateTime.MinValue;
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
            _autoCaptureCandidatePid = null;
            return;
        }

        // Require the candidate to stay in the foreground for a moment so a
        // transient window switch does not restart capture on the wrong app.
        if (_autoCaptureCandidatePid != pid)
        {
            _autoCaptureCandidatePid = pid;
            _autoCaptureCandidateSince = now;
            return;
        }

        if (now - _autoCaptureCandidateSince < AutoCaptureDwell)
        {
            return;
        }

        _lastAutoCaptureAttempt = now;
        string? state = await _client.StartFramesAsync(pid, _cts.Token).ConfigureAwait(true);
        if (state is null)
        {
            // The collector answered with an error (missing PresentMon, invalid
            // target). Without a cooldown this retries every tick and floods
            // both logs.
            _autoCaptureBlockedUntil = now.AddSeconds(15);
            _autoCaptureBlockedPid = pid;
            return;
        }

        _frameState = state;
        _frameTargetPid = pid;
        _frameError = null;
        _autoCaptureStartedPid = state is "capturing" or "starting" ? pid : null;

        if (state == "permissionDenied")
        {
            _autoCaptureBlockedUntil = now.AddMinutes(5);
            _autoCaptureBlockedPid = pid;
        }
        else if (state == "unavailable")
        {
            _autoCaptureBlockedUntil = now.AddMinutes(1);
            _autoCaptureBlockedPid = pid;
        }

        UpdateFrameTexts();
    }

    private bool IsAutoCaptureCandidate(int pid) => IsGameLikeProcess(pid);

    private bool IsGameLikeProcess(int pid)
    {
        string name = _names.GetName((uint)pid);
        if (string.IsNullOrEmpty(name) || IsExcludedCaptureProcess(name))
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

            // Exclusive fullscreen (D3D) is trusted for the foreground process;
            // borderless games are recognized by monitor coverage.
            return IsD3DFullscreen() || IsFullscreen(window) || GpuPercentFor(pid) >= 5.0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private bool IsExcludedCaptureProcess(string name)
    {
        if (name.StartsWith("AzureGameAssistant", StringComparison.OrdinalIgnoreCase) ||
            AutoCaptureExcluded.Contains(name))
        {
            return true;
        }

        foreach (string marker in AutoCaptureExcludedMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string excluded in _settings.GameDetection.Exclude)
        {
            string value = excluded.Trim();
            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^4];
            }

            if (value.Length > 0 && value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

        return GameDisplayRules.CoversMonitor(
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom,
            info.Monitor.Left,
            info.Monitor.Top,
            info.Monitor.Right,
            info.Monitor.Bottom);
    }

    private static bool IsD3DFullscreen()
    {
        return NativeMethods.SHQueryUserNotificationState(out int state) == 0 &&
               state == NativeMethods.QunsRunningD3DFullscreen;
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
            _samplingTargetMs = 0;
            UpdateCollectorTexts();
            return;
        }

        _connected = true;
        IsPaused = status.Paused;
        PauseText = IsPaused ? _loc["Resume"] : _loc["Pause"];
        _uptimeSeconds = status.UptimeSeconds;
        _samplingIntervalMs = status.SamplingIntervalMs > 0 ? status.SamplingIntervalMs : _settings.Sampling.NormalMs;
        _samplingTargetMs = _samplingIntervalMs;
        _collectorError = status.LastError;
        if (status.FrameTargetPid != _frameTargetPid)
        {
            _frameSnapshot = default;
            _lastFrameAt = DateTime.MinValue;
            _dropTracker.Clear();
            _highLoadTracker.Clear();
            _detector.Reset();
        }

        _frameState = status.FrameState;
        _frameTargetPid = status.FrameTargetPid;
        _frameError = status.FrameError;
        if (_frameState == "stopped")
        {
            _lastFrameAt = DateTime.MinValue;
            _autoCaptureStartedPid = null;
        }

        if (_autoCaptureStopPendingPid is int pendingStop &&
            (_frameState == "stopped" || status.FrameTargetPid != pendingStop))
        {
            _autoCaptureStopPendingPid = null;
        }

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

    private void TrackHighLoad()
    {
        if (_telemetry.Count == 0)
        {
            return;
        }

        SampleRecord sample = _telemetry.Latest;
        if (sample.UtcTicks != 0 && sample.UtcTicks == _lastLoadSampleTicks)
        {
            return;
        }

        _lastLoadSampleTicks = sample.UtcTicks;
        long utcTicks = sample.UtcTicks > 0 ? sample.UtcTicks : DateTime.UtcNow.Ticks;
        _highLoadTracker.Observe(sample, utcTicks);
    }

    private void TrackStutter()
    {
        if (_telemetry.Count == 0)
        {
            return;
        }

        SampleRecord sample = _telemetry.Latest;
        if (sample.UtcTicks == 0 || sample.UtcTicks == _lastStutterSampleTicks)
        {
            return;
        }

        _lastStutterSampleTicks = sample.UtcTicks;
        _detector.ObserveSample(sample);
    }

    private void DrainStutterEvents()
    {
        while (_detector.TryDequeue(out StutterEvent stutterEvent))
        {
            AddStutterEvent(stutterEvent);
        }
    }

    [RelayCommand]
    private void MarkStutter()
    {
        _detector.MarkManual(DateTime.UtcNow.Ticks);
        DrainStutterEvents();
    }

    private void AddStutterEvent(StutterEvent stutterEvent)
    {
        string name = stutterEvent.ForegroundPid == 0
            ? _loc["NotAvailable"]
            : _names.GetName(stutterEvent.ForegroundPid);
        if (string.IsNullOrEmpty(name))
        {
            name = _loc["Unknown"];
        }

        StutterEvents.Insert(0, new StutterEventRow(stutterEvent, name, _loc));
        while (StutterEvents.Count > StutterEventCapacity)
        {
            StutterEvents.RemoveAt(StutterEvents.Count - 1);
        }

        StutterEventCount++;
        StutterDetected?.Invoke(this, stutterEvent);

        int frameCount = CopyFrameTimeline();
        _sessions.Begin(
            stutterEvent,
            BuildSessionContext(),
            _timeline,
            TimelineCount,
            _frameTimelineBuffer,
            frameCount);
    }

    private SessionContext BuildSessionContext()
    {
        bool deep = _frameState == "capturing" || IsFrameDataFresh || _settings.Sensors.Enabled;
        var capabilities = new List<string>(6) { "samples", "processes" };
        if (_frameState == "capturing" || IsFrameDataFresh)
        {
            capabilities.Add("frames");
        }

        if (_settings.Sensors.Enabled)
        {
            capabilities.Add("sensors");
        }

        if (_telemetry.Count > 0 && TelemetryMath.HasVendorData(_telemetry.Latest))
        {
            capabilities.Add("gpu");
            capabilities.Add("vram");
        }

        if (PermissionDiagnostics.IsElevated())
        {
            capabilities.Add("elevated");
        }

        return new SessionContext(
            _appVersion,
            Environment.OSVersion.VersionString,
            CpuName,
            GpuName,
            deep ? "deep" : "shallow",
            _samplingIntervalMs > 0 ? _samplingIntervalMs : _settings.Sampling.NormalMs,
            PresentMonVersion,
            _settings.Sensors.Enabled ? _settings.Sensors.CpuMode : "off",
            PermissionDiagnostics.IsElevated(),
            _settings.Sensors.CpuMode,
            capabilities.ToArray(),
            _loc.CurrentLanguage);
    }

    private void UpdateFrames()
    {
        long writeIndex = _telemetry.FrameWriteIndex;
        if (writeIndex < _lastFrameIndex)
        {
            _frameStats.Clear();
            _frameTimeline.Clear();
            _dropTracker.Clear();
            _detector.Reset();
            _frameSnapshot = default;
            _lastFrameIndex = -1;
        }

        if (_lastFrameIndex < 0)
        {
            _lastFrameIndex = Math.Max(0, writeIndex - _frameBuffer.Length);
        }

        SampleRecord anchor = _telemetry.Latest;
        double qpcTicksPerSecond = EstimateQpcTicksPerSecond();
        double baselineFps = _frameSnapshot.AverageFps;
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
                long utcTicks = anchor.QpcTicks > 0
                    ? FrameTimeline.ToUtcTicks(frame.QpcTicks, anchor.QpcTicks, anchor.UtcTicks, qpcTicksPerSecond)
                    : 0;

                if (frame.FrameTimeMs >= DiscontinuityFrameMs)
                {
                    // Mark the gap on the chart but keep it out of the stats.
                    _frameTimeline.Add(utcTicks, frame.FrameTimeMs, true);
                    continue;
                }

                bool spike = _frameStats.Add(frame.FrameTimeMs);
                _framesSeen++;
                if (spike)
                {
                    _spikesSeen++;
                }

                _frameTimeline.Add(
                    utcTicks,
                    frame.FrameTimeMs,
                    spike,
                    frame.CpuWaitMs,
                    frame.GpuWaitMs,
                    frame.DisplayLatencyMs);
                _detector.ObserveFrame(utcTicks, frame.FrameTimeMs, frame.DisplayLatencyMs);
                _dropTracker.Observe(
                    utcTicks > 0 ? utcTicks : DateTime.UtcNow.Ticks,
                    frame,
                    spike,
                    baselineFps,
                    anchor);
            }

            _lastFrameIndex += read;
            added = true;
        }

        if (added)
        {
            _lastFrameAt = DateTime.UtcNow;
            _frameSnapshot = _frameStats.Snapshot();
        }

        _dropTracker.Tick(DateTime.UtcNow.Ticks);
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
        TimeSpan age = DateTime.UtcNow - _lastFrameAt;
        bool hasFrames = _frameSnapshot.FrameCount > 0 && _lastFrameAt != DateTime.MinValue;

        // PresentMon and ETW can pause for a few seconds; the supervisor now
        // lets it recover, so hold the last reading instead of blanking the card.
        bool holding = hasFrames && age < FrameHoldWindow;

        FrameValue = holding ? _frameSnapshot.InstantFps.ToString("F1") + " FPS" : "—";
        FrameSub = holding
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

    /// <summary>
    /// Snapshot for the long-run diagnostics recorder. Process names only, no
    /// window titles or paths.
    /// </summary>
    public DiagnosticsSnapshot CreateDiagnosticsSnapshot(UiProcessMetrics ui)
    {
        SampleRecord sample = _telemetry.Latest;
        bool hasSample = _telemetry.Count > 0;

        double gpuTemperature = 0;
        double cpuTemperature = 0;
        double vramTotal = 0;
        bool hasGpuTemperature = hasSample && TelemetryMath.TryGetGpuTemperature(sample, out gpuTemperature);
        bool hasCpuTemperature = hasSample && TelemetryMath.TryGetCpuTemperature(sample, out cpuTemperature);
        if (hasSample)
        {
            TelemetryMath.TryGetVramTotalMb(sample, out vramTotal);
        }

        double gpuHotspot = hasSample && TelemetryMath.HasVendorData(sample) ? sample.V0.HotSpotC : 0;
        double heartbeat = _telemetry.IsOpen && !double.IsInfinity(_telemetry.HeartbeatAgeMs)
            ? _telemetry.HeartbeatAgeMs
            : 0;

        var processes = new List<ProcessDiagnostic>(5);
        if (hasSample)
        {
            ProcessSlot[] slots = [sample.P0, sample.P1, sample.P2, sample.P3, sample.P4];
            foreach (ProcessSlot slot in slots)
            {
                if (slot.Pid == 0)
                {
                    continue;
                }

                processes.Add(new ProcessDiagnostic(
                    slot.Pid,
                    _names.GetName(slot.Pid),
                    slot.CpuPct,
                    slot.GpuPct,
                    slot.WorkingSetMb));
            }
        }

        bool framesFresh = _frameSnapshot.FrameCount > 0 &&
            _lastFrameAt != DateTime.MinValue &&
            (DateTime.UtcNow - _lastFrameAt) < FrameFreshWindow;
        double frameAge = _lastFrameAt == DateTime.MinValue
            ? 0
            : Math.Max(0, (DateTime.UtcNow - _lastFrameAt).TotalSeconds);

        return new DiagnosticsSnapshot(
            DateTime.UtcNow,
            _uptimeSeconds,
            _samplingIntervalMs,
            hasSample,
            sample,
            _connected,
            heartbeat,
            _telemetry.WriteIndex,
            _collectorPid,
            _collectorError,
            _frameState,
            _frameTargetPid,
            _frameError,
            framesFresh,
            frameAge,
            _telemetry.FrameWriteIndex,
            _frameSnapshot,
            _framesSeen,
            _spikesSeen,
            hasGpuTemperature,
            gpuTemperature,
            gpuHotspot,
            hasCpuTemperature,
            cpuTemperature,
            sample.GpuDedicatedMb,
            vramTotal,
            ui,
            processes);
    }

    public OverlayData GetOverlayData()    {
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
            _lastFrameAt != DateTime.MinValue &&
            (DateTime.UtcNow - _lastFrameAt) < OverlayFrameHoldWindow;

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
        OnPropertyChanged(nameof(StutterSummaryText));
        foreach (StutterEventRow row in StutterEvents)
        {
            row.RefreshTexts(_loc);
        }

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

        int frameCount = CopyFrameTimeline();
        _sessions.FlushPending(DateTime.UtcNow, _timeline, TimelineCount, _frameTimelineBuffer, frameCount);
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
