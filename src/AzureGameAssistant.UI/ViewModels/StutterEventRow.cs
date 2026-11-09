using System.Globalization;
using AzureGameAssistant.Core.Detection;
using AzureGameAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AzureGameAssistant.UI.ViewModels;

public sealed partial class StutterEventRow : ObservableObject
{
    private readonly StutterEvent _data;
    private readonly string _processName;

    public StutterEventRow(StutterEvent data, string processName, LocalizationService loc)
    {
        _data = data;
        _processName = processName;
        TimeText = data.StartUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentUICulture);
        DurationText = (Math.Max(0, data.DurationMs) / 1000.0).ToString("F1", CultureInfo.CurrentUICulture) + " s";
        PeakText = data.PeakFrameTimeMs > 0
            ? data.PeakFrameTimeMs.ToString("F0", CultureInfo.CurrentUICulture) + " ms"
            : string.Empty;
        RefreshTexts(loc);
    }

    public StutterEvent Data => _data;

    public string ProcessName => _processName;

    public string TimeText { get; }

    public string DurationText { get; }

    public string PeakText { get; }

    [ObservableProperty]
    private string _scoreText = string.Empty;

    [ObservableProperty]
    private string _causeText = string.Empty;

    [ObservableProperty]
    private string _signalsText = string.Empty;

    public void RefreshTexts(LocalizationService loc)
    {
        ScoreText = loc.Format("StutterScore", _data.Score);
        CauseText = loc[CauseKey(_data.Cause)];
        SignalsText = JoinSignals(_data.Signals, loc);
    }

    private static string JoinSignals(StutterSignals signals, LocalizationService loc)
    {
        var parts = new List<string>(4);
        if (signals.HasFlag(StutterSignals.FrameSpike))
        {
            parts.Add(loc["SignalFrameSpike"]);
        }

        if (signals.HasFlag(StutterSignals.DisplayGap))
        {
            parts.Add(loc["SignalDisplayGap"]);
        }

        if (signals.HasFlag(StutterSignals.Jitter))
        {
            parts.Add(loc["SignalJitter"]);
        }

        if (signals.HasFlag(StutterSignals.CpuSpike))
        {
            parts.Add(loc["SignalCpuSpike"]);
        }

        if (signals.HasFlag(StutterSignals.GpuSpike))
        {
            parts.Add(loc["SignalGpuSpike"]);
        }

        if (signals.HasFlag(StutterSignals.MemoryPressure))
        {
            parts.Add(loc["SignalMemoryPressure"]);
        }

        if (signals.HasFlag(StutterSignals.DiskPressure))
        {
            parts.Add(loc["SignalDiskPressure"]);
        }

        if (signals.HasFlag(StutterSignals.Manual))
        {
            parts.Add(loc["SignalManual"]);
        }

        return parts.Count == 0 ? loc["NotAvailable"] : string.Join(" · ", parts);
    }

    private static string CauseKey(StutterCause cause) => cause switch
    {
        StutterCause.Cpu => "CauseCpu",
        StutterCause.Gpu => "CauseGpu",
        StutterCause.Memory => "CauseMemory",
        StutterCause.Disk => "CauseDisk",
        StutterCause.FramePacing => "CauseFramePacing",
        StutterCause.Manual => "CauseManual",
        _ => "CauseUnknown",
    };
}
