using System.Globalization;

namespace AzureGameAssistant.Core.Frames;

public enum PresentMode : ushort
{
    Unknown = 0xFFFF,
    HardwareLegacyFlip = 0,
    HardwareLegacyCopy = 1,
    HardwareIndependentFlip = 2,
    ComposedFlip = 3,
    ComposedCopyGpu = 4,
    ComposedCopyCpu = 5,
    HardwareComposedIndependentFlip = 6,
}

/// <summary>
/// One PresentMon frame with metrics expressed in milliseconds. TimeMs is the
/// value of the CPUStartTime column (milliseconds since capture start) and is
/// negative when the column is unavailable.
/// </summary>
public readonly record struct PresentMonFrame(
    double TimeMs,
    double FrameTimeMs,
    double CpuBusyMs,
    double CpuWaitMs,
    double GpuLatencyMs,
    double GpuTimeMs,
    double GpuBusyMs,
    double GpuWaitMs,
    double DisplayLatencyMs,
    double AnimationErrorMs,
    int Pid,
    PresentMode PresentMode);

/// <summary>
/// Parses the CSV stream produced by PresentMon 2.6.0 with --v2_metrics.
/// Column order is taken from the header; optional columns degrade to zero.
/// </summary>
public sealed class FrameCsvParser
{
    private readonly int _time;
    private readonly int _frameTime;
    private readonly int _cpuBusy;
    private readonly int _cpuWait;
    private readonly int _gpuLatency;
    private readonly int _gpuTime;
    private readonly int _gpuBusy;
    private readonly int _gpuWait;
    private readonly int _displayLatency;
    private readonly int _animationError;
    private readonly int _pid;
    private readonly int _presentMode;

    public FrameCsvParser(ReadOnlySpan<char> headerLine)
    {
        _time = FindColumn(headerLine, "CPUStartTime");
        _frameTime = FindColumn(headerLine, "MsBetweenPresents");
        _cpuBusy = FindColumn(headerLine, "MsCPUBusy");
        _cpuWait = FindColumn(headerLine, "MsCPUWait");
        _gpuLatency = FindColumn(headerLine, "MsGPULatency");
        _gpuTime = FindColumn(headerLine, "MsGPUTime");
        _gpuBusy = FindColumn(headerLine, "MsGPUBusy");
        _gpuWait = FindColumn(headerLine, "MsGPUWait");
        _displayLatency = FindColumn(headerLine, "DisplayLatency");
        _animationError = FindColumn(headerLine, "MsAnimationError");
        _pid = FindColumn(headerLine, "ProcessID");
        _presentMode = FindColumn(headerLine, "PresentMode");

        if (_frameTime < 0 || _pid < 0)
        {
            throw new FormatException("PresentMon CSV header is missing MsBetweenPresents or ProcessID.");
        }
    }

    public bool TryParse(ReadOnlySpan<char> line, out PresentMonFrame frame)
    {
        frame = default;

        if (line.IsEmpty)
        {
            return false;
        }

        double time = -1;
        double frameTime = 0;
        double cpuBusy = 0;
        double cpuWait = 0;
        double gpuLatency = 0;
        double gpuTime = 0;
        double gpuBusy = 0;
        double gpuWait = 0;
        double displayLatency = 0;
        double animationError = 0;
        int pid = 0;
        PresentMode mode = PresentMode.Unknown;
        bool frameTimeValid = false;

        int position = 0;
        int index = 0;
        while (TryReadField(line, ref position, out ReadOnlySpan<char> field))
        {
            if (index == _frameTime)
            {
                frameTimeValid = TryParseDouble(field, out frameTime) && frameTime > 0;
            }
            else if (index == _pid)
            {
                int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out pid);
            }
            else if (index == _time)
            {
                if (!TryParseDouble(field, out time))
                {
                    time = -1;
                }
            }
            else if (index == _cpuBusy)
            {
                TryParseDouble(field, out cpuBusy);
            }
            else if (index == _cpuWait)
            {
                TryParseDouble(field, out cpuWait);
            }
            else if (index == _gpuLatency)
            {
                TryParseDouble(field, out gpuLatency);
            }
            else if (index == _gpuTime)
            {
                TryParseDouble(field, out gpuTime);
            }
            else if (index == _gpuBusy)
            {
                TryParseDouble(field, out gpuBusy);
            }
            else if (index == _gpuWait)
            {
                TryParseDouble(field, out gpuWait);
            }
            else if (index == _displayLatency)
            {
                TryParseDouble(field, out displayLatency);
            }
            else if (index == _animationError)
            {
                TryParseDouble(field, out animationError);
            }
            else if (index == _presentMode)
            {
                mode = ParsePresentMode(field);
            }

            index++;
        }

        if (!frameTimeValid || pid == 0)
        {
            return false;
        }

        frame = new PresentMonFrame(
            time,
            frameTime,
            cpuBusy,
            cpuWait,
            gpuLatency,
            gpuTime,
            gpuBusy,
            gpuWait,
            displayLatency,
            animationError,
            pid,
            mode);
        return true;
    }

    private static int FindColumn(ReadOnlySpan<char> header, string name)
    {
        int position = 0;
        int index = 0;
        while (TryReadField(header, ref position, out ReadOnlySpan<char> field))
        {
            if (field.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    private static bool TryReadField(ReadOnlySpan<char> line, ref int position, out ReadOnlySpan<char> field)
    {
        if (position > line.Length)
        {
            field = default;
            return false;
        }

        if (position == line.Length)
        {
            field = default;
            position = line.Length + 1;
            return false;
        }

        if (line[position] == '"')
        {
            int search = position + 1;
            while (search < line.Length)
            {
                int quote = line[search..].IndexOf('"');
                if (quote < 0)
                {
                    break;
                }

                int absolute = search + quote;
                if (absolute + 1 < line.Length && line[absolute + 1] == '"')
                {
                    search = absolute + 2;
                    continue;
                }

                field = line.Slice(position + 1, absolute - position - 1);
                position = absolute + 1 < line.Length && line[absolute + 1] == ','
                    ? absolute + 2
                    : line.Length + 1;
                return true;
            }

            field = line[(position + 1)..];
            position = line.Length + 1;
            return true;
        }

        ReadOnlySpan<char> remaining = line[position..];
        int comma = remaining.IndexOf(',');
        if (comma < 0)
        {
            field = remaining;
            position = line.Length + 1;
            return true;
        }

        field = remaining[..comma];
        position += comma + 1;
        return true;
    }

    private static bool TryParseDouble(ReadOnlySpan<char> text, out double value)
    {
        if (text.Equals("NA", StringComparison.OrdinalIgnoreCase))
        {
            value = 0;
            return false;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static PresentMode ParsePresentMode(ReadOnlySpan<char> text)
    {
        if (text.Equals("Hardware: Legacy Flip", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.HardwareLegacyFlip;
        }

        if (text.Equals("Hardware: Legacy Copy to front buffer", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.HardwareLegacyCopy;
        }

        if (text.Equals("Hardware: Independent Flip", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.HardwareIndependentFlip;
        }

        if (text.Equals("Composed: Flip", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.ComposedFlip;
        }

        if (text.Equals("Composed: Copy with GPU GDI", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.ComposedCopyGpu;
        }

        if (text.Equals("Composed: Copy with CPU GDI", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.ComposedCopyCpu;
        }

        if (text.Equals("Hardware Composed: Independent Flip", StringComparison.OrdinalIgnoreCase))
        {
            return PresentMode.HardwareComposedIndependentFlip;
        }

        return PresentMode.Unknown;
    }
}
