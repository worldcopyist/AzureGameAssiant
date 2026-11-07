using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AzureGameAssistant.Core;

namespace AzureGameAssistant.Ai;

public static class CostEstimator
{
    /// <summary>
    /// Conservative estimate: UTF-8 bytes / 3, rounded up. CJK text costs more
    /// per character than this, but the ceiling is intentional.
    /// </summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(Encoding.UTF8.GetByteCount(text) / 3.0);

    public static double EstimateCost(
        int inputTokens,
        int outputTokens,
        double inputPricePerMillion,
        double outputPricePerMillion) =>
        (inputTokens / 1_000_000.0 * inputPricePerMillion) +
        (outputTokens / 1_000_000.0 * outputPricePerMillion);
}

public sealed record AiBudgetStatus(
    bool Allowed,
    string? Reason,
    double DayCost,
    double MonthCost,
    double? DayRemaining,
    double? MonthRemaining);

public sealed class AiUsageStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private UsageFile _usage;

    public AiUsageStore(string? path = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path ?? AppPaths.AiUsagePath;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _usage = Load();
    }

    public AiBudgetStatus Check(double estimatedCost, double dailyBudget, double monthlyBudget)
    {
        lock (_gate)
        {
            Rollover();
            double day = _usage.DayCost;
            double month = _usage.MonthCost;
            double used = Math.Max(0, estimatedCost);

            if (dailyBudget > 0 && day + used > dailyBudget)
            {
                return new AiBudgetStatus(false, "已达到每日预算上限", day, month, Math.Max(0, dailyBudget - day), null);
            }

            if (monthlyBudget > 0 && month + used > monthlyBudget)
            {
                return new AiBudgetStatus(false, "已达到每月预算上限", day, month, null, Math.Max(0, monthlyBudget - month));
            }

            return new AiBudgetStatus(
                true,
                null,
                day,
                month,
                dailyBudget > 0 ? Math.Max(0, dailyBudget - day) : null,
                monthlyBudget > 0 ? Math.Max(0, monthlyBudget - month) : null);
        }
    }

    public void Record(int inputTokens, int outputTokens, double cost)
    {
        lock (_gate)
        {
            Rollover();
            _usage.DayInputTokens += Math.Max(0, inputTokens);
            _usage.DayOutputTokens += Math.Max(0, outputTokens);
            _usage.MonthInputTokens += Math.Max(0, inputTokens);
            _usage.MonthOutputTokens += Math.Max(0, outputTokens);
            _usage.DayCost += Math.Max(0, cost);
            _usage.MonthCost += Math.Max(0, cost);
            Save();
        }
    }

    public (int Input, int Output, double Cost) Snapshot()
    {
        lock (_gate)
        {
            Rollover();
            return (_usage.DayInputTokens, _usage.DayOutputTokens, _usage.DayCost);
        }
    }

    private void Rollover()
    {
        DateTimeOffset now = _clock();
        string day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        if (!string.Equals(_usage.Day, day, StringComparison.Ordinal))
        {
            _usage.Day = day;
            _usage.DayCost = 0;
            _usage.DayInputTokens = 0;
            _usage.DayOutputTokens = 0;
        }

        if (!string.Equals(_usage.Month, month, StringComparison.Ordinal))
        {
            _usage.Month = month;
            _usage.MonthCost = 0;
            _usage.MonthInputTokens = 0;
            _usage.MonthOutputTokens = 0;
        }
    }

    private UsageFile Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                UsageFile? loaded = JsonSerializer.Deserialize(File.ReadAllText(_path), UsageJsonContext.Default.UsageFile);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }

        DateTimeOffset now = _clock();
        return new UsageFile
        {
            Day = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Month = now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_usage, UsageJsonContext.Default.UsageFile));
            File.Move(temp, _path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public sealed class UsageFile
{
    public string Day { get; set; } = string.Empty;
    public string Month { get; set; } = string.Empty;
    public double DayCost { get; set; }
    public double MonthCost { get; set; }
    public int DayInputTokens { get; set; }
    public int DayOutputTokens { get; set; }
    public int MonthInputTokens { get; set; }
    public int MonthOutputTokens { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(UsageFile))]
public partial class UsageJsonContext : JsonSerializerContext
{
}
