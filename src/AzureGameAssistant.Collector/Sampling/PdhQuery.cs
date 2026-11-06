using System.Runtime.InteropServices;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Collector.Sampling;

internal sealed class PdhQuery : IDisposable
{
    private readonly FileLogger _logger;
    private IntPtr _query;

    private PdhQuery(IntPtr query, FileLogger logger)
    {
        _query = query;
        _logger = logger;
    }

    public bool IsOpen => _query != IntPtr.Zero;

    public static PdhQuery? TryOpen(FileLogger logger)
    {
        uint status = Pdh32.PdhOpenQueryW(null, 0, out IntPtr query);
        if (status != 0 || query == IntPtr.Zero)
        {
            logger.Error($"PDH 初始化失败：0x{status:X8}");
            return null;
        }

        return new PdhQuery(query, logger);
    }

    public IntPtr AddCounter(string path)
    {
        if (!IsOpen)
        {
            return IntPtr.Zero;
        }

        uint status = Pdh32.PdhAddEnglishCounterW(_query, path, 0, out IntPtr counter);
        if (status != 0 || counter == IntPtr.Zero)
        {
            _logger.Warn($"PDH 计数器不可用：{path}（0x{status:X8}）");
            return IntPtr.Zero;
        }

        return counter;
    }

    public bool Collect()
    {
        if (!IsOpen)
        {
            return false;
        }

        uint status = Pdh32.PdhCollectQueryData(_query);
        if (status != 0)
        {
            _logger.Warn($"PDH 采集失败：0x{status:X8}");
            return false;
        }

        return true;
    }

    public static bool TryReadDouble(IntPtr counter, out double value)
    {
        value = 0;
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        uint status = Pdh32.PdhGetFormattedCounterValue(
            counter,
            Pdh32.FmtDouble,
            out _,
            out PdhFmtCounterValue formatted);

        if (status != 0 ||
            (formatted.CStatus != Pdh32.StatusValidData && formatted.CStatus != Pdh32.StatusNewData))
        {
            return false;
        }

        value = formatted.DoubleValue;
        return double.IsFinite(value);
    }

    public static unsafe bool TryReadArray(IntPtr counter, ref IntPtr buffer, ref int bufferSize, out int itemCount)
    {
        itemCount = 0;
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        if (buffer == IntPtr.Zero)
        {
            bufferSize = 64 * 1024;
            buffer = (IntPtr)NativeMemory.Alloc((nuint)bufferSize);
        }

        uint size = (uint)bufferSize;
        uint status = Pdh32.PdhGetFormattedCounterArrayW(
            counter,
            Pdh32.FmtDouble,
            ref size,
            out uint count,
            buffer);

        if (status == Pdh32.MoreData)
        {
            if (size <= (uint)bufferSize)
            {
                size = (uint)(bufferSize * 2);
            }

            NativeMemory.Free((void*)buffer);
            bufferSize = (int)size;
            buffer = (IntPtr)NativeMemory.Alloc((nuint)size);
            size = (uint)bufferSize;

            status = Pdh32.PdhGetFormattedCounterArrayW(
                counter,
                Pdh32.FmtDouble,
                ref size,
                out count,
                buffer);
        }

        if (status != 0)
        {
            return false;
        }

        itemCount = (int)count;
        return true;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            Pdh32.PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }
}
