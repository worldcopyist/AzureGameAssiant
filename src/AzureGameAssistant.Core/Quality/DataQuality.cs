namespace AzureGameAssistant.Core.Quality;

[Flags]
public enum DataQualityFlags
{
    None = 0,

    /// <summary>Sensor-only run: no per-frame timeline, so FPS figures are absent.</summary>
    ShallowMode = 1,

    /// <summary>Frame capture did not produce fresh data for the event window.</summary>
    FrameInterrupted = 2,

    /// <summary>Collector heartbeat or sample stream had a gap.</summary>
    CollectorInterrupted = 4,
}

/// <summary>
/// Marks a reading as good, partial or degraded so reports never present
/// shallow or interrupted data as if it were a full capture.
/// </summary>
public static class DataQuality
{
    /// <summary>Frame data older than this counts as an interruption.</summary>
    public const double FrameStaleSeconds = 10;

    public static DataQualityFlags Evaluate(
        bool deepMode,
        bool framesFresh,
        double frameAgeSeconds,
        bool collectorConnected,
        int sampleDropped,
        int frameDropped)
    {
        DataQualityFlags flags = DataQualityFlags.None;

        if (!deepMode)
        {
            flags |= DataQualityFlags.ShallowMode;
        }

        bool stale = double.IsFinite(frameAgeSeconds) && frameAgeSeconds > FrameStaleSeconds;
        if (!framesFresh || stale)
        {
            flags |= DataQualityFlags.FrameInterrupted;
        }

        if (!collectorConnected || sampleDropped > 0)
        {
            flags |= DataQualityFlags.CollectorInterrupted;
        }

        if (frameDropped > 0)
        {
            flags |= DataQualityFlags.FrameInterrupted;
        }

        return flags;
    }

    public static string Describe(DataQualityFlags flags, bool zh)
    {
        if (flags == DataQualityFlags.None)
        {
            return zh ? "完整" : "complete";
        }

        var parts = new List<string>(3);
        if (flags.HasFlag(DataQualityFlags.ShallowMode))
        {
            parts.Add(zh ? "浅模式（无帧时间轴）" : "shallow mode (no frame timeline)");
        }

        if (flags.HasFlag(DataQualityFlags.FrameInterrupted))
        {
            parts.Add(zh ? "帧数据中断" : "frame data interrupted");
        }

        if (flags.HasFlag(DataQualityFlags.CollectorInterrupted))
        {
            parts.Add(zh ? "采集数据中断" : "collector data interrupted");
        }

        return string.Join(zh ? " · " : " · ", parts);
    }
}
