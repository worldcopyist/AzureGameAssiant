namespace AzureGameAssistant.Core.Performance;

/// <summary>
/// Load-shedding groups. Every user-facing capability belongs to exactly one
/// group. The reduced profile keeps only the three resident capabilities so the
/// assistant can still see load, frames and stutter while another program owns
/// the machine. New features must add a member here and ask
/// <see cref="PerformanceMode.IsEnabled"/> before doing periodic work; anything
/// not marked always-on is paused automatically in the reduced profile.
/// </summary>
public enum PerformanceModule
{
    /// <summary>Collector samples: system load and the Top-5 process ranking.</summary>
    Sampling = 0,

    /// <summary>PresentMon capture: FPS, frame times and frame-based stutter.</summary>
    FrameCapture,

    /// <summary>Automatic and manual stutter marks, including the event list.</summary>
    StutterMarking,

    /// <summary>Overview cards and timeline charts.</summary>
    Charts,

    /// <summary>Desktop overlay window and its refresh.</summary>
    Overlay,

    /// <summary>Session recording, replay data and exports.</summary>
    Sessions,

    /// <summary>Long-run diagnostics recorder.</summary>
    Diagnostics,

    /// <summary>The pinned "this app" row's process sampling.</summary>
    SelfUsage,

    /// <summary>Process ranking table refresh.</summary>
    ProcessView,

    /// <summary>Tray tooltip refresh.</summary>
    TrayTip,

    /// <summary>Stutter balloons and other user notifications.</summary>
    Notifications,

    /// <summary>Automatic AI analysis of finished sessions.</summary>
    AiAnalysis,
}

public readonly record struct LoadSample(uint Pid, double CpuPct, double GpuPct);

/// <summary>
/// Decides whether the assistant should run in the reduced profile. A reduced
/// profile is entered when another process keeps a high CPU or GPU share long
/// enough to matter, and left when that load disappears or the user focuses one
/// of the assistant's windows. Hysteresis keeps both transitions from flapping.
/// </summary>
public sealed class PerformanceMode
{
    public static readonly TimeSpan DefaultEnterDelay = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultExitDelay = TimeSpan.FromSeconds(12);
    public const double DefaultCpuThresholdPct = 20;
    public const double DefaultGpuThresholdPct = 30;

    private readonly double _cpuThresholdPct;
    private readonly double _gpuThresholdPct;
    private readonly TimeSpan _enterDelay;
    private readonly TimeSpan _exitDelay;

    private DateTime? _heavySince;
    private DateTime? _calmSince;

    public PerformanceMode(
        bool enabled,
        double cpuThresholdPct = DefaultCpuThresholdPct,
        double gpuThresholdPct = DefaultGpuThresholdPct,
        TimeSpan? enterDelay = null,
        TimeSpan? exitDelay = null)
    {
        Enabled = enabled;
        _cpuThresholdPct = Math.Max(1, cpuThresholdPct);
        _gpuThresholdPct = Math.Max(1, gpuThresholdPct);
        _enterDelay = enterDelay ?? DefaultEnterDelay;
        _exitDelay = exitDelay ?? DefaultExitDelay;
    }

    public bool Enabled { get; private set; }

    public bool IsReduced { get; private set; }

    /// <summary>PID of the process that triggered the reduced profile.</summary>
    public uint TriggerPid { get; private set; }

    public static bool IsAlwaysOn(PerformanceModule module) => module is
        PerformanceModule.Sampling or
        PerformanceModule.FrameCapture or
        PerformanceModule.StutterMarking;

    public bool IsEnabled(PerformanceModule module) => !IsReduced || IsAlwaysOn(module);

    /// <summary>Returns true when the reduced state changed.</summary>
    public bool SetEnabled(bool enabled)
    {
        if (Enabled == enabled)
        {
            return false;
        }

        Enabled = enabled;
        return enabled ? false : IsReduced && Release();
    }

    /// <summary>
    /// Feeds one sampling tick. An empty <paramref name="samples"/> means the
    /// collector has no data; treat that as calm instead of staying shed.
    /// Returns true when the reduced state changed.
    /// </summary>
    public bool Observe(DateTime nowUtc, ReadOnlySpan<LoadSample> samples, bool appFocused)
    {
        if (!Enabled)
        {
            return IsReduced && Release();
        }

        if (appFocused)
        {
            _heavySince = null;
            _calmSince = null;
            return IsReduced && Release();
        }

        uint triggerPid = FindHeavy(samples);
        if (triggerPid != 0)
        {
            _calmSince = null;
            _heavySince ??= nowUtc;
            if (!IsReduced && nowUtc - _heavySince.Value >= _enterDelay)
            {
                IsReduced = true;
                TriggerPid = triggerPid;
                return true;
            }

            if (IsReduced)
            {
                TriggerPid = triggerPid;
            }

            return false;
        }

        _heavySince = null;
        if (!IsReduced)
        {
            return false;
        }

        _calmSince ??= nowUtc;
        if (nowUtc - _calmSince.Value < _exitDelay)
        {
            return false;
        }

        return Release();
    }

    /// <summary>Forces the full profile. Returns true when it was reduced.</summary>
    public bool Release()
    {
        bool wasReduced = IsReduced;
        IsReduced = false;
        TriggerPid = 0;
        _heavySince = null;
        _calmSince = null;
        return wasReduced;
    }

    private uint FindHeavy(ReadOnlySpan<LoadSample> samples)
    {
        uint triggerPid = 0;
        double worst = 0;
        foreach (LoadSample sample in samples)
        {
            if (sample.Pid == 0)
            {
                continue;
            }

            double worstOfSample = Math.Max(
                sample.CpuPct / _cpuThresholdPct,
                sample.GpuPct / _gpuThresholdPct);
            if (worstOfSample >= 1 && worstOfSample > worst)
            {
                worst = worstOfSample;
                triggerPid = sample.Pid;
            }
        }

        return triggerPid;
    }
}
