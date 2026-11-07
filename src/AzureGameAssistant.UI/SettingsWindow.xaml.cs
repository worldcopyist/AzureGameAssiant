using System.Diagnostics;
using System.Windows;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
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
        _viewModel.ApplyTo(_settings);
        SaveRequested?.Invoke(this, _settings);
        Close();
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

    private void OnAiModelPicked(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
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
        base.OnClosed(e);
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.LogsDirectory);
    }

    private void OnOpenDiagnosticsFolder(object sender, RoutedEventArgs e)
    {
        OpenFolder(AppPaths.DiagnosticsDirectory);
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
