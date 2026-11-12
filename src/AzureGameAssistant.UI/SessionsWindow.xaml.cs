using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core;
using AzureGameAssistant.Core.Sessions;
using AzureGameAssistant.Core.Settings;
using AzureGameAssistant.UI.Services;
using AzureGameAssistant.UI.ViewModels;
using Microsoft.Win32;

namespace AzureGameAssistant.UI;

public partial class SessionsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly SettingsService _settingsService;
    private readonly AiAnalysisService _ai;
    private readonly ObservableCollection<SessionRow> _rows = new();
    private SessionRow? _selected;
    private SessionDocument? _document;

    public SessionsWindow(
        MainViewModel viewModel,
        AppSettings settings,
        SettingsService settingsService,
        AiAnalysisService ai)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        _settingsService = settingsService;
        _ai = ai;
        SessionList.ItemsSource = _rows;
        _viewModel.SessionsChanged += OnSessionsChanged;
        Loaded += (_, _) => RefreshList();
        Closed += (_, _) => _viewModel.SessionsChanged -= OnSessionsChanged;
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            RefreshList();
        }
    }

    private void RefreshList()
    {
        string? selectedPath = _selected?.Info.Path;
        _rows.Clear();
        foreach (SessionInfo info in SessionStore.Enumerate(_viewModel.Sessions.DirectoryPath))
        {
            _rows.Add(new SessionRow(info));
        }

        if (_rows.Count == 0)
        {
            _selected = null;
            ClearDetail(LocalizationService.Instance["SessionEmpty"]);
            return;
        }

        SessionRow? target = selectedPath is null
            ? _rows[0]
            : _rows.FirstOrDefault(row => string.Equals(row.Info.Path, selectedPath, StringComparison.OrdinalIgnoreCase)) ?? _rows[0];
        SessionList.SelectedItem = target;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshList();

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionRow row)
        {
            return;
        }

        _selected = row;
        LoadSession(row);
    }

    private void LoadSession(SessionRow row)
    {
        LocalizationService loc = LocalizationService.Instance;
        SessionInfo info = row.Info;

        try
        {
            SessionDocument document = SessionStore.Read(info.Path);
            _document = document;
            ReplayChart.SetSamples(document.Samples, document.Samples.Length);
            ReplayChart.SetFrames(document.Frames, document.Frames.Length);
            ReplayChart.SetEventWindow(document.Event.StartUtc, document.Event.EndUtc);
            ReplayChart.ResetPan();

            DetailHeader.Text = string.Format(
                CultureInfo.CurrentUICulture,
                "{0} · {1}",
                info.StartUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentUICulture),
                CauseText(info.Cause, loc));
            DetailText.Text = BuildDetail(document, info.Path, loc);
            StatusText.Text = loc.Format("SessionListStatus", _rows.Count, info.FileName);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or ArgumentException)
        {
            _document = null;
            ReplayChart.SetSamples([], 0);
            ReplayChart.SetFrames([], 0);
            ReplayChart.SetEventWindow(0, 0);
            DetailHeader.Text = loc["SessionLoadFailed"];
            DetailText.Text = ex.Message;
            StatusText.Text = loc["SessionLoadFailed"];
        }
    }

    private static string BuildDetail(SessionDocument document, string sessionPath, LocalizationService loc)
    {
        SessionHeaderLine header = document.Header;
        SessionEventLine eventLine = document.Event;
        var builder = new StringBuilder(512);

        builder.AppendLine(loc.Format(
            "SessionDetailTime",
            eventLine.StartUtc == 0
                ? "—"
                : new DateTime(eventLine.StartUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentUICulture),
            new DateTime(eventLine.EndUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentUICulture)));
        builder.AppendLine(loc.Format(
            "SessionDetailEvent",
            eventLine.Score,
            CauseText(eventLine.Cause, loc),
            SignalText(eventLine.Signals, loc)));
        builder.AppendLine(loc.Format("SessionDetailMode", header.Mode, header.SamplingMs));
        builder.AppendLine(loc.Format(
            "SessionDetailData",
            document.Samples.Length,
            document.Frames.Length,
            document.Footer?.Dropped ?? 0,
            document.Footer?.FrameDropped ?? 0));
        builder.AppendLine(loc.Format(
            "SessionDetailPermissions",
            header.Elevated ? loc["SessionPrivilegeAdmin"] : loc["SessionPrivilegeNormal"],
            string.IsNullOrEmpty(header.CpuTempSource) ? loc["NotAvailable"] : header.CpuTempSource,
            string.IsNullOrEmpty(header.Sensors) ? loc["NotAvailable"] : header.Sensors,
            string.IsNullOrEmpty(header.PresentMon) ? loc["NotAvailable"] : header.PresentMon));

        if (document.Footer is not { Complete: true })
        {
            builder.AppendLine(loc["SessionRecordingNote"]);
        }

        if (document.Footer is { Dropped: > 0 } || document.Footer is { FrameDropped: > 0 })
        {
            builder.AppendLine(loc.Format(
                "SessionGapNote",
                document.Footer?.Dropped ?? 0,
                document.Footer?.FrameDropped ?? 0));
        }

        builder.AppendLine();
        SessionReportLine? report = document.Report;
        if (report is null)
        {
            builder.AppendLine(loc["SessionNoReport"]);
            AppendAi(builder, sessionPath, loc);
            return builder.ToString();
        }

        builder.AppendLine(loc.Format("SessionReportTitle", report.Confidence));
        builder.AppendLine(report.Summary);
        builder.AppendLine();

        if (report.Suspects.Length > 0)
        {
            builder.AppendLine(loc["SessionSuspectsTitle"]);
            foreach (SessionReportSuspect suspect in report.Suspects)
            {
                builder.AppendLine(loc.Format(
                    "SessionSuspectLine",
                    string.IsNullOrEmpty(suspect.Name) ? "?" : suspect.Name,
                    suspect.Pid,
                    suspect.CpuPeak,
                    suspect.GpuPeak,
                    suspect.WsDeltaMb));
            }

            builder.AppendLine();
        }

        if (report.FrameEvidence is { } frames)
        {
            builder.AppendLine(loc["SessionFrameEvidenceTitle"]);
            builder.AppendLine(loc.Format(
                "SessionFrameEvidenceLine",
                frames.MaxFrameTimeMs,
                frames.SpikeCount,
                frames.GapCount,
                frames.AvgCpuWaitMs,
                frames.AvgGpuWaitMs,
                frames.AvgDisplayLatencyMs));
            builder.AppendLine(loc[frames.Consistent ? "SessionFrameConsistent" : "SessionFrameInconsistent"]);
            builder.AppendLine();
        }

        if (report.Suggestions.Length > 0)
        {
            builder.AppendLine(loc["SessionSuggestionsTitle"]);
            foreach (string suggestion in report.Suggestions)
            {
                builder.AppendLine("• " + suggestion);
            }

            builder.AppendLine();
        }

        if (report.Evidence.Length > 0)
        {
            builder.AppendLine(loc["SessionEvidenceTitle"]);
            foreach (string evidence in report.Evidence)
            {
                builder.AppendLine("• " + evidence);
            }
        }

        AppendAi(builder, sessionPath, loc);
        return builder.ToString();
    }

    private static void AppendAi(StringBuilder builder, string sessionPath, LocalizationService loc)
    {
        AiReport? report = AiReportStore.Read(sessionPath);
        if (report is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine(loc["SessionAiTitle"]);
        builder.AppendLine(report.Summary);
        builder.AppendLine(loc.Format("SessionAiCause", AiCauseText(report.PrimaryCause, loc), AiConfidenceText(report.Confidence, loc)));

        foreach (AiEvidence evidence in report.Evidence.Take(4))
        {
            builder.AppendLine("• " + evidence.Metric + ": " + evidence.Value + " — " + evidence.Interpretation);
        }

        foreach (AiRecommendation recommendation in report.Recommendations.Take(3))
        {
            builder.AppendLine("• " + recommendation.Action);
        }

        if (!string.IsNullOrWhiteSpace(report.Disclaimer))
        {
            builder.AppendLine();
            builder.AppendLine(report.Disclaimer);
        }
    }

    private static string AiCauseText(string cause, LocalizationService loc)
    {
        string key = "AiCause" + cause;
        string text = loc[key];
        return string.Equals(text, key, StringComparison.Ordinal) ? cause : text;
    }

    private static string AiConfidenceText(string confidence, LocalizationService loc) => confidence switch
    {
        "high" => loc["AiConfidenceHigh"],
        "medium" => loc["AiConfidenceMedium"],
        _ => loc["AiConfidenceLow"],
    };

    private static string CauseText(string cause, LocalizationService loc) => cause switch
    {
        "Cpu" => loc["CauseCpu"],
        "Gpu" => loc["CauseGpu"],
        "Memory" => loc["CauseMemory"],
        "Disk" => loc["CauseDisk"],
        "Vram" => loc["CauseVram"],
        "FramePacing" => loc["CauseFramePacing"],
        "Thermal" => loc["CauseThermal"],
        "Manual" => loc["CauseManual"],
        _ => loc["CauseUnknown"],
    };

    private static string SignalText(string[] signals, LocalizationService loc)
    {
        if (signals.Length == 0)
        {
            return loc["NotAvailable"];
        }

        var parts = new List<string>(signals.Length);
        foreach (string signal in signals)
        {
            parts.Add(signal switch
            {
                "FrameSpike" => loc["SignalFrameSpike"],
                "DisplayGap" => loc["SignalDisplayGap"],
                "Jitter" => loc["SignalJitter"],
                "CpuSpike" => loc["SignalCpuSpike"],
                "GpuSpike" => loc["SignalGpuSpike"],
                "MemoryPressure" => loc["SignalMemoryPressure"],
                "DiskPressure" => loc["SignalDiskPressure"],
                "Manual" => loc["SignalManual"],
                _ => signal,
            });
        }

        return string.Join(" · ", parts);
    }

    private void ClearDetail(string status)
    {
        _document = null;
        ReplayChart.SetSamples([], 0);
        ReplayChart.SetFrames([], 0);
        ReplayChart.SetEventWindow(0, 0);
        DetailHeader.Text = LocalizationService.Instance["SessionNoSelection"];
        DetailText.Text = status;
        StatusText.Text = status;
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        LocalizationService loc = LocalizationService.Instance;
        if (_selected is null)
        {
            return;
        }

        SessionDocument document;
        try
        {
            document = SessionStore.Read(_selected.Info.Path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or System.Text.Json.JsonException or ArgumentException)
        {
            StatusText.Text = loc.Format("SessionExportFailed", ex.Message);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = loc["SessionExportDialogTitle"],
            Filter = loc["SessionExportFilter"],
            DefaultExt = ".md",
            AddExtension = true,
            FileName = SessionMarkdownReport.BuildFileBaseName(document.Event),
            InitialDirectory = _viewModel.Sessions.DirectoryPath,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            string path = SessionExportService.Export(document, _selected.Info.Path, dialog.FileName);
            StatusText.Text = loc.Format("SessionExportSaved", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException or ArgumentException
                                       or System.Runtime.InteropServices.COMException)
        {
            StatusText.Text = loc.Format("SessionExportFailed", ex.Message);
        }
    }

    private void OnAiClick(object sender, RoutedEventArgs e)
    {
        LocalizationService loc = LocalizationService.Instance;
        if (_selected is null || _document is null)
        {
            return;
        }

        string sessionPath = _selected.Info.Path;
        var window = new AiAnalysisWindow(
            _settings,
            _settingsService,
            _ai,
            SessionFactsFactory.FromSession(_document, loc.CurrentLanguage),
            sessionPath)
        {
            Owner = this,
        };

        window.ShowDialog();

        if (AiReportStore.Read(sessionPath) is null)
        {
            return;
        }

        DetailText.Text = BuildDetail(_document, sessionPath, loc);
        StatusText.Text = loc.Format("SessionAiSaved", AiReportStore.PathFor(sessionPath));
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_viewModel.Sessions.DirectoryPath);
            Process.Start(new ProcessStartInfo(_viewModel.Sessions.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        LocalizationService loc = LocalizationService.Instance;
        if (_selected is null)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            loc["SessionDeleteConfirm"],
            loc["SessionsTitle"],
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        if (!SessionStore.Delete(_selected.Info.Path))
        {
            StatusText.Text = loc["SessionDeleteFailed"];
            return;
        }

        RefreshList();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    public sealed class SessionRow
    {
        public SessionRow(SessionInfo info)
        {
            Info = info;
            LocalizationService loc = LocalizationService.Instance;
            TitleText = string.Format(
                CultureInfo.CurrentUICulture,
                "{0} · {1}",
                info.StartUtc.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.CurrentUICulture),
                info.Score);
            MetaText = string.Format(
                CultureInfo.CurrentUICulture,
                "{0} · {1} · {2}",
                CauseText(info.Cause, loc),
                info.Complete ? loc["SessionStateComplete"] : loc["SessionStateRecording"],
                FormatSize(info.SizeBytes));
        }

        public SessionInfo Info { get; }

        public string TitleText { get; }

        public string MetaText { get; }

        private static string FormatSize(long bytes) => bytes switch
        {
            >= 1024 * 1024 => (bytes / 1024.0 / 1024.0).ToString("F1", CultureInfo.CurrentUICulture) + " MB",
            >= 1024 => (bytes / 1024.0).ToString("F0", CultureInfo.CurrentUICulture) + " KB",
            _ => bytes.ToString(CultureInfo.CurrentUICulture) + " B",
        };
    }
}
