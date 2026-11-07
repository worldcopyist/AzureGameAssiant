using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using AzureGameAssistant.UI.ViewModels;

namespace AzureGameAssistant.UI;

public partial class AiAnalysisWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly AiAnalysisService _ai;
    private readonly AiPreparedRequest _prepared;
    private readonly AiAnalysisViewModel _viewModel;
    private CancellationTokenSource? _cancellation;
    private DispatcherTimer? _copyTimer;

    public AiAnalysisWindow(
        AppSettings settings,
        SettingsService settingsService,
        AiAnalysisService ai,
        FactsInput input)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _ai = ai;

        FactsDocument facts = FactsBuilder.Build(input, settings.Ai.HideProcessNames);
        _prepared = ai.BuildRequest(settings, facts, LocalizationService.Instance.CurrentLanguage);
        _viewModel = new AiAnalysisViewModel(
            settings,
            LocalizationService.Instance,
            _prepared,
            input.Samples.Count > 0);

        DataContext = _viewModel;
        Closing += OnClosing;
    }

    private async void OnRunClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning || !_viewModel.CanRun)
        {
            return;
        }

        if (_viewModel.ConsentRequired)
        {
            _settings.Ai.ConsentSignature = _viewModel.Signature;
            try
            {
                _settingsService.Save(_settings);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _viewModel.BeginRun();

        var progress = new Progress<int>(_viewModel.ReportProgress);

        try
        {
            AiAnalysisResult result = await _ai.AnalyzeAsync(_prepared, progress, _cancellation.Token);
            _viewModel.ApplyResult(result);
        }
        catch (OperationCanceledException)
        {
            _viewModel.ApplyCancelled();
        }
        catch (Exception ex)
        {
            _viewModel.ApplyError(ex.Message);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _cancellation?.Cancel();

    private void OnCopyPreview(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.PreviewJson);
        }
        catch (COMException)
        {
            return;
        }

        CopyButton.Content = LocalizationService.Instance["AiCopied"];
        _copyTimer?.Stop();
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _copyTimer.Tick += (_, _) =>
        {
            _copyTimer?.Stop();
            CopyButton.Content = LocalizationService.Instance["AiCopy"];
        };
        _copyTimer.Start();
    }

    private void OnOpenReports(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AiReportsDirectory);
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            };
            startInfo.ArgumentList.Add(AppPaths.AiReportsDirectory);
            Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _cancellation?.Cancel();
        _copyTimer?.Stop();
        CopyButton.Content = LocalizationService.Instance["AiCopy"];
    }
}
