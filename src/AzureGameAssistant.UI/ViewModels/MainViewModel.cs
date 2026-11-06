using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;

namespace AzureGameAssistant.UI.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TelemetryService _telemetry;
    private readonly CollectorClient _client;
    private readonly CollectorLauncher _launcher;
    private readonly ProcessNameCache _names = new();
    private readonly LocalizationService _loc;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();

    private readonly SampleRecord[] _timeline = new SampleRecord[TelemetryLayout.SampleCapacity];
    private int _refreshing;
    private int _statusTicks;
    private bool _connected;
    private bool _helloDone;
    private string _appVersion = string.Empty;
    private int _collectorPid;
    private string? _collectorError;
    private double _uptimeSeconds;
    private int _samplingIntervalMs;

    public MainViewModel(
        TelemetryService telemetry,
        CollectorClient client,
        CollectorLauncher launcher,
        LocalizationService loc,
        AppSettings settings)
    {
        _telemetry = telemetry;
        _client = client;
        _launcher = launcher;
        _loc = loc;
        _settings = settings;

        for (int i = 0; i < 5; i++)
        {
            Processes.Add(new ProcessRow());
        }

        PauseText = _loc["Pause"];
        UpdateCollectorTexts();
        UpdateFooter();
    }

    public ObservableCollection<ProcessRow> Processes { get; } = new();

    public SampleRecord[] TimelineBuffer => _timeline;

    public int TimelineCount { get; private set; }

    public SampleRecord Latest => _telemetry.Latest;

    public bool IsConnectedToCollector => _connected;

    public bool HasCollectorProblem =>
        _launcher.State is CollectorState.Missing or CollectorState.Failed ||
        (!_connected && _launcher.State == CollectorState.Stopped);

    public event EventHandler? TelemetryUpdated;

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
            }

            _statusTicks++;
            if (!_connected || _statusTicks % 4 == 0)
            {
                await RefreshCollectorStatusAsync().ConfigureAwait(true);
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
    }

    private void UpdateMetrics()
    {
        SampleRecord sample = _telemetry.Latest;

        CpuValue = sample.CpuUtilityPct.ToString("F1") + "%";
        CpuSub = _loc.Format("CpuFrequencySub", sample.CpuFrequencyPct.ToString("F0"));
        GpuValue = sample.GpuUtilPct.ToString("F1") + "%";
        GpuSub = _loc.Format("GpuMemorySub", sample.GpuDedicatedMb.ToString());
        MemoryValue = (sample.MemAvailableMb / 1024.0).ToString("F1") + " GB";
        MemorySub = _loc.Format(
            "MemorySub",
            (sample.MemCommitMb / 1024.0).ToString("F1"),
            (sample.MemCommitLimitMb / 1024.0).ToString("F1"));
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
        UpdateFooter();

        if (_telemetry.Count > 0)
        {
            UpdateMetrics();
            UpdateProcesses();
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
