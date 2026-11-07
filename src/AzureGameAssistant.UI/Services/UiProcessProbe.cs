using System.Diagnostics;
using AzureGameAssistant.Core.Diagnostics;

namespace AzureGameAssistant.UI.Services;

/// <summary>
/// Samples the UI process itself (working set, private bytes, CPU share and
/// handles) for the long-run recorder. CPU is a delta between two samples.
/// </summary>
public sealed class UiProcessProbe : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private DateTime _lastAt = DateTime.UtcNow;
    private TimeSpan _lastCpu;
    private double _lastCpuPct;

    public UiProcessMetrics Sample()
    {
        _process.Refresh();
        DateTime now = DateTime.UtcNow;
        TimeSpan cpu = _process.TotalProcessorTime;
        double elapsed = (now - _lastAt).TotalSeconds;
        if (elapsed >= 0.2)
        {
            double used = (cpu - _lastCpu).TotalSeconds;
            _lastCpuPct = Math.Clamp(used / elapsed / Environment.ProcessorCount * 100.0, 0, 100);
            _lastAt = now;
            _lastCpu = cpu;
        }

        return new UiProcessMetrics(
            _process.WorkingSet64 / 1048576.0,
            _process.PrivateMemorySize64 / 1048576.0,
            _lastCpuPct,
            _process.HandleCount);
    }

    public void Dispose() => _process.Dispose();
}
