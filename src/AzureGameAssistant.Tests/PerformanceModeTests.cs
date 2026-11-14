using AzureGameAssistant.Core.Performance;

namespace AzureGameAssistant.Tests;

public sealed class PerformanceModeTests
{
    private static readonly DateTime T0 = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

    private static PerformanceMode Create(bool enabled = true) =>
        new(enabled, enterDelay: TimeSpan.FromSeconds(3), exitDelay: TimeSpan.FromSeconds(12));

    private static LoadSample[] Heavy() => [new LoadSample(4321, 40, 10)];

    private static LoadSample[] Calm() => [new LoadSample(4321, 5, 5)];

    [Fact]
    public void Observe_HeavySustained_EntersAfterDelayOnly()
    {
        PerformanceMode mode = Create();

        Assert.False(mode.Observe(T0, Heavy(), appFocused: false));
        Assert.False(mode.IsReduced);
        Assert.False(mode.Observe(T0.AddSeconds(2.9), Heavy(), appFocused: false));
        Assert.False(mode.IsReduced);

        Assert.True(mode.Observe(T0.AddSeconds(3), Heavy(), appFocused: false));
        Assert.True(mode.IsReduced);
        Assert.Equal(4321u, mode.TriggerPid);
    }

    [Fact]
    public void Observe_GpuHeavyAlsoEnters()
    {
        PerformanceMode mode = Create();
        LoadSample[] gpu = [new LoadSample(99, 2, 35)];

        mode.Observe(T0, gpu, appFocused: false);
        Assert.True(mode.Observe(T0.AddSeconds(3), gpu, appFocused: false));
        Assert.True(mode.IsReduced);
    }

    [Fact]
    public void Observe_BelowThresholds_StaysFull()
    {
        PerformanceMode mode = Create();
        LoadSample[] busyButLow = [new LoadSample(1, 19.9, 29.9)];

        for (int i = 0; i < 10; i++)
        {
            Assert.False(mode.Observe(T0.AddSeconds(i), busyButLow, appFocused: false));
        }

        Assert.False(mode.IsReduced);
    }

    [Fact]
    public void Observe_ShortBurst_DoesNotEnter()
    {
        PerformanceMode mode = Create();

        mode.Observe(T0, Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(1), Calm(), appFocused: false);
        mode.Observe(T0.AddSeconds(2), Heavy(), appFocused: false);

        Assert.False(mode.Observe(T0.AddSeconds(3.5), Heavy(), appFocused: false));
        Assert.False(mode.IsReduced);
    }

    [Fact]
    public void Observe_Calm_ExitsAfterDelayAndRestoresModules()
    {
        PerformanceMode mode = Create();
        mode.Observe(T0, Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(3), Heavy(), appFocused: false);
        Assert.True(mode.IsReduced);
        Assert.False(mode.IsEnabled(PerformanceModule.Charts));

        Assert.False(mode.Observe(T0.AddSeconds(4), Calm(), appFocused: false));
        Assert.True(mode.IsReduced);
        Assert.False(mode.Observe(T0.AddSeconds(15.9), Calm(), appFocused: false));
        Assert.True(mode.IsReduced);

        Assert.True(mode.Observe(T0.AddSeconds(16), Calm(), appFocused: false));
        Assert.False(mode.IsReduced);
        Assert.Equal(0u, mode.TriggerPid);
        Assert.True(mode.IsEnabled(PerformanceModule.Charts));
    }

    [Fact]
    public void Observe_EmptySamples_CountAsCalm()
    {
        PerformanceMode mode = Create();
        mode.Observe(T0, Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(3), Heavy(), appFocused: false);
        Assert.True(mode.IsReduced);

        Assert.False(mode.Observe(T0.AddSeconds(20), [], appFocused: false));
        Assert.True(mode.IsReduced);

        Assert.True(mode.Observe(T0.AddSeconds(32), [], appFocused: false));
        Assert.False(mode.IsReduced);
    }

    [Fact]
    public void Observe_AppFocused_RestoresImmediatelyAndRestartsDelay()
    {
        PerformanceMode mode = Create();
        mode.Observe(T0, Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(3), Heavy(), appFocused: false);
        Assert.True(mode.IsReduced);

        Assert.True(mode.Observe(T0.AddSeconds(4), Heavy(), appFocused: true));
        Assert.False(mode.IsReduced);

        // The focus release clears the timer, so one heavy tick is not enough.
        Assert.False(mode.Observe(T0.AddSeconds(5), Heavy(), appFocused: false));
        Assert.False(mode.IsReduced);
        Assert.True(mode.Observe(T0.AddSeconds(8), Heavy(), appFocused: false));
        Assert.True(mode.IsReduced);
    }

    [Fact]
    public void TriggerPid_TracksWorstOffender()
    {
        PerformanceMode mode = Create();
        LoadSample[] samples =
        [
            new LoadSample(10, 25, 0),
            new LoadSample(20, 10, 60),
        ];

        mode.Observe(T0, samples, appFocused: false);
        mode.Observe(T0.AddSeconds(3), samples, appFocused: false);

        Assert.True(mode.IsReduced);
        Assert.Equal(20u, mode.TriggerPid);
    }

    [Fact]
    public void Disabled_NeverEnters_AndReleasesRunningReduction()
    {
        PerformanceMode mode = Create();
        mode.Observe(T0, Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(3), Heavy(), appFocused: false);
        Assert.True(mode.IsReduced);

        Assert.True(mode.SetEnabled(false));
        Assert.False(mode.IsReduced);

        mode.Observe(T0.AddSeconds(4), Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(8), Heavy(), appFocused: false);
        Assert.False(mode.IsReduced);
    }

    [Fact]
    public void DisabledFromStart_StaysFull()
    {
        PerformanceMode mode = Create(enabled: false);

        for (int i = 0; i < 10; i++)
        {
            Assert.False(mode.Observe(T0.AddSeconds(i), Heavy(), appFocused: false));
        }

        Assert.False(mode.IsReduced);
        Assert.True(mode.IsEnabled(PerformanceModule.Charts));
    }

    [Fact]
    public void AlwaysOnModules_StayEnabledWhileReduced()
    {
        PerformanceMode mode = Create();
        mode.Observe(T0, Heavy(), appFocused: false);
        mode.Observe(T0.AddSeconds(3), Heavy(), appFocused: false);

        Assert.True(mode.IsReduced);
        Assert.True(mode.IsEnabled(PerformanceModule.Sampling));
        Assert.True(mode.IsEnabled(PerformanceModule.FrameCapture));
        Assert.True(mode.IsEnabled(PerformanceModule.StutterMarking));
        Assert.False(mode.IsEnabled(PerformanceModule.Overlay));
        Assert.False(mode.IsEnabled(PerformanceModule.Sessions));
        Assert.False(mode.IsEnabled(PerformanceModule.Diagnostics));
        Assert.False(mode.IsEnabled(PerformanceModule.SelfUsage));
        Assert.False(mode.IsEnabled(PerformanceModule.ProcessView));
        Assert.False(mode.IsEnabled(PerformanceModule.TrayTip));
        Assert.False(mode.IsEnabled(PerformanceModule.Notifications));
        Assert.False(mode.IsEnabled(PerformanceModule.AiAnalysis));
    }
}
