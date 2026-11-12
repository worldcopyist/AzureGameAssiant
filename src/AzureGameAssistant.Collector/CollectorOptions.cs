using System.Globalization;

namespace AzureGameAssistant.Collector;

internal sealed class CollectorOptions
{
    public TimeSpan? Duration { get; private init; }
    public int? IntervalOverrideMs { get; private init; }
    public bool EnablePipe { get; private init; } = true;
    public int? ParentProcessId { get; private init; }
    public bool ShowHelp { get; private init; }
    public string? ParseError { get; private init; }

    public static CollectorOptions Parse(string[] args)
    {
        TimeSpan? duration = null;
        int? interval = null;
        bool pipe = true;
        int? parent = null;
        bool help = false;
        string? error = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help" or "-h":
                    help = true;
                    break;

                case "--no-pipe":
                    pipe = false;
                    break;

                case "--duration":
                    if (i + 1 < args.Length &&
                        double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
                        seconds > 0)
                    {
                        duration = TimeSpan.FromSeconds(seconds);
                        i++;
                    }
                    else
                    {
                        error = "--duration 需要一个正数秒数";
                    }

                    break;

                case "--parent":
                    if (i + 1 < args.Length &&
                        int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentPid) &&
                        parentPid > 0)
                    {
                        parent = parentPid;
                        i++;
                    }
                    else
                    {
                        error = "--parent 需要一个有效的进程号";
                    }

                    break;

                case "--interval":
                    if (i + 1 < args.Length &&
                        int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int intervalMs) &&
                        intervalMs >= 100 &&
                        intervalMs <= 10_000)
                    {
                        interval = intervalMs;
                        i++;
                    }
                    else
                    {
                        error = "--interval 需要 100–10000 之间的毫秒数";
                    }

                    break;
            }
        }

        return new CollectorOptions
        {
            Duration = duration,
            IntervalOverrideMs = interval,
            EnablePipe = pipe,
            ParentProcessId = parent,
            ShowHelp = help,
            ParseError = error,
        };
    }
}
