using AzureGameAssistant.Core.Frames;
using AzureGameAssistant.Core.Models;
using AzureGameAssistant.Core.Sessions;

namespace AzureGameAssistant.Ai;

/// <summary>
/// Rebuilds the fact layer from a recorded session so an AI run targets the
/// exact event window the user opened, not whatever happens to be live now.
/// Collector restarts are stored as frames of 1000 ms or more; they are not
/// gameplay and would read as display gaps, so they stay out of the facts.
/// </summary>
public static class SessionFactsFactory
{
    public static FactsInput FromSession(SessionDocument document, string locale)
    {
        FrameTimePoint[] points = document.Frames;
        var frames = new List<FrameRecord>(points.Length);
        foreach (FrameTimePoint point in points)
        {
            if (!float.IsFinite(point.FrameTimeMs) ||
                point.FrameTimeMs <= 0 ||
                point.FrameTimeMs >= SessionSchema.DiscontinuityFrameMs)
            {
                continue;
            }

            frames.Add(new FrameRecord
            {
                FrameTimeMs = point.FrameTimeMs,
                CpuWaitMs = point.CpuWaitMs,
                GpuWaitMs = point.GpuWaitMs,
                DisplayLatencyMs = point.DisplayLatencyMs,
                Flags = point.Spike ? (ushort)1 : (ushort)0,
            });
        }

        return new FactsInput(
            document.Samples,
            frames,
            document.Names,
            document.Header.Mode,
            locale);
    }
}
