using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Diagnostics;
using AzureGameAssistant.Core.Hardware;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using AzureGameAssistant.UI.ViewModels;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.Linq;

namespace AzureGameAssistant.UI;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private bool _mutexReleased;
    private FileLogger? _logger;
    private SettingsService? _settingsService;
    private AppSettings _settings = new();
    private CollectorLauncher? _launcher;
    private CollectorClient? _client;
    private TelemetryService? _telemetry;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private OverlayWindow? _overlay;
    private TrayIconService? _tray;
    private AiAnalysisService? _ai;
    private AiAnalysisWindow? _aiWindow;
    private DispatcherTimer? _timer;
    private LongRunRecorder? _recorder;
    private UiProcessProbe? _uiProbe;
    private bool _shuttingDown;
    private int _appliedNormalMs = 1_000;
    private int _appliedSensorIntervalMs = 1_000;
    private string _appliedCpuMode = "acpi";
    private bool _appliedSensorsEnabled = true;
    private DateTime _lastHeartbeatLogUtc = DateTime.MinValue;
    private DateTime _lastLogPruneUtc = DateTime.MinValue;
    private string _lastLoggedFrameState = string.Empty;
    private bool? _lastFrameFresh;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        AppPaths.EnsureDirectories();
        _logger = new FileLogger("ui");
        _logger.Info("界面进程启动");

        bool elevatedRelaunch = e.Args.Any(arg =>
            string.Equals(arg, "--elevated-relaunch", StringComparison.OrdinalIgnoreCase));

        _instanceMutex = new Mutex(true, @"Local\AzureGameAssistant.UI", out _ownsMutex);
        if (!_ownsMutex && elevatedRelaunch)
        {
            try
            {
                _ownsMutex = _instanceMutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
        }

        if (!_ownsMutex)
        {
            MessageBox.Show(
                "蓝天游戏助手已在运行。\nAzure Game Assistant is already running.",
                "Azure Game Assistant",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            Shutdown();
            return;
        }

        _settingsService = new SettingsService();
        SettingsLoadResult result = _settingsService.Load();
        _settings = result.Settings;
        _logger.Enabled = _settings.Logs.Enabled;
        if (result.Warning is not null)
        {
            _logger.Warn(result.Warning);
        }

        _appliedNormalMs = _settings.Sampling.NormalMs;
        _appliedSensorIntervalMs = _settings.Sensors.IntervalMs;
        _appliedCpuMode = _settings.Sensors.CpuMode;
        _appliedSensorsEnabled = _settings.Sensors.Enabled;

        int pruned = LogMaintenance.Prune(AppPaths.LogsDirectory, _settings.Logs.RetentionDays);
        _lastLogPruneUtc = DateTime.UtcNow;
        if (pruned > 0)
        {
            _logger.Info($"已清理 {pruned} 个超过 {_settings.Logs.RetentionDays} 天的日志文件");
        }

        bool skipElevation = e.Args.Any(arg =>
            string.Equals(arg, "--no-elevate", StringComparison.OrdinalIgnoreCase));
        if (!elevatedRelaunch && !skipElevation &&
            _settings.RequestElevationOnStartup &&
            !PermissionDiagnostics.IsElevated() &&
            TryRelaunchElevated(e.Args))
        {
            return;
        }

        LocalizationService.Instance.SetLanguage(_settings.Language);
        ThemeService.Apply(_settings.Theme);

        _client = new CollectorClient();
        _telemetry = new TelemetryService();
        _launcher = new CollectorLauncher(_logger);
        _viewModel = new MainViewModel(_telemetry, _client, _launcher, LocalizationService.Instance, _settings, _settingsService);
        _viewModel.TelemetryUpdated += OnTelemetryUpdated;
        _viewModel.ElevationRequested += OnElevationRequested;
        _viewModel.PermissionCheckRequested += OnPermissionCheckRequested;

        _window = new MainWindow(_viewModel);
        _window.SettingsRequested += OnSettingsRequested;
        _window.AiAnalysisRequested += OnAiAnalysisRequested;
        _window.IsVisibleChanged += (_, _) => UpdateTimerState();
        _window.StateChanged += (_, _) => UpdateTimerState();
        _window.Show();

        _overlay = new OverlayWindow();
        _overlay.ApplySettings(_settings.Overlay);
        _overlay.IsVisibleChanged += (_, _) => UpdateTimerState();

        _tray = new TrayIconService(new WindowInteropHelper(_window).Handle);
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.MenuRequested += OnTrayMenuRequested;

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _timer.Tick += OnRefreshTick;

        _launcher.Start();

        StartLongRunRecorder();

        if (e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase)))
        {
            _window.Hide();
        }

        if (_settings.Overlay.Enabled)
        {
            _overlay.ShowOverlay();
        }

        UpdateTimerState();
        RefreshTrayTip();
    }

    private async void OnRefreshTick(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        await _viewModel.RefreshAsync();

        _window?.RefreshChart();
        if (_overlay?.IsVisible == true)
        {
            _overlay.Update(_viewModel.GetOverlayData());
        }

        RecordLongRunSample();
        RecordRuntimeLog();
        UpdateTimerState();
    }

    /// <summary>
    /// Session-long log: one compact status line per minute from launch to exit,
    /// plus explicit frame-data interruptions. Can be turned off in settings.
    /// </summary>
    private void RecordRuntimeLog()
    {
        DateTime now = DateTime.UtcNow;
        if (now - _lastLogPruneUtc >= TimeSpan.FromDays(1))
        {
            _lastLogPruneUtc = now;
            LogMaintenance.Prune(AppPaths.LogsDirectory, _settings.Logs.RetentionDays);
        }

        if (_viewModel is null || _telemetry is null)
        {
            return;
        }

        bool fresh = _viewModel.IsFrameDataFresh;
        if (_lastFrameFresh is bool wasFresh &&
            wasFresh != fresh &&
            _viewModel.IsFrameCaptureRunning)
        {
            _logger?.Info(fresh ? "帧率数据恢复" : "帧率数据中断：超过 5 秒没有新帧");
        }

        _lastFrameFresh = fresh;

        string frameState = _viewModel.FrameStateName;
        bool stateChanged = !string.Equals(frameState, _lastLoggedFrameState, StringComparison.Ordinal);
        if (stateChanged)
        {
            _lastLoggedFrameState = frameState;
        }

        if (!_settings.Logs.Enabled ||
            (!stateChanged && now - _lastHeartbeatLogUtc < TimeSpan.FromSeconds(60)))
        {
            return;
        }

        _lastHeartbeatLogUtc = now;

        if (_telemetry.Count == 0)
        {
            _logger?.Info($"运行状态：等待采集数据，帧采集 {frameState}");
            return;
        }

        SampleRecord sample = _telemetry.Latest;
        string target = _viewModel.FrameTargetPid is int pid ? $"(PID {pid})" : string.Empty;
        _logger?.Info(
            $"运行状态：采样 {_telemetry.Count}，CPU {sample.CpuUtilityPct:F1}%，GPU {sample.GpuUtilPct:F1}%，" +
            $"内存 {TelemetryMath.MemoryUsagePct(sample):F1}%，帧率 {_viewModel.FrameValue}，" +
            $"帧采集 {frameState}{target}");
    }

    private void RecordLongRunSample()
    {
        if (_recorder is null || _uiProbe is null || _viewModel is null)
        {
            return;
        }

        try
        {
            _recorder.Record(_viewModel.CreateDiagnosticsSnapshot(_uiProbe.Sample()));
        }
        catch (Exception ex)
        {
            _logger?.Error("写入长时诊断记录失败", ex);
        }
    }

    private void StartLongRunRecorder()
    {
        if (!_settings.Diagnostics.LongRunRecord || _recorder is not null)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(
                AppPaths.DiagnosticsDirectory,
                "diag-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            _recorder = new LongRunRecorder(directory, _settings.Diagnostics.IntervalSeconds, BuildDiagnosticsContext());
            _uiProbe = new UiProcessProbe();
            _logger?.Info("长时诊断记录开始：" + directory);
        }
        catch (Exception ex)
        {
            _logger?.Error("启动长时诊断记录失败", ex);
            _recorder?.Dispose();
            _recorder = null;
            _uiProbe?.Dispose();
            _uiProbe = null;
        }
    }

    private void SyncLongRunRecorder()
    {
        bool wanted = _settings.Diagnostics.LongRunRecord;
        if (wanted && _recorder is not null && _recorder.IntervalSeconds == _settings.Diagnostics.IntervalSeconds)
        {
            return;
        }

        StopLongRunRecorder("settings");
        if (wanted)
        {
            StartLongRunRecorder();
        }
    }

    private void StopLongRunRecorder(string reason)
    {
        if (_recorder is null)
        {
            return;
        }

        try
        {
            _recorder.Stop(reason);
        }
        catch (Exception ex)
        {
            _logger?.Error("结束长时诊断记录失败", ex);
        }

        _recorder.Dispose();
        _recorder = null;
        _uiProbe?.Dispose();
        _uiProbe = null;
        _logger?.Info("长时诊断记录结束：" + reason);
    }

    private string BuildDiagnosticsContext()
    {
        SystemInventorySnapshot inventory = SystemInventory.Capture();
        string version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var context = new
        {
            app = new { version, theme = _settings.Theme, language = _settings.Language, uiRenderMode = "software" },
            os = inventory.Os,
            cpu = inventory.Cpu,
            logicalProcessors = inventory.LogicalProcessors,
            physicalMemoryGb = inventory.MemoryGb,
            gpu = inventory.Gpu,
            gpuDriver = inventory.Driver,
            sampling = new { normalMs = _settings.Sampling.NormalMs, gameMs = _settings.Sampling.GameMs },
            sensors = new
            {
                enabled = _settings.Sensors.Enabled,
                intervalMs = _settings.Sensors.IntervalMs,
                cpuMode = _settings.Sensors.CpuMode,
            },
            frames = new
            {
                enabled = _settings.Frames.Enabled,
                autoCapture = _settings.Frames.AutoCapture,
                mode = _settings.Frames.Mode,
            },
            charts = new
            {
                separate = _settings.Charts.Separate,
                cpu = _settings.Charts.Cpu,
                gpu = _settings.Charts.Gpu,
                memory = _settings.Charts.Memory,
                vram = _settings.Charts.Vram,
                disk = _settings.Charts.Disk,
                frames = _settings.Charts.Frames,
            },
            overlay = new
            {
                enabled = _settings.Overlay.Enabled,
                corner = _settings.Overlay.Corner,
                theme = _settings.Overlay.Theme,
            },
            ai = new { enabled = _settings.Ai.Enabled },
            diagnostics = new { intervalSeconds = _settings.Diagnostics.IntervalSeconds, schema = 1 },
        };

        return JsonSerializer.Serialize(context, new JsonSerializerOptions { WriteIndented = true });
    }

    private void UpdateTimerState()
    {
        if (_timer is null)
        {
            return;
        }

        bool mainVisible = _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized;
        bool overlayVisible = _overlay?.IsVisible == true;
        bool chartsVisible = _window?.HasVisibleCharts == true;

        // Recording must continue from launch to exit, so the timer never
        // stops; hidden windows just refresh at a slower cadence.
        TimeSpan interval = mainVisible || overlayVisible || chartsVisible
            ? TimeSpan.FromMilliseconds(500)
            : TimeSpan.FromSeconds(2);
        if (_timer.Interval != interval)
        {
            _timer.Interval = interval;
        }

        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    private void SetOverlayEnabled(bool enabled)
    {
        if (_overlay is null)
        {
            return;
        }

        if (enabled)
        {
            _overlay.ApplySettings(_settings.Overlay);
            if (!_overlay.IsVisible)
            {
                _overlay.ShowOverlay();
            }
        }
        else if (_overlay.IsVisible)
        {
            _overlay.Hide();
        }

        UpdateTimerState();
    }

    private void ToggleOverlay()
    {
        _settings.Overlay.Enabled = !_settings.Overlay.Enabled;
        SaveSettingsQuietly("保存悬浮窗设置失败");
        SetOverlayEnabled(_settings.Overlay.Enabled);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        if (_settingsService is null || _window is null)
        {
            return;
        }

        ShowMainWindow();

        var viewModel = new SettingsViewModel(
            _settings,
            LocalizationService.Instance,
            AutoStartService.IsEnabled());

        var window = new SettingsWindow(viewModel, _settings)
        {
            Owner = _window,
        };

        window.SaveRequested += OnSettingsSaved;
        window.ShowDialog();
    }

    private void OnAiAnalysisRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null || _window is null || _settingsService is null)
        {
            return;
        }

        if (!_settings.Ai.Enabled)
        {
            MessageBox.Show(
                LocalizationService.Instance["AiDisabledHint"],
                LocalizationService.Instance["AiWindowTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            OnSettingsRequested(this, EventArgs.Empty);
            return;
        }

        if (_aiWindow is { IsVisible: true })
        {
            _aiWindow.Activate();
            return;
        }

        ShowMainWindow();
        _ai ??= new AiAnalysisService();

        var window = new AiAnalysisWindow(
            _settings,
            _settingsService,
            _ai,
            _viewModel.CreateFactsInput(LocalizationService.Instance.CurrentLanguage))
        {
            Owner = _window,
        };

        _aiWindow = window;
        window.Closed += (_, _) => _aiWindow = null;
        window.ShowDialog();
    }

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        if (_settingsService is null)
        {
            return;
        }

        bool autoStart = AutoStartService.IsEnabled();

        try
        {
            _settingsService.Save(settings);
        }
        catch (IOException ex)
        {
            _logger?.Error("保存设置失败", ex);
            MessageBox.Show(
                "保存设置失败：\n" + ex.Message,
                LocalizationService.Instance["SettingsTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.Error("保存设置失败", ex);
            MessageBox.Show(
                "保存设置失败：\n" + ex.Message,
                LocalizationService.Instance["SettingsTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        bool restart = settings.Sampling.NormalMs != _appliedNormalMs ||
            settings.Sensors.Enabled != _appliedSensorsEnabled ||
            settings.Sensors.IntervalMs != _appliedSensorIntervalMs ||
            !string.Equals(settings.Sensors.CpuMode, _appliedCpuMode, StringComparison.Ordinal);

        ThemeService.Apply(settings.Theme);
        LocalizationService.Instance.SetLanguage(settings.Language);
        _viewModel?.RefreshLocalization();
        _viewModel?.RefreshDisplaySettings();
        _window?.RefreshChart();
        SetOverlayEnabled(settings.Overlay.Enabled);

        if (settings.AutoStart != autoStart)
        {
            AutoStartService.SetEnabled(settings.AutoStart);
        }

        if (_logger is not null)
        {
            _logger.Enabled = settings.Logs.Enabled;
        }

        LogMaintenance.Prune(AppPaths.LogsDirectory, settings.Logs.RetentionDays);
        SyncLongRunRecorder();

        if (restart)
        {
            _appliedNormalMs = settings.Sampling.NormalMs;
            _appliedSensorIntervalMs = settings.Sensors.IntervalMs;
            _appliedCpuMode = settings.Sensors.CpuMode;
            _appliedSensorsEnabled = settings.Sensors.Enabled;
            _ = RestartCollectorAsync();
        }
    }

    private void SaveSettingsQuietly(string context)
    {
        try
        {
            _settingsService?.Save(_settings);
        }
        catch (IOException ex)
        {
            _logger?.Error(context, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.Error(context, ex);
        }
    }

    private void OnPermissionCheckRequested(object? sender, EventArgs e)
    {
        if (_viewModel is null || _window is null)
        {
            return;
        }

        var viewModel = new PermissionViewModel(
            () => _viewModel.GetPermissionSnapshot(),
            LocalizationService.Instance);
        var window = new PermissionWindow(viewModel)
        {
            Owner = _window,
        };

        window.ElevationRequested += (_, _) => OnElevationRequested(this, EventArgs.Empty);
        window.ShowDialog();
    }

    private bool TryRelaunchElevated(string[] args)
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            startInfo.ArgumentList.Add("--elevated-relaunch");
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--elevated-relaunch", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--no-elevate", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                startInfo.ArgumentList.Add(arg);
            }

            Process.Start(startInfo);
            _logger?.Info("已请求管理员权限并重启界面");
            ReleaseInstanceMutex();
            Shutdown();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _logger?.Info("用户取消了 UAC 提权，继续以普通权限运行");
            return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger?.Error("启动时提权失败", ex);
            return false;
        }
    }

    private void ReleaseInstanceMutex()
    {
        if (!_ownsMutex || _mutexReleased)
        {
            return;
        }

        _mutexReleased = true;
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
        }

        _ownsMutex = false;
    }

    private async void OnElevationRequested(object? sender, EventArgs e)
    {
        if (_shuttingDown || _launcher is null)
        {
            return;
        }

        string? executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            return;
        }

        try
        {
            _launcher.MarkIntentionalStop();

            if (_client is not null)
            {
                try
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                    await _client.QuitAsync(cancellation.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                }
            }

            await _launcher.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);

            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "--elevated-relaunch",
                WorkingDirectory = AppContext.BaseDirectory,
            });

            await ShutdownAsync().ConfigureAwait(true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _launcher.Start();
            MessageBox.Show(
                LocalizationService.Instance["ElevationFailed"],
                LocalizationService.Instance["AppTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _launcher.Start();
            _logger?.Error("提权重启失败", ex);
            MessageBox.Show(
                LocalizationService.Instance["ElevationFailed"] + "\n" + ex.Message,
                LocalizationService.Instance["AppTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task RestartCollectorAsync()
    {
        if (_client is null || _launcher is null)
        {
            return;
        }

        _launcher.MarkIntentionalStop();
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
            await _client.QuitAsync(cancellation.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }

        await _launcher.WaitForExitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        _launcher.Start();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        _viewModel?.RefreshLocalization();
        _window?.RefreshLocalization();
        _overlay?.RefreshLocalization();
    }

    private void OnTelemetryUpdated(object? sender, EventArgs e) => RefreshTrayTip();

    private void RefreshTrayTip()
    {
        if (_tray is null || _telemetry is null || _telemetry.Count == 0)
        {
            return;
        }

        SampleRecord latest = _telemetry.Latest;
        _tray.UpdateTip(LocalizationService.Instance.Format(
            "TrayTip",
            latest.CpuUtilityPct.ToString("F0"),
            latest.GpuUtilPct.ToString("F0")));
    }

    private void OnTrayMenuRequested(object? sender, System.Windows.Point point)
    {
        if (_viewModel is null)
        {
            return;
        }

        var menu = new ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        menu.Items.Add(new MenuItem
        {
            Header = LocalizationService.Instance["OpenMain"],
            Command = new RelayCommand(ShowMainWindow),
            FontWeight = FontWeights.SemiBold,
        });

        var mark = new MenuItem
        {
            Header = LocalizationService.Instance["MarkStutter"],
            IsEnabled = false,
        };

        menu.Items.Add(mark);
        menu.Items.Add(new Separator());

        var pause = new MenuItem
        {
            Header = LocalizationService.Instance[_viewModel.IsPaused ? "Resume" : "Pause"],
        };

        pause.Click += async (_, _) => await _viewModel.TogglePauseCommand.ExecuteAsync(null);
        menu.Items.Add(pause);

        menu.Items.Add(new MenuItem
        {
            Header = LocalizationService.Instance["TrayOverlay"],
            IsCheckable = true,
            IsChecked = _settings.Overlay.Enabled,
            Command = new RelayCommand(ToggleOverlay),
        });

        menu.Items.Add(new MenuItem
        {
            Header = LocalizationService.Instance[_settings.Notifications ? "MuteNotifications" : "UnmuteNotifications"],
            Command = new RelayCommand(ToggleNotifications),
        });

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = LocalizationService.Instance["Settings"],
            Command = new RelayCommand(() =>
            {
                ShowMainWindow();
                OnSettingsRequested(this, EventArgs.Empty);
            }),
        });

        menu.Items.Add(new MenuItem
        {
            Header = LocalizationService.Instance["QuitApp"],
            Command = new AsyncRelayCommand(ShutdownAsync),
        });

        menu.IsOpen = true;
    }

    private void ToggleNotifications()
    {
        _settings.Notifications = !_settings.Notifications;
        SaveSettingsQuietly("保存通知设置失败");
    }

    private async Task ShutdownAsync()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _logger?.Info("界面退出");

        _timer?.Stop();
        StopLongRunRecorder("exit");

        if (_overlay is not null)
        {
            _overlay.Close();
            _overlay = null;
        }

        if (_launcher is not null)
        {
            _launcher.MarkIntentionalStop();
        }

        if (_client is not null)
        {
            try
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                await _client.QuitAsync(cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _tray?.Dispose();
        _tray = null;

        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
            _window = null;
        }

        _aiWindow?.Close();
        _aiWindow = null;
        _ai?.Dispose();
        _viewModel?.Dispose();
        _client?.Dispose();
        _launcher?.Dispose();
        _telemetry?.Dispose();
        _ai = null;
        _viewModel = null;
        _client = null;
        _launcher = null;
        _telemetry = null;

        _logger?.Info("界面已退出");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ReleaseInstanceMutex();
        _instanceMutex?.Dispose();
        _logger?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.Error("界面未处理异常", e.Exception);
        MessageBox.Show(
            "界面发生错误，已记录到日志。\n" + e.Exception.Message,
            "Azure Game Assistant",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            _logger?.Error("界面域未处理异常", exception);
        }
        else
        {
            _logger?.Error($"界面域未处理异常：{e.ExceptionObject}");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("未观察的任务异常", e.Exception);
        e.SetObserved();
    }
}
