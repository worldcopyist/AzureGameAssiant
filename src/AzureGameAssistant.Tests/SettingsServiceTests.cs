using AzureGameAssistant.Core.Settings;

namespace AzureGameAssistant.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "AzureGameAssistant.Tests",
        Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    public SettingsServiceTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var service = new SettingsService(SettingsPath);

        SettingsLoadResult result = service.Load();

        Assert.False(result.Recovered);
        Assert.Null(result.Warning);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.Equal(1_000, result.Settings.Sampling.NormalMs);
        Assert.Equal(200, result.Settings.Sampling.GameMs);
        Assert.False(result.Settings.Ai.Enabled);
        Assert.True(result.Settings.Charts.Cpu);
        Assert.True(result.Settings.Overlay.Enabled);
        Assert.Equal("TopLeft", result.Settings.Overlay.Corner);
        Assert.Equal("Default", result.Settings.Overlay.Theme);
        Assert.Equal("#FFFFFF", result.Settings.Overlay.Color);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsValues()
    {
        var service = new SettingsService(SettingsPath);
        AppSettings settings = new();
        settings.Sampling.NormalMs = 2_000;
        settings.Sampling.GameMs = 250;
        settings.Theme = "dark";
        settings.GameDetection.Exclude.Add("obs.exe");

        service.Save(settings);
        SettingsLoadResult result = service.Load();

        Assert.False(result.Recovered);
        Assert.Equal(2_000, result.Settings.Sampling.NormalMs);
        Assert.Equal(250, result.Settings.Sampling.GameMs);
        Assert.Equal("Dark", result.Settings.Theme);
        Assert.Contains("obs.exe", result.Settings.GameDetection.Exclude);
    }

    [Fact]
    public void Load_CorruptFile_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        var service = new SettingsService(SettingsPath);

        SettingsLoadResult result = service.Load();

        Assert.True(result.Recovered);
        Assert.NotNull(result.Warning);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.False(File.Exists(SettingsPath));
        Assert.Single(Directory.GetFiles(_directory, "settings.corrupt-*.json"));
    }

    [Fact]
    public void Load_NewerSchema_BacksUpAndReturnsDefaults()
    {
        File.WriteAllText(SettingsPath, """{"schemaVersion": 99}""");
        var service = new SettingsService(SettingsPath);

        SettingsLoadResult result = service.Load();

        Assert.True(result.Recovered);
        Assert.Contains("99", result.Warning!);
        Assert.Single(Directory.GetFiles(_directory, "settings.unsupported-*.json"));
    }

    [Fact]
    public void Save_ProducesCamelCaseJsonWithoutApiKey()
    {
        var service = new SettingsService(SettingsPath);
        service.Save(new AppSettings());

        string json = File.ReadAllText(SettingsPath);

        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"activeProvider\"", json);
        Assert.DoesNotContain("apiKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_ClampsInvalidValues()
    {
        var settings = new AppSettings
        {
            Sampling = new SamplingSettings { NormalMs = 10, GameMs = 99_999 },
            Sessions = new SessionSettings { MaxCount = 0, MaxTotalMb = 1 },
            Theme = "PURPLE",
            Language = "fr",
            Frames = new FrameSettings { Mode = "weird", TargetPid = 0 },
        };

        settings.Normalize();

        Assert.Equal(100, settings.Sampling.NormalMs);
        Assert.Equal(100, settings.Sampling.GameMs);
        Assert.Equal(1, settings.Sessions.MaxCount);
        Assert.Equal(10, settings.Sessions.MaxTotalMb);
        Assert.Equal("Auto", settings.Theme);
        Assert.Equal("auto", settings.Language);
        Assert.Equal("auto", settings.Frames.Mode);
        Assert.Null(settings.Frames.TargetPid);
        Assert.Equal("Default", settings.Overlay.Theme);
        Assert.Equal("#FFFFFF", settings.Overlay.Color);
    }

    [Fact]
    public void Normalize_KeepsOneChartAndClampsOverlay()
    {
        var settings = new AppSettings
        {
            Charts = new ChartSettings
            {
                Cpu = false,
                Gpu = false,
                Memory = false,
                Vram = false,
                Disk = false,
            },
            Overlay = new OverlaySettings
            {
                Corner = "  topright ",
                OffsetX = -5,
                OffsetY = 99_999,
                ShowFps = false,
                ShowFrameTimes = false,
                ShowCpu = false,
                ShowGpu = false,
                ShowTemperature = false,
                ShowMemory = false,
                ShowVram = false,
                Theme = " custom ",
                Color = " 00ff7f ",
            },
        };

        settings.Normalize();

        Assert.True(settings.Charts.Cpu);
        Assert.Equal("TopRight", settings.Overlay.Corner);
        Assert.Equal(0, settings.Overlay.OffsetX);
        Assert.Equal(4_000, settings.Overlay.OffsetY);
        Assert.True(settings.Overlay.ShowCpu);
        Assert.True(settings.Overlay.ShowGpu);
        Assert.Equal("Custom", settings.Overlay.Theme);
        Assert.Equal("#00FF7F", settings.Overlay.Color);
    }

    [Fact]
    public void Normalize_InvalidOverlayColorFallsBackToWhite()
    {
        var settings = new AppSettings
        {
            Overlay = new OverlaySettings { Theme = "neon", Color = "#12ZZ34" },
        };

        settings.Normalize();

        Assert.Equal("Default", settings.Overlay.Theme);
        Assert.Equal("#FFFFFF", settings.Overlay.Color);
    }
}
