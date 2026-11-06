using System.Runtime.InteropServices;
using AzureGameAssistant.Collector.Interop;
using AzureGameAssistant.Core.Logging;
using AzureGameAssistant.Core.Models;

namespace AzureGameAssistant.Collector.Sampling;

internal sealed class ProcessSampler : IDisposable
{
    private const double BytesPerMegabyte = 1024.0 * 1024.0;
    private const double TicksPerSecond = 10_000_000.0;

    private readonly FileLogger _logger;
    private readonly int _processorCount;
    private readonly Dictionary<ProcessKey, ProcessTimes> _times = new();
    private readonly List<ProcessEntry> _entries = new();
    private readonly List<ProcessEntry> _selected = new(5);

    private IntPtr _buffer;
    private int _bufferSize;
    private long _lastQpc;
    private long _qpcFrequency;
    private int _generation;
    private int _samples;
    private bool _warned;

    public ProcessSampler(FileLogger logger)
    {
        _logger = logger;
        _processorCount = Math.Max(1, Environment.ProcessorCount);

        if (!Kernel32.QueryPerformanceFrequency(out _qpcFrequency) || _qpcFrequency <= 0)
        {
            _qpcFrequency = 10_000_000;
        }

        Kernel32.QueryPerformanceCounter(out _lastQpc);
    }

    public int Sample(Span<ProcessSlot> destination, uint foregroundPid, IReadOnlyDictionary<uint, float> gpuByPid)
    {
        if (destination.Length < 5)
        {
            throw new ArgumentException("需要 5 个进程槽位", nameof(destination));
        }

        destination[..5].Clear();

        if (!TryQueryProcesses())
        {
            return 0;
        }

        Kernel32.QueryPerformanceCounter(out long qpc);
        double elapsedSeconds = _lastQpc > 0 ? (qpc - _lastQpc) / (double)_qpcFrequency : 0;
        _lastQpc = qpc;

        _generation++;
        _entries.Clear();

        unsafe
        {
            var pointer = (SystemProcessInfo*)_buffer;
            while (true)
            {
                AddProcess(pointer, elapsedSeconds, gpuByPid);

                if (pointer->NextEntryOffset == 0)
                {
                    break;
                }

                pointer = (SystemProcessInfo*)((byte*)pointer + pointer->NextEntryOffset);
            }
        }

        CleanupStale();

        if (_entries.Count == 0)
        {
            return 0;
        }

        SelectTop(foregroundPid);

        int count = Math.Min(5, _selected.Count);
        for (int i = 0; i < count; i++)
        {
            ProcessEntry entry = _selected[i];
            destination[i] = new ProcessSlot
            {
                Pid = entry.Pid,
                StartTimeLow = entry.StartTimeLow,
                CpuPct = entry.CpuPct,
                GpuPct = entry.GpuPct,
                WorkingSetMb = entry.WorkingSetMb,
                PrivateMb = entry.PrivateMb,
                NameHint = entry.NameLength,
                Flags = entry.Pid == foregroundPid ? ProcessSlotFlags.Foreground : (ushort)0,
            };
        }

        return count;
    }

    private unsafe void AddProcess(SystemProcessInfo* info, double elapsedSeconds, IReadOnlyDictionary<uint, float> gpuByPid)
    {
        uint pid = (uint)info->UniqueProcessId.ToInt64();
        if (pid == 0)
        {
            return;
        }

        var key = new ProcessKey(pid, info->CreateTime);
        ulong kernelUser = unchecked((ulong)(info->KernelTime + info->UserTime));

        float cpuPct = 0;
        if (_times.TryGetValue(key, out ProcessTimes previous))
        {
            if (kernelUser > previous.KernelUser && elapsedSeconds > 0)
            {
                double deltaSeconds = (kernelUser - previous.KernelUser) / TicksPerSecond;
                cpuPct = (float)Math.Clamp(
                    deltaSeconds / (elapsedSeconds * _processorCount) * 100.0,
                    0,
                    100);
            }
        }

        _times[key] = new ProcessTimes
        {
            KernelUser = kernelUser,
            Generation = _generation,
        };

        gpuByPid.TryGetValue(pid, out float gpuPct);

        int nameLength = info->ImageNameLength / 2;
        _entries.Add(new ProcessEntry
        {
            Pid = pid,
            StartTimeLow = unchecked((uint)info->CreateTime),
            CpuPct = cpuPct,
            GpuPct = gpuPct,
            WorkingSetMb = (uint)Math.Clamp(info->WorkingSetSize / BytesPerMegabyte, 0, uint.MaxValue),
            PrivateMb = (uint)Math.Clamp(info->PagefileUsage / BytesPerMegabyte, 0, uint.MaxValue),
            NameLength = (ushort)Math.Clamp(nameLength, 0, ushort.MaxValue),
        });
    }

    private bool TryQueryProcesses()
    {
        if (_buffer == IntPtr.Zero)
        {
            Allocate(1024 * 1024);
        }

        for (int attempt = 0; attempt < 5; attempt++)
        {
            int status = Ntdll.NtQuerySystemInformation(
                Ntdll.SystemProcessInformation,
                _buffer,
                _bufferSize,
                out int required);

            if (status == 0)
            {
                return true;
            }

            if (status is Ntdll.StatusInfoLengthMismatch or Ntdll.StatusBufferTooSmall or Ntdll.StatusBufferOverflow)
            {
                int next = Math.Max(_bufferSize * 2, required + (64 * 1024));
                Allocate(next);
                continue;
            }

            if (!_warned)
            {
                _warned = true;
                _logger.Warn($"进程采样失败：0x{status:X8}");
            }

            return false;
        }

        return false;
    }

    private unsafe void Allocate(int size)
    {
        if (_buffer != IntPtr.Zero)
        {
            NativeMemory.Free((void*)_buffer);
        }

        _bufferSize = Math.Max(size, 1024 * 1024);
        _buffer = (IntPtr)NativeMemory.Alloc((nuint)_bufferSize);
    }

    private void SelectTop(uint foregroundPid)
    {
        _selected.Clear();

        ProcessEntry foreground = default;
        bool hasForeground = false;
        foreach (ProcessEntry entry in _entries)
        {
            if (entry.Pid == foregroundPid)
            {
                foreground = entry;
                hasForeground = true;
                break;
            }
        }

        if (hasForeground)
        {
            _selected.Add(foreground);
        }

        AddTopBy(static entry => entry.CpuPct);
        AddTopBy(static entry => entry.GpuPct);
        AddTopBy(static entry => entry.PrivateMb);
    }

    private void AddTopBy(Func<ProcessEntry, float> selector)
    {
        while (_selected.Count < 5)
        {
            ProcessEntry best = default;
            float bestValue = 0;
            bool found = false;

            foreach (ProcessEntry entry in _entries)
            {
                if (Contains(entry.Pid))
                {
                    continue;
                }

                float value = selector(entry);
                if (value > bestValue)
                {
                    bestValue = value;
                    best = entry;
                    found = true;
                }
            }

            if (!found)
            {
                return;
            }

            _selected.Add(best);
        }
    }

    private bool Contains(uint pid)
    {
        foreach (ProcessEntry entry in _selected)
        {
            if (entry.Pid == pid)
            {
                return true;
            }
        }

        return false;
    }

    private void CleanupStale()
    {
        _samples++;
        if (_samples % 60 != 0)
        {
            return;
        }

        List<ProcessKey>? stale = null;
        foreach (KeyValuePair<ProcessKey, ProcessTimes> pair in _times)
        {
            if (pair.Value.Generation < _generation - 2)
            {
                (stale ??= new List<ProcessKey>()).Add(pair.Key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (ProcessKey key in stale)
        {
            _times.Remove(key);
        }
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero)
        {
            unsafe
            {
                NativeMemory.Free((void*)_buffer);
            }

            _buffer = IntPtr.Zero;
        }
    }

    private readonly record struct ProcessKey(uint Pid, long CreateTime);

    private struct ProcessTimes
    {
        public ulong KernelUser;
        public int Generation;
    }

    private struct ProcessEntry
    {
        public uint Pid;
        public uint StartTimeLow;
        public float CpuPct;
        public float GpuPct;
        public uint WorkingSetMb;
        public uint PrivateMb;
        public ushort NameLength;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct SystemProcessInfo
    {
        [FieldOffset(0)]
        public uint NextEntryOffset;

        [FieldOffset(4)]
        public uint NumberOfThreads;

        [FieldOffset(8)]
        public long WorkingSetPrivateSize;

        [FieldOffset(32)]
        public long CreateTime;

        [FieldOffset(40)]
        public long UserTime;

        [FieldOffset(48)]
        public long KernelTime;

        [FieldOffset(56)]
        public ushort ImageNameLength;

        [FieldOffset(58)]
        public ushort ImageNameMaximumLength;

        [FieldOffset(64)]
        public IntPtr ImageNameBuffer;

        [FieldOffset(80)]
        public IntPtr UniqueProcessId;

        [FieldOffset(96)]
        public uint HandleCount;

        [FieldOffset(144)]
        public ulong WorkingSetSize;

        [FieldOffset(184)]
        public ulong PagefileUsage;
    }
}
