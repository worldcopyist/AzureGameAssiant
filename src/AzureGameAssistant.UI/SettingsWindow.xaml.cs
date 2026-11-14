using System.Diagnostics;
using System.Windows;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Diagnostics;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using AzureGameAssistant.UI.ViewModels;
using System.IO;

namespace AzureGameAssistant.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _modelFetch;
    private CancellationTokenSource? _connectionTest;

    public SettingsWindow(SettingsViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;
    }

    public event EventHandler<AppSettings>? SaveRequested;

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (!ValidateLogPaths())
        {
            return;
        }

        _viewModel.ApplyTo(_settings);
        SaveRequested?.Invoke(this, _settings);
        Close();
    }

    private bool ValidateLogPaths()
    {
        LocalizationService loc = LocalizationService.Instance;
        if (!LogPaths.TryNormalize(_viewModel.LogsDirectory, out string logsDirectory) ||
            !LogPaths.TryNormalize(_viewModel.IssueLogsDirectory, out string issueLogsDirectory))
        {
            MessageBox.Show(this, loc["LogsPathInvalid"], Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        logsDirectory = LogPaths.ResolveDirectory(logsDirectory);
        issueLogsDirectory = LogPaths.ResolveIssueDirectory(issueLogsDirectory, logsDirectory);
        foreach (string path in new[] { logsDirectory, issueLogsDirectory })
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException or System.Security.SecurityException)
            {
                MessageBox.Show(this, loc["LogsPathUnavailable"] + "\n" + path + "\n" + ex.Message, Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        return true;
    }

    private void OnBrowseLogs(object sender, RoutedEventArgs e) =>
        BrowseLogDirectory(path => _viewModel.LogsDirectory = path, _viewModel.LogsPath);

    private void OnBrowseIssueLogs(object sender, RoutedEventArgs e) =>
        BrowseLogDirectory(path => _viewModel.IssueLogsDirectory = path, _viewModel.IssueLogsPath);

    private void OnResetLogs(object sender, RoutedEventArgs e) => _viewModel.LogsDirectory = string.Empty;

    private void OnResetIssueLogs(object sender, RoutedEventArgs e) => _viewModel.IssueLogsDirectory = string.Empty;

    private void BrowseLogDirectory(Action<string> apply, string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.Instance["LogsDirectoryLabel"],
            InitialDirectory = Directory.Exists(current) ? current : AppPaths.LogsDirectory,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            apply(dialog.FolderName);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSaveAiKey(object sender, RoutedEventArgs e)
    {
        string key = AiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            return;
        }

        if (!CredentialStore.Shared.Save(_viewModel.AiActiveProvider, key))
        {
            MessageBox.Show(
                LocalizationService.Instance["AiKeySaveFailed"],
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        AiKeyBox.Clear();
        _viewModel.RefreshKeyStatus();
    }

    private void OnDeleteAiKey(object sender, RoutedEventArgs e)
    {
        CredentialStore.Shared.Delete(_viewModel.AiActiveProvider);
        AiKeyBox.Clear();
        _viewModel.RefreshKeyStatus();
    }

    private async void OnFetchAiModels(object sender, RoutedEventArgs e)
    {
        LocalizationService loc = LocalizationService.Instance;
        string provider = _viewModel.AiActiveProvider;

        string key = AiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            CredentialStore.Shared.TryRead(provider, out key);
        }

        if (_viewModel.AiRequiresKey && string.IsNullOrWhiteSpace(key))
        {
            _viewModel.AiModelsStatus = loc["AiModelsNeedKey"];
            return;
        }

        if (!AiEndpoint.TryNormalizeModels(_viewModel.AiBaseUrl, out Uri endpoint, out string? error))
        {
            _viewModel.AiModelsStatus = error ?? loc["AiModelsFailed"];
            return;
        }

        _modelFetch?.Cancel();
        _modelFetch?.Dispose();
        _modelFetch = new CancellationTokenSource();
        CancellationToken token = _modelFetch.Token;

        _viewModel.AiModelsBusy = true;
        _viewModel.AiModelsStatus = loc["AiModelsFetching"];

        try
        {
            using var client = new OpenAiCompatibleClient();
            AiModelList result = await client.ListModelsAsync(endpoint, key, token);

            if (!string.Equals(provider, _viewModel.AiActiveProvider, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (result.Success)
            {
                _viewModel.AiModelOptions = result.Models;
                if (string.IsNullOrWhiteSpace(_viewModel.AiModel))
                {
                    _viewModel.AiModel = result.Models[0];
                }

                _viewModel.AiModelsStatus = loc.Format("AiModelsLoaded", result.Models.Count);
            }
            else if (result.Failure != AiFailureKind.Cancelled)
            {
                _viewModel.AiModelsStatus = result.Error ?? loc["AiModelsFailed"];
            }
        }
        catch (Exception) when (_modelFetch?.IsCancellationRequested == true)
        {
        }
        catch (Exception)
        {
            _viewModel.AiModelsStatus = loc["AiModelsFailed"];
        }
        finally
        {
            _viewModel.AiModelsBusy = false;
        }
    }

    private async void OnTestAiConnection(object sender, RoutedEventArgs e)
    {
        LocalizationService loc = LocalizationService.Instance;
        string provider = _viewModel.AiActiveProvider;
        string model = (_viewModel.AiModel ?? string.Empty).Trim();
        if (model.Length == 0)
        {
            _viewModel.AiTestStatus = loc["AiTestNoModel"];
            return;
        }

        string key = AiKeyBox.Password.Trim();
        if (key.Length == 0)
        {
            CredentialStore.Shared.TryRead(provider, out key);
        }

        if (_viewModel.AiRequiresKey && string.IsNullOrWhiteSpace(key))
        {
            _viewModel.AiTestStatus = loc["AiTestNeedKey"];
            return;
        }

        if (!AiEndpoint.TryNormalize(_viewModel.AiBaseUrl, out Uri endpoint, out string? error))
        {
            _viewModel.AiTestStatus = error ?? loc["AiTestNoModel"];
            return;
        }

        _connectionTest?.Cancel();
        _connectionTest?.Dispose();
        _connectionTest = new CancellationTokenSource();
        CancellationToken token = _connectionTest.Token;

        _viewModel.AiTesting = true;
        _viewModel.AiTestStatus = loc["AiTestBusy"];

        try
        {
            using var client = new OpenAiCompatibleClient();
            AiConnectionTest result = await client.TestConnectionAsync(endpoint, model, key, token);

            if (!string.Equals(provider, _viewModel.AiActiveProvider, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _viewModel.AiTestStatus = result.Success
                ? loc.Format("AiTestOk", result.LatencyMs, model)
                : result.Error ?? loc["AiModelsFailed"];
        }
        catch (Exception) when (_connectionTest?.IsCancellationRequested == true)
        {
        }
        catch (Exception)
        {
            _viewModel.AiTestStatus = loc["AiModelsFailed"];
        }
        finally
        {
            _viewModel.AiTesting = false;
        }
    }

    private void OnAiModelPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)    {
        if (sender is System.Windows.Controls.ComboBox { SelectedItem: string model } && model.Length > 0)
        {
            _viewModel.AiModel = model;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _modelFetch?.Cancel();
        _modelFetch?.Dispose();
        _modelFetch = null;
        _connectionTest?.Cancel();
        _connectionTest?.Dispose();
        _connectionTest = null;
        base.OnClosed(e);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        OpenFolder(_viewModel.LogsPath);
    }

    private void OnOpenIssueLogs(object sender, RoutedEventArgs e)
    {
        OpenFolder(_viewModel.IssueLogsPath);
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.DiagnosticsDirectory);
    }

    private void OnExportDiagnosticBundle(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = LocalizationService.Instance["ExportDiagnosticBundle"],
            Filter = "ZIP (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = "ags-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip",
            InitialDirectory = AppPaths.DiagnosticBundlesDirectory,
        };
        Directory.CreateDirectory(AppPaths.DiagnosticBundlesDirectory);
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string logsDirectory = LogPaths.ResolveDirectory(_settings.Logs.Directory);
            DiagnosticBundleResult result = DiagnosticBundle.Create(new DiagnosticBundleOptions(
                AppPaths.DiagnosticsDirectory,
                LogPaths.ResolveIssueDirectory(_settings.Logs.IssueDirectory, logsDirectory),
                AppPaths.SettingsPath,
                dialog.FileName));
            MessageBox.Show(
                this,
                LocalizationService.Instance.Format(
                    "DiagnosticBundleDone",
                    result.Entries.Count,
                    result.RedactedFiles,
                    result.Path),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                LocalizationService.Instance.Format("DiagnosticBundleFailed", ex.Message),
                Title,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static void OpenFolder(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = directory,
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
