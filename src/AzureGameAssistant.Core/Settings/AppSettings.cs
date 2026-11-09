using System.Globalization;

namespace AzureGameAssistant.Core.Settings;

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 3;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public SamplingSettings Sampling { get; set; } = new();
    public bool RequestElevationOnStartup { get; set; } = true;
    public DetectionSettings Detection { get; set; } = new();
    public FrameSettings Frames { get; set; } = new();
    public SensorSettings Sensors { get; set; } = new();
    public string Hotkey { get; set; } = "Ctrl+Alt+M";
    public bool AutoStart { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public string Theme { get; set; } = "Auto";
    public string Language { get; set; } = "zh-CN";
    public SessionSettings Sessions { get; set; } = new();
    public GameDetectionSettings GameDetection { get; set; } = new();
    public AiSettings Ai { get; set; } = new();
    public ChartSettings Charts { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public DiagnosticsSettings Diagnostics { get; set; } = new();
    public LogSettings Logs { get; set; } = new();

    public void Normalize()
    {
        SchemaVersion = CurrentSchemaVersion;

        Sampling ??= new SamplingSettings();
        Sampling.NormalMs = Math.Clamp(Sampling.NormalMs, 100, 10_000);
        Sampling.GameMs = Math.Clamp(Sampling.GameMs, 50, Sampling.NormalMs);

        Detection ??= new DetectionSettings();
        Detection.JitterSuspectMs = Math.Clamp(Detection.JitterSuspectMs, 1, 2_000);
        Detection.JitterConfirmMs = Math.Clamp(Detection.JitterConfirmMs, Detection.JitterSuspectMs, 5_000);
        Detection.FrameSpikeRatio = Math.Clamp(Detection.FrameSpikeRatio, 1.0, 20.0);
        Detection.FrameSpikeFloorMs = Math.Clamp(Detection.FrameSpikeFloorMs, 1.0, 5_000.0);
        Detection.DisplayGapMs = Math.Clamp(Detection.DisplayGapMs, 10.0, 10_000.0);
        Detection.CpuSpikeRatio = Math.Clamp(Detection.CpuSpikeRatio, 1.0, 20.0);
        Detection.GpuSpikePct = Math.Clamp(Detection.GpuSpikePct, 1.0, 100.0);
        Detection.MemAvailableFloorMb = Math.Clamp(Detection.MemAvailableFloorMb, 128, 1_000_000);
        Detection.CommitRatio = Math.Clamp(Detection.CommitRatio, 0.5, 1.0);
        Detection.DiskLatencyMs = Math.Clamp(Detection.DiskLatencyMs, 1.0, 10_000.0);
        Detection.ConfirmScore = Math.Clamp(Detection.ConfirmScore, 0, 100);
        Detection.QuietMs = Math.Clamp(Detection.QuietMs, 500, 10_000);
        Detection.CooldownMs = Math.Clamp(Detection.CooldownMs, 0, 60_000);

        Frames ??= new FrameSettings();
        Frames.Mode = Frames.Mode?.Trim().ToLowerInvariant() switch
        {
            "pid" => "pid",
            "foreground" => "foreground",
            _ => "auto",
        };
        if (Frames.TargetPid is <= 0)
        {
            Frames.TargetPid = null;
        }
        Frames.PresentMonPath = string.IsNullOrWhiteSpace(Frames.PresentMonPath)
            ? "presentmon/PresentMon-2.6.0-x64.exe"
            : Frames.PresentMonPath.Trim();

        Sensors ??= new SensorSettings();
        Sensors.IntervalMs = Math.Clamp(Sensors.IntervalMs, 500, 10_000);
        Sensors.CpuMode = (Sensors.CpuMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "driver" => "driver",
            "off" => "off",
            _ => "acpi",
        };

        Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? "Ctrl+Alt+M" : Hotkey.Trim();

        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => "Light",
            "dark" => "Dark",
            _ => "Auto",
        };

        Language = Language?.Trim().ToLowerInvariant() switch
        {
            "en" or "en-us" => "en-US",
            "zh" or "zh-cn" => "zh-CN",
            _ => "auto",
        };

        Sessions ??= new SessionSettings();
        Sessions.MaxCount = Math.Clamp(Sessions.MaxCount, 1, 500);
        Sessions.MaxTotalMb = Math.Clamp(Sessions.MaxTotalMb, 10, 10_000);

        GameDetection ??= new GameDetectionSettings();
        GameDetection.Exclude = (GameDetection.Exclude ?? new List<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Charts ??= new ChartSettings();
        if (!Charts.Cpu && !Charts.Gpu && !Charts.Memory && !Charts.Vram && !Charts.Disk && !Charts.Frames)
        {
            Charts.Cpu = true;
        }

        Diagnostics ??= new DiagnosticsSettings();
        Diagnostics.IntervalSeconds = Diagnostics.IntervalSeconds switch
        {
            < 7 => 5,
            < 20 => 10,
            _ => 30,
        };

        Logs ??= new LogSettings();
        Logs.RetentionDays = Math.Clamp(Logs.RetentionDays, 1, 30);

        Overlay ??= new OverlaySettings();
        Overlay.Corner = Overlay.Corner?.Trim().ToLowerInvariant() switch
        {
            "topright" => "TopRight",
            "bottomleft" => "BottomLeft",
            "bottomright" => "BottomRight",
            _ => "TopLeft",
        };
        Overlay.OffsetX = Math.Clamp(Overlay.OffsetX, 0, 4_000);
        Overlay.OffsetY = Math.Clamp(Overlay.OffsetY, 0, 4_000);
        Overlay.Theme = Overlay.Theme?.Trim().ToLowerInvariant() switch
        {
            "light" => "Light",
            "azure" => "Azure",
            "custom" => "Custom",
            _ => "Default",
        };
        Overlay.Color = NormalizeColor(Overlay.Color);
        if (!Overlay.ShowFps &&
            !Overlay.ShowFrameTimes &&
            !Overlay.ShowCpu &&
            !Overlay.ShowGpu &&
            !Overlay.ShowTemperature &&
            !Overlay.ShowCpuTemperature &&
            !Overlay.ShowMemory &&
            !Overlay.ShowVram)
        {
            Overlay.ShowCpu = true;
            Overlay.ShowGpu = true;
        }

        Ai ??= new AiSettings();
        Ai.AutoAnalyzeMinScore = Math.Clamp(Ai.AutoAnalyzeMinScore, 0, 100);
        Ai.DailyBudget = double.IsFinite(Ai.DailyBudget) ? Math.Max(0, Ai.DailyBudget) : 0;
        Ai.MonthlyBudget = double.IsFinite(Ai.MonthlyBudget) ? Math.Max(0, Ai.MonthlyBudget) : 0;
        Ai.ConsentSignature = (Ai.ConsentSignature ?? string.Empty).Trim();
        Ai.Providers = (Ai.Providers ?? new List<AiProviderSettings>())
            .OfType<AiProviderSettings>()
            .Where(provider => !string.IsNullOrWhiteSpace(provider.Id) && !string.IsNullOrWhiteSpace(provider.BaseUrl))
            .ToList();

        foreach (AiProviderSettings provider in Ai.Providers)
        {
            provider.Id = provider.Id.Trim().ToLowerInvariant();
            provider.BaseUrl = provider.BaseUrl.Trim().TrimEnd('/');
            provider.Model = provider.Model?.Trim() ?? string.Empty;
            provider.MaxInputTokens = Math.Clamp(provider.MaxInputTokens, 1_000, 200_000);
            provider.MaxOutputTokens = Math.Clamp(provider.MaxOutputTokens, 128, 32_000);
            provider.InputPricePerMillion = Math.Max(0, provider.InputPricePerMillion);
            provider.OutputPricePerMillion = Math.Max(0, provider.OutputPricePerMillion);
        }

        if (Ai.Providers.Count > 0 &&
            !Ai.Providers.Any(provider => string.Equals(provider.Id, Ai.ActiveProvider, StringComparison.OrdinalIgnoreCase)))
        {
            Ai.ActiveProvider = Ai.Providers[0].Id;
        }
        else
        {
            Ai.ActiveProvider = Ai.ActiveProvider?.Trim().ToLowerInvariant() ?? string.Empty;
        }
    }

    private static string NormalizeColor(string? value)
    {
        string text = (value ?? string.Empty).Trim().TrimStart('#');
        if (text.Length == 6 &&
            uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            return "#" + text.ToUpperInvariant();
        }

        return "#FFFFFF";
    }
}

public sealed class SamplingSettings
{
    public int NormalMs { get; set; } = 1_000;
    public int GameMs { get; set; } = 200;
}

public sealed class DetectionSettings
{
    public bool Enabled { get; set; } = true;
    public int JitterSuspectMs { get; set; } = 50;
    public int JitterConfirmMs { get; set; } = 100;
    public double FrameSpikeRatio { get; set; } = 2.5;
    public double FrameSpikeFloorMs { get; set; } = 50;
    public double DisplayGapMs { get; set; } = 100;
    public double CpuSpikeRatio { get; set; } = 2.5;
    public double GpuSpikePct { get; set; } = 85;
    public int MemAvailableFloorMb { get; set; } = 1_024;
    public double CommitRatio { get; set; } = 0.90;
    public double DiskLatencyMs { get; set; } = 50;
    public int ConfirmScore { get; set; } = 40;
    public int QuietMs { get; set; } = 2_000;
    public int CooldownMs { get; set; } = 5_000;
}

public sealed class FrameSettings
{
    public bool Enabled { get; set; } = true;
    public bool AutoCapture { get; set; } = true;
    public string Mode { get; set; } = "auto";
    public int? TargetPid { get; set; }
    public string PresentMonPath { get; set; } = "presentmon/PresentMon-2.6.0-x64.exe";
}

public sealed class SensorSettings
{
    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 1_000;
    public string CpuMode { get; set; } = "acpi";
}

public sealed class ChartSettings
{
    public bool Separate { get; set; }
    public bool Cpu { get; set; } = true;
    public bool Gpu { get; set; } = true;
    public bool Memory { get; set; } = true;
    public bool Vram { get; set; } = true;
    public bool Disk { get; set; }
    public bool Frames { get; set; } = true;
}

public sealed class DiagnosticsSettings
{
    public bool LongRunRecord { get; set; }
    public int IntervalSeconds { get; set; } = 5;
}

public sealed class LogSettings
{
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 3;
}

public sealed class OverlaySettings
{
    public bool Enabled { get; set; } = true;
    public string Corner { get; set; } = "TopLeft";
    public int OffsetX { get; set; } = 16;
    public int OffsetY { get; set; } = 16;
    public bool ShowFps { get; set; } = true;
    public bool ShowFrameTimes { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowTemperature { get; set; } = true;
    public bool ShowCpuTemperature { get; set; } = true;
    public bool ShowMemory { get; set; } = true;
    public bool ShowVram { get; set; }
    public string Theme { get; set; } = "Default";
    public string Color { get; set; } = "#FFFFFF";
}

public sealed class SessionSettings
{
    public int MaxCount { get; set; } = 20;
    public int MaxTotalMb { get; set; } = 500;
}

public sealed class GameDetectionSettings
{
    public bool Enabled { get; set; } = true;
    public List<string> Exclude { get; set; } =
    [
        "chrome.exe",
        "msedge.exe",
        "firefox.exe",
        "vlc.exe",
    ];
}

public sealed class AiSettings
{
    public bool Enabled { get; set; }
    public string ActiveProvider { get; set; } = "deepseek";
    public bool AutoAnalyze { get; set; }
    public int AutoAnalyzeMinScore { get; set; } = 80;
    public bool Redact { get; set; } = true;
    public bool HideProcessNames { get; set; }
    public string ConsentSignature { get; set; } = string.Empty;
    public double DailyBudget { get; set; }
    public double MonthlyBudget { get; set; }
    public List<AiProviderSettings> Providers { get; set; } =
    [
        new AiProviderSettings
        {
            Id = "deepseek",
            BaseUrl = "https://api.deepseek.com/v1",
            Model = "deepseek-chat",
        },
    ];
}

public sealed class AiProviderSettings
{
    public string Id { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool RequiresKey { get; set; } = true;
    public int MaxInputTokens { get; set; } = 8_000;
    public int MaxOutputTokens { get; set; } = 2_000;
    public double InputPricePerMillion { get; set; }
    public double OutputPricePerMillion { get; set; }
}
