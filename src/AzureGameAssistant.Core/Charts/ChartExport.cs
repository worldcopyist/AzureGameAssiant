using System.Globalization;
using System.Text;
using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Charts;

/// <summary>
/// Builds the CSV for the part of the timeline currently shown by a chart so
/// users can archive or spreadsheet-review exactly what they were looking at.
/// Kept in Core so the row filtering and number formatting are unit tested.
/// </summary>
public static class ChartExport
{
    public const string CsvHeader =
        "utc,cpu_pct,gpu_pct,mem_used_pct,mem_commit_mb,mem_commit_limit_mb,disk_busy_pct,disk_latency_ms,vram_dedicated_mb,frame_time_ms,frame_spike";

    private const long FrameMatchTicks = TimeSpan.TicksPerSecond;

    public static string BuildVisibleCsv(
        SampleRecord[] samples,
        int sampleCount,
        FrameTimePoint[] frames,
        int frameCount,
        long startUtc,
        long endUtc)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(frames);

        int count = Math.Clamp(sampleCount, 0, samples.Length);
        int framesUsed = Math.Clamp(frameCount, 0, frames.Length);
        if (endUtc <= startUtc)
        {
            return CsvHeader + "\n";
        }

        var builder = new StringBuilder(CsvHeader.Length + (count * 80));
        builder.Append(CsvHeader).Append('\n');

        int frameIndex = 0;
        for (int index = 0; index < count; index++)
        {
            ref SampleRecord sample = ref samples[index];
            if (sample.UtcTicks < startUtc)
            {
                continue;
            }

            if (sample.UtcTicks > endUtc)
            {
                break;
            }

            while (frameIndex + 1 < framesUsed && frames[frameIndex + 1].UtcTicks <= sample.UtcTicks)
            {
                frameIndex++;
            }

            FrameTimePoint? frame = null;
            if (frameIndex < framesUsed &&
                frames[frameIndex].UtcTicks <= sample.UtcTicks &&
                sample.UtcTicks - frames[frameIndex].UtcTicks <= FrameMatchTicks)
            {
                frame = frames[frameIndex];
            }

            AppendRow(builder, sample, frame);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, in SampleRecord sample, FrameTimePoint? frame)
    {
        builder
            .Append(FormatUtc(sample.UtcTicks)).Append(',')
            .Append(F(sample.CpuUtilityPct)).Append(',')
            .Append(F(sample.GpuUtilPct)).Append(',')
            .Append(F(TelemetryMath.MemoryUsagePct(sample))).Append(',')
            .Append(sample.MemCommitMb.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(sample.MemCommitLimitMb.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(F(sample.DiskBusyPct)).Append(',')
            .Append(F2(sample.DiskLatencyMs)).Append(',')
            .Append(sample.GpuDedicatedMb.ToString(CultureInfo.InvariantCulture)).Append(',');

        if (frame is FrameTimePoint point)
        {
            builder
                .Append(F2(point.FrameTimeMs)).Append(',')
                .Append(point.Spike ? "1" : "0");
        }
        else
        {
            builder.Append(",0");
        }

        builder.Append('\n');
    }

    private static string FormatUtc(long ticks)
    {
        if (ticks <= 0)
        {
            return string.Empty;
        }

        DateTime utc = new(ticks, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }

    private static string F(float value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string F(double value) => value.ToString("F1", CultureInfo.InvariantCulture);

    private static string F2(float value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
