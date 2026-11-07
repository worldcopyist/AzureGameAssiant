using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AzureGameAssistant.Core;
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
    private DispatcherTimer? _timer;
    private bool _shuttingDown;

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

        _instanceMutex = new Mutex(true, @"Local\AzureGameAssistant.UI", out _ownsMutex);
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
        if (result.Warning is not null)
        {
            _logger.Warn(result.Warning);
        }

        LocalizationService.Instance.SetLanguage(_settings.Language);
        ThemeService.Apply(_settings.Theme);

        _client = new CollectorClient();
        _telemetry = new TelemetryService();
        _launcher = new CollectorLauncher(_logger);
        _viewModel = new MainViewModel(_telemetry, _client, _launcher, LocalizationService.Instance, _settings);
        _viewModel.TelemetryUpdated += OnTelemetryUpdated;

        _window = new MainWindow(_viewModel);
        _window.SettingsRequested += OnSettingsRequested;
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
        _overlay?.Update(_viewModel.GetOverlayData());
        UpdateTimerState();
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

        if (mainVisible || overlayVisible || chartsVisible)
        {
            if (!_timer.IsEnabled)
            {
                _timer.Start();
            }
        }
        else if (_timer.IsEnabled)
        {
            _timer.Stop();
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

    private void OnSettingsSaved(object? sender, AppSettings settings)
    {
        if (_settingsService is null)
        {
            return;
        }

        int normalMs = _settings.Sampling.NormalMs;
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

        bool restart = settings.Sampling.NormalMs != normalMs;

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

        if (restart)
        {
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

        _viewModel?.Dispose();
        _client?.Dispose();
        _launcher?.Dispose();
        _telemetry?.Dispose();
        _viewModel = null;
        _client = null;
        _launcher = null;
        _telemetry = null;

        _logger?.Info("界面已退出");
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

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
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.Error("未观察的任务异常", e.Exception);
        e.SetObserved();
    }
}
