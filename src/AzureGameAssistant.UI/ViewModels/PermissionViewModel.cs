using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using AzureGameAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AzureGameAssistant.UI.ViewModels;

public sealed partial class PermissionViewModel : ObservableObject
{
    private readonly Func<PermissionSnapshot> _snapshotProvider;
    private readonly LocalizationService _loc;

    public PermissionViewModel(Func<PermissionSnapshot> snapshotProvider, LocalizationService loc)
    {
        _snapshotProvider = snapshotProvider;
        _loc = loc;
        Refresh();
    }

    public ObservableCollection<PermissionCheck> Checks { get; } = new();

    public event EventHandler? ElevationRequested;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _checkedAt = string.Empty;

    [ObservableProperty]
    private string _hint = string.Empty;

    [ObservableProperty]
    private bool _showElevateButton;

    public void Refresh()
    {
        PermissionSnapshot snapshot = _snapshotProvider();
        IReadOnlyList<PermissionCheck> checks = PermissionDiagnostics.Evaluate(snapshot, _loc);

        Checks.Clear();
        foreach (PermissionCheck check in checks)
        {
            Checks.Add(check);
        }

        int missing = checks.Count(check => check.State == PermissionState.Missing);
        int warnings = checks.Count(check => check.State == PermissionState.Warning);
        Summary = missing == 0 && warnings == 0
            ? _loc["PermissionSummaryOk"]
            : _loc.Format("PermissionSummary", missing, warnings);
        CheckedAt = _loc.Format("PermissionCheckedAt", DateTime.Now.ToString("HH:mm:ss"));
        Hint = _loc["PermissionHint"];
        ShowElevateButton = !snapshot.IsElevated;
    }

    [RelayCommand]
    private void RefreshChecks() => Refresh();

    [RelayCommand]
    private void Elevate() => ElevationRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void CopyReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{_loc["PermissionWindowTitle"]} · {CheckedAt}");
        builder.AppendLine(Summary);
        foreach (PermissionCheck check in Checks)
        {
            builder.AppendLine($"[{check.StatusText}] {check.Name}: {check.Detail}");
        }

        try
        {
            Clipboard.SetText(builder.ToString());
        }
        catch (ExternalException)
        {
        }
    }
}
