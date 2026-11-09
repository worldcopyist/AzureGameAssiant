namespace AzureGameAssistant.Core.Detection;

/// <summary>
/// Pure geometry rules for fullscreen detection. Kept free of window handles so
/// the 95% coverage threshold can be exercised without a desktop.
/// </summary>
public static class GameDisplayRules
{
    public const double FullscreenCoverage = 0.95;

    /// <summary>
    /// True when the window covers at least 95% of the monitor area. A window
    /// that extends past the monitor only counts the visible overlap, so an
    /// oversized borderless window still qualifies.
    /// </summary>
    public static bool CoversMonitor(
        int windowLeft,
        int windowTop,
        int windowRight,
        int windowBottom,
        int monitorLeft,
        int monitorTop,
        int monitorRight,
        int monitorBottom)
    {
        long monitorWidth = (long)monitorRight - monitorLeft;
        long monitorHeight = (long)monitorBottom - monitorTop;
        if (monitorWidth <= 0 || monitorHeight <= 0)
        {
            return false;
        }

        long overlapWidth = Math.Min(windowRight, monitorRight) - Math.Max(windowLeft, monitorLeft);
        long overlapHeight = Math.Min(windowBottom, monitorBottom) - Math.Max(windowTop, monitorTop);
        if (overlapWidth <= 0 || overlapHeight <= 0)
        {
            return false;
        }

        double coverage = (double)overlapWidth * overlapHeight / ((double)monitorWidth * monitorHeight);
        return coverage >= FullscreenCoverage;
    }
}

/// <summary>
/// Debounces fullscreen detection into an active game window. Leaving
/// fullscreen keeps the mode alive for <see cref="DefaultExitDelay"/> so an
/// alt-tab, menu or loading screen does not bounce the sampler back and forth.
/// </summary>
public sealed class GameModeTracker
{
    public static readonly TimeSpan DefaultExitDelay = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _exitDelay;
    private int? _pid;
    private DateTime? _lostAt;

    public GameModeTracker(TimeSpan? exitDelay = null)
    {
        _exitDelay = exitDelay ?? DefaultExitDelay;
    }

    public bool IsActive => _pid is not null;

    public int? Pid => _pid;

    /// <summary>
    /// Feeds one detection result. <paramref name="gamePid"/> is null when the
    /// foreground window is not a game. The returned update tells the caller
    /// whether a game is active and whether the exit delay has elapsed.
    /// </summary>
    public GameModeUpdate Observe(DateTime now, int? gamePid)
    {
        if (gamePid is int detected)
        {
            bool pidChanged = _pid != detected;
            _pid = detected;
            _lostAt = null;
            return new GameModeUpdate(true, detected, pidChanged, false);
        }

        if (_pid is null)
        {
            return default;
        }

        _lostAt ??= now;
        if (now - _lostAt.Value >= _exitDelay)
        {
            int? previous = _pid;
            _pid = null;
            _lostAt = null;
            return new GameModeUpdate(false, null, true, true, previous);
        }

        // Still within the grace period: keep the game sampling rate.
        return new GameModeUpdate(true, _pid, false, false);
    }

    public void Reset()
    {
        _pid = null;
        _lostAt = null;
    }
}

public readonly record struct GameModeUpdate(
    bool Active,
    int? Pid,
    bool Changed,
    bool ExitDue,
    int? PreviousPid = null);
