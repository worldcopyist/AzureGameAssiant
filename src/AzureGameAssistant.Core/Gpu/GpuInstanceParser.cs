using System.Globalization;

namespace AzureGameAssistant.Core.Gpu;

public enum GpuEngineKind
{
    Unknown,
    ThreeD,
    Copy,
    Other,
}

public readonly record struct GpuInstanceInfo(uint Pid, ulong Luid, int Phys, GpuEngineKind Engine);

public static class GpuInstanceParser
{
    public static bool TryParse(ReadOnlySpan<char> instance, out GpuInstanceInfo info)
    {
        info = default;
        if (instance.IsEmpty ||
            instance.IndexOf("_total", StringComparison.OrdinalIgnoreCase) >= 0 ||
            instance.StartsWith("total", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        uint pid = 0;
        int pidIndex = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        if (pidIndex >= 0)
        {
            ReadOnlySpan<char> rest = instance[(pidIndex + 4)..];
            int end = rest.IndexOf('_');
            if (end <= 0 ||
                !uint.TryParse(rest[..end], NumberStyles.None, CultureInfo.InvariantCulture, out pid))
            {
                return false;
            }
        }

        int luidIndex = instance.IndexOf("luid_", StringComparison.OrdinalIgnoreCase);
        if (luidIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> luidRest = instance[(luidIndex + 5)..];
        int highEnd = luidRest.IndexOf('_');
        if (highEnd <= 0 || !TryParseHex(luidRest[..highEnd], out ulong high))
        {
            return false;
        }

        ReadOnlySpan<char> lowRest = luidRest[(highEnd + 1)..];
        int lowEnd = lowRest.IndexOf('_');
        ReadOnlySpan<char> lowSpan = lowEnd < 0 ? lowRest : lowRest[..lowEnd];
        if (!TryParseHex(lowSpan, out ulong low))
        {
            return false;
        }

        int phys = 0;
        int physIndex = instance.IndexOf("phys_", StringComparison.OrdinalIgnoreCase);
        if (physIndex >= 0)
        {
            ReadOnlySpan<char> physRest = instance[(physIndex + 5)..];
            int end = physRest.IndexOf('_');
            ReadOnlySpan<char> physSpan = end < 0 ? physRest : physRest[..end];
            if (!int.TryParse(physSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out phys))
            {
                return false;
            }
        }

        GpuEngineKind engine = GpuEngineKind.Unknown;
        int engineIndex = instance.IndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
        if (engineIndex >= 0)
        {
            ReadOnlySpan<char> engineSpan = instance[(engineIndex + 8)..];
            if (engineSpan.Equals("3D", StringComparison.OrdinalIgnoreCase))
            {
                engine = GpuEngineKind.ThreeD;
            }
            else if (engineSpan.Equals("Copy", StringComparison.OrdinalIgnoreCase))
            {
                engine = GpuEngineKind.Copy;
            }
            else
            {
                engine = GpuEngineKind.Other;
            }
        }

        info = new GpuInstanceInfo(pid, (high << 32) | low, phys, engine);
        return true;
    }

    private static bool TryParseHex(ReadOnlySpan<char> span, out ulong value)
    {
        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }

        return ulong.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }
}
