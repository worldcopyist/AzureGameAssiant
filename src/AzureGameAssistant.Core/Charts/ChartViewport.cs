namespace AzureGameAssistant.Core.Charts;

/// <summary>
/// Time-axis state for the timeline charts: visible window, horizontal pan and
/// the click-to-freeze flag. Kept free of WPF types so the interaction rules can
/// be unit tested.
/// </summary>
public sealed class ChartViewport
{
    public const double MinWindowSeconds = 10;
    public const double MaxWindowSeconds = 1800;
    public const double PanEpsilonSeconds = 0.5;

    public double WindowSeconds { get; private set; } = 60;

    public double PanSeconds { get; private set; }

    public bool IsPaused { get; private set; }

    public double PausedEndTicks { get; private set; }

    public bool IsPanned => PanSeconds > PanEpsilonSeconds;

    public static double MaxWindowFor(double availableSeconds) =>
        Math.Clamp(availableSeconds > 0 ? availableSeconds : MaxWindowSeconds, MinWindowSeconds, MaxWindowSeconds);

    public bool SetWindowSeconds(double seconds, double availableSeconds)
    {
        double clamped = Math.Clamp(seconds, MinWindowSeconds, MaxWindowFor(availableSeconds));
        if (Math.Abs(clamped - WindowSeconds) < 0.01)
        {
            return false;
        }

        WindowSeconds = clamped;
        ClampPan(availableSeconds);
        return true;
    }

    public bool Zoom(double factor, double availableSeconds) =>
        SetWindowSeconds(WindowSeconds * factor, availableSeconds);

    public bool SetPan(double panSeconds, double availableSeconds)
    {
        double maxPan = Math.Max(0, availableSeconds - WindowSeconds);
        double clamped = Math.Clamp(panSeconds, 0, maxPan);
        if (Math.Abs(clamped - PanSeconds) < 0.001)
        {
            return false;
        }

        PanSeconds = clamped;
        return true;
    }

    public bool PanBy(double deltaSeconds, double availableSeconds) =>
        SetPan(PanSeconds + deltaSeconds, availableSeconds);

    public bool ResetPan()
    {
        if (PanSeconds == 0)
        {
            return false;
        }

        PanSeconds = 0;
        return true;
    }

    /// <summary>
    /// Freezes the window at the latest sample. Returns false when there is no
    /// data yet or the viewport is already frozen.
    /// </summary>
    public bool Pause(double latestEndTicks)
    {
        if (IsPaused || latestEndTicks <= 0)
        {
            return false;
        }

        IsPaused = true;
        PausedEndTicks = latestEndTicks;
        return true;
    }

    public bool Resume()
    {
        if (!IsPaused)
        {
            return false;
        }

        IsPaused = false;
        PausedEndTicks = 0;
        return true;
    }

    /// <summary>
    /// While paused the window end stays at the frozen tick. If the data source
    /// resets to an older range, fall back to the latest tick so the viewport is
    /// never stuck in the future.
    /// </summary>
    public double ResolveEndTicks(double latestEndTicks) =>
        IsPaused && PausedEndTicks > 0 && latestEndTicks >= PausedEndTicks
            ? PausedEndTicks
            : latestEndTicks;

    private void ClampPan(double availableSeconds)
    {
        double maxPan = Math.Max(0, availableSeconds - WindowSeconds);
        PanSeconds = Math.Clamp(PanSeconds, 0, maxPan);
    }
}
