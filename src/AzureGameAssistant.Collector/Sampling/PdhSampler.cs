using System.Runtime.InteropServices;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core.Gpu;
using AzureGameAssistant.Core.Logging;

namespace AzureGameAssistant.Collector.Sampling;

internal struct SystemMetrics
{
    public float CpuUtilityPct;
    public float CpuFrequencyPct;
    public uint MemAvailableMb;
    public uint MemCommitMb;
    public uint MemCommitLimitMb;
    public float PageFilePct;
    public float DiskBusyPct;
    public float DiskLatencyMs;
    public ushort ProcessorQueueLength;
    public bool CpuValid;
    public bool MemoryValid;
    public bool DiskValid;
}

internal struct GpuTotals
{
    public float UtilPct;
    public uint DedicatedMb;
    public uint SharedMb;
    public bool Valid;
}

internal sealed class PdhSampler : IDisposable
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    private readonly FileLogger _logger;
    private readonly PdhQuery? _query;

    private IntPtr _cpuUtility;
    private IntPtr _cpuTime;
    private IntPtr _cpuFrequency;
    private IntPtr _processorQueue;
    private IntPtr _memAvailable;
    private IntPtr _memCommit;
    private IntPtr _memCommitLimit;
    private IntPtr _pageFile;
    private IntPtr _diskBusy;
    private IntPtr _diskLatency;

    private IntPtr _gpuEngine;
    private IntPtr _adapterDedicated;
    private IntPtr _adapterShared;

    private IntPtr _engineBuffer;
    private IntPtr _dedicatedBuffer;
    private IntPtr _sharedBuffer;
    private int _engineBufferSize;
    private int _dedicatedBufferSize;
    private int _sharedBufferSize;

    private bool _cpuWarned;
    private bool _cpuFrequencyWarned;
    private bool _queueWarned;
    private bool _memoryWarned;
    private bool _pageFileWarned;
    private bool _diskWarned;
    private bool _gpuWarned;

    public PdhSampler(FileLogger logger)
    {
        _logger = logger;
        _query = PdhQuery.TryOpen(logger);
        if (_query is null)
        {
            return;
        }

        _cpuUtility = _query.AddCounter(@"\Processor Information(_Total)\% Processor Utility");
        if (_cpuUtility == IntPtr.Zero)
        {
            _logger.Info("回退到 \\Processor(_Total)\\% Processor Time");
            _cpuTime = _query.AddCounter(@"\Processor(_Total)\% Processor Time");
        }

        _cpuFrequency = _query.AddCounter(@"\Processor Information(_Total)\% Processor Performance");
        _processorQueue = _query.AddCounter(@"\System\Processor Queue Length");
        _memAvailable = _query.AddCounter(@"\Memory\Available MBytes");
        _memCommit = _query.AddCounter(@"\Memory\Committed Bytes");
        _memCommitLimit = _query.AddCounter(@"\Memory\Commit Limit");
        _pageFile = _query.AddCounter(@"\Paging File(_Total)\% Usage");
        _diskBusy = _query.AddCounter(@"\PhysicalDisk(_Total)\% Disk Time");
        _diskLatency = _query.AddCounter(@"\PhysicalDisk(_Total)\Avg. Disk sec/Transfer");

        _gpuEngine = _query.AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _adapterDedicated = _query.AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _adapterShared = _query.AddCounter(@"\GPU Adapter Memory(*)\Shared Usage");

        _query.Collect();
    }

    public bool Collect() => _query?.Collect() ?? false;

    public SystemMetrics ReadSystem()
    {
        var metrics = default(SystemMetrics);
        if (_query is null)
        {
            return metrics;
        }

        bool cpuRead = TryRead(_cpuUtility, out double cpu, ref _cpuWarned, "CPU Utility");
        if (!cpuRead)
        {
            cpuRead = TryRead(_cpuTime, out cpu, ref _cpuWarned, "CPU Time");
        }

        if (cpuRead)
        {
            metrics.CpuUtilityPct = (float)Math.Clamp(cpu, 0, 100);
            metrics.CpuValid = true;
        }
        if (TryRead(_cpuFrequency, out double frequency, ref _cpuFrequencyWarned, "CPU 频率"))
        {
            metrics.CpuFrequencyPct = (float)Math.Clamp(frequency, 0, 100);
        }

        if (TryRead(_processorQueue, out double queue, ref _queueWarned, "处理器队列"))
        {
            metrics.ProcessorQueueLength = (ushort)Math.Clamp(queue, 0, ushort.MaxValue);
        }

        bool memory = TryRead(_memAvailable, out double available, ref _memoryWarned, "可用内存");
        memory &= TryRead(_memCommit, out double commit, ref _memoryWarned, "已提交内存");
        memory &= TryRead(_memCommitLimit, out double commitLimit, ref _memoryWarned, "提交上限");
        if (memory)
        {
            metrics.MemAvailableMb = (uint)Math.Clamp(available, 0, uint.MaxValue);
            metrics.MemCommitMb = BytesToMb(commit);
            metrics.MemCommitLimitMb = BytesToMb(commitLimit);
            metrics.MemoryValid = true;
        }

        if (TryRead(_pageFile, out double pageFile, ref _pageFileWarned, "页面文件"))
        {
            metrics.PageFilePct = (float)Math.Clamp(pageFile, 0, 100);
        }

        bool disk = TryRead(_diskBusy, out double diskBusy, ref _diskWarned, "磁盘占用");
        disk &= TryRead(_diskLatency, out double diskLatency, ref _diskWarned, "磁盘响应");
        if (disk)
        {
            metrics.DiskBusyPct = (float)Math.Max(0, diskBusy);
            metrics.DiskLatencyMs = (float)Math.Max(0, diskLatency * 1000.0);
            metrics.DiskValid = true;
        }

        return metrics;
    }

    public bool ReadGpu(out GpuTotals totals, Dictionary<uint, float> processGpu)
    {
        totals = default;
        processGpu.Clear();

        if (_query is null)
        {
            return false;
        }

        bool haveEngine = false;
        bool haveAdapter = false;
        ulong adapterLuid = 0;
        int adapterPhys = 0;
        float bestUtil = -1;
        bool haveMemory = false;

        if (_gpuEngine != IntPtr.Zero)
        {
            if (PdhQuery.TryReadArray(_gpuEngine, ref _engineBuffer, ref _engineBufferSize, out int count))
            {
                haveEngine = true;
                ReadEngineItems(count, processGpu, ref haveAdapter, ref adapterLuid, ref adapterPhys, ref bestUtil);
            }
            else if (!_gpuWarned)
            {
                _gpuWarned = true;
                _logger.Warn("GPU Engine 计数器读取失败，GPU 曲线降级");
            }
        }

        if (_adapterDedicated != IntPtr.Zero &&
            PdhQuery.TryReadArray(_adapterDedicated, ref _dedicatedBuffer, ref _dedicatedBufferSize, out int dedicatedCount))
        {
            double dedicatedBytes = 0;
            ReadAdapterMemoryItems(
                _dedicatedBuffer,
                dedicatedCount,
                ref haveAdapter,
                ref adapterLuid,
                ref adapterPhys,
                ref dedicatedBytes,
                ref haveMemory);
            totals.DedicatedMb = BytesToMb(dedicatedBytes);
        }

        if (_adapterShared != IntPtr.Zero &&
            PdhQuery.TryReadArray(_adapterShared, ref _sharedBuffer, ref _sharedBufferSize, out int sharedCount))
        {
            double sharedBytes = 0;
            ReadAdapterMemoryItems(
                _sharedBuffer,
                sharedCount,
                ref haveAdapter,
                ref adapterLuid,
                ref adapterPhys,
                ref sharedBytes,
                ref haveMemory);
            totals.SharedMb = BytesToMb(sharedBytes);
        }

        totals.UtilPct = bestUtil >= 0 ? bestUtil : 0;
        totals.Valid = haveEngine || haveMemory;
        return totals.Valid;
    }

    private unsafe void ReadEngineItems(
        int count,
        Dictionary<uint, float> processGpu,
        ref bool haveAdapter,
        ref ulong adapterLuid,
        ref int adapterPhys,
        ref float bestUtil)
    {
        byte* basePointer = (byte*)_engineBuffer;
        int stride = sizeof(PdhFmtCounterValueItemW);

        for (int i = 0; i < count; i++)
        {
            PdhFmtCounterValueItemW* item = (PdhFmtCounterValueItemW*)(basePointer + (i * stride));
            if (item->CStatus != Pdh32.StatusValidData && item->CStatus != Pdh32.StatusNewData)
            {
                continue;
            }

            ReadOnlySpan<char> name = ReadName(item->Name);
            if (!GpuInstanceParser.TryParse(name, out GpuInstanceInfo info))
            {
                continue;
            }

            float utilization = (float)item->DoubleValue;
            if (!float.IsFinite(utilization) || utilization < 0)
            {
                continue;
            }

            if (info.Engine is not (GpuEngineKind.ThreeD or GpuEngineKind.Copy))
            {
                continue;
            }

            if (info.Pid != 0)
            {
                processGpu.TryGetValue(info.Pid, out float current);
                float sum = current + utilization;
                processGpu[info.Pid] = sum > 100 ? 100 : sum;
            }

            if (info.Engine == GpuEngineKind.ThreeD && utilization > bestUtil)
            {
                bestUtil = utilization;
                haveAdapter = true;
                adapterLuid = info.Luid;
                adapterPhys = info.Phys;
            }
        }
    }

    private unsafe void ReadAdapterMemoryItems(
        IntPtr buffer,
        int count,
        ref bool haveAdapter,
        ref ulong adapterLuid,
        ref int adapterPhys,
        ref double bytes,
        ref bool haveMemory)
    {
        byte* basePointer = (byte*)buffer;
        int stride = sizeof(PdhFmtCounterValueItemW);

        for (int i = 0; i < count; i++)
        {
            PdhFmtCounterValueItemW* item = (PdhFmtCounterValueItemW*)(basePointer + (i * stride));
            if (item->CStatus != Pdh32.StatusValidData && item->CStatus != Pdh32.StatusNewData)
            {
                continue;
            }

            ReadOnlySpan<char> name = ReadName(item->Name);
            if (!GpuInstanceParser.TryParse(name, out GpuInstanceInfo info))
            {
                continue;
            }

            if (!haveAdapter)
            {
                haveAdapter = true;
                adapterLuid = info.Luid;
                adapterPhys = info.Phys;
            }

            if (info.Luid != adapterLuid || info.Phys != adapterPhys)
            {
                continue;
            }

            double value = item->DoubleValue;
            if (double.IsFinite(value) && value > 0)
            {
                bytes += value;
                haveMemory = true;
            }
        }
    }

    private bool TryRead(IntPtr counter, out double value, ref bool warned, string label)
    {
        value = 0;
        if (counter == IntPtr.Zero)
        {
            return false;
        }

        if (PdhQuery.TryReadDouble(counter, out value))
        {
            return true;
        }

        if (!warned)
        {
            warned = true;
            _logger.Warn($"PDH 计数器读取失败：{label}");
        }

        return false;
    }

    private static unsafe ReadOnlySpan<char> ReadName(IntPtr name)
    {
        if (name == IntPtr.Zero)
        {
            return default;
        }

        char* pointer = (char*)name;
        int length = 0;
        while (length < 512 && pointer[length] != '\0')
        {
            length++;
        }

        return new ReadOnlySpan<char>(pointer, length);
    }

    private static uint BytesToMb(double bytes) =>
        (uint)Math.Clamp(bytes / BytesPerMegabyte, 0, uint.MaxValue);

    public void Dispose()
    {
        _query?.Dispose();

        FreeBuffer(ref _engineBuffer);
        FreeBuffer(ref _dedicatedBuffer);
        FreeBuffer(ref _sharedBuffer);
    }

    private static unsafe void FreeBuffer(ref IntPtr buffer)
    {
        if (buffer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)buffer);
            buffer = IntPtr.Zero;
        }
    }
}
