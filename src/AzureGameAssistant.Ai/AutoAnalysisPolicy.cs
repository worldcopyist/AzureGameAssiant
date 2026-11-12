namespace AzureGameAssistant.Ai;

public sealed record AutoAnalysisInput(
    bool AiEnabled,
    bool AutoEnabled,
    int Score,
    int MinScore,
    double DailyBudget,
    double MonthlyBudget,
    bool ConsentMatched,
    bool AlreadyAnalyzed,
    string? ConfigurationError,
    bool RequiresKey,
    bool KeyAvailable,
    bool BudgetAllowed);

public sealed record AutoAnalysisDecision(bool ShouldRun, string Reason);

/// <summary>
/// Gate for FR-020 background analysis. Every privacy and cost prerequisite
/// must already hold; the feature never prompts and never sends a request on
/// its own beyond a session the user already let the recorder keep.
/// </summary>
public static class AutoAnalysisPolicy
{
    public static AutoAnalysisDecision Evaluate(AutoAnalysisInput input)
    {
        if (!input.AiEnabled)
        {
            return new AutoAnalysisDecision(false, "ai-disabled");
        }

        if (!input.AutoEnabled)
        {
            return new AutoAnalysisDecision(false, "auto-disabled");
        }

        if (input.Score < input.MinScore)
        {
            return new AutoAnalysisDecision(false, "score-below-min");
        }

        if (input.DailyBudget <= 0 || input.MonthlyBudget <= 0)
        {
            return new AutoAnalysisDecision(false, "budget-unset");
        }

        if (input.AlreadyAnalyzed)
        {
            return new AutoAnalysisDecision(false, "already-analyzed");
        }

        if (!input.ConsentMatched)
        {
            return new AutoAnalysisDecision(false, "consent-required");
        }

        if (input.ConfigurationError is not null)
        {
            return new AutoAnalysisDecision(false, "config-error");
        }

        if (input.RequiresKey && !input.KeyAvailable)
        {
            return new AutoAnalysisDecision(false, "key-missing");
        }

        if (!input.BudgetAllowed)
        {
            return new AutoAnalysisDecision(false, "budget-exhausted");
        }

        return new AutoAnalysisDecision(true, "ok");
    }
}
