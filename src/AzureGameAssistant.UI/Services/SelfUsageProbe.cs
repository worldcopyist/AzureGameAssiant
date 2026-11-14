using System.ComponentModel;
using System.Diagnostics;

namespace AzureGameAssistant.UI.Services;

public readonly record struct SelfUsage(
    int UiPid,
    double CpuPct,
    double MemoryMb,
    double UiMb,
    double CollectorMb,
    double CaptureMb,
    double SensorMb);

/// <summary>
/// Aggregates the application's own processes (UI, collector, PresentMon and
/// sensor sidecar) for the live process table. Child PIDs are rebound lazily
/// because the collector restarts PresentMon and the sidecar on its own.
/// </summary>
public sealed class SelfUsageProbe : IDisposable
{
    private const int UiIndex = 0;
    private const int CollectorIndex = 1;
    private const int CaptureIndex = 2;
    private const int SensorIndex = 3;

    private readonly Process _ui = Process.GetCurrentProcess();
    private readonly Process?[] _parts = new Process?[4];
    private readonly TimeSpan[] _lastCpu = new TimeSpan[4];
    private readonly double[] _memoryMb = new double[4];
    private DateTime _lastAt = DateTime.UtcNow;
    private DateTime _rebindAt = DateTime.MinValue;
    private double _lastCpuPct;

    public SelfUsage Sample()
    {
        DateTime now = DateTime.UtcNow;
        bool rebind = now >= _rebindAt;
        if (rebind)
        {
            _rebindAt = now.AddSeconds(30);
        }

        if (_parts[UiIndex] is null)
        {
            Bind(UiIndex, _ui);
        }

        if (rebind || IsDead(_parts[CollectorIndex]))
        {
            Bind(CollectorIndex, FindSingle("AzureGameAssistant.Collector"));
        }

        if (rebind || IsDead(_parts[SensorIndex]))
        {
            Bind(SensorIndex, FindSingle("AzureGameAssistant.Sensors"));
        }

        if (rebind || IsDead(_parts[CaptureIndex]))
        {
            Bind(CaptureIndex, FindPresentMon());
        }

        double elapsed = (now - _lastAt).TotalSeconds;
        TimeSpan used = TimeSpan.Zero;
        double memoryMb = 0;

        for (int i = 0; i < _parts.Length; i++)
        {
            Process? part = _parts[i];
            if (part is null)
            {
                _memoryMb[i] = 0;
                continue;
            }

            try
            {
                part.Refresh();
                TimeSpan cpu = part.TotalProcessorTime;
                if (elapsed >= 0.2)
                {
                    TimeSpan delta = cpu - _lastCpu[i];
                    if (delta > TimeSpan.Zero)
                    {
                        used += delta;
                    }

                    _lastCpu[i] = cpu;
                }

                _memoryMb[i] = part.WorkingSet64 / 1048576.0;
                memoryMb += _memoryMb[i];
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // A non-elevated UI cannot query an elevated child. Drop the
                // slot instead of reporting a wrong number.
                _memoryMb[i] = 0;
                _parts[i] = null;
            }
        }

        if (elapsed >= 0.2)
        {
            _lastCpuPct = Math.Clamp(used.TotalSeconds / elapsed / Environment.ProcessorCount * 100.0, 0, 100);
            _lastAt = now;
        }

        return new SelfUsage(
            _ui.Id,
            _lastCpuPct,
            memoryMb,
            _memoryMb[UiIndex],
            _memoryMb[CollectorIndex],
            _memoryMb[CaptureIndex],
            _memoryMb[SensorIndex]);
    }

    private void Bind(int index, Process? process)
    {
        if (ReferenceEquals(process, _ui) == false)
        {
            _parts[index]?.Dispose();
        }

        _parts[index] = process;
        _memoryMb[index] = 0;
        _lastCpu[index] = TimeSpan.Zero;

        if (process is null)
        {
            return;
        }

        try
        {
            process.Refresh();
            _lastCpu[index] = process.TotalProcessorTime;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            _parts[index] = null;
            if (!ReferenceEquals(process, _ui))
            {
                process.Dispose();
            }
        }
    }

    private static bool IsDead(Process? process)
    {
        if (process is null)
        {
            return true;
        }

        try
        {
            process.Refresh();
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return true;
        }
    }

    private static Process? FindSingle(string name)
    {
        Process[] matches = Process.GetProcessesByName(name);
        if (matches.Length == 0)
        {
            return null;
        }

        Process first = matches[0];
        for (int i = 1; i < matches.Length; i++)
        {
            matches[i].Dispose();
        }

        return first;
    }

    private static Process? FindPresentMon()
    {
        foreach (Process process in Process.GetProcesses())
        {
            bool match;
            try
            {
                match = process.ProcessName.StartsWith("PresentMon-", StringComparison.OrdinalIgnoreCase);
            }
            catch (InvalidOperationException)
            {
                match = false;
            }

            if (!match)
            {
                process.Dispose();
                continue;
            }

            return process;
        }

        return null;
    }

    public void Dispose()
    {
        for (int i = 0; i < _parts.Length; i++)
        {
            if (!ReferenceEquals(_parts[i], _ui))
            {
                _parts[i]?.Dispose();
            }

            _parts[i] = null;
        }

        _ui.Dispose();
    }
}
