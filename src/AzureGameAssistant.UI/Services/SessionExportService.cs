using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AzureGameAssistant.Ai;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sessions;
using AzureGameAssistant.UI.Controls;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Writes the Markdown report and the timeline PNG next to a user-chosen path.
/// Everything is rendered from the session file already on disk; exporting does
/// not touch the network.
/// </summary>
public static class SessionExportService
{
    private const int ChartWidth = 1200;
    private const int ChartHeight = 400;
    private const double ChartPadding = 12;
    private const double MinSpanSeconds = 10;

    public static string Export(SessionDocument document, string sessionPath, string markdownPath)
    {
        string directory = Path.GetDirectoryName(markdownPath) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(markdownPath);
        string chartFileName = baseName + SessionMarkdownReport.ChartSuffix;

        SaveChart(document, Path.Combine(directory, chartFileName));

        string markdown = SessionMarkdownReport.Build(
            document,
            CultureInfo.CurrentUICulture.Name,
            TryReadAiSummary(sessionPath),
            chartFileName);
        File.WriteAllText(markdownPath, markdown, new UTF8Encoding(false));
        return markdownPath;
    }

    /// <summary>
    /// The AI report is optional and lives in a sibling file. Missing, stale or
    /// foreign files simply mean the export has no AI section.
    /// </summary>
    public static SessionAiSummary? TryReadAiSummary(string sessionPath)
    {
        string path = sessionPath + ".ai.json";
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            AiReport? report = JsonSerializer.Deserialize(File.ReadAllText(path), AiReportJsonContext.Default.AiReport);
            return report is null ? null : Flatten(report);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static SessionAiSummary Flatten(AiReport report) => new(
        report.Summary,
        report.PrimaryCause,
        report.Confidence,
        report.Evidence.Select(entry => $"{entry.Metric}: {entry.Value} — {entry.Interpretation}").ToArray(),
        report.Recommendations.Select(entry => $"{entry.Action} — {entry.HowTo}").ToArray(),
        report.Limitations.ToArray(),
        report.Disclaimer);

    private static void SaveChart(SessionDocument document, string path)
    {
        SampleRecord[] samples = document.Samples;
        FrameTimePoint[] frames = document.Frames;

        var chart = new TimelineChart { Metric = ChartMetric.Combined };
        chart.SetSamples(samples, samples.Length);
        chart.SetFrames(frames, frames.Length);
        chart.SetEventWindow(document.Event.StartUtc, document.Event.EndUtc);
        chart.WindowSeconds = SpanSeconds(document, samples, frames);

        var root = new Border
        {
            Background = ThemeBrush("SurfaceBrush", Brushes.White),
            Padding = new Thickness(ChartPadding),
            Width = ChartWidth,
            Height = ChartHeight,
            Child = chart,
        };

        var size = new Size(ChartWidth, ChartHeight);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(ChartWidth, ChartHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(path);
        encoder.Save(stream);
    }

    private static double SpanSeconds(SessionDocument document, SampleRecord[] samples, FrameTimePoint[] frames)
    {
        long start = long.MaxValue;
        long end = 0;
        if (samples.Length > 0)
        {
            start = samples[0].UtcTicks;
            end = samples[^1].UtcTicks;
        }

        if (frames.Length > 0)
        {
            start = Math.Min(start, frames[0].UtcTicks);
            end = Math.Max(end, frames[^1].UtcTicks);
        }

        if (start == long.MaxValue || end <= start)
        {
            return Math.Max(MinSpanSeconds, document.Event.DurationMs / 1000.0);
        }

        return Math.Max(MinSpanSeconds, (end - start) / (double)TimeSpan.TicksPerSecond);
    }

    private static Brush ThemeBrush(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}
