using AzureGameAssistant.Ai;

namespace AzureGameAssistant.Tests;

public sealed class AutoAnalysisPolicyTests
{
    private static AutoAnalysisInput Allowed() => new(
        AiEnabled: true,
        AutoEnabled: true,
        Score: 85,
        MinScore: 80,
        DailyBudget: 1,
        MonthlyBudget: 10,
        ConsentMatched: true,
        AlreadyAnalyzed: false,
        ConfigurationError: null,
        RequiresKey: true,
        KeyAvailable: true,
        BudgetAllowed: true);

    [Fact]
    public void RunsOnlyWhenEveryPrerequisiteHolds()
    {
        AutoAnalysisDecision decision = AutoAnalysisPolicy.Evaluate(Allowed());

        Assert.True(decision.ShouldRun);
        Assert.Equal("ok", decision.Reason);
    }

    [Fact]
    public void ScoreMustReachTheThreshold()
    {
        Assert.False(AutoAnalysisPolicy.Evaluate(Allowed() with { Score = 79 }).ShouldRun);
        Assert.True(AutoAnalysisPolicy.Evaluate(Allowed() with { Score = 80 }).ShouldRun);
    }

    [Fact]
    public void DisabledSwitchesNeverRun()
    {
        Assert.Equal("ai-disabled", AutoAnalysisPolicy.Evaluate(Allowed() with { AiEnabled = false }).Reason);
        Assert.Equal("auto-disabled", AutoAnalysisPolicy.Evaluate(Allowed() with { AutoEnabled = false }).Reason);
    }

    [Fact]
    public void BudgetsMustBeSetExplicitly()
    {
        Assert.Equal("budget-unset", AutoAnalysisPolicy.Evaluate(Allowed() with { DailyBudget = 0 }).Reason);
        Assert.Equal("budget-unset", AutoAnalysisPolicy.Evaluate(Allowed() with { MonthlyBudget = 0 }).Reason);
    }

    [Fact]
    public void CostPrivacyAndConfigurationGateTheRequest()
    {
        Assert.Equal("already-analyzed", AutoAnalysisPolicy.Evaluate(Allowed() with { AlreadyAnalyzed = true }).Reason);
        Assert.Equal("consent-required", AutoAnalysisPolicy.Evaluate(Allowed() with { ConsentMatched = false }).Reason);
        Assert.Equal("config-error", AutoAnalysisPolicy.Evaluate(Allowed() with { ConfigurationError = "bad url" }).Reason);
        Assert.Equal("key-missing", AutoAnalysisPolicy.Evaluate(Allowed() with { KeyAvailable = false }).Reason);
        Assert.Equal("budget-exhausted", AutoAnalysisPolicy.Evaluate(Allowed() with { BudgetAllowed = false }).Reason);
    }

    [Fact]
    public void LocalProviderDoesNotNeedAKey()
    {
        AutoAnalysisDecision decision = AutoAnalysisPolicy.Evaluate(
            Allowed() with { RequiresKey = false, KeyAvailable = false });

        Assert.True(decision.ShouldRun);
    }
}
