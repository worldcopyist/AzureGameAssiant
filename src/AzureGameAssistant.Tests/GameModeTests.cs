using AzureGameAssistant.Core.Detection;
using Xunit;

namespace AzureGameAssistant.Tests;

public sealed class GameModeTests
{
    [Fact]
    public void ExactMonitorBoundsCoverTheWholeScreen()
    {
        Assert.True(GameDisplayRules.CoversMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080));
    }

    [Fact]
    public void OversizedBorderlessWindowCountsAsFullscreen()
    {
        Assert.True(GameDisplayRules.CoversMonitor(-12, -12, 1932, 1092, 0, 0, 1920, 1080));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1040)] // 96.3%
    [InlineData(0, 0, 1844, 1080)] // 96.0%
    public void NinetyFivePercentIsTheThreshold(int left, int top, int right, int bottom)
    {
        Assert.True(GameDisplayRules.CoversMonitor(left, top, right, bottom, 0, 0, 1920, 1080));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1000)] // 92.6%
    [InlineData(0, 0, 1800, 1080)] // 93.75%
    [InlineData(0, 0, 1536, 864)] // 64%, a windowed game
    public void SmallerWindowsStayWindowed(int left, int top, int right, int bottom)
    {
        Assert.False(GameDisplayRules.CoversMonitor(left, top, right, bottom, 0, 0, 1920, 1080));
    }

    [Fact]
    public void WindowOutsideTheMonitorDoesNotCount()
    {
        Assert.False(GameDisplayRules.CoversMonitor(2000, 0, 3920, 1080, 0, 0, 1920, 1080));
    }

    [Fact]
    public void OneSmallMonitorIsNotAValidTarget()
    {
        Assert.False(GameDisplayRules.CoversMonitor(0, 0, 100, 100, 0, 0, 0, 0));
    }

    [Fact]
    public void EnteringAndSwitchingGamesReportsChanges()
    {
        var tracker = new GameModeTracker(TimeSpan.FromSeconds(10));
        DateTime now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        GameModeUpdate first = tracker.Observe(now, 42);
        Assert.True(first.Active);
        Assert.True(first.Changed);
        Assert.Equal(42, first.Pid);

        GameModeUpdate same = tracker.Observe(now.AddSeconds(1), 42);
        Assert.True(same.Active);
        Assert.False(same.Changed);

        GameModeUpdate switched = tracker.Observe(now.AddSeconds(2), 77);
        Assert.True(switched.Active);
        Assert.True(switched.Changed);
        Assert.Equal(77, switched.Pid);
    }

    [Fact]
    public void LeavingFullscreenKeepsGameModeForTenSeconds()
    {
        var tracker = new GameModeTracker(TimeSpan.FromSeconds(10));
        DateTime now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        tracker.Observe(now, 42);
        GameModeUpdate lost = tracker.Observe(now.AddSeconds(1), null);
        Assert.True(lost.Active);
        Assert.False(lost.ExitDue);

        GameModeUpdate held = tracker.Observe(now.AddSeconds(10.9), null);
        Assert.True(held.Active);
        Assert.False(held.ExitDue);

        GameModeUpdate exited = tracker.Observe(now.AddSeconds(11.1), null);
        Assert.False(exited.Active);
        Assert.True(exited.ExitDue);
        Assert.Equal(42, exited.PreviousPid);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void ReturningToTheGameCancelsTheExitDelay()
    {
        var tracker = new GameModeTracker(TimeSpan.FromSeconds(10));
        DateTime now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        tracker.Observe(now, 42);
        tracker.Observe(now.AddSeconds(1), null);
        GameModeUpdate back = tracker.Observe(now.AddSeconds(9), 42);
        Assert.True(back.Active);
        // The grace window never deactivated, so the target did not change.
        Assert.False(back.Changed);

        GameModeUpdate later = tracker.Observe(now.AddSeconds(30), null);
        Assert.True(later.Active);
        Assert.False(later.ExitDue);
    }

    [Fact]
    public void IdleTrackerIgnoresMissingGame()
    {
        var tracker = new GameModeTracker();
        GameModeUpdate update = tracker.Observe(DateTime.UtcNow, null);
        Assert.False(update.Active);
        Assert.False(update.ExitDue);
        Assert.False(tracker.IsActive);
    }

    [Fact]
    public void ResetClearsTheActiveTarget()
    {
        var tracker = new GameModeTracker();
        tracker.Observe(DateTime.UtcNow, 42);
        tracker.Reset();
        Assert.False(tracker.IsActive);
        Assert.Null(tracker.Pid);
    }
}
