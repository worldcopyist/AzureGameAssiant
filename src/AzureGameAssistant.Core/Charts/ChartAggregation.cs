using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Core.Charts;

/// <summary>
/// Down-sampling helpers for the custom timeline. Each pixel column stores the
/// min and max of the samples that fall into it so spikes survive zooming.
/// </summary>
public static class ChartAggregation
{
    public static double SpanSeconds(long firstTicks, long lastTicks, int count) =>
        count > 1 ? (lastTicks - firstTicks) / (double)TimeSpan.TicksPerSecond : 0;

    public static double LatestEndTicks(
        long lastSampleTicks,
        int sampleCount,
        long lastFrameTicks,
        int frameCount,
        bool includeFrames)
    {
        double end = sampleCount > 0 ? lastSampleTicks : frameCount > 0 ? lastFrameTicks : 0;
        if (includeFrames && frameCount > 0)
        {
            end = Math.Max(end, lastFrameTicks);
        }

        return end;
    }

    public static void AggregateSamples(
        SampleRecord[] samples,
        int count,
        Func<SampleRecord, double> selector,
        double startTicks,
        double endTicks,
        int width,
        float[] min,
        float[] max)
    {
        Array.Fill(min, float.NaN, 0, width);
        Array.Fill(max, float.NaN, 0, width);

        double span = Math.Max(1, endTicks - startTicks);
        int safeCount = Math.Clamp(count, 0, samples.Length);
        for (int i = 0; i < safeCount; i++)
        {
            SampleRecord sample = samples[i];
            double offset = (sample.UtcTicks - startTicks) / span;
            if (offset < 0 || offset > 1)
            {
                continue;
            }

            int x = Math.Clamp((int)(offset * width), 0, width - 1);
            float value = (float)Math.Max(0, selector(sample));

            if (float.IsNaN(min[x]) || value < min[x])
            {
                min[x] = value;
            }

            if (float.IsNaN(max[x]) || value > max[x])
            {
                max[x] = value;
            }
        }
    }

    public static double AggregateFrames(
        FrameTimePoint[] frames,
        int count,
        double startTicks,
        double endTicks,
        int width,
        float[] min,
        float[] max,
        float[] spikes)
    {
        Array.Fill(min, float.NaN, 0, width);
        Array.Fill(max, float.NaN, 0, width);
        Array.Fill(spikes, float.NaN, 0, width);

        double span = Math.Max(1, endTicks - startTicks);
        double windowMax = 0;
        int safeCount = Math.Clamp(count, 0, frames.Length);
        for (int i = 0; i < safeCount; i++)
        {
            FrameTimePoint frame = frames[i];
            double offset = (frame.UtcTicks - startTicks) / span;
            if (offset < 0 || offset > 1)
            {
                continue;
            }

            float value = frame.FrameTimeMs;
            if (!(value > 0))
            {
                continue;
            }

            int x = Math.Clamp((int)(offset * width), 0, width - 1);
            if (float.IsNaN(min[x]) || value < min[x])
            {
                min[x] = value;
            }

            if (float.IsNaN(max[x]) || value > max[x])
            {
                max[x] = value;
            }

            if (frame.Spike && (float.IsNaN(spikes[x]) || value > spikes[x]))
            {
                spikes[x] = value;
            }

            windowMax = Math.Max(windowMax, value);
        }

        return windowMax;
    }

    public static double FrameScaleMax(double max) => max switch
    {
        <= 16.7 => 16.7,
        <= 33.3 => 33.3,
        <= 50 => 50,
        <= 66.7 => 66.7,
        <= 100 => 100,
        _ => Math.Ceiling(max / 50.0) * 50.0,
    };
}
