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
    private readonly string? _sessionPath;
    private CancellationTokenSource? _cancellation;
    private DispatcherTimer? _copyTimer;

    public AiAnalysisWindow(
        AppSettings settings,
        SettingsService settingsService,
        AiAnalysisService ai,
        FactsInput input,
        string? sessionPath = null)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _ai = ai;
        _sessionPath = sessionPath;

        FactsDocument facts = FactsBuilder.Build(input, settings.Ai.HideProcessNames);
        _prepared = ai.BuildRequest(settings, facts, LocalizationService.Instance.CurrentLanguage);
        _viewModel = new AiAnalysisViewModel(
            settings,
            LocalizationService.Instance,
            _prepared,
            input.Samples.Count > 0);

        DataContext = _viewModel;
        Closing += OnClosing;

        if (sessionPath is not null)
        {
            AiReport? stored = AiReportStore.Read(sessionPath);
            if (stored is not null &&
                AiReportStore.IsReusable(stored, _prepared.Provider.Model, _prepared.FactsHash))
            {
                _viewModel.ApplyCachedReport(stored, AiReportStore.PathFor(sessionPath));
            }
        }
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

        if (_ai.IsBusy)
        {
            _viewModel.ApplyError(LocalizationService.Instance["AiBusy"]);
            return;
        }

        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _viewModel.BeginRun();

        var progress = new Progress<int>(_viewModel.ReportProgress);

        try
        {
            AiAnalysisResult result = await _ai.AnalyzeAsync(_prepared, progress, _cancellation.Token);
            _viewModel.ApplyResult(result);
            AttachToSession(result);
            if (_sessionPath is not null)
            {
                _viewModel.MarkSessionBound();
            }
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

    /// <summary>
    /// A session-scoped run stores its report next to that session so replay
    /// and export pick it up. The offline fallback is local attribution data,
    /// not a model answer, so it is never written as an AI report.
    /// </summary>
    private void AttachToSession(AiAnalysisResult result)
    {
        if (_sessionPath is null || result.UsedLocalFallback || result.Report is not { } report)
        {
            return;
        }

        report.Model = _prepared.Provider.Model;
        report.FactsHash = _prepared.FactsHash;

        if (AiReportStore.Write(_sessionPath, report))
        {
            _viewModel.ApplySessionReport(AiReportStore.PathFor(_sessionPath));
        }
    }

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
